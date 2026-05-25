// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Providers.Codex;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches ChatGPT Codex subscription limits using the OAuth login maintained
/// by the local Codex client in ~/.codex/auth.json.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    private readonly ILogger<CodexProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly string _authPath;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private ProviderUsageResult? _cachedResult;

    public CodexProvider(
        ILogger<CodexProvider> logger,
        IHttpClientFactory httpClientFactory,
        ISettingsService settings)
        : this(logger, httpClientFactory, settings, GetDefaultAuthPath())
    {
    }

    internal CodexProvider(
        ILogger<CodexProvider> logger,
        IHttpClientFactory httpClientFactory,
        ISettingsService settings,
        string authPath)
    {
        this._logger = logger;
        this._httpClientFactory = httpClientFactory;
        this._settings = settings;
        this._authPath = authPath;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Codex,
        DisplayName = "ChatGPT / Codex",
        Description = "Codex subscription usage from the current ChatGPT sign-in",
        DashboardUrl = "https://chatgpt.com/codex/settings/usage",
        StatusPageUrl = "https://status.openai.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = true,
        SupportsCredits = false,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(this._settings.IsProviderEnabled(ProviderId.Codex));

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var credentials = this.ReadCredentials();
        if (credentials is null)
        {
            return ProviderUsageResult.Failure(
                ProviderId.Codex,
                "No Codex ChatGPT login found. Run 'codex' and sign in with ChatGPT.");
        }

        if (this._cachedResult is not null &&
            DateTimeOffset.UtcNow - this._lastFetch < CacheTtl)
        {
            return this._cachedResult;
        }

        try
        {
            using var request = BuildRequest(credentials);
            using var client = this._httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ProviderUsageResult.Failure(
                    ProviderId.Codex,
                    "Codex ChatGPT login expired. Run 'codex' and sign in again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderUsageResult.Failure(
                    ProviderId.Codex,
                    $"ChatGPT usage returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            var result = ParseUsage(payload);
            if (!result.Success)
            {
                return result;
            }

            this._cachedResult = result;
            this._lastFetch = DateTimeOffset.UtcNow;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Codex usage fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Codex, ex.Message);
        }
    }

    internal static HttpRequestMessage BuildRequest(CodexCredentials credentials)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodexBar");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        return request;
    }

    internal static ProviderUsageResult ParseUsage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("rate_limit", out var rateLimit))
            {
                return ProviderUsageResult.Failure(ProviderId.Codex, "ChatGPT usage response has no rate limits.");
            }

            var windows = new List<WindowData>();
            AddWindow(rateLimit, "primary_window", windows);
            AddWindow(rateLimit, "secondary_window", windows);
            if (windows.Count == 0)
            {
                return ProviderUsageResult.Failure(ProviderId.Codex, "No Codex usage limits available for this account.");
            }

            windows.Sort((left, right) => left.DurationSeconds.CompareTo(right.DurationSeconds));
            var bars = windows.Select(ToBar).ToList();
            var primary = ToSnapshot(windows[0]);
            var secondary = windows.Count > 1 ? ToSnapshot(windows[1]) : null;

            return new ProviderUsageResult
            {
                Provider = ProviderId.Codex,
                Success = true,
                SessionUsage = primary,
                WeeklyUsage = secondary,
                Items =
                [
                    new UsageItem
                    {
                        Key = "codex:chatgpt",
                        DisplayName = "ChatGPT / Codex",
                        PrimaryUsage = primary,
                        SecondaryUsage = secondary,
                        Bars = bars,
                    },
                ],
            };
        }
        catch (JsonException)
        {
            return ProviderUsageResult.Failure(ProviderId.Codex, "Could not parse ChatGPT usage response.");
        }
    }

    internal static string FormatReset(DateTimeOffset resetAt)
    {
        var remaining = resetAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "Resets now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"Resets {(int)remaining.TotalDays}d";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"Resets {(int)remaining.TotalHours}h";
        }

        return $"Resets {Math.Max(1, (int)remaining.TotalMinutes)}m";
    }

    private static void AddWindow(JsonElement rateLimit, string propertyName, List<WindowData> windows)
    {
        if (!rateLimit.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("used_percent", out var usedPercent) ||
            !window.TryGetProperty("reset_at", out var resetAt) ||
            !window.TryGetProperty("limit_window_seconds", out var duration) ||
            !usedPercent.TryGetDouble(out var percent) ||
            !resetAt.TryGetInt64(out var resetEpoch) ||
            !duration.TryGetInt32(out var durationSeconds))
        {
            return;
        }

        windows.Add(new WindowData(
            Math.Clamp(percent / 100.0, 0, 1),
            DateTimeOffset.FromUnixTimeSeconds(resetEpoch),
            durationSeconds));
    }

    private static UsageBar ToBar(WindowData window) => new()
    {
        Label = LabelForDuration(window.DurationSeconds),
        UsedPercent = window.UsedPercent,
        ResetsAt = window.ResetsAt,
        ResetDescription = FormatReset(window.ResetsAt),
    };

    private static UsageSnapshot ToSnapshot(WindowData window) => new()
    {
        UsedPercent = window.UsedPercent,
        UsageLabel = $"{window.UsedPercent:P0} used",
        ResetsAt = window.ResetsAt,
        ResetDescription = FormatReset(window.ResetsAt),
    };

    private static string LabelForDuration(int durationSeconds) => durationSeconds switch
    {
        18000 => "5 hour usage limit",
        604800 => "Weekly usage limit",
        _ => $"{Math.Max(1, durationSeconds / 3600)} hour usage limit",
    };

    private CodexCredentials? ReadCredentials()
    {
        if (!File.Exists(this._authPath))
        {
            return null;
        }

        try
        {
            var payload = File.ReadAllText(this._authPath);
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("tokens", out var tokens))
            {
                return null;
            }

            var accessToken = ReadToken(tokens, "access_token", "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            return new CodexCredentials(accessToken, ReadToken(tokens, "account_id", "accountId"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            this._logger.LogDebug(ex, "Could not read Codex auth file at {Path}", this._authPath);
            return null;
        }
    }

    private static string? ReadToken(JsonElement tokens, string snakeCase, string camelCase)
    {
        if (tokens.TryGetProperty(snakeCase, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return tokens.TryGetProperty(camelCase, out value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string GetDefaultAuthPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var directory = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;
        return Path.Combine(directory, "auth.json");
    }

    internal sealed record CodexCredentials(string AccessToken, string? AccountId);

    private sealed record WindowData(double UsedPercent, DateTimeOffset ResetsAt, int DurationSeconds);
}
