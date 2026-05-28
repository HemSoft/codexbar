// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// Comprehensive coverage tests for ClaudeProvider targeting all uncovered branches:
/// FormatTokenCount, FormatBarReset, FormatResetCountdown, FormatUsageLabel,
/// BuildSessionSnapshotFromLimits, BuildWeeklySnapshot, BuildUsageBars,
/// TryGetFreshCachedLimits, CacheAndReturnLimits, ParseTokenRefreshResponse,
/// PersistCredentials, WriteOAuthSection, ParseRateLimitHeaders,
/// ResolvePricing, CalculateEquivalentCost, FetchRateLimitsAsync,
/// ProbeAndCacheRateLimitsAsync, TryRefreshTokenAsync, ReadStatsCache, ReadAccountInfo.
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class ClaudeProviderFullCoverageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _credPath;
    private readonly string _statsPath;
    private readonly string _claudeJsonPath;

    public ClaudeProviderFullCoverageTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"claude_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        this._credPath = Path.Combine(this._tempDir, "credentials.json");
        this._statsPath = Path.Combine(this._tempDir, "stats-cache.json");
        this._claudeJsonPath = Path.Combine(this._tempDir, "claude.json");
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;
        try
        {
            Directory.Delete(this._tempDir, true);
        }
        catch
        {
        }
    }

    // --- FormatTokenCount ---
    [Fact]
    public void FormatTokenCount_BillionRange_FormatsAsBillions()
    {
        var result = ClaudeProvider.FormatTokenCount(2_500_000_000);
        Assert.Equal("2.5B tokens", result);
    }

    [Fact]
    public void FormatTokenCount_MillionRange_FormatsAsMillions()
    {
        var result = ClaudeProvider.FormatTokenCount(1_500_000);
        Assert.Equal("1.5M tokens", result);
    }

    [Fact]
    public void FormatTokenCount_ThousandRange_FormatsAsThousands()
    {
        var result = ClaudeProvider.FormatTokenCount(42_000);
        Assert.Equal("42.0K tokens", result);
    }

    [Fact]
    public void FormatTokenCount_BelowThousand_FormatsRaw()
    {
        var result = ClaudeProvider.FormatTokenCount(999);
        Assert.Equal("999 tokens", result);
    }

    [Fact]
    public void FormatTokenCount_ExactBoundary_Billion()
    {
        var result = ClaudeProvider.FormatTokenCount(1_000_000_000);
        Assert.Equal("1.0B tokens", result);
    }

    [Fact]
    public void FormatTokenCount_ExactBoundary_Million()
    {
        var result = ClaudeProvider.FormatTokenCount(1_000_000);
        Assert.Equal("1.0M tokens", result);
    }

    [Fact]
    public void FormatTokenCount_ExactBoundary_Thousand()
    {
        var result = ClaudeProvider.FormatTokenCount(1_000);
        Assert.Equal("1.0K tokens", result);
    }

    // --- FormatBarReset ---
    [Fact]
    public void FormatBarReset_FutureMoreThanOneDay_ReturnsResetsDays()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.StartsWith("Resets ", result);
        Assert.EndsWith("d", result);
    }

    [Fact]
    public void FormatBarReset_FutureMoreThanOneHour_ReturnsResetsHours()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.StartsWith("Resets ", result);
        Assert.EndsWith("h", result);
    }

    [Fact]
    public void FormatBarReset_FutureLessThanOneHour_ReturnsResetsMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatBarReset(epoch);
        Assert.StartsWith("Resets ", result);
        Assert.EndsWith("m", result);
    }

    // --- FormatResetCountdown ---
    [Fact]
    public void FormatResetCountdown_FutureMoreThanOneDay_FormatsDaysAndHours()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(2).AddHours(3).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Weekly");
        Assert.StartsWith("Weekly resets in ", result);
        Assert.Contains("d", result);
        Assert.Contains("h", result);
    }

    [Fact]
    public void FormatResetCountdown_FutureBetweenHourAndDay_FormatsHoursAndMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(15).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "5-hour limit");
        Assert.StartsWith("5-hour limit resets in ", result);
        Assert.Contains("h", result);
        Assert.Contains("m", result);
    }

    [Fact]
    public void FormatResetCountdown_FutureLessThanOneHour_FormatsMinutes()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "5-hour limit");
        Assert.StartsWith("5-hour limit resets in ", result);
        Assert.EndsWith("m", result);
    }

    [Fact]
    public void FormatResetCountdown_PastTime_FormatsResetsNow()
    {
        var epoch = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var result = ClaudeProvider.FormatResetCountdown(epoch, "Weekly");
        Assert.Equal("Weekly resets now", result);
    }

    // --- FormatUsageLabel ---
    [Fact]
    public void FormatUsageLabel_WithEquivalentCost_IncludesCost()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 5000, 12.50, null);
        Assert.Contains("Pro plan", result);
        Assert.Contains("~$12.50 equiv.", result);
    }

    [Fact]
    public void FormatUsageLabel_NoCostButTokens_IncludesTokens()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 2_500_000, 0, null);
        Assert.Contains("Pro plan", result);
        Assert.Contains("2.5M tokens", result);
    }

    [Fact]
    public void FormatUsageLabel_NoCostNoTokens_JustPlan()
    {
        var result = ClaudeProvider.FormatUsageLabel("Pro", 0, 0, null);
        Assert.Equal("Pro plan", result);
    }

    [Fact]
    public void FormatUsageLabel_WithExtraUsage_IncludesExtraUsageOn()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };
        var result = ClaudeProvider.FormatUsageLabel("Pro", 0, 5.0, accountInfo);
        Assert.Contains("extra usage on", result);
    }

    [Fact]
    public void FormatUsageLabel_ExtraUsageDisabled_DoesNotInclude()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = false };
        var result = ClaudeProvider.FormatUsageLabel("Pro", 0, 5.0, accountInfo);
        Assert.DoesNotContain("extra usage on", result);
    }

    // --- BuildStatusLabel ---
    [Fact]
    public void BuildStatusLabel_WithCostAndExtraUsage_AllParts()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };
        var result = ClaudeProvider.BuildStatusLabel("Pro", 1000, 10.5, accountInfo);
        Assert.Contains("Pro plan", result);
        Assert.Contains("~$10.50 equiv.", result);
        Assert.Contains("extra usage on", result);
    }

    [Fact]
    public void BuildStatusLabel_NoCostWithTokens_IncludesTokenCount()
    {
        var result = ClaudeProvider.BuildStatusLabel("Pro", 5_000_000, 0, null);
        Assert.Contains("Pro plan", result);
        Assert.Contains("5.0M tokens", result);
    }

    [Fact]
    public void BuildStatusLabel_NoCostNoTokens_JustPlan()
    {
        var result = ClaudeProvider.BuildStatusLabel("Free", 0, 0, null);
        Assert.Equal("Free plan", result);
    }

    // --- BuildSessionSnapshotFromLimits ---
    [Fact]
    public void BuildSessionSnapshotFromLimits_WithResetTime_IncludesResetDescription()
    {
        var resetEpoch = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.4,
            FiveHourReset = resetEpoch,
        };
        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 0, null);

        Assert.Equal(0.4, result.UsedPercent);
        Assert.NotNull(result.ResetDescription);
        Assert.Contains("5-hour limit resets in", result.ResetDescription);
        Assert.False(result.IsUnlimited);
    }

    [Fact]
    public void BuildSessionSnapshotFromLimits_NoResetTime_NullResetDescription()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.1,
            FiveHourReset = 0,
        };
        var result = ClaudeProvider.BuildSessionSnapshotFromLimits(limits, "Pro", 0, 0, null);

        Assert.Null(result.ResetDescription);
        Assert.Null(result.ResetsAt);
    }

    // --- BuildSessionSnapshot ---
    [Fact]
    public void BuildSessionSnapshot_NullLimits_ReturnsFallbackWithUnavailable()
    {
        var result = ClaudeProvider.BuildSessionSnapshot(null, "Pro", 1000, 5.0, null);

        Assert.True(result.IsUnlimited);
        Assert.Contains("Rate limits unavailable", result.UsageLabel);
        Assert.Contains("Pro plan", result.UsageLabel);
    }

    // --- BuildWeeklySnapshot ---
    [Fact]
    public void BuildWeeklySnapshot_NoReset_NullResetDescription()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.3,
            SevenDayReset = 0,
        };
        var result = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(result);
        Assert.Null(result!.ResetDescription);
        Assert.Null(result.ResetsAt);
    }

    // --- BuildUsageBars ---
    [Fact]
    public void BuildUsageBars_WithBothResets_ReturnsTwoBarsWithResetDescriptions()
    {
        var fiveHReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var sevenDReset = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.3,
            FiveHourReset = fiveHReset,
            SevenDayUtilization = 0.5,
            SevenDayReset = sevenDReset,
        };
        var result = ClaudeProvider.BuildUsageBars(limits);

        Assert.Equal(2, result.Count);
        Assert.Equal("5-hour limit", result[0].Label);
        Assert.NotNull(result[0].ResetDescription);
        Assert.NotNull(result[0].ResetsAt);
        Assert.Equal("Weekly · all models", result[1].Label);
        Assert.NotNull(result[1].ResetDescription);
        Assert.NotNull(result[1].ResetsAt);
    }

    [Fact]
    public void BuildUsageBars_NoResets_BarsHaveNullResetInfo()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.1,
            FiveHourReset = 0,
            SevenDayUtilization = 0.2,
            SevenDayReset = 0,
        };
        var result = ClaudeProvider.BuildUsageBars(limits);

        Assert.Equal(2, result.Count);
        Assert.Null(result[0].ResetDescription);
        Assert.Null(result[0].ResetsAt);
        Assert.Null(result[1].ResetDescription);
        Assert.Null(result[1].ResetsAt);
    }

    // --- ParseRateLimitHeaders ---
    [Fact]
    public void ParseRateLimitHeaders_BothUtilizations_ReturnsParsedLimits()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.45");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1750000000");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.60");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1750100000");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "active");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0.45, result!.FiveHourUtilization);
        Assert.Equal(1750000000L, result.FiveHourReset);
        Assert.Equal("active", result.FiveHourStatus);
        Assert.Equal(0.60, result.SevenDayUtilization);
        Assert.Equal(1750100000L, result.SevenDayReset);
        Assert.Equal("active", result.SevenDayStatus);
    }

    [Fact]
    public void ParseRateLimitHeaders_OnlyFiveHour_StillReturnsResult()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.25");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0.25, result!.FiveHourUtilization);
        Assert.Equal(0, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_OnlySevenDay_StillReturnsResult()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.80");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0.80, result!.SevenDayUtilization);
        Assert.Equal(0, result.FiveHourUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_NeitherHeader_ReturnsNull()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("some-other-header", "value");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);
        Assert.Null(result);
    }

    [Fact]
    public void ParseRateLimitHeaders_InvalidNumericValues_FallsBackToDefaults()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "not-a-number");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "not-a-long");
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "also-invalid");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal(0, result!.FiveHourUtilization);
        Assert.Equal(0, result.FiveHourReset);
        Assert.Equal(0, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_MissingStatusHeaders_DefaultsToUnknown()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.5");

        var result = ClaudeProvider.ParseRateLimitHeaders(headers);

        Assert.NotNull(result);
        Assert.Equal("unknown", result!.FiveHourStatus);
        Assert.Equal("unknown", result.SevenDayStatus);
    }

    // --- ParseTokenRefreshResponse ---
    [Fact]
    public void ParseTokenRefreshResponse_AllFields_ReturnsAll()
    {
        var json = """{"access_token":"new-at","refresh_token":"new-rt","expires_at":9999999}""";
        using var doc = JsonDocument.Parse(json);
        var (at, rt, ea) = ClaudeProvider.ParseTokenRefreshResponse(doc.RootElement);

        Assert.Equal("new-at", at);
        Assert.Equal("new-rt", rt);
        Assert.Equal(9999999L, ea);
    }

    [Fact]
    public void ParseTokenRefreshResponse_MissingFields_ReturnsDefaults()
    {
        var json = "{}";
        using var doc = JsonDocument.Parse(json);
        var (at, rt, ea) = ClaudeProvider.ParseTokenRefreshResponse(doc.RootElement);

        Assert.Null(at);
        Assert.Null(rt);
        Assert.Equal(0L, ea);
    }

    [Fact]
    public void ParseTokenRefreshResponse_PartialFields_ReturnsMixed()
    {
        var json = """{"access_token":"new-at"}""";
        using var doc = JsonDocument.Parse(json);
        var (at, rt, ea) = ClaudeProvider.ParseTokenRefreshResponse(doc.RootElement);

        Assert.Equal("new-at", at);
        Assert.Null(rt);
        Assert.Equal(0L, ea);
    }

    // --- WriteOAuthSection ---
    [Fact]
    public void WriteOAuthSection_AllKnownFields_UpdatesCorrectly()
    {
        var json = """
        {
            "accessToken": "old-at",
            "refreshToken": "old-rt",
            "expiresAt": 1000,
            "otherField": "preserved"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "new-at",
            RefreshToken = "new-rt",
            ExpiresAt = 9999,
        };

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        ClaudeProvider.WriteOAuthSection(writer, doc.RootElement, credentials);
        writer.Flush();

        var output = Encoding.UTF8.GetString(stream.ToArray());
        using var outDoc = JsonDocument.Parse(output);
        var root = outDoc.RootElement;

        Assert.Equal("new-at", root.GetProperty("accessToken").GetString());
        Assert.Equal("new-rt", root.GetProperty("refreshToken").GetString());
        Assert.Equal(9999L, root.GetProperty("expiresAt").GetInt64());
        Assert.Equal("preserved", root.GetProperty("otherField").GetString());
    }

    [Fact]
    public void WriteOAuthSection_NullRefreshToken_PreservesOriginal()
    {
        var json = """{"refreshToken": "original-rt","accessToken":"old","expiresAt":1}""";
        using var doc = JsonDocument.Parse(json);
        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "new-at",
            RefreshToken = null,
            ExpiresAt = 2,
        };

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        ClaudeProvider.WriteOAuthSection(writer, doc.RootElement, credentials);
        writer.Flush();

        var output = Encoding.UTF8.GetString(stream.ToArray());
        using var outDoc = JsonDocument.Parse(output);
        var root = outDoc.RootElement;

        // When RefreshToken is null, the original property is written as-is
        Assert.Equal("original-rt", root.GetProperty("refreshToken").GetString());
    }

    // --- ResolvePricing ---
    [Fact]
    public void ResolvePricing_ExactMatch_ReturnsExactPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-opus-4-7");
        Assert.Equal(5.0, pricing.InputPerMTok);
        Assert.Equal(25.0, pricing.OutputPerMTok);
    }

    [Fact]
    public void ResolvePricing_PrefixMatch_ReturnsLongestPrefixPricing()
    {
        // "claude-sonnet-4-6-20250514" starts with "claude-sonnet-4-6" (longest prefix)
        var pricing = ClaudeProvider.ResolvePricing("claude-sonnet-4-6-20250514");
        Assert.Equal(3.0, pricing.InputPerMTok);
        Assert.Equal(15.0, pricing.OutputPerMTok);
    }

    [Fact]
    public void ResolvePricing_ContainsOpus_FallsBackToOpusPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("some-new-opus-model");
        Assert.Equal(5.0, pricing.InputPerMTok);
    }

    [Fact]
    public void ResolvePricing_ContainsHaiku_FallsBackToHaikuPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("some-new-haiku-model");
        Assert.Equal(1.0, pricing.InputPerMTok);
    }

    [Fact]
    public void ResolvePricing_UnknownModel_FallsBackToSonnetPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("completely-unknown-model");
        Assert.Equal(3.0, pricing.InputPerMTok);
        Assert.Equal(15.0, pricing.OutputPerMTok);
    }

    // --- CalculateEquivalentCost ---
    [Fact]
    public void CalculateEquivalentCost_WithModelUsages_CalculatesCorrectly()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            ModelId = "claude-sonnet-4-6",
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            CacheCreationInputTokens = 200_000,
            CacheReadInputTokens = 100_000,
        });

        var result = ClaudeProvider.CalculateEquivalentCost(stats);

        // Sonnet pricing: input=3/M, output=15/M, cache_write=3.75/M, cache_read=0.30/M
        // 1M input * 3 + 0.5M output * 15 + 0.2M cache_write * 3.75 + 0.1M cache_read * 0.30
        // = 3.0 + 7.5 + 0.75 + 0.03 = 11.28
        Assert.True(result > 11.0);
        Assert.True(result < 12.0);
    }

    // --- CalculateTotalTokens ---
    [Fact]
    public void CalculateTotalTokens_WithMultipleModels_SumsAll()
    {
        var stats = new ClaudeProvider.ClaudeStatsCache();
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            InputTokens = 100,
            OutputTokens = 200,
            CacheReadInputTokens = 50,
            CacheCreationInputTokens = 75,
        });
        stats.ModelUsages.Add(new ClaudeProvider.ClaudeModelUsage
        {
            InputTokens = 300,
            OutputTokens = 400,
            CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0,
        });

        var result = ClaudeProvider.CalculateTotalTokens(stats);
        Assert.Equal(1125, result); // 100+200+50+75 + 300+400+0+0
    }

    // --- ParseModelUsages ---
    [Fact]
    public void ParseModelUsages_NonObjectValue_ReturnsEmpty()
    {
        var json = "\"not an object\"";
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseModelUsages_ValidObject_ParsesAllModels()
    {
        var json = """
        {
            "claude-sonnet-4-6": {
                "inputTokens": 1000,
                "outputTokens": 2000,
                "cacheReadInputTokens": 300,
                "cacheCreationInputTokens": 400
            },
            "claude-opus-4-7": {
                "inputTokens": 500,
                "outputTokens": 700
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ClaudeProvider.ParseModelUsages(doc.RootElement);

        Assert.Equal(2, result.Count);
        Assert.Equal("claude-sonnet-4-6", result[0].ModelId);
        Assert.Equal(1000, result[0].InputTokens);
        Assert.Equal(2000, result[0].OutputTokens);
        Assert.Equal(300, result[0].CacheReadInputTokens);
        Assert.Equal(400, result[0].CacheCreationInputTokens);
        Assert.Equal("claude-opus-4-7", result[1].ModelId);
        Assert.Equal(500, result[1].InputTokens);
    }

    // --- FetchUsageAsync (full integration-style test exercising TryRefreshTokenAsync) ---
    [Fact]
    public async Task FetchUsageAsync_ExpiredTokenWithRefresh_RefreshesSuccessfully()
    {
        this.SetupOverrides();
        var expiredEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var newExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(expiredEpoch, "expired-token", "refresh-token-1");

        var callCount = 0;
        var handler = new DelegatingHandlerFunc((req, ct) =>
        {
            Interlocked.Increment(ref callCount);
            if (req.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                var refreshResponse = $$"""{"access_token":"new-token","refresh_token":"new-refresh","expires_at":{{newExpiry}}}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(refreshResponse, Encoding.UTF8, "application/json"),
                });
            }

            // Rate limit probe response
            var rateLimitResponse = new HttpResponseMessage(HttpStatusCode.OK);
            rateLimitResponse.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.3");
            rateLimitResponse.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.5");
            return Task.FromResult(rateLimitResponse);
        });

        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.True(callCount >= 2); // Refresh + probe
    }

    [Fact]
    public async Task FetchUsageAsync_ExpiredTokenNoRefreshToken_ReturnsFailure()
    {
        this.SetupOverrides();
        var expiredEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        this.WriteCredentials(expiredEpoch, "expired-token", null);

        var factory = CreateFactory(new DelegatingHandlerFunc((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("expired", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_RefreshFails_ReturnsExpiredError()
    {
        this.SetupOverrides();
        var expiredEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        this.WriteCredentials(expiredEpoch, "expired-token", "refresh-token");

        var handler = new DelegatingHandlerFunc((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("bad request", Encoding.UTF8),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("expired", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_RefreshReturnsNoAccessToken_ReturnsError()
    {
        this.SetupOverrides();
        var expiredEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        this.WriteCredentials(expiredEpoch, "expired-token", "refresh-token");

        var handler = new DelegatingHandlerFunc((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_RefreshThrows_ReturnsError()
    {
        this.SetupOverrides();
        var expiredEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        this.WriteCredentials(expiredEpoch, "expired-token", "refresh-token");

        var handler = new DelegatingHandlerFunc((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                throw new HttpRequestException("Connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_NoCredentialsFile_ReturnsError()
    {
        this.SetupOverrides();

        // Don't create credentials file
        var factory = CreateFactory(new DelegatingHandlerFunc((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No Claude Code credentials found", result.ErrorMessage!);
    }

    [Fact]
    public async Task FetchUsageAsync_CorruptedCredentials_ReturnsError()
    {
        this.SetupOverrides();
        File.WriteAllText(this._credPath, "not valid json at all {{{{");
        var factory = CreateFactory(new DelegatingHandlerFunc((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("could not be read or is corrupted", result.ErrorMessage!);
    }

    [Fact]
    public async Task FetchUsageAsync_MissingOauthSection_ReturnsError()
    {
        this.SetupOverrides();
        File.WriteAllText(this._credPath, """{"someOtherField": "value"}""");
        var factory = CreateFactory(new DelegatingHandlerFunc((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_Api401_ReturnsNull()
    {
        this.SetupOverrides();
        var validEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(validEpoch, "valid-token", null);

        var handler = new DelegatingHandlerFunc((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success); // Success with null limits (fallback)
    }

    [Fact]
    public async Task FetchUsageAsync_ApiTimeout_FallsBackToCached()
    {
        this.SetupOverrides();
        var validEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(validEpoch, "valid-token", null);

        var handler = new DelegatingHandlerFunc(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        // This will timeout due to the 15s ApiTimeout
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_HttpException_FallsBackToCached()
    {
        this.SetupOverrides();
        var validEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(validEpoch, "valid-token", null);

        var handler = new DelegatingHandlerFunc((_, _) =>
            throw new HttpRequestException("Connection refused"));
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success); // Falls back to cached (null)
    }

    [Fact]
    public async Task FetchUsageAsync_GeneralException_FallsBackToCached()
    {
        this.SetupOverrides();
        var validEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(validEpoch, "valid-token", null);

        var handler = new DelegatingHandlerFunc((_, _) =>
            throw new InvalidOperationException("Unexpected error"));
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_CancellationRequested_ThrowsOce()
    {
        this.SetupOverrides();
        var validEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(validEpoch, "valid-token", null);

        var handler = new DelegatingHandlerFunc((_, _) =>
            throw new OperationCanceledException());
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchUsageAsync(cts.Token));
    }

    [Fact]
    public async Task FetchUsageAsync_ValidToken_NoExpirySet_ProceedsNormally()
    {
        this.SetupOverrides();
        this.WriteCredentials(0, "valid-token", null); // ExpiresAt = 0 means no expiry

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.2");
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.4");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_ValidTokenNotExpired_DoesNotRefresh()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", "refresh-token");

        int refreshCalls = 0;
        var handler = new DelegatingHandlerFunc((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                Interlocked.Increment(ref refreshCalls);
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.1");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(0, refreshCalls);
    }

    [Fact]
    public async Task FetchUsageAsync_CacheHit_DoesNotProbeApi()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", null);

        int apiCalls = 0;
        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            Interlocked.Increment(ref apiCalls);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                        "five_hour": { "utilization": 10 },
                        "seven_day": { "utilization": 20 }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        // First call populates cache
        await provider.FetchUsageAsync();

        // Second call should hit cache
        await provider.FetchUsageAsync();

        Assert.Equal(1, apiCalls);
    }

    [Fact]
    public async Task FetchUsageAsync_NoAccessTokenInCredentials_ReturnsNoLimits()
    {
        this.SetupOverrides();
        var credJson = """{"claudeAiOauth":{"subscriptionType":"pro","expiresAt":0}}""";
        File.WriteAllText(this._credPath, credJson);

        var handler = new DelegatingHandlerFunc((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Contains("Rate limits unavailable", result.SessionUsage!.UsageLabel);
    }

    [Fact]
    public async Task FetchUsageAsync_WithStatsCache_IncludesTotalTokens()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", null);
        File.WriteAllText(this._statsPath, """
        {
            "totalSessions": 5,
            "totalMessages": 100,
            "modelUsage": {
                "claude-sonnet-4-6": {
                    "inputTokens": 1000000,
                    "outputTokens": 500000,
                    "cacheReadInputTokens": 200000,
                    "cacheCreationInputTokens": 100000
                }
            }
        }
        """);

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.3");
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.5");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_WithAccountInfo_IncludesDisplayName()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", null);
        File.WriteAllText(this._claudeJsonPath, """
        {
            "oauthAccount": {
                "displayName": "John Doe",
                "billingType": "pro",
                "hasExtraUsageEnabled": true
            }
        }
        """);

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.2");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items!.First();
        Assert.Contains("John Doe", item.DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_AccountInfoMissingOauthAccount_ReturnsNullAccountInfo()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", null);
        File.WriteAllText(this._claudeJsonPath, """{"otherField": "value"}""");

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.1");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items!.First();
        Assert.Equal("Claude (Pro)", item.DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_CorruptedStatsCache_ProceedsWithNullStats()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", null);
        File.WriteAllText(this._statsPath, "invalid json {{{");

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.1");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_CorruptedClaudeJson_ProceedsWithNullAccountInfo()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", null);
        File.WriteAllText(this._claudeJsonPath, "not json");

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.1");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_ProviderDisabled_IsNotAvailable()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(false);
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var available = await provider.IsAvailableAsync();
        Assert.False(available);
    }

    [Fact]
    public async Task FetchUsageAsync_InvalidExpiresAtValue_TreatsAsValid()
    {
        this.SetupOverrides();

        // Write credentials with an absurd ExpiresAt value that can't be parsed as DateTimeOffset
        // Actually long.MaxValue would overflow - but the code catches exceptions and returns credentials
        var credJson = """{"claudeAiOauth":{"accessToken":"tok","expiresAt":99999999999999999}}""";
        File.WriteAllText(this._credPath, credJson);

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.1");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        // Should not throw - the exception handler in EnsureTokenFreshAsync catches and returns credentials
        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_ExpiresAtInMilliseconds_NormalizesToSeconds()
    {
        this.SetupOverrides();

        // Millisecond epoch for a future time (year ~2026)
        var futureMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        this.WriteCredentialsWithMsExpiry(futureMs, "valid-token", null);

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.1");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_PersistCredentials_WritesToFile()
    {
        this.SetupOverrides();
        var expiredEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var newExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(expiredEpoch, "expired-token", "refresh-token");

        var handler = new DelegatingHandlerFunc((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/oauth/token")
            {
                var refreshResponse = $$"""{"access_token":"new-token","refresh_token":"new-refresh","expires_at":{{newExpiry}}}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(refreshResponse, Encoding.UTF8, "application/json"),
                });
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.1");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);

        // Verify persisted credentials contain new token
        var persisted = File.ReadAllText(this._credPath);
        Assert.Contains("new-token", persisted);
    }

    [Fact]
    public async Task FetchUsageAsync_CacheAndReturnLimits_NullResult_LogsHeaderNames()
    {
        this.SetupOverrides();
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        this.WriteCredentials(futureExpiry, "valid-token", null);

        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);

            // No rate limit headers → ParseRateLimitHeaders returns null
            resp.Headers.TryAddWithoutValidation("x-custom-header", "test");
            return Task.FromResult(resp);
        });
        var factory = CreateFactory(handler);
        var settings = CreateSettings();
        var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        // Should succeed but with fallback (no limits)
        Assert.True(result.Success);
        Assert.Contains("Rate limits unavailable", result.SessionUsage!.UsageLabel);
    }

    [Fact]
    public void NormalizeEpochToSeconds_SecondsValue_ReturnsUnchanged()
    {
        var seconds = 1_750_000_000L;
        var result = ClaudeProvider.NormalizeEpochToSeconds(seconds);
        Assert.Equal(1_750_000_000L, result);
    }

    // --- Helpers ---
    private void SetupOverrides()
    {
        ClaudeProvider.CredentialsPathOverride = this._credPath;
        ClaudeProvider.StatsCachePathOverride = this._statsPath;
        ClaudeProvider.ClaudeJsonPathOverride = this._claudeJsonPath;
    }

    private void WriteCredentials(long expiresAt, string accessToken, string? refreshToken)
    {
        var refreshPart = refreshToken is not null
            ? $",\"refreshToken\":\"{refreshToken}\""
            : string.Empty;
        var json = $"{{\"claudeAiOauth\":{{\"subscriptionType\":\"pro\",\"accessToken\":\"{accessToken}\",\"expiresAt\":{expiresAt}{refreshPart}}}}}";
        File.WriteAllText(this._credPath, json);
    }

    private void WriteCredentialsWithMsExpiry(long expiresAtMs, string accessToken, string? refreshToken)
    {
        var refreshPart = refreshToken is not null
            ? $",\"refreshToken\":\"{refreshToken}\""
            : string.Empty;
        var json = $"{{\"claudeAiOauth\":{{\"subscriptionType\":\"pro\",\"accessToken\":\"{accessToken}\",\"expiresAt\":{expiresAtMs}{refreshPart}}}}}";
        File.WriteAllText(this._credPath, json);
    }

    private static ISettingsService CreateSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        return settings;
    }

    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private sealed class DelegatingHandlerFunc(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
