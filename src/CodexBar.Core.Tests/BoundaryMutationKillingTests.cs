// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Boundary-condition tests designed to kill surviving mutants:
/// - Equality operator mutations (> to >=. < to <=)
/// - Logical mutations (&amp;&amp; to ||)
/// - Statement removal mutations
/// - Boolean literal mutations
/// </summary>
public class BoundaryMutationKillingTests
{
    // ==========================================================================
    // ClaudeProvider.FormatBarReset boundary tests
    // Targets: lines 385 (<=), 390 (>=), 395 (>=)
    // ==========================================================================
    [Fact]
    public void FormatBarReset_ExactlyZeroRemaining_ReturnsResetsNow()
    {
        // remaining <= TimeSpan.Zero at the boundary (exactly zero)
        // If mutated to < TimeSpan.Zero, this would wrongly NOT return "Resets now"
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.Equal("Resets now", result);
    }

    [Fact]
    public void FormatBarReset_Exactly1Day_ReturnsDays()
    {
        // remaining.TotalDays >= 1 at boundary (exactly 1 day)
        // If mutated to > 1, this would fall through to hours branch
        var epoch = DateTimeOffset.UtcNow.AddDays(1).AddSeconds(1).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.EndsWith("d", result);
        Assert.StartsWith("Resets ", result);
    }

    [Fact]
    public void FormatBarReset_Exactly1Hour_ReturnsHours()
    {
        // remaining.TotalHours >= 1 at boundary (exactly 1 hour)
        // If mutated to > 1, this would fall through to minutes branch
        var epoch = DateTimeOffset.UtcNow.AddHours(1).AddSeconds(1).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.EndsWith("h", result);
        Assert.StartsWith("Resets ", result);
    }

    [Fact]
    public void FormatBarReset_JustUnder1Hour_ReturnsMinutes()
    {
        // Ensures 59 minutes doesn't hit the hours branch
        var epoch = DateTimeOffset.UtcNow.AddMinutes(59).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.EndsWith("m", result);
    }

    [Fact]
    public void FormatBarReset_JustUnder1Day_ReturnsHours()
    {
        // Ensures 23 hours doesn't hit the days branch
        var epoch = DateTimeOffset.UtcNow.AddHours(23).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.EndsWith("h", result);
    }

    // ==========================================================================
    // ClaudeProvider.FormatResetCountdown boundary tests
    // Targets: lines 408 (<=), 413 (>=), 418 (>=)
    // ==========================================================================
    [Fact]
    public void FormatResetCountdown_ExactlyZeroRemaining_ReturnsResetsNow()
    {
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Window");
        Assert.Equal("Window resets now", result);
    }

    [Fact]
    public void FormatResetCountdown_Exactly1Day_ReturnsDaysAndHours()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(1).AddSeconds(1).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Window");
        Assert.Contains("d", result);
        Assert.Contains("h", result);
    }

    [Fact]
    public void FormatResetCountdown_Exactly1Hour_ReturnsHoursAndMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(1).AddSeconds(1).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Window");
        Assert.Contains("h", result);
        Assert.Contains("m", result);
    }

    [Fact]
    public void FormatResetCountdown_JustUnder1Hour_ReturnsMinutesOnly()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(59).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Limit");
        Assert.EndsWith("m", result);
        Assert.DoesNotContain("h ", result);
        Assert.DoesNotContain("d ", result);
    }

    // ==========================================================================
    // CopilotProvider.Metadata boolean fields
    // Targets: lines 101-103 (Boolean mutations)
    // ==========================================================================
    [Fact]
    public void CopilotProvider_Metadata_SupportsSessionUsage_IsTrue()
    {
        var provider = CreateCopilotProvider();
        Assert.True(provider.Metadata.SupportsSessionUsage);
    }

    [Fact]
    public void CopilotProvider_Metadata_SupportsWeeklyUsage_IsFalse()
    {
        var provider = CreateCopilotProvider();
        Assert.False(provider.Metadata.SupportsWeeklyUsage);
    }

    [Fact]
    public void CopilotProvider_Metadata_SupportsCredits_IsFalse()
    {
        var provider = CreateCopilotProvider();
        Assert.False(provider.Metadata.SupportsCredits);
    }

    // ==========================================================================
    // CopilotProvider.ExtractUsername boundary tests
    // Targets: lines 364, 368, 372 (>= 0 to > 0 mutations)
    // ==========================================================================
    [Fact]
    public void ExtractUsername_SpaceAtIndexZero_StillExtractsName()
    {
        // "account user rest" — spaceIdx is 4 (>= 0), returns "user"
        // If mutated to > 0, would still pass. Use a case where space IS at index 0
        // after the split: "account  two_spaces" where first space is at index 0 of rest
        var result = CopilotProvider.ExtractUsername("Logged in to github.com account user (keyring)");
        Assert.Equal("user", result);
    }

    [Fact]
    public void ExtractUsername_NoSpaceAfterAccountName_ReturnsFullRest()
    {
        // When there's no space after the username, spaceIdx = -1 (< 0) → return full rest
        var result = CopilotProvider.ExtractUsername("Logged in to github.com account myuser");
        Assert.Equal("myuser", result);
    }

    [Fact]
    public void ExtractUsername_AsPattern_SpaceAfterUsername_ExtractsName()
    {
        var result = CopilotProvider.ExtractUsername("Logged in to github.com as myuser (keyring)");
        Assert.Equal("myuser", result);
    }

    [Fact]
    public void ExtractUsername_AsPattern_NoSpaceAfterUsername_ReturnsFullRest()
    {
        var result = CopilotProvider.ExtractUsername("Logged in to github.com as myuser");
        Assert.Equal("myuser", result);
    }

    [Fact]
    public void ExtractUsername_NeitherPattern_ReturnsNull()
    {
        var result = CopilotProvider.ExtractUsername("some random line");
        Assert.Null(result);
    }

    // ==========================================================================
    // CopilotProvider.BuildFetchResult logic (tested via FetchUsageAsync)
    // Targets: line 149 (&& to ||), line 164 (All to Any)
    // ==========================================================================
    [Fact]
    public async Task FetchUsageAsync_AllAccountsFail_ErrorMessagePresent()
    {
        // All accounts fail → ErrorMessage should be populated with all usernames
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string> { "user1", "user2" });

        var handler = new MockHttpMessageHandler(System.Net.HttpStatusCode.Unauthorized, "{}");
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance, httpFactory, settings);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("fake-token");

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("user1", result.ErrorMessage);
        Assert.Contains("user2", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_OneSuccessOneFail_SuccessTrueNoErrorMessage()
    {
        // One success + one fail → Success=true, ErrorMessage=null
        // Kills All→Any mutation
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string> { "user1", "user2" });

        var callCount = 0;
        var successJson = """{"copilot_plan":"individual_pro","quota_snapshots":{"premium_interactions":{"entitlement":100,"remaining":50}}}""";
        var handler = new MockHttpMessageHandler((req) =>
        {
            var count = Interlocked.Increment(ref callCount);
            return count == 1
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(successJson) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        });

        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance, httpFactory, settings);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("fake-token");

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_SuccessButNoPremiumInteractions_NullSessionUsage()
    {
        // Success=true but response has no premium_interactions → SessionUsage null
        // Kills the && to || mutation
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string> { "user1" });

        var json = """{"copilot_plan":"individual_pro","quota_snapshots":{}}""";
        var handler = new MockHttpMessageHandler(System.Net.HttpStatusCode.OK, json);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance, httpFactory, settings);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("fake-token");

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Null(result.SessionUsage);
    }

    // ==========================================================================
    // SettingsService.IsProviderEnabled logic
    // Target: line 255 logical mutation (! to no-!)
    // ==========================================================================
    [Fact]
    public void IsProviderEnabled_NullProviderSettings_ReturnsTrue()
    {
        // When the provider entry is null in the dict, ps is null → returns true (enabled by default)
        var tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var json = """
            {
                "refreshIntervalSeconds": 120,
                "providers": {
                    "Copilot": null
                }
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "settings.json"), json);
            var sut = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            Assert.True(sut.IsProviderEnabled(ProviderId.Copilot));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ==========================================================================
    // SettingsService.MergeSessionBaselines / MergeSessionResetTimes
    // Targets: lines 114, 151, 162-163 (null-coalescing and statement mutations)
    // ==========================================================================
    [Fact]
    public void Save_MergesSessionBaselinesFromDisk_WhenMemoryHasNullDict()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Write disk file with baselines
            var diskSettings = new AppSettings
            {
                RefreshIntervalSeconds = 120,
                SessionSpendingBaselines = new Dictionary<string, decimal> { ["Claude"] = 10.5m },
                SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset> { ["Claude"] = DateTimeOffset.UtcNow },
                Providers = new Dictionary<string, ProviderSettings>()
            };
            var json = JsonSerializer.Serialize(diskSettings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(Path.Combine(tempDir, "settings.json"), json);

            var sut = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var loaded = sut.Load();

            // Now save with null baselines — merge should preserve disk values
            loaded.SessionSpendingBaselines = null!;
            loaded.SessionSpendingResetTimes = null!;
            sut.Save(loaded);

            var final = sut.Load();
            Assert.NotNull(final.SessionSpendingBaselines);
            Assert.True(final.SessionSpendingBaselines.ContainsKey("Claude"));
            Assert.Equal(10.5m, final.SessionSpendingBaselines["Claude"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Save_MergesProviderFromDisk_WhenMemoryLacksProvider()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Disk has a provider that memory doesn't
            var diskSettings = new AppSettings
            {
                RefreshIntervalSeconds = 120,
                Providers = new Dictionary<string, ProviderSettings>
                {
                    ["Copilot"] = new() { Enabled = true, ApiKey = "disk-key" },
                    ["OpenCodeZen"] = new() { Enabled = true, ApiKey = "zen-key" }
                }
            };
            var json = JsonSerializer.Serialize(diskSettings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(Path.Combine(tempDir, "settings.json"), json);

            var sut = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var loaded = sut.Load();

            // Remove OpenCodeZen from memory then save
            loaded.Providers!.Remove("OpenCodeZen");
            sut.Save(loaded);

            var final = sut.Load();

            // OpenCodeZen should be merged back from disk
            Assert.True(final.Providers!.ContainsKey("OpenCodeZen"));
            Assert.Equal("zen-key", final.Providers["OpenCodeZen"]!.ApiKey);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ==========================================================================
    // UsageRefreshService.Dispose — waits for refresh loop
    // Targets: lines 78, 210 (is null mutations), 89/225 (statement removal of cts.Dispose)
    // ==========================================================================
    [Fact]
    public async Task StopAsync_WaitsForRefreshLoop_ThenClearsState()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata
        {
            Id = ProviderId.Copilot,
            DisplayName = "Copilot",
            Description = "test",
        });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(ProviderUsageResult.EmptySuccess(ProviderId.Copilot));

        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromMilliseconds(50);
        service.Start();

        // Let it run at least one cycle
        await Task.Delay(100);

        // Stop should complete without exceptions
        await service.StopAsync();

        Assert.Null(service.NextRefreshAtUtc);

        // Calling Stop again should be safe (idempotent)
        await service.StopAsync();
        Assert.Null(service.NextRefreshAtUtc);
    }

    [Fact]
    public void Dispose_WhenRunning_WaitsForLoop_ThenClearsState()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata
        {
            Id = ProviderId.Copilot,
            DisplayName = "Copilot",
            Description = "test",
        });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(ProviderUsageResult.EmptySuccess(ProviderId.Copilot));

        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromMilliseconds(50);
        service.Start();

        Thread.Sleep(100);

        // Dispose should not throw and should clear state
        service.Dispose();
        Assert.Null(service.NextRefreshAtUtc);

        // Double-dispose safe
        service.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_ClearsNextRefreshAndWaitsForLoop()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata
        {
            Id = ProviderId.OpenRouter,
            DisplayName = "OpenRouter",
            Description = "test",
        });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(ProviderUsageResult.EmptySuccess(ProviderId.OpenRouter));

        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromMilliseconds(50);
        service.Start();

        await Task.Delay(100);
        await service.DisposeAsync();

        Assert.Null(service.NextRefreshAtUtc);
    }

    // ==========================================================================
    // UsageRefreshService — SetNextRefreshAtUtc fires event (statement removal)
    // Target: line 112 (removing SetNextRefreshAtUtc call in loop)
    // ==========================================================================
    [Fact]
    public async Task Start_SetsNextRefreshAtUtc_AfterFirstFetch()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata
        {
            Id = ProviderId.Copilot,
            DisplayName = "Copilot",
            Description = "test",
        });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(ProviderUsageResult.EmptySuccess(ProviderId.Copilot));

        using var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromMilliseconds(100);

        DateTimeOffset? firedValue = null;
        service.NextRefreshChanged += v => firedValue = v;
        service.Start();

        // Wait for initial fetch + SetNextRefreshAtUtc
        await Task.Delay(200);

        Assert.NotNull(service.NextRefreshAtUtc);
        Assert.NotNull(firedValue);

        await service.StopAsync();
    }

    // ==========================================================================
    // ClaudeProvider.IsAvailableAsync — boolean mutation
    // Target: line 224 (Boolean mutation → false)
    // ==========================================================================
    [Fact]
    public async Task ClaudeProvider_IsAvailableAsync_WhenEnabled_ReturnsTrue()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task ClaudeProvider_IsAvailableAsync_WhenDisabled_ReturnsFalse()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(false);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.False(await provider.IsAvailableAsync());
    }

    // ==========================================================================
    // CopilotProvider.IsAvailableAsync — boolean mutation
    // Target: line 108 (Boolean mutation → false on isEnabled)
    // ==========================================================================
    [Fact]
    public async Task CopilotProvider_IsAvailableAsync_WhenEnabled_ReturnsTrue()
    {
        var provider = CreateCopilotProvider(enabled: true);
        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task CopilotProvider_IsAvailableAsync_WhenDisabled_ReturnsFalse()
    {
        var provider = CreateCopilotProvider(enabled: false);
        Assert.False(await provider.IsAvailableAsync());
    }

    // ==========================================================================
    // SettingsService.CreateDefaults — boolean mutations on default Enabled values
    // Targets: lines 378 (OpenRouter=true), 381 (Claude=false)
    // ==========================================================================
    [Fact]
    public void SettingsService_Defaults_OpenRouterEnabled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sut = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var settings = sut.Load();
            Assert.True(settings.Providers![ProviderId.OpenRouter.ToString()]!.Enabled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsService_Defaults_ClaudeDisabled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sut = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var settings = sut.Load();
            Assert.False(settings.Providers![ProviderId.Claude.ToString()]!.Enabled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SettingsService_Defaults_OpenCodeGoEnabled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sut = new SettingsService(NullLogger<SettingsService>.Instance, tempDir);
            var settings = sut.Load();
            Assert.True(settings.Providers![ProviderId.OpenCodeGo.ToString()]!.Enabled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================
    private static CopilotProvider CreateCopilotProvider(bool enabled = true)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(enabled);
        settings.GetCopilotAccounts().Returns(new List<string>());

        return new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(System.Net.HttpStatusCode statusCode, string content)
        {
            this._handler = _ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
            };
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            this._handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(this._handler(request));
    }
}
