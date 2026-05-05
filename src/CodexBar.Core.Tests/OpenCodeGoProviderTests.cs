// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeGo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class OpenCodeGoProviderTests
{
    private static OpenCodeGoProvider CreateProviderWithHandler(
        out MockHttpMessageHandler handler,
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        settings ??= CreateSettingsService(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie-value");
        handler = new MockHttpMessageHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK));
        var httpClientFactory = new MockHttpClientFactory(handler);

        return new OpenCodeGoProvider(
            NullLogger<OpenCodeGoProvider>.Instance,
            httpClientFactory,
            settings);
    }

    private static OpenCodeGoProvider CreateProvider(
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        return CreateProviderWithHandler(out _, settings, response);
    }

    private static ISettingsService CreateSettingsService(
        bool enabled = true,
        string? workspaceId = null,
        string? apiKey = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeGo).Returns(enabled);
        settings.GetOpenCodeGoWorkspaceId().Returns(workspaceId);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns(apiKey);
        return settings;
    }

    [Fact]
    public void Metadata_IsCorrect()
    {
        var provider = CreateProvider();
        Assert.Equal(ProviderId.OpenCodeGo, provider.Metadata.Id);
        Assert.Equal("OpenCode Go", provider.Metadata.DisplayName);
        Assert.False(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
        Assert.False(provider.Metadata.SupportsWeeklyUsage);
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = CreateSettingsService(enabled: false);
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_Enabled_ReturnsTrue()
    {
        var provider = CreateProvider();
        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task FetchUsageAsync_NoWorkspaceId_ReturnsFailure()
    {
        var settings = CreateSettingsService(enabled: true, workspaceId: null, apiKey: "cookie");
        var provider = CreateProvider(settings: settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("OPENCODE_GO_WORKSPACE_ID", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_NoAuthCookie_ReturnsFailure()
    {
        var settings = CreateSettingsService(enabled: true, workspaceId: "ws-123", apiKey: null);
        var provider = CreateProvider(settings: settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("AUTH_COOKIE", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_401_ReturnsAuthFailure()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Auth cookie", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_403_ReturnsAuthFailure()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Auth cookie", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_500_ReturnsHttpError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("HTTP 500", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ValidHtml_ReturnsUsage()
    {
        var html = CreateDashboardHtml(rollingPct: 45, rollingSec: 120, weeklyPct: 60, weeklySec: 3600, monthlyPct: 75, monthlySec: 86400);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Equal("OpenCode Go", result.Items![0].DisplayName);
        Assert.Equal(0.45, result.Items[0].PrimaryUsage!.UsedPercent);
    }

    [Fact]
    public async Task FetchUsageAsync_RollingOnly_ReturnsUsage()
    {
        var html = CreateDashboardHtml(rollingPct: 30, rollingSec: 60);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task FetchUsageAsync_UnparseableHtml_ReturnsParseFailure()
    {
        var html = "<html><body>No usage data here</body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("parse", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_CachesResult()
    {
        var html = CreateDashboardHtml(rollingPct: 50, rollingSec: 300);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProviderWithHandler(out var handler, response: response);

        // First call should hit HTTP
        var result1 = await provider.FetchUsageAsync();
        Assert.True(result1.Success);

        // Second call should use cache (same result)
        var result2 = await provider.FetchUsageAsync();
        Assert.True(result2.Success);

        // Verify only 1 HTTP request was made (second call used cache)
        Assert.Equal(1, handler!.SendCount);
    }

    private static string CreateDashboardHtml(
        int? rollingPct = null, int? rollingSec = null,
        int? weeklyPct = null, int? weeklySec = null,
        int? monthlyPct = null, int? monthlySec = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<html><body>");
        sb.Append("<script>window.__SOLID_DATA={");

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

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;
        private int sendCount;

        public MockHttpMessageHandler(HttpResponseMessage response) => this.response = response;

        public int SendCount => this.sendCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Fail-closed: deny requests with missing or invalid auth cookie
            if (!request.Headers.TryGetValues("Cookie", out var cookies) ||
                !cookies.Any(c => c.Contains("auth=")))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            Interlocked.Increment(ref this.sendCount);

            return Task.FromResult(this.response);
        }
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly MockHttpMessageHandler handler;

        public MockHttpClientFactory(MockHttpMessageHandler handler) => this.handler = handler;

        public HttpClient CreateClient(string name) => new(this.handler);
    }
}
