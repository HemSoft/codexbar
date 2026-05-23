// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests for refactored CopilotProvider methods: BuildCopilotApiRequest, ParseCopilotApiResponse,
/// and CopilotAccountResult factory methods.
/// </summary>
public class CopilotProviderRefactoredTests
{
    // --- BuildCopilotApiRequest ---
    [Fact]
    public void BuildCopilotApiRequest_SetsAuthorizationHeader()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("gho_abc123");

        Assert.Equal("token", request.Headers.Authorization?.Scheme);
        Assert.Equal("gho_abc123", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void BuildCopilotApiRequest_SetsCorrectUrl()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("token");

        Assert.Equal("https://api.github.com/copilot_internal/user", request.RequestUri?.ToString());
    }

    [Fact]
    public void BuildCopilotApiRequest_SetsGetMethod()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("token");

        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public void BuildCopilotApiRequest_IncludesAcceptJson()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("token");

        Assert.Contains(
            new MediaTypeWithQualityHeaderValue("application/json"),
            request.Headers.Accept);
    }

    [Fact]
    public void BuildCopilotApiRequest_IncludesEditorVersionHeader()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("token");

        Assert.True(request.Headers.TryGetValues("Editor-Version", out var values));
        Assert.NotEmpty(values);
    }

    [Fact]
    public void BuildCopilotApiRequest_IncludesEditorPluginVersionHeader()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("token");

        Assert.True(request.Headers.TryGetValues("Editor-Plugin-Version", out var values));
        Assert.NotEmpty(values);
    }

    [Fact]
    public void BuildCopilotApiRequest_IncludesApiVersionHeader()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("token");

        Assert.True(request.Headers.TryGetValues("X-Github-Api-Version", out var values));
        Assert.NotEmpty(values);
    }

    [Fact]
    public void BuildCopilotApiRequest_IncludesUserAgent()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("token");

        Assert.NotEmpty(request.Headers.UserAgent);
    }

    // --- ParseCopilotApiResponse ---
    [Fact]
    public void ParseCopilotApiResponse_ValidJson_ReturnsSuccess()
    {
        var json = BuildFullJson();

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.True(result.Success);
        Assert.Equal("testuser", result.Username);
        Assert.Equal("individual_pro", result.Plan);
    }

    [Fact]
    public void ParseCopilotApiResponse_ValidJson_ParsesPremiumInteractions()
    {
        var json = BuildFullJson(entitlement: 2000, remaining: 500);

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.NotNull(result.PremiumInteractions);
        Assert.Equal(2000, result.PremiumInteractions!.Entitlement);
        Assert.Equal(500, result.PremiumInteractions.Remaining);
    }

    [Fact]
    public void ParseCopilotApiResponse_ValidJson_ParsesChat()
    {
        var json = BuildFullJson();

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.NotNull(result.Chat);
        Assert.Equal(1000, result.Chat!.Entitlement);
    }

    [Fact]
    public void ParseCopilotApiResponse_ValidJson_ParsesOrganizations()
    {
        var json = BuildFullJson();

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.NotNull(result.Organizations);
        Assert.Equal(2, result.Organizations!.Count);
    }

    [Fact]
    public void ParseCopilotApiResponse_ValidJson_ParsesResetDate()
    {
        var json = BuildFullJson(resetDate: "2026-06-01T00:00:00Z");

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser");

        Assert.Equal("2026-06-01T00:00:00Z", result.QuotaResetDateUtc);
    }

    [Fact]
    public void ParseCopilotApiResponse_NullJson_ReturnsError()
    {
        var result = CopilotProvider.ParseCopilotApiResponse("null", "testuser");

        Assert.False(result.Success);
        Assert.Equal("testuser", result.Username);
        Assert.Equal("Empty API response", result.ErrorMessage);
    }

    [Fact]
    public void ParseCopilotApiResponse_MinimalJson_ReturnsSuccessWithNullFields()
    {
        var json = """{"login": "user"}""";

        var result = CopilotProvider.ParseCopilotApiResponse(json, "user");

        Assert.True(result.Success);
        Assert.Null(result.PremiumInteractions);
        Assert.Null(result.Chat);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void ParseCopilotApiResponse_WithLogger_ReturnsSuccess()
    {
        var json = BuildFullJson();
        var logger = NullLogger<CopilotProvider>.Instance;

        var result = CopilotProvider.ParseCopilotApiResponse(json, "testuser", logger);

        Assert.True(result.Success);
    }

    // --- CopilotAccountResult factory methods ---
    [Fact]
    public void TokenMissing_ReturnsFailureWithUsername()
    {
        var result = CopilotAccountResult.TokenMissing("alice");

        Assert.False(result.Success);
        Assert.Equal("alice", result.Username);
        Assert.Contains("No token", result.ErrorMessage);
        Assert.Contains("alice", result.ErrorMessage!);
    }

    [Fact]
    public void Unauthorized_ReturnsFailureWithMessage()
    {
        var result = CopilotAccountResult.Unauthorized("bob");

        Assert.False(result.Success);
        Assert.Equal("bob", result.Username);
        Assert.Contains("expired or invalid", result.ErrorMessage);
    }

    [Fact]
    public void Error_ReturnsFailureWithCustomMessage()
    {
        var result = CopilotAccountResult.Error("charlie", "Connection timeout");

        Assert.False(result.Success);
        Assert.Equal("charlie", result.Username);
        Assert.Equal("Connection timeout", result.ErrorMessage);
    }

    // --- Helpers ---
    private static string BuildFullJson(
        int entitlement = 2000,
        int remaining = 500,
        string? resetDate = null)
    {
        resetDate ??= "2026-06-01T00:00:00Z";
        return $$"""
        {
            "login": "testuser",
            "copilot_plan": "individual_pro",
            "organization_login_list": ["org1", "org2"],
            "quota_reset_date_utc": "{{resetDate}}",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": {{entitlement}},
                    "remaining": {{remaining}},
                    "overage_count": 0,
                    "overage_permitted": false,
                    "percent_remaining": 25.0,
                    "unlimited": false,
                    "quota_id": "premium-test",
                    "timestamp_utc": "2026-05-14T00:00:00Z"
                },
                "chat": {
                    "entitlement": 1000,
                    "remaining": 800,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "percent_remaining": 80.0,
                    "unlimited": false,
                    "quota_id": "chat-test",
                    "timestamp_utc": "2026-05-14T00:00:00Z"
                }
            }
        }
        """;
    }
}
