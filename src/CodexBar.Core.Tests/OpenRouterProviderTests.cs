using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenRouter;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBar.Core.Tests;

public class OpenRouterProviderTests
{
    [Fact]
    public void Metadata_IsCorrect()
    {
        var provider = new OpenRouterProvider(
            NullLogger<OpenRouterProvider>.Instance,
            new DummyHttpClientFactory(),
            new DummySettingsService().Instance);

        Assert.Equal(ProviderId.OpenRouter, provider.Metadata.Id);
        Assert.Equal("OpenRouter", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsCredits);
        Assert.False(provider.Metadata.SupportsSessionUsage);
    }

    private sealed class DummyHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }

    private sealed class DummySettingsService
    {
        // SettingsService is sealed, so we use it directly with its public ctor
        public Core.Configuration.SettingsService Instance { get; } =
            new Core.Configuration.SettingsService(NullLogger<Core.Configuration.SettingsService>.Instance);
    }
}
