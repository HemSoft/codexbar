// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeZen;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

public class OpenCodeZenProviderTests
{
    [Fact]
    public void Metadata_IsCorrect()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        Assert.Equal(ProviderId.OpenCodeZen, provider.Metadata.Id);
        Assert.Equal("OpenCode Zen", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
        Assert.False(provider.Metadata.SupportsWeeklyUsage);
    }

    [Fact]
    public async Task IsAvailableAsync_NoCredentials_ReturnsFalse()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService(workspaceId: null, authCookie: null));

        var result = await provider.IsAvailableAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithCredentials_ReturnsTrue()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.IsAvailableAsync();
        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailableAsync_DisabledInSettings_ReturnsFalse()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "cookie123", enabled: false));

        var result = await provider.IsAvailableAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WorkspaceWithoutCookie_ReturnsFalse()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: null));

        var result = await provider.IsAvailableAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task FetchUsageAsync_NoWorkspaceId_ReturnsFailure()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService(workspaceId: null, authCookie: "cookie"));

        var result = await provider.FetchUsageAsync();
        Assert.False(result.Success);
        Assert.Contains("OPENCODE_GO_WORKSPACE_ID", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_NoAuthCookie_ReturnsFailure()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: null));

        var result = await provider.FetchUsageAsync();
        Assert.False(result.Success);
        Assert.Contains("OPENCODE_GO_AUTH_COOKIE", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_Success_ReturnsBalance()
    {
        // SSR HTML with balance:2000000000 (nanodollars) = $20.00
        var html = "<html>some stuff balance:2000000000,reload:null more stuff</html>";
        var handler = new StubHttpMessageHandler(html, HttpStatusCode.OK, "text/html");
        var httpClient = new HttpClient(handler);

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new SingleHttpClientFactory(httpClient),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(20.00m, result.CreditsRemaining);
        Assert.Equal(ProviderId.OpenCodeZen, result.Provider);
    }

    [Fact]
    public async Task FetchUsageAsync_SmallBalance_ParsesCorrectly()
    {
        // balance:1050000000 = $10.50
        var html = @"data:{balance:1050000000,reloadAmount:20}";
        var handler = new StubHttpMessageHandler(html, HttpStatusCode.OK, "text/html");
        var httpClient = new HttpClient(handler);

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new SingleHttpClientFactory(httpClient),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(10.50m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_Unauthorized_ReturnsKeyError()
    {
        var handler = new StubHttpMessageHandler("unauthorized", HttpStatusCode.Unauthorized, "text/html");
        var httpClient = new HttpClient(handler);

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new SingleHttpClientFactory(httpClient),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "bad-cookie"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Auth cookie rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_MissingBalanceInHtml_ReturnsFailure()
    {
        var html = "<html>no balance data here</html>";
        var handler = new StubHttpMessageHandler(html, HttpStatusCode.OK, "text/html");
        var httpClient = new HttpClient(handler);

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new SingleHttpClientFactory(httpClient),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Could not parse balance", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ServerError_ReturnsHttpError()
    {
        var handler = new StubHttpMessageHandler("error", HttpStatusCode.InternalServerError, "text/html");
        var httpClient = new HttpClient(handler);

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new SingleHttpClientFactory(httpClient),
            new DummySettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("HTTP 500", result.ErrorMessage);
    }

    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class SingleHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient client;

        public SingleHttpClientFactory(HttpClient client) => this.client = client;

        public HttpClient CreateClient(string name) => this.client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string responseContent;
        private readonly HttpStatusCode statusCode;
        private readonly string mediaType;

        public StubHttpMessageHandler(string responseContent, HttpStatusCode statusCode, string mediaType = "application/json")
        {
            this.responseContent = responseContent;
            this.statusCode = statusCode;
            this.mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(this.statusCode)
            {
                Content = new StringContent(this.responseContent, System.Text.Encoding.UTF8, this.mediaType),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class DummySettingsService : ISettingsService
    {
        private readonly string? workspaceId;
        private readonly string? authCookie;
        private readonly bool enabled;

        public DummySettingsService(string? workspaceId = null, string? authCookie = null, bool enabled = true)
        {
            this.workspaceId = workspaceId;
            this.authCookie = authCookie;
            this.enabled = enabled;
        }

        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
        public string? GetApiKey(ProviderId providerId) =>
            providerId == ProviderId.OpenCodeGo ? this.authCookie :
            providerId == ProviderId.OpenCodeZen ? this.authCookie : null;
        public bool IsProviderEnabled(ProviderId providerId) => this.enabled;
        public string? GetOpenCodeGoWorkspaceId() => this.workspaceId;
        public IReadOnlyList<string> GetCopilotAccounts() => [];
    }
}