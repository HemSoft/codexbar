// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenRouter;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Additional tests for OpenRouterProvider covering timeout, edge cases,
/// and response variations not covered in the main OpenRouterProviderTests.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class OpenRouterProviderMoreTests
{
    private const string ValidApiKey = "test-api-key-more";

    private static OpenRouterProvider CreateProvider(
        ISettingsService? settings = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        settings ??= CreateDefaultSettings();
        httpClientFactory ??= Substitute.For<IHttpClientFactory>();
        return new OpenRouterProvider(
            NullLogger<OpenRouterProvider>.Instance,
            httpClientFactory,
            settings);
    }

    private static ISettingsService CreateDefaultSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);
        return settings;
    }

    [Fact]
    public async Task FetchUsageAsync_Timeout_ReturnsTimeoutError()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
        try
        {
            var settings = CreateDefaultSettings();
            var handler = new TimeoutHandler();
            var client = new HttpClient(handler);
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(client);

            var provider = CreateProvider(settings: settings, httpClientFactory: factory);

            var result = await provider.FetchUsageAsync();

            Assert.False(result.Success);
            Assert.Equal(ProviderId.OpenRouter, result.Provider);
            Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", envKey);
        }
    }

    [Fact]
    public async Task FetchUsageAsync_NegativeBalance_ReturnsNegativeCredits()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
        try
        {
            var settings = CreateDefaultSettings();
            var json = """{"data":{"total_credits":50.0,"total_usage":75.0}}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            var handler = new SingleResponseHandler(response);
            var client = new HttpClient(handler);
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(client);

            var provider = CreateProvider(settings: settings, httpClientFactory: factory);

            var result = await provider.FetchUsageAsync();

            Assert.True(result.Success);
            Assert.Equal(-25.0m, result.CreditsRemaining);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", envKey);
        }
    }

    [Fact]
    public async Task FetchUsageAsync_LargeCredits_ParsesCorrectly()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
        try
        {
            var settings = CreateDefaultSettings();
            var json = """{"data":{"total_credits":10000.0,"total_usage":1234.56}}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            var handler = new SingleResponseHandler(response);
            var client = new HttpClient(handler);
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(client);

            var provider = CreateProvider(settings: settings, httpClientFactory: factory);

            var result = await provider.FetchUsageAsync();

            Assert.True(result.Success);
            Assert.Equal(8765.44m, result.CreditsRemaining);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", envKey);
        }
    }

    [Fact]
    public async Task IsAvailableAsync_EnabledWithSettingsKey_ReturnsTrue()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
        try
        {
            var settings = Substitute.For<ISettingsService>();
            settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
            settings.GetApiKey(ProviderId.OpenRouter).Returns("sk-or-test");

            var provider = CreateProvider(settings: settings);

            Assert.True(await provider.IsAvailableAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", envKey);
        }
    }

    /// <summary>
    /// Simulates OperationCanceledException where the caller's token is NOT cancelled
    /// (i.e., an internal timeout rather than external cancellation).
    /// </summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new OperationCanceledException("The operation was canceled.", new TimeoutException());
        }
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public SingleResponseHandler(HttpResponseMessage response) => this._response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(this._response);
        }
    }
}
