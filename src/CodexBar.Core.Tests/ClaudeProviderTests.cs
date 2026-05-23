// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Providers.Claude;
using Xunit;

public class ClaudeProviderTests
{
    [Fact]
    public void FormatTokenCount_Billions_ReturnsB() => Assert.Equal("1.5B tokens", ClaudeProvider.FormatTokenCount(1_500_000_000));

    [Fact]
    public void FormatTokenCount_Millions_ReturnsM() => Assert.Equal("2.5M tokens", ClaudeProvider.FormatTokenCount(2_500_000));

    [Fact]
    public void FormatTokenCount_Thousands_ReturnsK() => Assert.Equal("3.5K tokens", ClaudeProvider.FormatTokenCount(3_500));

    [Fact]
    public void FormatTokenCount_Small_ReturnsRaw() => Assert.Equal("42 tokens", ClaudeProvider.FormatTokenCount(42));

    [Fact]
    public void FormatUsageLabel_WithCost_ReturnsCost()
    {
        var label = ClaudeProvider.FormatUsageLabel("Pro", 0, 1.23, null);
        Assert.Equal("Pro plan · ~$1.23 equiv.", label);
    }

    [Fact]
    public void FormatUsageLabel_WithTokens_ReturnsTokens()
    {
        var label = ClaudeProvider.FormatUsageLabel("Free", 1500, 0, null);
        Assert.Equal("Free plan · 1.5K tokens", label);
    }

    [Fact]
    public void FormatUsageLabel_WithExtraUsage_ReturnsExtraUsage()
    {
        var info = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };
        var label = ClaudeProvider.FormatUsageLabel("Pro", 0, 0, info);
        Assert.Equal("Pro plan · extra usage on", label);
    }

    [Fact]
    public void FormatBarReset_FutureDays_ReturnsDays()
    {
        var future = DateTimeOffset.UtcNow.AddDays(2).AddHours(1).ToUnixTimeSeconds();
        Assert.StartsWith("Resets 2d", ClaudeProvider.FormatBarReset(future));
    }

    [Fact]
    public void FormatBarReset_FutureHours_ReturnsHours()
    {
        var future = DateTimeOffset.UtcNow.AddHours(5).AddMinutes(10).ToUnixTimeSeconds();
        Assert.StartsWith("Resets 5h", ClaudeProvider.FormatBarReset(future));
    }

    [Fact]
    public void FormatBarReset_Past_ReturnsNow()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        Assert.Equal("Resets now", ClaudeProvider.FormatBarReset(past));
    }

    [Fact]
    public void FormatResetCountdown_FutureDays_ReturnsDays()
    {
        var future = DateTimeOffset.UtcNow.AddDays(2).AddHours(2).ToUnixTimeSeconds();
        Assert.StartsWith("Weekly resets in 2d", ClaudeProvider.FormatResetCountdown(future, "Weekly"));
    }

    [Fact]
    public void BuildWeeklySnapshot_WithLimits_ReturnsSnapshot()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits { SevenDayUtilization = 0.5, SevenDayReset = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() };
        var snapshot = ClaudeProvider.BuildWeeklySnapshot(limits);
        Assert.NotNull(snapshot);
        Assert.Equal(0.5, snapshot.UsedPercent);
    }

    [Fact]
    public void BuildWeeklySnapshot_Null_ReturnsNull() => Assert.Null(ClaudeProvider.BuildWeeklySnapshot(null));

    [Fact]
    public void BuildUsageBars_WithLimits_ReturnsTwoBars()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.3,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.7,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
        };
        var bars = ClaudeProvider.BuildUsageBars(limits);
        Assert.Equal(2, bars.Count);
        Assert.Equal("5-hour limit", bars[0].Label);
        Assert.Equal(0.3, bars[0].UsedPercent);
        Assert.Equal("Weekly · all models", bars[1].Label);
        Assert.Equal(0.7, bars[1].UsedPercent);
    }

    [Fact]
    public void BuildUsageBars_Null_ReturnsEmptyList()
    {
        var bars = ClaudeProvider.BuildUsageBars(null);
        Assert.Empty(bars);
    }

    [Fact]
    public void CalculateTotalTokens_Null_ReturnsZero() => Assert.Equal(0, ClaudeProvider.CalculateTotalTokens(null));

    [Fact]
    public void CalculateTotalTokens_WithUsages_ReturnsSum()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage { InputTokens = 10, OutputTokens = 20, CacheReadInputTokens = 5, CacheCreationInputTokens = 5 });
        Assert.Equal(40, ClaudeProvider.CalculateTotalTokens(stats));
    }

    [Fact]
    public void CalculateEquivalentCost_Null_ReturnsZero() => Assert.Equal(0, ClaudeProvider.CalculateEquivalentCost(null));

    [Fact]
    public void ResolvePricing_ExactMatch_ReturnsPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-opus-4-7");
        Assert.Equal(5.0, pricing.InputPerMTok);
        Assert.Equal(25.0, pricing.OutputPerMTok);
    }

    [Fact]
    public void ResolvePricing_PrefixMatch_ReturnsPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-sonnet-4-5-extra");
        Assert.Equal(3.0, pricing.InputPerMTok);
    }

    [Fact]
    public void ResolvePricing_Unknown_ReturnsSonnetFallback()
    {
        var pricing = ClaudeProvider.ResolvePricing("unknown-model");
        Assert.Equal(3.0, pricing.InputPerMTok);
    }

    [Fact]
    public void FormatResetCountdown_FutureHours_ReturnsHours()
    {
        var future = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(25).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(future, "5-hour limit");
        Assert.StartsWith("5-hour limit resets in 3h", result);
    }

    [Fact]
    public void CalculateEquivalentCost_WithMultipleModels_SumsCorrectly()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-5",
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            CacheReadInputTokens = 100_000,
            CacheCreationInputTokens = 50_000,
        });
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-haiku-4-5",
            InputTokens = 500_000,
            OutputTokens = 200_000,
            CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0,
        });

        var cost = ClaudeProvider.CalculateEquivalentCost(stats);
        Assert.True(cost > 10);
        Assert.True(cost < 15);
    }

    [Fact]
    public void CalculateTotalTokens_MultipleModels_SumsAll()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-5",
            InputTokens = 100,
            OutputTokens = 200,
            CacheReadInputTokens = 50,
            CacheCreationInputTokens = 25,
        });
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-haiku-4-5",
            InputTokens = 300,
            OutputTokens = 400,
            CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0,
        });

        var total = ClaudeProvider.CalculateTotalTokens(stats);
        Assert.Equal(1075, total);
    }
}
