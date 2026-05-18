// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Xunit;

/// <summary>
/// Additional tests for CopilotProvider covering HTTP paths and edge cases.
/// </summary>
public class CopilotProviderHttpTests
{
    [Fact]
    public void FormatDisplayName_IndividualPro_ReturnsPro()
    {
        // Arrange & Act
        var result = CopilotProvider.FormatDisplayName("john", "individual_pro");

        // Assert
        Assert.Equal("Copilot · john (Pro)", result);
    }

    [Fact]
    public void FormatDisplayName_Business_ReturnsBiz()
    {
        // Arrange & Act
        var result = CopilotProvider.FormatDisplayName("jane", "business");

        // Assert
        Assert.Equal("Copilot · jane (Biz)", result);
    }

    [Fact]
    public void FormatDisplayName_UnknownPlanWithUnderscore_ReplacesUnderscores()
    {
        // Arrange & Act
        var result = CopilotProvider.FormatDisplayName("alice", "custom_plan_name");

        // Assert
        Assert.Equal("Copilot · alice (custom plan name)", result);
    }

    [Fact]
    public void ParseReset_Tomorrow_ReturnsTomorrow()
    {
        // Arrange - set reset date to approximately 1.5 days in the future
        var tomorrow = DateTimeOffset.UtcNow.AddHours(36).ToString("O");

        // Act
        var (resetsAt, description) = CopilotProvider.ParseReset(tomorrow);

        // Assert
        Assert.NotNull(resetsAt);
        Assert.Equal("Resets tomorrow", description);
    }

    [Fact]
    public void ParseReset_Hours_ReturnsHoursMinutes()
    {
        // Arrange - set reset date to approximately 12 hours and 30 minutes in the future
        var futureTime = DateTimeOffset.UtcNow.AddHours(12).AddMinutes(30).ToString("O");

        // Act
        var (resetsAt, description) = CopilotProvider.ParseReset(futureTime);

        // Assert
        Assert.NotNull(resetsAt);
        Assert.StartsWith("Resets in 12h", description);
    }

    [Fact]
    public void ComputeUsageMetrics_ChatOverLimitNotPermitted_ShowsOverLimitWithLabel()
    {
        // Arrange
        var quota = new CopilotQuotaSnapshot
        {
            Unlimited = false,
            Entitlement = 100,
            Remaining = -20,
            OverageCount = 20,
            OveragePermitted = false,
        };

        // Act
        var (usedPercent, usageLabel, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "chat");

        // Assert
        Assert.Equal(1.2, usedPercent); // 120 / 100 = 1.2
        Assert.Equal("120 / 100 Chat (over limit)", usageLabel);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ExtractUsername_AsAtEndOfLine_ReturnsUsername()
    {
        // Arrange - "as " at the end with no space after username
        var line = "Logged in to github.com as alice";

        // Act
        var result = CopilotProvider.ExtractUsername(line);

        // Assert
        Assert.Equal("alice", result);
    }

    [Fact]
    public void ExtractUsername_AccountAtEndOfLine_ReturnsUsername()
    {
        // Arrange - "account " at the end with no space after username
        var line = "Logged in to github.com account bob";

        // Act
        var result = CopilotProvider.ExtractUsername(line);

        // Assert
        Assert.Equal("bob", result);
    }
}
