using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Claude;

/// <summary>
/// Fetches Claude usage via the Claude CLI OAuth credentials.
/// Reads credentials from %APPDATA%\claude-cli\ or the OAuth token cache.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly ILogger<ClaudeProvider> _logger;
    private readonly HttpClient _httpClient;

    public ClaudeProvider(ILogger<ClaudeProvider> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Claude,
        DisplayName = "Claude",
        Description = "Anthropic Claude — session + weekly usage tracking",
        DashboardUrl = "https://claude.ai",
        StatusPageUrl = "https://status.anthropic.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = true,
        SupportsCredits = false
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // TODO: Check for Claude CLI credentials on disk
        _logger.LogDebug("Checking Claude CLI credentials availability");
        return Task.FromResult(false);
    }

    public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        // TODO: Implement Claude usage fetch via OAuth API or CLI PTY
        _logger.LogInformation("Claude usage fetch not yet implemented");
        return Task.FromResult(ProviderUsageResult.Failure(ProviderId.Claude, "Not yet implemented"));
    }
}
