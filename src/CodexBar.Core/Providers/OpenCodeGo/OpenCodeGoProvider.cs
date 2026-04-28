using System.Net;
using System.Text.RegularExpressions;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.OpenCodeGo;

/// <summary>
/// Fetches OpenCode Go usage by scraping the SolidJS SSR dashboard.
/// Auth: workspace ID + auth cookie from env vars or settings.
/// URL: https://opencode.ai/workspace/{workspaceId}/go
/// Metrics: 5-hour rolling, weekly, monthly (dollar-value limits).
/// </summary>
public sealed partial class OpenCodeGoProvider : IUsageProvider
{
    private readonly ILogger<OpenCodeGoProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;

    private const string DashboardPrefix = "https://opencode.ai/workspace/";
    private const string DashboardSuffix = "/go";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private ParsedUsage? _cached;

    public OpenCodeGoProvider(
        ILogger<OpenCodeGoProvider> logger,
        IHttpClientFactory httpClientFactory,
        SettingsService settings)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.OpenCodeGo,
        DisplayName = "OpenCode Go",
        Description = "OpenCode Go — bundled AI coding model subscription",
        DashboardUrl = "https://opencode.ai/go",
        StatusPageUrl = null,
        SupportsSessionUsage = false,
        SupportsWeeklyUsage = false,
        SupportsCredits = false
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(_settings.IsProviderEnabled(ProviderId.OpenCodeGo));

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var (workspaceId, authCookie) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(authCookie))
        {
            return ProviderUsageResult.Failure(ProviderId.OpenCodeGo,
                "Configure OPENCODE_GO_WORKSPACE_ID and OPENCODE_GO_AUTH_COOKIE " +
                "(env vars or openCodeGoWorkspaceId + providers.OpenCodeGo.apiKey in settings.json).");
        }

        // Serve cached result if still fresh
        if (_cached is not null && DateTimeOffset.UtcNow - _lastFetch < CacheTtl)
        {
            _logger.LogDebug("OpenCodeGo: cached result ({Age:F0}s old)", (DateTimeOffset.UtcNow - _lastFetch).TotalSeconds);
            return BuildResult(_cached);
        }

        try
        {
            var url = $"{DashboardPrefix}{Uri.EscapeDataString(workspaceId)}{DashboardSuffix}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Gecko/20100101 Firefox/148.0");
            request.Headers.Add("Accept", "text/html");
            request.Headers.Add("Cookie", $"auth={authCookie}");

            using var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ProviderUsageResult.Failure(ProviderId.OpenCodeGo,
                    "Auth cookie rejected. Refresh OPENCODE_GO_AUTH_COOKIE.");

            if (!response.IsSuccessStatusCode)
                return ProviderUsageResult.Failure(ProviderId.OpenCodeGo,
                    $"Dashboard returned HTTP {(int)response.StatusCode}.");

            var html = await response.Content.ReadAsStringAsync(ct);
            var usage = ParseDashboardHtml(html);

            if (usage is null)
                return ProviderUsageResult.Failure(ProviderId.OpenCodeGo,
                    "Could not parse usage data from OpenCode Go dashboard. " +
                    "The dashboard markup may have changed.");

            _cached = usage;
            _lastFetch = DateTimeOffset.UtcNow;
            _logger.LogDebug(
                "OpenCodeGo: rolling={R:P0} weekly={W:P0} monthly={M:P0}",
                usage.Rolling?.UsedPercent, usage.Weekly?.UsedPercent, usage.Monthly?.UsedPercent);

            return BuildResult(usage);
        }
        catch (TaskCanceledException)
        {
            return ProviderUsageResult.Failure(ProviderId.OpenCodeGo, "Dashboard request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenCodeGo fetch failed");
            return ProviderUsageResult.Failure(ProviderId.OpenCodeGo, ex.Message);
        }
    }

    private static ProviderUsageResult BuildResult(ParsedUsage usage)
    {
        var bars = BuildBars(usage);
        var primary = usage.Rolling ?? usage.Monthly;

        var item = new UsageItem
        {
            Key = "opencode-go:go",
            DisplayName = "OpenCode Go",
            PrimaryUsage = primary is null ? null : new UsageSnapshot
            {
                UsedPercent = primary.UsedPercent,
                UsageLabel = primary.UsageLabel,
                ResetsAt = primary.ResetsAt,
                ResetDescription = primary.ResetDescription
            },
            Bars = bars.Count > 0 ? bars : null,
            Success = true
        };

        return new ProviderUsageResult
        {
            Provider = ProviderId.OpenCodeGo,
            Success = true,
            Items = [item]
        };
    }

    private static IReadOnlyList<UsageBar> BuildBars(ParsedUsage usage)
    {
        var bars = new List<UsageBar>(3);

        if (usage.Rolling is { } r)
            bars.Add(new UsageBar
            {
                Label = "5-hour limit",
                UsedPercent = r.UsedPercent,
                ResetDescription = r.ResetDescription,
                ResetsAt = r.ResetsAt
            });

        if (usage.Weekly is { } w)
            bars.Add(new UsageBar
            {
                Label = "Weekly limit",
                UsedPercent = w.UsedPercent,
                ResetDescription = w.ResetDescription,
                ResetsAt = w.ResetsAt
            });

        if (usage.Monthly is { } m)
            bars.Add(new UsageBar
            {
                Label = "Monthly limit",
                UsedPercent = m.UsedPercent,
                ResetDescription = m.ResetDescription,
                ResetsAt = m.ResetsAt
            });

        return bars;
    }

    // SolidJS SSR hydration — field order may vary, so two orderings per window.
    [GeneratedRegex(@"rollingUsage:\$R\[\d+\]=\{[^}]*usagePercent:(\d+)[^}]*resetInSec:(\d+)[^}]*\}")]
    private static partial Regex RollingPctFirst();
    [GeneratedRegex(@"rollingUsage:\$R\[\d+\]=\{[^}]*resetInSec:(\d+)[^}]*usagePercent:(\d+)[^}]*\}")]
    private static partial Regex RollingResetFirst();

    [GeneratedRegex(@"weeklyUsage:\$R\[\d+\]=\{[^}]*usagePercent:(\d+)[^}]*resetInSec:(\d+)[^}]*\}")]
    private static partial Regex WeeklyPctFirst();
    [GeneratedRegex(@"weeklyUsage:\$R\[\d+\]=\{[^}]*resetInSec:(\d+)[^}]*usagePercent:(\d+)[^}]*\}")]
    private static partial Regex WeeklyResetFirst();

    [GeneratedRegex(@"monthlyUsage:\$R\[\d+\]=\{[^}]*usagePercent:(\d+)[^}]*resetInSec:(\d+)[^}]*\}")]
    private static partial Regex MonthlyPctFirst();
    [GeneratedRegex(@"monthlyUsage:\$R\[\d+\]=\{[^}]*resetInSec:(\d+)[^}]*usagePercent:(\d+)[^}]*\}")]
    private static partial Regex MonthlyResetFirst();

    private static ParsedUsage? ParseDashboardHtml(string html)
    {
        var rolling = TryParseWindow(html, RollingPctFirst(), RollingResetFirst(), pctGroup: 1, secGroup: 2);
        var weekly  = TryParseWindow(html, WeeklyPctFirst(),  WeeklyResetFirst(),  pctGroup: 1, secGroup: 2);
        var monthly = TryParseWindow(html, MonthlyPctFirst(), MonthlyResetFirst(), pctGroup: 1, secGroup: 2);

        return (rolling is null && weekly is null && monthly is null) ? null
            : new ParsedUsage { Rolling = rolling, Weekly = weekly, Monthly = monthly };
    }

    private static WindowData? TryParseWindow(
        string html, Regex pctFirst, Regex resetFirst, int pctGroup, int secGroup)
    {
        var m = pctFirst.Match(html);
        if (m.Success
            && int.TryParse(m.Groups[pctGroup].Value, out var pct1)
            && int.TryParse(m.Groups[secGroup].Value, out var sec1))
            return MakeWindow(pct1, sec1);

        // Try alternate ordering (resetInSec comes before usagePercent)
        m = resetFirst.Match(html);
        if (m.Success
            && int.TryParse(m.Groups[2].Value, out var pct2)
            && int.TryParse(m.Groups[1].Value, out var sec2))
            return MakeWindow(pct2, sec2);

        return null;
    }

    private static WindowData MakeWindow(int usagePercent, int resetInSec)
    {
        var usedPct = Math.Clamp(usagePercent, 0, 100) / 100.0;
        var resetsAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, resetInSec));
        return new WindowData
        {
            UsedPercent = usedPct,
            ResetsAt = resetsAt,
            ResetDescription = FormatReset(resetsAt),
            UsageLabel = $"{usagePercent}% used"
        };
    }

    private static string FormatReset(DateTimeOffset resetsAt)
    {
        var remaining = resetsAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "Resets now";
        if (remaining.TotalDays >= 1) return $"Resets {(int)remaining.TotalDays}d";
        if (remaining.TotalHours >= 1) return $"Resets {(int)remaining.TotalHours}h";
        return $"Resets {(int)remaining.TotalMinutes}m";
    }

    private (string? workspaceId, string? authCookie) ResolveCredentials()
    {
        var workspaceId =
            Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID")
            ?? _settings.GetOpenCodeGoWorkspaceId();

        var authCookie =
            Environment.GetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE")
            ?? _settings.GetApiKey(ProviderId.OpenCodeGo);

        return (workspaceId, authCookie);
    }

    private sealed class ParsedUsage
    {
        public WindowData? Rolling { get; init; }
        public WindowData? Weekly { get; init; }
        public WindowData? Monthly { get; init; }
    }

    private sealed class WindowData
    {
        public double UsedPercent { get; init; }
        public DateTimeOffset ResetsAt { get; init; }
        public string? ResetDescription { get; init; }
        public string? UsageLabel { get; init; }
    }
}
