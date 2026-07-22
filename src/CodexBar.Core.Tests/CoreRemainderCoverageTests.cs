// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Codex;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
#if WINDOWS
using Microsoft.Data.Sqlite;
#endif

public sealed class CodexProviderRemainderCoverageTests : IDisposable
{
    public CodexProviderRemainderCoverageTests()
    {
        CodexProvider.ResetTimeZoneResolverForTests();
    }

    public void Dispose()
    {
        CodexProvider.ResetTimeZoneResolverForTests();
    }

    [Theory]
    [InlineData("free", "Free")]
    [InlineData("pro", "Pro")]
    [InlineData("team", "Team")]
    [InlineData("enterprise", "Enterprise")]
    public void FormatPlanName_KnownPlans_ReturnsDisplayName(string planType, string expected)
    {
        var result = CodexProvider.FormatPlanName(planType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetEasternTimeZoneAbbreviation_UnknownOffset_ReturnsGenericLabel()
    {
        var result = CodexProvider.GetEasternTimeZoneAbbreviation(
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("ET", result);
    }

    [Fact]
    public void ResolveEasternTimeZone_WindowsIdMissing_UsesIanaFallback()
    {
        var iana = TimeZoneInfo.CreateCustomTimeZone("America/New_York", TimeSpan.FromHours(-5), "Eastern", "Eastern");
        CodexProvider.TimeZoneResolver = id => id == "America/New_York" ? iana : null;

        var result = CodexProvider.ResolveEasternTimeZone();

        Assert.Same(iana, result);
    }

    [Fact]
    public void ResolveEasternTimeZone_KnownIdsMissing_UsesLocalFallback()
    {
        var local = TimeZoneInfo.CreateCustomTimeZone("LocalTest", TimeSpan.FromHours(2), "Local", "Local");
        CodexProvider.TimeZoneResolver = _ => null;
        CodexProvider.LocalTimeZone = local;

        var result = CodexProvider.ResolveEasternTimeZone();

        Assert.Same(local, result);
    }

    [Fact]
    public async Task TimeZoneOverrides_SetInParallelAsyncContext_DoNotAffectSiblingContext()
    {
        var local = TimeZoneInfo.CreateCustomTimeZone("LocalTest", TimeSpan.FromHours(2), "Local", "Local");
        var overrideSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOverride = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overrideTask = Task.Run(async () =>
        {
            CodexProvider.TimeZoneResolver = _ => null;
            CodexProvider.LocalTimeZone = local;
            overrideSet.SetResult();

            await releaseOverride.Task;

            return CodexProvider.ResolveEasternTimeZone();
        });

        await overrideSet.Task;
        TimeZoneInfo siblingTimeZone;
        try
        {
            siblingTimeZone = await Task.Run(CodexProvider.ResolveEasternTimeZone);
        }
        finally
        {
            releaseOverride.SetResult();
        }

        var overrideTimeZone = await overrideTask;

        Assert.Same(local, overrideTimeZone);
        Assert.NotSame(local, siblingTimeZone);
        Assert.NotEqual(local.Id, siblingTimeZone.Id);
    }

    [Fact]
    public void ResetTimeZoneResolverForTests_LocalTimeZoneOverrideSet_RestoresSystemLocal()
    {
        CodexProvider.LocalTimeZone = TimeZoneInfo.CreateCustomTimeZone("LocalTest", TimeSpan.FromHours(2), "Local", "Local");

        CodexProvider.ResetTimeZoneResolverForTests();

        Assert.Same(TimeZoneInfo.Local, CodexProvider.LocalTimeZone);
    }

    [Fact]
    public void DefaultTimeZoneResolver_MissingId_ReturnsNull()
    {
        var result = CodexProvider.TimeZoneResolver($"missing-zone-{Guid.NewGuid():N}");

        Assert.Null(result);
    }
}

public sealed class SettingsServiceRemainderCoverageTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceRemainderCoverageTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_settings_remainder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Save_MemoryProviderCardOrderEmpty_PreservesDiskOrder()
    {
        File.WriteAllText(
            Path.Combine(this._tempDir, "settings.json"),
            """
            {
              "providerCardOrder": ["Claude", "Codex"],
              "providers": {}
            }
            """);
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = [],
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Equal(["Claude", "Codex"], loaded.ProviderCardOrder);
    }

    [Fact]
    public void Save_ProviderCardOrderContainsBlankAndDuplicate_NormalizesOrder()
    {
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = ["Claude", " ", "claude", "Codex"],
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Equal(["Claude", "Codex"], loaded.ProviderCardOrder);
    }

    [Fact]
    public void Save_MemoryProviderCardOrderPresent_DoesNotUseDiskOrder()
    {
        File.WriteAllText(
            Path.Combine(this._tempDir, "settings.json"),
            """
            {
              "providerCardOrder": ["Disk"],
              "providers": {}
            }
            """);
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = ["Memory"],
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Equal(["Memory"], loaded.ProviderCardOrder);
    }

    [Fact]
    public void Save_MemoryProviderCardOrderEmptyAndDiskOrderEmpty_LeavesOrderEmpty()
    {
        File.WriteAllText(
            Path.Combine(this._tempDir, "settings.json"),
            """
            {
              "providerCardOrder": [],
              "providers": {}
            }
            """);
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = [],
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Empty(loaded.ProviderCardOrder);
    }

    [Fact]
    public void Save_NullProviderCardOrder_NormalizesToEmptyOrder()
    {
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = null!,
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Empty(loaded.ProviderCardOrder);
    }

    [Fact]
    public void Save_NullMemoryProviderCardOrder_PreservesDiskOrder()
    {
        File.WriteAllText(
            Path.Combine(this._tempDir, "settings.json"),
            """
            {
              "providerCardOrder": ["Disk"],
              "providers": {}
            }
            """);
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = null!,
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Equal(["Disk"], loaded.ProviderCardOrder);
    }

    [Fact]
    public void Save_MemoryProviderCardOrderEmptyAndDiskOrderMissing_LeavesOrderEmpty()
    {
        File.WriteAllText(
            Path.Combine(this._tempDir, "settings.json"),
            """
            {
              "providers": {}
            }
            """);
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = [],
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Empty(loaded.ProviderCardOrder);
    }

    [Fact]
    public void Save_MemoryProviderCardOrderEmptyAndDiskOrderNull_LeavesOrderEmpty()
    {
        File.WriteAllText(
            Path.Combine(this._tempDir, "settings.json"),
            """
            {
              "providerCardOrder": null,
              "providers": {}
            }
            """);
        var service = this.CreateService();

        service.Save(new AppSettings
        {
            ProviderCardOrder = [],
            Providers = [],
        });

        var loaded = this.CreateService().Load();
        Assert.Empty(loaded.ProviderCardOrder);
    }

    private SettingsService CreateService() =>
        new(NullLogger<SettingsService>.Instance, this._tempDir);
}

public sealed class CopilotProviderRemainderCoverageTests
{
    [Theory]
    [InlineData(null, "fallback", "fallback")]
    [InlineData("  ", "fallback", "fallback")]
    [InlineData(" value ", "fallback", "value")]
    public void NormalizeCopilotSetting_BlankOrValue_ReturnsExpected(string? value, string fallback, string expected)
    {
        var result = CopilotProvider.NormalizeCopilotSetting(value, fallback);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(10, 10)]
    public void NormalizeCopilotPoolTotalOverride_NonPositiveOrPositive_ReturnsExpected(
        int? value,
        int? expected)
    {
        decimal? result = CopilotProvider.NormalizeCopilotPoolTotalOverride(value);

        Assert.Equal((decimal?)expected, result);
    }

    [Fact]
    public void SumGrossQuantity_NullItems_ReturnsZero()
    {
        var result = CopilotProvider.SumGrossQuantity(null);

        Assert.Equal(0, result);
    }

    [Fact]
    public void SumGrossQuantity_Items_ReturnsTotal()
    {
        var result = CopilotProvider.SumGrossQuantity(
            [
                new BillingUsageItem { GrossQuantity = 1.5m },
                new BillingUsageItem { GrossQuantity = 2.5m },
            ]);

        Assert.Equal(4, result);
    }

    [Theory]
    [InlineData(0, 10, 20, 0)]
    [InlineData(10, 0, 20, 10)]
    [InlineData(10, 20, 20, 10)]
    [InlineData(10, 10, 20, 20)]
    public void ProjectMonthEndCredits_BoundariesAndActiveWindow_ReturnsExpected(
        decimal consumed,
        int elapsedDays,
        int totalDays,
        decimal expected)
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(totalDays);
        var now = start.AddDays(elapsedDays);

        var result = CopilotProvider.ProjectMonthEndCredits(consumed, start, end, now);

        Assert.Equal(expected, Math.Round(result, 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void BuildOrgUsageBars_MissingPoolTotal_ReturnsNull(int? poolTotalValue)
    {
        decimal? poolTotal = poolTotalValue;
        var primary = new UsageSnapshot { UsedPercent = 0.25, UsageLabel = "usage" };

        var bars = CopilotProvider.BuildOrgUsageBars(
            100,
            poolTotal,
            200,
            primary,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        Assert.Null(bars);
    }

    [Fact]
    public void BuildUserUsageBars_NoOrgConsumed_ExcludesShareBar()
    {
        var primary = new UsageSnapshot { UsedPercent = 0.25, UsageLabel = "usage" };

        var bars = CopilotProvider.BuildUserUsageBars(
            100,
            1_000,
            200,
            primary,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            100,
            null);

        Assert.DoesNotContain(bars, bar => bar.Label.StartsWith("Share of org", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildMonthlyUsageSnapshot_TotalMissing_ReturnsZeroUsagePercent()
    {
        var snapshot = CopilotProvider.BuildMonthlyUsageSnapshot(100, null, "100 AI credits", 2026, 6);

        Assert.Equal(0, snapshot.UsedPercent);
        Assert.Equal("100 AI credits", snapshot.UsageLabel);
    }

    [Theory]
    [InlineData(2025, 6, 3900)]
    [InlineData(2026, 5, 3900)]
    [InlineData(2026, 6, 7000)]
    [InlineData(2026, 8, 7000)]
    [InlineData(2026, 9, 3900)]
    public void GetCreditsPerSeat_PromotionalWindow_ReturnsExpectedCredits(int year, int month, int expected)
    {
        var result = CopilotProvider.GetCreditsPerSeat(year, month);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_OrgTokenMissing_ReturnsNullAsync()
    {
        var provider = CreateCopilotProvider(_ => new HttpResponseMessage(HttpStatusCode.OK), _ => Task.FromResult<string?>(null));

        var result = await InvokeTryBuildBillingItemsAsync(provider, [("alice", EnterpriseAccount("alice"))], new AppSettings());

        Assert.Null(result);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_NonEligibleAccount_AddsFallbackItemAsync()
    {
        var provider = CreateCopilotProvider(request =>
        {
            var uri = request.RequestUri!.ToString();
            return uri.Contains("/settings/billing/usage/summary", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : JsonResponse("""{"usageItems":[]}""");
        });

        var result = await InvokeTryBuildBillingItemsAsync(
            provider,
            [
                ("alice", EnterpriseAccount("alice")),
                ("bob", new CopilotAccountResult
                {
                    Username = "bob",
                    Success = true,
                    Plan = "individual_pro",
                    PremiumInteractions = new CopilotQuotaSnapshot { Entitlement = 100, Remaining = 40 },
                }),
            ],
            new AppSettings());

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_SecondaryEligibleUserTokenMissing_AddsFallbackItemAsync()
    {
        var provider = CreateCopilotProvider(
            request => request.RequestUri!.ToString().Contains("/settings/billing/usage/summary", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse("""{"usageItems":[{"grossQuantity":10}]}""")
                : JsonResponse("""{"usageItems":[]}"""),
            username => Task.FromResult<string?>(username == "alice" ? "token" : null));

        var result = await InvokeTryBuildBillingItemsAsync(
            provider,
            [("alice", EnterpriseAccount("alice")), ("carol", EnterpriseAccount("carol"))],
            new AppSettings());

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_UserBillingMissing_FallsBackToQuotaItemAsync()
    {
        var provider = CreateCopilotProvider(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/settings/billing/usage/summary", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"usageItems":[{"grossQuantity":10}]}""");
            }

            if (uri.Contains("/copilot/billing", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"seat_breakdown":{}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await InvokeTryBuildBillingItemsAsync(provider, [("alice", EnterpriseAccount("alice"))], new AppSettings());

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_AllBillingRequestsMissing_ReturnsNullAsync()
    {
        var provider = CreateCopilotProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await InvokeTryBuildBillingItemsAsync(provider, [("alice", EnterpriseAccount("alice"))], new AppSettings());

        Assert.Null(result);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_SeatCountRequestMissing_UsesNoPoolTotalAsync()
    {
        var provider = CreateCopilotProvider(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/settings/billing/usage/summary", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"usageItems":[{"grossQuantity":10}]}""");
            }

            if (uri.Contains("/copilot/billing", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return JsonResponse("""{"usageItems":[]}""");
        });

        var result = await InvokeTryBuildBillingItemsAsync(provider, [("alice", EnterpriseAccount("alice"))], new AppSettings());

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_PoolTotalOverride_SkipsSeatLookupAsync()
    {
        var sawSeatLookup = false;
        var provider = CreateCopilotProvider(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/copilot/billing", StringComparison.OrdinalIgnoreCase))
            {
                sawSeatLookup = true;
            }

            return uri.Contains("/settings/billing/usage/summary", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse("""{"usageItems":[{"grossQuantity":10}]}""")
                : JsonResponse("""{"usageItems":[]}""");
        });

        var result = await InvokeTryBuildBillingItemsAsync(
            provider,
            [("alice", EnterpriseAccount("alice"))],
            new AppSettings { CopilotPoolTotal = 123m });

        Assert.NotNull(result);
        Assert.False(sawSeatLookup);
    }

    [Fact]
    public async Task TryBuildBillingItemsAsync_RestOperationCanceled_RethrowsAsync()
    {
        var provider = CreateCopilotProvider(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            InvokeTryBuildBillingItemsAsync(provider, [("alice", EnterpriseAccount("alice"))], new AppSettings()));
    }

    private static CopilotProvider CreateCopilotProvider(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        Func<string, Task<string?>>? tokenResolver = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            new HttpClientFactoryStub(new CopilotRemainderHandler(handler)),
            settings)
        {
            TokenResolverOverride = (username, _) => tokenResolver?.Invoke(username) ?? Task.FromResult<string?>("token"),
        };
        return provider;
    }

    private static Task<object?> InvokeTryBuildBillingItemsAsync(
        CopilotProvider provider,
        IReadOnlyList<(string Username, CopilotAccountResult Result)> accountResults,
        AppSettings appSettings) =>
        ReflectionTestHelpers.InvokePrivateAsync<object?>(
            provider,
            "TryBuildBillingItemsAsync",
            accountResults,
            appSettings,
            CancellationToken.None);

    private static CopilotAccountResult EnterpriseAccount(string username) => new()
    {
        Username = username,
        Success = true,
        Plan = "enterprise",
        Organizations = ["Relias-Engineering"],
        PremiumInteractions = new CopilotQuotaSnapshot { Entitlement = 100, Remaining = 40 },
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class CopilotRemainderHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

[Collection("ClaudeProviderFileIo")]
public sealed class ClaudeProviderRemainderCoverageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _credentialsPath;
    private readonly string _statsPath;
    private readonly string _claudeJsonPath;

    public ClaudeProviderRemainderCoverageTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"claude_remainder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        this._credentialsPath = Path.Combine(this._tempDir, ".credentials.json");
        this._statsPath = Path.Combine(this._tempDir, "stats-cache.json");
        this._claudeJsonPath = Path.Combine(this._tempDir, "claude.json");
        this.SetupOverrides();
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;
        ClaudeProvider.EnvironmentAccessTokenOverride = null;
        ClaudeProvider.ResetEnvironmentProvidersForTests();
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = null;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = null;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = null;
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;
        ClaudeProvider.WebSessionCachePathOverride = null;

        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task FetchUsageAsync_CorruptedCredentialsJson_ReturnsCredentialErrorAsync()
    {
        File.WriteAllText(this._credentialsPath, "{not-json");

        var result = await this.CreateProvider().FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchUsageAsync_MalformedOAuthElement_ReturnsCredentialErrorAsync()
    {
        File.WriteAllText(this._credentialsPath, """{"claudeAiOauth":"not-an-object"}""");

        var result = await this.CreateProvider().FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchUsageAsync_MissingAccessToken_ReturnsSuccessWithFallbackUsageAsync()
    {
        File.WriteAllText(this._credentialsPath, """{"claudeAiOauth":{"subscriptionType":"pro"}}""");

        var result = await this.CreateProvider().FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Contains("Rate limits unavailable", result.SessionUsage!.UsageLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchUsageAsync_ExpiredTokenRefreshThrows_UsesExistingCredentialsAsync()
    {
        this.WriteCredentials(DateTimeOffset.UtcNow.AddMinutes(-5), "old-token", "refresh-token");
        var provider = this.CreateProvider(_ => throw new InvalidOperationException("refresh failed"));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_CallerCancellation_RethrowsAsync()
    {
        using var cts = new CancellationTokenSource();
        var provider = this.CreateProvider(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "old-token",
            RefreshToken = "refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.ClaudeCredentials?>(
                provider,
                "TryRefreshTokenAsync",
                credentials,
                cts.Token));
    }

    [Fact]
    public async Task FetchUsageAsync_OAuthUsagePayload_ReturnsUsageAsync()
    {
        this.WriteCredentials(DateTimeOffset.UtcNow.AddHours(1), "valid-token", null);
        var reset = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var provider = this.CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "five_hour": { "utilization": 0.2, "resets_at": "{{reset}}" },
                  "seven_day": { "utilization": 0.4, "resets_at": "{{reset}}" }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(0.2, result.SessionUsage!.UsedPercent);
    }

    [Fact]
    public async Task FetchOAuthUsageAsync_Timeout_ReturnsFallbackAsync()
    {
        var provider = this.CreateProvider(_ => throw new OperationCanceledException());

        var result = await ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.UnifiedRateLimits?>(
            provider,
            "FetchOAuthUsageAsync",
            "token",
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchOAuthUsageAsync_CallerCancellation_RethrowsAsync()
    {
        using var cts = new CancellationTokenSource();
        var provider = this.CreateProvider(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.UnifiedRateLimits?>(
                provider,
                "FetchOAuthUsageAsync",
                "token",
                cts.Token));
    }

    [Fact]
    public async Task FetchOAuthUsageAsync_AuthoritativeCacheExists_ReturnsCachedUsageAsync()
    {
        var provider = this.CreateProvider();
        var cached = new ClaudeProvider.UnifiedRateLimits { FiveHourUtilization = 0.42 };
        provider.CacheAndReturnUsageLimits(cached);

        var result = await ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.UnifiedRateLimits?>(
            provider,
            "FetchOAuthUsageAsync",
            "token",
            CancellationToken.None);

        Assert.Same(cached, result);
    }

    [Fact]
    public async Task FetchOAuthUsageAsync_CacheFilledAfterLockWait_ReturnsCachedUsageAsync()
    {
        var provider = this.CreateProvider(_ => throw new InvalidOperationException("HTTP should not be reached."));
        var cacheLockField = typeof(ClaudeProvider).GetField("cacheLock", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cacheLockField);
        var cacheLock = Assert.IsType<SemaphoreSlim>(cacheLockField!.GetValue(provider));
        await cacheLock.WaitAsync();
        var task = ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.UnifiedRateLimits?>(
            provider,
            "FetchOAuthUsageAsync",
            "token",
            CancellationToken.None);
        await Task.Delay(50);
        var cached = new ClaudeProvider.UnifiedRateLimits { FiveHourUtilization = 0.64 };
        provider.CacheAndReturnUsageLimits(cached);
        cacheLock.Release();

        var result = await task;

        Assert.Same(cached, result);
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_OkPayload_ReturnsUsageAsync()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var provider = this.CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "five_hour": { "utilization": 0.3, "resets_at": "{{reset}}" },
                  "seven_day": { "utilization": 0.6, "resets_at": "{{reset}}" }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });

        var result = await provider.FetchClaudeWebUsageAsync("org-1", "cookie=value", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0.3, result!.FiveHourUtilization);
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_Timeout_ReturnsNullAsync()
    {
        var provider = this.CreateProvider(_ => throw new OperationCanceledException());

        var result = await provider.FetchClaudeWebUsageAsync("org-1", "cookie=value", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_CallerCancellation_RethrowsAsync()
    {
        using var cts = new CancellationTokenSource();
        var provider = this.CreateProvider(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchClaudeWebUsageAsync("org-1", "cookie=value", cts.Token));
    }

    [Fact]
    public void ReadEnvironmentCredentials_ProcessEnvironmentToken_UsesToken()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.EnvironmentVariableProvider = _ => "process-token";
        ClaudeProvider.TargetEnvironmentVariableProvider = (_, _) => throw new InvalidOperationException("User env should not be read.");

        var result = ReadEnvironmentCredentials();

        Assert.NotNull(result);
        Assert.Equal("process-token", result!.AccessToken);
    }

    [Fact]
    public void ReadEnvironmentCredentials_UserEnvironmentToken_UsesFallbackToken()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.EnvironmentVariableProvider = _ => null;
        ClaudeProvider.TargetEnvironmentVariableProvider = (_, target) =>
            target == EnvironmentVariableTarget.User ? "user-token" : null;

        var result = ReadEnvironmentCredentials();

        Assert.NotNull(result);
        Assert.Equal("user-token", result!.AccessToken);
    }

    [Fact]
    public void ReadEnvironmentCredentials_NoEnvironmentToken_ReturnsNull()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.EnvironmentVariableProvider = _ => null;
        ClaudeProvider.TargetEnvironmentVariableProvider = (_, _) => null;

        var result = ReadEnvironmentCredentials();

        Assert.Null(result);
    }

    [Fact]
    public void CacheAndReturnLimits_EmptySnapshot_ReturnsFallback()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var provider = this.CreateProvider();

        var result = provider.CacheAndReturnLimits(new ClaudeProvider.UnifiedRateLimits(), response.Headers);

        Assert.Null(result);
    }

    [Fact]
    public void MapOAuthUsageToRateLimits_NullUsage_ReturnsNull()
    {
        var result = ClaudeProvider.MapOAuthUsageToRateLimits(null);

        Assert.Null(result);
    }

    [Fact]
    public void MapOAuthUsageToRateLimits_NoUsageWindows_ReturnsNull()
    {
        var result = ClaudeProvider.MapOAuthUsageToRateLimits(new ClaudeProvider.ClaudeOAuthUsageResponse());

        Assert.Null(result);
    }

    [Fact]
    public void MapOAuthUsageToRateLimits_FallbackSevenDayAndNullExtraUsage_ReturnsLimits()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var usage = new ClaudeProvider.ClaudeOAuthUsageResponse
        {
            FiveHour = new ClaudeProvider.ClaudeOAuthUsageWindow { Utilization = 150, ResetsAt = reset },
            SevenDayOAuthApps = new ClaudeProvider.ClaudeOAuthUsageWindow { Utilization = 0.25, ResetsAt = reset },
            ExtraUsage = null,
        };

        var result = ClaudeProvider.MapOAuthUsageToRateLimits(usage);

        Assert.NotNull(result);
        Assert.Equal(1, result!.FiveHourUtilization);
        Assert.Equal(0.25, result.SevenDayUtilization);
        Assert.Null(result.ExtraUsageEnabled);
    }

    [Fact]
    public void MapOAuthUsageToRateLimits_OnlySevenDayAndExtraUsage_ReturnsLimits()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var usage = new ClaudeProvider.ClaudeOAuthUsageResponse
        {
            SevenDay = new ClaudeProvider.ClaudeOAuthUsageWindow { Utilization = 75, ResetsAt = reset },
            ExtraUsage = new ClaudeProvider.ClaudeOAuthExtraUsage { IsEnabled = true },
        };

        var result = ClaudeProvider.MapOAuthUsageToRateLimits(usage);

        Assert.NotNull(result);
        Assert.Equal(0, result!.FiveHourUtilization);
        Assert.Equal(0.75, result.SevenDayUtilization);
        Assert.True(result.ExtraUsageEnabled);
    }

    [Fact]
    public void MapOAuthUsageToRateLimits_OnlyFiveHour_ReturnsLimits()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var usage = new ClaudeProvider.ClaudeOAuthUsageResponse
        {
            FiveHour = new ClaudeProvider.ClaudeOAuthUsageWindow { Utilization = 0.5, ResetsAt = reset },
        };

        var result = ClaudeProvider.MapOAuthUsageToRateLimits(usage);

        Assert.NotNull(result);
        Assert.Equal(0.5, result!.FiveHourUtilization);
        Assert.Equal(0, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseClaudeDesktopTokenCache_NonObjectEntry_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"account:org:https://api.anthropic.com:user:inference":[]}""");

        var result = ClaudeProvider.ParseClaudeDesktopTokenCache(doc.RootElement);

        Assert.Null(result);
    }

    [Fact]
    public void ParseClaudeDesktopTokenCache_OnlyTokenEntry_ReturnsCredentialsWithNullOptionalFields()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "account:org:https://api.anthropic.com:user:inference": {
                "token": "token-only"
              }
            }
            """);

        var result = ClaudeProvider.ParseClaudeDesktopTokenCache(doc.RootElement);

        Assert.NotNull(result);
        Assert.Equal("token-only", result!.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Equal(0, result.ExpiresAt);
        Assert.Null(result.SubscriptionType);
        Assert.Null(result.RateLimitTier);
    }

    [Fact]
    public void ClaudeDesktopPaths_OverridesAndDefaults_ReturnExpectedValues()
    {
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = "local-state";
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = "cookies";
        ClaudeProvider.ClaudeDesktopConfigPathOverride = "config";

        Assert.Equal("local-state", ClaudeProvider.ClaudeDesktopLocalStatePath);
        Assert.Equal("cookies", ClaudeProvider.ClaudeDesktopCookiesPath);
        Assert.Equal("config", ClaudeProvider.ClaudeDesktopConfigPath);

        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = null;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = null;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = null;

        Assert.NotEmpty(ClaudeProvider.ClaudeDesktopLocalStatePath);
        Assert.NotEmpty(ClaudeProvider.ClaudeDesktopCookiesPath);
        Assert.NotEmpty(ClaudeProvider.ClaudeDesktopConfigPath);
    }

    [Fact]
    public void DecodeChromiumCookiePlaintext_ShortPlaintext_ReturnsUtf8Text()
    {
        var result = ClaudeProvider.DecodeChromiumCookiePlaintext("claude.ai", Encoding.UTF8.GetBytes("abc"));

        Assert.Equal("abc", result);
    }

    [Fact]
    public void DecodeChromiumCookiePlaintext_PrefixedDigest_ReturnsPayload()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("claude.ai"));
        var payload = Encoding.UTF8.GetBytes("payload");
        var plaintext = digest.Concat(payload).ToArray();

        var result = ClaudeProvider.DecodeChromiumCookiePlaintext("claude.ai", plaintext);

        Assert.Equal("payload", result);
    }

    [Fact]
    public void DecodeChromiumCookiePlaintext_NonPrefixedLongPlaintext_ReturnsUtf8Text()
    {
        var plaintext = Encoding.UTF8.GetBytes(new string('x', 40));

        var result = ClaudeProvider.DecodeChromiumCookiePlaintext("claude.ai", plaintext);

        Assert.Equal(new string('x', 40), result);
    }

    [Fact]
    public void IsChromiumCookieExpired_ZeroOrHugeExpiry_ReturnsFalse()
    {
        Assert.False(ClaudeProvider.IsChromiumCookieExpired(0));
        Assert.False(ClaudeProvider.IsChromiumCookieExpired(265046774400000000));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("sessionKey=value", "sessionKey=value")]
    [InlineData("foo=bar; sessionKey=value", "foo=bar; sessionKey=value")]
    [InlineData("raw-session-value", "sessionKey=raw-session-value")]
    [InlineData("sk-ant-api03-example", null)]
    public void NormalizeClaudeWebCookieHeader_Input_ReturnsExpected(string? input, string? expected)
    {
        var result = ClaudeProvider.NormalizeClaudeWebCookieHeader(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_ConfiguredSettingsCookie_ReturnsCookieBeforeDesktopRead()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        settings.GetApiKey(ProviderId.Claude).Returns("sessionKey=configured");
        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            new HttpClientFactoryStub(new DelegatingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            settings);

        var result = InvokePrivateInstance<string?>(
            provider,
            "TryReadClaudeDesktopCookieHeader");

        Assert.Equal("sessionKey=configured", result);
    }

#if WINDOWS
    [Fact]
    public void WebSessionCache_ValidPath_RoundTripsEncryptedCookieHeader()
    {
        var cachePath = Path.Combine(this._tempDir, "nested", "web-session.bin");
        ClaudeProvider.WebSessionCachePathOverride = cachePath;
        var provider = this.CreateProvider();

        _ = InvokePrivateInstance<object?>(provider, "PersistWebSessionCookieHeader", "sessionKey=secret");
        var result = InvokePrivateInstance<string?>(provider, "TryLoadPersistedWebSessionCookieHeader");

        Assert.Equal("sessionKey=secret", result);
        Assert.DoesNotContain("sessionKey=secret", Encoding.UTF8.GetString(File.ReadAllBytes(cachePath)), StringComparison.Ordinal);
    }

    [Fact]
    public void WebSessionCache_InvalidCiphertext_ReturnsNull()
    {
        var cachePath = Path.Combine(this._tempDir, "invalid-web-session.bin");
        File.WriteAllBytes(cachePath, [1, 2, 3]);
        ClaudeProvider.WebSessionCachePathOverride = cachePath;

        var result = InvokePrivateInstance<string?>(this.CreateProvider(), "TryLoadPersistedWebSessionCookieHeader");

        Assert.Null(result);
    }

    [Fact]
    public void WebSessionCache_UnwritablePath_DoesNotThrow()
    {
        ClaudeProvider.WebSessionCachePathOverride = this._tempDir;

        var exception = Record.Exception(() =>
            InvokePrivateInstance<object?>(this.CreateProvider(), "PersistWebSessionCookieHeader", "sessionKey=value"));

        Assert.Null(exception);
    }

    [Fact]
    public void WebSessionCache_Disabled_SkipsPersistenceLoadingAndInvalidation()
    {
        ClaudeProvider.WebSessionCachePathOverride = null;
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = "test-override";
        var provider = this.CreateProvider();

        _ = InvokePrivateInstance<object?>(provider, "PersistWebSessionCookieHeader", "sessionKey=value");
        var loaded = InvokePrivateInstance<string?>(provider, "TryLoadPersistedWebSessionCookieHeader");
        _ = InvokePrivateInstance<object?>(provider, "InvalidatePersistedWebSessionCookieHeader");

        Assert.Null(loaded);
    }

    [Fact]
    public void InvalidatePersistedWebSessionCookieHeader_ExistingCache_DeletesFile()
    {
        var cachePath = Path.Combine(this._tempDir, "stale-web-session.bin");
        File.WriteAllText(cachePath, "stale");
        ClaudeProvider.WebSessionCachePathOverride = cachePath;

        _ = InvokePrivateInstance<object?>(this.CreateProvider(), "InvalidatePersistedWebSessionCookieHeader");

        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public void TryCopySidecarFile_ReadableSource_CopiesFile()
    {
        var source = Path.Combine(this._tempDir, "source-wal");
        var destination = Path.Combine(this._tempDir, "destination-wal");
        File.WriteAllText(source, "sidecar");

        _ = InvokePrivateStatic<object?>("TryCopySidecarFile", source, destination);

        Assert.Equal("sidecar", File.ReadAllText(destination));
    }

    [Fact]
    public void TryCopySidecarFile_LockedSourceAndDirectoryDestination_SwallowsExpectedErrors()
    {
        var source = Path.Combine(this._tempDir, "locked-wal");
        File.WriteAllText(source, "sidecar");
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _ = InvokePrivateStatic<object?>("TryCopySidecarFile", source, Path.Combine(this._tempDir, "locked-copy"));
        }

        var destinationDirectory = Directory.CreateDirectory(Path.Combine(this._tempDir, "directory-destination"));
        _ = InvokePrivateStatic<object?>("TryCopySidecarFile", source, destinationDirectory.FullName);
    }

    [Fact]
    public void TryDeleteFile_LockedFileAndDirectory_SwallowsExpectedErrors()
    {
        var lockedPath = Path.Combine(this._tempDir, "locked-delete");
        File.WriteAllText(lockedPath, "locked");
        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _ = InvokePrivateStatic<object?>("TryDeleteFile", lockedPath);
        }

        var directory = Directory.CreateDirectory(Path.Combine(this._tempDir, "directory-delete"));
        _ = InvokePrivateStatic<object?>("TryDeleteFile", directory.FullName);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_TestCredentialOverrideWithoutDesktopOverrides_ReturnsNull()
    {
        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_MissingDesktopFiles_ReturnsNull()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = Path.Combine(this._tempDir, "missing-local-state");
        ClaudeProvider.ClaudeDesktopConfigPathOverride = Path.Combine(this._tempDir, "missing-config");

        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_ConfigFileMissing_ReturnsNull()
    {
        var localStatePath = Path.Combine(this._tempDir, "Local State");
        File.WriteAllText(localStatePath, "{}");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = Path.Combine(this._tempDir, "missing-config");

        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_MissingEncryptionKey_ReturnsNull()
    {
        var localStatePath = this.WriteLocalState("{}");
        var configPath = this.WriteDesktopConfig("{}");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = configPath;

        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_MissingTokenCacheProperty_ReturnsNull()
    {
        var key = CreateKey();
        var localStatePath = this.WriteLocalStateWithKey(key);
        var configPath = this.WriteDesktopConfig("{}");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = configPath;

        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_InvalidTokenCacheBase64_ReturnsNull()
    {
        var key = CreateKey();
        var localStatePath = this.WriteLocalStateWithKey(key);
        var configPath = this.WriteDesktopConfig("""{"oauth:tokenCache":"not-base64!"}""");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = configPath;

        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_NullTokenCacheValue_ReturnsNull()
    {
        var key = CreateKey();
        var localStatePath = this.WriteLocalStateWithKey(key);
        var configPath = this.WriteDesktopConfig("""{"oauth:tokenCache":null}""");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = configPath;

        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.Null(result);
    }

    [Fact]
    public void ReadClaudeDesktopTokenCacheCredentials_EncryptedTokenCache_ReturnsParsedCredentials()
    {
        var key = CreateKey();
        var tokenCache = """
            {
              "account:org:https://api.anthropic.com:user:claude_code": {
                "token": "desktop-access-token",
                "refreshToken": "desktop-refresh-token",
                "expiresAt": 1800000000,
                "subscriptionType": "max",
                "rateLimitTier": "tier-2"
              }
            }
            """;
        var encryptedTokenCache = Convert.ToBase64String(CreateAesGcmPayload(tokenCache, key));
        var localStatePath = this.WriteLocalStateWithKey(key);
        var configPath = this.WriteDesktopConfig($$"""{"oauth:tokenCache":"{{encryptedTokenCache}}"}""");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = configPath;

        var result = InvokePrivateInstance<ClaudeProvider.ClaudeCredentials?>(
            this.CreateProvider(),
            "ReadClaudeDesktopTokenCacheCredentials");

        Assert.NotNull(result);
        Assert.Equal("desktop-access-token", result!.AccessToken);
        Assert.Equal("desktop-refresh-token", result.RefreshToken);
        Assert.Equal(1800000000, result.ExpiresAt);
        Assert.Equal("max", result.SubscriptionType);
        Assert.Equal("tier-2", result.RateLimitTier);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_TestPathOverrideWithoutDesktopOverrides_ReturnsNull()
    {
        var result = InvokePrivateInstance<string?>(
            this.CreateProvider(),
            "TryReadClaudeDesktopCookieHeader");

        Assert.Null(result);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_DefaultCredentialPathsWithMissingDesktopFiles_ReturnsNull()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = Path.Combine(this._tempDir, "missing-local-state");
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = Path.Combine(this._tempDir, "missing-cookies");

        var result = InvokePrivateInstance<string?>(
            this.CreateProvider(),
            "TryReadClaudeDesktopCookieHeader");

        Assert.Null(result);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_CookiesFileMissing_ReturnsNull()
    {
        var localStatePath = this.WriteLocalState("{}");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = Path.Combine(this._tempDir, "missing-cookies");

        var result = InvokePrivateInstance<string?>(
            this.CreateProvider(),
            "TryReadClaudeDesktopCookieHeader");

        Assert.Null(result);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_MissingEncryptionKey_ReturnsNull()
    {
        var localStatePath = this.WriteLocalState("{}");
        var cookiesPath = Path.Combine(this._tempDir, "Cookies");
        File.WriteAllText(cookiesPath, string.Empty);
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = cookiesPath;

        var result = InvokePrivateInstance<string?>(
            this.CreateProvider(),
            "TryReadClaudeDesktopCookieHeader");

        Assert.Null(result);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_NoClaudeCookies_ReturnsNull()
    {
        var key = CreateKey();
        var localStatePath = this.WriteLocalStateWithKey(key);
        var cookiesPath = this.CreateCookiesDatabase(
            new CookieRow("example.com", "ignored", "ignored-value", null, 0));
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = cookiesPath;

        var result = InvokePrivateInstance<string?>(
            this.CreateProvider(),
            "TryReadClaudeDesktopCookieHeader");

        Assert.Null(result);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_UnreadableCookiesFile_ReturnsNull()
    {
        var key = CreateKey();
        var localStatePath = this.WriteLocalStateWithKey(key);
        var cookiesPath = Path.Combine(this._tempDir, "Cookies");
        File.WriteAllText(cookiesPath, "not a sqlite database");
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = cookiesPath;

        var result = InvokePrivateInstance<string?>(
            this.CreateProvider(),
            "TryReadClaudeDesktopCookieHeader");

        Assert.Null(result);
    }

    [Fact]
    public void TryReadClaudeDesktopCookieHeader_ValidCookies_ReturnsCookieHeader()
    {
        var key = CreateKey();
        var localStatePath = this.WriteLocalStateWithKey(key);
        var cookiesPath = this.CreateCookiesDatabase(
            new CookieRow("claude.ai", "plainSession", "plain-value", null, 0),
            new CookieRow(
                ".claude.ai",
                "encryptedSession",
                string.Empty,
                CreateAesGcmChromiumCookie(".claude.ai", "encrypted-value", key),
                0));
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = localStatePath;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = cookiesPath;

        var result = InvokePrivateInstance<string?>(
            this.CreateProvider(),
            "TryReadClaudeDesktopCookieHeader");

        Assert.Equal("plainSession=plain-value; encryptedSession=encrypted-value", result);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"os_crypt":{}}""")]
    [InlineData("""{"os_crypt":{"encrypted_key":null}}""")]
    [InlineData("""{"os_crypt":{"encrypted_key":"RFBBUEk="}}""")]
    public void ReadChromiumEncryptionKey_MissingOrInvalidKey_ReturnsNull(string localStateJson)
    {
        var localStatePath = this.WriteLocalState(localStateJson);

        var result = InvokePrivateStatic<byte[]?>("ReadChromiumEncryptionKey", localStatePath);

        Assert.Null(result);
    }

    [Fact]
    public void ReadChromiumEncryptionKey_ValidDpapiLocalState_ReturnsUnprotectedKey()
    {
        var expectedKey = CreateKey();
        var localStatePath = this.WriteLocalStateWithKey(expectedKey);

        var result = InvokePrivateStatic<byte[]?>("ReadChromiumEncryptionKey", localStatePath);

        Assert.Equal(expectedKey, result);
    }

    [Fact]
    public void ReadClaudeDesktopCookies_MixedCookieRows_ReturnsReadableUnexpiredCookies()
    {
        var key = CreateKey();
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
            new CookieRow("claude.ai", "dbNullValue", null, null, futureExpiresUtc),
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

        var result = InvokePrivateStatic<object>("ReadClaudeDesktopCookies", key, NullLogger<ClaudeProvider>.Instance);

        var cookies = ReadCookiePairs(result);
        Assert.Equal(
            [
                ("plainSession", "plain-value"),
                ("encryptedSession", "encrypted-value"),
                ("legacySession", "legacy-value"),
            ],
            cookies);
    }

    [Fact]
    public void DecryptAesGcmString_ShortPayload_ReturnsEmptyString()
    {
        var result = InvokePrivateStatic<string>("DecryptAesGcmString", new byte[30], CreateKey());

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData(typeof(CryptographicException), true)]
    [InlineData(typeof(FormatException), true)]
    [InlineData(typeof(InvalidCastException), true)]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(ArgumentOutOfRangeException), true)]
    [InlineData(typeof(NotSupportedException), true)]
    [InlineData(typeof(IOException), false)]
    public void IsRecoverableCookieReadException_KnownExceptionTypes_ReturnsExpected(
        Type exceptionType,
        bool expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var result = InvokePrivateStatic<bool>("IsRecoverableCookieReadException", exception);

        Assert.Equal(expected, result);
    }
#endif

    private void SetupOverrides()
    {
        ClaudeProvider.CredentialsPathOverride = this._credentialsPath;
        ClaudeProvider.StatsCachePathOverride = this._statsPath;
        ClaudeProvider.ClaudeJsonPathOverride = this._claudeJsonPath;
        ClaudeProvider.EnvironmentAccessTokenOverride = null;
        ClaudeProvider.ResetEnvironmentProvidersForTests();
        ClaudeProvider.ClaudeDesktopLocalStatePathOverride = null;
        ClaudeProvider.ClaudeDesktopCookiesPathOverride = null;
        ClaudeProvider.ClaudeDesktopConfigPathOverride = null;
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;
        ClaudeProvider.WebSessionCachePathOverride = Path.Combine(this._tempDir, "claude-web-session.bin");
    }

#if WINDOWS
    private static T? InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(ClaudeProvider).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T?)method!.Invoke(null, args);
    }

    private static T? InvokePrivateInstance<T>(ClaudeProvider provider, string methodName, params object?[] args)
    {
        var method = typeof(ClaudeProvider).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (T?)method!.Invoke(provider, args);
    }

    private static byte[] CreateKey() =>
        Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private string WriteLocalState(string json)
    {
        var path = Path.Combine(this._tempDir, $"Local State {Guid.NewGuid():N}");
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteLocalStateWithKey(byte[] key)
    {
        var encryptedKey = Convert.ToBase64String(CreateDpapiLocalStateKey(key));
        return this.WriteLocalState($$"""
            {
              "os_crypt": {
                "encrypted_key": "{{encryptedKey}}"
              }
            }
            """);
    }

    private string WriteDesktopConfig(string json)
    {
        var path = Path.Combine(this._tempDir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string CreateCookiesDatabase(params CookieRow[] rows)
    {
        var path = Path.Combine(this._tempDir, $"Cookies-{Guid.NewGuid():N}");
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
        var plaintext = SHA256.HashData(Encoding.UTF8.GetBytes(hostKey))
            .Concat(Encoding.UTF8.GetBytes(value))
            .ToArray();

        return CreateAesGcmPayload(plaintext, key, prefix);
    }

    private static byte[] CreateAesGcmPayload(string value, byte[] key) =>
        CreateAesGcmPayload(Encoding.UTF8.GetBytes(value), key, "v10");

    private static byte[] CreateAesGcmPayload(byte[] plaintext, byte[] key, string prefix)
    {
        const int tagLength = 16;

        var nonce = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
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
#endif

    private ClaudeProvider CreateProvider() =>
        this.CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

    private ClaudeProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        return new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            new HttpClientFactoryStub(new DelegatingHandler(handler)),
            settings);
    }

    private void WriteCredentials(DateTimeOffset expiresAt, string accessToken, string? refreshToken)
    {
        var refreshJson = refreshToken is null ? string.Empty : $",\"refreshToken\":\"{refreshToken}\"";
        var json = $"{{\"claudeAiOauth\":{{\"subscriptionType\":\"pro\",\"accessToken\":\"{accessToken}\",\"expiresAt\":{expiresAt.ToUnixTimeSeconds()}{refreshJson}}}}}";
        File.WriteAllText(
            this._credentialsPath,
            json);
    }

    private static ClaudeProvider.ClaudeCredentials? ReadEnvironmentCredentials()
    {
        var method = typeof(ClaudeProvider).GetMethod(
            "ReadEnvironmentCredentials",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (ClaudeProvider.ClaudeCredentials?)method!.Invoke(null, null);
    }

    private sealed class DelegatingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
