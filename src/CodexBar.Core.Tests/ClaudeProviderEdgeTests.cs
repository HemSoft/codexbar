// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net.Http.Headers;
using CodexBar.Core.Providers.Claude;

/// <summary>
/// Edge-case tests for ClaudeProvider internal helper methods that exercise
/// uncommon branches not fully covered in the main test suites.
/// </summary>
public class ClaudeProviderEdgeTests
{
    // --- NormalizeEpochToSeconds ---
    [Fact]
    public void NormalizeEpochToSeconds_MillisecondValue_DividesBy1000()
    {
        // 1_700_000_000_000 ms → 1_700_000_000 s
        var result = ClaudeProvider.NormalizeEpochToSeconds(1_700_000_000_000);
        Assert.Equal(1_700_000_000, result);
    }

    [Fact]
    public void NormalizeEpochToSeconds_SecondValue_ReturnsUnchanged()
    {
        var result = ClaudeProvider.NormalizeEpochToSeconds(1_700_000_000);
        Assert.Equal(1_700_000_000, result);
    }

    [Fact]
    public void NormalizeEpochToSeconds_ExactBoundary_TreatsAsSeconds()
    {
        // 1_000_000_000_000 is exactly the threshold; it should be treated as ms
        var result = ClaudeProvider.NormalizeEpochToSeconds(1_000_000_000_001);
        Assert.Equal(1_000_000_000, result);
    }

    [Fact]
    public void NormalizeEpochToSeconds_Zero_ReturnsZero()
    {
        var result = ClaudeProvider.NormalizeEpochToSeconds(0);
        Assert.Equal(0, result);
    }

    // --- FormatUsageLabel ---
    [Fact]
    public void FormatUsageLabel_ZeroCostZeroTokens_ReturnsOnlyPlan()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 0, 0, null);
        Assert.Equal("Pro plan", result);
    }

    [Fact]
    public void FormatUsageLabel_NullAccountInfo_NoExtraUsageNote()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 1000, 5.0, null);
        Assert.DoesNotContain("extra usage", result);
        Assert.Contains("$5.00", result);
    }

    [Fact]
    public void FormatUsageLabel_ExtraUsageDisabled_NoExtraUsageNote()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = false };
        var result = ClaudeProvider.FormatUsageLabel("Pro", 1000, 5.0, accountInfo);
        Assert.DoesNotContain("extra usage", result);
    }

    [Fact]
    public void FormatUsageLabel_CostOverridesTokens_ShowsCostOnly()
    {
        var result = ClaudeProvider.FormatUsageLabel("Enterprise", 500_000, 10.50, null);

        Assert.Contains("$10.50", result);
        Assert.DoesNotContain("tokens", result);
    }

    [Fact]
    public void FormatUsageLabel_ZeroCostWithTokens_ShowsTokenCount()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 2_500_000, 0, null);

        Assert.Contains("M tokens", result);
        Assert.DoesNotContain("$", result);
    }

    // --- FormatBarReset edge cases ---
    [Fact]
    public void FormatBarReset_VeryFarFuture_ReturnsDays()
    {
        var farFuture = DateTimeOffset.UtcNow.AddDays(30);
        var epoch = farFuture.ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);

        Assert.StartsWith("Resets", result);
        Assert.Contains("d", result);
    }

    [Fact]
    public void FormatBarReset_LessThanOneHour_ReturnsMinutes()
    {
        var soon = DateTimeOffset.UtcNow.AddMinutes(45);
        var epoch = soon.ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);

        Assert.Contains("m", result);
        Assert.DoesNotContain("h", result);
    }

    // --- FormatResetCountdown edge cases ---
    [Fact]
    public void FormatResetCountdown_LessThanOneMinute_ReturnsZeroMinutes()
    {
        var almostNow = DateTimeOffset.UtcNow.AddSeconds(30);
        var epoch = almostNow.ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "5-hour limit");

        Assert.Contains("5-hour limit", result);
        Assert.Contains("0m", result);
    }

    [Fact]
    public void FormatResetCountdown_ExactlyOneDay_ReturnsDaysAndHours()
    {
        var inOneDay = DateTimeOffset.UtcNow.AddHours(25);
        var epoch = inOneDay.ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Weekly");

        Assert.Contains("Weekly resets in 1d", result);
    }

    // --- FormatTokenCount edge cases ---
    [Fact]
    public void FormatTokenCount_ExactlyOneMillion_ReturnsM()
    {
        var result = ClaudeProvider.FormatTokenCount(1_000_000);
        Assert.Contains("M tokens", result);
    }

    [Fact]
    public void FormatTokenCount_BetweenThousandAndMillion_ReturnsK()
    {
        var result = ClaudeProvider.FormatTokenCount(500_000);
        Assert.Contains("K tokens", result);
    }

    [Fact]
    public void FormatTokenCount_SmallValue_ReturnsRawCount()
    {
        var result = ClaudeProvider.FormatTokenCount(999);
        Assert.Equal("999 tokens", result);
    }

    // --- ParseRateLimitHeaders edge cases ---
    [Fact]
    public void ParseRateLimitHeaders_OnlyFiveHourUtilization_ReturnsWithDefaults()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.5");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0.5, result!.FiveHourUtilization);
        Assert.Equal(0, result.FiveHourReset);
        Assert.Equal(0, result.SevenDayUtilization);
        Assert.Equal("unknown", result.FiveHourStatus);
    }

    [Fact]
    public void ParseRateLimitHeaders_OnlySevenDayUtilization_ReturnsWithDefaults()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.3");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0.3, result!.SevenDayUtilization);
        Assert.Equal(0, result.SevenDayReset);
        Assert.Equal(0, result.FiveHourUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_AllHeaders_ParsesComplete()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.75");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1700000000");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.40");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1700500000");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "active");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0.75, result!.FiveHourUtilization);
        Assert.Equal(1700000000, result.FiveHourReset);
        Assert.Equal("active", result.FiveHourStatus);
        Assert.Equal(0.40, result.SevenDayUtilization);
        Assert.Equal(1700500000, result.SevenDayReset);
        Assert.Equal("active", result.SevenDayStatus);
    }

    [Fact]
    public void ParseRateLimitHeaders_MalformedUtilization_DefaultsToZero()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "not-a-number");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.5");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0, result!.FiveHourUtilization);
        Assert.Equal(0.5, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_MalformedReset_DefaultsToZero()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.5");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "abc");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0, result!.FiveHourReset);
    }

    // --- BuildWeeklySnapshot edge cases ---
    [Fact]
    public void BuildWeeklySnapshot_ZeroReset_NoResetDescription()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.3,
            SevenDayReset = 0,
        };

        var result = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(result);
        Assert.Null(result!.ResetDescription);
        Assert.Null(result.ResetsAt);
    }

    [Fact]
    public void BuildWeeklySnapshot_WithReset_HasResetDescription()
    {
        var futureReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds();
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.7,
            SevenDayReset = futureReset,
        };

        var result = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(result);
        Assert.NotNull(result!.ResetDescription);
        Assert.NotNull(result.ResetsAt);
    }

    // --- BuildUsageBars edge cases ---
    [Fact]
    public void BuildUsageBars_ZeroResets_NullResetDescriptions()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = 0,
            SevenDayUtilization = 0.3,
            SevenDayReset = 0,
        };

        var bars = ClaudeProvider.BuildUsageBars(limits);

        Assert.Equal(2, bars.Count);
        Assert.Null(bars[0].ResetDescription);
        Assert.Null(bars[0].ResetsAt);
        Assert.Null(bars[1].ResetDescription);
        Assert.Null(bars[1].ResetsAt);
    }

    [Fact]
    public void BuildUsageBars_WithResets_HasResetInfo()
    {
        var fiveHourReset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds();
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.8,
            FiveHourReset = fiveHourReset,
            SevenDayUtilization = 0.4,
            SevenDayReset = weeklyReset,
        };

        var bars = ClaudeProvider.BuildUsageBars(limits);

        Assert.Equal(2, bars.Count);
        Assert.NotNull(bars[0].ResetDescription);
        Assert.NotNull(bars[0].ResetsAt);
        Assert.NotNull(bars[1].ResetDescription);
        Assert.NotNull(bars[1].ResetsAt);
    }

    // --- ResolvePricing edge cases ---
    [Fact]
    public void ResolvePricing_EmptyString_ReturnsSonnetFallback()
    {
        var pricing = ClaudeProvider.ResolvePricing(string.Empty);
        Assert.Equal(3.0, pricing.InputPerMTok);
    }

    [Fact]
    public void ResolvePricing_CompletelyUnknownModel_ReturnsSonnetFallback()
    {
        var pricing = ClaudeProvider.ResolvePricing("gpt-4o-2025");
        Assert.Equal(3.0, pricing.InputPerMTok);
    }

    // --- CalculateEquivalentCost with mixed models ---
    [Fact]
    public void CalculateEquivalentCost_SingleModel_CalculatesCorrectly()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-6",
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
        });

        var result = ClaudeProvider.CalculateEquivalentCost(stats);

        // Input: 1M * 3.0/M = 3.0, Output: 0.5M * 15.0/M = 7.5 → Total: 10.5
        Assert.Equal(10.5, result, 2);
    }

    // --- CalculateTotalTokens with all token types ---
    [Fact]
    public void CalculateTotalTokens_AllTokenTypes_SumsAll()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-6",
            InputTokens = 100,
            OutputTokens = 200,
            CacheReadInputTokens = 300,
            CacheCreationInputTokens = 400,
        });

        var result = ClaudeProvider.CalculateTotalTokens(stats);

        Assert.Equal(1000, result);
    }

    [Fact]
    public void CalculateTotalTokens_MultipleModels_SumsAcrossModels()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-6",
            InputTokens = 100,
            OutputTokens = 200,
        });
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-haiku-4-5",
            InputTokens = 300,
            OutputTokens = 400,
        });

        var result = ClaudeProvider.CalculateTotalTokens(stats);

        Assert.Equal(1000, result);
    }

    // --- UnifiedRateLimits default values ---
    [Fact]
    public void UnifiedRateLimits_DefaultStatus_IsUnknown()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits();

        Assert.Equal("unknown", limits.FiveHourStatus);
        Assert.Equal("unknown", limits.SevenDayStatus);
    }
}
