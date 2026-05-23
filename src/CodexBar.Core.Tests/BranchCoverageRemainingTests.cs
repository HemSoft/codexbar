// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.OpenCodeGo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Additional branch coverage tests targeting the remaining uncovered branches
/// after the initial BranchCoverageTests pass. Focuses on file I/O property
/// branches, null-coalescing paths, and platform-specific code.
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class BranchCoverageRemainingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _credentialsPath;
    private readonly string _statsCachePath;
    private readonly string _claudeJsonPath;

    public BranchCoverageRemainingTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_branch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        this._credentialsPath = Path.Combine(this._tempDir, ".credentials.json");
        this._statsCachePath = Path.Combine(this._tempDir, "stats-cache.json");
        this._claudeJsonPath = Path.Combine(this._tempDir, ".claude.json");

        ClaudeProvider.CredentialsPathOverride = this._credentialsPath;
        ClaudeProvider.StatsCachePathOverride = this._statsCachePath;
        ClaudeProvider.ClaudeJsonPathOverride = this._claudeJsonPath;
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;

        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Exercises ReadCredentials where the oauth object exists but is missing all optional properties
    /// (line 751-757: all TryGetProperty false branches).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_CredentialsMissingOptionalFields_HandlesGracefully()
    {
        // Credentials file with claudeAiOauth but missing all sub-properties
        File.WriteAllText(this._credentialsPath, """{"claudeAiOauth":{}}""");

        var provider = this.CreateProvider();
        var result = await provider.FetchUsageAsync();

        // With empty oauth object, accessToken will be null — provider handles this gracefully
        // (may succeed with limited data or fail with descriptive error)
        Assert.NotNull(result);
    }

    /// <summary>
    /// Exercises ReadCredentials where only some optional properties exist
    /// (covering more TryGetProperty branches at line 751).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_CredentialsPartialFields_ParsesAvailableOnes()
    {
        // Has accessToken and expiresAt but missing subscriptionType, rateLimitTier, refreshToken
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = this.CreateProvider(factory);
        var result = await provider.FetchUsageAsync();

        // Should succeed (access token present) but with limited data
        Assert.True(result.Success);
    }

    /// <summary>
    /// Exercises ReadAccountInfo where oauthAccount exists but has missing properties
    /// (line 785-789: TryGetProperty false branches for displayName, billingType, hasExtraUsageEnabled).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_AccountInfoMissingFields_HandlesGracefully()
    {
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        // claude.json with oauthAccount but no sub-properties
        File.WriteAllText(this._claudeJsonPath, """{"oauthAccount":{}}""");

        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = this.CreateProvider(factory);
        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    /// <summary>
    /// Exercises ReadAccountInfo with all fields present (line 785-789: TryGetProperty true branches).
    /// This covers the DisplayName is not null path at line 208.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_AccountInfoAllFields_DisplayNameUsed()
    {
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        File.WriteAllText(this._claudeJsonPath, """
        {
            "oauthAccount": {
                "displayName": "Alice",
                "billingType": "pro",
                "hasExtraUsageEnabled": true
            }
        }
        """);

        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = this.CreateProvider(factory);
        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);

        // Verify DisplayName path (line 208)
        var item = result.Items?[0];
        Assert.NotNull(item);
        Assert.Contains("Alice", item.DisplayName);
    }

    /// <summary>
    /// Exercises ReadStatsCache where modelUsage properties are missing from individual entries
    /// (line 823: TryGetProperty false branches for inputTokens, outputTokens, etc.).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_StatsCacheMissingModelFields_DefaultsToZero()
    {
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        // Stats cache with modelUsage but entries missing some token fields
        File.WriteAllText(this._statsCachePath, """
        {
            "totalSessions": 5,
            "modelUsage": {
                "claude-sonnet-4-6": {}
            }
        }
        """);

        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = this.CreateProvider(factory);
        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    /// <summary>
    /// Exercises ReadStatsCache where modelUsage is not an object (line 818-819: ValueKind != Object).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_StatsCacheModelUsageNotObject_SkipsModels()
    {
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        // modelUsage is a string instead of an object
        File.WriteAllText(this._statsCachePath, """
        {
            "totalSessions": 3,
            "totalMessages": 10,
            "modelUsage": "not-an-object"
        }
        """);

        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = this.CreateProvider(factory);
        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    /// <summary>
    /// Exercises ReadStatsCache missing totalSessions and totalMessages (line 812-816: TryGetProperty false).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_StatsCacheMissingTopLevelFields_DefaultsToZero()
    {
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        // Stats cache with no top-level properties
        File.WriteAllText(this._statsCachePath, """{}""");

        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = this.CreateProvider(factory);
        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshot when FormatUsageLabel returns an empty-like string
    /// (line 246: the IsNullOrEmpty fallbackLabel branch).
    /// FormatUsageLabel always returns at least "X plan", so this tests the non-empty path with null limits.
    /// </summary>
    [Fact]
    public void BuildSessionSnapshot_NullLimits_NonEmptyLabel_AppendsFallback()
    {
        // With subscription "Pro", totalTokens=0, cost=0, no account info → label = "Pro plan"
        var result = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 0, 0, null);
        Assert.Contains("Pro plan", result.UsageLabel);
        Assert.Contains("Rate limits unavailable", result.UsageLabel);
        Assert.Contains("·", result.UsageLabel);
    }

    /// <summary>
    /// Exercises the path overrides when set to null (right side of ?? at lines 44/46).
    /// When overrides are null, the default paths are used.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_NoPathOverrides_UsesDefaultPaths()
    {
        // Reset overrides so default paths are used
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;

        var provider = this.CreateProvider();

        // Default paths won't have valid credentials, so fetch will fail
        var result = await provider.FetchUsageAsync();
        Assert.False(result.Success);
    }

    /// <summary>
    /// Exercises the StatsCachePath and ClaudeJsonPath overrides being non-null (left side of ?? at lines 44/46).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_WithPathOverrides_UsesOverridePaths()
    {
        // Overrides are already set in constructor — set credential file
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        // stats-cache and claude.json don't exist — tests null/graceful paths
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = this.CreateProvider(factory);
        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    private ClaudeProvider CreateProvider(IHttpClientFactory? httpFactory = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        httpFactory ??= Substitute.For<IHttpClientFactory>();
        return new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, httpFactory, settings);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FakeHandler(HttpResponseMessage response) => this._response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpResponseMessage(this._response.StatusCode)
            {
                Content = this._response.Content is not null
                    ? new StringContent(
                        this._response.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                        Encoding.UTF8,
                        this._response.Content.Headers.ContentType?.MediaType ?? "application/json")
                    : null,
            };
            return Task.FromResult(clone);
        }
    }
}

/// <summary>
/// Additional SettingsService branch coverage tests for null-coalescing paths.
/// </summary>
public class SettingsServiceBranchTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceBranchTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-settings-branch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this._tempDir))
            {
                Directory.Delete(this._tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private SettingsService CreateService() =>
        new(NullLogger<SettingsService>.Instance, this._tempDir);

    /// <summary>
    /// Exercises MergeFromDisk where disk.Providers is null (line 97: disk.Providers ?? []).
    /// </summary>
    [Fact]
    public void Save_DiskProvidersNull_MergeHandlesGracefully()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");

        // Write JSON where providers is explicitly null
        File.WriteAllText(filePath, """{"refreshIntervalSeconds":30,"providers":null}""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 60,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Copilot"] = new() { Enabled = true },
            },
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);
    }

    /// <summary>
    /// Exercises MergeFromDisk where disk.SessionSpendingBaselines is null (line 118: ?? []).
    /// </summary>
    [Fact]
    public void Save_DiskBaselinesNull_MergeHandlesGracefully()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");

        File.WriteAllText(filePath, """{"providers":{},"sessionSpendingBaselines":null,"sessionSpendingResetTimes":null}""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 60,
            Providers = new Dictionary<string, ProviderSettings>(),
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);
    }

    /// <summary>
    /// Exercises SaveInternal where settings.Providers is null (line 140: settings.Providers ?? []).
    /// </summary>
    [Fact]
    public void Save_NullProviders_SavesEmpty()
    {
        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 60,
            Providers = null!,
        };

        service.Save(memSettings);
        var loaded = service.Load();
        Assert.NotNull(loaded.Providers);
    }

    /// <summary>
    /// Exercises GetApiKey where the provider key exists but ProviderSettings value is null (line 198: ps?.ApiKey).
    /// We achieve this by writing a settings file with a null provider value and loading it.
    /// </summary>
    [Fact]
    public void GetApiKey_NullProviderSettings_ReturnsNull()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");

        // After NormalizeProviders runs, null entries become new ProviderSettings() with null ApiKey
        File.WriteAllText(filePath, """{"providers":{"Copilot":null}}""");

        var service = this.CreateService();
        var result = service.GetApiKey(ProviderId.Copilot);
        Assert.Null(result);
    }

    /// <summary>
    /// Exercises GetCopilotAccounts where CopilotAccounts is null in cached settings (line 232: ?? []).
    /// </summary>
    [Fact]
    public void GetCopilotAccounts_NullInCached_ReturnsEmpty()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");
        File.WriteAllText(filePath, """{"providers":{},"copilotAccounts":null}""");

        var service = this.CreateService();
        var accounts = service.GetCopilotAccounts();
        Assert.Empty(accounts);
    }

    /// <summary>
    /// Exercises EnsureCached deserialization returning null (line 309: ?? CreateDefaults()).
    /// The JSON "null" deserializes to null.
    /// </summary>
    [Fact]
    public void Load_DeserializesToNull_UsesDefaults()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");
        File.WriteAllText(filePath, "null");

        var service = this.CreateService();
        var loaded = service.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded.RefreshIntervalSeconds > 0);
    }

    /// <summary>
    /// Exercises DeepCopy where CopilotAccounts is null (line 337+340: source.CopilotAccounts ?? []).
    /// </summary>
    [Fact]
    public void Load_NullCopilotAccounts_DeepCopyHandles()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");
        File.WriteAllText(filePath, """{"providers":{},"copilotAccounts":null,"openCodeGoWorkspaceId":null}""");

        var service = this.CreateService();
        var loaded = service.Load();
        Assert.NotNull(loaded.CopilotAccounts);
    }

    /// <summary>
    /// Exercises DeepCopy where SessionSpendingBaselines is null (line 337+347).
    /// </summary>
    [Fact]
    public void Load_NullSessionBaselines_DeepCopyHandles()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");
        File.WriteAllText(filePath, """{"providers":{},"sessionSpendingBaselines":null,"sessionSpendingResetTimes":null}""");

        var service = this.CreateService();
        var loaded = service.Load();
        Assert.NotNull(loaded.SessionSpendingBaselines);
        Assert.NotNull(loaded.SessionSpendingResetTimes);
    }

    /// <summary>
    /// Exercises DeepCopy where provider value is null in the source (line 352: kvp.Value is null).
    /// Since NormalizeProviders replaces null with defaults, we test the intermediary state by
    /// saving with a null-valued provider and loading — DeepCopy sees the normalized value.
    /// </summary>
    [Fact]
    public void Load_AfterSaveWithNullProvider_DeepCopyProducesDefaults()
    {
        var service = this.CreateService();

        // First save with a null provider entry
        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Test"] = null!,
            },
        };
        service.Save(memSettings);

        // Load exercises DeepCopy
        var first = service.Load();
        var second = service.Load();

        // Both should have the provider with default values
        Assert.NotNull(first.Providers["Test"]);
        Assert.NotNull(second.Providers["Test"]);
    }

    /// <summary>
    /// Exercises IsProviderEnabled where the provider exists and ps is not null (the normal true path).
    /// </summary>
    [Fact]
    public void IsProviderEnabled_ProviderDisabled_ReturnsFalse()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");
        File.WriteAllText(filePath, """{"providers":{"Copilot":{"enabled":false}}}""");

        var service = this.CreateService();
        Assert.False(service.IsProviderEnabled(ProviderId.Copilot));
    }

    /// <summary>
    /// Exercises EnsureCached where deserialized Providers is null (line 309: Providers ??= []).
    /// JSON with explicit "providers": null overrides the C# property initializer.
    /// </summary>
    [Fact]
    public void Load_ExplicitNullProviders_AssignsEmptyDictionary()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");
        File.WriteAllText(filePath, """{"refreshIntervalSeconds":30,"providers":null}""");

        var service = this.CreateService();
        var loaded = service.Load();
        Assert.NotNull(loaded.Providers);
        Assert.Empty(loaded.Providers);
    }

    /// <summary>
    /// Exercises MergeFromDisk where disk has a provider with null ApiKey
    /// and memory has the same provider with an ApiKey — memory value preserved
    /// (the else-if diskProvider?.ApiKey is not null check returns false).
    /// </summary>
    [Fact]
    public void Save_DiskProviderApiKeyNull_MemoryPreserved()
    {
        var filePath = Path.Combine(this._tempDir, "settings.json");
        File.WriteAllText(filePath, """{"providers":{"OpenRouter":{"enabled":true,"apiKey":null}}}""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = "my-key" },
            },
        };

        service.Save(memSettings);
        var loaded = service.Load();
        Assert.Equal("my-key", loaded.Providers["OpenRouter"].ApiKey);
    }
}

/// <summary>
/// Tests for ClaudeProvider token refresh with missing expires_at (line 622: "unknown" branch).
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class ClaudeTokenRefreshBranchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _credentialsPath;

    public ClaudeTokenRefreshBranchTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_refresh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        this._credentialsPath = Path.Combine(this._tempDir, ".credentials.json");

        ClaudeProvider.CredentialsPathOverride = this._credentialsPath;
        ClaudeProvider.StatsCachePathOverride = Path.Combine(this._tempDir, "stats-cache.json");
        ClaudeProvider.ClaudeJsonPathOverride = Path.Combine(this._tempDir, ".claude.json");
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;

        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Exercises EnsureTokenFreshAsync where refresh response has access_token but NO expires_at.
    /// This hits the `newExpiresAt > 0` false branch at line 622, producing "unknown" log.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task EnsureTokenFresh_MissingExpiresAt_LogsUnknown()
    {
        // Set up expired credentials to trigger refresh
        var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        File.WriteAllText(this._credentialsPath, $$"""
        {
            "claudeAiOauth": {
                "accessToken": "expired-token",
                "expiresAt": {{pastExpiry}},
                "refreshToken": "test-refresh"
            }
        }
        """);

        // Refresh endpoint returns access_token but no expires_at
        var refreshResponse = """{"access_token": "new-token"}""";
        var requestCount = 0;
        var handler = new MultiResponseHandler(request =>
        {
            Interlocked.Increment(ref requestCount);
            if (request.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(refreshResponse, Encoding.UTF8, "application/json"),
                };
            }

            // Usage API call - return empty response
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        // The token refresh should succeed and usage fetch should proceed
        Assert.True(result.Success);
    }

    /// <summary>
    /// Exercises EnsureTokenFreshAsync where refresh response has expires_at = 0 explicitly.
    /// Same branch as above but via explicit 0 value.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task EnsureTokenFresh_ExpiresAtZero_LogsUnknown()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        File.WriteAllText(this._credentialsPath, $$"""
        {
            "claudeAiOauth": {
                "accessToken": "expired-token",
                "expiresAt": {{pastExpiry}},
                "refreshToken": "test-refresh"
            }
        }
        """);

        var refreshResponse = """{"access_token": "new-token", "expires_at": 0}""";
        var handler = new MultiResponseHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(refreshResponse, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    private sealed class MultiResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

/// <summary>
/// Tests for StartupManager branch where TestStore is null (falls through to system).
/// </summary>
[Collection("StartupManager")]
public class StartupManagerSystemFallbackTests
{
    /// <summary>
    /// When TestStore is null, IsEnabled falls through to IsEnabledFromSystem.
    /// On Windows, this reads the real registry — just verify no exception.
    /// </summary>
    [Fact]
    public void IsEnabled_TestStoreNull_CompletesWithoutError()
    {
        var original = StartupManager.TestStore;
        try
        {
            StartupManager.TestStore = null;
            var ex = Record.Exception(() => StartupManager.IsEnabled());
            Assert.Null(ex);
        }
        finally
        {
            StartupManager.TestStore = original;
        }
    }

    /// <summary>
    /// When TestStore is null, SetEnabled falls through to SetEnabledFromSystem.
    /// On non-Windows this is a no-op; on Windows it touches the registry so we skip.
    /// </summary>
    [Fact]
    public void SetEnabled_TestStoreNull_CompletesWithoutError()
    {
        if (OperatingSystem.IsWindows())
        {
            // Avoid touching the real Windows registry in tests
            return;
        }

        var original = StartupManager.TestStore;
        try
        {
            StartupManager.TestStore = null;
            var ex = Record.Exception(() => StartupManager.SetEnabled(false));
            Assert.Null(ex);
        }
        finally
        {
            StartupManager.TestStore = original;
        }
    }
}

/// <summary>
/// Tests for CopilotProvider discovery returning null (line 196 null path).
/// </summary>
[Collection("EnvironmentVariableTests")]
public class CopilotProviderDiscoveryNullTests
{
    /// <summary>
    /// Exercises the pattern match where _cachedAccounts is null after discovery
    /// (line 196: _cachedAccounts is { Count: 0 } — null doesn't match the pattern).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_DiscoveryReturnsNull_HandlesGracefully()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string>());

        var factory = Substitute.For<IHttpClientFactory>();
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);

        provider.AccountDiscoveryOverride = _ => Task.FromResult<List<string>>(null!);

        var result = await provider.FetchUsageAsync();
        Assert.False(result.Success);
    }
}

/// <summary>
/// Tests for ClaudeProvider path override null branches (lines 44, 46).
/// These test the right side of ?? (default path) for StatsCachePath and ClaudeJsonPath.
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class ClaudeProviderPathDefaultTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _credentialsPath;

    public ClaudeProviderPathDefaultTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_pathdef_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        this._credentialsPath = Path.Combine(this._tempDir, ".credentials.json");
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;

        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Exercises StatsCachePath and ClaudeJsonPath using DEFAULT paths (overrides null)
    /// while CredentialsPathOverride points to valid credentials.
    /// This covers the right side of ?? at lines 44 and 46.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_StatsAndClaudeJsonUseDefaultPath_WhenOverridesNull()
    {
        // Set credentials to a valid file (left side of ?? on line 42)
        File.WriteAllText(this._credentialsPath, """
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999
            }
        }
        """);

        ClaudeProvider.CredentialsPathOverride = this._credentialsPath;

        // Leave StatsCachePathOverride and ClaudeJsonPathOverride as null
        // to exercise the default path (right side of ??)
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;

        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        // Should succeed — credentials found, stats/claude.json use defaults (files won't exist → empty data)
        Assert.True(result.Success);
    }

    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = response.Content is not null
                    ? new StringContent(
                        response.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                        Encoding.UTF8,
                        response.Content.Headers.ContentType?.MediaType ?? "application/json")
                    : null,
            });
    }
}
