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
/// Mutation-killing tests for OpenRouterProvider:
/// API key resolution, usage calculation, error handling, and boundary conditions.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class OpenRouterProviderMutationTests : IDisposable
{
    private readonly string? _origKey;

    public OpenRouterProviderMutationTests()
    {
        this._origKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", this._origKey);
    }

    // === IsAvailableAsync ===
    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: false, apiKey: "key");
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_NoKey_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: true, apiKey: null);
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_EmptyKey_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: true, apiKey: string.Empty);
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_WhitespaceKey_ReturnsFalse()
    {
        var settings = CreateSettings(enabled: true, apiKey: "   ");
        var provider = CreateProvider(settings: settings);
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_ValidKey_ReturnsTrue()
    {
        var settings = CreateSettings(enabled: true, apiKey: "valid-key");
        var provider = CreateProvider(settings: settings);
        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_EnvVar_ReturnsTrue()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "env-key");
        var settings = CreateSettings(enabled: true, apiKey: null);
        var provider = CreateProvider(settings: settings);
        Assert.True(await provider.IsAvailableAsync());
    }

    // === FetchUsageAsync ===
    [Fact]
    public async Task FetchUsageAsync_NoKey_ReturnsFailure()
    {
        var settings = CreateSettings(enabled: true, apiKey: null);
        var provider = CreateProvider(settings: settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No API key", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ValidResponse_ReturnsBalance()
    {
        var json = """{"data": {"total_credits": 100.0, "total_usage": 30.0}}""";
        var provider = CreateProvider(response: OkJson(json));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.Equal(70.0m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_ZeroTotalCredits_ZeroUsedPercent()
    {
        var json = """{"data": {"total_credits": 0.0, "total_usage": 0.0}}""";
        var provider = CreateProvider(response: OkJson(json));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(0m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_FullUsage_NegativeBalance()
    {
        var json = """{"data": {"total_credits": 50.0, "total_usage": 60.0}}""";
        var provider = CreateProvider(response: OkJson(json));
        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(-10.0m, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_MissingDataField_ReturnsFailure()
    {
        var json = """{"other": 123}""";
        var provider = CreateProvider(response: OkJson(json));
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("'data'", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_MissingCreditFields_ReturnsFailure()
    {
        var json = """{"data": {"something_else": 123}}""";
        var provider = CreateProvider(response: OkJson(json));
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("credit fields", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_401_ReturnsAuthError()
    {
        var handler = new ExceptionHandler(new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized));
        var settings = CreateSettings(enabled: true, apiKey: "key");
        var factory = new SimpleFactory(handler);
        var provider = new OpenRouterProvider(NullLogger<OpenRouterProvider>.Instance, factory, settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("invalid or revoked", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_403_ReturnsAuthError()
    {
        var handler = new ExceptionHandler(new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden));
        var settings = CreateSettings(enabled: true, apiKey: "key");
        var factory = new SimpleFactory(handler);
        var provider = new OpenRouterProvider(NullLogger<OpenRouterProvider>.Instance, factory, settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("invalid or revoked", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_429_ReturnsRateLimitError()
    {
        var handler = new ExceptionHandler(new HttpRequestException("Rate limited", null, (System.Net.HttpStatusCode)429));
        var settings = CreateSettings(enabled: true, apiKey: "key");
        var factory = new SimpleFactory(handler);
        var provider = new OpenRouterProvider(NullLogger<OpenRouterProvider>.Instance, factory, settings);
        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Rate limited", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_Timeout_ReturnsTimeoutError()
    {
        var handler = new ExceptionHandler(new OperationCanceledException("timeout"));
        var settings = CreateSettings(enabled: true, apiKey: "key");
        var factory = new SimpleFactory(handler);
        var provider = new OpenRouterProvider(NullLogger<OpenRouterProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_GeneralException_ReturnsError()
    {
        var handler = new ExceptionHandler(new InvalidOperationException("test error"));
        var settings = CreateSettings(enabled: true, apiKey: "key");
        var factory = new SimpleFactory(handler);
        var provider = new OpenRouterProvider(NullLogger<OpenRouterProvider>.Instance, factory, settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("test error", result.ErrorMessage);
    }

    // === Metadata ===
    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();
        Assert.Equal(ProviderId.OpenRouter, provider.Metadata.Id);
        Assert.Equal("OpenRouter", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
        Assert.False(provider.Metadata.SupportsWeeklyUsage);
        Assert.Equal("https://openrouter.ai/activity", provider.Metadata.DashboardUrl);
    }

    // === Helper methods ===
    private static OpenRouterProvider CreateProvider(
        ISettingsService? settings = null,
        HttpResponseMessage? response = null)
    {
        settings ??= CreateSettings(enabled: true, apiKey: "test-key");
        var handler = new SimpleHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK));
        var factory = new SimpleFactory(handler);
        return new OpenRouterProvider(NullLogger<OpenRouterProvider>.Instance, factory, settings);
    }

    private static ISettingsService CreateSettings(bool enabled = true, string? apiKey = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(enabled);
        settings.GetApiKey(ProviderId.OpenRouter).Returns(apiKey);
        return settings;
    }

    private static HttpResponseMessage OkJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class SimpleHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
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
