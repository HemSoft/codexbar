// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;

/// <summary>
/// Mutation-killing tests for ClaudeProvider static methods.
/// Targets: FormatBarReset, FormatResetCountdown, FormatTokenCount,
/// FormatUsageLabel, BuildSessionSnapshot, BuildWeeklySnapshot,
/// BuildUsageBars, NormalizeEpochToSeconds, ResolvePricing,
/// CalculateTotalTokens, CalculateEquivalentCost, FormatSubscriptionType,
/// ParseRateLimitHeaders.
/// </summary>
public class ClaudeProviderMutationTests
{
    // === NormalizeEpochToSeconds ===
    [Theory]
    [InlineData(1_000_000_000_001, 1_000_000_000)] // just above threshold → divide by 1000
    [InlineData(1_700_000_000_000, 1_700_000_000)] // typical millisecond timestamp
    [InlineData(1_000_000_000_000, 1_000_000_000_000)] // exactly at threshold, returned as-is
    [InlineData(999_999_999_999, 999_999_999_999)] // below threshold, returned as-is
    [InlineData(1_000_000_000, 1_000_000_000)] // seconds value, returned as-is
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void NormalizeEpochToSeconds_ConvertsCorrectly(long input, long expected)
    {
        Assert.Equal(expected, ClaudeProvider.NormalizeEpochToSeconds(input));
    }

    // === FormatSubscriptionType ===
    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("  ", "Unknown")]
    [InlineData("pro", "Pro")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("max", "Max")]
    public void FormatSubscriptionType_FormatsCorrectly(string? input, string expected)
    {
        Assert.Equal(expected, ClaudeProvider.FormatSubscriptionType(input));
    }

    // === FormatTokenCount ===
    [Theory]
    [InlineData(0, "0 tokens")]
    [InlineData(999, "999 tokens")]
    [InlineData(1000, "1.0K tokens")]
    [InlineData(1500, "1.5K tokens")]
    [InlineData(999_999, "1000.0K tokens")]
    [InlineData(1_000_000, "1.0M tokens")]
    [InlineData(5_500_000, "5.5M tokens")]
    [InlineData(999_999_999, "1000.0M tokens")]
    [InlineData(1_000_000_000, "1.0B tokens")]
    [InlineData(2_500_000_000, "2.5B tokens")]
    public void FormatTokenCount_FormatsCorrectly(long input, string expected)
    {
        Assert.Equal(expected, ClaudeProvider.FormatTokenCount(input));
    }

    // === FormatBarReset ===
    [Fact]
    public void FormatBarReset_PastEpoch_ReturnsResetsNow()
    {
        var pastEpoch = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(pastEpoch);
        Assert.Equal("Resets now", result);
    }

    [Fact]
    public void FormatBarReset_LessThan1Hour_ReturnsMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.StartsWith("Resets ", result);
        Assert.EndsWith("m", result);
    }

    [Fact]
    public void FormatBarReset_Between1And24Hours_ReturnsHours()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.StartsWith("Resets ", result);
        Assert.EndsWith("h", result);
    }

    [Fact]
    public void FormatBarReset_MoreThan1Day_ReturnsDays()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.StartsWith("Resets ", result);
        Assert.EndsWith("d", result);
    }

    // === FormatResetCountdown ===
    [Fact]
    public void FormatResetCountdown_PastEpoch_ReturnsResetsNow()
    {
        var pastEpoch = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(pastEpoch, "5-hour limit");
        Assert.Equal("5-hour limit resets now", result);
    }

    [Fact]
    public void FormatResetCountdown_LessThan1Hour_ReturnsMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Weekly");
        Assert.StartsWith("Weekly resets in ", result);
        Assert.EndsWith("m", result);
    }

    [Fact]
    public void FormatResetCountdown_Between1And24Hours_ReturnsHoursAndMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(15).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "5-hour limit");
        Assert.StartsWith("5-hour limit resets in ", result);
        Assert.Contains("h", result);
        Assert.Contains("m", result);
    }

    [Fact]
    public void FormatResetCountdown_MoreThan1Day_ReturnsDaysAndHours()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(3).AddHours(5).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Weekly");
        Assert.StartsWith("Weekly resets in ", result);
        Assert.Contains("d", result);
        Assert.Contains("h", result);
    }

    // === FormatUsageLabel ===
    [Fact]
    public void FormatUsageLabel_WithEquivalentCost_ShowsCost()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 1000, 5.50, null);
        Assert.Contains("Pro plan", result);
        Assert.Contains("~$5.50 equiv.", result);
    }

    [Fact]
    public void FormatUsageLabel_ZeroCost_WithTokens_ShowsTokens()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 1_500_000, 0, null);
        Assert.Contains("Pro plan", result);
        Assert.Contains("1.5M tokens", result);
    }

    [Fact]
    public void FormatUsageLabel_ZeroCost_ZeroTokens_ShowsOnlyPlan()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 0, 0, null);
        Assert.Equal("Pro plan", result);
    }

    [Fact]
    public void FormatUsageLabel_WithExtraUsage_ShowsExtraUsageOn()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };
        var result = ClaudeProvider.FormatUsageLabel("Max", 0, 1.0, accountInfo);
        Assert.Contains("extra usage on", result);
    }

    [Fact]
    public void FormatUsageLabel_WithoutExtraUsage_DoesNotShowExtraUsage()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = false };
        var result = ClaudeProvider.FormatUsageLabel("Max", 0, 1.0, accountInfo);
        Assert.DoesNotContain("extra usage on", result);
    }

    [Fact]
    public void FormatUsageLabel_NullAccountInfo_DoesNotShowExtraUsage()
    {
        var result = ClaudeProvider.FormatUsageLabel("Max", 0, 1.0, null);
        Assert.DoesNotContain("extra usage on", result);
    }

    // === BuildSessionSnapshot ===
    [Fact]
    public void BuildSessionSnapshot_NullLimits_ReturnsFallbackLabel()
    {
        var result = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 0, 0, null);
        Assert.True(result.IsUnlimited);
        Assert.Contains("Rate limits unavailable", result.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshot_NullLimits_WithTokens_IncludesTokensInLabel()
    {
        var result = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 1_000_000, 0, null);
        Assert.True(result.IsUnlimited);
        Assert.Contains("1.0M tokens", result.UsageLabel);
        Assert.Contains("Rate limits unavailable", result.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshot_WithLimits_ReturnsCorrectSnapshot()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.75,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.3,
        };
        var result = ClaudeProvider.BuildSessionSnapshot(limits, "Pro", 0, 2.5, null);

        Assert.False(result.IsUnlimited);
        Assert.Equal(0.75, result.UsedPercent);
        Assert.Contains("Pro plan", result.UsageLabel);
        Assert.Contains("~$2.50 equiv.", result.UsageLabel);
        Assert.NotNull(result.ResetDescription);
    }

    [Fact]
    public void BuildSessionSnapshot_ZeroFiveHourReset_NoResetDescription()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = 0,
        };
        var result = ClaudeProvider.BuildSessionSnapshot(limits, "Pro", 0, 0, null);

        Assert.Null(result.ResetDescription);
        Assert.Null(result.ResetsAt);
    }

    // === BuildWeeklySnapshot ===
    [Fact]
    public void BuildWeeklySnapshot_WithLimits_ReturnsCorrectSnapshot()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.6,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
        };
        var result = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(result);
        Assert.Equal(0.6, result!.UsedPercent);
        Assert.Contains("Weekly · all models:", result.UsageLabel);
        Assert.False(result.IsUnlimited);
        Assert.NotNull(result.ResetsAt);
        Assert.NotNull(result.ResetDescription);
    }

    [Fact]
    public void BuildWeeklySnapshot_ZeroSevenDayReset_NoResetInfo()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.4,
            SevenDayReset = 0,
        };
        var result = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(result);
        Assert.Null(result!.ResetsAt);
        Assert.Null(result.ResetDescription);
    }

    // === BuildUsageBars ===
    [Fact]
    public void BuildUsageBars_ZeroResets_NullResetInfo()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.2,
            FiveHourReset = 0,
            SevenDayUtilization = 0.1,
            SevenDayReset = 0,
        };
        var result = ClaudeProvider.BuildUsageBars(limits);

        Assert.Equal(2, result.Count);
        Assert.Null(result[0].ResetDescription);
        Assert.Null(result[0].ResetsAt);
        Assert.Null(result[1].ResetDescription);
        Assert.Null(result[1].ResetsAt);
    }

    // === ResolvePricing ===
    [Fact]
    public void ResolvePricing_ExactMatch_ReturnsCorrect()
    {
        var (input, output, cacheWrite, cacheRead) = ClaudeProvider.ResolvePricing("claude-opus-4-7");
        Assert.Equal(5.0, input);
        Assert.Equal(25.0, output);
        Assert.Equal(6.25, cacheWrite);
        Assert.Equal(0.50, cacheRead);
    }

    [Fact]
    public void ResolvePricing_HaikuModel_ReturnsHaikuPricing()
    {
        var (input, output, _, _) = ClaudeProvider.ResolvePricing("claude-haiku-4-5");
        Assert.Equal(1.0, input);
        Assert.Equal(5.0, output);
    }

    [Fact]
    public void ResolvePricing_UnknownModelContainsOpus_ReturnsOpusPricing()
    {
        var (input, output, _, _) = ClaudeProvider.ResolvePricing("my-custom-opus-model");
        Assert.Equal(5.0, input);
        Assert.Equal(25.0, output);
    }

    [Fact]
    public void ResolvePricing_UnknownModelContainsHaiku_ReturnsHaikuPricing()
    {
        var (input, output, _, _) = ClaudeProvider.ResolvePricing("some-haiku-variant");
        Assert.Equal(1.0, input);
        Assert.Equal(5.0, output);
    }

    [Fact]
    public void ResolvePricing_CompletelyUnknown_ReturnsSonnetDefault()
    {
        var (input, output, _, _) = ClaudeProvider.ResolvePricing("unknown-model-xyz");
        Assert.Equal(3.0, input);
        Assert.Equal(15.0, output);
    }

    [Fact]
    public void ResolvePricing_PrefixMatch_LongestWins()
    {
        // "claude-sonnet-4-6" is exact match → should return Sonnet pricing
        var (input, _, _, _) = ClaudeProvider.ResolvePricing("claude-sonnet-4-6");
        Assert.Equal(3.0, input);
    }

    // === CalculateTotalTokens ===
    [Fact]
    public void CalculateTotalTokens_EmptyModelUsages_ReturnsZero()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        Assert.Equal(0, ClaudeProvider.CalculateTotalTokens(stats));
    }

    // === CalculateEquivalentCost ===
    [Fact]
    public void CalculateEquivalentCost_CalculatesCorrectCost()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-6",
            InputTokens = 1_000_000, // 1M * $3/M = $3.00
            OutputTokens = 1_000_000, // 1M * $15/M = $15.00
            CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0,
        });

        var cost = ClaudeProvider.CalculateEquivalentCost(stats);
        Assert.Equal(18.0, cost, 2);
    }

    [Fact]
    public void CalculateEquivalentCost_IncludesCacheTokens()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-6",
            InputTokens = 0,
            OutputTokens = 0,
            CacheReadInputTokens = 1_000_000, // 1M * $0.30/M = $0.30
            CacheCreationInputTokens = 1_000_000, // 1M * $3.75/M = $3.75
        });

        var cost = ClaudeProvider.CalculateEquivalentCost(stats);
        Assert.Equal(4.05, cost, 2);
    }

    // === ParseRateLimitHeaders ===
    [Fact]
    public void ParseRateLimitHeaders_NoHeaders_ReturnsNull()
    {
        using var response = new HttpResponseMessage();
        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.Null(result);
    }

    [Fact]
    public void ParseRateLimitHeaders_WithFiveHourOnly_ReturnsLimits()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.75");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1700000000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.NotNull(result);
        Assert.Equal(0.75, result!.FiveHourUtilization);
        Assert.Equal(1700000000, result.FiveHourReset);
        Assert.Equal("active", result.FiveHourStatus);
        Assert.Equal(0.0, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_WithBothHeaders_ReturnsFull()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.5");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1700000000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.3");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1700100000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "normal");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.NotNull(result);
        Assert.Equal(0.5, result!.FiveHourUtilization);
        Assert.Equal(0.3, result.SevenDayUtilization);
        Assert.Equal(1700100000, result.SevenDayReset);
        Assert.Equal("normal", result.SevenDayStatus);
    }

    [Fact]
    public void ParseRateLimitHeaders_InvalidValues_DefaultsToZero()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "not-a-number");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "bad");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.NotNull(result);
        Assert.Equal(0.0, result!.FiveHourUtilization);
        Assert.Equal(0, result.FiveHourReset);
    }

    [Fact]
    public void ParseRateLimitHeaders_MissingStatusHeader_DefaultsToUnknown()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.5");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);
        Assert.NotNull(result);
        Assert.Equal("unknown", result!.FiveHourStatus);
        Assert.Equal("unknown", result.SevenDayStatus);
    }

    // === BuildRateLimitProbeRequest ===
    [Fact]
    public void BuildRateLimitProbeRequest_SetsCorrectHeaders()
    {
        var request = ClaudeProvider.BuildRateLimitProbeRequest("test-token");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
        Assert.NotNull(request.Content);
    }

    // === BuildSessionSnapshotFromLimits ===
    [Fact]
    public void BuildSessionSnapshotFromLimits_ExtraUsageEnabled_ShowsInLabel()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
        };
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };

        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 0, accountInfo);
        Assert.Contains("extra usage on", result.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_EquivalentCostOverTokens_PrefersEquivalentCost()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = 0,
        };

        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 1_000_000, 5.0, null);
        Assert.Contains("~$5.00 equiv.", result.UsageLabel);
        Assert.DoesNotContain("tokens", result.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_ZeroCostWithTokens_ShowsTokens()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = 0,
        };

        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 1_000_000, 0, null);
        Assert.Contains("1.0M tokens", result.UsageLabel);
    }
}
