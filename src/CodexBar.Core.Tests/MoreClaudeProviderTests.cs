// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class MoreClaudeProviderTests
{
    private static ClaudeProvider CreateProvider(ISettingsService? settings = null)
    {
        settings ??= CreateSettingsService();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        return new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            httpClientFactory,
            settings);
    }

    private static ISettingsService CreateSettingsService(bool enabled = true)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(enabled);
        return settings;
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = CreateSettingsService(enabled: false);
        var provider = CreateProvider(settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_Enabled_ReturnsTrue()
    {
        var provider = CreateProvider();
        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public void FormatResetCountdown_FutureHours_ReturnsHours()
    {
        var future = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(25).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(future, "5-hour limit");
        Assert.StartsWith("5-hour limit resets in 3h", result);
    }

    [Fact]
    public void FormatResetCountdown_FutureDays_ReturnsDays()
    {
        var future = DateTimeOffset.UtcNow.AddDays(5).AddHours(2).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(future, "Weekly");
        Assert.StartsWith("Weekly resets in 5d", result);
    }

    [Fact]
    public void FormatResetCountdown_Past_ReturnsNow()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(past, "5-hour limit");
        Assert.Equal("5-hour limit resets now", result);
    }

    [Fact]
    public void FormatResetCountdown_MinutesOnly_ReturnsMinutes()
    {
        var future = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(future, "5-hour limit");
        Assert.StartsWith("5-hour limit resets in", result);
        Assert.Contains("m", result);
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
    public void ResolvePricing_HaikuModel_ReturnsHaikuPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-haiku-3-5");
        Assert.Equal(0.80, pricing.InputPerMTok);
        Assert.Equal(4.0, pricing.OutputPerMTok);
    }

    [Fact]
    public void ResolvePricing_OpusPrefix_ReturnsOpusPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-opus-4-7-something-extra");
        Assert.Equal(5.0, pricing.InputPerMTok);
    }

    [Fact]
    public void CalculateTotalTokens_SumsModelUsages()
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

        // input + output for both models: 100 + 200 + 300 + 400 + 50 + 25 = 1075
        Assert.Equal(1075, total);
    }

    [Fact]
    public void CalculateTotalTokens_NullStats_ReturnsZero()
    {
        var total = ClaudeProvider.CalculateTotalTokens(null);
        Assert.Equal(0, total);
    }

    [Fact]
    public void FormatBarReset_FutureReset_ReturnsFormatted()
    {
        var future = DateTimeOffset.UtcNow.AddDays(2).AddHours(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(future);
        Assert.Contains("2d", result);
    }

    [Fact]
    public void BuildWeeklySnapshot_WithLimits_ReturnsSnapshot()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.75,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds(),
        };
        var snapshot = ClaudeProvider.BuildWeeklySnapshot(limits);
        Assert.NotNull(snapshot);
        Assert.Equal(0.75, snapshot.UsedPercent);
        Assert.Contains("Weekly", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildWeeklySnapshot_NullLimits_ReturnsNull()
    {
        var snapshot = ClaudeProvider.BuildWeeklySnapshot(null);
        Assert.Null(snapshot);
    }

    [Fact]
    public void BuildUsageBars_WithSessionAndWeekly_ReturnsBoth()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.6,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.45,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
        };

        var bars = ClaudeProvider.BuildUsageBars(limits);
        Assert.Equal(2, bars.Count);
    }

    [Fact]
    public void BuildUsageBars_NullLimits_ReturnsEmptyList()
    {
        var bars = ClaudeProvider.BuildUsageBars(null);
        Assert.Empty(bars);
    }
}
