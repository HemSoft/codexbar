// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeZen;
using CodexBar.Core.Providers.OpenRouter;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Round 3 mutation-killing tests targeting surviving mutants from stryker analysis.
/// Focus areas:
/// - SettingsService null-coalescing merge operations (lines 114, 151, 162, 163)
/// - SettingsService.IsProviderEnabled logical mutation (line 255)
/// - OpenCodeZenProvider cache logical mutation (line 126)
/// - OpenRouterProvider totalCredits boundary mutations (line 86).
/// </summary>
public class MutationKillingRound3Tests
{
    // ==========================================================================
    // SettingsService.MergeSessionResetTimes — L162 ??= to = mutation
    // Kills: CoalesceAssignmentExpression → SimpleAssignmentExpression mutation
    // The mutant replaces ??= with =, wiping memory entries. This test verifies
    // that pre-existing memory entries survive the merge (not wiped to []).
    // ==========================================================================
    [Fact]
    public void Save_MemoryHasResetTimes_DiskHasDifferentResetTimes_BothPreserved()
    {
        using var fixture = new SettingsFixture();
        var memTime = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var diskTime = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

        // Write disk with provider2's reset time
        var diskSettings = new AppSettings
        {
            Providers = [],
            SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset>
            {
                ["provider2"] = diskTime,
            },
        };
        fixture.WriteDisk(diskSettings);

        // Memory has provider1's reset time (non-null, non-empty)
        var service = fixture.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset>
            {
                ["provider1"] = memTime,
            },
        };
        service.Save(memSettings);

        // Both should be in the saved file
        var loaded = fixture.CreateService().Load();
        Assert.True(
            loaded.SessionSpendingResetTimes.ContainsKey("provider1"),
            "Memory's reset time should be preserved (??= should not wipe it)");
        Assert.Equal(memTime, loaded.SessionSpendingResetTimes["provider1"]);
        Assert.True(
            loaded.SessionSpendingResetTimes.ContainsKey("provider2"),
            "Disk's reset time should be merged in");
        Assert.Equal(diskTime, loaded.SessionSpendingResetTimes["provider2"]);
    }

    // ==========================================================================
    // SettingsService.MergeSessionBaselines — L150 ??= to = mutation
    // Similar pattern: memory baselines should not be wiped during merge.
    // ==========================================================================
    [Fact]
    public void Save_MemoryHasBaselines_DiskHasDifferentBaselines_BothPreserved()
    {
        using var fixture = new SettingsFixture();

        // Write disk with provider2's baseline
        var diskSettings = new AppSettings
        {
            Providers = [],
            SessionSpendingBaselines = new Dictionary<string, decimal>
            {
                ["provider2"] = 50m,
            },
        };
        fixture.WriteDisk(diskSettings);

        // Memory has provider1's baseline
        var service = fixture.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            SessionSpendingBaselines = new Dictionary<string, decimal>
            {
                ["provider1"] = 100m,
            },
        };
        service.Save(memSettings);

        var loaded = fixture.CreateService().Load();
        Assert.True(
            loaded.SessionSpendingBaselines.ContainsKey("provider1"),
            "Memory's baseline should be preserved (??= should not wipe it)");
        Assert.Equal(100m, loaded.SessionSpendingBaselines["provider1"]);
        Assert.True(
            loaded.SessionSpendingBaselines.ContainsKey("provider2"),
            "Disk's baseline should be merged in");
        Assert.Equal(50m, loaded.SessionSpendingBaselines["provider2"]);
    }

    // ==========================================================================
    // SettingsService.MergeProviders — L114 null coalescing (remove right)
    // Kills: disk.Providers ?? [] → disk.Providers (if null, foreach crashes)
    // ==========================================================================
    [Fact]
    public void Save_DiskHasExplicitNullProviders_MergeDoesNotThrow()
    {
        using var fixture = new SettingsFixture();

        // Write JSON with explicit null for providers
        File.WriteAllText(fixture.SettingsPath, """{"providers":null}""");

        var service = fixture.CreateService();
        var memSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Test"] = new() { Enabled = true },
            },
        };

        // Without ?? [], this would throw NullReferenceException in foreach
        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);

        var loaded = fixture.CreateService().Load();
        Assert.True(loaded.Providers.ContainsKey("Test"));
    }

    // ==========================================================================
    // SettingsService.MergeSessionBaselines — L151 null coalescing (remove right)
    // Kills: disk.SessionSpendingBaselines ?? [] → disk.SessionSpendingBaselines
    // ==========================================================================
    [Fact]
    public void Save_DiskHasExplicitNullBaselines_MergeDoesNotThrow()
    {
        using var fixture = new SettingsFixture();

        // Write JSON with explicit null for sessionSpendingBaselines
        File.WriteAllText(fixture.SettingsPath, """{"providers":{},"sessionSpendingBaselines":null}""");

        var service = fixture.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            SessionSpendingBaselines = new Dictionary<string, decimal> { ["k1"] = 25m },
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);

        var loaded = fixture.CreateService().Load();
        Assert.Equal(25m, loaded.SessionSpendingBaselines["k1"]);
    }

    // ==========================================================================
    // SettingsService.MergeSessionResetTimes — L163 null coalescing (remove right)
    // Kills: disk.SessionSpendingResetTimes ?? [] → disk.SessionSpendingResetTimes
    // ==========================================================================
    [Fact]
    public void Save_DiskHasExplicitNullResetTimes_MergeDoesNotThrow()
    {
        using var fixture = new SettingsFixture();

        // Write JSON with explicit null for sessionSpendingResetTimes
        File.WriteAllText(
            fixture.SettingsPath,
            """{"providers":{},"sessionSpendingBaselines":{},"sessionSpendingResetTimes":null}""");

        var service = fixture.CreateService();
        var time = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var memSettings = new AppSettings
        {
            Providers = [],
            SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset> { ["k1"] = time },
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);

        var loaded = fixture.CreateService().Load();
        Assert.Equal(time, loaded.SessionSpendingResetTimes["k1"]);
    }

    // ==========================================================================
    // Helper infrastructure
    // ==========================================================================
    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _tempDir;

        public SettingsFixture()
        {
            this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-r3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(this._tempDir);
        }

        public string SettingsPath => Path.Combine(this._tempDir, "settings.json");

        public SettingsService CreateService() =>
            new(NullLogger<SettingsService>.Instance, this._tempDir);

        public void WriteDisk(AppSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(this.SettingsPath, json);
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
    }
}

/// <summary>
/// OpenCodeZen cache mutation tests (requires env var isolation).
/// Kills: L126 logical mutation (&&→||) on workspace check.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class OpenCodeZenCacheMutationTests : IDisposable
{
    public OpenCodeZenCacheMutationTests()
    {
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", null);
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE", null);
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", null);
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", null);
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE", null);
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", null);
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", null);
    }

    /// <summary>
    /// Verifies that a cached result for workspace "ws-A" is NOT returned when
    /// the workspace changes to "ws-B". The logical mutation (&&→||) would skip
    /// the workspace equality check and return stale cache for any workspace.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous unit test.</placeholder></returns>
    [Fact]
    public async Task FetchUsageAsync_CachedForDifferentWorkspace_DoesNotReturnStaleCache()
    {
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", "ws-A");
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE", "cookie");

        var callCount = 0;
        var handler = new OpenCodeZenTestHandler(() =>
        {
            callCount++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("balance:500000000"), // $0.50
            };
        });

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeZen).Returns(true);
        settings.GetApiKey(ProviderId.OpenCodeZen).Returns((string?)null);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns((string?)null);
        settings.GetOpenCodeGoWorkspaceId().Returns((string?)null);

        var factory = new HttpClientFactoryStub(handler);
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        // First fetch caches for workspace "ws-A"
        var result1 = await provider.FetchUsageAsync();
        Assert.True(result1.Success);
        Assert.Equal(1, callCount);

        // Change workspace — must NOT return stale cache
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", "ws-B");

        var result2 = await provider.FetchUsageAsync();
        Assert.True(result2.Success);
        Assert.Equal(2, callCount);
    }

    private sealed class OpenCodeZenTestHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(factory());
        }
    }
}
