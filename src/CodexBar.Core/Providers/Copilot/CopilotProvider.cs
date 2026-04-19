using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Copilot;

/// <summary>
/// Fetches GitHub Copilot usage via the Copilot internal API.
/// Auth: resolves GitHub token from ~/.codexbar/settings.json, then GITHUB_TOKEN env var, then gh CLI hosts config.
/// Endpoint: GET https://api.github.com/copilot_internal/user
/// </summary>
public sealed class CopilotProvider : IUsageProvider
{
    // Defaults preserve the currently working behavior, but can be overridden at runtime
    // to avoid code changes and redeploys when upstream clients/API versions change.
    private static readonly string EditorVersion = GetConfiguredValue(
        "CODEXBAR_COPILOT_EDITOR_VERSION",
        "vscode/1.96.2");
    private static readonly string EditorPluginVersion = GetConfiguredValue(
        "CODEXBAR_COPILOT_EDITOR_PLUGIN_VERSION",
        "copilot-chat/0.26.7");
    private static readonly string UserAgentProduct = GetConfiguredValue(
        "CODEXBAR_COPILOT_USER_AGENT_PRODUCT",
        "GitHubCopilotChat/0.26.7");
    private static readonly string GitHubApiVersion = GetConfiguredValue(
        "CODEXBAR_COPILOT_API_VERSION",
        "2025-04-01");

    private static string GetConfiguredValue(string environmentVariableName, string fallbackValue)
    {
        var configuredValue = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(configuredValue) ? fallbackValue : configuredValue.Trim();
    }

    private readonly ILogger<CopilotProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;

    public CopilotProvider(ILogger<CopilotProvider> logger, IHttpClientFactory httpClientFactory, SettingsService settings)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
        SupportsWeeklyUsage = false,
        SupportsCredits = false
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var isEnabled = _settings.IsProviderEnabled(ProviderId.Copilot);
        var hasToken = ResolveGitHubToken() is not null;
        return Task.FromResult(isEnabled && hasToken);
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
            request.Headers.Add("Editor-Version", EditorVersion);
            request.Headers.Add("Editor-Plugin-Version", EditorPluginVersion);
            request.Headers.UserAgent.ParseAdd(UserAgentProduct);
            request.Headers.Add("X-Github-Api-Version", GitHubApiVersion);

            using var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);
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
                // Copilot exposes "Premium" and "Chat" quota types,
                // not session/weekly time windows. Mapping Chat into WeeklyUsage
                // would cause downstream consumers to label it as "Weekly".
                // TODO: Introduce a secondary quota concept for multi-bucket providers.
                WeeklyUsage = null
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

        if (elem.TryGetProperty("percentRemaining", out var pct) && pct.ValueKind == JsonValueKind.Number)
            percentRemaining = Math.Clamp(pct.GetDouble(), 0.0, 100.0);

        var usedPercent = Math.Clamp(1.0 - (percentRemaining / 100.0), 0.0, 1.0);

        return new UsageSnapshot
        {
            UsedPercent = usedPercent,
            UsageLabel = $"{label}: {usedPercent:P0} used"
        };
    }

    /// <summary>
    /// Resolves a GitHub token from: settings → GITHUB_TOKEN env → gh CLI hosts config.
    /// </summary>
    private string? ResolveGitHubToken()
    {
        // 1. Check settings
        var key = _settings.GetApiKey(ProviderId.Copilot);
        if (!string.IsNullOrWhiteSpace(key)) return key;

        // 2. Check env var
        key = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(key)) return key;

        // 3. Read from gh CLI hosts.yml
        return ReadGhCliToken();
    }

    private static IEnumerable<string> GetGhHostsFilePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var windowsPath = Path.GetFullPath(Path.Combine(appData, "GitHub CLI", "hosts.yml"));
            if (seen.Add(windowsPath))
                yield return windowsPath;
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            var xdgPath = Path.GetFullPath(Path.Combine(xdgConfigHome, "gh", "hosts.yml"));
            if (seen.Add(xdgPath))
                yield return xdgPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var unixPath = Path.GetFullPath(Path.Combine(userProfile, ".config", "gh", "hosts.yml"));
            if (seen.Add(unixPath))
                yield return unixPath;

            var macPath = Path.GetFullPath(Path.Combine(userProfile, "Library", "Application Support", "GitHub CLI", "hosts.yml"));
            if (seen.Add(macPath))
                yield return macPath;
        }
    }

    private string? ReadGhCliToken()
    {
        foreach (var hostsFile in GetGhHostsFilePaths())
        {
            if (!File.Exists(hostsFile))
            {
                _logger.LogDebug("gh CLI hosts.yml not found at {Path}", hostsFile);
                continue;
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
                        // Strip optional surrounding quotes (single or double)
                        if (token.Length >= 2 &&
                            ((token[0] == '"' && token[^1] == '"') ||
                             (token[0] == '\'' && token[^1] == '\'')))
                        {
                            token = token[1..^1];
                        }
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            _logger.LogDebug("Found GitHub token from gh CLI at {Path}", hostsFile);
                            return token;
                        }
                    }
                    if (inGithubCom && !line.StartsWith(' ') && !line.StartsWith('\t') && trimmed.Length > 0)
                        inGithubCom = false; // left github.com block
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read gh CLI hosts.yml at {Path}", hostsFile);
            }
        }

        return null;
    }
}
