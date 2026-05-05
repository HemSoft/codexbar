// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class ClaudeProviderAsyncTests
{
    [Fact]
    public async Task IsAvailableAsync_Enabled_ReturnsTrue()
    {
        var settings = CreateSettingsService();
        var provider = CreateProvider(settings: settings);
        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = CreateSettingsService(enabled: false);
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task FetchUsageAsync_NoCredentials_ReturnsProviderId()
    {
        var provider = CreateProvider();
        var result = await provider.FetchUsageAsync();
        Assert.Equal(ProviderId.Claude, result.Provider);
    }

    [Fact]
    public void BuildUsageBars_WithLimits_ReturnsTwoBars()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.75,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.30,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
        };

        var bars = ClaudeProvider.BuildUsageBars(limits);
        Assert.Equal(2, bars.Count);
        Assert.Equal("5-hour limit", bars[0].Label);
        Assert.Equal(0.75, bars[0].UsedPercent);
        Assert.Equal("Weekly · all models", bars[1].Label);
        Assert.Equal(0.30, bars[1].UsedPercent);
    }

    [Fact]
    public void BuildUsageBars_Null_ReturnsEmptyList()
    {
        var bars = ClaudeProvider.BuildUsageBars(null);
        Assert.Empty(bars);
    }

    [Fact]
    public void BuildWeeklySnapshot_WithLimits_ReturnsSnapshot()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.65,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds(),
        };

        var snapshot = ClaudeProvider.BuildWeeklySnapshot(limits);
        Assert.NotNull(snapshot);
        Assert.Equal(0.65, snapshot!.UsedPercent);
        Assert.False(snapshot.IsUnlimited);
    }

    [Fact]
    public void BuildWeeklySnapshot_Null_ReturnsNull()
    {
        var snapshot = ClaudeProvider.BuildWeeklySnapshot(null);
        Assert.Null(snapshot);
    }

    [Fact]
    public void FormatBarReset_FutureHours_ReturnsHours()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(resetAt);
        Assert.Contains("Resets", result);
        Assert.Contains("h", result);
    }

    [Fact]
    public void FormatBarReset_PastTime_ReturnsResetsNow()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(resetAt);
        Assert.Equal("Resets now", result);
    }

    [Fact]
    public void FormatBarReset_Days_ReturnsDays()
    {
        var resetAt = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(resetAt);
        Assert.Contains("d", result);
    }

    [Fact]
    public void FormatBarReset_Minutes_ReturnsMinutes()
    {
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(resetAt);
        Assert.Contains("m", result);
    }

    [Fact]
    public void ResolvePricing_ExactMatch_ReturnsExactPrice()
    {
        var (input, output, _, _) = ClaudeProvider.ResolvePricing("claude-sonnet-4-5");
        Assert.Equal(3.0, input);
        Assert.Equal(15.0, output);
    }

    [Fact]
    public void ResolvePricing_PrefixMatch_ReturnsPrefixPrice()
    {
        var (input, _, _, _) = ClaudeProvider.ResolvePricing("claude-opus-4-5-20250123");
        Assert.Equal(5.0, input);
    }

    [Fact]
    public void ResolvePricing_FallbackOpus_ReturnsOpusPrice()
    {
        var (input, _, _, _) = ClaudeProvider.ResolvePricing("claude-opus-custom");
        Assert.Equal(5.0, input);
    }

    [Fact]
    public void ResolvePricing_FallbackHaiku_ReturnsHaikuPrice()
    {
        var (input, _, _, _) = ClaudeProvider.ResolvePricing("claude-haiku-custom");
        Assert.Equal(1.0, input);
    }

    [Fact]
    public void ResolvePricing_DefaultFallback_ReturnsSonnetPrice()
    {
        var (input, _, _, _) = ClaudeProvider.ResolvePricing("unknown-model");
        Assert.Equal(3.0, input);
    }

    [Fact]
    public void CalculateEquivalentCost_WithStats_ReturnsCost()
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

        var cost = ClaudeProvider.CalculateEquivalentCost(stats);
        Assert.True(cost > 0);
    }

    [Fact]
    public void CalculateEquivalentCost_NullStats_ReturnsZero()
    {
        var cost = ClaudeProvider.CalculateEquivalentCost(null);
        Assert.Equal(0, cost);
    }

    [Fact]
    public void CalculateTotalTokens_WithUsage_ReturnsSum()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-haiku-4-5",
            InputTokens = 100,
            OutputTokens = 200,
            CacheReadInputTokens = 50,
            CacheCreationInputTokens = 25,
        });

        var total = ClaudeProvider.CalculateTotalTokens(stats);
        Assert.Equal(375, total);
    }

    [Fact]
    public void CalculateTotalTokens_NullStats_ReturnsZero()
    {
        var total = ClaudeProvider.CalculateTotalTokens(null);
        Assert.Equal(0, total);
    }

    [Fact]
    public void FormatUsageLabel_WithCost_ReturnsCostLabel()
    {
        var result = ClaudeProvider.FormatUsageLabel("pro", 1000, 5.50, null);
        Assert.Contains("pro plan", result);
        Assert.Contains("$5.50", result);
    }

    [Fact]
    public void FormatUsageLabel_WithTokens_ReturnsTokenLabel()
    {
        var result = ClaudeProvider.FormatUsageLabel("pro", 1000, 0, null);
        Assert.Contains("pro plan", result);
        Assert.Contains("1.0K tokens", result);
    }

    [Fact]
    public void FormatUsageLabel_WithExtraUsage_ReturnsExtraLabel()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };
        var result = ClaudeProvider.FormatUsageLabel("pro", 0, 0, accountInfo);
        Assert.Contains("extra usage on", result);
    }

    [Fact]
    public void UnifiedRateLimits_Defaults_AreSet()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits();
        Assert.Equal(0, limits.FiveHourUtilization);
        Assert.Equal(0, limits.FiveHourReset);
        Assert.Equal("unknown", limits.FiveHourStatus);
        Assert.Equal(0, limits.SevenDayUtilization);
        Assert.Equal(0, limits.SevenDayReset);
        Assert.Equal("unknown", limits.SevenDayStatus);
    }

    [Fact]
    public void ClaudeAccountInfo_Defaults_AreSet()
    {
        var info = new ClaudeProvider.ClaudeAccountInfo();
        Assert.Null(info.DisplayName);
        Assert.Null(info.BillingType);
        Assert.False(info.HasExtraUsageEnabled);
    }

    [Fact]
    public void ClaudeModelUsage_Defaults_AreSet()
    {
        var usage = new ClaudeProvider.ClaudeModelUsage();
        Assert.Equal(string.Empty, usage.ModelId);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.CacheReadInputTokens);
        Assert.Equal(0, usage.CacheCreationInputTokens);
    }

    [Fact]
    public void FormatTokenCount_LargeValues_FormatsCorrectly()
    {
        Assert.Contains("B tokens", ClaudeProvider.FormatTokenCount(1_500_000_000));
        Assert.Contains("M tokens", ClaudeProvider.FormatTokenCount(2_500_000));
        Assert.Contains("K tokens", ClaudeProvider.FormatTokenCount(3_500));
        Assert.Contains("tokens", ClaudeProvider.FormatTokenCount(50));
    }

    private static ClaudeProvider CreateProvider(
        ISettingsService? settings = null,
        HttpResponseMessage? httpResponse = null)
    {
        settings ??= CreateSettingsService();
        var handler = new ClaudeMockHttpMessageHandler(httpResponse ?? new HttpResponseMessage(HttpStatusCode.OK));
        var factory = new ClaudeMockHttpClientFactory(handler);
        return new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);
    }

    private static ISettingsService CreateSettingsService(bool enabled = true)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(enabled);
        return settings;
    }

    private sealed class ClaudeMockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public ClaudeMockHttpMessageHandler(HttpResponseMessage response) => this.response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(this.response);
    }

    private sealed class ClaudeMockHttpClientFactory : IHttpClientFactory
    {
        private readonly ClaudeMockHttpMessageHandler handler;

        public ClaudeMockHttpClientFactory(ClaudeMockHttpMessageHandler handler) => this.handler = handler;

        public HttpClient CreateClient(string? name = null) => new(this.handler);
    }
}
