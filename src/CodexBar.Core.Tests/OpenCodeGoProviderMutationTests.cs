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
/// Mutation-killing tests for OpenCodeGoProvider:
/// FormatReset boundary conditions, ParseDashboardHtml edge cases,
/// BuildResult/BuildBars logic, caching, and credential resolution.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class OpenCodeGoProviderMutationTests : IDisposable
{
    private readonly string? _origWorkspace;
    private readonly string? _origCookie;

    public OpenCodeGoProviderMutationTests()
    {
        this._origWorkspace = Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID");
        this._origCookie = Environment.GetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE");
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", null);
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", this._origWorkspace);
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", this._origCookie);
    }

    // === FormatReset boundary conditions ===
    [Fact]
    public async Task FetchUsageAsync_ResetInMinutes_ShowsMinutes()
    {
        // resetInSec = 1800 (30 minutes < 1 hour)
        var html = BuildHtml(rollingPct: 40, rollingSec: 1800);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var bar = result.Items![0].Bars![0];
        Assert.Contains("Resets", bar.ResetDescription);
        Assert.Contains("m", bar.ResetDescription);
    }

    [Fact]
    public async Task FetchUsageAsync_ResetInHours_ShowsHours()
    {
        // resetInSec = 7200 (2 hours)
        var html = BuildHtml(rollingPct: 60, rollingSec: 7200);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var bar = result.Items![0].Bars![0];
        Assert.Contains("Resets", bar.ResetDescription);
        Assert.Contains("h", bar.ResetDescription);
    }

    // === ParseDashboardHtml - multiple windows ===
    [Fact]
    public async Task FetchUsageAsync_AllWindows_BuildsThreeBars()
    {
        var html = BuildHtmlAllWindows(rolling: (50, 3600), weekly: (30, 86400), monthly: (10, 172800));
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Items);
        var bars = result.Items![0].Bars!;
        Assert.Equal(3, bars.Count);
        Assert.Equal("5-hour limit", bars[0].Label);
        Assert.Equal("Weekly limit", bars[1].Label);
        Assert.Equal("Monthly limit", bars[2].Label);
    }

    [Fact]
    public async Task FetchUsageAsync_OnlyMonthly_BuildsOneBar()
    {
        var html = "monthlyUsage:$R[0]={usagePercent:80,resetInSec:7200}";
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var bars = result.Items![0].Bars!;
        Assert.Single(bars);
        Assert.Equal("Monthly limit", bars[0].Label);
    }

    [Fact]
    public async Task FetchUsageAsync_OnlyWeekly_PrimaryUsageIsNull()
    {
        // Only weekly, no rolling — primary usage derives from rolling or monthly
        var html = "weeklyUsage:$R[0]={usagePercent:70,resetInSec:3600}";
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);

        // PrimaryUsage should be null since rolling and monthly are both null
        Assert.Null(result.Items![0].PrimaryUsage);
    }

    // === UsedPercent boundary values ===
    [Fact]
    public async Task FetchUsageAsync_Percent0_ClampsToZero()
    {
        var html = BuildHtml(rollingPct: 0, rollingSec: 3600);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(0.0, result.Items![0].Bars![0].UsedPercent);
    }

    [Fact]
    public async Task FetchUsageAsync_Percent100_ClampsTo1()
    {
        var html = BuildHtml(rollingPct: 100, rollingSec: 3600);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(1.0, result.Items![0].Bars![0].UsedPercent);
    }

    [Fact]
    public async Task FetchUsageAsync_PercentOver100_ClampsTo1()
    {
        var html = BuildHtml(rollingPct: 150, rollingSec: 3600);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(1.0, result.Items![0].Bars![0].UsedPercent);
    }

    // === UsageLabel ===
    [Fact]
    public async Task FetchUsageAsync_UsageLabel_ContainsPercentUsed()
    {
        var html = BuildHtml(rollingPct: 65, rollingSec: 3600);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("65% used", result.Items![0].PrimaryUsage!.UsageLabel);
    }

    // === Caching ===
    [Fact]
    public async Task FetchUsageAsync_SecondCall_ReturnsCachedResult()
    {
        var html = BuildHtml(rollingPct: 50, rollingSec: 3600);
        var handler = new CountingHandler(OkHtml(html));
        var provider = CreateProviderWithHandler(handler);

        var result1 = await provider.FetchUsageAsync();
        var result2 = await provider.FetchUsageAsync();

        Assert.True(result1.Success);
        Assert.True(result2.Success);
        Assert.Equal(1, handler.CallCount); // Only 1 HTTP call due to caching
    }

    // === No-parse failure ===
    [Fact]
    public async Task FetchUsageAsync_UnparseableHtml_ReturnsFailure()
    {
        var provider = CreateProvider(response: OkHtml("<html>nothing here</html>"));
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Could not parse", result.ErrorMessage);
    }

    // === TaskCanceledException ===
    [Fact]
    public async Task FetchUsageAsync_Timeout_ReturnsFailure()
    {
        var handler = new ExceptionHandler(new TaskCanceledException("Timeout"));
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "cookie");
        var factory = CreateFactory(handler);
        var provider = new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    // === Metadata ===
    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();
        Assert.Equal(ProviderId.OpenCodeGo, provider.Metadata.Id);
        Assert.Equal("OpenCode Go", provider.Metadata.DisplayName);
        Assert.Equal("https://opencode.ai/go", provider.Metadata.DashboardUrl);
        Assert.Null(provider.Metadata.StatusPageUrl);
        Assert.False(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
        Assert.False(provider.Metadata.SupportsWeeklyUsage);
    }

    // === Env var credential resolution ===
    [Fact]
    public async Task FetchUsageAsync_EnvVarCredentials_UsesEnvVars()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "env-ws");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "env-cookie");

        var html = BuildHtml(rollingPct: 20, rollingSec: 300);
        var settings = CreateSettings(enabled: true, workspaceId: null, apiKey: null);
        var provider = CreateProvider(settings: settings, response: OkHtml(html));

        var result = await provider.FetchUsageAsync();
        Assert.True(result.Success);
    }

    // === Env var takes priority over settings ===
    [Fact]
    public async Task FetchUsageAsync_BothEnvVarAndSettings_EnvVarWinsForWorkspace()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "env-workspace");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "env-cookie");

        var html = BuildHtml(rollingPct: 30, rollingSec: 600);
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "settings-workspace", apiKey: "settings-cookie");
        var factory = CreateFactory(handler);
        var provider = new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("env-workspace", handler.LastRequest!.RequestUri?.ToString());
        Assert.DoesNotContain("settings-workspace", handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task FetchUsageAsync_BothEnvVarAndSettings_EnvVarWinsForCookie()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "env-ws");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "env-cookie-value");

        var html = BuildHtml(rollingPct: 30, rollingSec: 600);
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "settings-ws", apiKey: "settings-cookie-value");
        var factory = CreateFactory(handler);
        var provider = new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        var cookieHeader = handler.LastRequest!.Headers.GetValues("Cookie").First();
        Assert.Contains("env-cookie-value", cookieHeader);
        Assert.DoesNotContain("settings-cookie-value", cookieHeader);
    }

    // === Request structure verification ===
    [Fact]
    public async Task FetchUsageAsync_SetsUserAgentHeader()
    {
        var html = BuildHtml(rollingPct: 30, rollingSec: 600);
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var factory = CreateFactory(handler);
        var provider = new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("User-Agent"));
    }

    [Fact]
    public async Task FetchUsageAsync_SetsAcceptHeader()
    {
        var html = BuildHtml(rollingPct: 30, rollingSec: 600);
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var factory = CreateFactory(handler);
        var provider = new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("Accept"));
        Assert.Equal("text/html", handler.LastRequest.Headers.GetValues("Accept").First());
    }

    [Fact]
    public async Task FetchUsageAsync_UrlContainsWorkspaceId()
    {
        var html = BuildHtml(rollingPct: 30, rollingSec: 600);
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "my-special-ws", apiKey: "cookie");
        var factory = CreateFactory(handler);
        var provider = new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("my-special-ws", handler.LastRequest!.RequestUri?.ToString());
        Assert.StartsWith("https://opencode.ai/workspace/", handler.LastRequest.RequestUri?.ToString());
        Assert.EndsWith("/go", handler.LastRequest.RequestUri?.ToString());
    }

    // === ResetInSec = 0 boundary ===
    [Fact]
    public async Task FetchUsageAsync_ResetZeroSeconds_ShowsResetsNowOrMinutes()
    {
        var html = BuildHtml(rollingPct: 80, rollingSec: 0);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var bar = result.Items![0].Bars![0];
        Assert.Contains("Resets", bar.ResetDescription);
    }

    // === ResetInSec in days ===
    [Fact]
    public async Task FetchUsageAsync_ResetInDays_ShowsDays()
    {
        // 172800 seconds = 2 days — safely in the days branch
        var html = BuildHtml(rollingPct: 10, rollingSec: 172800);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var bar = result.Items![0].Bars![0];
        Assert.Contains("d", bar.ResetDescription);
    }

    // === Item structure ===
    [Fact]
    public async Task FetchUsageAsync_ValidResponse_ItemHasCorrectKey()
    {
        var html = BuildHtml(rollingPct: 40, rollingSec: 3600);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("opencode-go:go", result.Items![0].Key);
        Assert.Equal("OpenCode Go", result.Items![0].DisplayName);
        Assert.True(result.Items![0].Success);
    }

    // === ResetsAt calculation ===
    [Fact]
    public async Task FetchUsageAsync_ResetsAt_IsInFuture()
    {
        var html = BuildHtml(rollingPct: 40, rollingSec: 7200);
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var bar = result.Items![0].Bars![0];
        Assert.NotNull(bar.ResetsAt);
        Assert.True(bar.ResetsAt > DateTimeOffset.UtcNow);
    }

    // === Helper methods ===
    private static OpenCodeGoProvider CreateProvider(
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        settings ??= CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie");
        var handler = new SimpleHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK));
        var factory = CreateFactory(handler);
        return new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);
    }

    private static OpenCodeGoProvider CreateProviderWithHandler(CountingHandler handler)
    {
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie");
        var factory = CreateFactory(handler);
        return new OpenCodeGoProvider(NullLogger<OpenCodeGoProvider>.Instance, factory, settings);
    }

    private static ISettingsService CreateSettings(bool enabled, string? workspaceId, string? apiKey)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeGo).Returns(enabled);
        settings.GetOpenCodeGoWorkspaceId().Returns(workspaceId);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns(apiKey);
        return settings;
    }

    private static HttpResponseMessage OkHtml(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html"),
    };

    private static string BuildHtml(int rollingPct, int rollingSec) =>
        $"rollingUsage:$R[0]={{usagePercent:{rollingPct},resetInSec:{rollingSec}}}";

    private static string BuildHtmlAllWindows((int pct, int sec) rolling, (int pct, int sec) weekly, (int pct, int sec) monthly) =>
        $"rollingUsage:$R[0]={{usagePercent:{rolling.pct},resetInSec:{rolling.sec}}} " +
        $"weeklyUsage:$R[1]={{usagePercent:{weekly.pct},resetInSec:{weekly.sec}}} " +
        $"monthlyUsage:$R[2]={{usagePercent:{monthly.pct},resetInSec:{monthly.sec}}}";

    private sealed class SimpleHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            this.LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class CountingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            this.CallCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class ExceptionHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw exception;
    }

    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        return factory;
    }
}
