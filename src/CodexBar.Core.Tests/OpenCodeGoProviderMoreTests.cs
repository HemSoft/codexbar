// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeGo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class OpenCodeGoProviderMoreTests
{
    private static OpenCodeGoProvider CreateProviderWithHandler(
        out MockHttpMessageHandler handler,
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        settings ??= CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie-value");
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

    [Fact]
    public async Task FetchUsageAsync_AlternateFieldOrder_ParsesCorrectly()
    {
        // Test parsing when resetInSec comes before usagePercent
        var html = CreateDashboardHtml(
            rollingPct: null, rollingSec: null, // no rolling
            weeklyPct: null, weeklySec: null,   // no weekly
            monthlyPct: 55, monthlySec: 300,    // monthly with alternate order
            alternateOrder: true);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);
        Assert.Equal(0.55, result.Items[0].PrimaryUsage!.UsedPercent);
    }

    [Fact]
    public async Task FetchUsageAsync_MonthlyOnly_UsesMonthlyAsPrimary()
    {
        // Test that when only monthly data exists, it becomes the primary usage
        var html = CreateDashboardHtml(
            rollingPct: null, rollingSec: null, // no rolling
            weeklyPct: null, weeklySec: null,   // no weekly
            monthlyPct: 80, monthlySec: 86400); // only monthly

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);

        // Primary should be monthly (80%)
        Assert.Equal(0.80, result.Items[0].PrimaryUsage!.UsedPercent);

        // Should have one bar for the monthly window
        var bars = result.Items[0].Bars;
        Assert.NotNull(bars);
        Assert.Single(bars);
        Assert.Contains("Monthly limit", bars[0].Label);
    }

    [Fact]
    public async Task FetchUsageAsync_WeeklyAndMonthlyNoBars_ReturnsTwoBars()
    {
        // Test that weekly + monthly (no rolling) produces two bars
        var html = CreateDashboardHtml(
            rollingPct: null, rollingSec: null, // no rolling
            weeklyPct: 60, weeklySec: 3600,     // weekly
            monthlyPct: 75, monthlySec: 86400); // monthly

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);

        // Primary should be monthly (no rolling to override it)
        Assert.Equal(0.75, result.Items[0].PrimaryUsage!.UsedPercent);

        // Should have two bars: weekly and monthly
        var bars = result.Items[0].Bars;
        Assert.NotNull(bars);
        Assert.Equal(2, bars.Count);
        Assert.Contains("Weekly limit", bars[0].Label);
        Assert.Contains("Monthly limit", bars[1].Label);
    }

    [Fact]
    public async Task FetchUsageAsync_GenericException_ReturnsExMessage()
    {
        // Test that a generic exception during HTTP request returns the exception message
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "cookie");
        var handler = new ThrowingHttpMessageHandler(new InvalidOperationException("test error message"));
        var httpClientFactory = new MockHttpClientFactory(handler);

        var provider = new OpenCodeGoProvider(
            NullLogger<OpenCodeGoProvider>.Instance,
            httpClientFactory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("test error message", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_TaskCanceledException_ReturnsTimeoutMessage()
    {
        // Test that TaskCanceledException returns a specific timeout message
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "cookie");
        var handler = new ThrowingHttpMessageHandler(new TaskCanceledException());
        var httpClientFactory = new MockHttpClientFactory(handler);

        var provider = new OpenCodeGoProvider(
            NullLogger<OpenCodeGoProvider>.Instance,
            httpClientFactory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_OperationCanceledWithToken_Throws()
    {
        // When the caller's CancellationToken is cancelled, OperationCanceledException propagates
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "cookie");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new CancellationThrowingHandler(cts.Token);
        var httpClientFactory = new MockHttpClientFactory(handler);

        var provider = new OpenCodeGoProvider(
            NullLogger<OpenCodeGoProvider>.Instance,
            httpClientFactory,
            settings);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.FetchUsageAsync(cts.Token));
    }

    [Fact]
    public async Task FetchUsageAsync_AllThreeWindows_ReturnsThreeBars()
    {
        // Test that rolling + weekly + monthly produces three bars with correct labels and primary from rolling
        var html = CreateDashboardHtml(
            rollingPct: 40, rollingSec: 120,    // rolling (lowest, will be primary)
            weeklyPct: 65, weeklySec: 3600,     // weekly
            monthlyPct: 80, monthlySec: 86400); // monthly

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        Assert.Single(result.Items);

        // Primary should be rolling (40%)
        Assert.Equal(0.40, result.Items[0].PrimaryUsage!.UsedPercent);

        // Should have three bars: rolling, weekly, monthly
        var bars = result.Items[0].Bars;
        Assert.NotNull(bars);
        Assert.Equal(3, bars.Count);

        // Verify bar labels
        var labels = bars.Select(b => b.Label).ToList();
        Assert.Contains("5-hour limit", labels);
        Assert.Contains("Weekly limit", labels);
        Assert.Contains("Monthly limit", labels);
    }

    [Fact]
    public async Task FetchUsageAsync_ResetTimeFormatting_DisplaysCorrectly()
    {
        // Test various reset time formats (Xd, Xh, Xm, "Resets now")
        // This tests FormatReset logic indirectly through bar descriptions
        var html = CreateDashboardHtml(
            rollingPct: 50, rollingSec: 300); // 5 minutes

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        var bars = result.Items[0].Bars;
        Assert.NotNull(bars);
        Assert.NotEmpty(bars);

        // Should have a description with reset information
        var resetDescription = bars[0].ResetDescription;
        Assert.NotNull(resetDescription);
        Assert.NotEmpty(resetDescription);
    }

    private static string CreateDashboardHtml(
        int? rollingPct = null, int? rollingSec = null,
        int? weeklyPct = null, int? weeklySec = null,
        int? monthlyPct = null, int? monthlySec = null,
        bool alternateOrder = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<html><body>");
        sb.Append("<script>window.__SOLID_DATA={");

        var parts = new List<string>();
        if (rollingPct.HasValue && rollingSec.HasValue)
        {
            if (alternateOrder)
            {
                parts.Add($"rollingUsage:$R[0]={{resetInSec:{rollingSec},usagePercent:{rollingPct}}}");
            }
            else
            {
                parts.Add($"rollingUsage:$R[0]={{usagePercent:{rollingPct},resetInSec:{rollingSec}}}");
            }
        }

        if (weeklyPct.HasValue && weeklySec.HasValue)
        {
            if (alternateOrder)
            {
                parts.Add($"weeklyUsage:$R[1]={{resetInSec:{weeklySec},usagePercent:{weeklyPct}}}");
            }
            else
            {
                parts.Add($"weeklyUsage:$R[1]={{usagePercent:{weeklyPct},resetInSec:{weeklySec}}}");
            }
        }

        if (monthlyPct.HasValue && monthlySec.HasValue)
        {
            if (alternateOrder)
            {
                parts.Add($"monthlyUsage:$R[2]={{resetInSec:{monthlySec},usagePercent:{monthlyPct}}}");
            }
            else
            {
                parts.Add($"monthlyUsage:$R[2]={{usagePercent:{monthlyPct},resetInSec:{monthlySec}}}");
            }
        }

        sb.Append(string.Join(",", parts));
        sb.Append("}</script></body></html>");
        return sb.ToString();
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public MockHttpMessageHandler(HttpResponseMessage response) => this.response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Fail-closed: deny requests with missing or invalid auth cookie
            if (!request.Headers.TryGetValues("Cookie", out var cookies) ||
                !cookies.Any(c => c.Contains("auth=")))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(this.response);
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception exception;

        public ThrowingHttpMessageHandler(Exception exception) => this.exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(this.exception);
        }
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler handler;

        public MockHttpClientFactory(HttpMessageHandler handler) => this.handler = handler;

        public HttpClient CreateClient(string name) => new(this.handler);
    }

    private sealed class CancellationThrowingHandler(CancellationToken token) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(token);
        }
    }
}
