// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Reflection;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

[Collection("ClaudeProviderFileIo")]
public sealed class ClaudeProviderReadOnlyRefreshTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-read-only-{Guid.NewGuid():N}");

    public ClaudeProviderReadOnlyRefreshTests()
    {
        Directory.CreateDirectory(this._tempDir);
        ClaudeProvider.CredentialsPathOverride = Path.Combine(this._tempDir, "credentials.json");
        ClaudeProvider.StatsCachePathOverride = Path.Combine(this._tempDir, "stats.json");
        ClaudeProvider.ClaudeJsonPathOverride = Path.Combine(this._tempDir, "account.json");
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = string.Empty;
        ClaudeProvider.WebSessionCachePathOverride = Path.Combine(this._tempDir, "session.bin");
        ClaudeProvider.EnvironmentVariableProvider = _ => null;
        ClaudeProvider.TargetEnvironmentVariableProvider = (_, _) => null;
        File.WriteAllText(ClaudeProvider.CredentialsPathOverride, """
            {"claudeAiOauth":{"accessToken":"synthetic-token","subscriptionType":"pro"}}
            """);
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;
        ClaudeProvider.WebSessionCachePathOverride = null;
        ClaudeProvider.ResetEnvironmentProvidersForTests();
        Directory.Delete(this._tempDir, recursive: true);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.TooManyRequests, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "{")]
    public async Task FetchUsageAsync_UsageUnavailable_NeverSendsInferenceRequest(HttpStatusCode status, string payload)
    {
        var requests = new List<(HttpMethod Method, string Url)>();
        var provider = CreateProvider(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Response(status, payload);
        });

        var result = await provider.FetchUsageAsync();

        Assert.Equal([(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage")], requests);
        Assert.True(result.Success);
        Assert.Contains("Rate limits unavailable", result.SessionUsage!.UsageLabel);
        Assert.Null(result.WeeklyUsage);
        Assert.Empty(result.Items![0].Bars!);
    }

    [Fact]
    public async Task FetchUsageAsync_WebAndOAuthUnavailable_NeverSendsInferenceRequest()
    {
        this.ConfigureWebAccount();
        var requests = new List<(HttpMethod Method, string Url)>();
        var provider = CreateProvider(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Response(HttpStatusCode.Forbidden, "{}");
        });

        var result = await provider.FetchUsageAsync();

        Assert.Equal(
            [
                (HttpMethod.Get, "https://claude.ai/api/organizations/synthetic-org/usage"),
                (HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage"),
            ],
            requests);
        Assert.Contains("Rate limits unavailable", result.SessionUsage!.UsageLabel);
    }

    [Fact]
    public async Task FetchUsageAsync_WebUsageAvailable_PreservesNonBillableSource()
    {
        this.ConfigureWebAccount();
        var requests = new List<(HttpMethod Method, string Url)>();
        var provider = CreateProvider(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Response(HttpStatusCode.OK, UsagePayload);
        });

        var result = await provider.FetchUsageAsync();

        Assert.Equal([(HttpMethod.Get, "https://claude.ai/api/organizations/synthetic-org/usage")], requests);
        Assert.Equal(0.35, result.SessionUsage!.UsedPercent);
        Assert.Equal(0.6, result.WeeklyUsage!.UsedPercent);
    }

    [Theory]
    [InlineData(35, 60)]
    [InlineData(0, 0)]
    public async Task FetchUsageAsync_ExpiredCacheAndReadOnlyFailure_PreservesLastGoodLimits(double fiveHour, double sevenDay)
    {
        var requests = new List<(HttpMethod Method, string Url)>();
        var provider = CreateProvider(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return requests.Count == 1
                ? Response(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
                {
                    five_hour = new { utilization = fiveHour },
                    seven_day = new { utilization = sevenDay },
                }))
                : Response(HttpStatusCode.Forbidden, "{}");
        });
        var first = await provider.FetchUsageAsync();
        typeof(ClaudeProvider).GetField("limitsCachedAtTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(provider, DateTimeOffset.UtcNow.AddHours(-1).UtcTicks);

        var second = await provider.FetchUsageAsync();
        var third = await provider.FetchUsageAsync();

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.Url);
        });
        Assert.Equal(first.SessionUsage!.UsedPercent, second.SessionUsage!.UsedPercent);
        Assert.Equal(first.WeeklyUsage!.UsedPercent, second.WeeklyUsage!.UsedPercent);
        Assert.Equal(first.SessionUsage.UsedPercent, third.SessionUsage!.UsedPercent);
        Assert.Equal(first.Items![0].Bars, second.Items![0].Bars);
        Assert.Contains("Cached usage; rate limits unavailable", second.SessionUsage.UsageLabel);
        Assert.Contains("Cached usage; rate limits unavailable", third.SessionUsage.UsageLabel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchUsageAsync_ReadOnlyNetworkFailure_NeverSendsInferenceRequest(bool timeout)
    {
        var requests = new List<HttpMethod>();
        var provider = CreateProvider(request =>
        {
            requests.Add(request.Method);
            if (timeout)
            {
                throw new TaskCanceledException("Synthetic timeout");
            }

            throw new HttpRequestException("Synthetic network failure");
        });

        var result = await provider.FetchUsageAsync();

        Assert.Equal([HttpMethod.Get], requests);
        Assert.Contains("Rate limits unavailable", result.SessionUsage!.UsageLabel);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    [InlineData(1800)]
    public async Task FetchUsageAsync_RateLimitedReadOnlySource_HonorsBackoffWithoutInference(int retryAfterSeconds)
    {
        var requests = new List<HttpMethod>();
        var provider = CreateProvider(request =>
        {
            requests.Add(request.Method);
            var response = Response(HttpStatusCode.TooManyRequests, new string('x', 600));
            if (retryAfterSeconds >= 0)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            }

            return response;
        });

        await provider.FetchUsageAsync();
        var result = await provider.FetchUsageAsync();

        Assert.Equal([HttpMethod.Get], requests);
        Assert.Contains("Rate limits unavailable", result.SessionUsage!.UsageLabel);
    }

    private const string UsagePayload = """
        {"five_hour":{"utilization":35,"resets_at":"2099-01-01T01:00:00Z"},"seven_day":{"utilization":60,"resets_at":"2099-01-07T01:00:00Z"}}
        """;

    private static HttpResponseMessage Response(HttpStatusCode status, string payload) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    private static ClaudeProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new RecordingHandler(respond)));
        return new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, factory, settings);
    }

    private void ConfigureWebAccount()
    {
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = "sessionKey=synthetic-session";
        File.WriteAllText(ClaudeProvider.ClaudeJsonPathOverride!, """
            {"oauthAccount":{"organizationUuid":"synthetic-org","displayName":"Synthetic account"}}
            """);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
