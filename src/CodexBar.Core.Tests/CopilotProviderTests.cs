using CodexBar.Core.Providers.Copilot;
using Xunit;

namespace CodexBar.Core.Tests;

public class CopilotProviderTests
{
    [Fact]
    public void FormatDisplayName_WithPlan_ReturnsLabel()
    {
        var name = CopilotProvider.FormatDisplayName("user", "enterprise");
        Assert.Equal("Copilot · user (Ent)", name);
    }

    [Fact]
    public void FormatDisplayName_WithoutPlan_ReturnsSimple()
    {
        var name = CopilotProvider.FormatDisplayName("user", null);
        Assert.Equal("Copilot · user", name);
    }

    [Fact]
    public void FormatQuotaLabel_KnownLabels_ReturnsMapped()
    {
        Assert.Equal("Premium interactions", CopilotProvider.FormatQuotaLabel("premium"));
        Assert.Equal("Chat", CopilotProvider.FormatQuotaLabel("chat"));
        Assert.Equal("other", CopilotProvider.FormatQuotaLabel("other"));
    }

    [Fact]
    public void ExtractUsername_AccountPrefix_ReturnsName()
    {
        var name = CopilotProvider.ExtractUsername("Logged in to github.com account alice (oauth)");
        Assert.Equal("alice", name);
    }

    [Fact]
    public void ExtractUsername_AsPrefix_ReturnsName()
    {
        var name = CopilotProvider.ExtractUsername("Logged in to github.com as bob");
        Assert.Equal("bob", name);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_ParsesMultipleLines()
    {
        var stderr = "Logged in to github.com account alice\nLogged in to github.com as bob\nother line";
        var users = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);
        Assert.Equal(2, users.Count);
        Assert.Contains("alice", users);
        Assert.Contains("bob", users);
    }

    [Fact]
    public void ParseReset_ValidDate_ReturnsDescription()
    {
        var future = DateTimeOffset.UtcNow.AddDays(3).ToString("O");
        var (resetsAt, description) = CopilotProvider.ParseReset(future);
        Assert.NotNull(resetsAt);
        Assert.StartsWith("Resets in", description);
    }

    [Fact]
    public void ParseReset_Null_ReturnsNulls()
    {
        var (resetsAt, description) = CopilotProvider.ParseReset(null);
        Assert.Null(resetsAt);
        Assert.Null(description);
    }

    [Fact]
    public void ComputeUsageMetrics_Unlimited_ReturnsZeroAndUnlimited()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = true };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, pct);
        Assert.Equal("Unlimited", label);
        Assert.True(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_NoEntitlement_ReturnsNoQuota()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = false, Entitlement = 0 };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, pct);
        Assert.Equal("No quota", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_WithUsage_ReturnsPercentAndLabel()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = false, Entitlement = 100, Remaining = 30 };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0.7, pct);
        Assert.Equal("70 / 100 Premium interactions", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_WithOverage_ReturnsOverageLabel()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = false, Entitlement = 100, Remaining = -5, OverageCount = 5, OveragePermitted = true };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.True(label.Contains("overage"), label);
    }
}
