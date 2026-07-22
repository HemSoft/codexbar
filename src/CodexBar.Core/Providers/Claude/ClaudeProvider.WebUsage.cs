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

    private static readonly string _defaultWebSessionCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codexbar",
        "claude-web-session.bin");

    internal static string? ClaudeDesktopLocalStatePathOverride { get; set; }

    internal static string? ClaudeDesktopCookiesPathOverride { get; set; }

    internal static string? ClaudeDesktopConfigPathOverride { get; set; }

    internal static string? ClaudeDesktopCookieHeaderOverride { get; set; }

    internal static string? WebSessionCachePathOverride { get; set; }

    private static string WebSessionCachePath => WebSessionCachePathOverride ?? _defaultWebSessionCachePath;

    /// <summary>
    /// Gets a value indicating whether the session cache should participate.
    /// </summary>
    private static bool WebSessionCacheEnabled =>
        WebSessionCachePathOverride is not null ||
        (ClaudeDesktopCookiesPathOverride is null &&
         ClaudeDesktopLocalStatePathOverride is null &&
         ClaudeDesktopCookieHeaderOverride is null);

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

        if (IsInBackoff(ref this.webUsageBackoffUntilTicks))
        {
            return null;
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

                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    // The persisted session is stale or revoked — drop it so the
                    // next successful live cookie read replaces it.
                    this.InvalidatePersistedWebSessionCookieHeader();
                    SetBackoff(ref this.webUsageBackoffUntilTicks, WebUsageForbiddenBackoff);
                }

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
        request.Headers.TryAddWithoutValidation("Origin", ClaudeWebBaseUrl);
        request.Headers.Referrer = new Uri($"{ClaudeWebBaseUrl}/settings/usage");
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

        var configuredCookieHeader = this.ResolveConfiguredClaudeWebCookieHeader();
        if (!string.IsNullOrWhiteSpace(configuredCookieHeader))
        {
            return configuredCookieHeader;
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
                return this.TryLoadPersistedWebSessionCookieHeader();
            }

            var cookies = ReadClaudeDesktopCookies(key, this.logger);
            if (cookies.Count == 0)
            {
                return this.TryLoadPersistedWebSessionCookieHeader();
            }

            var header = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
            this.PersistWebSessionCookieHeader(header);
            return header;
        }
        catch (Exception ex)
        {
            // Claude Desktop holds the cookies DB with an exclusive lock while it
            // runs, so live reads regularly fail. Fall back to the session header
            // persisted from the last successful read.
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                ex,
                "Failed to read Claude Desktop web cookies");
            return this.TryLoadPersistedWebSessionCookieHeader();
        }
#else
        return null;
#endif
    }

    private string? ResolveConfiguredClaudeWebCookieHeader()
    {
        var configured = EnvironmentVariableProvider("CLAUDE_WEB_SESSION_COOKIE")
            ?? EnvironmentVariableProvider("CLAUDE_AI_COOKIE")
            ?? this.settings.GetApiKey(CodexBar.Core.Models.ProviderId.Claude);

        return NormalizeClaudeWebCookieHeader(configured);
    }

    internal static string? NormalizeClaudeWebCookieHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.Contains('=') || trimmed.Contains(';')
            ? trimmed
            : $"sessionKey={trimmed}";
    }

#if WINDOWS
    /// <summary>
    /// Persists the claude.ai session cookie header, DPAPI-encrypted for the
    /// current user, so usage keeps working while Claude Desktop holds the
    /// live cookies DB locked.
    /// </summary>
    private void PersistWebSessionCookieHeader(string header)
    {
        if (!WebSessionCacheEnabled)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(WebSessionCachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(header),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(WebSessionCachePath, encrypted);
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                ex,
                "Failed to persist Claude web session cache");
        }
    }

    private string? TryLoadPersistedWebSessionCookieHeader()
    {
        if (!WebSessionCacheEnabled)
        {
            return null;
        }

        try
        {
            if (!File.Exists(WebSessionCachePath))
            {
                return null;
            }

            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(WebSessionCachePath),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                "Using persisted Claude web session cookie header");
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                this.logger,
                ex,
                "Failed to load persisted Claude web session cache");
            return null;
        }
    }

    private void InvalidatePersistedWebSessionCookieHeader()
    {
        if (!WebSessionCacheEnabled)
        {
            return;
        }

        TryDeleteFile(WebSessionCachePath);
        Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
            this.logger,
            "Invalidated persisted Claude web session cache");
    }
#else
    private string? TryLoadPersistedWebSessionCookieHeader() => null;

    private void InvalidatePersistedWebSessionCookieHeader()
    {
    }
#endif

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
        // Claude Desktop (Chromium) keeps the cookies DB WAL-journaled and locked
        // while running, so a direct read-only open fails with "unable to open
        // database file". Read from a private temp copy instead — the standard
        // approach for Chromium cookie stores.
        var tempDb = Path.Combine(Path.GetTempPath(), $"codexbar-claude-cookies-{Guid.NewGuid():N}.db");
        try
        {
            CopySharedFile(ClaudeDesktopCookiesPath, tempDb);
            TryCopySidecarFile(ClaudeDesktopCookiesPath + "-wal", tempDb + "-wal");
            TryCopySidecarFile(ClaudeDesktopCookiesPath + "-shm", tempDb + "-shm");
            return ReadCookiesFromDatabase(tempDb, key, logger);
        }
        finally
        {
            TryDeleteFile(tempDb);
            TryDeleteFile(tempDb + "-wal");
            TryDeleteFile(tempDb + "-shm");
        }
    }

    private static void TryCopySidecarFile(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                CopySharedFile(source, destination);
            }
        }
        catch (IOException)
        {
            // Sidecar unavailable — the main DB copy alone may miss the newest
            // cookies but is still readable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Copies a file that another process holds open for writing. File.Copy
    /// opens the source denying writers, which fails against Chromium's live
    /// databases — a manual stream copy with ReadWrite|Delete sharing works.
    /// </summary>
    private static void CopySharedFile(string source, string destination)
    {
        using var sourceStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var destinationStream = File.Create(destination);
        sourceStream.CopyTo(destinationStream);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static List<ClaudeWebCookie> ReadCookiesFromDatabase(string databasePath, byte[] key, ILogger? logger)
    {
        var cookies = new List<ClaudeWebCookie>();

        // ReadWrite so SQLite can recover the copied WAL; Pooling=false so the
        // file handle is released before the temp copy is deleted.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
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
