// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;

/// <summary>
/// Mutation-killing tests for CopilotProvider static methods.
/// Targets: boundary conditions, arithmetic, and comparisons.
/// </summary>
public class CopilotProviderMutationTests
{
    // === FormatDisplayName ===
    [Fact]
    public void FormatDisplayName_UnknownPlan_ReplacesUnderscores()
    {
        var result = CopilotProvider.FormatDisplayName("testuser", "some_plan");
        Assert.Equal("Copilot · testuser (some plan)", result);
    }

    [Fact]
    public void FormatDisplayName_EmptyString_ReturnsSimple()
    {
        var result = CopilotProvider.FormatDisplayName("user", string.Empty);
        Assert.Equal("Copilot · user ()", result);
    }

    // === ComputeUsageMetrics ===
    [Fact]
    public void ComputeUsageMetrics_Entitlement100_Used50_Returns50Percent()
    {
        var quota = new CopilotQuotaSnapshot { Entitlement = 100, Remaining = 50 };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(0.5, usedPercent);
        Assert.False(isUnlimited);
        Assert.Contains("50", label);
        Assert.Contains("100", label);
    }

    [Fact]
    public void ComputeUsageMetrics_Entitlement0_Unlimited_ReturnsUnlimited()
    {
        var quota = new CopilotQuotaSnapshot { Entitlement = 0, Remaining = 0, Unlimited = true };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(0.0, usedPercent);
        Assert.True(isUnlimited);
        Assert.Equal("Unlimited", label);
    }

    [Fact]
    public void ComputeUsageMetrics_Entitlement0_NotUnlimited_ReturnsNoQuota()
    {
        var quota = new CopilotQuotaSnapshot { Entitlement = 0, Remaining = 0, Unlimited = false };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(0.0, usedPercent);
        Assert.False(isUnlimited);
        Assert.Equal("No quota", label);
    }

    [Fact]
    public void ComputeUsageMetrics_NegativeRemaining_OverageWithPermission_ShowsCost()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -10,
            OveragePermitted = true,
            OverageCount = 10,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(1.1, usedPercent, 2);
        Assert.False(isUnlimited);
        Assert.Contains("$", label);
    }

    [Fact]
    public void ComputeUsageMetrics_NegativeRemaining_OverageNotPermitted_ShowsOverLimit()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -5,
            OveragePermitted = false,
            OverageCount = 5,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(1.05, usedPercent, 2);
        Assert.Contains("over limit", label);
    }

    [Fact]
    public void ComputeUsageMetrics_FullUsage_Returns100Percent()
    {
        var quota = new CopilotQuotaSnapshot { Entitlement = 100, Remaining = 0 };
        var (usedPercent, _, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(1.0, usedPercent);
    }

    [Fact]
    public void ComputeUsageMetrics_ChatLabel_IncludesQuotaLabel()
    {
        var quota = new CopilotQuotaSnapshot { Entitlement = 50, Remaining = 25 };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "chat");

        Assert.Contains("Chat", label);
    }

    [Fact]
    public void ComputeUsageMetrics_PremiumLabel_DoesNotIncludeQuotaLabel()
    {
        var quota = new CopilotQuotaSnapshot { Entitlement = 50, Remaining = 25, OveragePermitted = false };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.DoesNotContain("Premium", label);
    }

    // === ParseReset ===
    [Fact]
    public void ParseReset_LessThan1Day_ShowsHoursMinutes()
    {
        var future = DateTimeOffset.UtcNow.AddHours(5).AddMinutes(30).ToString("O");
        var (resetsAt, description) = CopilotProvider.ParseReset(future);

        Assert.NotNull(resetsAt);
        Assert.Contains("Resets in", description);
        Assert.Contains("h", description);
        Assert.Contains("m", description);
    }

    [Fact]
    public void ParseReset_Between1And2Days_ReturnsTomorrow()
    {
        var future = DateTimeOffset.UtcNow.AddHours(30).ToString("O");
        var (_, description) = CopilotProvider.ParseReset(future);

        Assert.Equal("Resets tomorrow", description);
    }

    [Fact]
    public void ParseReset_MoreThan2Days_ReturnsDays()
    {
        var future = DateTimeOffset.UtcNow.AddDays(5).ToString("O");
        var (_, description) = CopilotProvider.ParseReset(future);

        Assert.Contains("Resets in", description);
        Assert.Contains("d", description);
    }

    // === ExtractUsername ===
    [Fact]
    public void ExtractUsername_AccountAtEnd_ReturnsEntireRest()
    {
        var result = CopilotProvider.ExtractUsername("account username");
        Assert.Equal("username", result);
    }

    [Fact]
    public void ExtractUsername_AsAtEnd_ReturnsEntireRest()
    {
        var result = CopilotProvider.ExtractUsername("logged in as myuser");
        Assert.Equal("myuser", result);
    }

    // === ExtractUsernamesFromGhStatus ===
    [Fact]
    public void ExtractUsernamesFromGhStatus_EmptyString_ReturnsEmpty()
    {
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_NoMatchingLines_ReturnsEmpty()
    {
        var result = CopilotProvider.ExtractUsernamesFromGhStatus("line1\nline2\nline3");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_SkipsBlankUsernames()
    {
        // "Logged in to github.com" without user token info
        var stderr = "Logged in to github.com account \n";
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);
        Assert.Empty(result);
    }

    // === BuildCopilotApiRequest ===
    [Fact]
    public void BuildCopilotApiRequest_SetsCorrectHeaders()
    {
        var request = CopilotProvider.BuildCopilotApiRequest("test-token-123");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/copilot_internal/user", request.RequestUri!.ToString());
        Assert.Equal("token", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-token-123", request.Headers.Authorization!.Parameter);
        Assert.True(request.Headers.Contains("Editor-Version"));
        Assert.True(request.Headers.Contains("Editor-Plugin-Version"));
        Assert.True(request.Headers.Contains("X-Github-Api-Version"));
    }

    // === ParseCopilotApiResponse ===
    [Fact]
    public void ParseCopilotApiResponse_NullData_ReturnsError()
    {
        var result = CopilotProvider.ParseCopilotApiResponse("null", "user");
        Assert.False(result.Success);
        Assert.Contains("Empty API response", result.ErrorMessage);
    }

    [Fact]
    public void ParseCopilotApiResponse_ValidResponse_ReturnsSuccess()
    {
        var json = """
        {
            "login": "testuser",
            "copilot_plan": "enterprise",
            "organization_login_list": ["org1", "org2"],
            "quota_reset_date_utc": "2026-06-01T00:00:00Z",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 300,
                    "remaining": 150,
                    "overage_count": 0,
                    "overage_permitted": true,
                    "percent_remaining": 50.0,
                    "unlimited": false
                },
                "chat": {
                    "entitlement": 1000,
                    "remaining": 500,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "percent_remaining": 50.0,
                    "unlimited": false
                }
            }
        }
        """;

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.True(result.Success);
        Assert.Equal("testuser", result.Username);
        Assert.Equal("enterprise", result.Plan);
        Assert.NotNull(result.Organizations);
        Assert.Equal(2, result.Organizations!.Count);
        Assert.NotNull(result.PremiumInteractions);
        Assert.Equal(300, result.PremiumInteractions!.Entitlement);
        Assert.Equal(150, result.PremiumInteractions.Remaining);
        Assert.NotNull(result.Chat);
        Assert.Equal(1000, result.Chat!.Entitlement);
        Assert.Equal("2026-06-01T00:00:00Z", result.QuotaResetDateUtc);
    }

    [Fact]
    public void ParseCopilotApiResponse_NoPremium_SucceedsWithNull()
    {
        var json = """
        {
            "login": "testuser",
            "copilot_plan": "individual_pro",
            "quota_snapshots": {}
        }
        """;

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.True(result.Success);
        Assert.Null(result.PremiumInteractions);
        Assert.Null(result.Chat);
    }

    [Fact]
    public void ParseCopilotApiResponse_NoQuotaSnapshots_SucceedsWithNull()
    {
        var json = """
        {
            "login": "testuser",
            "copilot_plan": "business"
        }
        """;

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.True(result.Success);
        Assert.Null(result.PremiumInteractions);
    }

    // === FormatQuotaLabel ===
    [Theory]
    [InlineData("premium", "Premium interactions")]
    [InlineData("chat", "Chat")]
    [InlineData("completions", "completions")]
    [InlineData("unknown", "unknown")]
    public void FormatQuotaLabel_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, CopilotProvider.FormatQuotaLabel(input));
    }

    // === CopilotAccountResult static factories ===
    [Fact]
    public void CopilotAccountResult_TokenMissing_HasCorrectFields()
    {
        var result = CopilotAccountResult.TokenMissing("alice");
        Assert.Equal("alice", result.Username);
        Assert.False(result.Success);
        Assert.Contains("alice", result.ErrorMessage!);
        Assert.Contains("No token", result.ErrorMessage!);
    }

    [Fact]
    public void CopilotAccountResult_Unauthorized_HasCorrectFields()
    {
        var result = CopilotAccountResult.Unauthorized("bob");
        Assert.Equal("bob", result.Username);
        Assert.False(result.Success);
        Assert.Contains("expired", result.ErrorMessage!);
    }

    [Fact]
    public void CopilotAccountResult_Error_HasCorrectFields()
    {
        var result = CopilotAccountResult.Error("carol", "Something went wrong");
        Assert.Equal("carol", result.Username);
        Assert.False(result.Success);
        Assert.Equal("Something went wrong", result.ErrorMessage);
    }

    // === Overage calculation edge cases ===
    [Fact]
    public void ComputeUsageMetrics_ZeroOverageCount_NegativeRemaining_UsesRemainingOverage()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -20,
            OverageCount = 0,
            OveragePermitted = true,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        // Overage = max(0, 0) vs max(0, 20) → 20
        // Cost = 20 * 0.04 = $0.80
        Assert.Contains("$0.80", label);
    }

    [Fact]
    public void ComputeUsageMetrics_OverageCountLargerThanNegativeRemaining_UsesOverageCount()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -5,
            OverageCount = 15,
            OveragePermitted = true,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        // Overage = max(0, 15) vs max(0, 5) → 15
        // Cost = 15 * 0.04 = $0.60
        Assert.Contains("$0.60", label);
    }

    [Fact]
    public void ComputeUsageMetrics_PositiveRemainingWithOverage_OverageIsOverageCount()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = 10,
            OverageCount = 5,
            OveragePermitted = true,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        // Overage = max(0, 5) vs max(0, -10) → 5
        // Cost = 5 * 0.04 = $0.20
        Assert.Contains("$0.20", label);
    }
}
