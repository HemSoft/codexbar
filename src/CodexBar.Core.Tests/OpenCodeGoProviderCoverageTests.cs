// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeGo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Coverage tests targeting previously uncovered FormatReset branches in OpenCodeGoProvider:
/// "Resets now" (resetInSec=0) and "Resets Xd" (resetInSec &gt; 86400).
/// </summary>
public class OpenCodeGoProviderCoverageTests
{
    [Fact]
    public async Task FetchUsageAsync_ZeroResetSeconds_ResetDescriptionShowsNow()
    {
        var html = BuildDashboardHtml(rollingPct: 50, rollingSec: 0);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.NotEmpty(result.Items);
        Assert.Contains("Resets now", result.Items[0].Bars![0].ResetDescription);
    }

    [Fact]
    public async Task FetchUsageAsync_LargeResetSeconds_ResetDescriptionShowsDays()
    {
        var html = BuildDashboardHtml(rollingPct: 30, rollingSec: 172800); // 2 days
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.NotEmpty(result.Items);
        var resetDescription = result.Items[0].Bars![0].ResetDescription!;
        Assert.StartsWith("Resets", resetDescription);
        Assert.Contains("d", resetDescription);
    }

    private static OpenCodeGoProvider CreateProvider(
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        settings ??= CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie-value");
        var handler = new CookieAwareHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK));
        var httpClientFactory = new SimpleHttpClientFactory(handler);

        return new OpenCodeGoProvider(
            NullLogger<OpenCodeGoProvider>.Instance,
            httpClientFactory,
            settings);
    }

    private static ISettingsService CreateSettings(
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

    private static string BuildDashboardHtml(
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

    [Fact]
    public async Task FetchUsageAsync_OverflowUsagePercent_ReturnsFailure()
    {
        // usagePercent value exceeds int.MaxValue so TryParse fails
        var html = "<html><body><script>window.__SOLID_DATA={rollingUsage:$R[0]={usagePercent:99999999999,resetInSec:300}}</script></body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var handler = new CookieAwareHandler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie-value");
        var provider = new OpenCodeGoProvider(
            NullLogger<OpenCodeGoProvider>.Instance,
            factory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_OverflowResetInSec_ReturnsFailure()
    {
        // resetInSec value exceeds int.MaxValue so TryParse fails
        var html = "<html><body><script>window.__SOLID_DATA={rollingUsage:$R[0]={usagePercent:50,resetInSec:99999999999}}</script></body></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var handler = new CookieAwareHandler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie-value");
        var provider = new OpenCodeGoProvider(
            NullLogger<OpenCodeGoProvider>.Instance,
            factory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    private sealed class CookieAwareHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode = response.StatusCode;
        private readonly string? _payload = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        private readonly string? _mediaType = response.Content?.Headers.ContentType?.MediaType;

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

    private sealed class SimpleHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
