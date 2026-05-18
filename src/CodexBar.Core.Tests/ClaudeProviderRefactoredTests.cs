// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net.Http.Headers;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;

/// <summary>
/// Tests for refactored ClaudeProvider methods: BuildSessionSnapshot, BuildSessionSnapshotFromLimits,
/// FormatSubscriptionType, and BuildRateLimitProbeRequest.
/// </summary>
public class ClaudeProviderRefactoredTests
{
    // --- FormatSubscriptionType ---
    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("  ", "Unknown")]
    [InlineData("pro", "Pro")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("max", "Max")]
    public void FormatSubscriptionType_VariousInputs_ReturnsExpected(string? input, string expected)
    {
        var result = ClaudeProvider.FormatSubscriptionType(input);
        Assert.Equal(expected, result);
    }

    // --- BuildSessionSnapshot (null limits — fallback path) ---
    [Fact]
    public void BuildSessionSnapshot_NullLimits_ReturnsUnlimitedFallback()
    {
        var snapshot = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 0, 0, null);

        Assert.True(snapshot.IsUnlimited);
        Assert.Contains("Rate limits unavailable", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshot_NullLimits_WithCost_IncludesCostInFallback()
    {
        var snapshot = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 0, 12.5, null);

        Assert.True(snapshot.IsUnlimited);
        Assert.Contains("~$12.50 equiv.", snapshot.UsageLabel);
        Assert.Contains("Rate limits unavailable", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshot_NullLimits_WithTokens_IncludesTokensInFallback()
    {
        var snapshot = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 1_500_000, 0, null);

        Assert.True(snapshot.IsUnlimited);
        Assert.Contains("1.5M tokens", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshot_NullLimits_NoData_JustUnavailable()
    {
        // Production always passes FormatSubscriptionType output — mirror that here
        var displaySub = ClaudeProvider.FormatSubscriptionType(string.Empty);
        var snapshot = ClaudeProvider.BuildSessionSnapshot(null, displaySub, 0, 0, null);

        Assert.True(snapshot.IsUnlimited);
        Assert.Equal("Unknown plan · Rate limits unavailable", snapshot.UsageLabel);
    }

    // --- BuildSessionSnapshotFromLimits ---
    [Fact]
    public void BuildSessionSnapshotFromLimits_BasicLimits_ReturnsCorrectPercent()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.65,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
        };

        var snapshot = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 0, null);

        Assert.Equal(0.65, snapshot.UsedPercent);
        Assert.False(snapshot.IsUnlimited);
        Assert.Contains("Pro plan", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_WithEquivalentCost_IncludesCost()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.3,
        };

        var snapshot = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Max", 5000, 8.75, null);

        Assert.Contains("~$8.75 equiv.", snapshot.UsageLabel);
        Assert.Contains("Max plan", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_WithTokensNoCost_IncludesTokens()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.1,
        };

        var snapshot = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 2_500_000, 0, null);

        Assert.Contains("2.5M tokens", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_ExtraUsageEnabled_IncludesLabel()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
        };
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo
        {
            HasExtraUsageEnabled = true,
        };

        var snapshot = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 0, accountInfo);

        Assert.Contains("extra usage on", snapshot.UsageLabel);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_WithReset_IncludesResetInfo()
    {
        var futureReset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.8,
            FiveHourReset = futureReset,
        };

        var snapshot = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 0, null);

        Assert.NotNull(snapshot.ResetsAt);
        Assert.NotNull(snapshot.ResetDescription);
        Assert.Contains("5-hour limit resets", snapshot.ResetDescription);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_NoReset_NullResetInfo()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.2,
            FiveHourReset = 0,
        };

        var snapshot = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 0, null);

        Assert.Null(snapshot.ResetsAt);
        Assert.Null(snapshot.ResetDescription);
    }

    // --- BuildSessionSnapshot with limits (delegates to BuildSessionSnapshotFromLimits) ---
    [Fact]
    public void BuildSessionSnapshot_WithLimits_DelegatesToFromLimits()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.45,
        };

        var snapshot = ClaudeProvider.BuildSessionSnapshot(limits, "Enterprise", 0, 5.0, null);

        Assert.False(snapshot.IsUnlimited);
        Assert.Equal(0.45, snapshot.UsedPercent);
        Assert.Contains("Enterprise plan", snapshot.UsageLabel);
    }

    // --- BuildRateLimitProbeRequest ---
    [Fact]
    public void BuildRateLimitProbeRequest_SetsAuthorizationHeader()
    {
        using var request = ClaudeProvider.BuildRateLimitProbeRequest("test-token");

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void BuildRateLimitProbeRequest_SetsCorrectUrl()
    {
        using var request = ClaudeProvider.BuildRateLimitProbeRequest("token");

        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
    }

    [Fact]
    public void BuildRateLimitProbeRequest_SetsPostMethod()
    {
        using var request = ClaudeProvider.BuildRateLimitProbeRequest("token");

        Assert.Equal(HttpMethod.Post, request.Method);
    }

    [Fact]
    public void BuildRateLimitProbeRequest_IncludesAnthropicVersion()
    {
        using var request = ClaudeProvider.BuildRateLimitProbeRequest("token");

        Assert.True(request.Headers.TryGetValues("anthropic-version", out var values));
        Assert.Contains("2023-06-01", values);
    }

    [Fact]
    public void BuildRateLimitProbeRequest_IncludesBetaHeader()
    {
        using var request = ClaudeProvider.BuildRateLimitProbeRequest("token");

        Assert.True(request.Headers.TryGetValues("anthropic-beta", out var values));
        Assert.Contains("oauth-2025-04-20", values);
    }

    [Fact]
    public void BuildRateLimitProbeRequest_HasJsonContent()
    {
        using var request = ClaudeProvider.BuildRateLimitProbeRequest("token");

        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
    }
}
