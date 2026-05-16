// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Coverage tests for CopilotProvider targeting uncovered branches:
/// OperationCanceledException rethrow in FetchAccountQuotaAsync,
/// and token caching behavior across multiple fetches.
/// </summary>
public class CopilotProviderCoverageTests
{
    private const string FakeToken = "gho_coverage_test";

    [Fact]
    public async Task FetchUsageAsync_Cancelled_ThrowsOperationCanceled()
    {
        // This test exercises the catch (OperationCanceledException) { throw; } path
        // in FetchAccountQuotaAsync (lines 403-405).
        var settings = CreateSettings("testuser");
        var handler = new CancellingHandler();
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchUsageAsync(cts.Token));
    }

    [Fact]
    public async Task FetchUsageAsync_TokenCached_SecondCallReusesToken()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings("alice");
        var factory = CreateFactory(new CloneableHandler(response));

        int tokenCallCount = 0;
        var provider = CreateProvider(settings, factory, (_, _) =>
        {
            Interlocked.Increment(ref tokenCallCount);
            return Task.FromResult<string?>(FakeToken);
        });

        var result1 = await provider.FetchUsageAsync();
        var result2 = await provider.FetchUsageAsync();

        Assert.True(result1.Success);
        Assert.True(result2.Success);

        // Token resolver should only be called once; second call uses cache
        Assert.Equal(1, tokenCallCount);
    }

    [Fact]
    public async Task FetchUsageAsync_MultipleAccounts_AllAccountsFetched()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings("alice", "bob", "charlie");
        var factory = CreateFactory(new CloneableHandler(response));
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(3, result.Items!.Count);
    }

    [Fact]
    public async Task FetchUsageAsync_DuplicateAccounts_Deduplicated()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings("alice", "Alice", "ALICE");
        var factory = CreateFactory(new CloneableHandler(response));
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Single(result.Items!); // Deduplicated case-insensitively
    }

    [Fact]
    public async Task FetchUsageAsync_WhitespaceAccount_Filtered()
    {
        var json = BuildCopilotUserJson();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings("alice", string.Empty, "  ");
        var factory = CreateFactory(new CloneableHandler(response));
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Single(result.Items!); // Only "alice" survives filtering
    }

    [Fact]
    public async Task FetchUsageAsync_AllAccountsFail_ReturnsAggregateError()
    {
        var settings = CreateSettings("alice", "bob");
        var factory = CreateFactory(new CloneableHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("alice", result.ErrorMessage);
        Assert.Contains("bob", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_NullDeserializedResponse_ReturnsEmptyApiResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };
        var settings = CreateSettings("testuser");
        var factory = CreateFactory(new CloneableHandler(response));
        var provider = CreateProvider(settings, factory, (_, _) => Task.FromResult<string?>(FakeToken));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success); // Null response → account failure → overall failure
        Assert.Contains(result.Items!, item => item.ErrorMessage == "Empty API response");
    }

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

    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private static string BuildCopilotUserJson(string plan = "individual_pro")
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

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CloneableHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _payload;
        private readonly string? _mediaType;

        public CloneableHandler(HttpResponseMessage response)
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
}
