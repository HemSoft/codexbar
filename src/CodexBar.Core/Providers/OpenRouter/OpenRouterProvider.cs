using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.OpenRouter;

/// <summary>
/// Fetches OpenRouter credit usage via the OpenRouter API.
/// Auth: API key from OPENROUTER_API_KEY env var or settings.
/// Endpoints: /api/v1/credits (balance) + /api/v1/auth/key (rate limits).
/// </summary>
public sealed class OpenRouterProvider : IUsageProvider
{
    private readonly ILogger<OpenRouterProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settings;

    private const string BaseUrl = "https://openrouter.ai/api/v1";

    public OpenRouterProvider(ILogger<OpenRouterProvider> logger, HttpClient httpClient, SettingsService settings)
    {
        _logger = logger;
        _httpClient = httpClient;
        _settings = settings;
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
        var key = ResolveApiKey();
        return Task.FromResult(!string.IsNullOrWhiteSpace(key));
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var key = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return ProviderUsageResult.Failure(ProviderId.OpenRouter, "No API key configured");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Add("X-Title", "CodexBar");

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data))
                return ProviderUsageResult.Failure(ProviderId.OpenRouter, "Unexpected response: missing 'data'");
            if (!data.TryGetProperty("total_credits", out var tcEl) || !data.TryGetProperty("total_usage", out var tuEl))
                return ProviderUsageResult.Failure(ProviderId.OpenRouter, "Unexpected response: missing credit fields");

            var totalCredits = tcEl.GetDouble();
            var totalUsage = tuEl.GetDouble();
            var balance = totalCredits - totalUsage;
            var usedPercent = totalCredits > 0 ? totalUsage / totalCredits : 0;

            _logger.LogDebug("OpenRouter: ${Balance:F2} remaining ({UsedPct:P0} used)", balance, usedPercent);

            return new ProviderUsageResult
            {
                Provider = ProviderId.OpenRouter,
                Success = true,
                CreditsRemaining = (decimal)balance,
                SessionUsage = new UsageSnapshot
                {
                    UsedPercent = usedPercent,
                    UsageLabel = $"${balance:F2} remaining (${totalUsage:F2} / ${totalCredits:F2})"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenRouter fetch failed");
            return ProviderUsageResult.Failure(ProviderId.OpenRouter, ex.Message);
        }
    }

    private string? ResolveApiKey() =>
        _settings.GetApiKey("OpenRouter")
        ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
}
