// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Moonshot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

[Collection("EnvironmentVariableTests")]
public sealed class MoonshotProviderTests : IDisposable
{
    private const string ApiKey = "moonshot-test-key";

    private readonly string? _originalApiKey = Environment.GetEnvironmentVariable("MOONSHOT_API_KEY");

    public MoonshotProviderTests()
    {
        Environment.SetEnvironmentVariable("MOONSHOT_API_KEY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MOONSHOT_API_KEY", this._originalApiKey);
    }

    [Fact]
    public void Metadata_WhenRead_DescribesMoonshotCredits()
    {
        var provider = CreateProvider();

        Assert.Equal(ProviderId.Moonshot, provider.Metadata.Id);
        Assert.Equal("Moonshot (Kimi)", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
        Assert.False(provider.Metadata.SupportsWeeklyUsage);
    }

    [Fact]
    public void SettingsService_OnNewInstall_DisablesMoonshotByDefault()
    {
        var settingsDirectory = CreateTemporaryDirectory();
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, settingsDirectory);

            Assert.False(service.Load().Providers[ProviderId.Moonshot.ToString()].Enabled);
        }
        finally
        {
            Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    [Fact]
    public void SettingsService_WhenExistingSettingsLackMoonshot_TreatsProviderAsDisabled()
    {
        var settingsDirectory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), """{"providers":{"OpenRouter":{"enabled":true}}}""");
            var service = new SettingsService(NullLogger<SettingsService>.Instance, settingsDirectory);

            Assert.False(service.IsProviderEnabled(ProviderId.Moonshot));
            Assert.DoesNotContain(ProviderId.Moonshot.ToString(), service.Load().Providers.Keys);
        }
        finally
        {
            Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task IsAvailableAsync_WhenDisabled_ReturnsFalse()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Moonshot).Returns(false);
        settings.GetApiKey(ProviderId.Moonshot).Returns(ApiKey);

        var result = await CreateProvider(settings).IsAvailableAsync();

        Assert.False(result);
        settings.DidNotReceive().GetApiKey(Arg.Any<ProviderId>());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Authorization:", false)]
    [InlineData("Bearer moonshot-test-key", true)]
    public async Task IsAvailableAsync_WhenEnabled_ReflectsNormalizedApiKey(string? apiKey, bool expected)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Moonshot).Returns(true);
        settings.GetApiKey(ProviderId.Moonshot).Returns(apiKey);

        var result = await CreateProvider(settings).IsAvailableAsync();

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task FetchUsageAsync_WithoutApiKey_ReturnsFailure()
    {
        var result = await CreateProvider().FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal(ProviderId.Moonshot, result.Provider);
        Assert.Equal("No API key configured", result.ErrorMessage);
    }

    [Theory]
    [InlineData("123.45", 123.45)]
    [InlineData("\"67.89\"", 67.89)]
    public async Task FetchUsageAsync_WithValidBalance_ReturnsCreditsAndSendsExpectedRequest(string balanceJson, double expected)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.Moonshot).Returns(" \"Authorization: Bearer moonshot-test-key\" ");
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.moonshot.ai/v1/users/me/balance", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(ApiKey, request.Headers.Authorization?.Parameter);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "CodexBar");
            return Task.FromResult(JsonResponse("{\"data\":{\"available_balance\":" + balanceJson + "}}"));
        });

        var result = await CreateProvider(settings, handler).FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.Moonshot, result.Provider);
        Assert.Equal((decimal)expected, result.CreditsRemaining);
    }

    [Fact]
    public async Task FetchUsageAsync_WithEnvironmentApiKey_PrefersEnvironmentValue()
    {
        Environment.SetEnvironmentVariable("MOONSHOT_API_KEY", "environment-key");
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.Moonshot).Returns("settings-key");
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal("environment-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse("""{"data":{"available_balance":1}}"""));
        });

        var result = await CreateProvider(settings, handler).FetchUsageAsync();

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"available_balance\":true}}")]
    [InlineData("{\"data\":{\"available_balance\":\"not-a-number\"}}")]
    public async Task FetchUsageAsync_WithMissingOrInvalidBalance_ReturnsParseFailure(string json)
    {
        var result = await CreateProviderWithResponse(HttpStatusCode.OK, json).FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal("Could not parse Moonshot balance.", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_WithMalformedJson_ReturnsParseFailure()
    {
        var result = await CreateProviderWithResponse(HttpStatusCode.OK, "not-json").FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal("Could not parse Moonshot balance.", result.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task FetchUsageAsync_WithHttpFailure_ReturnsActionableError(HttpStatusCode statusCode, string expectedMessage)
    {
        var result = await CreateProviderWithResponse(statusCode, "{}").FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenRequestTimesOut_ReturnsTimeoutFailure()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new TaskCanceledException());

        var result = await CreateProviderWithHandler(handler).FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHttpMessageHandler((_, token) => throw new OperationCanceledException(token));
        var provider = CreateProviderWithHandler(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchUsageAsync(cts.Token));
    }

    [Fact]
    public async Task FetchUsageAsync_WhenUnexpectedExceptionOccurs_ReturnsFailure()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("network exploded"));

        var result = await CreateProviderWithHandler(handler).FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Equal("network exploded", result.ErrorMessage);
    }

    private static MoonshotProvider CreateProvider(
        ISettingsService? settings = null,
        HttpMessageHandler? handler = null)
    {
        settings ??= Substitute.For<ISettingsService>();
        var client = new HttpClient(handler ?? new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{}"))));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return new MoonshotProvider(NullLogger<MoonshotProvider>.Instance, factory, settings);
    }

    private static MoonshotProvider CreateProviderWithResponse(HttpStatusCode statusCode, string json)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(json, statusCode)));
        return CreateProviderWithHandler(handler);
    }

    private static MoonshotProvider CreateProviderWithHandler(HttpMessageHandler handler)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetApiKey(ProviderId.Moonshot).Returns(ApiKey);
        return CreateProvider(settings, handler);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexbar-moonshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }
}
