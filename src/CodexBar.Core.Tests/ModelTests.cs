// <copyright file="ModelTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using Xunit;

public class ModelTests
{
    [Fact]
    public void ProviderUsageResult_Failure_CreatesFailedResult()
    {
        var result = ProviderUsageResult.Failure(ProviderId.Claude, "error msg");

        Assert.Equal(ProviderId.Claude, result.Provider);
        Assert.False(result.Success);
        Assert.Equal("error msg", result.ErrorMessage);
        Assert.Null(result.SessionUsage);
        Assert.Null(result.WeeklyUsage);
        Assert.Null(result.Items);
    }

    [Fact]
    public void ProviderUsageResult_EmptySuccess_CreatesSuccessfulResult()
    {
        var result = ProviderUsageResult.EmptySuccess(ProviderId.OpenRouter);

        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.SessionUsage);
        Assert.Null(result.WeeklyUsage);
    }

    [Fact]
    public void ProviderUsageResult_WithItems_SetsProperties()
    {
        var capturedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var items = new List<UsageItem>
        {
            new() { Key = "test", DisplayName = "Test", Success = true },
        };

        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            SessionUsage = new UsageSnapshot { UsedPercent = 0.5, CapturedAt = capturedAt },
            WeeklyUsage = new UsageSnapshot { UsedPercent = 0.3, CapturedAt = capturedAt },
            CreditsRemaining = 10.5m,
            Items = items,
        };

        Assert.Equal(ProviderId.Copilot, result.Provider);
        Assert.True(result.Success);
        Assert.Equal(0.5, result.SessionUsage!.UsedPercent);
        Assert.Equal(0.3, result.WeeklyUsage!.UsedPercent);
        Assert.Equal(10.5m, result.CreditsRemaining);
        Assert.Single(result.Items!);
    }

    [Fact]
    public void UsageSnapshot_Defaults_AreSet()
    {
        var snapshot = new UsageSnapshot();

        Assert.Equal(0.0, snapshot.UsedPercent);
        Assert.Null(snapshot.UsageLabel);
        Assert.Null(snapshot.ResetsAt);
        Assert.Null(snapshot.ResetDescription);
        Assert.False(snapshot.IsUnlimited);
        Assert.True(snapshot.CapturedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void UsageSnapshot_WithValues_SetsProperties()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(2);
        var snapshot = new UsageSnapshot
        {
            UsedPercent = 0.75,
            UsageLabel = "75 / 100",
            ResetsAt = resetAt,
            ResetDescription = "Resets in 2h",
            IsUnlimited = true,
        };

        Assert.Equal(0.75, snapshot.UsedPercent);
        Assert.Equal("75 / 100", snapshot.UsageLabel);
        Assert.Equal(resetAt, snapshot.ResetsAt);
        Assert.Equal("Resets in 2h", snapshot.ResetDescription);
        Assert.True(snapshot.IsUnlimited);
    }

    [Fact]
    public void UsageBar_SetsProperties()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddHours(5);
        var bar = new UsageBar
        {
            Label = "5-hour limit",
            UsedPercent = 0.6,
            ResetDescription = "Resets 3h",
            ResetsAt = resetsAt,
        };

        Assert.Equal("5-hour limit", bar.Label);
        Assert.Equal(0.6, bar.UsedPercent);
        Assert.Equal("Resets 3h", bar.ResetDescription);
        Assert.Equal(resetsAt, bar.ResetsAt);
    }

    [Fact]
    public void UsageItem_SetsProperties()
    {
        var item = new UsageItem
        {
            Key = "copilot:hemsoft",
            DisplayName = "Copilot · HemSoft",
            CreditsRemaining = 50m,
            Success = true,
        };

        Assert.Equal("copilot:hemsoft", item.Key);
        Assert.Equal("Copilot · HemSoft", item.DisplayName);
        Assert.Equal(50m, item.CreditsRemaining);
        Assert.True(item.Success);
        Assert.Null(item.ErrorMessage);
    }

    [Fact]
    public void UsageItem_Failed_SetsError()
    {
        var item = new UsageItem
        {
            Key = "copilot:fail",
            DisplayName = "Failed",
            Success = false,
            ErrorMessage = "connection refused",
        };

        Assert.False(item.Success);
        Assert.Equal("connection refused", item.ErrorMessage);
    }

    [Fact]
    public void ProviderMetadata_SetsProperties()
    {
        var meta = new ProviderMetadata
        {
            Id = ProviderId.Claude,
            DisplayName = "Claude",
            Description = "Anthropic Claude",
            DashboardUrl = "https://claude.ai",
            StatusPageUrl = "https://status.anthropic.com",
            SupportsSessionUsage = true,
            SupportsWeeklyUsage = false,
            SupportsCredits = true,
        };

        Assert.Equal(ProviderId.Claude, meta.Id);
        Assert.Equal("Claude", meta.DisplayName);
        Assert.Equal("Anthropic Claude", meta.Description);
        Assert.Equal("https://claude.ai", meta.DashboardUrl);
        Assert.True(meta.SupportsSessionUsage);
        Assert.False(meta.SupportsWeeklyUsage);
        Assert.True(meta.SupportsCredits);
    }

    [Fact]
    public void CopilotAccountResult_SetsProperties()
    {
        var result = new CopilotAccountResult
        {
            Username = "testuser",
            Plan = "enterprise",
            Organizations = ["org1", "org2"],
            Success = true,
        };

        Assert.Equal("testuser", result.Username);
        Assert.Equal("enterprise", result.Plan);
        Assert.Equal(2, result.Organizations!.Count);
        Assert.True(result.Success);
    }

    [Fact]
    public void CopilotQuotaSnapshot_DefaultValues()
    {
        var snapshot = new CopilotQuotaSnapshot();

        Assert.Equal(0, snapshot.Entitlement);
        Assert.Equal(0, snapshot.Remaining);
        Assert.Equal(0, snapshot.OverageCount);
        Assert.False(snapshot.OveragePermitted);
        Assert.Equal(0.0, snapshot.PercentRemaining);
        Assert.False(snapshot.Unlimited);
        Assert.Null(snapshot.QuotaId);
        Assert.Null(snapshot.TimestampUtc);
    }
}
