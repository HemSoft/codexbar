// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeZen;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class OpenCodeZenProviderTests
{
    private static ISettingsService CreateSettingsService(
        bool enabled = true,
        string? workspaceId = null,
        string? authCookie = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeZen).Returns(enabled);
        settings.GetOpenCodeGoWorkspaceId().Returns(workspaceId);
        settings.GetApiKey(ProviderId.OpenCodeZen).Returns(authCookie);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns((string?)null);
        return settings;
    }

    private static IHttpClientFactory CreateHttpClientFactory(string html, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new MockHttpMessageHandler(html, statusCode);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        return factory;
    }

    [Fact]
    public void Metadata_IsCorrect()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

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
            Substitute.For<IHttpClientFactory>(),
            CreateSettingsService(workspaceId: null, authCookie: null));

        var result = await provider.IsAvailableAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithCredentials_ReturnsTrue()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.IsAvailableAsync();
        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailableAsync_DisabledInSettings_ReturnsFalse()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123", enabled: false));

        var result = await provider.IsAvailableAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WorkspaceWithoutCookie_ReturnsFalse()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: null));

        var result = await provider.IsAvailableAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task FetchUsageAsync_NoWorkspaceId_ReturnsFailure()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            CreateSettingsService(workspaceId: null, authCookie: "cookie"));

        var result = await provider.FetchUsageAsync();
        Assert.False(result.Success);
        Assert.Contains("OPENCODE_ZEN_WORKSPACE_ID", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_NoAuthCookie_ReturnsFailure()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: null));

        var result = await provider.FetchUsageAsync();
        Assert.False(result.Success);
        Assert.Contains("OPENCODE_ZEN_AUTH_COOKIE", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_Success_ReturnsBalance()
    {
        // SSR HTML with balance:2000000000 (nanodollars) = $20.00
        var html = "<html>some stuff balance:2000000000,reload:null more stuff</html>";

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            CreateHttpClientFactory(html, HttpStatusCode.OK),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

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

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            CreateHttpClientFactory(html, HttpStatusCode.OK),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(10.50m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_Unauthorized_ReturnsKeyError()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            CreateHttpClientFactory("unauthorized", HttpStatusCode.Unauthorized),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "bad-cookie"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Auth cookie rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_MissingBalanceInHtml_ReturnsFailure()
    {
        var html = "<html>no balance data here</html>";

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            CreateHttpClientFactory(html, HttpStatusCode.OK),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Could not parse balance", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ServerError_ReturnsHttpError()
    {
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            CreateHttpClientFactory("error", HttpStatusCode.InternalServerError),
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("HTTP 500", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_CachesResult()
    {
        var html = "<html>some stuff balance:2000000000,reload:null more stuff</html>";
        var handler = new MockHttpMessageHandler(html, HttpStatusCode.OK);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            factory,
            CreateSettingsService(workspaceId: "wrk_test", authCookie: "cookie123"));

        // First call should hit HTTP
        var result1 = await provider.FetchUsageAsync();
        Assert.True(result1.Success);

        // Second call should use cache (same result)
        var result2 = await provider.FetchUsageAsync();
        Assert.True(result2.Success);

        // Verify only 1 HTTP request was made (second call used cache)
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task FetchUsageAsync_WorkspaceSwitch_BypassesCache()
    {
        var html = "<html>some stuff balance:2000000000,reload:null more stuff</html>";
        var handler = new MockHttpMessageHandler(html, HttpStatusCode.OK);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var settings = CreateSettingsService(workspaceId: "wrk_a", authCookie: "cookie123");
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            factory,
            settings);

        // First call with workspace wrk_a
        var result1 = await provider.FetchUsageAsync();
        Assert.True(result1.Success);

        // Simulate workspace switch
        settings.GetOpenCodeGoWorkspaceId().Returns("wrk_b");

        // Second call should hit HTTP again because workspace changed
        var result2 = await provider.FetchUsageAsync();
        Assert.True(result2.Success);

        // Verify 2 HTTP requests were made (cache bypassed)
        Assert.Equal(2, handler.SendCount);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string html;
        private readonly HttpStatusCode statusCode;
        private int sendCount;

        public MockHttpMessageHandler(string html, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            this.html = html;
            this.statusCode = statusCode;
        }

        public int SendCount => this.sendCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.sendCount);
            var response = new HttpResponseMessage(this.statusCode)
            {
                Content = new StringContent(this.html, System.Text.Encoding.UTF8, "text/html"),
            };
            return Task.FromResult(response);
        }
    }
}
