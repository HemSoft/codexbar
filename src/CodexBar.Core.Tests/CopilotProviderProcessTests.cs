// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests for CopilotProvider process-based methods: ResolveTokenForUserAsync,
/// DiscoverGhAccountsAsync, WaitForGhProcessAsync, and related coverage gaps.
/// Uses the TokenResolverOverride and AccountDiscoveryOverride for most paths,
/// plus reflection to test internal process handling.
/// </summary>
public class CopilotProviderProcessTests
{
    private static CopilotProvider CreateProvider(
        ISettingsService? settings = null,
        IHttpClientFactory? httpFactory = null)
    {
        settings ??= CreateSettings();
        httpFactory ??= Substitute.For<IHttpClientFactory>();
        return new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            httpFactory,
            settings);
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

    // --- ResolveTokenForUserAsync via TokenResolverOverride ---
    [Fact]
    public async Task FetchUsageAsync_TokenResolverReturnsNull_ReturnsTokenMissing()
    {
        var settings = CreateSettings("user1");
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(null);

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result);
        Assert.False(result.Items[0].Success);
    }

    [Fact]
    public async Task FetchUsageAsync_TokenResolverReturnsWhitespace_ReturnsTokenMissing()
    {
        var settings = CreateSettings("user1");
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("  ");

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result);

        // Whitespace token should still be treated as missing - tests token caching behavior
        Assert.False(result.Items[0].Success);
    }

    [Fact]
    public async Task FetchUsageAsync_TokenCached_SecondCallUsesCache()
    {
        var callCount = 0;
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings("user1");
        var httpFactory = CreateFactory(response);
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) =>
        {
            callCount++;
            return Task.FromResult<string?>("gho_test_token");
        };

        await provider.FetchUsageAsync();
        await provider.FetchUsageAsync();

        // Token should be cached after first call
        Assert.Equal(1, callCount);
    }

    // --- AccountDiscoveryOverride ---
    [Fact]
    public async Task FetchUsageAsync_AccountDiscoveryReturnsEmpty_ReturnsError()
    {
        var settings = CreateSettings(); // No configured accounts
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, httpFactory);
        provider.AccountDiscoveryOverride = _ => Task.FromResult(new List<string>());

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No Copilot accounts found", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_AccountDiscoveryOverride_ReturnsAccounts()
    {
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings(); // No configured accounts
        var httpFactory = CreateFactory(response);
        var provider = CreateProvider(settings, httpFactory);
        provider.AccountDiscoveryOverride = _ => Task.FromResult(new List<string> { "discovered-user" });
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("gho_token");

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Contains("discovered-user", result.Items[0].DisplayName);
    }

    // --- GetAccountsToFetchAsync empty discovery caching ---
    [Fact]
    public async Task FetchUsageAsync_EmptyDiscovery_CachedFor5Minutes()
    {
        var callCount = 0;
        var settings = CreateSettings(); // No configured accounts
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, httpFactory);
        provider.AccountDiscoveryOverride = _ =>
        {
            callCount++;
            return Task.FromResult(new List<string>());
        };

        await provider.FetchUsageAsync();
        await provider.FetchUsageAsync();

        // Second call should hit the empty cache, not invoke discovery again
        Assert.Equal(1, callCount);
    }

    // --- InvalidateTokenForUserAsync (via 401 response) ---
    [Fact]
    public async Task FetchUsageAsync_401Response_InvalidatesTokenAndReturnsUnauthorized()
    {
        var settings = CreateSettings("user1");
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var handler = new ThrowOnUnauthorizedHandler();
        var httpClient = new HttpClient(handler);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var tokenCallCount = 0;
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) =>
        {
            tokenCallCount++;
            return Task.FromResult<string?>("gho_token");
        };

        // First call gets 401, invalidates the cache
        var result = await provider.FetchUsageAsync();
        Assert.False(result.Items[0].Success);

        // Second call should resolve token again (cache was cleared)
        await provider.FetchUsageAsync();
        Assert.Equal(2, tokenCallCount);
    }

    // --- Multiple accounts with mixed success/failure ---
    [Fact]
    public async Task FetchUsageAsync_MultipleAccounts_MixedResults()
    {
        var json = BuildCopilotJson();
        var callIndex = 0;
        var settings = CreateSettings("user1", "user2");
        var handler = new ConditionalHandler(req =>
        {
            callIndex++;
            if (callIndex == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var httpClient = new HttpClient(handler);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("gho_token");

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success); // At least one account succeeded
        Assert.Equal(2, result.Items.Count);
    }

    // --- Duplicate/whitespace account filtering ---
    [Fact]
    public async Task FetchUsageAsync_DuplicateAccounts_DeduplicatedCaseInsensitive()
    {
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings("User1", "user1", " User1 ");
        var httpFactory = CreateFactory(response);
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("gho_token");

        var result = await provider.FetchUsageAsync();

        // Should be deduplicated to just 1 account
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task FetchUsageAsync_WhitespaceAccounts_Filtered()
    {
        var settings = CreateSettings(string.Empty, "  ", "user1");
        var json = BuildCopilotJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var httpFactory = CreateFactory(response);
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>("gho_token");

        var result = await provider.FetchUsageAsync();

        Assert.Single(result.Items);
    }

    // --- DiscoverGhAccountsAsync via real 'gh' if available ---
    [Fact]
    public async Task FetchUsageAsync_NoConfiguredAccounts_NoDiscoveryOverride_UsesRealGh()
    {
        // This tests the actual DiscoverGhAccountsAsync path.
        // On CI or systems without gh, it should still return a result (error or success).
        var settings = CreateSettings(); // No configured accounts
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, httpFactory);

        // No overrides set — will actually try to run 'gh'
        var result = await provider.FetchUsageAsync();

        // The result could be success (if gh is configured) or failure (if not)
        // but it should never throw
        Assert.NotNull(result);
        Assert.Equal(ProviderId.Copilot, result.Provider);
    }

    // --- ExtractUsernamesFromGhStatus ---
    [Fact]
    public void ExtractUsernamesFromGhStatus_NoLoggedInLine_ReturnsEmpty()
    {
        var result = CopilotProvider.ExtractUsernamesFromGhStatus("Some random output\nNo login info here");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_AccountFormat_ReturnsUsername()
    {
        var stderr = "✓ Logged in to github.com account testuser (keyring)\n";
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);
        Assert.Single(result);
        Assert.Equal("testuser", result[0]);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_AsFormat_ReturnsUsername()
    {
        var stderr = "✓ Logged in to github.com as myuser (token)\n";
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);
        Assert.Single(result);
        Assert.Equal("myuser", result[0]);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_MultipleAccounts_ReturnsAll()
    {
        var stderr = """
        ✓ Logged in to github.com account user1 (keyring)
        ✓ Logged in to github.com account user2 (token)
        """;
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);
        Assert.Equal(2, result.Count);
        Assert.Contains("user1", result);
        Assert.Contains("user2", result);
    }

    // --- ExtractUsername ---
    [Fact]
    public void ExtractUsername_NoMatch_ReturnsNull()
    {
        var result = CopilotProvider.ExtractUsername("random line without patterns");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractUsername_AccountAtEnd_ReturnsUsername()
    {
        var result = CopilotProvider.ExtractUsername("Logged in to github.com account myuser");
        Assert.Equal("myuser", result);
    }

    // --- ComputeUsageMetrics edge cases ---
    [Fact]
    public void ComputeUsageMetrics_ZeroEntitlement_Unlimited_ReturnsUnlimited()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 0,
            Remaining = 0,
            Unlimited = true,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, usedPercent);
        Assert.True(isUnlimited);
        Assert.Equal("Unlimited", label);
    }

    [Fact]
    public void ComputeUsageMetrics_ZeroEntitlement_NotUnlimited_ReturnsNoQuota()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 0,
            Remaining = 0,
            Unlimited = false,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, usedPercent);
        Assert.False(isUnlimited);
        Assert.Equal("No quota", label);
    }

    // --- FormatDisplayName ---
    [Theory]
    [InlineData("enterprise", "Copilot · alice (Ent)")]
    [InlineData("individual_pro", "Copilot · alice (Pro)")]
    [InlineData("business", "Copilot · alice (Biz)")]
    [InlineData("some_other", "Copilot · alice (some other)")]
    [InlineData(null, "Copilot · alice")]
    public void FormatDisplayName_VariousPlans_ReturnsExpected(string? plan, string expected)
    {
        var result = CopilotProvider.FormatDisplayName("alice", plan);
        Assert.Equal(expected, result);
    }

    // --- FormatQuotaLabel ---
    [Theory]
    [InlineData("premium", "Premium interactions")]
    [InlineData("chat", "Chat")]
    [InlineData("other", "other")]
    public void FormatQuotaLabel_VariousInputs_ReturnsExpected(string input, string expected)
    {
        var result = CopilotProvider.FormatQuotaLabel(input);
        Assert.Equal(expected, result);
    }

    // --- ParseReset ---
    [Fact]
    public void ParseReset_NullDate_ReturnsNulls()
    {
        var (resetsAt, desc) = CopilotProvider.ParseReset(null);
        Assert.Null(resetsAt);
        Assert.Null(desc);
    }

    [Fact]
    public void ParseReset_InvalidDate_ReturnsNulls()
    {
        var (resetsAt, desc) = CopilotProvider.ParseReset("not-a-date");
        Assert.Null(resetsAt);
        Assert.Null(desc);
    }

    [Fact]
    public void ParseReset_FutureDate_LessThanOneDay_ReturnsHoursMinutes()
    {
        var future = DateTimeOffset.UtcNow.AddHours(5).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(future);
        Assert.NotNull(resetsAt);
        Assert.Contains("Resets in", desc);
        Assert.Contains("h", desc);
    }

    [Fact]
    public void ParseReset_FutureDate_OneTwoDay_ReturnsTomorrow()
    {
        var future = DateTimeOffset.UtcNow.AddHours(30).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(future);
        Assert.NotNull(resetsAt);
        Assert.Equal("Resets tomorrow", desc);
    }

    [Fact]
    public void ParseReset_FutureDate_MultipleDays_ReturnsDays()
    {
        var future = DateTimeOffset.UtcNow.AddDays(5).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(future);
        Assert.NotNull(resetsAt);
        Assert.Contains("Resets in", desc);
        Assert.Contains("d", desc);
    }

    [Fact]
    public void ParseReset_PastDate_ReturnsOverdue()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-2).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(past);
        Assert.NotNull(resetsAt);
        Assert.Equal("Reset overdue", desc);
    }

    // --- Helpers ---
    private static string BuildCopilotJson(
        string plan = "individual_pro",
        int entitlement = 2000,
        int remaining = 500)
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

    private sealed class ThrowOnUnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        }
    }

    private sealed class ConditionalHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
