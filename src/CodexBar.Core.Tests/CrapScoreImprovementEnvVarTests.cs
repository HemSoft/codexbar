// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenRouter;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests from CrapScoreImprovementTests that mutate the OPENROUTER_API_KEY environment variable.
/// Placed in the "EnvironmentVariableTests" collection to serialize with other tests that
/// touch process-wide environment variables.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class CrapScoreImprovementEnvVarTests
{
    [Fact]
    public async Task OpenRouter_FetchUsageAsync_NoApiKey_ReturnsError()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenRouter).Returns(true);
        settings.GetApiKey(ProviderId.OpenRouter).Returns((string?)null);

        var factory = Substitute.For<IHttpClientFactory>();

        // Clear environment variable for this test
        var originalEnv = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
        try
        {
            var provider = new OpenRouterProvider(
                NullLogger<OpenRouterProvider>.Instance,
                factory,
                settings);

            var result = await provider.FetchUsageAsync();

            Assert.False(result.Success);
            Assert.Contains("No API key", result.ErrorMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", originalEnv);
        }
    }
}
