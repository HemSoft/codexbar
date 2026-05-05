// <copyright file="CopilotModelTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Models;
using Xunit;

public class CopilotModelTests
{
    [Fact]
    public void CopilotUserResponse_Deserializes_AllFields()
    {
        var json = """{"login":"testuser","copilot_plan":"business","organization_login_list":["org1","org2"],"quota_reset_date":"2025-01-15","quota_reset_date_utc":"2025-01-15T00:00:00Z","quota_snapshots":{"chat":{"entitlement":300,"remaining":200,"overage_count":0,"overage_permitted":true,"percent_remaining":66.7,"unlimited":false,"quota_id":"chat_monthly","timestamp_utc":"2025-01-10T12:00:00Z"},"completions":{"entitlement":1000,"remaining":800,"overage_count":10,"overage_permitted":false,"percent_remaining":80.0,"unlimited":false,"quota_id":"completions_monthly","timestamp_utc":"2025-01-10T12:00:00Z"},"premium_interactions":{"entitlement":500,"remaining":300,"overage_count":5,"overage_permitted":true,"percent_remaining":60.0,"unlimited":false,"quota_id":"premium_monthly","timestamp_utc":"2025-01-10T12:00:00Z"}}}""";

        var response = JsonSerializer.Deserialize<CopilotUserResponse>(json);

        Assert.NotNull(response);
        Assert.Equal("testuser", response!.Login);
        Assert.Equal("business", response.CopilotPlan);
        Assert.Equal(2, response.OrganizationLoginList!.Count);
        Assert.Equal("org1", response.OrganizationLoginList[0]);
        Assert.Equal("2025-01-15", response.QuotaResetDate);
        Assert.Equal("2025-01-15T00:00:00Z", response.QuotaResetDateUtc);
        Assert.NotNull(response.QuotaSnapshots);

        var chat = response.QuotaSnapshots!.Chat;
        Assert.NotNull(chat);
        Assert.Equal(300, chat!.Entitlement);
        Assert.Equal(200, chat.Remaining);
        Assert.Equal(0, chat.OverageCount);
        Assert.True(chat.OveragePermitted);
        Assert.Equal(66.7, chat.PercentRemaining);
        Assert.False(chat.Unlimited);
        Assert.Equal("chat_monthly", chat.QuotaId);

        var completions = response.QuotaSnapshots.Completions;
        Assert.NotNull(completions);
        Assert.Equal(1000, completions!.Entitlement);
        Assert.Equal(800, completions.Remaining);

        var premium = response.QuotaSnapshots.PremiumInteractions;
        Assert.NotNull(premium);
        Assert.Equal(500, premium!.Entitlement);
        Assert.Equal(300, premium.Remaining);
    }

    [Fact]
    public void CopilotUserResponse_Deserializes_MinimalFields()
    {
        var json = """{"login":"minimal"}""";
        var response = JsonSerializer.Deserialize<CopilotUserResponse>(json);

        Assert.NotNull(response);
        Assert.Equal("minimal", response!.Login);
        Assert.Null(response.CopilotPlan);
        Assert.Null(response.OrganizationLoginList);
        Assert.Null(response.QuotaResetDate);
        Assert.Null(response.QuotaResetDateUtc);
        Assert.Null(response.QuotaSnapshots);
    }

    [Fact]
    public void CopilotQuotaSnapshot_Defaults_AreSet()
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

    [Fact]
    public void CopilotQuotaSnapshots_Defaults_AreNull()
    {
        var snapshots = new CopilotQuotaSnapshots();
        Assert.Null(snapshots.Chat);
        Assert.Null(snapshots.Completions);
        Assert.Null(snapshots.PremiumInteractions);
    }

    [Fact]
    public void CopilotAccountResult_SetsProperties()
    {
        var result = new CopilotAccountResult
        {
            Username = "testuser",
            Plan = "enterprise",
            Organizations = ["org1"],
            Success = true,
        };

        Assert.Equal("testuser", result.Username);
        Assert.Equal("enterprise", result.Plan);
        Assert.Single(result.Organizations!);
        Assert.True(result.Success);
    }

    [Fact]
    public void CopilotAccountResult_Failure()
    {
        var result = new CopilotAccountResult
        {
            Username = "baduser",
            Success = false,
            ErrorMessage = "connection failed",
        };

        Assert.False(result.Success);
        Assert.Equal("connection failed", result.ErrorMessage);
    }
}
