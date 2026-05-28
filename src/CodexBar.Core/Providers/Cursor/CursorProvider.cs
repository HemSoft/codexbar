// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Providers.Cursor;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches Cursor plan usage from Cursor's local auth session and dashboard service.
/// </summary>
public sealed class CursorProvider(
    ILogger<CursorProvider> logger,
    IHttpClientFactory httpClientFactory,
    ISettingsService settings) : IUsageProvider
{
    private readonly ILogger<CursorProvider> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ISettingsService _settings = settings;

    private const string DashboardUsageUrl = "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage";
    private const string CursorAuthFileName = "auth.json";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    internal static Func<string, CancellationToken, Task<CommandResult>> RunCommandAsync { get; set; } =
        RunCursorAgentAsync;

    internal static string? AuthPathOverride { get; set; }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Cursor,
        DisplayName = "Cursor",
        Description = "Cursor plan and on-demand usage",
        DashboardUrl = "https://cursor.com/dashboard",
        StatusPageUrl = "https://status.cursor.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = false,
        SupportsCredits = false,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(this._settings.IsProviderEnabled(ProviderId.Cursor));

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var credentials = ReadCredentials();
            if (string.IsNullOrWhiteSpace(credentials?.AccessToken))
            {
                return ProviderUsageResult.Failure(
                    ProviderId.Cursor,
                    "Cursor credentials were not found. Sign in to Cursor, then refresh CodexBar.");
            }

            var usage = await this.FetchDashboardUsageAsync(credentials.AccessToken, ct);
            var status = await TryFetchStatusAsync(ct);
            var about = await TryFetchAboutAsync(ct);

            return BuildResult(usage, status, about);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Cursor fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Cursor, "Cursor usage could not be read. Sign in to Cursor and try again.");
        }
    }

    internal static ProviderUsageResult BuildResult(CursorCurrentPeriodUsage usage, CursorStatus? status, CursorAbout? about)
    {
        var plan = usage.PlanUsage;
        var email = status?.UserInfo?.Email ?? about?.UserEmail;
        var membership = string.IsNullOrWhiteSpace(about?.SubscriptionTier) ? null : about!.SubscriptionTier!;
        var displayName = FormatDisplayName(email, membership);

        var totalPercent = NormalizePercent(plan?.TotalPercentUsed);
        var reset = TryParseUnixMilliseconds(usage.BillingCycleEnd);
        var usageLabel = BuildUsageLabel(membership, plan);

        var snapshot = new UsageSnapshot
        {
            UsedPercent = totalPercent,
            UsageLabel = usageLabel,
            ResetsAt = reset,
            ResetDescription = FormatResetDate(reset),
            CapturedAt = DateTimeOffset.UtcNow,
        };

        var item = new UsageItem
        {
            Key = "cursor:dashboard",
            DisplayName = displayName,
            PrimaryUsage = snapshot,
            Bars = BuildUsageBars(usage),
            Success = true,
        };

        return new ProviderUsageResult
        {
            Provider = ProviderId.Cursor,
            Success = true,
            SessionUsage = snapshot,
            Items = [item],
        };
    }

    internal static CursorCredentials? ReadCredentials()
    {
        var authPath = ResolveAuthPath();
        if (!File.Exists(authPath))
        {
            return null;
        }

        var json = File.ReadAllText(authPath);
        return JsonSerializer.Deserialize<CursorCredentials>(json);
    }

    internal static string ResolveAuthPath()
    {
        if (AuthPathOverride is not null)
        {
            return AuthPathOverride;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Cursor", CursorAuthFileName);
    }

    internal static CursorStatus? ParseStatus(string json) =>
        JsonSerializer.Deserialize<CursorStatus>(json);

    internal static CursorAbout? ParseAbout(string json) =>
        JsonSerializer.Deserialize<CursorAbout>(json);

    internal static CursorCurrentPeriodUsage ParseCurrentPeriodUsage(string json) =>
        JsonSerializer.Deserialize<CursorCurrentPeriodUsage>(json)
        ?? throw new InvalidOperationException("Cursor returned an empty usage response.");

    private async Task<CursorCurrentPeriodUsage> FetchDashboardUsageAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DashboardUsageUrl)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.UserAgent.ParseAdd("CodexBar/1.0");

        using var httpClient = this._httpClientFactory.CreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ApiTimeout);

        using var response = await httpClient.SendAsync(request, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Cursor usage returned HTTP {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        return ParseCurrentPeriodUsage(json);
    }

    private static async Task<CursorStatus?> TryFetchStatusAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunCommandAsync("status --format json", ct);
            return result.ExitCode == 0 ? ParseStatus(result.Stdout) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static async Task<CursorAbout?> TryFetchAboutAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunCommandAsync("about --format json", ct);
            return result.ExitCode == 0 ? ParseAbout(result.Stdout) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static List<UsageBar> BuildUsageBars(CursorCurrentPeriodUsage usage)
    {
        var bars = new List<UsageBar>();
        var reset = TryParseUnixMilliseconds(usage.BillingCycleEnd);
        var resetDescription = FormatResetDate(reset);

        if (usage.PlanUsage is not null)
        {
            bars.Add(new UsageBar
            {
                Label = "Total",
                UsedPercent = NormalizePercent(usage.PlanUsage.TotalPercentUsed),
                ResetDescription = resetDescription,
                ResetsAt = reset,
            });
            bars.Add(new UsageBar
            {
                Label = "Auto",
                UsedPercent = NormalizePercent(usage.PlanUsage.AutoPercentUsed),
                ResetDescription = resetDescription,
                ResetsAt = reset,
            });
            bars.Add(new UsageBar
            {
                Label = "API",
                UsedPercent = NormalizePercent(usage.PlanUsage.ApiPercentUsed),
                ResetDescription = resetDescription,
                ResetsAt = reset,
            });
        }

        var onDemand = usage.SpendLimitUsage;
        if (onDemand?.IndividualLimit is > 0 && onDemand.IndividualRemaining is not null)
        {
            var used = Math.Max(0, onDemand.IndividualLimit.Value - onDemand.IndividualRemaining.Value);
            bars.Add(new UsageBar
            {
                Label = $"On-demand {FormatCents(used)} / {FormatCents(onDemand.IndividualLimit.Value)}",
                UsedPercent = Clamp01(used / onDemand.IndividualLimit.Value),
            });
        }

        return bars;
    }

    private static string BuildUsageLabel(string? membership, CursorPlanUsage? plan)
    {
        var planName = FormatPlanName(membership) ?? "Cursor";
        if (plan is null)
        {
            return $"{planName} plan";
        }

        var parts = new List<string> { "Included usage" };
        if (plan.AutoPercentUsed is not null)
        {
            parts.Add($"Auto {FormatPercent(plan.AutoPercentUsed.Value)}");
        }

        if (plan.ApiPercentUsed is not null)
        {
            parts.Add($"API {FormatPercent(plan.ApiPercentUsed.Value)}");
        }

        if (parts.Count == 1 && plan.TotalPercentUsed is not null)
        {
            parts.Add($"Total {FormatPercent(plan.TotalPercentUsed.Value)}");
        }

        return string.Join(" · ", parts);
    }

    internal static string FormatDisplayName(string? email, string? membership)
    {
        var providerName = FormatPlanName(membership) is { } planName
            ? $"Cursor ({planName})"
            : "Cursor";

        return string.IsNullOrWhiteSpace(email) ? providerName : $"{providerName} · {email}";
    }

    private static string? FormatPlanName(string? membership) =>
        string.IsNullOrWhiteSpace(membership)
            ? null
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(membership.ToLowerInvariant());

    private static double NormalizePercent(double? percent) =>
        percent is null ? 0 : Clamp01(percent.Value / 100);

    private static double Clamp01(double value) =>
        Math.Min(1, Math.Max(0, value));

    private static DateTimeOffset? TryParseUnixMilliseconds(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string? FormatResetDate(DateTimeOffset? reset)
    {
        if (reset is null)
        {
            return null;
        }

        var localReset = reset.Value.ToLocalTime();
        return $"Resets {localReset:MMM d}";
    }

    private static string FormatCents(double? cents)
    {
        var dollars = (cents ?? 0) / 100;
        return dollars.ToString("C", CultureInfo.GetCultureInfo("en-US"));
    }

    private static string FormatPercent(double percent) =>
        $"{Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero):0}%";

    private static async Task<CommandResult> RunCursorAgentAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveCursorAgentCommand(),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CommandTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            BestEffortKill(process);
            return new CommandResult(-1, string.Empty, "cursor-agent timed out.");
        }
    }

    private static void BestEffortKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch
        {
        }
    }

    internal static string ResolveCursorAgentCommand()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localCommand = Path.Combine(localAppData, "cursor-agent", "cursor-agent.cmd");
        return File.Exists(localCommand) ? localCommand : "cursor-agent";
    }

    internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    internal sealed record CursorCredentials
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; init; }
    }

    internal sealed record CursorStatus
    {
        [JsonPropertyName("isAuthenticated")]
        public bool IsAuthenticated { get; init; }

        [JsonPropertyName("userInfo")]
        public CursorUserInfo? UserInfo { get; init; }
    }

    internal sealed record CursorUserInfo
    {
        [JsonPropertyName("email")]
        public string? Email { get; init; }
    }

    internal sealed record CursorAbout
    {
        [JsonPropertyName("subscriptionTier")]
        public string? SubscriptionTier { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("userEmail")]
        public string? UserEmail { get; init; }
    }

    internal sealed record CursorCurrentPeriodUsage
    {
        [JsonPropertyName("billingCycleEnd")]
        public string? BillingCycleEnd { get; init; }

        [JsonPropertyName("planUsage")]
        public CursorPlanUsage? PlanUsage { get; init; }

        [JsonPropertyName("spendLimitUsage")]
        public CursorSpendLimitUsage? SpendLimitUsage { get; init; }
    }

    internal sealed record CursorPlanUsage
    {
        [JsonPropertyName("totalSpend")]
        public double? TotalSpend { get; init; }

        [JsonPropertyName("includedSpend")]
        public double? IncludedSpend { get; init; }

        [JsonPropertyName("limit")]
        public double? Limit { get; init; }

        [JsonPropertyName("autoPercentUsed")]
        public double? AutoPercentUsed { get; init; }

        [JsonPropertyName("apiPercentUsed")]
        public double? ApiPercentUsed { get; init; }

        [JsonPropertyName("totalPercentUsed")]
        public double? TotalPercentUsed { get; init; }
    }

    internal sealed record CursorSpendLimitUsage
    {
        [JsonPropertyName("individualLimit")]
        public double? IndividualLimit { get; init; }

        [JsonPropertyName("individualRemaining")]
        public double? IndividualRemaining { get; init; }
    }
}
