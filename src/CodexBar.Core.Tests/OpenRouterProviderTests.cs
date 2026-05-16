// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenRouter;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

[Collection("EnvironmentVariableTests")]
public class OpenRouterProviderTests
{
    private const string ValidApiKey = "test-api-key-12345";

    /// <summary>
    /// Custom HttpMessageHandler that can return a response or throw an exception.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;

        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            this._response = response;
            this._exception = null;
        }

        public MockHttpMessageHandler(Exception exception)
        {
            this._response = null;
            this._exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (this._exception != null)
            {
                throw this._exception;
            }

            return Task.FromResult(this._response!);
        }
    }

    /// <summary>
    /// Creates an HttpClient with the given response.
    /// </summary>
    private static HttpClient CreateHttpClient(HttpResponseMessage response)
    {
        var handler = new MockHttpMessageHandler(response);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Creates an HttpClient that throws the given exception.
    /// </summary>
    private static HttpClient CreateHttpClient(Exception exception)
    {
        var handler = new MockHttpMessageHandler(exception);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Creates an IHttpClientFactory that returns the given HttpClient.
    /// </summary>
    private static IHttpClientFactory CreateHttpClientFactory(HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    /// <summary>
    /// Creates an OpenRouterProvider with the given dependencies.
    /// </summary>
    private static OpenRouterProvider CreateProvider(
        ISettingsService? settingsService = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        var settings = settingsService ?? Substitute.For<ISettingsService>();
        var factory = httpClientFactory ?? Substitute.For<IHttpClientFactory>();
        var logger = NullLogger<OpenRouterProvider>.Instance;

        return new OpenRouterProvider(logger, factory, settings);
    }

    [Fact]
    public void Metadata_IsCorrect()
    {
        var provider = CreateProvider();

        Assert.Equal(ProviderId.OpenRouter, provider.Metadata.Id);
        Assert.Equal("OpenRouter", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(false);

        var provider = CreateProvider(settingsService: settings);

        var result = await provider.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_NoApiKey_ReturnsFalse()
    {
        // Clear env var that ResolveApiKey checks first
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
        try
        {
            var settings = Substitute.For<ISettingsService>();
            settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
            settings.GetApiKey(ProviderId.OpenRouter).Returns((string?)null);

            var provider = CreateProvider(settingsService: settings);

            var result = await provider.IsAvailableAsync();

            Assert.False(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", envKey);
        }
    }

    [Fact]
    public async Task IsAvailableAsync_WithApiKey_ReturnsTrue()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var provider = CreateProvider(settingsService: settings);

        var result = await provider.IsAvailableAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task FetchUsageAsync_NoApiKey_ReturnsFailure()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns((string?)null);

        var provider = CreateProvider(settingsService: settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.NotEmpty(result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ValidResponse_ReturnsCredits()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var jsonResponse = """{"data":{"total_credits":100.0,"total_usage":25.0}}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json"),
        };
        var httpClient = CreateHttpClient(response);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Equal(75.0m, result.CreditsRemaining); // 100 - 25
    }

    [Fact]
    public async Task FetchUsageAsync_MissingDataField_ReturnsFailure()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var jsonResponse = """{"error":"something"}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json"),
        };
        var httpClient = CreateHttpClient(response);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Contains("missing 'data'", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_MissingCreditFields_ReturnsFailure()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var jsonResponse = """{"data":{"total_credits":100.0}}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json"),
        };
        var httpClient = CreateHttpClient(response);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Contains("missing credit fields", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_Unauthorized_ReturnsAuthError()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var exception = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        var httpClient = CreateHttpClient(exception);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Contains("invalid", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_Forbidden_ReturnsAuthError()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var exception = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);
        var httpClient = CreateHttpClient(exception);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Contains("invalid", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_RateLimited_ReturnsRateLimitError()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var exception = new HttpRequestException("Too Many Requests", null, (HttpStatusCode)429);
        var httpClient = CreateHttpClient(exception);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Contains("rate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_GenericException_ReturnsFailure()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var exception = new InvalidOperationException("Something went wrong");
        var httpClient = CreateHttpClient(exception);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Contains("Something went wrong", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ZeroCredits_ReturnsZeroPercent()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.OpenRouter).Returns(ValidApiKey);

        var jsonResponse = """{"data":{"total_credits":0.0,"total_usage":0.0}}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json"),
        };
        var httpClient = CreateHttpClient(response);
        var httpClientFactory = CreateHttpClientFactory(httpClient);

        var provider = CreateProvider(settingsService: settings, httpClientFactory: httpClientFactory);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Equal(0.0m, result.CreditsRemaining); // 0 - 0
    }
}
