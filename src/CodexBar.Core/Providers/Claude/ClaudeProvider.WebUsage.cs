// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Providers.Claude;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Data.Sqlite;
#endif

public sealed partial class ClaudeProvider
{
    private const string ClaudeWebBaseUrl = "https://claude.ai";

    private static readonly string _defaultClaudeDesktopLocalStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude",
        "Local State");

    private static readonly string _defaultClaudeDesktopCookiesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude",
        "Network",
        "Cookies");

    private static readonly string _defaultClaudeDesktopConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude",
        "config.json");

    internal static string? ClaudeDesktopLocalStatePathOverride { get; set; }

    internal static string? ClaudeDesktopCookiesPathOverride { get; set; }

    internal static string? ClaudeDesktopConfigPathOverride { get; set; }

    internal static string? ClaudeDesktopCookieHeaderOverride { get; set; }

    internal static string ClaudeDesktopLocalStatePath =>
        ClaudeDesktopLocalStatePathOverride ?? _defaultClaudeDesktopLocalStatePath;

    internal static string ClaudeDesktopCookiesPath =>
        ClaudeDesktopCookiesPathOverride ?? _defaultClaudeDesktopCookiesPath;

    internal static string ClaudeDesktopConfigPath =>
        ClaudeDesktopConfigPathOverride ?? _defaultClaudeDesktopConfigPath;

    private ClaudeCredentials? ReadClaudeDesktopTokenCacheCredentials()
    {
#if WINDOWS
        if (CredentialsPathOverride is not null &&
            (ClaudeDesktopConfigPathOverride is null || ClaudeDesktopLocalStatePathOverride is null))
        {
            return null;
        }

        if (!File.Exists(ClaudeDesktopLocalStatePath) || !File.Exists(ClaudeDesktopConfigPath))
        {
            return null;
        }

        try
        {
            var key = ReadChromiumEncryptionKey(ClaudeDesktopLocalStatePath);
            if (key is null)
            {
                return null;
            }

            var configJson = File.ReadAllText(ClaudeDesktopConfigPath);
            using var configDoc = JsonDocument.Parse(configJson);
            if (!configDoc.RootElement.TryGetProperty("oauth:tokenCache", out var tokenCacheProperty))
            {
                return null;
            }

            var encryptedTokenCache = Convert.FromBase64String(tokenCacheProperty.GetString() ?? string.Empty);
            var tokenCacheJson = DecryptAesGcmString(encryptedTokenCache, key);
            using var tokenCacheDoc = JsonDocument.Parse(tokenCacheJson);
            return ParseClaudeDesktopTokenCache(tokenCacheDoc.RootElement);
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                ex,
                "Failed to read Claude Desktop OAuth token cache");
            return null;
        }
#else
        return null;
#endif
    }

    internal static ClaudeCredentials? ParseClaudeDesktopTokenCache(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        ClaudeCredentials? fallback = null;
        foreach (var entry in root.EnumerateObject())
        {
            var credentials = ParseClaudeDesktopTokenCacheEntry(entry.Value);
            if (credentials?.AccessToken is null)
            {
                continue;
            }

            if (entry.Name.EndsWith(":claude_code", StringComparison.OrdinalIgnoreCase))
            {
                return credentials;
            }

            fallback ??= credentials;
        }

        return fallback;
    }

    private static ClaudeCredentials? ParseClaudeDesktopTokenCacheEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("token", out var token) ||
            string.IsNullOrWhiteSpace(token.GetString()))
        {
            return null;
        }

        return new ClaudeCredentials
        {
            AccessToken = token.GetString(),
            RefreshToken = entry.TryGetProperty("refreshToken", out var refreshToken) ? refreshToken.GetString() : null,
            ExpiresAt = entry.TryGetProperty("expiresAt", out var expiresAt) ? expiresAt.GetInt64() : 0,
            SubscriptionType = entry.TryGetProperty("subscriptionType", out var subscriptionType) ? subscriptionType.GetString() : null,
            RateLimitTier = entry.TryGetProperty("rateLimitTier", out var rateLimitTier) ? rateLimitTier.GetString() : null,
        };
    }

    private async Task<UnifiedRateLimits?> FetchClaudeWebUsageAsync(ClaudeAccountInfo? accountInfo, CancellationToken ct)
    {
        if (this.TryGetFreshCachedLimits(out var cached, requireAuthoritative: true))
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(accountInfo?.OrganizationUuid))
        {
            return null;
        }

        var cookieHeader = this.TryReadClaudeDesktopCookieHeader();
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        await this.cacheLock.WaitAsync(ct);
        try
        {
            if (this.TryGetFreshCachedLimits(out cached, requireAuthoritative: true))
            {
                return cached;
            }

            return await this.FetchClaudeWebUsageAsync(accountInfo.OrganizationUuid, cookieHeader, ct);
        }
        finally
        {
            this.cacheLock.Release();
        }
    }

    internal async Task<UnifiedRateLimits?> FetchClaudeWebUsageAsync(string organizationUuid, string cookieHeader, CancellationToken ct)
    {
        try
        {
            using var httpClient = this.httpClientFactory.CreateClient();
            httpClient.Timeout = ApiTimeout;

            using var request = BuildClaudeWebUsageRequest(organizationUuid, cookieHeader);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ApiTimeout);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                "Claude web usage endpoint returned status {StatusCode}",
                (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                    this.logger,
                    "Claude web usage endpoint failed with status {StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var usage = JsonSerializer.Deserialize<ClaudeOAuthUsageResponse>(json);
            var result = MapOAuthUsageToRateLimits(usage);
            return this.CacheAndReturnUsageLimits(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                "Claude web usage endpoint timed out");
            return null;
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                this.logger,
                ex,
                "Claude web usage endpoint failed: {Message}",
                ex.Message);
            return null;
        }
    }

    internal static HttpRequestMessage BuildClaudeWebUsageRequest(string organizationUuid, string cookieHeader)
    {
        var escapedOrganizationUuid = Uri.EscapeDataString(organizationUuid);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ClaudeWebBaseUrl}/api/organizations/{escapedOrganizationUuid}/usage");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("x-organization-uuid", organizationUuid);
        request.Headers.UserAgent.ParseAdd("CodexBar/1.0");
        return request;
    }

    private string? TryReadClaudeDesktopCookieHeader()
    {
        if (ClaudeDesktopCookieHeaderOverride is not null)
        {
            return ClaudeDesktopCookieHeaderOverride;
        }

#if WINDOWS
        if ((CredentialsPathOverride is not null || ClaudeJsonPathOverride is not null) &&
            (ClaudeDesktopCookiesPathOverride is null || ClaudeDesktopLocalStatePathOverride is null))
        {
            return null;
        }

        if (!File.Exists(ClaudeDesktopLocalStatePath) || !File.Exists(ClaudeDesktopCookiesPath))
        {
            return null;
        }

        try
        {
            var key = ReadChromiumEncryptionKey(ClaudeDesktopLocalStatePath);
            if (key is null)
            {
                return null;
            }

            var cookies = ReadClaudeDesktopCookies(key, this.logger);
            return cookies.Count > 0
                ? string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"))
                : null;
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                ex,
                "Failed to read Claude Desktop web cookies");
            return null;
        }
#else
        return null;
#endif
    }

#if WINDOWS
    private static byte[]? ReadChromiumEncryptionKey(string localStatePath)
    {
        var json = File.ReadAllText(localStatePath);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
            !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyProp))
        {
            return null;
        }

        var encryptedKey = Convert.FromBase64String(encryptedKeyProp.GetString() ?? string.Empty);
        var dpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");
        if (encryptedKey.Length <= dpapiPrefix.Length || !encryptedKey.AsSpan(0, dpapiPrefix.Length).SequenceEqual(dpapiPrefix))
        {
            return null;
        }

        return ProtectedData.Unprotect(encryptedKey[dpapiPrefix.Length..], optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    private static List<ClaudeWebCookie> ReadClaudeDesktopCookies(byte[] key, ILogger? logger = null)
    {
        var cookies = new List<ClaudeWebCookie>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ClaudeDesktopCookiesPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT host_key, name, value, encrypted_value, expires_utc
            FROM cookies
            WHERE host_key = 'claude.ai' OR host_key = '.claude.ai' OR host_key LIKE '%.claude.ai'
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var host = reader.GetString(0);
                var name = reader.GetString(1);
                var value = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var encryptedValue = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader.GetValue(3);
                var expiresUtc = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);

                if (IsChromiumCookieExpired(expiresUtc))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(value) && encryptedValue.Length > 0)
                {
                    value = DecryptChromiumCookie(host, encryptedValue, key);
                }

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                {
                    cookies.Add(new ClaudeWebCookie(name, value));
                }
            }
            catch (Exception ex) when (IsRecoverableCookieReadException(ex))
            {
                logger?.LogDebug(ex, "Skipping unreadable Claude Desktop cookie row");
            }
        }

        return cookies;
    }

    private static string DecryptChromiumCookie(string hostKey, byte[] encryptedValue, byte[] key)
    {
        var prefix = Encoding.ASCII.GetString(encryptedValue.AsSpan(0, Math.Min(3, encryptedValue.Length)));
        if (prefix is "v20")
        {
            throw new NotSupportedException("Chromium app-bound v20 cookies are not supported.");
        }

        if (prefix is "v10" or "v11")
        {
            return DecodeChromiumCookiePlaintext(hostKey, DecryptAesGcmBytes(encryptedValue, key));
        }

        var plaintext = ProtectedData.Unprotect(encryptedValue, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static bool IsRecoverableCookieReadException(Exception ex) =>
        ex is CryptographicException
        or FormatException
        or InvalidCastException
        or InvalidOperationException
        or ArgumentOutOfRangeException
        or NotSupportedException;

    private static string DecryptAesGcmString(byte[] encryptedValue, byte[] key) =>
        Encoding.UTF8.GetString(DecryptAesGcmBytes(encryptedValue, key));

    private static byte[] DecryptAesGcmBytes(byte[] encryptedValue, byte[] key)
    {
        const int prefixLength = 3;
        const int nonceLength = 12;
        const int tagLength = 16;

        if (encryptedValue.Length <= prefixLength + nonceLength + tagLength)
        {
            return [];
        }

        var nonce = encryptedValue.AsSpan(prefixLength, nonceLength);
        var cipherTextLength = encryptedValue.Length - prefixLength - nonceLength - tagLength;
        var cipherText = encryptedValue.AsSpan(prefixLength + nonceLength, cipherTextLength);
        var tag = encryptedValue.AsSpan(encryptedValue.Length - tagLength, tagLength);
        var plaintext = new byte[cipherTextLength];

        using var aes = new AesGcm(key, tagLength);
        aes.Decrypt(nonce, cipherText, tag, plaintext);

        return plaintext;
    }

    private sealed record ClaudeWebCookie(string Name, string Value);
#endif

    internal static string DecodeChromiumCookiePlaintext(string hostKey, byte[] plaintext)
    {
        var hostDigest = SHA256.HashData(Encoding.UTF8.GetBytes(hostKey));
        if (plaintext.Length == hostDigest.Length &&
            plaintext.AsSpan().SequenceEqual(hostDigest))
        {
            return string.Empty;
        }

        if (plaintext.Length <= hostDigest.Length)
        {
            return Encoding.UTF8.GetString(plaintext);
        }

        return plaintext.AsSpan(0, hostDigest.Length).SequenceEqual(hostDigest)
            ? Encoding.UTF8.GetString(plaintext.AsSpan(hostDigest.Length))
            : Encoding.UTF8.GetString(plaintext);
    }

    internal static bool IsChromiumCookieExpired(long expiresUtc)
    {
        const long maxChromiumCookieExpiresUtc = 265046774399999999;

        if (expiresUtc <= 0)
        {
            return false;
        }

        if (expiresUtc > maxChromiumCookieExpiresUtc)
        {
            return false;
        }

        var expires = new DateTimeOffset(DateTime.FromFileTimeUtc(expiresUtc * 10));
        return expires <= DateTimeOffset.UtcNow;
    }
}
