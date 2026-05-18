// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenRouter;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Coverage test targeting the previously uncovered "No API key" branch
/// in OpenRouterProvider.FetchUsageAsync (lines 53-54).
/// </summary>
public class OpenRouterProviderCoverageTests : IDisposable
{
    private readonly string? _savedEnvVar;

    public OpenRouterProviderCoverageTests()
    {
        // Save and clear env var so ResolveApiKey falls through to settings
        this._savedEnvVar = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", this._savedEnvVar);
    }

    [Fact]
    public async Task FetchUsageAsync_NoApiKey_ReturnsFailure()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
        settings.GetApiKey(ProviderId.OpenRouter).Returns((string?)null);

        var factory = Substitute.For<IHttpClientFactory>();

        var provider = new OpenRouterProvider(
            NullLogger<OpenRouterProvider>.Instance,
            factory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No API key", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_EmptyApiKey_ReturnsFailure()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
        settings.GetApiKey(ProviderId.OpenRouter).Returns(string.Empty);

        var factory = Substitute.For<IHttpClientFactory>();

        var provider = new OpenRouterProvider(
            NullLogger<OpenRouterProvider>.Instance,
            factory,
            settings);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No API key", result.ErrorMessage);
    }
}
