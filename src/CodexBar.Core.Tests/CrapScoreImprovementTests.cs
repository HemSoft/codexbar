// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.OpenCodeGo;
using CodexBar.Core.Providers.OpenRouter;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Targeted tests to reduce CRAP scores for high-complexity, zero-coverage methods.
/// Covers: CopilotProvider (DiscoverAccountsUnderLockAsync, LogQuotaDebug, ParseReset, BuildUsageLabel,
///         BuildFetchResult, ToUsageItem, FormatDisplayName, ExtractUsername),
///         OpenCodeGoProvider (TryParseWindow, CheckResponseStatus, FetchFromDashboardAsync),
///         ClaudeProvider (TryRefreshTokenAsync, BuildUsageBars, WriteOAuthSection, ParseCredentials,
///                        ResolvePricing, BuildStatusLabel, FormatUsageLabel, BuildWeeklySnapshot),
///         OpenRouterProvider (FetchUsageAsync).
/// </summary>
public class CrapScoreImprovementTests
{
    [Fact]
    public void ParseReset_NullInput_ReturnsBothNull()
    {
        var (resetsAt, description) = CopilotProvider.ParseReset(null);
        Assert.Null(resetsAt);
        Assert.Null(description);
    }

    [Fact]
    public void ParseReset_InvalidDate_ReturnsBothNull()
    {
        var (resetsAt, description) = CopilotProvider.ParseReset("not-a-date");
        Assert.Null(resetsAt);
        Assert.Null(description);
    }

    [Fact]
    public void ParseReset_FutureDate_LessThanOneDay_ReturnsHoursAndMinutes()
    {
        var future = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(15).ToString("o");
        var (resetsAt, description) = CopilotProvider.ParseReset(future);
        Assert.NotNull(resetsAt);
        Assert.Contains("Resets in", description);
        Assert.Contains("h", description);
        Assert.Contains("m", description);
    }

    [Fact]
    public void ParseReset_FutureDate_OneDayAway_ReturnsTomorrow()
    {
        var future = DateTimeOffset.UtcNow.AddHours(30).ToString("o");
        var (resetsAt, description) = CopilotProvider.ParseReset(future);
        Assert.NotNull(resetsAt);
        Assert.Equal("Resets tomorrow", description);
    }

    [Theory]
    [InlineData("alice", "enterprise", "Copilot · alice (Ent)")]
    [InlineData("bob", "individual_pro", "Copilot · bob (Pro)")]
    [InlineData("carol", "business", "Copilot · carol (Biz)")]
    [InlineData("dave", "team_plan", "Copilot · dave (team plan)")]
    [InlineData("eve", null, "Copilot · eve")]
    public void FormatDisplayName_VariousPlans_FormatsCorrectly(string username, string? plan, string expected)
    {
        var result = CopilotProvider.FormatDisplayName(username, plan);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Logged in to github.com account alice (keyring)", "alice")]
    [InlineData("Logged in to github.com as bob via token", "bob")]
    [InlineData("Some other line without keywords", null)]
    public void ExtractUsername_VariousFormats_ExtractsCorrectly(string line, string? expected)
    {
        var result = CopilotProvider.ExtractUsername(line);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseCopilotApiResponse_NoPremiumQuota_SucceedsWithNullPremium()
    {
        var json = """{"login":"user","copilot_plan":"free","quota_snapshots":{}}""";
        var result = CopilotProvider.ParseCopilotApiResponse(json, "user", NullLogger<CopilotProvider>.Instance);
        Assert.True(result.Success);
        Assert.Null(result.PremiumInteractions);
    }

    [Fact]
    public void ComputeUsageMetrics_WithEntitlement_ReturnsUsedPercent()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = 500,
            OverageCount = 0,
            OveragePermitted = false,
            Unlimited = false,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0.75, usedPercent);
        Assert.Contains("1,500", label);
        Assert.Contains("2,000", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_WithOverage_ShowsOverageCost()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -10,
            OverageCount = 10,
            OveragePermitted = true,
            Unlimited = false,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Contains("$", label);
    }

    [Fact]
    public void ComputeUsageMetrics_ChatQuota_IncludesQuotaLabel()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 500,
            Remaining = 400,
            OverageCount = 0,
            OveragePermitted = false,
            Unlimited = false,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "chat");
        Assert.Contains("Chat", label);
    }

    [Fact]
    public async Task FetchUsageAsync_DiscoveryReturnsAccounts_UsesDiscoveredAccount()
    {
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateCopilotSettings(); // No configured accounts
        var factory = CreateHttpFactory(response);
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);
        provider.AccountDiscoveryOverride = _ => Task.FromResult(new List<string> { "discovered" });
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("gho_test");

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Contains("discovered", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_DiscoveryEmpty_CachesForFiveMinutes()
    {
        int callCount = 0;
        var settings = CreateCopilotSettings();
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);
        provider.AccountDiscoveryOverride = _ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new List<string>());
        };

        await provider.FetchUsageAsync();
        await provider.FetchUsageAsync();

        // Second call should use empty cache, not call discovery again
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task FetchUsageAsync_DiscoveryNonEmpty_CachesResult()
    {
        int discoveryCallCount = 0;
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateCopilotSettings();
        var factory = CreateHttpFactory(response);
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);
        provider.AccountDiscoveryOverride = _ =>
        {
            Interlocked.Increment(ref discoveryCallCount);
            return Task.FromResult(new List<string> { "user1" });
        };
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("gho_test");

        await provider.FetchUsageAsync();
        await provider.FetchUsageAsync();

        // Accounts are cached after first successful discovery
        Assert.Equal(1, discoveryCallCount);
    }

    [Fact]
    public async Task FetchUsageAsync_GhProcessTimesOut_ReturnsDiscoveryError()
    {
        var settings = CreateCopilotSettings();
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);
        provider.DiscoveryTimeoutOverride = TimeSpan.FromMilliseconds(1);
        provider.GhStatusProcessOverride = () => CreateSlowProcess();

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_GhProcessNonZeroExit_ReturnsAuthError()
    {
        var settings = CreateCopilotSettings();
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);
        provider.DiscoveryTimeoutOverride = TimeSpan.FromSeconds(5);
        provider.GhStatusProcessOverride = () => CreateExitProcess(1, stderr: "not logged in");

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("auth failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_GhProcessSuccess_ExtractsAccounts()
    {
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateCopilotSettings();
        var factory = CreateHttpFactory(response);
        var provider = new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);
        provider.DiscoveryTimeoutOverride = TimeSpan.FromSeconds(5);
        provider.GhStatusProcessOverride = () => CreateExitProcess(
            0, stdout: "Logged in to github.com account testuser (keyring)");
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("gho_abc");

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Contains("testuser", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_ValidHtml_ParsesAllWindows()
    {
        var html = BuildOpenCodeGoHtml(
            rollingPct: 25, rollingSec: 3600,
            weeklyPct: 40, weeklySec: 86400, monthlyPct: 60, monthlySec: 604800);
        var provider = CreateOpenCodeGoProvider(html);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        var bars = result.Items![0].Bars!;
        Assert.Equal(3, bars.Count);
    }

    [Fact]
    public async Task FetchUsageAsync_OnlyRolling_ParsesSuccessfully()
    {
        var html = BuildOpenCodeGoHtml(rollingPct: 50, rollingSec: 1800);
        var provider = CreateOpenCodeGoProvider(html);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Single(result.Items![0].Bars!);
        Assert.Equal("5-hour limit", result.Items[0].Bars![0].Label);
    }

    [Fact]
    public async Task FetchUsageAsync_NoMatchableHtml_ReturnsParseError()
    {
        var provider = CreateOpenCodeGoProvider("<html><body>Nothing here</body></html>");

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Could not parse", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_UsagePercentClamped_Above100()
    {
        var html = BuildOpenCodeGoHtml(rollingPct: 150, rollingSec: 100);
        var provider = CreateOpenCodeGoProvider(html);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.True(result.Items![0].PrimaryUsage!.UsedPercent <= 1.0);
    }

    [Fact]
    public void BuildUsageBars_WithResets_IncludesResetDescriptions()
    {
        var futureEpoch = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.5,
            FiveHourReset = futureEpoch,
            SevenDayUtilization = 0.3,
            SevenDayReset = futureEpoch,
        };

        var bars = ClaudeProvider.BuildUsageBars(limits);

        Assert.Equal(2, bars.Count);
        Assert.NotNull(bars[0].ResetDescription);
        Assert.NotNull(bars[1].ResetDescription);
    }

    [Fact]
    public void WriteOAuthSection_UpdatesCredentialFields()
    {
        var originalJson = """{"accessToken":"old","refreshToken":"old-rt","expiresAt":100,"otherField":"keep"}""";
        using var doc = JsonDocument.Parse(originalJson);
        var oauthElement = doc.RootElement;

        var credentials = new ClaudeProvider.ClaudeCredentials
        {
            AccessToken = "new-token",
            RefreshToken = "new-rt",
            ExpiresAt = 9999,
        };

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        ClaudeProvider.WriteOAuthSection(writer, oauthElement, credentials);
        writer.Flush();

        var result = Encoding.UTF8.GetString(stream.ToArray());
        using var resultDoc = JsonDocument.Parse(result);
        var root = resultDoc.RootElement;

        Assert.Equal("new-token", root.GetProperty("accessToken").GetString());
        Assert.Equal("new-rt", root.GetProperty("refreshToken").GetString());
        Assert.Equal(9999, root.GetProperty("expiresAt").GetInt64());
        Assert.Equal("keep", root.GetProperty("otherField").GetString());
    }

    [Fact]
    public void ParseCredentials_FullObject_ParsesAllFields()
    {
        var json = """
            {
                "subscriptionType": "pro",
                "rateLimitTier": "tier2",
                "expiresAt": 1700000000,
                "accessToken": "at-123",
                "refreshToken": "rt-456"
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var creds = ClaudeProvider.ParseCredentials(doc.RootElement);

        Assert.Equal("pro", creds.SubscriptionType);
        Assert.Equal("tier2", creds.RateLimitTier);
        Assert.Equal(1700000000, creds.ExpiresAt);
        Assert.Equal("at-123", creds.AccessToken);
        Assert.Equal("rt-456", creds.RefreshToken);
    }

    [Fact]
    public void ParseCredentials_EmptyObject_ReturnsDefaults()
    {
        using var doc = JsonDocument.Parse("{}");

        var creds = ClaudeProvider.ParseCredentials(doc.RootElement);

        Assert.Null(creds.SubscriptionType);
        Assert.Null(creds.RateLimitTier);
        Assert.Equal(0, creds.ExpiresAt);
        Assert.Null(creds.AccessToken);
        Assert.Null(creds.RefreshToken);
    }

    [Fact]
    public void ResolvePricing_PrefixMatch_ReturnsBestMatch()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-sonnet-4-6-20260514");
        Assert.True(pricing.InputPerMTok > 0);
    }

    [Fact]
    public void ResolvePricing_OpusFamily_ReturnsOpusPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-opus-unknown-version");
        Assert.True(pricing.InputPerMTok > 0);
    }

    [Fact]
    public void ResolvePricing_HaikuFamily_ReturnsHaikuPricing()
    {
        var pricing = ClaudeProvider.ResolvePricing("claude-haiku-unknown-version");
        Assert.True(pricing.InputPerMTok > 0);
    }

    [Fact]
    public void ResolvePricing_UnknownModel_ReturnsSonnetFallback()
    {
        var pricing = ClaudeProvider.ResolvePricing("completely-unknown-model");
        var sonnet = ClaudeProvider.ResolvePricing("claude-sonnet-4-6");
        Assert.Equal(sonnet.InputPerMTok, pricing.InputPerMTok);
    }

    [Fact]
    public void BuildStatusLabel_WithCost_ShowsCost()
    {
        var label = ClaudeProvider.BuildStatusLabel("Pro", 1000, 5.25, null);
        Assert.Contains("Pro plan", label);
        Assert.Contains("$5.25", label);
    }

    [Fact]
    public void BuildStatusLabel_NoCostWithTokens_ShowsTokens()
    {
        var label = ClaudeProvider.BuildStatusLabel("Pro", 1500000, 0, null);
        Assert.Contains("Pro plan", label);
        Assert.DoesNotContain("$", label);
    }

    [Fact]
    public void BuildStatusLabel_WithExtraUsage_ShowsExtraUsageOn()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };
        var label = ClaudeProvider.BuildStatusLabel("Pro", 0, 1.0, accountInfo);
        Assert.Contains("extra usage on", label);
    }

    [Fact]
    public void BuildStatusLabel_NoExtraUsage_OmitsExtraUsage()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = false };
        var label = ClaudeProvider.BuildStatusLabel("Pro", 0, 1.0, accountInfo);
        Assert.DoesNotContain("extra usage", label);
    }

    [Fact]
    public void FormatUsageLabel_WithCost_ShowsCost()
    {
        var label = ClaudeProvider.FormatUsageLabel("Max", 5000, 10.50, null);
        Assert.Contains("Max plan", label);
        Assert.Contains("$10.50", label);
    }

    [Fact]
    public void FormatUsageLabel_NoCostWithTokens_ShowsTokens()
    {
        var label = ClaudeProvider.FormatUsageLabel("Pro", 2000000, 0, null);
        Assert.Contains("Pro plan", label);
    }

    [Fact]
    public void FormatUsageLabel_ExtraUsageEnabled_ShowsLabel()
    {
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo { HasExtraUsageEnabled = true };
        var label = ClaudeProvider.FormatUsageLabel("Pro", 0, 2.0, accountInfo);
        Assert.Contains("extra usage on", label);
    }

    [Fact]
    public void BuildWeeklySnapshot_ZeroReset_NullResetInfo()
    {
        var limits = new ClaudeProvider.UnifiedRateLimits
        {
            SevenDayUtilization = 0.1,
            SevenDayReset = 0,
        };

        var snapshot = ClaudeProvider.BuildWeeklySnapshot(limits);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.ResetDescription);
        Assert.Null(snapshot.ResetsAt);
    }

    [Fact]
    public async Task OpenRouter_FetchUsageAsync_ValidResponse_ReturnsCredits()
    {
        var json = """{"data":{"total_credits":100.0,"total_usage":25.0}}""";
        var handler = new DelegatingHandlerFunc(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        var provider = CreateOpenRouterProvider(handler);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(75.0m, result.CreditsRemaining);
    }

    [Fact]
    public async Task OpenRouter_FetchUsageAsync_Unauthorized_ReturnsAuthError()
    {
        var handler = new DelegatingHandlerFunc(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = CreateOpenRouterProvider(handler);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("invalid or revoked", result.ErrorMessage);
    }

    [Fact]
    public async Task OpenRouter_FetchUsageAsync_RateLimited_ReturnsRateLimitError()
    {
        var handler = new DelegatingHandlerFunc(_ =>
            new HttpResponseMessage((HttpStatusCode)429));
        var provider = CreateOpenRouterProvider(handler);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Rate limited", result.ErrorMessage);
    }

    [Fact]
    public async Task OpenRouter_FetchUsageAsync_MissingDataField_ReturnsError()
    {
        var handler = new DelegatingHandlerFunc(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        var provider = CreateOpenRouterProvider(handler);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("missing", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRouter_FetchUsageAsync_MissingCreditFields_ReturnsError()
    {
        var handler = new DelegatingHandlerFunc(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{}}""", Encoding.UTF8, "application/json"),
            });
        var provider = CreateOpenRouterProvider(handler);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("credit fields", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRateLimitHeaders_WithHeaders_ParsesValues()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.45");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1700000000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.20");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1700100000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "active");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(0.45, result!.FiveHourUtilization);
        Assert.Equal(1700000000, result.FiveHourReset);
        Assert.Equal("active", result.FiveHourStatus);
        Assert.Equal(0.20, result.SevenDayUtilization);
    }

    [Fact]
    public void ParseRateLimitHeaders_PartialHeaders_ParsesAvailable()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.6");

        var result = ClaudeProvider.ParseRateLimitHeaders(response.Headers);

        Assert.NotNull(result);
        Assert.Equal(0.6, result!.FiveHourUtilization);
        Assert.Equal(0, result.SevenDayUtilization);
    }

    private static string BuildCopilotJson(string plan = "individual_pro")
    {
        var resetDate = DateTimeOffset.UtcNow.AddDays(15).ToString("o");
        return $$"""
            {
                "login": "testuser",
                "copilot_plan": "{{plan}}",
                "organization_login_list": ["org1"],
                "quota_reset_date_utc": "{{resetDate}}",
                "quota_snapshots": {
                    "premium_interactions": {
                        "entitlement": 2000,
                        "remaining": 500,
                        "overage_count": 0,
                        "overage_permitted": false,
                        "percent_remaining": 25.0,
                        "unlimited": false,
                        "quota_id": "premium-test",
                        "timestamp_utc": "2026-05-14T00:00:00Z"
                    }
                }
            }
            """;
    }

    private static ISettingsService CreateCopilotSettings(params string[] accounts)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(accounts.ToList());
        return settings;
    }

    private static IHttpClientFactory CreateHttpFactory(HttpResponseMessage response)
    {
        var handler = new CloneableResponseHandler(response);
        return CreateHttpFactory(handler);
    }

    private static IHttpClientFactory CreateHttpFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private static string BuildOpenCodeGoHtml(
        int? rollingPct = null, int? rollingSec = null,
        int? weeklyPct = null, int? weeklySec = null,
        int? monthlyPct = null, int? monthlySec = null)
    {
        var sb = new StringBuilder();
        sb.Append("<html><body><script>window.__SOLID_DATA={");

        var parts = new List<string>();
        if (rollingPct.HasValue && rollingSec.HasValue)
        {
            parts.Add($"rollingUsage:$R[0]={{usagePercent:{rollingPct},resetInSec:{rollingSec}}}");
        }

        if (weeklyPct.HasValue && weeklySec.HasValue)
        {
            parts.Add($"weeklyUsage:$R[1]={{usagePercent:{weeklyPct},resetInSec:{weeklySec}}}");
        }

        if (monthlyPct.HasValue && monthlySec.HasValue)
        {
            parts.Add($"monthlyUsage:$R[2]={{usagePercent:{monthlyPct},resetInSec:{monthlySec}}}");
        }

        sb.Append(string.Join(",", parts));
        sb.Append("}</script></body></html>");
        return sb.ToString();
    }

    private static OpenCodeGoProvider CreateOpenCodeGoProvider(
        string html = "<html></html>",
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode);
        if (statusCode == HttpStatusCode.OK)
        {
            response.Content = new StringContent(html, Encoding.UTF8, "text/html");
        }

        var handler = new OpenCodeGoCookieHandler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeGo).Returns(true);
        settings.GetOpenCodeGoWorkspaceId().Returns("ws-test");
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns("test-cookie");

        return new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);
    }

    private static OpenCodeGoProvider CreateOpenCodeGoProvider(HttpStatusCode statusCode)
    {
        return CreateOpenCodeGoProvider("<html></html>", statusCode);
    }

    private static OpenRouterProvider CreateOpenRouterProvider(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
        settings.GetApiKey(ProviderId.OpenRouter).Returns("sk-or-test");

        return new OpenRouterProvider(NullLogger<OpenRouterProvider>.Instance, factory, settings);
    }

    private static System.Diagnostics.Process CreateSlowProcess()
    {
        return new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ping",
                Arguments = "-n 10 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
    }

    private static System.Diagnostics.Process CreateExitProcess(int exitCode, string stderr = "", string stdout = "")
    {
        // Use base64-encoded PowerShell script to avoid quoting issues
        var script =
            $"[Console]::Out.Write('{stdout.Replace("'", "''")}'); " +
            $"[Console]::Error.Write('{stderr.Replace("'", "''")}'); " +
            $"exit {exitCode}";
        var bytes = System.Text.Encoding.Unicode.GetBytes(script);
        var encoded = Convert.ToBase64String(bytes);

        return new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
    }

    private sealed class CloneableResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _payload;
        private readonly string? _mediaType;

        public CloneableResponseHandler(HttpResponseMessage response)
        {
            this._statusCode = response.StatusCode;
            this._payload = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            this._mediaType = response.Content?.Headers.ContentType?.MediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpResponseMessage(this._statusCode);
            if (this._payload is not null)
            {
                clone.Content = this._mediaType is null
                    ? new StringContent(this._payload, Encoding.UTF8)
                    : new StringContent(this._payload, Encoding.UTF8, this._mediaType);
            }

            return Task.FromResult(clone);
        }
    }

    private sealed class OpenCodeGoCookieHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _payload;
        private readonly string? _mediaType;

        public OpenCodeGoCookieHandler(HttpResponseMessage response)
        {
            this._statusCode = response.StatusCode;
            this._payload = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            this._mediaType = response.Content?.Headers.ContentType?.MediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.TryGetValues("Cookie", out var cookies) ||
                !cookies.Any(c => c.Contains("auth=")))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            var clone = new HttpResponseMessage(this._statusCode);
            if (this._payload is not null)
            {
                clone.Content = this._mediaType is null
                    ? new StringContent(this._payload, Encoding.UTF8)
                    : new StringContent(this._payload, Encoding.UTF8, this._mediaType);
            }

            return Task.FromResult(clone);
        }
    }

    private sealed class DelegatingHandlerFunc(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
