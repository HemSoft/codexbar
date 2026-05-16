// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Edge-case and branch-coverage tests for CopilotProvider that complement
/// the main CopilotProviderFetchTests and CopilotProviderTests suites.
/// </summary>
public class CopilotProviderEdgeTests
{
    private const string FakeToken = "gho_test_token_edge";

    private static CopilotProvider CreateProvider(
        ISettingsService settings,
        IHttpClientFactory httpClientFactory,
        Func<string, CancellationToken, Task<string?>>? tokenResolver = null)
    {
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            httpClientFactory,
            settings);
        provider.TokenResolverOverride = tokenResolver;
        return provider;
    }

    private static ISettingsService CreateSettings(params string[] accounts)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(accounts.ToList());
        return settings;
    }

    private static IHttpClientFactory CreateFactory(HttpResponseMessage response)
    {
        var handler = new CloneableResponseHandler(response);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static IHttpClientFactory CreateFactory(Exception exception)
    {
        var handler = new ExceptionHandler(exception);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static string BuildCopilotJson(
        string plan = "individual_pro",
        int entitlement = 2000,
        int remaining = 500,
        int overageCount = 0,
        bool overagePermitted = false,
        bool unlimited = false,
        bool includeChat = true,
        string? resetDate = null)
    {
        resetDate ??= DateTimeOffset.UtcNow.AddDays(15).ToString("o");
        var chatBlock = includeChat
            ? """
              ,"chat": {
                  "entitlement": 1000,
                  "remaining": 800,
                  "overage_count": 0,
                  "overage_permitted": false,
                  "percent_remaining": 80.0,
                  "unlimited": false,
                  "quota_id": "chat-test",
                  "timestamp_utc": "2026-05-14T00:00:00Z"
              }
              """
            : string.Empty;

        return $$"""
        {
            "login": "testuser",
            "copilot_plan": "{{plan}}",
            "organization_login_list": ["org1"],
            "quota_reset_date_utc": {{(resetDate is not null ? $"\"{resetDate}\"" : "null")}},
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": {{entitlement}},
                    "remaining": {{remaining}},
                    "overage_count": {{overageCount}},
                    "overage_permitted": {{overagePermitted.ToString().ToLowerInvariant()}},
                    "percent_remaining": {{(entitlement > 0 ? (double)remaining / entitlement * 100 : 0)}},
                    "unlimited": {{unlimited.ToString().ToLowerInvariant()}},
                    "quota_id": "premium-test",
                    "timestamp_utc": "2026-05-14T00:00:00Z"
                }
                {{chatBlock}}
            }
        }
        """;
    }

    // --- Display name formatting via FetchUsageAsync ---
    [Fact]
    public async Task FetchUsageAsync_EnterprisePlan_DisplayNameShowsEnt()
    {
        var json = BuildCopilotJson(plan: "enterprise");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var provider = CreateProvider(
            CreateSettings("alice"),
            CreateFactory(response),
            (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Contains("Ent", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_BusinessPlan_DisplayNameShowsBiz()
    {
        var json = BuildCopilotJson(plan: "business");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var provider = CreateProvider(
            CreateSettings("bob"),
            CreateFactory(response),
            (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Contains("Biz", result.Items![0].DisplayName);
    }

    // --- Chat quota secondary usage ---
    [Fact]
    public async Task FetchUsageAsync_WithoutChatQuota_NullSecondaryUsage()
    {
        var json = BuildCopilotJson(includeChat: false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var provider = CreateProvider(
            CreateSettings("testuser"),
            CreateFactory(response),
            (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Null(result.Items![0].SecondaryUsage);
    }

    // --- Partial multi-account failure ---
    [Fact]
    public async Task FetchUsageAsync_MixedResults_SomeSucceedSomeFail_OverallSuccess()
    {
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var factory = CreateFactory(response);

        // alice gets a token, bob does not
        var provider = CreateProvider(
            CreateSettings("alice", "bob"),
            factory,
            (user, _) => Task.FromResult<string?>(user == "alice" ? FakeToken : null));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success); // at least one succeeded
        Assert.Equal(2, result.Items!.Count);
        Assert.Contains(result.Items, i => i.Success);
        Assert.Contains(result.Items, i => !i.Success);
    }

    // --- No accounts configured ---
    [Fact]
    public async Task FetchUsageAsync_NoAccounts_ReturnsFailure()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string>());
        var factory = Substitute.For<IHttpClientFactory>();

        // TokenResolverOverride blocks gh discovery so we get zero accounts
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(null));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No Copilot accounts", result.ErrorMessage);
    }

    // --- Overage calculation edge cases ---
    [Fact]
    public void ComputeUsageMetrics_NegativeRemainingHigherThanOverageCount_UsesNegativeRemaining()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = -200,
            OverageCount = 50,
            OveragePermitted = true,
        };

        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.True(usedPercent >= 1.0); // over 100% usage
        Assert.Contains("$", label); // overage cost shown
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_OverageCountHigherThanNegativeRemaining_UsesOverageCount()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = -10,
            OverageCount = 300,
            OveragePermitted = true,
        };

        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Contains("$12.00", label); // 300 * $0.04
    }

    [Fact]
    public void ComputeUsageMetrics_ZeroOverage_NoOverageInLabel()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = 500,
            OverageCount = 0,
            OveragePermitted = true,
        };

        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.DoesNotContain("$", label);
        Assert.Contains("1,500 / 2,000", label);
    }

    [Fact]
    public void ComputeUsageMetrics_ChatLabel_ShowsChatType()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 1000,
            Remaining = 800,
            OverageCount = 0,
            OveragePermitted = false,
        };

        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "chat");

        Assert.Contains("Chat", label);
    }

    [Fact]
    public void ComputeUsageMetrics_OverLimitNotPermitted_ShowsOverLimitLabel()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -50,
            OverageCount = 50,
            OveragePermitted = false,
        };

        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Contains("over limit", label);
    }

    // --- ParseReset edge cases ---
    [Fact]
    public void ParseReset_InvalidDateString_ReturnsNulls()
    {
        var (resetsAt, description) = CopilotProvider.ParseReset("not-a-date");

        Assert.Null(resetsAt);
        Assert.Null(description);
    }

    [Fact]
    public void ParseReset_FarFuture_ReturnsDays()
    {
        var future = DateTimeOffset.UtcNow.AddDays(10).ToString("o");
        var (resetsAt, description) = CopilotProvider.ParseReset(future);

        Assert.NotNull(resetsAt);
        Assert.Matches(@"\dd", description!); // e.g. "9d" or "10d"
    }

    [Fact]
    public void ParseReset_ExactlyTomorrow_ReturnsTomorrowLabel()
    {
        var tomorrow = DateTimeOffset.UtcNow.AddHours(30).ToString("o");
        var (_, description) = CopilotProvider.ParseReset(tomorrow);

        Assert.Contains("tomorrow", description!, StringComparison.OrdinalIgnoreCase);
    }

    // --- FormatQuotaLabel edge case ---
    [Fact]
    public void FormatQuotaLabel_UnknownLabel_ReturnsAsIs()
    {
        var result = CopilotProvider.FormatQuotaLabel("completions");

        Assert.Equal("completions", result);
    }

    // --- ExtractUsername edge cases ---
    [Fact]
    public void ExtractUsername_AccountWithTrailingParenthesis_ExtractsName()
    {
        var result = CopilotProvider.ExtractUsername("  Logged in to github.com account bob (token)");

        Assert.Equal("bob", result);
    }

    [Fact]
    public void ExtractUsername_AsAtEndOfLine_ExtractsFullName()
    {
        var result = CopilotProvider.ExtractUsername("  Logged in to github.com as alice");

        Assert.Equal("alice", result);
    }

    // --- Delegating handlers ---
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

    private sealed class ExceptionHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ExceptionHandler(Exception exception) => this._exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw this._exception;
        }
    }
}
