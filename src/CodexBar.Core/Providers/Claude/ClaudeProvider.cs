using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Claude;

/// <summary>
/// Fetches Claude usage via the Claude CLI OAuth credentials.
/// Reads OAuth token from ~/.claude/.credentials.json.
/// Endpoint: GET https://api.anthropic.com/api/oauth/usage
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly ILogger<ClaudeProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;

    private static readonly string CredentialsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public ClaudeProvider(ILogger<ClaudeProvider> logger, IHttpClientFactory httpClientFactory, SettingsService settings)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _settings = settings;
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
        if (!_settings.IsProviderEnabled("Claude"))
            return Task.FromResult(false);

        var token = ReadAccessToken();
        return Task.FromResult(token is not null);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var token = ReadAccessToken();
        if (token is null)
            return ProviderUsageResult.Failure(ProviderId.Claude,
                "No Claude CLI credentials found. Run 'claude login' first.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            using var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            UsageSnapshot? session = null;
            UsageSnapshot? weekly = null;

            if (root.TryGetProperty("five_hour", out var fiveHour))
                session = ParseUsageWindow(fiveHour, "Session (5h)");

            if (root.TryGetProperty("seven_day", out var sevenDay))
                weekly = ParseUsageWindow(sevenDay, "Weekly");

            _logger.LogDebug("Claude: session={SessionPct:P0}, weekly={WeeklyPct:P0}",
                session?.UsedPercent ?? 0, weekly?.UsedPercent ?? 0);

            return new ProviderUsageResult
            {
                Provider = ProviderId.Claude,
                Success = true,
                SessionUsage = session,
                WeeklyUsage = weekly
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return ProviderUsageResult.Failure(ProviderId.Claude,
                "Claude token lacks 'user:profile' scope. Re-run 'claude login'.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Claude, ex.Message);
        }
    }

    private static UsageSnapshot ParseUsageWindow(JsonElement window, string label)
    {
        var percentUsed = 0.0;
        string? resetDesc = null;
        DateTimeOffset? resetsAt = null;

        if (window.TryGetProperty("percent_used", out var pct))
            percentUsed = pct.GetDouble() / 100.0;
        else if (window.TryGetProperty("percent_remaining", out var rem))
            percentUsed = 1.0 - (rem.GetDouble() / 100.0);

        if (window.TryGetProperty("resets_at", out var reset))
        {
            if (DateTimeOffset.TryParse(reset.GetString(), out var resetTime))
            {
                resetsAt = resetTime;
                var remaining = resetTime - DateTimeOffset.UtcNow;
                resetDesc = remaining <= TimeSpan.Zero
                    ? "Resets soon"
                    : remaining.TotalHours >= 1
                        ? $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                        : $"Resets in {remaining.Minutes}m";
            }
        }

        var clampedPercentUsed = Math.Clamp(percentUsed, 0, 1);

        return new UsageSnapshot
        {
            UsedPercent = clampedPercentUsed,
            UsageLabel = $"{label}: {clampedPercentUsed:P0} used",
            ResetsAt = resetsAt,
            ResetDescription = resetDesc
        };
    }

    private string? ReadAccessToken()
    {
        if (!File.Exists(CredentialsPath))
        {
            _logger.LogDebug("Claude credentials file not found at {Path}", CredentialsPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("accessToken", out var at))
                return at.GetString();
            if (doc.RootElement.TryGetProperty("access_token", out var at2))
                return at2.GetString();

            _logger.LogDebug("No access token found in Claude credentials file");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Claude credentials from {Path}", CredentialsPath);
            return null;
        }
    }
}
