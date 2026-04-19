using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Copilot;

/// <summary>
/// Fetches GitHub Copilot usage via the Copilot internal API.
/// Auth: reads GitHub token from gh CLI config or ~/.codexbar/settings.json.
/// Endpoint: GET https://api.github.com/copilot_internal/user
/// </summary>
public sealed class CopilotProvider : IUsageProvider
{
    private readonly ILogger<CopilotProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settings;

    public CopilotProvider(ILogger<CopilotProvider> logger, HttpClient httpClient, SettingsService settings)
    {
        _logger = logger;
        _httpClient = httpClient;
        _settings = settings;
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
        if (!_settings.IsProviderEnabled("Copilot"))
            return Task.FromResult(false);

        var token = ResolveGitHubToken();
        return Task.FromResult(token is not null);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var token = ResolveGitHubToken();
        if (token is null)
            return ProviderUsageResult.Failure(ProviderId.Copilot,
                "No GitHub token found. Run 'gh auth login' first.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/copilot_internal/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Editor-Version", "vscode/1.96.2");
            request.Headers.Add("Editor-Plugin-Version", "copilot-chat/0.26.7");
            request.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.26.7");
            request.Headers.Add("X-Github-Api-Version", "2025-04-01");

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            UsageSnapshot? premium = null;
            UsageSnapshot? chat = null;
            string? plan = null;

            if (root.TryGetProperty("copilotPlan", out var planElem))
                plan = planElem.GetString();

            if (root.TryGetProperty("quotaSnapshots", out var snapshots))
            {
                if (snapshots.TryGetProperty("premiumInteractions", out var premiumElem))
                    premium = ParseQuotaSnapshot(premiumElem, "Premium");

                if (snapshots.TryGetProperty("chat", out var chatElem))
                    chat = ParseQuotaSnapshot(chatElem, "Chat");
            }

            _logger.LogDebug("Copilot ({Plan}): premium={PremPct:P0}",
                plan ?? "unknown", premium?.UsedPercent ?? 0);

            return new ProviderUsageResult
            {
                Provider = ProviderId.Copilot,
                Success = true,
                SessionUsage = premium ?? chat,
                WeeklyUsage = premium is not null ? chat : null
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return ProviderUsageResult.Failure(ProviderId.Copilot,
                "GitHub token expired or invalid. Run 'gh auth login'.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Copilot, ex.Message);
        }
    }

    private static UsageSnapshot ParseQuotaSnapshot(JsonElement elem, string label)
    {
        var percentRemaining = 100.0;

        if (elem.TryGetProperty("percentRemaining", out var pct))
            percentRemaining = pct.GetDouble();

        var usedPercent = 1.0 - (percentRemaining / 100.0);

        return new UsageSnapshot
        {
            UsedPercent = Math.Clamp(usedPercent, 0, 1),
            UsageLabel = $"{label}: {percentRemaining:F0}% remaining"
        };
    }

    /// <summary>
    /// Resolves a GitHub token from: settings → GITHUB_TOKEN env → gh CLI hosts config.
    /// </summary>
    private string? ResolveGitHubToken()
    {
        // 1. Check settings
        var key = _settings.GetApiKey("Copilot");
        if (!string.IsNullOrWhiteSpace(key)) return key;

        // 2. Check env var
        key = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(key)) return key;

        // 3. Read from gh CLI hosts.yml
        return ReadGhCliToken();
    }

    private string? ReadGhCliToken()
    {
        var ghConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitHub CLI");
        var hostsFile = Path.Combine(ghConfigDir, "hosts.yml");

        if (!File.Exists(hostsFile))
        {
            _logger.LogDebug("gh CLI hosts.yml not found at {Path}", hostsFile);
            return null;
        }

        try
        {
            // Simple YAML parsing — just looking for oauth_token under github.com
            var lines = File.ReadAllLines(hostsFile);
            var inGithubCom = false;
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("github.com:"))
                {
                    inGithubCom = true;
                    continue;
                }
                if (inGithubCom && trimmed.StartsWith("oauth_token:"))
                {
                    var token = trimmed["oauth_token:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        _logger.LogDebug("Found GitHub token from gh CLI");
                        return token;
                    }
                }
                if (inGithubCom && !line.StartsWith(' ') && !line.StartsWith('\t') && trimmed.Length > 0)
                    inGithubCom = false; // left github.com block
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read gh CLI hosts.yml");
        }

        return null;
    }
}
