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
/// Tests for CopilotProvider HTTP paths: FetchAccountQuotaAsync, token resolution,
/// and FetchUsageAsync with multiple accounts. Uses the internal
/// <see cref="CopilotProvider.TokenResolverOverride"/> seam to bypass gh CLI.
/// </summary>
public class CopilotProviderFetchTests
{
    private const string FakeToken = "gho_test_token_12345";

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
        var handler = new DelegatingHandlerStub(response);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static IHttpClientFactory CreateFactory(Exception exception)
    {
        var handler = new DelegatingHandlerStub(exception);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static string BuildCopilotUserJson(
        string login = "testuser",
        string plan = "individual_pro",
        int entitlement = 2000,
        int remaining = 500,
        int overageCount = 0,
        bool overagePermitted = false,
        bool unlimited = false,
        string? resetDate = null)
    {
        resetDate ??= DateTimeOffset.UtcNow.AddDays(15).ToString("o");
        return $$"""
        {
            "login": "{{login}}",
            "copilot_plan": "{{plan}}",
            "organization_login_list": ["org1", "org2"],
            "quota_reset_date_utc": "{{resetDate}}",
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

    // --- FetchUsageAsync: success path ---
    [Fact]
    public async Task FetchUsageAsync_SingleAccount_ValidResponse_ReturnsSuccess()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.Copilot, result.Provider);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Contains("testuser", result.Items[0].DisplayName);
        Assert.True(result.Items[0].Success);
    }

    [Fact]
    public async Task FetchUsageAsync_SingleAccount_HasPrimaryAndSecondaryUsage()
    {
        var json = BuildCopilotUserJson(entitlement: 2000, remaining: 500);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items![0];
        Assert.NotNull(item.PrimaryUsage);
        Assert.NotNull(item.SecondaryUsage);
        Assert.Equal(0.75, item.PrimaryUsage!.UsedPercent, 2);
    }

    [Fact]
    public async Task FetchUsageAsync_SingleAccount_OveragePermitted_IncludesOverageCost()
    {
        var json = BuildCopilotUserJson(
            entitlement: 2000,
            remaining: -100,
            overageCount: 100,
            overagePermitted: true);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items![0];
        Assert.NotNull(item.OverageCost);
        Assert.Equal(4.00m, item.OverageCost!.Value); // 100 * $0.04
    }

    [Fact]
    public async Task FetchUsageAsync_SingleAccount_OverageNotPermitted_NoOverageCost()
    {
        var json = BuildCopilotUserJson(
            entitlement: 2000,
            remaining: 500,
            overageCount: 0,
            overagePermitted: false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Null(result.Items![0].OverageCost);
    }

    [Fact]
    public async Task FetchUsageAsync_SingleAccount_SetsSessionUsageFromPremium()
    {
        var json = BuildCopilotUserJson(entitlement: 2000, remaining: 500);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.SessionUsage);
        Assert.Equal(0.75, result.SessionUsage!.UsedPercent, 2);
    }

    // --- FetchUsageAsync: error paths ---
    [Fact]
    public async Task FetchUsageAsync_TokenNull_ReturnsFailureItem()
    {
        var settings = CreateSettings("testuser");
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(null));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.False(result.Items[0].Success);
        Assert.Contains("No token", result.Items[0].ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_Unauthorized_InvalidatesTokenAndReturnsError()
    {
        var exception = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        var settings = CreateSettings("testuser");
        var factory = CreateFactory(exception);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.False(result.Items[0].Success);
        Assert.Contains("expired", result.Items[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_EmptyApiResponse_ReturnsEmptyApiError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result.Items);
        var item = result.Items[0];
        Assert.False(item.Success);
        Assert.Contains("Empty", item.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_GenericException_ReturnsFailureWithMessage()
    {
        var exception = new InvalidOperationException("Network failure");
        var settings = CreateSettings("testuser");
        var factory = CreateFactory(exception);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result.Items);
        Assert.False(result.Items[0].Success);
        Assert.Contains("Network failure", result.Items[0].ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_HttpFailureStatus_ThrowsAndReturnsFailure()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server error"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result.Items);
        Assert.False(result.Items[0].Success);
    }

    // --- FetchUsageAsync: multi-account ---
    [Fact]
    public async Task FetchUsageAsync_MultipleAccounts_ReturnsItemPerAccount()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("alice", "bob");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task FetchUsageAsync_WhitespaceAccounts_AreFilteredOut()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("alice", string.Empty, "  ", "bob");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task FetchUsageAsync_DuplicateAccounts_AreDeduped()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("alice", "ALICE", "alice");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task FetchUsageAsync_AllAccountsFail_ReturnsOverallFailure()
    {
        var settings = CreateSettings("baduser");
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(null));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // --- FetchUsageAsync: response with no premium interactions ---
    [Fact]
    public async Task FetchUsageAsync_NoPremiumInteractions_NoSessionUsage()
    {
        var json = """
        {
            "login": "testuser",
            "copilot_plan": "enterprise",
            "quota_snapshots": {}
        }
        """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Null(result.SessionUsage);
    }

    // --- FetchUsageAsync: unlimited quota ---
    [Fact]
    public async Task FetchUsageAsync_UnlimitedQuota_MarksAsUnlimited()
    {
        var json = BuildCopilotUserJson(entitlement: 0, remaining: 0, unlimited: true);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var settings = CreateSettings("testuser");
        var factory = CreateFactory(response);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items![0];
        Assert.NotNull(item.PrimaryUsage);
        Assert.True(item.PrimaryUsage!.IsUnlimited);
    }

    // --- IsAvailableAsync ---
    [Fact]
    public async Task IsAvailableAsync_Enabled_ReturnsTrue()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(false);
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.False(await provider.IsAvailableAsync());
    }

    // --- ParseReset edge cases ---
    [Fact]
    public void ParseReset_OverdueDate_ReturnsOverdue()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-2).ToString("o");
        var (_, description) = CopilotProvider.ParseReset(past);
        Assert.Equal("Reset overdue", description);
    }

    [Fact]
    public void ParseReset_SameDay_ReturnsHoursMinutes()
    {
        var soon = DateTimeOffset.UtcNow.AddHours(5).AddMinutes(30).ToString("o");
        var (_, description) = CopilotProvider.ParseReset(soon);
        Assert.Contains("5h", description!);
    }

    // --- Delegating handler for tests ---
    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;

        public DelegatingHandlerStub(HttpResponseMessage response) => this._response = response;

        public DelegatingHandlerStub(Exception exception) => this._exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (this._exception is not null)
            {
                throw this._exception;
            }

            return Task.FromResult(this._response!);
        }
    }
}
