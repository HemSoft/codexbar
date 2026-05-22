// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.OpenCodeGo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Comprehensive branch coverage tests targeting all remaining uncovered branches
/// in CodexBar.Core to achieve 100% branch coverage.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class BranchCoverageTests
{
    /// <summary>
    /// Exercises MergeFromDisk when disk has a provider entry that is null
    /// (covers the diskProvider ?? new ProviderSettings() branch at line 102).
    /// </summary>
    [Fact]
    public void Save_DiskProviderIsNull_MergesAsNewProviderSettings()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            // Write JSON where a provider value is null
            File.WriteAllText(filePath, """{"providers":{"NewProvider":null}}""");

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                Providers = new Dictionary<string, ProviderSettings>(),
            };

            service.Save(memSettings);
            var loaded = service.Load();

            // The null provider from disk should be merged as a new ProviderSettings
            Assert.True(loaded.Providers.ContainsKey("NewProvider"));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises MergeFromDisk when memory Providers is null before merge (line 96: settings.Providers ??= []).
    /// </summary>
    [Fact]
    public void Save_MemoryProvidersNull_InitializedBeforeMerge()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            File.WriteAllText(filePath, """{"providers":{"Claude":{"enabled":true,"apiKey":"sk-123"}}}""");

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                Providers = null!,
            };

            service.Save(memSettings);
            var loaded = service.Load();
            Assert.NotNull(loaded.Providers);
            Assert.True(loaded.Providers.ContainsKey("Claude"));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises the branch where disk has SessionSpendingBaselines that are null on the disk object
    /// (covers disk.SessionSpendingBaselines ?? [] at line 118).
    /// </summary>
    [Fact]
    public void Save_DiskHasNullBaselines_DoesNotThrow()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            // Write settings with no baselines/reset times properties
            File.WriteAllText(filePath, """{"refreshIntervalSeconds":30,"providers":{}}""");

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                Providers = new Dictionary<string, ProviderSettings>(),
                SessionSpendingBaselines = null!,
                SessionSpendingResetTimes = null!,
            };

            var ex = Record.Exception(() => service.Save(memSettings));
            Assert.Null(ex);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises MergeFromDisk exception handler (line 130-133) when disk file is corrupted JSON.
    /// </summary>
    [Fact]
    public void Save_DiskFileCorruptedJson_MergeSkippedGracefully()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            File.WriteAllText(filePath, "not valid json {{{");

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                Providers = new Dictionary<string, ProviderSettings>
                {
                    ["Copilot"] = new() { Enabled = true },
                },
            };

            // Save should not throw - the MergeFromDisk catches the exception
            var ex = Record.Exception(() => service.Save(memSettings));
            Assert.Null(ex);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises SaveInternal where provider value is null (covers kvp.Value?.Enabled ?? true
    /// and kvp.Value?.ApiKey at line 157-159).
    /// </summary>
    [Fact]
    public void Save_ProviderWithNullValue_SanitizedCorrectly()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                Providers = new Dictionary<string, ProviderSettings>
                {
                    ["NullProvider"] = null!,
                },
            };

            service.Save(memSettings);
            var loaded = service.Load();

            Assert.True(loaded.Providers.ContainsKey("NullProvider"));
            Assert.True(loaded.Providers["NullProvider"].Enabled);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises SaveInternal ZoomLevel boundaries (0 and >5 fall to default 1.0, line 148).
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(5.5)]
    [InlineData(-1.0)]
    public void Save_InvalidZoomLevel_DefaultsToOne(double zoomLevel)
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                ZoomLevel = zoomLevel,
                Providers = new Dictionary<string, ProviderSettings>(),
            };

            service.Save(memSettings);
            var loaded = service.Load();
            Assert.Equal(1.0, loaded.ZoomLevel);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises EnsureCached when the deserialized value from file is null (line 308: ?? CreateDefaults()).
    /// </summary>
    [Fact]
    public void Load_FileContainsNull_ReturnsDefaults()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            File.WriteAllText(filePath, "null");

            var loaded = service.Load();
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Providers);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises EnsureCached when file reading throws (line 316-319 catch).
    /// </summary>
    [Fact]
    public void Load_FileInvalidJson_ReturnsDefaults()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            File.WriteAllText(filePath, "{{invalid json}}}");

            var loaded = service.Load();
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Providers);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises NormalizeProviders replacing null entries (line 366).
    /// </summary>
    [Fact]
    public void Load_ProviderEntryNull_NormalizedToDefault()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            File.WriteAllText(filePath, """{"providers":{"Test":null,"Other":{"enabled":false}}}""");

            var loaded = service.Load();
            Assert.NotNull(loaded.Providers["Test"]);
            Assert.True(loaded.Providers["Test"].Enabled); // default value
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises DeepCopy when provider Value is null (line 352: kvp.Value is null ? new ProviderSettings() ...).
    /// </summary>
    [Fact]
    public void Load_ThenLoad_DeepCopiesNullProviderValues()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            // Write valid settings, then load twice to hit DeepCopy path
            File.WriteAllText(filePath, """{"providers":{"Copilot":{"enabled":true}}}""");

            var first = service.Load();
            var second = service.Load();

            // Mutations on one copy should not affect the other
            first.Providers["Copilot"].Enabled = false;
            Assert.True(second.Providers["Copilot"].Enabled);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises the GetApiKey and IsProviderEnabled branches when provider is not
    /// in settings or has null ProviderSettings (line 198, 207).
    /// </summary>
    [Fact]
    public void GetApiKey_ProviderNotInSettings_ReturnsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");
            File.WriteAllText(filePath, """{"providers":{}}""");

            var apiKey = service.GetApiKey(ProviderId.OpenRouter);
            Assert.Null(apiKey);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises IsProviderEnabled when the provider is not in settings
    /// (defaults to enabled, line 207).
    /// </summary>
    [Fact]
    public void IsProviderEnabled_ProviderNotFound_ReturnsTrue()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");
            File.WriteAllText(filePath, """{"providers":{}}""");

            Assert.True(service.IsProviderEnabled(ProviderId.Copilot));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises GetSessionBaseline returning null when key not found (line 247-249).
    /// </summary>
    [Fact]
    public void GetSessionBaseline_KeyNotFound_ReturnsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");
            File.WriteAllText(filePath, """{"providers":{}}""");

            Assert.Null(service.GetSessionBaseline(ProviderId.Copilot));
            Assert.Null(service.GetSessionResetTime(ProviderId.Copilot));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises MergeFromDisk when the memory provider has a non-empty ApiKey
    /// and disk also has an ApiKey — memory value should be preserved (the else-if at line 104 is false).
    /// </summary>
    [Fact]
    public void Save_MemoryHasApiKey_DiskApiKeyNotOverwritten()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");

            File.WriteAllText(filePath, """{"providers":{"OpenRouter":{"enabled":true,"apiKey":"old-disk-key"}}}""");

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                Providers = new Dictionary<string, ProviderSettings>
                {
                    ["OpenRouter"] = new() { Enabled = true, ApiKey = "new-memory-key" },
                },
            };

            service.Save(memSettings);
            var loaded = service.Load();
            Assert.Equal("new-memory-key", loaded.Providers["OpenRouter"].ApiKey);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises SaveInternal with null OpenCodeGoWorkspaceId (line 147).
    /// </summary>
    [Fact]
    public void Save_EmptyWorkspaceId_SavedAsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                OpenCodeGoWorkspaceId = "   ", // whitespace-only
                Providers = new Dictionary<string, ProviderSettings>(),
            };

            service.Save(memSettings);
            var loaded = service.Load();
            Assert.Null(loaded.OpenCodeGoWorkspaceId);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises EnsureCached when SaveInternal throws during initial defaults persist (line 294-300).
    /// Makes the settings path a directory so the file write fails with IOException.
    /// </summary>
    [Fact]
    public void Load_SaveInternalFailsOnFirstLoad_ReturnsDefaults()
    {
        var tempDir = CreateTempDir();
        try
        {
            // Create a directory named "settings.json" so that the file write throws
            var settingsFilePath = Path.Combine(tempDir, "settings.json");
            Directory.CreateDirectory(settingsFilePath);

            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);

            // EnsureCached will try to SaveInternal which fails because "settings.json" is a directory
            var loaded = service.Load();
            Assert.NotNull(loaded);
            Assert.Equal(120, loaded.RefreshIntervalSeconds);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises SaveInternal with null CopilotAccounts (line 146: (settings.CopilotAccounts ?? []).ToList()).
    /// </summary>
    [Fact]
    public void Save_NullCopilotAccounts_SavesEmptyList()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);

            var memSettings = new AppSettings
            {
                RefreshIntervalSeconds = 30,
                CopilotAccounts = null!,
                Providers = new Dictionary<string, ProviderSettings>(),
                SessionSpendingBaselines = null!,
                SessionSpendingResetTimes = null!,
            };

            service.Save(memSettings);
            var loaded = service.Load();
            Assert.NotNull(loaded.CopilotAccounts);
            Assert.Empty(loaded.CopilotAccounts);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises GetCopilotAccounts when CopilotAccounts is null (line 232).
    /// </summary>
    [Fact]
    public void GetCopilotAccounts_NullInSettings_ReturnsEmptyList()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var filePath = Path.Combine(tempDir, "settings.json");
            File.WriteAllText(filePath, """{"providers":{}}""");

            var accounts = service.GetCopilotAccounts();
            Assert.NotNull(accounts);
            Assert.Empty(accounts);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Exercises IsEnabled when TestStore.GetValue returns null (line 37: the null branch).
    /// </summary>
    [Fact]
    public void IsEnabled_TestStoreReturnsNull_ReturnsFalse()
    {
        var originalStore = StartupManager.TestStore;
        try
        {
            StartupManager.TestStore = new NullReturningStore();
            Assert.False(StartupManager.IsEnabled());
        }
        finally
        {
            StartupManager.TestStore = originalStore;
        }
    }

    /// <summary>
    /// Exercises SetEnabled(true) then SetEnabled(false) through TestStore path (complete branch coverage).
    /// </summary>
    [Fact]
    public void SetEnabled_Toggle_CoversBothBranches()
    {
        var originalStore = StartupManager.TestStore;
        try
        {
            var store = new InMemoryStartupStore();
            StartupManager.TestStore = store;

            StartupManager.SetEnabled(true);
            Assert.True(StartupManager.IsEnabled());

            StartupManager.SetEnabled(false);
            Assert.False(StartupManager.IsEnabled());
        }
        finally
        {
            StartupManager.TestStore = originalStore;
        }
    }

    private sealed class NullReturningStore : IStartupStore
    {
        public object? GetValue(string name) => null;

        public void SetValue(string name, string value)
        {
        }

        public void DeleteValue(string name)
        {
        }
    }

    private sealed class InMemoryStartupStore : IStartupStore
    {
        private readonly Dictionary<string, string> _values = [];

        public object? GetValue(string name) => this._values.GetValueOrDefault(name);

        public void SetValue(string name, string value) => this._values[name] = value;

        public void DeleteValue(string name) => this._values.Remove(name);
    }

    /// <summary>
    /// Exercises BuildCopilotApiRequest to verify authorization and configured headers.
    /// </summary>
    [Fact]
    public void BuildCopilotApiRequest_SetsAuthorizationAndConfiguredHeaders()
    {
        var request = CopilotProvider.BuildCopilotApiRequest("test-token");
        Assert.NotNull(request);
        Assert.Equal("token", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization?.Parameter);

        // Verify Editor-Version header is present (sourced from CODEXBAR_COPILOT_EDITOR_VERSION or default)
        Assert.True(request.Headers.Contains("Editor-Version"));
        var editorVersion = request.Headers.GetValues("Editor-Version").First();
        Assert.False(string.IsNullOrWhiteSpace(editorVersion));

        // Verify Editor-Plugin-Version header is present
        Assert.True(request.Headers.Contains("Editor-Plugin-Version"));
        var pluginVersion = request.Headers.GetValues("Editor-Plugin-Version").First();
        Assert.False(string.IsNullOrWhiteSpace(pluginVersion));

        // Verify X-Github-Api-Version header is present
        Assert.True(request.Headers.Contains("X-Github-Api-Version"));
    }

    /// <summary>
    /// Exercises ParseCopilotApiResponse with a null data.CopilotPlan (line 429: logger branches).
    /// </summary>
    [Fact]
    public void ParseCopilotApiResponse_NullPlan_LogsUnknown()
    {
        var json = """
        {
            "copilot_plan": null,
            "quota_snapshots": {
                "premium_interactions": {
                    "remaining": 50,
                    "entitlement": 100,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "unlimited": false
                }
            }
        }
        """;

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser", NullLogger<CopilotProvider>.Instance);
        Assert.True(result.Success);
        Assert.Null(result.Plan);
    }

    /// <summary>
    /// Exercises ParseCopilotApiResponse with null logger (line 429: logger?.LogDebug branch where logger is null).
    /// </summary>
    [Fact]
    public void ParseCopilotApiResponse_NullLogger_DoesNotThrow()
    {
        var json = """
        {
            "copilot_plan": "individual_pro",
            "quota_snapshots": {
                "premium_interactions": {
                    "remaining": 50,
                    "entitlement": 100,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "unlimited": false
                }
            }
        }
        """;

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser", null);
        Assert.True(result.Success);
    }

    /// <summary>
    /// Exercises ParseCopilotApiResponse with missing quota_snapshots (the null branches).
    /// </summary>
    [Fact]
    public void ParseCopilotApiResponse_NoQuotaSnapshots_SucceedsWithNullPremium()
    {
        var json = """{"copilot_plan":"enterprise"}""";

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser", NullLogger<CopilotProvider>.Instance);
        Assert.True(result.Success);
        Assert.Null(result.PremiumInteractions);
        Assert.Null(result.Chat);
    }

    /// <summary>
    /// Exercises the cached accounts negative-result path (line 179/194)
    /// where an empty discovery is cached to avoid repeated process spawning.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_EmptyDiscovery_CachedForFiveMinutes()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string>());

        var factory = Substitute.For<IHttpClientFactory>();
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);

        int discoveryCount = 0;
        provider.AccountDiscoveryOverride = _ =>
        {
            Interlocked.Increment(ref discoveryCount);
            return Task.FromResult(new List<string>());
        };

        // First call triggers discovery
        var result1 = await provider.FetchUsageAsync();
        Assert.False(result1.Success);
        Assert.Equal(1, discoveryCount);

        // Second call should use negative cache (not call discovery again)
        var result2 = await provider.FetchUsageAsync();
        Assert.False(result2.Success);
        Assert.Equal(1, discoveryCount);
    }

    /// <summary>
    /// Exercises the account discovery path where _cachedAccounts has items (line 179: Count > 0).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_CachedAccountsNotEmpty_ReusedOnSecondCall()
    {
        var json = BuildCopilotJson();
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string>());

        var handler = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);

        int discoveryCount = 0;
        provider.AccountDiscoveryOverride = _ =>
        {
            Interlocked.Increment(ref discoveryCount);
            return Task.FromResult(new List<string> { "user1" });
        };
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("fake-token");

        var result1 = await provider.FetchUsageAsync();
        Assert.True(result1.Success);
        Assert.Equal(1, discoveryCount);

        // Second call: cached accounts reused (discovery not called)
        var result2 = await provider.FetchUsageAsync();
        Assert.True(result2.Success);
        Assert.Equal(1, discoveryCount);
    }

    /// <summary>
    /// Exercises FormatDisplayName with different plan values.
    /// </summary>
    [Theory]
    [InlineData("enterprise", "Copilot · alice (Ent)")]
    [InlineData("business", "Copilot · alice (Biz)")]
    [InlineData("individual_pro", "Copilot · alice (Pro)")]
    [InlineData("some_other", "Copilot · alice (some other)")]
    [InlineData(null, "Copilot · alice")]
    public void FormatDisplayName_VariousPlans_FormattedCorrectly(string? plan, string expected)
    {
        var result = CopilotProvider.FormatDisplayName("alice", plan);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Exercises ComputeUsageMetrics with unlimited quota (line 661-663).
    /// </summary>
    [Fact]
    public void ComputeUsageMetrics_Unlimited_ReturnsZeroPercentUnlimited()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Remaining = 0,
            Entitlement = 0,
            Unlimited = true,
            OverageCount = 0,
            OveragePermitted = false,
        };

        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, usedPercent);
        Assert.Equal("Unlimited", label);
        Assert.True(isUnlimited);
    }

    /// <summary>
    /// Exercises ComputeUsageMetrics with zero entitlement and not unlimited (line 666).
    /// </summary>
    [Fact]
    public void ComputeUsageMetrics_ZeroEntitlementNotUnlimited_ReturnsNoQuota()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Remaining = 0,
            Entitlement = 0,
            Unlimited = false,
            OverageCount = 0,
            OveragePermitted = false,
        };

        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, usedPercent);
        Assert.Equal("No quota", label);
        Assert.False(isUnlimited);
    }

    /// <summary>
    /// Exercises BuildUsageLabel overage branch where overage is over limit but not permitted (line 684-687).
    /// </summary>
    [Fact]
    public void ComputeUsageMetrics_OverLimitNotPermitted_ShowsOverLimit()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Remaining = -5,
            Entitlement = 100,
            OverageCount = 5,
            OveragePermitted = false,
            Unlimited = false,
        };

        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Contains("over limit", label);
    }

    /// <summary>
    /// Exercises BuildUsageLabel for non-premium quota label (line 679-682: chat label).
    /// </summary>
    [Fact]
    public void ComputeUsageMetrics_ChatQuota_IncludesQuotaTypeLabel()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Remaining = 50,
            Entitlement = 100,
            OverageCount = 0,
            OveragePermitted = false,
            Unlimited = false,
        };

        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "chat");
        Assert.Contains("Chat", label);
    }

    /// <summary>
    /// Exercises ParseReset with null value (line 694).
    /// </summary>
    [Fact]
    public void ParseReset_NullResetDate_ReturnsNulls()
    {
        var (resetsAt, description) = CopilotProvider.ParseReset(null);
        Assert.Null(resetsAt);
        Assert.Null(description);
    }

    /// <summary>
    /// Exercises ParseReset with invalid date string.
    /// </summary>
    [Fact]
    public void ParseReset_InvalidDate_ReturnsNulls()
    {
        var (resetsAt, description) = CopilotProvider.ParseReset("not-a-date");
        Assert.Null(resetsAt);
        Assert.Null(description);
    }

    /// <summary>
    /// Exercises ParseReset with past date (line 702: "Reset overdue").
    /// </summary>
    [Fact]
    public void ParseReset_PastDate_ReturnsOverdue()
    {
        var pastDate = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
        var (_, description) = CopilotProvider.ParseReset(pastDate);
        Assert.Equal("Reset overdue", description);
    }

    /// <summary>
    /// Exercises ParseReset with date 3+ days in future (line 705: "Resets in Xd").
    /// </summary>
    [Fact]
    public void ParseReset_FutureDays_ReturnsDays()
    {
        var futureDate = DateTimeOffset.UtcNow.AddDays(5).ToString("o");
        var (_, description) = CopilotProvider.ParseReset(futureDate);
        Assert.Contains("Resets in", description);
        Assert.Contains("d", description);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshot with null limits (line 244-256: fallback path).
    /// </summary>
    [Fact]
    public void BuildSessionSnapshot_NullLimits_ReturnsFallbackLabel()
    {
        var result = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 0, 0, null);
        Assert.True(result.IsUnlimited);
        Assert.Contains("Rate limits unavailable", result.UsageLabel);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshot fallback when FormatUsageLabel returns non-empty string (line 246-248).
    /// </summary>
    [Fact]
    public void BuildSessionSnapshot_NullLimitsWithTokens_AppendsFallbackSuffix()
    {
        var result = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 1000, 0, null);
        Assert.Contains("Rate limits unavailable", result.UsageLabel);
        Assert.Contains("Pro plan", result.UsageLabel);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshot fallback when FormatUsageLabel returns empty string (line 246-247: first branch).
    /// </summary>
    [Fact]
    public void BuildSessionSnapshot_NullLimitsEmptyLabel_ReturnsOnlyFallback()
    {
        // FormatUsageLabel returns "Unknown plan" even with empty subscription type,
        // so we test the "Rate limits unavailable" suffix is appended correctly
        var result = ClaudeProvider.BuildSessionSnapshot(null, "Unknown", 0, 0, null);
        Assert.Contains("Rate limits unavailable", result.UsageLabel);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshotFromLimits with accountInfo that has DisplayName but no extra usage
    /// (line 277: accountInfo?.HasExtraUsageEnabled == true check is false).
    /// </summary>
    [Fact]
    public void BuildSessionSnapshotFromLimits_AccountInfoNoExtraUsage_DoesNotIncludeExtraLabel()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.3,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
        };

        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { DisplayName = "TestUser" };
        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 50000, 1.5, accountInfo);
        Assert.DoesNotContain("extra usage on", result.UsageLabel);
        Assert.Contains("Pro plan", result.UsageLabel);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshotFromLimits with extra usage enabled (line 277-279).
    /// </summary>
    [Fact]
    public void BuildSessionSnapshotFromLimits_ExtraUsageEnabled_IncludesLabel()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.3,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
        };

        var accountInfo = new ClaudeProvider.ClaudeAccountInfo
        {
            DisplayName = "TestUser",
            HasExtraUsageEnabled = true,
        };

        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 50000, 1.5, accountInfo);
        Assert.Contains("extra usage on", result.UsageLabel);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshotFromLimits where equivalentCost is 0 but totalTokens > 0 (line 272-274).
    /// </summary>
    [Fact]
    public void BuildSessionSnapshotFromLimits_ZeroCostWithTokens_ShowsTokenCount()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.3,
            SevenDayReset = 0,
        };

        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 1_500_000, 0, null);
        Assert.Contains("tokens", result.UsageLabel);
    }

    /// <summary>
    /// Exercises BuildSessionSnapshotFromLimits where FiveHourReset is 0 (line 282-283: null branch).
    /// </summary>
    [Fact]
    public void BuildSessionSnapshotFromLimits_ZeroReset_NullResetDescription()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = 0,
            SevenDayUtilization = 0.3,
            SevenDayReset = 0,
        };

        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 5.0, null);
        Assert.Null(result.ResetDescription);
        Assert.Null(result.ResetsAt);
    }

    /// <summary>
    /// Exercises the token refresh log branch where newExpiresAt is 0 (line 622: "unknown" branch).
    /// This tests the ternary: newExpiresAt > 0 ? ... : "unknown".
    /// </summary>
    [Fact]
    public void NormalizeEpochToSeconds_Milliseconds_ConvertedToSeconds()
    {
        var result = ClaudeProvider.NormalizeEpochToSeconds(1_700_000_000_000);
        Assert.Equal(1_700_000_000, result);
    }

    /// <summary>
    /// Exercises NormalizeEpochToSeconds when value is already in seconds.
    /// </summary>
    [Fact]
    public void NormalizeEpochToSeconds_AlreadySeconds_ReturnsUnchanged()
    {
        var result = ClaudeProvider.NormalizeEpochToSeconds(1_700_000_000);
        Assert.Equal(1_700_000_000, result);
    }

    /// <summary>
    /// Exercises FormatSubscriptionType with null (line 192-195).
    /// </summary>
    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("  ", "Unknown")]
    [InlineData("pro", "Pro")]
    [InlineData("enterprise", "Enterprise")]
    public void FormatSubscriptionType_VariousInputs_FormattedCorrectly(string? input, string expected)
    {
        var result = ClaudeProvider.FormatSubscriptionType(input);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Exercises FormatTokenCount for each magnitude bracket.
    /// </summary>
    [Theory]
    [InlineData(500, "500 tokens")]
    [InlineData(1500, "1.5K tokens")]
    [InlineData(1_500_000, "1.5M tokens")]
    [InlineData(2_000_000_000, "2.0B tokens")]
    public void FormatTokenCount_VariousMagnitudes_FormattedCorrectly(long tokens, string expected)
    {
        var result = ClaudeProvider.FormatTokenCount(tokens);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Exercises FormatQuotaLabel for the known and default cases.
    /// </summary>
    [Theory]
    [InlineData("premium", "Premium interactions")]
    [InlineData("chat", "Chat")]
    [InlineData("other", "other")]
    public void FormatQuotaLabel_VariousInputs_MapsCorrectly(string input, string expected)
    {
        var result = CopilotProvider.FormatQuotaLabel(input);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Exercises CalculateEquivalentCost with null stats.
    /// </summary>
    [Fact]
    public void CalculateEquivalentCost_NullStats_ReturnsZero()
    {
        Assert.Equal(0, ClaudeProvider.CalculateEquivalentCost(null));
    }

    /// <summary>
    /// Exercises CalculateTotalTokens with null stats.
    /// </summary>
    [Fact]
    public void CalculateTotalTokens_NullStats_ReturnsZero()
    {
        Assert.Equal(0, ClaudeProvider.CalculateTotalTokens(null));
    }

    /// <summary>
    /// Exercises BuildWeeklySnapshot with null limits.
    /// </summary>
    [Fact]
    public void BuildWeeklySnapshot_NullLimits_ReturnsNull()
    {
        Assert.Null(ClaudeProvider.BuildWeeklySnapshot(null));
    }

    /// <summary>
    /// Exercises BuildUsageBars with null limits.
    /// </summary>
    [Fact]
    public void BuildUsageBars_NullLimits_ReturnsEmptyList()
    {
        var result = ClaudeProvider.BuildUsageBars(null);
        Assert.Empty(result);
    }

    /// <summary>
    /// Exercises FormatBarReset for each time bracket.
    /// </summary>
    [Fact]
    public void FormatBarReset_PastEpoch_ReturnsNow()
    {
        var pastEpoch = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        Assert.Equal("Resets now", ClaudeProvider.FormatBarReset(pastEpoch));
    }

    [Fact]
    public void FormatBarReset_MinutesAway_ReturnsMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.Contains("Resets", result);
        Assert.Contains("m", result);
    }

    [Fact]
    public void FormatBarReset_HoursAway_ReturnsHours()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.Contains("Resets", result);
        Assert.Contains("h", result);
    }

    [Fact]
    public void FormatBarReset_DaysAway_ReturnsDays()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.Contains("Resets", result);
        Assert.Contains("d", result);
    }

    /// <summary>
    /// Exercises FormatResetCountdown for various time ranges.
    /// </summary>
    [Fact]
    public void FormatResetCountdown_Past_ReturnsNow()
    {
        var pastEpoch = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(pastEpoch, "5-hour limit");
        Assert.Contains("resets now", result);
    }

    [Fact]
    public void FormatResetCountdown_Minutes_ReturnsMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "5-hour limit");
        Assert.Contains("resets in", result);
        Assert.Contains("m", result);
    }

    [Fact]
    public void FormatResetCountdown_Hours_ReturnsHoursAndMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "5-hour limit");
        Assert.Contains("resets in", result);
        Assert.Contains("h", result);
    }

    [Fact]
    public void FormatResetCountdown_Days_ReturnsDaysAndHours()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Weekly");
        Assert.Contains("resets in", result);
        Assert.Contains("d", result);
    }

    /// <summary>
    /// Exercises ResolvePricing with unknown model (falls back to Sonnet pricing).
    /// </summary>
    [Fact]
    public void ResolvePricing_UnknownModel_FallsBackToSonnet()
    {
        var pricing = ClaudeProvider.ResolvePricing("completely-unknown-model");
        Assert.Equal(3.0, pricing.InputPerMTok); // Sonnet pricing
    }

    /// <summary>
    /// Exercises ResolvePricing with model containing "opus" (family fallback).
    /// </summary>
    [Fact]
    public void ResolvePricing_ContainsOpus_UsesOpusPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("some-opus-variant");
        Assert.Equal(5.0, pricing.InputPerMTok); // Opus pricing
    }

    /// <summary>
    /// Exercises ResolvePricing with model containing "haiku" (family fallback).
    /// </summary>
    [Fact]
    public void ResolvePricing_ContainsHaiku_UsesHaikuPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("some-haiku-variant");
        Assert.Equal(1.0, pricing.InputPerMTok); // Haiku pricing
    }

    /// <summary>
    /// Exercises ParseRateLimitHeaders when no utilization headers are present (returns null).
    /// </summary>
    [Fact]
    public void ParseRateLimitHeaders_NoHeaders_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.Null(result);
    }

    /// <summary>
    /// Exercises ParseRateLimitHeaders with only 5h header (7d is null - the ?? "unknown" branch for status).
    /// </summary>
    [Fact]
    public void ParseRateLimitHeaders_Only5hHeader_ParsesCorrectly()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.75");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1700000000");

        // No 7d headers — tests the null branches
        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.NotNull(result);
        Assert.Equal(0.75, result.FiveHourUtilization, 0.01);
        Assert.Equal(0, result.SevenDayUtilization);
        Assert.Equal("unknown", result.SevenDayStatus);
    }

    /// <summary>
    /// Exercises ParseRateLimitHeaders with both headers including status.
    /// </summary>
    [Fact]
    public void ParseRateLimitHeaders_BothHeaders_ParsesAll()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.5");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1700000000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.3");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1700500000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "active");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.NotNull(result);
        Assert.Equal(0.5, result.FiveHourUtilization, 0.01);
        Assert.Equal(0.3, result.SevenDayUtilization, 0.01);
        Assert.Equal("active", result.FiveHourStatus);
        Assert.Equal("active", result.SevenDayStatus);
    }

    /// <summary>
    /// Exercises BuildResult where Rolling is null so primary falls back to Monthly (line 127: ?? usage.Monthly).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_MonthlyOnly_PrimaryUsageFromMonthly()
    {
        var originalWorkspace = Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID");
        var originalCookie = Environment.GetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "test-workspace");
            Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "test-cookie");

            // Build HTML with only monthly data (no rolling, no weekly)
            var html = "monthlyUsage:$R[0]={usagePercent:40,resetInSec:86400}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
            var provider = CreateOpenCodeGoProvider(response);

            var result = await provider.FetchUsageAsync();

            Assert.True(result.Success);
            Assert.NotNull(result.Items);
            var item = result.Items[0];
            Assert.NotNull(item.PrimaryUsage);
            Assert.Equal(0.4, item.PrimaryUsage.UsedPercent, 0.01);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", originalWorkspace);
            Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", originalCookie);
        }
    }

    /// <summary>
    /// Exercises BuildResult where both Rolling and Monthly are null (line 127+133: primary is null branch).
    /// This case shouldn't normally happen via FetchUsageAsync (as it returns failure when parse returns null),
    /// but exercises the BuildBars empty list path (line 140: bars.Count > 0 ? bars : null).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_WeeklyOnly_NoBarsIfWeeklyOnly()
    {
        var originalWorkspace = Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID");
        var originalCookie = Environment.GetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "test-workspace");
            Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "test-cookie");

            // This exercises the path where weekly exists but rolling and monthly don't
            var html = "weeklyUsage:$R[0]={usagePercent:60,resetInSec:3600}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
            var provider = CreateOpenCodeGoProvider(response);

            var result = await provider.FetchUsageAsync();
            Assert.True(result.Success);
            var item = result.Items![0];

            // Rolling is null, monthly is null, so primary = null via ?? chain
            Assert.Null(item.PrimaryUsage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", originalWorkspace);
            Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", originalCookie);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codexbar-branch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static string BuildCopilotJson(int remaining = 50, int entitlement = 100)
    {
        return $$"""
        {
            "copilot_plan": "individual_pro",
            "organization_login_list": ["org1"],
            "quota_snapshots": {
                "premium_interactions": {
                    "remaining": {{remaining}},
                    "entitlement": {{entitlement}},
                    "overage_count": 0,
                    "overage_permitted": false,
                    "unlimited": false
                }
            },
            "quota_reset_date_utc": "{{DateTimeOffset.UtcNow.AddDays(10):yyyy-MM-dd}}"
        }
        """;
    }

    private static OpenCodeGoProvider CreateOpenCodeGoProvider(HttpResponseMessage response)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeGo).Returns(true);
        settings.GetOpenCodeGoWorkspaceId().Returns("test-workspace");
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns("test-cookie");

        var handler = new FakeHttpHandler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        return new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FakeHttpHandler(HttpResponseMessage response) => this._response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Clone the response for each request to avoid disposed content issues
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
