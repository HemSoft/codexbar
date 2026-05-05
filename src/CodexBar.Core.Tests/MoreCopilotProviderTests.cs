// <copyright file="MoreCopilotProviderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class MoreCopilotProviderTests
{
    [Fact]
    public void ExtractUsernamesFromGhStatus_MultipleUsers_ReturnsAll()
    {
        var output = """
            alice
              ✓ Logged in to github.com as alice
            bob
              ✓ Logged in to github.com as bob
            """;

        var names = CopilotProvider.ExtractUsernamesFromGhStatus(output);
        Assert.Equal(2, names.Count);
        Assert.Contains("alice", names);
        Assert.Contains("bob", names);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_EmptyInput_ReturnsEmpty()
    {
        var names = CopilotProvider.ExtractUsernamesFromGhStatus(string.Empty);
        Assert.Empty(names);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_NoLoggedIn_ReturnsEmpty()
    {
        var output = "alice\n  X Could not authenticate";
        var names = CopilotProvider.ExtractUsernamesFromGhStatus(output);
        Assert.Empty(names);
    }

    [Fact]
    public void ExtractUsername_ValidLine_ExtractsName()
    {
        var line = "  ✓ Logged in to github.com account (testuser)";
        var name = CopilotProvider.ExtractUsername(line);
        Assert.Equal("(testuser)", name);
    }

    [Fact]
    public void ExtractUsername_NoMatch_ReturnsNull()
    {
        var line = "  X Could not authenticate";
        var name = CopilotProvider.ExtractUsername(line);
        Assert.Null(name);
    }

    [Fact]
    public void ExtractUsername_NullOrEmpty_ReturnsNull()
    {
        var name = CopilotProvider.ExtractUsername(string.Empty);
        Assert.Null(name);
    }

    [Fact]
    public void FormatDisplayName_WithPlan_ReturnsFormatted()
    {
        var result = CopilotProvider.FormatDisplayName("alice", "enterprise");
        Assert.Equal("Copilot · alice (Ent)", result);
    }

    [Fact]
    public void FormatDisplayName_WithoutPlan_ReturnsSimple()
    {
        var result = CopilotProvider.FormatDisplayName("bob", null);
        Assert.Equal("Copilot · bob", result);
    }

    [Fact]
    public void FormatQuotaLabel_KnownLabels_ReturnsMapped()
    {
        Assert.Equal("Premium interactions", CopilotProvider.FormatQuotaLabel("premium"));
        Assert.Equal("Chat", CopilotProvider.FormatQuotaLabel("chat"));
        Assert.Equal("other", CopilotProvider.FormatQuotaLabel("other"));
    }

    [Fact]
    public void ComputeUsageMetrics_NormalUsage_ReturnsMetrics()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = 500,
            OverageCount = 0,
            OveragePermitted = false,
            Unlimited = false,
        };

        var (usedPercent, usageLabel, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.False(isUnlimited);
        Assert.Equal(0.75, usedPercent, 2);
        Assert.Contains("Premium interactions", usageLabel);
    }

    [Fact]
    public void ComputeUsageMetrics_Unlimited_ReturnsUnlimited()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 0,
            Remaining = 0,
            Unlimited = true,
        };

        var (usedPercent, usageLabel, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.True(isUnlimited);
        Assert.Equal("Unlimited", usageLabel);
        Assert.Equal(0, usedPercent);
    }

    [Fact]
    public void ComputeUsageMetrics_ZeroEntitlement_ReturnsNoQuota()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 0,
            Remaining = 0,
            Unlimited = false,
        };

        var (usedPercent, usageLabel, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.False(isUnlimited);
        Assert.Equal("No quota", usageLabel);
    }

    [Fact]
    public void ComputeUsageMetrics_OveragePermitted_IncludesOverage()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = -100,
            OverageCount = 50,
            OveragePermitted = true,
        };

        var (_, usageLabel, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Contains("overage", usageLabel);
    }

    [Fact]
    public void ParseReset_ValidDate_ReturnsReset()
    {
        // Use 10 days to avoid rounding edge cases
        var futureDate = DateTimeOffset.UtcNow.AddDays(10).ToString("o");
        var (resetsAt, resetDescription) = CopilotProvider.ParseReset(futureDate);
        Assert.NotNull(resetsAt);
        Assert.NotNull(resetDescription);
        Assert.Contains("d", resetDescription);
        Assert.True(resetsAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ParseReset_InvalidString_ReturnsNulls()
    {
        var (resetsAt, resetDescription) = CopilotProvider.ParseReset("not-a-date");
        Assert.Null(resetsAt);
        Assert.Null(resetDescription);
    }

    [Fact]
    public void ParseReset_Null_ReturnsNulls()
    {
        var (resetsAt, resetDescription) = CopilotProvider.ParseReset(null);
        Assert.Null(resetsAt);
        Assert.Null(resetDescription);
    }

    [Fact]
    public void ParseReset_PastDate_ReturnsOverdue()
    {
        var pastDate = DateTimeOffset.UtcNow.AddHours(-2).ToString("o");
        var (_, resetDescription) = CopilotProvider.ParseReset(pastDate);
        Assert.Equal("Reset overdue", resetDescription);
    }

    [Fact]
    public async Task FetchUsageAsync_NoAccounts_ReturnsNoAccounts()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetApiKey(ProviderId.Copilot).Returns((string?)null);
        settings.GetCopilotAccounts().Returns([]);

        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        var result = await provider.FetchUsageAsync();
        Assert.False(result.Success);
    }

    [Fact]
    public void Metadata_IsCorrect()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.Equal(ProviderId.Copilot, provider.Metadata.Id);
        Assert.Equal("Copilot", provider.Metadata.DisplayName);
    }
}
