// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests for ClaudeProvider file I/O methods: ReadCredentials, ReadAccountInfo,
/// ReadStatsCache, PersistCredentials, and EnsureTokenFreshAsync.
/// Uses temporary files with path overrides to exercise the actual file-reading code paths.
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class ClaudeProviderFileIoTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _credentialsPath;
    private readonly string _statsCachePath;
    private readonly string _claudeJsonPath;

    public ClaudeProviderFileIoTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        this._credentialsPath = Path.Combine(this._tempDir, ".credentials.json");
        this._statsCachePath = Path.Combine(this._tempDir, "stats-cache.json");
        this._claudeJsonPath = Path.Combine(this._tempDir, ".claude.json");

        ClaudeProvider.CredentialsPathOverride = this._credentialsPath;
        ClaudeProvider.StatsCachePathOverride = this._statsCachePath;
        ClaudeProvider.ClaudeJsonPathOverride = this._claudeJsonPath;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = Path.Combine(this._tempDir, "Cookies");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = Path.Combine(this._tempDir, "Local State");
        ClaudeProvider.ClaudeDesktopConfigPathOverride = Path.Combine(this._tempDir, "desktop-config.json");
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = string.Empty;
        ClaudeProvider.EnvironmentAccessTokenOverride = null;
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = null;
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = null;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = null;
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;
        ClaudeProvider.EnvironmentAccessTokenOverride = null;

        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static ClaudeProvider CreateProvider(IHttpClientFactory? httpFactory = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        httpFactory ??= Substitute.For<IHttpClientFactory>();
        return new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            httpFactory,
            settings);
    }

    private static object? InvokePrivateMethod(ClaudeProvider provider, string methodName, params object?[] args)
    {
        var method = typeof(ClaudeProvider).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.Invoke(provider, args);
    }

    private static async Task<object?> InvokePrivateAsyncMethod(ClaudeProvider provider, string methodName, params object?[] args)
    {
        var method = typeof(ClaudeProvider).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(provider, args)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    // --- ReadCredentials ---
    [Fact]
    public void ReadCredentials_FileDoesNotExist_ReturnsNull()
    {
        ClaudeProvider.CredentialsPathOverride = Path.Combine(this._tempDir, "nonexistent.json");
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadCredentials");
        Assert.Null(result);
    }

    [Fact]
    public void ReadCredentials_ValidFile_ReturnsCredentials()
    {
        var json = """
        {
            "claudeAiOauth": {
                "subscriptionType": "pro",
                "rateLimitTier": "tier-1",
                "expiresAt": 1750000000,
                "accessToken": "test-access-token",
                "refreshToken": "test-refresh-token"
            }
        }
        """;
        File.WriteAllText(this._credentialsPath, json);
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadCredentials");
        Assert.NotNull(result);
        var credType = result!.GetType();
        Assert.Equal("pro", credType.GetProperty("SubscriptionType")!.GetValue(result) as string);
        Assert.Equal("test-access-token", credType.GetProperty("AccessToken")!.GetValue(result) as string);
        Assert.Equal("test-refresh-token", credType.GetProperty("RefreshToken")!.GetValue(result) as string);
        Assert.Equal(1750000000L, (long)credType.GetProperty("ExpiresAt")!.GetValue(result)!);
    }

    [Fact]
    public void ReadCredentials_FileAndEnvironmentTokenExist_ReturnsFileCredentials()
    {
        ClaudeProvider.EnvironmentAccessTokenOverride = "env-access-token";
        var json = """
        {
            "claudeAiOauth": {
                "subscriptionType": "pro",
                "expiresAt": 1780929880500,
                "accessToken": "file-access-token"
            }
        }
        """;
        File.WriteAllText(this._credentialsPath, json);
        var provider = CreateProvider();

        var result = InvokePrivateMethod(provider, "ReadCredentials");

        Assert.NotNull(result);
        var credType = result!.GetType();
        Assert.Equal("pro", credType.GetProperty("SubscriptionType")!.GetValue(result) as string);
        Assert.Equal("file-access-token", credType.GetProperty("AccessToken")!.GetValue(result) as string);
        Assert.Equal(1780929880500L, (long)credType.GetProperty("ExpiresAt")!.GetValue(result)!);
    }

    [Fact]
    public void ReadCredentials_FileMissingAndEnvironmentTokenExists_ReturnsEnvironmentCredentials()
    {
        ClaudeProvider.EnvironmentAccessTokenOverride = "env-access-token";
        ClaudeProvider.CredentialsPathOverride = Path.Combine(this._tempDir, "missing-credentials.json");
        var provider = CreateProvider();

        var result = InvokePrivateMethod(provider, "ReadCredentials");

        Assert.NotNull(result);
        var credType = result!.GetType();
        Assert.Equal("subscription", credType.GetProperty("SubscriptionType")!.GetValue(result) as string);
        Assert.Equal("env-access-token", credType.GetProperty("AccessToken")!.GetValue(result) as string);
    }

    [Fact]
    public void ReadCredentials_FileMissingOAuthSection_ReturnsNull()
    {
        var json = """{"someOtherKey": "value"}""";
        File.WriteAllText(this._credentialsPath, json);
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadCredentials");
        Assert.Null(result);
    }

    [Fact]
    public void ReadCredentials_InvalidJson_ReturnsNull()
    {
        File.WriteAllText(this._credentialsPath, "not valid json {{{}");
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadCredentials");
        Assert.Null(result);
    }

    // --- ReadAccountInfo ---
    [Fact]
    public void ReadAccountInfo_FileDoesNotExist_ReturnsNull()
    {
        ClaudeProvider.ClaudeJsonPathOverride = Path.Combine(this._tempDir, "nonexistent.json");
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadAccountInfo");
        Assert.Null(result);
    }

    [Fact]
    public void ReadAccountInfo_ValidFile_ReturnsAccountInfo()
    {
        var json = """
        {
            "oauthAccount": {
                "displayName": "Test User",
                "billingType": "pro",
                "hasExtraUsageEnabled": true
            }
        }
        """;
        File.WriteAllText(this._claudeJsonPath, json);
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadAccountInfo");
        Assert.NotNull(result);
        var infoType = result!.GetType();
        Assert.Equal("Test User", infoType.GetProperty("DisplayName")!.GetValue(result) as string);
        Assert.Equal("pro", infoType.GetProperty("BillingType")!.GetValue(result) as string);
        Assert.True((bool)infoType.GetProperty("HasExtraUsageEnabled")!.GetValue(result)!);
    }

    [Fact]
    public void ReadAccountInfo_MissingOAuthAccount_ReturnsNull()
    {
        var json = """{"otherKey": "value"}""";
        File.WriteAllText(this._claudeJsonPath, json);
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadAccountInfo");
        Assert.Null(result);
    }

    [Fact]
    public void ReadAccountInfo_InvalidJson_ReturnsNull()
    {
        File.WriteAllText(this._claudeJsonPath, "invalid json");
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadAccountInfo");
        Assert.Null(result);
    }

    // --- ReadStatsCache ---
    [Fact]
    public void ReadStatsCache_FileDoesNotExist_ReturnsNull()
    {
        ClaudeProvider.StatsCachePathOverride = Path.Combine(this._tempDir, "nonexistent.json");
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadStatsCache");
        Assert.Null(result);
    }

    [Fact]
    public void ReadStatsCache_ValidFile_WithModelUsage_ReturnsStats()
    {
        var json = """
        {
            "totalSessions": 42,
            "totalMessages": 100,
            "modelUsage": {
                "claude-sonnet-4-5": {
                    "inputTokens": 10000,
                    "outputTokens": 5000,
                    "cacheReadInputTokens": 200,
                    "cacheCreationInputTokens": 300
                },
                "claude-haiku-4-5": {
                    "inputTokens": 2000,
                    "outputTokens": 1000,
                    "cacheReadInputTokens": 50,
                    "cacheCreationInputTokens": 100
                }
            }
        }
        """;
        File.WriteAllText(this._statsCachePath, json);
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadStatsCache");
        Assert.NotNull(result);
        var statsType = result!.GetType();
        Assert.Equal(42, (int)statsType.GetProperty("TotalSessions")!.GetValue(result)!);
        Assert.Equal(100, (int)statsType.GetProperty("TotalMessages")!.GetValue(result)!);
        var modelUsages = statsType.GetProperty("ModelUsages")!.GetValue(result) as System.Collections.IList;
        Assert.NotNull(modelUsages);
        Assert.Equal(2, modelUsages!.Count);
    }

    [Fact]
    public void ReadStatsCache_ValidFile_NoModelUsage_ReturnsStatsWithEmptyList()
    {
        var json = """
        {
            "totalSessions": 5,
            "totalMessages": 10
        }
        """;
        File.WriteAllText(this._statsCachePath, json);
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadStatsCache");
        Assert.NotNull(result);
        var statsType = result!.GetType();
        Assert.Equal(5, (int)statsType.GetProperty("TotalSessions")!.GetValue(result)!);
        var modelUsages = statsType.GetProperty("ModelUsages")!.GetValue(result) as System.Collections.IList;
        Assert.NotNull(modelUsages);
        Assert.Empty(modelUsages!);
    }

    [Fact]
    public void ReadStatsCache_InvalidJson_ReturnsNull()
    {
        File.WriteAllText(this._statsCachePath, "not valid json");
        var provider = CreateProvider();
        var result = InvokePrivateMethod(provider, "ReadStatsCache");
        Assert.Null(result);
    }

    // --- PersistCredentials ---
    [Fact]
    public void PersistCredentials_FileDoesNotExist_SilentlySkips()
    {
        ClaudeProvider.CredentialsPathOverride = Path.Combine(this._tempDir, "nonexistent.json");
        var provider = CreateProvider();
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "new-token");
        credType.GetProperty("RefreshToken")!.SetValue(cred, "new-refresh");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, 1800000000L);

        var ex = Record.Exception(() => InvokePrivateMethod(provider, "PersistCredentials", cred));
        Assert.Null(ex);
    }

    [Fact]
    public void PersistCredentials_ValidFile_UpdatesTokens()
    {
        var originalJson = """
        {
            "claudeAiOauth": {
                "subscriptionType": "pro",
                "accessToken": "old-token",
                "refreshToken": "old-refresh",
                "expiresAt": 1700000000,
                "rateLimitTier": "tier-1"
            },
            "otherKey": "preserved"
        }
        """;
        File.WriteAllText(this._credentialsPath, originalJson);
        var provider = CreateProvider();
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "new-token");
        credType.GetProperty("RefreshToken")!.SetValue(cred, "new-refresh");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, 1800000000L);

        InvokePrivateMethod(provider, "PersistCredentials", cred);

        var saved = File.ReadAllText(this._credentialsPath);
        Assert.Contains("new-token", saved);
        Assert.Contains("new-refresh", saved);
        Assert.Contains("1800000000", saved);
        Assert.Contains("otherKey", saved);
        Assert.Contains("preserved", saved);
    }

    [Fact]
    public void PersistCredentials_InvalidJson_SwallowsParseError()
    {
        File.WriteAllText(this._credentialsPath, "not json");
        var provider = CreateProvider();
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "token");
        credType.GetProperty("RefreshToken")!.SetValue(cred, "refresh");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, 1800000000L);

        var ex = Record.Exception(() => InvokePrivateMethod(provider, "PersistCredentials", cred));
        Assert.Null(ex);
    }

    // --- EnsureTokenFreshAsync ---
    [Fact]
    public async Task EnsureTokenFreshAsync_ExpiresAtZero_ReturnsOriginalCredentials()
    {
        var provider = CreateProvider();
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "valid-token");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, 0L);

        var result = await InvokePrivateAsyncMethod(provider, "EnsureTokenFreshAsync", cred, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(cred, result);
    }

    [Fact]
    public async Task EnsureTokenFreshAsync_TokenNotExpired_ReturnsOriginalCredentials()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var provider = CreateProvider();
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "valid-token");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, futureExpiry);

        var result = await InvokePrivateAsyncMethod(provider, "EnsureTokenFreshAsync", cred, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(cred, result);
    }

    [Fact]
    public async Task EnsureTokenFreshAsync_TokenExpired_NoRefreshToken_ReturnsOriginalCredentials()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(httpFactory);
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "expired-token");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, pastExpiry);
        credType.GetProperty("RefreshToken")!.SetValue(cred, (string?)null);

        var result = await InvokePrivateAsyncMethod(provider, "EnsureTokenFreshAsync", cred, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(cred, result);
    }

    [Fact]
    public async Task EnsureTokenFreshAsync_TokenExpired_WithRefreshToken_AttemptsRefresh()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var refreshResponse = """
        {
            "access_token": "refreshed-token",
            "refresh_token": "new-refresh",
            "expires_at": 1800000000
        }
        """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(refreshResponse, Encoding.UTF8, "application/json"),
        };
        var handler = new TestResponseHandler(response);
        var httpClient = new HttpClient(handler);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        // PersistCredentials will check for the file
        ClaudeProvider.CredentialsPathOverride = Path.Combine(this._tempDir, "nonexistent-for-persist.json");

        var provider = CreateProvider(httpFactory);
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "expired-token");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, pastExpiry);
        credType.GetProperty("RefreshToken")!.SetValue(cred, "old-refresh");

        var result = await InvokePrivateAsyncMethod(provider, "EnsureTokenFreshAsync", cred, CancellationToken.None);
        Assert.NotNull(result);
        var resultType = result!.GetType();
        Assert.Equal("refreshed-token", resultType.GetProperty("AccessToken")!.GetValue(result) as string);
    }

    [Fact]
    public async Task EnsureTokenFreshAsync_MillisecondEpoch_NormalizesCorrectly()
    {
        var futureMs = (DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() * 1000) + 500;
        var provider = CreateProvider();
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "valid-token");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, futureMs);

        var result = await InvokePrivateAsyncMethod(provider, "EnsureTokenFreshAsync", cred, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(cred, result);
    }

    [Fact]
    public async Task EnsureTokenFreshAsync_InvalidExpiresAt_ReturnsOriginalCredentials()
    {
        var provider = CreateProvider();
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        var cred = Activator.CreateInstance(credType)!;
        credType.GetProperty("AccessToken")!.SetValue(cred, "valid-token");
        credType.GetProperty("ExpiresAt")!.SetValue(cred, long.MaxValue);

        var result = await InvokePrivateAsyncMethod(provider, "EnsureTokenFreshAsync", cred, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(cred, result);
    }

    // --- FetchUsageAsync full path with credentials on disk ---
    [Fact]
    public async Task FetchUsageAsync_WithValidCredentialsOnDisk_ReturnsSuccess()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var credsJson = $$"""
        {
            "claudeAiOauth": {
                "subscriptionType": "pro",
                "accessToken": "test-token",
                "refreshToken": "test-refresh",
                "expiresAt": {{futureExpiry}}
            }
        }
        """;
        var statsJson = """
        {
            "totalSessions": 10,
            "totalMessages": 50,
            "modelUsage": {
                "claude-sonnet-4-5": {
                    "inputTokens": 5000,
                    "outputTokens": 2500,
                    "cacheReadInputTokens": 100,
                    "cacheCreationInputTokens": 200
                }
            }
        }
        """;
        var accountJson = """
        {
            "oauthAccount": {
                "displayName": "Test User",
                "billingType": "pro",
                "hasExtraUsageEnabled": false
            }
        }
        """;

        File.WriteAllText(this._credentialsPath, credsJson);
        File.WriteAllText(this._statsCachePath, statsJson);
        File.WriteAllText(this._claudeJsonPath, accountJson);

        var apiResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                    "five_hour": {
                        "utilization": 45,
                        "resets_at": "2026-05-25T15:00:00Z"
                    },
                    "seven_day": {
                        "utilization": 25,
                        "resets_at": "2026-05-29T15:00:00Z"
                    }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };

        var handler = new TestResponseHandler(apiResponse);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            httpFactory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.Claude, result.Provider);
        Assert.NotNull(result.SessionUsage);
        Assert.NotNull(result.WeeklyUsage);
        Assert.Equal(0.45, result.SessionUsage!.UsedPercent, 0.01);
        Assert.Equal(0.25, result.WeeklyUsage!.UsedPercent, 0.01);
        Assert.Contains("Test User", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_CredentialsExistButCorrupt_ReturnsFailure()
    {
        File.WriteAllText(this._credentialsPath, "corrupt json data {{");
        var provider = CreateProvider();
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_NoCredentialsFile_ReturnsNoCredentialsError()
    {
        ClaudeProvider.CredentialsPathOverride = Path.Combine(this._tempDir, "does_not_exist.json");
        var provider = CreateProvider();
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No Claude Code credentials found", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_TokenExpiredRefreshFails_UsesExistingToken()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var credsJson = $$"""
        {
            "claudeAiOauth": {
                "subscriptionType": "pro",
                "accessToken": "expired-token",
                "refreshToken": "invalid-refresh",
                "expiresAt": {{pastExpiry}}
            }
        }
        """;
        File.WriteAllText(this._credentialsPath, credsJson);

        var handler = new DelegatingHandlerFunc(request =>
            request.RequestUri?.AbsolutePath == "/v1/oauth/token"
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("bad request", Encoding.UTF8, "text/plain"),
                }
                : CreateRateLimitResponse("0.12", "0.02"));
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            httpFactory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(0.12, result.SessionUsage!.UsedPercent, 0.01);
        Assert.Equal(0.02, result.WeeklyUsage!.UsedPercent, 0.01);
    }

    private static HttpResponseMessage CreateRateLimitResponse(string fiveHour, string sevenDay)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"msg_123","type":"message","content":[]}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", fiveHour);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", sevenDay);
        return response;
    }

    [Fact]
    public async Task FetchUsageAsync_CancellationRequested_ThrowsOperationCancelled()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var credsJson = $$"""
        {
            "claudeAiOauth": {
                "subscriptionType": "pro",
                "accessToken": "test-token",
                "expiresAt": {{futureExpiry}}
            }
        }
        """;
        File.WriteAllText(this._credentialsPath, credsJson);
        ClaudeProvider.StatsCachePathOverride = Path.Combine(this._tempDir, "no-stats.json");
        ClaudeProvider.ClaudeJsonPathOverride = Path.Combine(this._tempDir, "no-claude.json");

        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CancellingHandler();
        var httpClient = new HttpClient(handler);
        httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            httpFactory,
            settings);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.FetchUsageAsync(cts.Token));
    }

    [Fact]
    public async Task FetchUsageAsync_GenericException_ReturnsFailure()
    {
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var credsJson = $$"""
        {
            "claudeAiOauth": {
                "subscriptionType": "pro",
                "accessToken": "test-token",
                "expiresAt": {{futureExpiry}}
            }
        }
        """;
        File.WriteAllText(this._credentialsPath, credsJson);
        ClaudeProvider.StatsCachePathOverride = Path.Combine(this._tempDir, "no-stats.json");
        ClaudeProvider.ClaudeJsonPathOverride = Path.Combine(this._tempDir, "no-claude.json");

        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new ThrowingHandler(new InvalidOperationException("Unexpected"));
        var httpClient = new HttpClient(handler);
        httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            httpFactory,
            settings);

        // Dispose the internal cacheLock to trigger ObjectDisposedException in FetchRateLimitsAsync
        // which propagates to the outer catch (Exception ex) block in FetchUsageAsync
        var lockField = typeof(ClaudeProvider).GetField("cacheLock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var semaphore = (SemaphoreSlim)lockField.GetValue(provider)!;
        semaphore.Dispose();

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    // --- LoadAndValidateCredentials ---
    [Fact]
    public void LoadAndValidateCredentials_CredentialsMissing_FileExists_ReturnsNoCredentialsError()
    {
        // File exists but doesn't have claudeAiOauth
        var json = """{"noOauth": true}""";
        File.WriteAllText(this._credentialsPath, json);

        var provider = CreateProvider();
        var method = typeof(ClaudeProvider).GetMethod(
            "LoadAndValidateCredentials",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var result = method!.Invoke(provider, null);
        var resultType = result!.GetType();
        var creds = resultType.GetField("Item1")?.GetValue(result);
        var error = resultType.GetField("Item2")?.GetValue(result) as string;

        Assert.Null(creds);
        Assert.Contains("No Claude Code OAuth credentials found", error);
    }

    [Fact]
    public void LoadAndValidateCredentials_McpOauthOnly_ReturnsMissingClaudeCodeCredentialsError()
    {
        var json = """{"mcpOAuth":{"plugin:github|abc":{"accessToken":"token"}}}""";
        File.WriteAllText(this._credentialsPath, json);

        var provider = CreateProvider();
        var method = typeof(ClaudeProvider).GetMethod(
            "LoadAndValidateCredentials",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var result = method!.Invoke(provider, null);
        var resultType = result!.GetType();
        var creds = resultType.GetField("Item1")?.GetValue(result);
        var error = resultType.GetField("Item2")?.GetValue(result) as string;

        Assert.Null(creds);
        Assert.Contains("Claude MCP credentials exist", error);
    }

    // Helper message handlers for tests
    private sealed class TestResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _payload;
        private readonly string? _mediaType;
        private readonly List<KeyValuePair<string, IEnumerable<string>>> _responseHeaders;

        public TestResponseHandler(HttpResponseMessage response)
        {
            this._statusCode = response.StatusCode;
            this._payload = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            this._mediaType = response.Content?.Headers.ContentType?.MediaType;
            this._responseHeaders = response.Headers.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpResponseMessage(this._statusCode);
            if (this._payload is not null)
            {
                clone.Content = this._mediaType is null
                    ? new StringContent(this._payload, Encoding.UTF8)
                    : new StringContent(this._payload, Encoding.UTF8, this._mediaType);
            }

            foreach (var header in this._responseHeaders)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return Task.FromResult(clone);
        }
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    [Fact]
    public async Task FetchUsageAsync_ExpiredToken_RefreshSucceeds_FetchesUsage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"claude-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credPath = Path.Combine(tempDir, "credentials.json");
            var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            File.WriteAllText(credPath, $$"""
                {
                    "claudeAiOauth": {
                        "subscriptionType": "pro",
                        "expiresAt": {{pastExpiry}},
                        "accessToken": "expired-token",
                        "refreshToken": "valid-refresh"
                    }
                }
                """);

            int refreshCallCount = 0;
            int totalRequestCount = 0;
            var handler = new DelegatingHandlerFunc(req =>
            {
                Interlocked.Increment(ref totalRequestCount);
                if (req.RequestUri!.AbsolutePath.Contains("oauth/token"))
                {
                    Interlocked.Increment(ref refreshCallCount);
                    var refreshResponse = """
                        {
                            "access_token": "new-at",
                            "refresh_token": "new-rt",
                            "expires_at": 9999999999
                        }
                        """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(refreshResponse, Encoding.UTF8, "application/json"),
                    };
                }

                var msgResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"msg_123","type":"message","content":[]}""", Encoding.UTF8, "application/json"),
                };
                msgResponse.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.3");
                msgResponse.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.1");
                return msgResponse;
            });

            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
            var settings = Substitute.For<ISettingsService>();
            settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

            var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, httpFactory, settings);
            ClaudeProvider.CredentialsPathOverride = credPath;

            var result = await provider.FetchUsageAsync();

            Assert.True(refreshCallCount >= 1, "Expected at least one call to /oauth/token for token refresh");
            Assert.True(totalRequestCount >= 2, "Expected at least a refresh call and a usage fetch call");
            Assert.True(result.Success, $"Expected successful fetch after token refresh, got: {result.ErrorMessage}");
        }
        finally
        {
            ClaudeProvider.CredentialsPathOverride = null;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FetchUsageAsync_ExpiredToken_RefreshFails_UsesExistingToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"claude-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credPath = Path.Combine(tempDir, "credentials.json");
            var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            File.WriteAllText(credPath, $$"""
                {
                    "claudeAiOauth": {
                        "subscriptionType": "pro",
                        "expiresAt": {{pastExpiry}},
                        "accessToken": "expired-token",
                        "refreshToken": "bad-refresh"
                    }
                }
                """);

            var handler = new DelegatingHandlerFunc(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("oauth/token"))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
                    };
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"msg_123","type":"message","content":[]}""", Encoding.UTF8, "application/json"),
                };
                response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.3");
                response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.1");
                return response;
            });

            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
            var settings = Substitute.For<ISettingsService>();
            settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

            var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, httpFactory, settings);
            ClaudeProvider.CredentialsPathOverride = credPath;

            var result = await provider.FetchUsageAsync();

            Assert.True(result.Success);
            Assert.Equal(0.3, result.SessionUsage!.UsedPercent, 0.01);
        }
        finally
        {
            ClaudeProvider.CredentialsPathOverride = null;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class DelegatingHandlerFunc(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
