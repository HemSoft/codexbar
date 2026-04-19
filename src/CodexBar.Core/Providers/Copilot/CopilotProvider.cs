using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Copilot;

/// <summary>
/// Fetches GitHub Copilot usage via the GitHub Device Flow + Copilot internal API.
/// </summary>
public sealed class CopilotProvider : IUsageProvider
{
    private readonly ILogger<CopilotProvider> _logger;
    private readonly HttpClient _httpClient;

    public CopilotProvider(ILogger<CopilotProvider> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Copilot,
        DisplayName = "Copilot",
        Description = "GitHub Copilot — usage limits via GitHub API",
        DashboardUrl = "https://github.com/settings/copilot",
        StatusPageUrl = "https://www.githubstatus.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = true,
        SupportsCredits = false
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // TODO: Check for GitHub token / device flow auth
        _logger.LogDebug("Checking Copilot/GitHub auth availability");
        return Task.FromResult(false);
    }

    public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        // TODO: Implement Copilot usage fetch via GitHub Device Flow + internal API
        _logger.LogInformation("Copilot usage fetch not yet implemented");
        return Task.FromResult(ProviderUsageResult.Failure(ProviderId.Copilot, "Not yet implemented"));
    }
}
