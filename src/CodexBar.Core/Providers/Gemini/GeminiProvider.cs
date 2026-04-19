using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Gemini;

/// <summary>
/// Fetches Gemini usage via the Gemini CLI OAuth credentials.
/// Reads tokens from ~/.gemini/oauth_creds.json.
/// Endpoint: POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota
/// </summary>
public sealed class GeminiProvider : IUsageProvider
{
    private readonly ILogger<GeminiProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settings;

    private static readonly string OAuthCredsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");

    public GeminiProvider(ILogger<GeminiProvider> logger, HttpClient httpClient, SettingsService settings)
    {
        _logger = logger;
        _httpClient = httpClient;
        _settings = settings;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Gemini,
        DisplayName = "Gemini",
        Description = "Google Gemini — OAuth-backed quota tracking",
        DashboardUrl = "https://aistudio.google.com",
        StatusPageUrl = "https://status.cloud.google.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = false,
        SupportsCredits = false
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_settings.IsProviderEnabled("Gemini"))
            return Task.FromResult(false);

        return Task.FromResult(File.Exists(OAuthCredsPath));
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var accessToken = ReadAccessToken();
        if (accessToken is null)
            return ProviderUsageResult.Failure(ProviderId.Gemini,
                "No Gemini CLI credentials found. Run 'gemini login' first.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Parse quota buckets — find the model with the lowest remaining fraction
            UsageSnapshot? proSnapshot = null;
            UsageSnapshot? flashSnapshot = null;

            if (root.TryGetProperty("quotas", out var quotas) && quotas.ValueKind == JsonValueKind.Array)
            {
                double lowestProRemaining = 1.0;
                double lowestFlashRemaining = 1.0;
                DateTimeOffset? proReset = null;
                DateTimeOffset? flashReset = null;

                foreach (var quota in quotas.EnumerateArray())
                {
                    var modelId = quota.TryGetProperty("modelId", out var mid) ? mid.GetString() ?? "" : "";
                    var remaining = quota.TryGetProperty("remainingFraction", out var rf) ? rf.GetDouble() : 1.0;
                    DateTimeOffset? resetTime = null;

                    if (quota.TryGetProperty("resetTime", out var rt) &&
                        DateTimeOffset.TryParse(rt.GetString(), out var parsed))
                        resetTime = parsed;

                    var isFlash = modelId.Contains("flash", StringComparison.OrdinalIgnoreCase);

                    if (isFlash)
                    {
                        if (remaining < lowestFlashRemaining)
                        {
                            lowestFlashRemaining = remaining;
                            flashReset = resetTime;
                        }
                    }
                    else
                    {
                        if (remaining < lowestProRemaining)
                        {
                            lowestProRemaining = remaining;
                            proReset = resetTime;
                        }
                    }
                }

                proSnapshot = MakeSnapshot("Pro", 1.0 - lowestProRemaining, proReset);
                flashSnapshot = MakeSnapshot("Flash", 1.0 - lowestFlashRemaining, flashReset);
            }

            _logger.LogDebug("Gemini: pro={ProPct:P0}, flash={FlashPct:P0}",
                proSnapshot?.UsedPercent ?? 0, flashSnapshot?.UsedPercent ?? 0);

            return new ProviderUsageResult
            {
                Provider = ProviderId.Gemini,
                Success = true,
                SessionUsage = proSnapshot,
                WeeklyUsage = flashSnapshot
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return ProviderUsageResult.Failure(ProviderId.Gemini,
                "Gemini OAuth token expired. Run 'gemini login' to refresh.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Gemini, ex.Message);
        }
    }

    private static UsageSnapshot MakeSnapshot(string label, double usedPercent, DateTimeOffset? resetsAt)
    {
        string? resetDesc = null;
        if (resetsAt is not null)
        {
            var remaining = resetsAt.Value - DateTimeOffset.UtcNow;
            resetDesc = remaining.TotalHours >= 1
                ? $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"Resets in {remaining.Minutes}m";
        }

        return new UsageSnapshot
        {
            UsedPercent = Math.Clamp(usedPercent, 0, 1),
            UsageLabel = $"{label}: {usedPercent:P0} used",
            ResetsAt = resetsAt,
            ResetDescription = resetDesc
        };
    }

    private string? ReadAccessToken()
    {
        if (!File.Exists(OAuthCredsPath))
        {
            _logger.LogDebug("Gemini OAuth credentials not found at {Path}", OAuthCredsPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(OAuthCredsPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("access_token", out var token))
                return null;

            // Check expiry
            if (doc.RootElement.TryGetProperty("expiry_date", out var expiry))
            {
                if (expiry.TryGetInt64(out var epochMs))
                {
                    var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
                    if (expiresAt < DateTimeOffset.UtcNow)
                    {
                        _logger.LogDebug("Gemini access token expired at {Expiry}", expiresAt);
                        return null;
                    }
                }
            }

            return token.GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Gemini OAuth credentials");
            return null;
        }
    }
}
