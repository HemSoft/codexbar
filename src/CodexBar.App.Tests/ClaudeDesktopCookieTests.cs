// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodexBar.Core.Providers.Claude;
using Microsoft.Data.Sqlite;

[Collection("ClaudeDesktopCookieFileIo")]
public sealed class ClaudeDesktopCookieTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeDesktopCookieTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_app_cookie_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = null;
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = null;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = null;
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;

        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ReadChromiumEncryptionKey_ValidDpapiLocalState_ReturnsUnprotectedKey()
    {
        var expectedKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var encryptedKey = Convert.ToBase64String(CreateDpapiLocalStateKey(expectedKey));
        var localStatePath = Path.Combine(this._tempDir, "Local State");
        File.WriteAllText(localStatePath, $$"""
            {
                "os_crypt": {
                    "encrypted_key": "{{encryptedKey}}"
                }
            }
            """);

        var result = (byte[]?)InvokePrivateStaticMethod("ReadChromiumEncryptionKey", localStatePath);

        Assert.Equal(expectedKey, result);
    }

    [Fact]
    public void ReadChromiumEncryptionKey_InvalidLocalState_ReturnsNull()
    {
        var localStatePath = Path.Combine(this._tempDir, "Local State");
        File.WriteAllText(localStatePath, """{"os_crypt":{"encrypted_key":"bm90LWRwYXBp"}}""");

        var result = InvokePrivateStaticMethod("ReadChromiumEncryptionKey", localStatePath);

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopCookies_MixedCookieRows_ReturnsUnexpiredClaudeCookies()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var futureExpiresUtc = DateTimeOffset.UtcNow.AddDays(1).ToFileTime() / 10;
        var pastExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1).ToFileTime() / 10;
        var cookiesPath = this.CreateCookiesDatabase(
            new CookieRow("claude.ai", "plainSession", "plain-value", null, futureExpiresUtc),
            new CookieRow(
                ".claude.ai",
                "encryptedSession",
                string.Empty,
                CreateAesGcmChromiumCookie(".claude.ai", "encrypted-value", key),
                futureExpiresUtc),
            new CookieRow("app.claude.ai", "legacySession", string.Empty, CreateDpapiCookie("legacy-value"), futureExpiresUtc),
            new CookieRow(
                "claude.ai",
                "malformedEncrypted",
                string.Empty,
                CreateMalformedAesGcmChromiumCookie(),
                futureExpiresUtc),
            new CookieRow(
                "claude.ai",
                "appBoundSession",
                string.Empty,
                CreateAesGcmChromiumCookie("claude.ai", "app-bound-value", key, "v20"),
                futureExpiresUtc),
            new CookieRow("claude.ai", "expiredSession", "expired-value", null, pastExpiresUtc),
            new CookieRow(".claude.ai", string.Empty, "missing-name", null, null),
            new CookieRow("claude.ai", "emptyEncrypted", string.Empty, [], futureExpiresUtc),
            new CookieRow("example.com", "ignored", "ignored-value", null, futureExpiresUtc));

        ClaudeProvider.ClaudeDesktopCookiesPathOverride = cookiesPath;

        var result = InvokePrivateStaticMethod("ReadClaudeDesktopCookies", key, null);

        var cookies = ReadCookiePairs(result);
        Assert.Equal(
            [
                ("plainSession", "plain-value"),
                ("encryptedSession", "encrypted-value"),
                ("legacySession", "legacy-value"),
            ],
            cookies);
    }

    private static object? InvokePrivateStaticMethod(string methodName, params object?[] args)
    {
        var method = typeof(ClaudeProvider).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args);
    }

    private string CreateCookiesDatabase(params CookieRow[] rows)
    {
        var path = Path.Combine(this._tempDir, "Cookies");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE cookies (
                    host_key TEXT NOT NULL,
                    name TEXT NOT NULL,
                    value TEXT NULL,
                    encrypted_value BLOB NULL,
                    expires_utc INTEGER NULL
                )
                """;
            command.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO cookies (host_key, name, value, encrypted_value, expires_utc)
                VALUES ($host, $name, $value, $encryptedValue, $expiresUtc)
                """;
            command.Parameters.AddWithValue("$host", row.HostKey);
            command.Parameters.AddWithValue("$name", row.Name);
            command.Parameters.AddWithValue("$value", row.Value is null ? DBNull.Value : row.Value);
            command.Parameters.AddWithValue("$encryptedValue", row.EncryptedValue is null ? DBNull.Value : row.EncryptedValue);
            command.Parameters.AddWithValue("$expiresUtc", row.ExpiresUtc is null ? DBNull.Value : row.ExpiresUtc);
            command.ExecuteNonQuery();
        }

        return path;
    }

    private static byte[] CreateAesGcmChromiumCookie(string hostKey, string value, byte[] key, string prefix = "v10")
    {
        const int tagLength = 16;

        var nonce = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var plaintext = SHA256.HashData(Encoding.UTF8.GetBytes(hostKey))
            .Concat(Encoding.UTF8.GetBytes(value))
            .ToArray();
        var cipherText = new byte[plaintext.Length];
        var tag = new byte[tagLength];

        using var aes = new AesGcm(key, tagLength);
        aes.Encrypt(nonce, plaintext, cipherText, tag);

        return Encoding.ASCII.GetBytes(prefix)
            .Concat(nonce)
            .Concat(cipherText)
            .Concat(tag)
            .ToArray();
    }

    private static byte[] CreateMalformedAesGcmChromiumCookie() =>
        Encoding.ASCII.GetBytes("v10")
            .Concat(new byte[29])
            .ToArray();

    private static byte[] CreateDpapiCookie(string value) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);

    private static byte[] CreateDpapiLocalStateKey(byte[] key)
    {
        var protectedKey = ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.ASCII.GetBytes("DPAPI").Concat(protectedKey).ToArray();
    }

    private static IReadOnlyList<(string Name, string Value)> ReadCookiePairs(object? cookies)
    {
        var pairs = new List<(string Name, string Value)>();
        foreach (var cookie in (System.Collections.IEnumerable)cookies!)
        {
            var type = cookie.GetType();
            pairs.Add((
                (string)type.GetProperty("Name")!.GetValue(cookie)!,
                (string)type.GetProperty("Value")!.GetValue(cookie)!));
        }

        return pairs;
    }

    private sealed record CookieRow(
        string HostKey,
        string Name,
        string? Value,
        byte[]? EncryptedValue,
        long? ExpiresUtc);
}

[CollectionDefinition("ClaudeDesktopCookieFileIo", DisableParallelization = true)]
public sealed class ClaudeDesktopCookieFileIoCollection
{
}
