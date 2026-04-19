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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;

    private static readonly string OAuthCredsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");

    public GeminiProvider(ILogger<GeminiProvider> logger, IHttpClientFactory httpClientFactory, SettingsService settings)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
        if (!_settings.IsProviderEnabled(ProviderId.Gemini))
            return Task.FromResult(false);

        return Task.FromResult(ReadAccessToken().Status == AccessTokenStatus.Success);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var tokenResult = ReadAccessToken();
        if (tokenResult.Status != AccessTokenStatus.Success)
        {
            var message = tokenResult.Status == AccessTokenStatus.Expired
                ? "Gemini OAuth token expired. Run 'gemini login' to refresh."
                : "No Gemini CLI credentials found. Run 'gemini login' first.";
            return ProviderUsageResult.Failure(ProviderId.Gemini, message);
        }

        var accessToken = tokenResult.Token!;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);
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
                bool foundPro = false;
                bool foundFlash = false;

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
                        foundFlash = true;
                        if (remaining < lowestFlashRemaining)
                        {
                            lowestFlashRemaining = remaining;
                            flashReset = resetTime;
                        }
                    }
                    else
                    {
                        foundPro = true;
                        if (remaining < lowestProRemaining)
                        {
                            lowestProRemaining = remaining;
                            proReset = resetTime;
                        }
                    }
                }

                if (foundPro)
                    proSnapshot = MakeSnapshot("Pro", 1.0 - lowestProRemaining, proReset);
                if (foundFlash)
                    flashSnapshot = MakeSnapshot("Flash", 1.0 - lowestFlashRemaining, flashReset);
            }

            _logger.LogDebug("Gemini: pro={ProPct:P0}, flash={FlashPct:P0}",
                proSnapshot?.UsedPercent ?? 0, flashSnapshot?.UsedPercent ?? 0);

            return new ProviderUsageResult
            {
                Provider = ProviderId.Gemini,
                Success = true,
                SessionUsage = proSnapshot,
                // Gemini exposes separate "Pro" and "Flash" quota buckets,
                // not session/weekly time windows. Mapping Flash into WeeklyUsage
                // would cause downstream consumers to label it as "Weekly".
                // TODO: Introduce a secondary quota concept for multi-bucket providers.
                WeeklyUsage = null
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
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            resetDesc = remaining.TotalHours >= 1
                ? $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"Resets in {remaining.Minutes}m";
        }

        var clampedUsedPercent = Math.Clamp(usedPercent, 0, 1);

        return new UsageSnapshot
        {
            UsedPercent = clampedUsedPercent,
            UsageLabel = $"{label}: {clampedUsedPercent:P0} used",
            ResetsAt = resetsAt,
            ResetDescription = resetDesc
        };
    }

    private enum AccessTokenStatus { Success, Missing, Expired }

    private sealed record AccessTokenResult(AccessTokenStatus Status, string? Token);

    private AccessTokenResult ReadAccessToken()
    {
        if (!File.Exists(OAuthCredsPath))
        {
            _logger.LogDebug("Gemini OAuth credentials not found at {Path}", OAuthCredsPath);
            return new AccessTokenResult(AccessTokenStatus.Missing, null);
        }

        try
        {
            var json = File.ReadAllText(OAuthCredsPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("access_token", out var token))
                return new AccessTokenResult(AccessTokenStatus.Missing, null);

            // Check expiry
            if (doc.RootElement.TryGetProperty("expiry_date", out var expiry))
            {
                if (expiry.TryGetInt64(out var epochMs))
                {
                    var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
                    if (expiresAt < DateTimeOffset.UtcNow)
                    {
                        _logger.LogDebug("Gemini access token expired at {Expiry}", expiresAt);
                        return new AccessTokenResult(AccessTokenStatus.Expired, null);
                    }
                }
            }

            var tokenString = token.GetString();
            if (string.IsNullOrEmpty(tokenString))
                return new AccessTokenResult(AccessTokenStatus.Missing, null);

            return new AccessTokenResult(AccessTokenStatus.Success, tokenString);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Gemini OAuth credentials");
            return new AccessTokenResult(AccessTokenStatus.Missing, null);
        }
    }
}
