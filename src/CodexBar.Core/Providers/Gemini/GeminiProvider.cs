using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Gemini;

/// <summary>
/// Fetches Gemini usage via gcloud OAuth credentials.
/// Uses the Gemini CLI's stored OAuth tokens.
/// </summary>
public sealed class GeminiProvider : IUsageProvider
{
    private readonly ILogger<GeminiProvider> _logger;
    private readonly HttpClient _httpClient;

    public GeminiProvider(ILogger<GeminiProvider> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Gemini,
        DisplayName = "Gemini",
        Description = "Google Gemini — OAuth-backed quota tracking",
        DashboardUrl = "https://aistudio.google.com",
        StatusPageUrl = "https://status.cloud.google.com",
        SupportsSessionUsage = false,
        SupportsWeeklyUsage = false,
        SupportsCredits = true
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // TODO: Check for gcloud credentials
        _logger.LogDebug("Checking Gemini/gcloud credentials availability");
        return Task.FromResult(false);
    }

    public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        // TODO: Implement Gemini quota fetch via OAuth API
        _logger.LogInformation("Gemini usage fetch not yet implemented");
        return Task.FromResult(ProviderUsageResult.Failure(ProviderId.Gemini, "Not yet implemented"));
    }
}
