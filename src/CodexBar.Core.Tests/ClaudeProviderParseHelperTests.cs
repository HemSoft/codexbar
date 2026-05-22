// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text;
using System.Text.Json;
using CodexBar.Core.Providers.Claude;
using Xunit;

/// <summary>
/// Direct tests for the extracted parsing helpers in ClaudeProvider:
/// ParseCredentials, ParseAccountInfo, ParseModelUsages, WriteOAuthSection.
/// These ensure high branch coverage for each helper method independently.
/// </summary>
public class ClaudeProviderParseHelperTests
{
    // --- ParseCredentials ---
    [Fact]
    public void ParseCredentials_AllFieldsPresent_ReturnsFullCredentials()
    {
        var json = """
        {
            "subscriptionType": "pro",
            "rateLimitTier": "tier-1",
            "expiresAt": 1750000000,
            "accessToken": "test-token",
            "refreshToken": "test-refresh"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseCredentials(doc.RootElement);

        Assert.Equal("pro", result.SubscriptionType);
        Assert.Equal("tier-1", result.RateLimitTier);
        Assert.Equal(1750000000L, result.ExpiresAt);
        Assert.Equal("test-token", result.AccessToken);
        Assert.Equal("test-refresh", result.RefreshToken);
    }

    [Fact]
    public void ParseCredentials_AllFieldsMissing_ReturnsDefaults()
    {
        var json = "{}";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseCredentials(doc.RootElement);

        Assert.Null(result.SubscriptionType);
        Assert.Null(result.RateLimitTier);
        Assert.Equal(0L, result.ExpiresAt);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public void ParseCredentials_PartialFields_ReturnsMixedDefaults()
    {
        var json = """
        {
            "accessToken": "only-token",
            "expiresAt": 999
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseCredentials(doc.RootElement);

        Assert.Equal("only-token", result.AccessToken);
        Assert.Equal(999L, result.ExpiresAt);
        Assert.Null(result.SubscriptionType);
        Assert.Null(result.RateLimitTier);
        Assert.Null(result.RefreshToken);
    }

    // --- ParseAccountInfo ---
    [Fact]
    public void ParseAccountInfo_AllFieldsPresent_ReturnsAccountInfo()
    {
        var json = """
        {
            "displayName": "Test User",
            "billingType": "pro",
            "hasExtraUsageEnabled": true
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseAccountInfo(doc.RootElement);

        Assert.Equal("Test User", result.DisplayName);
        Assert.Equal("pro", result.BillingType);
        Assert.True(result.HasExtraUsageEnabled);
    }

    [Fact]
    public void ParseAccountInfo_AllFieldsMissing_ReturnsDefaults()
    {
        var json = "{}";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseAccountInfo(doc.RootElement);

        Assert.Null(result.DisplayName);
        Assert.Null(result.BillingType);
        Assert.False(result.HasExtraUsageEnabled);
    }

    [Fact]
    public void ParseAccountInfo_ExtraUsageFalse_ReturnsFalse()
    {
        var json = """{"hasExtraUsageEnabled": false}""";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseAccountInfo(doc.RootElement);

        Assert.False(result.HasExtraUsageEnabled);
    }

    [Fact]
    public void ParseAccountInfo_PartialFields_ReturnsAvailableValues()
    {
        var json = """{"displayName": "Partial"}""";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseAccountInfo(doc.RootElement);

        Assert.Equal("Partial", result.DisplayName);
        Assert.Null(result.BillingType);
        Assert.False(result.HasExtraUsageEnabled);
    }

    // --- ParseModelUsages ---
    [Fact]
    public void ParseModelUsages_ObjectWithMultipleModels_ReturnsAllUsages()
    {
        var json = """
        {
            "claude-sonnet-4-5": {
                "inputTokens": 10000,
                "outputTokens": 5000,
                "cacheReadInputTokens": 200,
                "cacheCreationInputTokens": 300
            },
            "claude-haiku-4-5": {
                "inputTokens": 2000,
                "outputTokens": 1000,
                "cacheReadInputTokens": 50,
                "cacheCreationInputTokens": 100
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);

        Assert.Equal(2, result.Count);
        Assert.Equal("claude-sonnet-4-5", result[0].ModelId);
        Assert.Equal(10000L, result[0].InputTokens);
        Assert.Equal(5000L, result[0].OutputTokens);
        Assert.Equal(200L, result[0].CacheReadInputTokens);
        Assert.Equal(300L, result[0].CacheCreationInputTokens);
        Assert.Equal("claude-haiku-4-5", result[1].ModelId);
        Assert.Equal(2000L, result[1].InputTokens);
    }

    [Fact]
    public void ParseModelUsages_EmptyObject_ReturnsEmptyList()
    {
        var json = "{}";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseModelUsages_NonObjectValueKind_ReturnsEmptyList()
    {
        var json = "[1, 2, 3]";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseModelUsages_NullValueKind_ReturnsEmptyList()
    {
        var json = "null";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseModelUsages_MissingTokenFields_DefaultsToZero()
    {
        var json = """
        {
            "claude-sonnet-4-5": {
                "inputTokens": 500
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);

        Assert.Single(result);
        Assert.Equal("claude-sonnet-4-5", result[0].ModelId);
        Assert.Equal(500L, result[0].InputTokens);
        Assert.Equal(0L, result[0].OutputTokens);
        Assert.Equal(0L, result[0].CacheReadInputTokens);
        Assert.Equal(0L, result[0].CacheCreationInputTokens);
    }

    [Fact]
    public void ParseModelUsages_SingleModelAllFields_ParsesCorrectly()
    {
        var json = """
        {
            "claude-opus-4-5": {
                "inputTokens": 99999,
                "outputTokens": 88888,
                "cacheReadInputTokens": 77777,
                "cacheCreationInputTokens": 66666
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);

        Assert.Single(result);
        Assert.Equal("claude-opus-4-5", result[0].ModelId);
        Assert.Equal(99999L, result[0].InputTokens);
        Assert.Equal(88888L, result[0].OutputTokens);
        Assert.Equal(77777L, result[0].CacheReadInputTokens);
        Assert.Equal(66666L, result[0].CacheCreationInputTokens);
    }

    // --- WriteOAuthSection ---
    [Fact]
    public void WriteOAuthSection_ReplacesAccessTokenAndExpiresAt()
    {
        var inputJson = """
        {
            "accessToken": "old-token",
            "expiresAt": 1000000,
            "subscriptionType": "pro"
        }
        """;
        using var doc = JsonDocument.Parse(inputJson);
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "new-token",
            RefreshToken = "new-refresh",
            ExpiresAt = 2000000,
        };

        var output = WriteOAuthToString(doc.RootElement, credentials);

        Assert.Contains("\"accessToken\": \"new-token\"", output);
        Assert.Contains("\"expiresAt\": 2000000", output);
        Assert.Contains("\"subscriptionType\": \"pro\"", output);
        Assert.DoesNotContain("old-token", output);
    }

    [Fact]
    public void WriteOAuthSection_ReplacesRefreshToken_WhenCredentialsHasRefreshToken()
    {
        var inputJson = """
        {
            "refreshToken": "old-refresh",
            "accessToken": "old-token",
            "expiresAt": 1000000
        }
        """;
        using var doc = JsonDocument.Parse(inputJson);
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "new-token",
            RefreshToken = "new-refresh",
            ExpiresAt = 2000000,
        };

        var output = WriteOAuthToString(doc.RootElement, credentials);

        Assert.Contains("\"refreshToken\": \"new-refresh\"", output);
        Assert.DoesNotContain("old-refresh", output);
    }

    [Fact]
    public void WriteOAuthSection_PreservesOldRefreshToken_WhenCredentialsRefreshTokenIsNull()
    {
        var inputJson = """
        {
            "refreshToken": "keep-this",
            "accessToken": "old-token",
            "expiresAt": 1000000
        }
        """;
        using var doc = JsonDocument.Parse(inputJson);
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "new-token",
            RefreshToken = null,
            ExpiresAt = 2000000,
        };

        var output = WriteOAuthToString(doc.RootElement, credentials);

        Assert.Contains("keep-this", output);
    }

    [Fact]
    public void WriteOAuthSection_PreservesUnknownProperties()
    {
        var inputJson = """
        {
            "accessToken": "old-token",
            "expiresAt": 1000000,
            "rateLimitTier": "tier-1",
            "unknownFutureProp": "preserved-value"
        }
        """;
        using var doc = JsonDocument.Parse(inputJson);
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "new-token",
            RefreshToken = "new-refresh",
            ExpiresAt = 2000000,
        };

        var output = WriteOAuthToString(doc.RootElement, credentials);

        Assert.Contains("rateLimitTier", output);
        Assert.Contains("tier-1", output);
        Assert.Contains("unknownFutureProp", output);
        Assert.Contains("preserved-value", output);
    }

    [Fact]
    public void WriteOAuthSection_EmptyObject_WritesEmptyObject()
    {
        var inputJson = "{}";
        using var doc = JsonDocument.Parse(inputJson);
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "token",
            ExpiresAt = 123,
        };

        var output = WriteOAuthToString(doc.RootElement, credentials);

        Assert.Contains("{", output);
        Assert.Contains("}", output);
        Assert.DoesNotContain("accessToken", output);
    }

    private static string WriteOAuthToString(JsonElement oauthElement, ClaudeProvider.ClaudeCredentials credentials)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            ClaudeProvider.WriteOAuthSection(writer, oauthElement, credentials);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
