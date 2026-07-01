// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Providers.Copilot;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches GitHub Copilot usage for one or more accounts.
/// <para>
/// Uses the Copilot internal API for compatibility and augments enterprise accounts with
/// GitHub REST billing data when the configured organization can be resolved.
/// </para>
/// </summary>
public sealed class CopilotProvider(ILogger<CopilotProvider> logger, IHttpClientFactory httpClientFactory, ISettingsService settings) : IUsageProvider
{
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

    private const decimal OverageCostPerRequest = 0.04m;
    private const string GitHubRestApiBaseUrl = "https://api.github.com";
    private const string GitHubRestApiVersion = "2026-03-10";
    private const string GitHubRestUserAgent = "CodexBar/1.0";
    private const int PromotionalCreditsPerSeat = 7000;
    private const int StandardCreditsPerSeat = 3900;

    [ExcludeFromCodeCoverage]
    private static string GetConfiguredValue(string environmentVariableName, string fallbackValue)
    {
        var configuredValue = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(configuredValue) ? fallbackValue : configuredValue.Trim();
    }

    private readonly ILogger<CopilotProvider> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ISettingsService _settings = settings;

    // Cached account list — refreshed on startup or auth failure
    private readonly SemaphoreSlim _accountsLock = new(1, 1);
    private List<string>? _cachedAccounts;
    private DateTime _emptyDiscoveryCachedUntil;
    private string? _lastDiscoveryError;
    private readonly ConcurrentDictionary<string, string> _tokenCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets an optional delegate that overrides token resolution for a given username.
    /// When set, <see cref="ResolveTokenForUserAsync"/> delegates to this instead of spawning <c>gh</c>.
    /// </summary>
    internal Func<string, CancellationToken, Task<string?>>? TokenResolverOverride { get; set; }

    /// <summary>
    /// Gets or sets an optional delegate that overrides account discovery.
    /// When set, <see cref="GetAccountsToFetchAsync"/> calls this instead of <see cref="DiscoverGhAccountsAsync"/>.
    /// </summary>
    internal Func<CancellationToken, Task<List<string>>>? AccountDiscoveryOverride { get; set; }

    /// <summary>
    /// Gets or sets a factory that creates the <see cref="Process"/> for <c>gh auth status</c>.
    /// When set, <see cref="DiscoverGhAccountsAsync"/> uses it instead of <see cref="StartGhAuthStatusProcess"/>.
    /// </summary>
    internal Func<Process>? GhStatusProcessOverride { get; set; }

    /// <summary>
    /// Gets or sets a factory that creates the <see cref="Process"/> for <c>gh auth token --user</c>.
    /// When set, <see cref="ResolveTokenForUserAsync"/> uses it instead of building the standard PSI.
    /// </summary>
    internal Func<string, Process>? GhTokenProcessOverride { get; set; }

    /// <summary>
    /// Gets or sets an override for the token resolution timeout (default 5 s).
    /// </summary>
    internal TimeSpan? TokenTimeoutOverride { get; set; }

    /// <summary>
    /// Gets or sets an override for the discovery timeout (default 10 s).
    /// </summary>
    internal TimeSpan? DiscoveryTimeoutOverride { get; set; }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Copilot,
        DisplayName = "Copilot",
        Description = "GitHub Copilot — usage limits via GitHub API",
        DashboardUrl = "https://github.com/settings/copilot",
        StatusPageUrl = "https://www.githubstatus.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = false,
        SupportsCredits = false,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var isEnabled = this._settings.IsProviderEnabled(ProviderId.Copilot);
        return Task.FromResult(isEnabled);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        // Note: IsAvailableAsync already gates on IsProviderEnabled — UsageRefreshService
        // skips disabled providers before calling FetchUsageAsync, so no redundant check here.
        var accounts = (await this.GetAccountsToFetchAsync(ct))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0)
        {
            return ProviderUsageResult.Failure(
                ProviderId.Copilot,
                this._lastDiscoveryError
                ?? "No Copilot accounts found. Add copilotAccounts to ~/.codexbar/settings.json or run 'gh auth login'.");
        }

        var tasks = accounts.Select(async username =>
        {
            var result = await this.FetchAccountQuotaAsync(username, ct);
            return (Username: username, Result: result);
        });

        var accountResults = (await Task.WhenAll(tasks)).ToList();
        var billingItems = await this.TryBuildBillingItemsAsync(accountResults, this._settings.Load() ?? new AppSettings(), ct);
        if (billingItems is not null)
        {
            return BuildFetchResult(
                accountResults.Select(pair => pair.Result).ToList(),
                billingItems.Items,
                billingItems.SessionUsage);
        }

        var items = accountResults
            .Select(pair => ToUsageItem(pair.Username, pair.Result))
            .ToList();

        return BuildFetchResult(accountResults.Select(pair => pair.Result).ToList(), items);
    }

    private static ProviderUsageResult BuildFetchResult(List<CopilotAccountResult> accountResults, List<UsageItem> items, UsageSnapshot? aggregateSession = null)
    {
        if (aggregateSession is null)
        {
            var firstSuccess = accountResults.FirstOrDefault(r => r.Success && r.PremiumInteractions is not null);
            if (firstSuccess?.PremiumInteractions is not null)
            {
                aggregateSession = BuildUsageSnapshot(
                    firstSuccess.PremiumInteractions,
                    firstSuccess.QuotaResetDateUtc);
            }
        }

        return new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = accountResults.Any(r => r.Success),
            SessionUsage = aggregateSession,
            Items = items,
            ErrorMessage = accountResults.All(r => !r.Success)
                ? string.Join("; ", accountResults.Select(r => $"{r.Username}: {r.ErrorMessage}"))
                : null,
        };
    }

    private async Task<BillingBuildResult?> TryBuildBillingItemsAsync(
        IReadOnlyList<(string Username, CopilotAccountResult Result)> accountResults,
        AppSettings appSettings,
        CancellationToken ct)
    {
        var configuration = TryCreateBillingConfiguration(accountResults, appSettings);
        if (configuration is null)
        {
            return null;
        }

        var orgToken = await this.ResolveTokenForUserAsync(configuration.BillingUsername, ct);
        if (orgToken is null)
        {
            return null;
        }

        var items = new List<UsageItem>();
        UsageSnapshot? sessionUsage = null;
        var usedBilling = false;

        decimal? orgConsumed = null;
        var orgItem = await this.TryBuildOrgBillingItemAsync(configuration, orgToken, ct);
        if (orgItem is not null)
        {
            items.Add(orgItem.Value.Item);
            sessionUsage = orgItem.Value.Item.PrimaryUsage;
            orgConsumed = orgItem.Value.Consumed;
            usedBilling = true;
        }

        foreach (var (username, result) in accountResults)
        {
            if (!IsBillingEligibleAccount(result, configuration.Organization))
            {
                items.Add(ToUsageItem(username, result));
                continue;
            }

            var userToken = string.Equals(username, configuration.BillingUsername, StringComparison.OrdinalIgnoreCase)
                ? orgToken
                : await this.ResolveTokenForUserAsync(username, ct);

            if (userToken is not null)
            {
                var userItem = await this.TryBuildUserBillingItemAsync(username, result, configuration, orgConsumed, userToken, ct);
                if (userItem is not null)
                {
                    items.Add(userItem);
                    sessionUsage ??= userItem.PrimaryUsage;
                    usedBilling = true;
                    continue;
                }
            }

            items.Add(ToUsageItem(username, result));
        }

        return usedBilling ? new BillingBuildResult(items, sessionUsage) : null;
    }

    private static CopilotBillingConfiguration? TryCreateBillingConfiguration(
        IReadOnlyList<(string Username, CopilotAccountResult Result)> accountResults,
        AppSettings appSettings)
    {
        var defaults = new AppSettings();
        var enterprise = NormalizeCopilotSetting(appSettings.CopilotEnterprise, defaults.CopilotEnterprise);
        var organization = NormalizeCopilotSetting(appSettings.CopilotOrganization, defaults.CopilotOrganization);

        foreach (var (username, result) in accountResults)
        {
            if (IsBillingEligibleAccount(result, organization))
            {
                var now = DateTimeOffset.UtcNow;
                return new CopilotBillingConfiguration(
                    enterprise,
                    organization,
                    NormalizeCopilotPoolTotalOverride(appSettings.CopilotPoolTotal),
                    username,
                    now.Year,
                    now.Month,
                    GetCreditsPerSeat(now.Year, now.Month));
            }
        }

        return null;
    }

    private static bool IsBillingEligibleAccount(CopilotAccountResult result, string organization)
    {
        if (!result.Success || !string.Equals(result.Plan, "enterprise", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return result.Organizations?.Any(org => string.Equals(org, organization, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async Task<(UsageItem Item, decimal Consumed)?> TryBuildOrgBillingItemAsync(CopilotBillingConfiguration configuration, string token, CancellationToken ct)
    {
        var response = await this.FetchOrgBillingAsync(configuration, token, ct);
        if (response is null)
        {
            return null;
        }

        var period = response.TimePeriod is { Year: > 0, Month: > 0 }
            ? response.TimePeriod
            : new BillingTimePeriod { Year = configuration.Year, Month = configuration.Month };
        var periodStart = ComputeBillingPeriodStart(period.Year, period.Month);
        var periodEnd = periodStart.AddMonths(1);
        var consumed = SumGrossQuantity(response.UsageItems);
        var poolTotal = await this.ResolvePoolTotalAsync(configuration, token, ct);
        var projectedMonthEnd = ProjectMonthEndCredits(consumed, periodStart, periodEnd, DateTimeOffset.UtcNow);
        var usageLabel = poolTotal is > 0
            ? $"{consumed:N0} / {poolTotal.Value:N0} AI credits"
            : $"{consumed:N0} AI credits · month end est. {projectedMonthEnd:N0}";
        var primaryUsage = BuildMonthlyUsageSnapshot(consumed, poolTotal, usageLabel, period.Year, period.Month);

        var item = new UsageItem
        {
            Key = $"copilot:org:{configuration.Organization.ToLowerInvariant()}",
            DisplayName = $"Copilot · {configuration.Organization}",
            PrimaryUsage = primaryUsage,
            Bars = BuildOrgUsageBars(consumed, poolTotal, projectedMonthEnd, primaryUsage, periodStart, periodEnd),
            Success = true,
        };

        return (item, consumed);
    }

    private async Task<UsageItem?> TryBuildUserBillingItemAsync(
        string username,
        CopilotAccountResult accountResult,
        CopilotBillingConfiguration configuration,
        decimal? orgConsumed,
        string token,
        CancellationToken ct)
    {
        var response = await this.FetchUserBillingAsync(username, configuration, token, ct);
        if (response is null)
        {
            return null;
        }

        var periodStart = ComputeBillingPeriodStart(configuration.Year, configuration.Month);
        var periodEnd = periodStart.AddMonths(1);
        var billingConsumed = SumGrossQuantity(response.UsageItems);
        var (consumed, total, primary) = BuildUserPrimaryUsage(configuration, accountResult, billingConsumed);
        var projectedMonthEnd = ProjectMonthEndCredits(consumed, periodStart, periodEnd, DateTimeOffset.UtcNow);
        var bars = BuildUserUsageBars(consumed, total, projectedMonthEnd, primary, periodStart, periodEnd, billingConsumed, orgConsumed);

        return new UsageItem
        {
            Key = $"copilot:{username}",
            DisplayName = $"Copilot · {username}",
            PrimaryUsage = primary,
            Bars = bars,
            Success = true,
        };
    }

    private async Task<decimal?> ResolvePoolTotalAsync(CopilotBillingConfiguration configuration, string token, CancellationToken ct)
    {
        if (configuration.PoolTotalOverride is > 0)
        {
            return configuration.PoolTotalOverride;
        }

        var seatCount = await this.FetchSeatCountAsync(configuration, token, ct);
        return seatCount is > 0 ? seatCount.Value * configuration.CreditsPerSeat : null;
    }

    private Task<BillingUsageSummaryResponse?> FetchOrgBillingAsync(CopilotBillingConfiguration configuration, string token, CancellationToken ct)
    {
        var requestUri = $"{GitHubRestApiBaseUrl}/enterprises/{Uri.EscapeDataString(configuration.Enterprise)}/settings/billing/usage/summary?year={configuration.Year}&month={configuration.Month}&product=Copilot&organization={Uri.EscapeDataString(configuration.Organization)}";
        return this.SendGitHubRestRequestAsync<BillingUsageSummaryResponse>(requestUri, token, $"org billing for {configuration.Organization}", ct);
    }

    private async Task<BillingPremiumRequestResponse?> FetchUserBillingAsync(string username, CopilotBillingConfiguration configuration, string token, CancellationToken ct)
    {
        var usageItems = new List<BillingUsageItem>();
        var foundUsage = false;

        foreach (var day in GetBillingDays(configuration.Year, configuration.Month, DateTimeOffset.UtcNow))
        {
            var response = await this.FetchUserBillingDayAsync(username, configuration, day, token, ct);
            if (response is null)
            {
                continue;
            }

            foundUsage = true;
            usageItems.AddRange(response.UsageItems);
        }

        return foundUsage ? new BillingPremiumRequestResponse { UsageItems = usageItems } : null;
    }

    private Task<BillingPremiumRequestResponse?> FetchUserBillingDayAsync(string username, CopilotBillingConfiguration configuration, int day, string token, CancellationToken ct)
    {
        // NOTE: The API does not allow combining user + organization filters (returns 400).
        // Also, month-level user queries return empty; day-level is required.
        var requestUri = $"{GitHubRestApiBaseUrl}/enterprises/{Uri.EscapeDataString(configuration.Enterprise)}/settings/billing/premium_request/usage?year={configuration.Year}&month={configuration.Month}&day={day}&user={Uri.EscapeDataString(username)}";
        return this.SendGitHubRestRequestAsync<BillingPremiumRequestResponse>(requestUri, token, $"user billing for {username}", ct);
    }

    private async Task<int?> FetchSeatCountAsync(CopilotBillingConfiguration configuration, string token, CancellationToken ct)
    {
        var requestUri = $"{GitHubRestApiBaseUrl}/orgs/{Uri.EscapeDataString(configuration.Organization)}/copilot/billing";
        var response = await this.SendGitHubRestRequestAsync<CopilotBillingSeatsResponse>(requestUri, token, $"seat count for {configuration.Organization}", ct);
        return response?.SeatBreakdown?.Total;
    }

    private async Task<T?> SendGitHubRestRequestAsync<T>(string requestUri, string token, string operation, CancellationToken ct)
    {
        try
        {
            using var request = BuildGitHubRestApiRequest(requestUri, token);
            using var httpClient = this._httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogDebug(ex, "Copilot {Operation} request failed", operation);
            return default;
        }
    }

    private static HttpRequestMessage BuildGitHubRestApiRequest(string requestUri, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", GitHubRestApiVersion);
        request.Headers.UserAgent.ParseAdd(GitHubRestUserAgent);
        return request;
    }

    internal static string NormalizeCopilotSetting(string? value, string fallbackValue) =>
        string.IsNullOrWhiteSpace(value) ? fallbackValue : value.Trim();

    internal static decimal? NormalizeCopilotPoolTotalOverride(decimal? poolTotal) =>
        poolTotal is > 0 ? poolTotal : null;

    internal static decimal SumGrossQuantity(IEnumerable<BillingUsageItem>? usageItems) =>
        (usageItems ?? []).Sum(item => item.GrossQuantity);

    internal static decimal ProjectMonthEndCredits(decimal consumed, int year, int month, DateTimeOffset now)
    {
        var periodStart = ComputeBillingPeriodStart(year, month);
        return ProjectMonthEndCredits(consumed, periodStart, periodStart.AddMonths(1), now);
    }

    internal static IReadOnlyList<int> GetBillingDays(int year, int month, DateTimeOffset now)
    {
        var lastDay = now.Year == year && now.Month == month
            ? now.Day
            : DateTime.DaysInMonth(year, month);
        return Enumerable.Range(1, lastDay).ToArray();
    }

    internal static decimal ProjectMonthEndCredits(decimal consumed, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now)
    {
        if (now <= periodStart || now >= periodEnd || consumed <= 0)
        {
            return consumed;
        }

        var elapsed = (decimal)(now - periodStart).TotalSeconds;
        var total = (decimal)(periodEnd - periodStart).TotalSeconds;
        return consumed * total / elapsed;
    }

    internal static IReadOnlyList<UsageBar>? BuildOrgUsageBars(
        decimal consumed,
        decimal? poolTotal,
        decimal projectedMonthEnd,
        UsageSnapshot primaryUsage,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        if (poolTotal is not > 0)
        {
            return null;
        }

        return
        [
            new UsageBar
            {
                Label = $"Current · {consumed:N0} / {poolTotal.Value:N0}",
                UsedPercent = primaryUsage.UsedPercent,
                ResetDescription = primaryUsage.ResetDescription,
                ResetsAt = primaryUsage.ResetsAt,
            },
            new UsageBar
            {
                Label = $"Month end est. · {projectedMonthEnd:N0} / {poolTotal.Value:N0}",
                UsedPercent = Math.Clamp((double)(projectedMonthEnd / poolTotal.Value), 0.0, 1.0),
                ResetDescription = primaryUsage.ResetDescription,
                ResetsAt = primaryUsage.ResetsAt,
                ProjectionCurrent = consumed,
                ProjectionLimit = poolTotal.Value,
                ProjectionPeriodStart = periodStart,
                ProjectionPeriodEnd = periodEnd,
            },
        ];
    }

    private static (decimal Consumed, decimal Total, UsageSnapshot PrimaryUsage) BuildUserPrimaryUsage(
        CopilotBillingConfiguration configuration,
        CopilotAccountResult accountResult,
        decimal billingConsumed)
    {
        var total = accountResult.PremiumInteractions?.Entitlement is > 0
            ? accountResult.PremiumInteractions.Entitlement
            : configuration.CreditsPerSeat;

        return (billingConsumed,
            total,
            BuildMonthlyUsageSnapshot(
                billingConsumed,
                total,
                $"{billingConsumed:N0} / {total:N0} AI credits",
                configuration.Year,
                configuration.Month));
    }

    internal static IReadOnlyList<UsageBar> BuildUserUsageBars(
        decimal consumed,
        decimal total,
        decimal projectedMonthEnd,
        UsageSnapshot primaryUsage,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        decimal billingConsumed,
        decimal? orgConsumed)
    {
        var bars = new List<UsageBar>
        {
            new()
            {
                Label = $"Current · {consumed:N0} / {total:N0}",
                UsedPercent = primaryUsage.UsedPercent,
                ResetDescription = primaryUsage.ResetDescription,
                ResetsAt = primaryUsage.ResetsAt,
            },
            new()
            {
                Label = $"Month end est. · {projectedMonthEnd:N0} / {total:N0}",
                UsedPercent = Math.Clamp((double)(projectedMonthEnd / total), 0.0, 1.0),
                ResetDescription = primaryUsage.ResetDescription,
                ResetsAt = primaryUsage.ResetsAt,
                ProjectionCurrent = consumed,
                ProjectionLimit = total,
                ProjectionPeriodStart = periodStart,
                ProjectionPeriodEnd = periodEnd,
            },
        };

        if (orgConsumed is > 0)
        {
            bars.Add(new UsageBar
            {
                Label = $"Share of org · {billingConsumed:N0} / {orgConsumed.Value:N0}",
                UsedPercent = Math.Clamp((double)(billingConsumed / orgConsumed.Value), 0.0, 1.0),
            });
        }

        return bars;
    }

    internal static UsageSnapshot BuildMonthlyUsageSnapshot(decimal used, decimal? total, string usageLabel, int year, int month)
    {
        var resetAt = ComputeBillingResetAt(year, month);
        var (resetsAt, resetDescription) = ParseReset(resetAt.ToString("O"));
        var usedPercent = total is > 0 ? (double)(used / total.Value) : 0.0;

        return new UsageSnapshot
        {
            UsedPercent = Math.Clamp(usedPercent, 0.0, 1.0),
            UsageLabel = usageLabel,
            ResetsAt = resetsAt ?? resetAt,
            ResetDescription = resetDescription,
            IsUnlimited = false,
        };
    }

    private static DateTimeOffset ComputeBillingPeriodStart(int year, int month) =>
        new(year, month, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset ComputeBillingResetAt(int year, int month) =>
        ComputeBillingPeriodStart(year, month).AddMonths(1);

    internal static int GetCreditsPerSeat(int year, int month) =>
        year == 2026 && month is >= 6 and <= 8 ? PromotionalCreditsPerSeat : StandardCreditsPerSeat;

    private sealed record BillingBuildResult(List<UsageItem> Items, UsageSnapshot? SessionUsage);

    private sealed record CopilotBillingConfiguration(
        string Enterprise,
        string Organization,
        decimal? PoolTotalOverride,
        string BillingUsername,
        int Year,
        int Month,
        int CreditsPerSeat);

    /// <summary>
    /// Returns the list of accounts to fetch: from settings if configured, otherwise auto-discovered.
    /// </summary>
    private async Task<List<string>> GetAccountsToFetchAsync(CancellationToken ct)
    {
        var configured = this._settings.GetCopilotAccounts();
        if (configured.Count > 0)
        {
            return configured.ToList();
        }

        // Auto-discover from gh CLI (async-safe via SemaphoreSlim)
        await this._accountsLock.WaitAsync(ct);
        try
        {
            return await this.DiscoverAccountsUnderLockAsync(ct);
        }
        finally
        {
            this._accountsLock.Release();
        }
    }

    private async Task<List<string>> DiscoverAccountsUnderLockAsync(CancellationToken ct)
    {
        if (!this.ShouldDiscoverAccounts())
        {
            return this._cachedAccounts ?? [];
        }

        var discoveryTask = this.AccountDiscoveryOverride is { } overrideFn
            ? overrideFn(ct)
            : this.DiscoverGhAccountsAsync(ct);

        this._cachedAccounts = await discoveryTask;

        this.UpdateEmptyCacheWindow();

        return this._cachedAccounts ?? [];
    }

    private bool ShouldDiscoverAccounts()
    {
        if (this._cachedAccounts is { Count: > 0 })
        {
            return false;
        }

        return DateTime.UtcNow >= this._emptyDiscoveryCachedUntil;
    }

    private void UpdateEmptyCacheWindow()
    {
        if (this._cachedAccounts is not { Count: 0 })
        {
            return;
        }

        this._emptyDiscoveryCachedUntil = DateTime.UtcNow.AddMinutes(5);
        this._cachedAccounts = null;
    }

    /// <summary>
    /// Discovers all GitHub.com accounts from <c>gh auth status</c>.
    /// Uses async process handling so the calling thread is not blocked and
    /// the provided <paramref name="ct"/> can interrupt a stuck gh process.
    /// </summary>
    private async Task<List<string>> DiscoverGhAccountsAsync(CancellationToken ct)
    {
        this._lastDiscoveryError = null;
        var accounts = new List<string>();

        try
        {
            using var process = this.GhStatusProcessOverride?.Invoke() ?? StartGhAuthStatusProcess();
            var (exitCode, stderr, stdout) = await WaitForGhProcessAsync(process, this.DiscoveryTimeoutOverride ?? TimeSpan.FromSeconds(10), ct);
            if (exitCode is null)
            {
                this._lastDiscoveryError = "GitHub CLI (gh) timed out. Ensure 'gh' is responsive.";
                return accounts;
            }

            if (exitCode != 0)
            {
                this._logger.LogWarning("gh auth status exited with code {ExitCode}: {Stderr}", exitCode, stderr.Trim());
                this._lastDiscoveryError = $"GitHub CLI (gh) auth failed (exit code {exitCode}). Run 'gh auth login'.";
                return accounts;
            }

            accounts = ExtractUsernamesFromGhStatus(string.Join('\n', stdout, stderr));
            this._logger.LogDebug("Discovered {Count} gh CLI accounts: {Accounts}", accounts.Count, string.Join(", ", accounts));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception ex)
        {
            this._logger.LogWarning(ex, "GitHub CLI (gh) not found on PATH");
            this._lastDiscoveryError = "GitHub CLI (gh) not found. Install from https://cli.github.com and run 'gh auth login'.";
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Failed to discover gh CLI accounts");
            this._lastDiscoveryError = $"Failed to discover accounts: {ex.Message}";
        }

        return accounts;
    }

    private static Process StartGhAuthStatusProcess() =>
        new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth status --hostname github.com",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
        };

    private static async Task<(int? exitCode, string stderr, string stdout)> WaitForGhProcessAsync(
        Process process, TimeSpan timeout, CancellationToken ct)
    {
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            var stderr = await stderrTask;
            var stdout = await stdoutTask;
            return (process.ExitCode, stderr, stdout);
        }
        catch (OperationCanceledException)
        {
            BestEffortKillAndDrain(process, stderrTask, stdoutTask);

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return (null, string.Empty, string.Empty);
        }
    }

    internal static void BestEffortKillAndDrain(Process process, Task<string> stderrTask, Task<string> stdoutTask)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            stderrTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        try
        {
            stdoutTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
    }

    internal static List<string> ExtractUsernamesFromGhStatus(string stderr)
    {
        var accounts = new List<string>();
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("Logged in to github.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var username = ExtractUsername(trimmed);
            if (!string.IsNullOrWhiteSpace(username))
            {
                accounts.Add(username.Trim());
            }
        }

        return accounts;
    }

    internal static string? ExtractUsername(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var accountIdx = line.IndexOf("account ", StringComparison.OrdinalIgnoreCase);
        if (accountIdx >= 0)
        {
            var rest = line[(accountIdx + "account ".Length)..];
            var spaceIdx = rest.IndexOf(' ');
            return spaceIdx >= 0 ? rest[..spaceIdx] : rest;
        }

        var asIdx = line.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
        if (asIdx >= 0)
        {
            var rest = line[(asIdx + " as ".Length)..];
            var spaceIdx = rest.IndexOf(' ');
            return spaceIdx >= 0 ? rest[..spaceIdx] : rest;
        }

        return null;
    }

    /// <summary>
    /// Fetches quota for a single account.
    /// </summary>
    private async Task<CopilotAccountResult> FetchAccountQuotaAsync(string username, CancellationToken ct)
    {
        var token = await this.ResolveTokenForUserAsync(username, ct);
        if (token is null)
        {
            return CopilotAccountResult.TokenMissing(username);
        }

        try
        {
            using var request = BuildCopilotApiRequest(token);
            using var httpClient = this._httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseCopilotApiResponse(json, username, this._logger);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await this.InvalidateTokenForUserAsync(username, ct);
            return CopilotAccountResult.Unauthorized(username);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Copilot fetch failed for {User}", username);
            return CopilotAccountResult.Error(username, ex.Message);
        }
    }

    internal static HttpRequestMessage BuildCopilotApiRequest(string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/copilot_internal/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Editor-Version", EditorVersion);
        request.Headers.Add("Editor-Plugin-Version", EditorPluginVersion);
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);
        request.Headers.Add("X-Github-Api-Version", GitHubApiVersion);
        return request;
    }

    internal static CopilotAccountResult ParseCopilotApiResponse(string json, string username, ILogger? logger = null)
    {
        var data = JsonSerializer.Deserialize<CopilotUserResponse>(json);

        if (data is null)
        {
            return CopilotAccountResult.Error(username, "Empty API response");
        }

        LogQuotaDebug(logger, username, data);

        return new CopilotAccountResult
        {
            Username = username,
            Plan = data.CopilotPlan,
            Organizations = data.OrganizationLoginList?.AsReadOnly(),
            PremiumInteractions = data.QuotaSnapshots?.PremiumInteractions,
            Chat = data.QuotaSnapshots?.Chat,
            QuotaResetDateUtc = data.QuotaResetDateUtc,
            Success = true,
        };
    }

    private static void LogQuotaDebug(ILogger? logger, string username, CopilotUserResponse data)
    {
        if (logger is null)
        {
            return;
        }

        var plan = data.CopilotPlan ?? "unknown";
        var premium = data.QuotaSnapshots?.PremiumInteractions;
        logger.LogDebug(
            "Copilot {User} ({Plan}): premium remaining={Remaining}/{Entitlement}",
            username, plan,
            premium?.Remaining ?? 0,
            premium?.Entitlement ?? 0);
    }

    /// <summary>
    /// Resolves a GitHub token for a specific user via <c>gh auth token --user --hostname github.com</c>.
    /// Uses async process handling so the calling thread is not blocked and
    /// the provided <paramref name="ct"/> can interrupt a stuck gh process.
    /// </summary>
    private async Task<string?> ResolveTokenForUserAsync(string username, CancellationToken ct)
    {
        if (this._tokenCache.TryGetValue(username, out var cached))
        {
            return cached;
        }

        if (this.TokenResolverOverride is { } resolver)
        {
            return await this.ResolveTokenViaOverrideAsync(username, ct, resolver);
        }

        return await this.ResolveTokenViaGhCliAsync(username, ct);
    }

    private async Task<string?> ResolveTokenViaOverrideAsync(
        string username,
        CancellationToken ct,
        Func<string, CancellationToken, Task<string?>> resolver)
    {
        var resolved = await resolver(username, ct);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            this._tokenCache[username] = resolved;
        }

        return resolved;
    }

    private async Task<string?> ResolveTokenViaGhCliAsync(string username, CancellationToken ct)
    {
        try
        {
            using var process = this.CreateGhTokenProcess(username);
            var timeout = this.TokenTimeoutOverride ?? TimeSpan.FromSeconds(5);
            var (exitCode, stderr, stdout) = await WaitForGhProcessAsync(process, timeout, ct);

            if (exitCode is null)
            {
                this._logger.LogWarning("gh auth token --user {User} timed out", username);
                return null;
            }

            if (exitCode != 0)
            {
                this.LogNonZeroGhTokenExit(username, exitCode.Value, stderr);
                return null;
            }

            return this.CacheTokenIfValid(username, stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogDebug(ex, "Failed to get token for {User}", username);
            return null;
        }
    }

    private Process CreateGhTokenProcess(string username)
    {
        if (this.GhTokenProcessOverride is not null)
        {
            return this.GhTokenProcessOverride(username);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("token");
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add(username);
        psi.ArgumentList.Add("--hostname");
        psi.ArgumentList.Add("github.com");
        return new Process { StartInfo = psi };
    }

    private void LogNonZeroGhTokenExit(string username, int exitCode, string stderrOutput)
    {
        var sanitizedStderr = string.IsNullOrWhiteSpace(stderrOutput)
            ? "(no stderr)"
            : stderrOutput.Trim().Length > 200
                ? stderrOutput.Trim()[..200] + "…"
                : stderrOutput.Trim();
        this._logger.LogDebug(
            "gh auth token --user {User} exited {Code}: {Stderr}",
            username, exitCode, sanitizedStderr);
    }

    private string? CacheTokenIfValid(string username, string stdout)
    {
        var token = stdout.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        this._tokenCache[username] = token;
        this._logger.LogDebug("Resolved token for {User} ({Length} chars)", username, token.Length);
        return token;
    }

    private async Task InvalidateTokenForUserAsync(string username, CancellationToken ct = default)
    {
        this._tokenCache.TryRemove(username, out _);
        await this._accountsLock.WaitAsync(ct);
        try
        {
            this._cachedAccounts = null;
            this._emptyDiscoveryCachedUntil = default;
        }
        finally
        {
            this._accountsLock.Release();
        }
    }

    private static UsageItem ToUsageItem(string username, CopilotAccountResult result)
    {
        if (!result.Success)
        {
            return new UsageItem
            {
                Key = $"copilot:{username}",
                DisplayName = $"Copilot · {username}",
                Success = false,
                ErrorMessage = result.ErrorMessage,
            };
        }

        var premium = result.PremiumInteractions;
        UsageSnapshot? primaryUsage = null;
        UsageSnapshot? secondaryUsage = null;
        decimal? overageCost = null;

        if (premium is not null)
        {
            primaryUsage = BuildUsageSnapshot(premium, result.QuotaResetDateUtc, "premium");
            overageCost = premium.OveragePermitted
                ? ComputeOverageRequests(premium) * OverageCostPerRequest
                : null;
        }

        if (result.Chat is not null)
        {
            secondaryUsage = BuildUsageSnapshot(result.Chat, result.QuotaResetDateUtc, "chat");
        }

        return new UsageItem
        {
            Key = $"copilot:{username}",
            DisplayName = FormatDisplayName(username, result.Plan),
            PrimaryUsage = primaryUsage,
            SecondaryUsage = secondaryUsage,
            OverageCost = overageCost,
            Success = true,
        };
    }

    private static readonly Dictionary<string, string> PlanLabels = new(StringComparer.Ordinal)
    {
        ["enterprise"] = "Ent",
        ["individual_pro"] = "Pro",
        ["business"] = "Biz",
    };

    internal static string FormatDisplayName(string username, string? plan)
    {
        var planLabel = plan is not null && PlanLabels.TryGetValue(plan, out var label)
            ? label
            : plan?.Replace("_", " ");

        return planLabel is not null ? $"Copilot · {username} ({planLabel})" : $"Copilot · {username}";
    }

    private static readonly Dictionary<string, string> QuotaLabels = new(StringComparer.Ordinal)
    {
        ["premium"] = "Premium interactions",
        ["chat"] = "Chat",
    };

    internal static string FormatQuotaLabel(string quotaLabel) =>
        QuotaLabels.TryGetValue(quotaLabel, out var label) ? label : quotaLabel;

    private static UsageSnapshot BuildUsageSnapshot(CopilotQuotaSnapshot quota, string? resetDateUtc, string quotaLabel = "premium")
    {
        var (usedPercent, usageLabel, isUnlimited) = ComputeUsageMetrics(quota, quotaLabel);
        var (resetsAt, resetDescription) = ParseReset(resetDateUtc);

        return new UsageSnapshot
        {
            UsedPercent = Math.Clamp(usedPercent, 0.0, 1.0),
            UsageLabel = usageLabel,
            ResetsAt = resetsAt,
            ResetDescription = resetDescription,
            IsUnlimited = isUnlimited,
        };
    }

    internal static (double UsedPercent, string UsageLabel, bool IsUnlimited) ComputeUsageMetrics(CopilotQuotaSnapshot quota, string quotaLabel)
    {
        if (quota.Entitlement > 0)
        {
            var used = Math.Max(0, quota.Entitlement - quota.Remaining);
            var usedPercent = (double)used / quota.Entitlement;
            var label = BuildUsageLabel(used, quota.Entitlement, quotaLabel, ComputeOverageRequests(quota), quota.OveragePermitted);
            return (usedPercent, label, false);
        }

        if (quota.Unlimited)
        {
            return (0, "Unlimited", true);
        }

        return (0, "No quota", false);
    }

    private static string BuildUsageLabel(int used, int entitlement, string quotaLabel, int overageRequests, bool overagePermitted)
    {
        if (overageRequests > 0 && overagePermitted)
        {
            return $"{used:N0} - ${overageRequests * OverageCostPerRequest:F2}";
        }

        var baseLabel = $"{used:N0} / {entitlement:N0}";

        // Keep the quota type label for non-premium quotas (e.g., "Chat")
        if (quotaLabel != "premium")
        {
            baseLabel += $" {FormatQuotaLabel(quotaLabel)}";
        }

        if (overageRequests > 0 && !overagePermitted)
        {
            return $"{baseLabel} (over limit)";
        }

        return baseLabel;
    }

    internal static (DateTimeOffset? ResetsAt, string? ResetDescription) ParseReset(string? resetDateUtc)
    {
        if (resetDateUtc is null || !DateTimeOffset.TryParse(resetDateUtc, out var parsed))
        {
            return (null, null);
        }

        var remaining = parsed - DateTimeOffset.UtcNow;
        var description = remaining.TotalDays switch
        {
            < 0 => "Reset overdue",
            < 1 => $"Resets in {remaining.Hours}h {remaining.Minutes}m",
            < 2 => "Resets tomorrow",
            _ => $"Resets in {(int)remaining.TotalDays}d",
        };

        return (parsed, description);
    }

    private static int ComputeOverageRequests(CopilotQuotaSnapshot premium)
    {
        var overageByCount = Math.Max(0, premium.OverageCount);
        var overageByRemaining = Math.Max(0, -premium.Remaining);
        return Math.Max(overageByCount, overageByRemaining);
    }
}
