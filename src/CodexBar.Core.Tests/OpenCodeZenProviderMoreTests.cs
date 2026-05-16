// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeZen;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

[Collection("OpenCodeZenEnvVars")]
public class OpenCodeZenProviderMoreTests
{
    private static OpenCodeZenProvider CreateProviderWithHandler(
        out MockHttpMessageHandler handler,
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        settings ??= CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie");
        handler = new MockHttpMessageHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK));
        var httpClientFactory = new MockHttpClientFactory(handler);

        return new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            httpClientFactory,
            settings);
    }

    private static OpenCodeZenProvider CreateProvider(
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
        settings.IsProviderEnabled(ProviderId.OpenCodeZen).Returns(enabled);
        settings.GetOpenCodeGoWorkspaceId().Returns(workspaceId);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns(apiKey);
        settings.GetApiKey(ProviderId.OpenCodeZen).Returns((string?)null);
        return settings;
    }

    [Fact]
    public async Task IsAvailableAsync_NoWorkspaceId_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: true, workspaceId: null, apiKey: "auth-cookie");
        var provider = CreateProvider(settings: settings);

        var result = await provider.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_NoAuthCookie_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: null);
        var provider = CreateProvider(settings: settings);

        var result = await provider.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task FetchUsageAsync_Forbidden_ReturnsAuthFailure()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        var provider = CreateProvider(response: response);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Auth cookie", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_CachesResult_SecondCallUsesCache()
    {
        var html = "<html>some data balance:1000000000 more stuff</html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProviderWithHandler(out var handler, response: response);

        // First call should hit HTTP
        var result1 = await provider.FetchUsageAsync();
        Assert.True(result1.Success);
        Assert.Equal(10.00m, result1.CreditsRemaining);

        // Second call should use cache (same result)
        var result2 = await provider.FetchUsageAsync();
        Assert.True(result2.Success);
        Assert.Equal(10.00m, result2.CreditsRemaining);

        // Verify only 1 HTTP request was made (second call used cache)
        Assert.Equal(1, handler!.SendCount);
    }

    [Fact]
    public async Task FetchUsageAsync_ZeroBalance_ReturnsZeroCredits()
    {
        var html = "<html>data balance:0 here</html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(response: response);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(0m, result.CreditsRemaining);
        Assert.Equal(ProviderId.OpenCodeZen, result.Provider);
    }

    [Fact]
    public async Task FetchUsageAsync_TaskCanceledException_ReturnsTimeoutFailure()
    {
        var handler = new CancellingHttpMessageHandler();
        var httpClientFactory = new MockHttpClientFactory(handler);
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie");

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            httpClientFactory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_GenericException_ReturnsErrorMessage()
    {
        var handler = new ExceptionThrowingHttpMessageHandler(new InvalidOperationException("Test error"));
        var httpClientFactory = new MockHttpClientFactory(handler);
        var settings = CreateSettings(enabled: true, workspaceId: "ws-123", apiKey: "auth-cookie");

        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            httpClientFactory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("OpenCode Zen request failed", result.ErrorMessage);
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
            Interlocked.Increment(ref this.sendCount);
            return Task.FromResult(this.response);
        }
    }

    private sealed class CancellingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("Request timed out"));
        }
    }

    private sealed class ExceptionThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception exception;

        public ExceptionThrowingHttpMessageHandler(Exception exception) => this.exception = exception;

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
}
