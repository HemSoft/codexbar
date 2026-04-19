using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.OpenRouter;

/// <summary>
/// Fetches OpenRouter credit usage via the OpenRouter API.
/// Requires an API key configured in settings.
/// </summary>
public sealed class OpenRouterProvider : IUsageProvider
{
    private readonly ILogger<OpenRouterProvider> _logger;
    private readonly HttpClient _httpClient;

    public OpenRouterProvider(ILogger<OpenRouterProvider> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.OpenRouter,
        DisplayName = "OpenRouter",
        Description = "OpenRouter — credit-based usage across AI providers",
        DashboardUrl = "https://openrouter.ai/activity",
        StatusPageUrl = null,
        SupportsSessionUsage = false,
        SupportsWeeklyUsage = false,
        SupportsCredits = true
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // TODO: Check for API key in settings/environment
        _logger.LogDebug("Checking OpenRouter API key availability");
        return Task.FromResult(false);
    }

    public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        // TODO: Implement OpenRouter credits fetch via API
        // Endpoint: GET https://openrouter.ai/api/v1/auth/key (with Bearer token)
        _logger.LogInformation("OpenRouter usage fetch not yet implemented");
        return Task.FromResult(ProviderUsageResult.Failure(ProviderId.OpenRouter, "Not yet implemented"));
    }
}
