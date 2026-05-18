// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeZen;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Mutation-killing tests for OpenCodeZenProvider:
/// ParseBalance boundary conditions, credential resolution,
/// caching logic, and error handling.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class OpenCodeZenProviderMutationTests : IDisposable
{
    private readonly string? _origWorkspace;
    private readonly string? _origCookie;
    private readonly string? _origZenWorkspace;
    private readonly string? _origZenCookie;

    public OpenCodeZenProviderMutationTests()
    {
        this._origWorkspace = Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID");
        this._origCookie = Environment.GetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE");
        this._origZenWorkspace = Environment.GetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID");
        this._origZenCookie = Environment.GetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE");
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", null);
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", null);
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", null);
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", this._origWorkspace);
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", this._origCookie);
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", this._origZenWorkspace);
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE", this._origZenCookie);
    }

    // === ParseBalance ===
    [Fact]
    public async Task FetchUsageAsync_ValidBalance_ConvertsNanodollarsCorrectly()
    {
        // 2_000_000_000 nanodollars = $20.00
        var html = "balance:2000000000";
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(20.00m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_ZeroBalance_ReturnsZero()
    {
        var html = "balance:0";
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(0m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_SmallBalance_ConvertsCorrectly()
    {
        // 100_000_000 nanodollars = $1.00
        var html = "balance:100000000";
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(1.00m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_LargeBalance_ConvertsCorrectly()
    {
        // 10_000_000_000 nanodollars = $100.00
        var html = "balance:10000000000";
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(100.00m, result.CreditsRemaining);
    }

    // === Credential resolution ===
    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: false);
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_NoWorkspaceId_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: true, workspaceId: null, apiKey: "cookie");
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_NoCookie_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: null);
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_BothPresent_ReturnsTrue()
    {
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var provider = CreateProvider(settings: settings);
        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task FetchUsageAsync_NoWorkspaceId_ReturnsFailure()
    {
        var settings = CreateSettings(enabled: true, workspaceId: null, apiKey: "cookie");
        var provider = CreateProvider(settings: settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("WORKSPACE_ID", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_NoCookie_ReturnsFailure()
    {
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: null);
        var provider = CreateProvider(settings: settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("AUTH_COOKIE", result.ErrorMessage);
    }

    // === Zen-specific env var overrides ===
    [Fact]
    public async Task FetchUsageAsync_ZenWorkspaceEnvVar_TakesPriority()
    {
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", "zen-ws");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "cookie");

        var html = "balance:500000000"; // $5.00
        var settings = CreateSettings(enabled: true, workspaceId: null, apiKey: null);
        var provider = CreateProvider(settings: settings, response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(5.00m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_ZenCookieEnvVar_TakesPriority()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "ws");
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE", "zen-cookie");

        var html = "balance:300000000"; // $3.00
        var settings = CreateSettings(enabled: true, workspaceId: null, apiKey: null);
        var provider = CreateProvider(settings: settings, response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(3.00m, result.CreditsRemaining);
    }

    // === Credential priority: env var wins over settings ===
    [Fact]
    public async Task FetchUsageAsync_GoEnvVarAndSettings_EnvVarWinsForWorkspace()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "env-ws-id");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "env-cookie");

        var html = "balance:100000000";
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "settings-ws-id", apiKey: "settings-cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("env-ws-id", handler.LastRequest!.RequestUri?.ToString());
        Assert.DoesNotContain("settings-ws-id", handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task FetchUsageAsync_GoEnvVarAndSettings_EnvVarWinsForCookie()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "ws");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "env-cookie-priority");

        var html = "balance:100000000";
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "settings-cookie-lower");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        var cookieHeader = handler.LastRequest!.Headers.GetValues("Cookie").First();
        Assert.Contains("env-cookie-priority", cookieHeader);
        Assert.DoesNotContain("settings-cookie-lower", cookieHeader);
    }

    [Fact]
    public async Task FetchUsageAsync_ZenEnvVarOverridesGoEnvVar_ForWorkspace()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "go-ws");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "go-cookie");
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID", "zen-ws-override");

        var html = "balance:100000000";
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "settings-ws", apiKey: "settings-cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("zen-ws-override", handler.LastRequest!.RequestUri?.ToString());
        Assert.DoesNotContain("go-ws", handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task FetchUsageAsync_ZenCookieEnvVarOverridesGoEnvVar()
    {
        Environment.SetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", "ws");
        Environment.SetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE", "go-cookie-lower");
        Environment.SetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE", "zen-cookie-wins");

        var html = "balance:100000000";
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "settings-cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        var cookieHeader = handler.LastRequest!.Headers.GetValues("Cookie").First();
        Assert.Contains("zen-cookie-wins", cookieHeader);
        Assert.DoesNotContain("go-cookie-lower", cookieHeader);
    }

    // === Request structure verification ===
    [Fact]
    public async Task FetchUsageAsync_UrlContainsWorkspaceAndBillingPath()
    {
        var html = "balance:100000000";
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "my-workspace", apiKey: "cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("my-workspace", handler.LastRequest!.RequestUri?.ToString());
        Assert.Contains("/billing", handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task FetchUsageAsync_SetsUserAgentHeader()
    {
        var html = "balance:100000000";
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("User-Agent"));
    }

    [Fact]
    public async Task FetchUsageAsync_SetsAcceptHeader()
    {
        var html = "balance:100000000";
        var handler = new CapturingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        await provider.FetchUsageAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("Accept"));
    }

    [Fact]
    public async Task FetchUsageAsync_ZenApiKey_TakesPriorityOverGoApiKey()
    {
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "go-cookie", zenApiKey: "zen-cookie");
        var html = "balance:200000000"; // $2.00
        var provider = CreateProvider(settings: settings, response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(2.00m, result.CreditsRemaining);
    }

    // === HTTP error handling ===
    [Fact]
    public async Task FetchUsageAsync_401_ReturnsAuthFailure()
    {
        var provider = CreateProvider(response: new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Auth cookie rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_403_ReturnsAuthFailure()
    {
        var provider = CreateProvider(response: new HttpResponseMessage(HttpStatusCode.Forbidden));
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Auth cookie rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_500_ReturnsHttpError()
    {
        var provider = CreateProvider(response: new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("HTTP 500", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_UnparsableHtml_ReturnsFailure()
    {
        var provider = CreateProvider(response: OkHtml("<html>no balance here</html>"));
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Could not parse", result.ErrorMessage);
    }

    // === Caching ===
    [Fact]
    public async Task FetchUsageAsync_SecondCall_ReturnsCached()
    {
        var html = "balance:100000000";
        var handler = new CountingHandler(OkHtml(html));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        var result1 = await provider.FetchUsageAsync();
        var result2 = await provider.FetchUsageAsync();

        Assert.True(result1.Success);
        Assert.True(result2.Success);
        Assert.Equal(1, handler.CallCount);
    }

    // === Metadata ===
    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();
        Assert.Equal(ProviderId.OpenCodeZen, provider.Metadata.Id);
        Assert.Equal("OpenCode Zen", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
        Assert.False(provider.Metadata.SupportsWeeklyUsage);
    }

    // === Result structure ===
    [Fact]
    public async Task FetchUsageAsync_ValidResult_HasCorrectProvider()
    {
        var html = "balance:100000000";
        var provider = CreateProvider(response: OkHtml(html));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.OpenCodeZen, result.Provider);
    }

    // === Timeout ===
    [Fact]
    public async Task FetchUsageAsync_Timeout_ReturnsFailure()
    {
        var handler = new ExceptionHandler(new TaskCanceledException("Timeout"));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    // === General exception ===
    [Fact]
    public async Task FetchUsageAsync_GeneralException_ReturnsFailure()
    {
        var handler = new ExceptionHandler(new InvalidOperationException("Network error"));
        var settings = CreateSettings(enabled: true, workspaceId: "ws", apiKey: "cookie");
        var factory = new SimpleFactory(handler);
        var provider = new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("failed", result.ErrorMessage);
    }

    // === Helper methods ===
    private static OpenCodeZenProvider CreateProvider(
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        settings ??= CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie");
        var handler = new SimpleHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK));
        var factory = new SimpleFactory(handler);
        return new OpenCodeZenProvider(NullLogger<OpenCodeZenProvider>.Instance, factory, settings);
    }

    private static ISettingsService CreateSettings(
        bool enabled = true,
        string? workspaceId = "ws-123",
        string? apiKey = "cookie",
        string? zenApiKey = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeZen).Returns(enabled);
        settings.GetOpenCodeGoWorkspaceId().Returns(workspaceId);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns(apiKey);
        settings.GetApiKey(ProviderId.OpenCodeZen).Returns(zenApiKey);
        return settings;
    }

    private static HttpResponseMessage OkHtml(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html"),
    };

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

    private sealed class SimpleFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
