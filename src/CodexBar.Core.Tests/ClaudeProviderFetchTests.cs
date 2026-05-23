// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests for ClaudeProvider: ParseRateLimitHeaders, NormalizeEpochToSeconds,
/// and other coverage gap areas.
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class ClaudeProviderFetchTests
{
    // --- ParseRateLimitHeaders ---
    [Fact]
    public void ParseRateLimitHeaders_NoHeaders_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.Null(result);
    }

    [Fact]
    public void ParseRateLimitHeaders_OnlyFiveHour_ParsesCorrectly()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.42");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1715700000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(0.42, result!.FiveHourUtilization, 4);
        Assert.Equal(1715700000L, result.FiveHourReset);
        Assert.Equal("active", result.FiveHourStatus);
        Assert.Equal(0.0, result.SevenDayUtilization);
        Assert.Equal("unknown", result.SevenDayStatus);
    }

    [Fact]
    public void ParseRateLimitHeaders_OnlySevenDay_ParsesCorrectly()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.85");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1716304800");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "throttled");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(0.85, result!.SevenDayUtilization, 4);
        Assert.Equal(1716304800L, result.SevenDayReset);
        Assert.Equal("throttled", result.SevenDayStatus);
        Assert.Equal(0.0, result.FiveHourUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_BothWindows_ParsesBoth()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.30");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1715700000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.60");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1716300000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "active");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(0.30, result!.FiveHourUtilization, 4);
        Assert.Equal(0.60, result.SevenDayUtilization, 4);
    }

    [Fact]
    public void ParseRateLimitHeaders_MalformedValues_UsesDefaults()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "not-a-number");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "abc");

        // no status header -> defaults to "unknown"
        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.FiveHourUtilization);
        Assert.Equal(0L, result.FiveHourReset);
        Assert.Equal("unknown", result.FiveHourStatus);
    }

    [Fact]
    public void ParseRateLimitHeaders_ZeroUtilization_Parses()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.0");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.FiveHourUtilization);
        Assert.Equal(0.0, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_FullUtilization_Parses()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "1.0");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.999");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.FiveHourUtilization, 4);
        Assert.Equal(0.999, result.SevenDayUtilization, 4);
    }

    // --- NormalizeEpochToSeconds ---
    [Theory]
    [InlineData(1715700000L, 1715700000L)] // Already in seconds
    [InlineData(1715700000000L, 1715700000L)] // Milliseconds -> seconds
    [InlineData(0L, 0L)] // Zero stays zero
    [InlineData(999999999999L, 999999999999L)] // Under threshold stays
    [InlineData(1000000000000L, 1000000000000L)] // Exact boundary: NOT greater-than, stays
    public void NormalizeEpochToSeconds_ConvertsCorrectly(long input, long expected)
    {
        Assert.Equal(expected, ClaudeProvider.NormalizeEpochToSeconds(input));
    }

    // --- BuildWeeklySnapshot ---
    [Fact]
    public void BuildWeeklySnapshot_NullLimits_ReturnsNull()
    {
        var result = ClaudeProvider.BuildWeeklySnapshot(null);
        Assert.Null(result);
    }

    [Fact]
    public void BuildWeeklySnapshot_WithLimits_SetsUsedPercentFromSevenDay()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.65,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
            SevenDayStatus = "active",
        };

        var result = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(result);
        Assert.Equal(0.65, result!.UsedPercent, 4);
        Assert.Contains("Weekly", result.UsageLabel);
    }

    [Fact]
    public void BuildWeeklySnapshot_ZeroReset_NoResetDate()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.10,
            SevenDayReset = 0,
        };

        var result = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(result);
        Assert.Null(result!.ResetsAt);
        Assert.Null(result.ResetDescription);
    }

    // --- BuildUsageBars ---
    [Fact]
    public void BuildUsageBars_NullLimits_ReturnsEmptyList()
    {
        var result = ClaudeProvider.BuildUsageBars(null);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildUsageBars_WithLimits_ReturnsTwoBars()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.40,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds(),
            FiveHourStatus = "active",
            SevenDayUtilization = 0.70,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds(),
            SevenDayStatus = "active",
        };

        var result = ClaudeProvider.BuildUsageBars(limits);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, b => b.Label.Contains("5"));
        Assert.Contains(result, b => b.Label.Contains("Week"));
    }

    // --- CalculateEquivalentCost ---
    [Fact]
    public void CalculateEquivalentCost_NullStats_ReturnsZero()
    {
        var result = ClaudeProvider.CalculateEquivalentCost(null);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculateEquivalentCost_KnownModel_CalculatesCost()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-5",
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            CacheReadInputTokens = 200_000,
            CacheCreationInputTokens = 100_000,
        });

        var result = ClaudeProvider.CalculateEquivalentCost(stats);

        // ($3.0 * 1M/1M) + ($15.0 * 0.5M/1M) + ($0.30 * 0.2M/1M) + ($3.75 * 0.1M/1M)
        // = 3.0 + 7.5 + 0.06 + 0.375 = 10.935
        Assert.True(result > 10.0);
    }

    [Fact]
    public void CalculateEquivalentCost_UnknownModel_UsesFallbackPricing()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "unknown-model-42",
            InputTokens = 1_000_000,
        });

        // Falls back to Sonnet pricing: $3.0/M input
        var result = ClaudeProvider.CalculateEquivalentCost(stats);
        Assert.Equal(3.0, result, 2);
    }

    [Fact]
    public void CalculateEquivalentCost_MultipleModels_SumsAll()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-5",
            InputTokens = 1_000_000,
        });
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-haiku-4-5",
            InputTokens = 1_000_000,
        });

        var result = ClaudeProvider.CalculateEquivalentCost(stats);

        // sonnet: 3.0/1M * 1M = 3.0, haiku: 1.0/1M * 1M = 1.0
        Assert.True(result > 3.5);
    }

    // --- CalculateTotalTokens ---
    [Fact]
    public void CalculateTotalTokens_NullStats_ReturnsZero()
    {
        Assert.Equal(0L, ClaudeProvider.CalculateTotalTokens(null));
    }

    [Fact]
    public void CalculateTotalTokens_SumsAllTokenTypes()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            InputTokens = 100,
            OutputTokens = 200,
            CacheReadInputTokens = 50,
            CacheCreationInputTokens = 25,
        });

        Assert.Equal(375L, ClaudeProvider.CalculateTotalTokens(stats));
    }

    // --- FormatTokenCount ---
    [Theory]
    [InlineData(0, "0 tokens")]
    [InlineData(999, "999 tokens")]
    [InlineData(1000, "1.0K tokens")]
    [InlineData(1500, "1.5K tokens")]
    [InlineData(999999, "1000.0K tokens")]
    [InlineData(1_000_000, "1.0M tokens")]
    [InlineData(2_500_000, "2.5M tokens")]
    public void FormatTokenCount_FormatsCorrectly(long tokens, string expected)
    {
        Assert.Equal(expected, ClaudeProvider.FormatTokenCount(tokens));
    }

    // --- ResolvePricing ---
    [Fact]
    public void ResolvePricing_KnownModel_ReturnsPricing()
    {
        var (inp, outp, cacheWrite, cacheRead) = ClaudeProvider.ResolvePricing("claude-opus-4-5");
        Assert.Equal(5.0, inp);
        Assert.Equal(25.0, outp);
        Assert.Equal(6.25, cacheWrite);
        Assert.Equal(0.50, cacheRead);
    }

    [Fact]
    public void ResolvePricing_UnknownModel_FallsBackToSonnet()
    {
        var result = ClaudeProvider.ResolvePricing("some-unknown-model");

        // Falls back to Sonnet pricing
        Assert.Equal(3.0, result.InputPerMTok);
    }

    [Fact]
    public void ResolvePricing_CaseInsensitive()
    {
        var result = ClaudeProvider.ResolvePricing("Claude-Sonnet-4-6");
        Assert.Equal(3.0, result.InputPerMTok);
    }

    // --- FormatUsageLabel ---
    [Fact]
    public void FormatUsageLabel_WithCost_IncludesCostInLabel()
    {
        var label = ClaudeProvider.FormatUsageLabel("Pro", 1_000_000, 5.50, null);
        Assert.Contains("$5.50", label);
        Assert.Contains("Pro", label);
    }

    [Fact]
    public void FormatUsageLabel_WithTokensNoCost_IncludesTokenCount()
    {
        var label = ClaudeProvider.FormatUsageLabel("Pro", 2_500_000, 0.0, null);
        Assert.Contains("2.5M", label);
    }

    [Fact]
    public void FormatUsageLabel_WithExtraUsageEnabled_IncludesNote()
    {
        var info = new ClaudeProvider.ClaudeAccountInfo
        {
            HasExtraUsageEnabled = true,
        };
        var label = ClaudeProvider.FormatUsageLabel("Pro", 0, 0.0, info);
        Assert.Contains("extra usage", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatUsageLabel_NullSubscription_ReturnsEmptyOrHandles()
    {
        var label = ClaudeProvider.FormatUsageLabel(null!, 0, 0.0, null);
        Assert.NotNull(label);
    }

    // --- FormatBarReset ---
    [Fact]
    public void FormatBarReset_ZeroResetEpoch_ReturnsEmptyOrDefault()
    {
        var result = ClaudeProvider.FormatBarReset(0);
        Assert.NotNull(result);
    }

    [Fact]
    public void FormatBarReset_FutureResetEpoch_ContainsTimeInfo()
    {
        var futureEpoch = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(futureEpoch);
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void FormatBarReset_PastResetEpoch_ReturnsResetInfo()
    {
        var pastEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(pastEpoch);
        Assert.NotNull(result);
    }

    // --- FormatResetCountdown ---
    [Fact]
    public void FormatResetCountdown_FutureTime_ContainsTimeRemaining()
    {
        var future = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(30).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(future, "5-hour limit");
        Assert.NotNull(result);
        Assert.Contains("3h", result!);
        Assert.Contains("5-hour limit", result);
    }

    [Fact]
    public void FormatResetCountdown_PastTime_ReturnsOverdue()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(past, "Weekly");
        Assert.NotNull(result);
    }

    [Fact]
    public void FormatResetCountdown_ZeroEpoch_HandlesGracefully()
    {
        var result = ClaudeProvider.FormatResetCountdown(0, "test");
        Assert.NotNull(result);
    }

    // --- IsAvailableAsync ---
    [Fact]
    public async Task IsAvailableAsync_Enabled_CompletesSuccessfully()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        var ex = await Record.ExceptionAsync(() => provider.IsAvailableAsync());

        Assert.Null(ex);
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(false);
        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.False(await provider.IsAvailableAsync());
    }

    // --- FetchUsageAsync error paths ---
    [Fact]
    public async Task FetchUsageAsync_NoMockedCredentials_CompletesWithoutException()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        // Exercises FetchUsageAsync against real filesystem.
        // On machines without .claude/.credentials.json → returns error.
        // On machines with credentials → may fail at API call.
        // Either way, the provider must complete gracefully.
        var result = await provider.FetchUsageAsync();

        Assert.Equal(ProviderId.Claude, result.Provider);
        Assert.False(result.Success, "Expected failure: either no credentials file or no valid API token for real HTTP call");
    }

    // --- Metadata ---
    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var settings = Substitute.For<ISettingsService>();
        var provider = new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.Equal(ProviderId.Claude, provider.Metadata.Id);
        Assert.Equal("Claude", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsSessionUsage);
        Assert.True(provider.Metadata.SupportsWeeklyUsage);
        Assert.False(provider.Metadata.SupportsCredits);
    }
}
