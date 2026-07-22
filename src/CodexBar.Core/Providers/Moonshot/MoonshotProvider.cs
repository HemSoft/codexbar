// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Providers.Moonshot;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches the remaining Moonshot (Kimi) API credit balance.
/// </summary>
public sealed class MoonshotProvider(ILogger<MoonshotProvider> logger, IHttpClientFactory httpClientFactory, ISettingsService settings) : IUsageProvider
{
    private const string BalanceUrl = "https://api.moonshot.ai/v1/users/me/balance";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<MoonshotProvider> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ISettingsService _settings = settings;

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Moonshot,
        DisplayName = "Moonshot (Kimi)",
        Description = "Moonshot (Kimi) — remaining API credit balance",
        DashboardUrl = "https://platform.kimi.ai/",
        StatusPageUrl = null,
        SupportsSessionUsage = false,
        SupportsWeeklyUsage = false,
        SupportsCredits = true,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!this._settings.IsProviderEnabled(ProviderId.Moonshot))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(this.ResolveApiKey() is not null);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var apiKey = this.ResolveApiKey();
        if (apiKey is null)
        {
            return ProviderUsageResult.Failure(ProviderId.Moonshot, "No API key configured");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("CodexBar/1.0");

            using var httpClient = this._httpClientFactory.CreateClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ApiTimeout);
            using var response = await httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return CreateHttpFailure(response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("available_balance", out var balanceElement)
                || !TryReadDecimal(balanceElement, out var balance))
            {
                return ProviderUsageResult.Failure(ProviderId.Moonshot, "Could not parse Moonshot balance.");
            }

            this._logger.LogDebug("Moonshot: ${Balance:F2} remaining", balance);
            return new ProviderUsageResult
            {
                Provider = ProviderId.Moonshot,
                Success = true,
                CreditsRemaining = balance,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderUsageResult.Failure(ProviderId.Moonshot, "Moonshot request timed out. Try again later.");
        }
        catch (JsonException ex)
        {
            this._logger.LogWarning(ex, "Moonshot returned invalid JSON");
            return ProviderUsageResult.Failure(ProviderId.Moonshot, "Could not parse Moonshot balance.");
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Moonshot fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Moonshot, ex.Message);
        }
    }

    private static ProviderUsageResult CreateHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderUsageResult.Failure(
            ProviderId.Moonshot,
            "Moonshot rejected this API key. Verify it was created at platform.kimi.ai."),
        HttpStatusCode.TooManyRequests => ProviderUsageResult.Failure(
            ProviderId.Moonshot,
            "Moonshot rate limit reached. Try again later."),
        _ => ProviderUsageResult.Failure(
            ProviderId.Moonshot,
            $"Moonshot balance returned HTTP {(int)statusCode}."),
    };

    private static bool TryReadDecimal(JsonElement element, out decimal value) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetDecimal(out value),
        JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
        _ => Fail(out value),
    };

    private static bool Fail(out decimal value)
    {
        value = default;
        return false;
    }

    private string? ResolveApiKey() =>
        NormalizeApiKey(Environment.GetEnvironmentVariable("MOONSHOT_API_KEY"))
        ?? NormalizeApiKey(this._settings.GetApiKey(ProviderId.Moonshot));

    private static string? NormalizeApiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('"', '\'');
        if (normalized.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Authorization:".Length..].TrimStart();
        }

        if (normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Bearer ".Length..].Trim();
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
