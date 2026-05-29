// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Providers.Claude;

using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches Claude Code subscription usage from the OAuth usage endpoint, with
/// rate-limit response headers as a fallback for older token behavior.
/// </summary>
public sealed partial class ClaudeProvider(ILogger<ClaudeProvider> logger, IHttpClientFactory httpClientFactory, ISettingsService settings) : IUsageProvider
{
    private readonly ILogger<ClaudeProvider> logger = logger;
    private readonly ISettingsService settings = settings;
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;

    private static readonly string ClaudeDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static readonly string _defaultCredentialsPath = Path.Combine(ClaudeDir, ".credentials.json");
    private static readonly string _defaultStatsCachePath = Path.Combine(ClaudeDir, "stats-cache.json");

    private static readonly string _defaultClaudeJsonPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>Gets or sets an override for the credentials file path (test hook).</summary>
    internal static string? CredentialsPathOverride { get; set; }

    /// <summary>Gets or sets an override for the stats cache file path (test hook).</summary>
    internal static string? StatsCachePathOverride { get; set; }

    /// <summary>Gets or sets an override for the claude.json file path (test hook).</summary>
    internal static string? ClaudeJsonPathOverride { get; set; }

    /// <summary>Gets or sets an override for the environment access token (test hook).</summary>
    internal static string? EnvironmentAccessTokenOverride { get; set; }

    private static string CredentialsPath => CredentialsPathOverride ?? _defaultCredentialsPath;

    private static string StatsCachePath => StatsCachePathOverride ?? _defaultStatsCachePath;

    private static string ClaudeJsonPath => ClaudeJsonPathOverride ?? _defaultClaudeJsonPath;

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private const string UsageApiUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenRefreshUrl = "https://platform.claude.com/v1/oauth/token";

    // Minimal request body: haiku model, 1 token, trivial prompt — just enough to trigger rate-limit headers
    private static readonly string ProbeRequestBody = JsonSerializer.Serialize(new
    {
        model = "claude-haiku-4-5",
        max_tokens = 1,
        messages = new[] { new { role = "user", content = "x" } },
    });

    // Anthropic API pricing per million tokens (used to calculate equivalent cost)
    private static readonly Dictionary<string, (double InputPerMTok, double OutputPerMTok, double CacheWritePerMTok, double CacheReadPerMTok)> ModelPricing =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-4-7"] = (5.0, 25.0, 6.25, 0.50),
            ["claude-opus-4-6"] = (5.0, 25.0, 6.25, 0.50),
            ["claude-opus-4-5"] = (5.0, 25.0, 6.25, 0.50),
            ["claude-opus"] = (5.0, 25.0, 6.25, 0.50),
            ["opus"] = (5.0, 25.0, 6.25, 0.50),
            ["claude-sonnet-4-6"] = (3.0, 15.0, 3.75, 0.30),
            ["claude-sonnet-4-5"] = (3.0, 15.0, 3.75, 0.30),
            ["claude-sonnet-4"] = (3.0, 15.0, 3.75, 0.30),
            ["claude-sonnet"] = (3.0, 15.0, 3.75, 0.30),
            ["claude-haiku-4-5"] = (1.0, 5.0, 1.25, 0.10),
            ["claude-haiku-3-5"] = (0.80, 4.0, 1.0, 0.08),
            ["claude-haiku"] = (1.0, 5.0, 1.25, 0.10),
            ["haiku"] = (1.0, 5.0, 1.25, 0.10),
        };

    // Cache the last known rate-limit data so we don't probe the API on every refresh.
    // TTL is 30 min to avoid burning subscription quota with frequent probes (~48 req/day vs ~288).
    private UnifiedRateLimits? cachedLimits;
    private bool cachedLimitsAreAuthoritative;
    private long limitsCachedAtTicks; // stored as ticks for atomic reads via Volatile
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim cacheLock = new(1, 1);

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Claude,
        DisplayName = "Claude",
        Description = "Claude Code — subscription usage via API rate-limit headers",
        DashboardUrl = "https://claude.ai/settings/usage",
        StatusPageUrl = "https://status.anthropic.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = true,
        SupportsCredits = false,
    };

    /// <summary>
    /// Normalises a Unix epoch that may be in milliseconds to seconds.
    /// </summary>
    internal static long NormalizeEpochToSeconds(long value) =>
        value > 1_000_000_000_000 ? value / 1000 : value;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!this.settings.IsProviderEnabled(ProviderId.Claude))
        {
            return Task.FromResult(false);
        }

        // Return true when enabled even if credentials are missing so that
        // FetchUsageAsync runs and produces an actionable error card in the UI
        // (rather than leaving Claude completely invisible).
        return Task.FromResult(true);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var (credentials, credentialError) = this.LoadAndValidateCredentials();
            if (credentials is null)
            {
                return ProviderUsageResult.Failure(ProviderId.Claude, credentialError!);
            }

            credentials = await this.EnsureTokenFreshAsync(credentials, ct);
            if (credentials is null)
            {
                return ProviderUsageResult.Failure(
                    ProviderId.Claude,
                    "Claude login expired and could not be refreshed. Run 'claude' in terminal and sign in again.");
            }

            var accountInfo = this.ReadAccountInfo();
            var stats = this.ReadStatsCache();
            var displaySub = FormatSubscriptionType(credentials.SubscriptionType);

            var equivalentCost = CalculateEquivalentCost(stats);
            var totalTokens = CalculateTotalTokens(stats);
            var limits = await this.FetchClaudeWebUsageAsync(accountInfo, ct)
                ?? await this.FetchOAuthUsageAsync(credentials.AccessToken, ct)
                ?? await this.FetchRateLimitsAsync(credentials.AccessToken, ct);

            return BuildFetchResult(limits, displaySub, totalTokens, equivalentCost, accountInfo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Claude fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Claude, ex.Message);
        }
    }

    private (ClaudeCredentials? Credentials, string? Error) LoadAndValidateCredentials()
    {
        var credentials = this.ReadCredentials();
        if (credentials is not null)
        {
            return (credentials, null);
        }

        var error = BuildCredentialErrorMessage();

        return (null, error);
    }

    private static string BuildCredentialErrorMessage()
    {
        if (!File.Exists(CredentialsPath))
        {
            return "No Claude Code credentials found. Run 'claude' and sign in.";
        }

        try
        {
            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("mcpOAuth", out _))
            {
                return $"Claude MCP credentials exist, but Claude Code OAuth credentials are missing ({CredentialsPath}). Run 'claude auth login' or 'claude setup-token' and set CLAUDE_CODE_OAUTH_TOKEN.";
            }

            if (!root.TryGetProperty("claudeAiOauth", out _))
            {
                return $"No Claude Code OAuth credentials found in {CredentialsPath}. Run 'claude auth login' or 'claude setup-token' and set CLAUDE_CODE_OAUTH_TOKEN.";
            }
        }
        catch (Exception)
        {
            return $"Claude credentials file exists but could not be read or is corrupted ({CredentialsPath}). Delete the file and run 'claude' to re-authenticate.";
        }

        return $"Claude credentials file exists but could not be read or is corrupted ({CredentialsPath}). Delete the file and run 'claude auth login' to re-authenticate.";
    }

    private async Task<ClaudeCredentials?> EnsureTokenFreshAsync(ClaudeCredentials credentials, CancellationToken ct)
    {
        if (credentials.ExpiresAt <= 0)
        {
            this.logger.LogDebug("Claude OAuth token has no expiry set");
            return credentials;
        }

        try
        {
            var expiresAtSeconds = NormalizeEpochToSeconds(credentials.ExpiresAt);
            var tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds);
            if (tokenExpiry >= DateTimeOffset.UtcNow)
            {
                this.logger.LogDebug("Claude OAuth token valid until {Expiry}", tokenExpiry);
                return credentials;
            }

            this.logger.LogWarning("Claude OAuth token expired at {Expiry}, attempting refresh", tokenExpiry);
            return await this.TryRefreshTokenAsync(credentials, ct);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Claude OAuth token expiry value {ExpiresAt} could not be parsed", credentials.ExpiresAt);
            return credentials;
        }
    }

    internal static string FormatSubscriptionType(string? subscriptionType) =>
        string.IsNullOrWhiteSpace(subscriptionType)
            ? "Unknown"
            : char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];

    private static ProviderUsageResult BuildFetchResult(
        UnifiedRateLimits? limits,
        string displaySub,
        long totalTokens,
        double equivalentCost,
        ClaudeAccountInfo? accountInfo)
    {
        var sessionSnapshot = BuildSessionSnapshot(limits, displaySub, totalTokens, equivalentCost, accountInfo);
        var weeklySnapshot = BuildWeeklySnapshot(limits);
        var bars = BuildUsageBars(limits);

        var providerDisplayName = FormatProviderDisplayName(displaySub);
        var itemDisplayName = accountInfo?.DisplayName is not null
            ? $"{providerDisplayName} · {accountInfo.DisplayName}"
            : providerDisplayName;

        var item = new UsageItem
        {
            Key = "claude:code",
            DisplayName = itemDisplayName,
            PrimaryUsage = sessionSnapshot,
            SecondaryUsage = weeklySnapshot,
            Bars = bars,
            Success = true,
        };

        return new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            SessionUsage = sessionSnapshot,
            WeeklyUsage = weeklySnapshot,
            Items = [item],
        };
    }

    internal static string FormatProviderDisplayName(string subscriptionType) =>
        string.IsNullOrWhiteSpace(subscriptionType) || string.Equals(subscriptionType, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? "Claude"
            : $"Claude ({subscriptionType})";

    internal static UsageSnapshot BuildSessionSnapshot(
        UnifiedRateLimits? limits,
        string subscriptionType,
        long totalTokens,
        double equivalentCost,
        ClaudeAccountInfo? accountInfo)
    {
        if (limits is not null)
        {
            return BuildSessionSnapshotFromLimits(limits, subscriptionType, totalTokens, equivalentCost, accountInfo);
        }

        // Fallback: no API data available
        var fallbackLabel = FormatUsageLabel(subscriptionType, totalTokens, equivalentCost, accountInfo);
        fallbackLabel += " · Rate limits unavailable";

        return new UsageSnapshot
        {
            IsUnlimited = true,
            UsageLabel = fallbackLabel,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static UsageSnapshot BuildSessionSnapshotFromLimits(
        UnifiedRateLimits limits,
        string subscriptionType,
        long totalTokens,
        double equivalentCost,
        ClaudeAccountInfo? accountInfo)
    {
        var usedPercent = limits.FiveHourUtilization;
        var usageLabel = BuildStatusLabel(subscriptionType, totalTokens, equivalentCost, accountInfo);

        var resetDesc = limits.FiveHourReset > 0
            ? FormatResetCountdown(limits.FiveHourReset, "5-hour limit")
            : null;

        return new UsageSnapshot
        {
            UsedPercent = usedPercent,
            UsageLabel = usageLabel,
            ResetsAt = limits.FiveHourReset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(limits.FiveHourReset)
                : null,
            ResetDescription = resetDesc,
            IsUnlimited = false,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static string BuildStatusLabel(
        string subscriptionType,
        long totalTokens,
        double equivalentCost,
        ClaudeAccountInfo? accountInfo)
    {
        var statusParts = new List<string> { $"{subscriptionType} plan" };
        if (equivalentCost > 0)
        {
            statusParts.Add($"~${equivalentCost:F2} equiv.");
        }
        else if (totalTokens > 0)
        {
            statusParts.Add(FormatTokenCount(totalTokens));
        }

        if (accountInfo?.HasExtraUsageEnabled == true)
        {
            statusParts.Add("extra usage on");
        }

        return string.Join(" · ", statusParts);
    }

    internal static UsageSnapshot? BuildWeeklySnapshot(UnifiedRateLimits? limits)
    {
        if (limits is null)
        {
            return null;
        }

        var usedPercent = limits.SevenDayUtilization;
        var resetDesc = limits.SevenDayReset > 0
            ? FormatResetCountdown(limits.SevenDayReset, "Weekly")
            : null;

        return new UsageSnapshot
        {
            UsedPercent = usedPercent,
            UsageLabel = $"Weekly · all models: {usedPercent:P0}",
            ResetsAt = limits.SevenDayReset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(limits.SevenDayReset)
                : null,
            ResetDescription = resetDesc,
            IsUnlimited = false,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Builds the labelled usage bars for the Claude Code windows that are visible
    /// and actionable in Claude's usage UI: 5-hour and weekly.
    /// </summary>
    /// <returns></returns>
    internal static List<UsageBar> BuildUsageBars(UnifiedRateLimits? limits)
    {
        if (limits is null)
        {
            return [];
        }

        var bars = new List<UsageBar>(2)
        {
            new UsageBar
            {
                Label = "5-hour limit",
                UsedPercent = limits.FiveHourUtilization,
                ResetDescription = limits.FiveHourReset > 0
                ? FormatBarReset(limits.FiveHourReset)
                : null,
                ResetsAt = limits.FiveHourReset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(limits.FiveHourReset)
                : null,
            },
            new UsageBar
            {
                Label = "Weekly · all models",
                UsedPercent = limits.SevenDayUtilization,
                ResetDescription = limits.SevenDayReset > 0
                ? FormatBarReset(limits.SevenDayReset)
                : null,
                ResetsAt = limits.SevenDayReset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(limits.SevenDayReset)
                : null
            },
        };

        return bars;
    }

    /// <summary>
    /// Formats a compact reset string for the bar's right-side display (e.g., "Resets 2h", "Resets 2d").
    /// </summary>
    /// <returns></returns>
    internal static string FormatBarReset(long resetsAtEpoch)
    {
        var remaining = DateTimeOffset.FromUnixTimeSeconds(resetsAtEpoch) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "Resets now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"Resets {remaining.Days}d";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"Resets {(int)remaining.TotalHours}h";
        }

        return $"Resets {remaining.Minutes}m";
    }

    internal static string FormatResetCountdown(long resetsAtEpoch, string windowName)
    {
        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAtEpoch);
        var remaining = resetsAt - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            return $"{windowName} resets now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{windowName} resets in {remaining.Days}d {remaining.Hours}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{windowName} resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"{windowName} resets in {remaining.Minutes}m";
    }

    internal static string FormatTokenCount(long totalTokens) =>
        totalTokens switch
        {
            >= 1_000_000_000 => $"{totalTokens / 1_000_000_000.0:F1}B tokens",
            >= 1_000_000 => $"{totalTokens / 1_000_000.0:F1}M tokens",
            >= 1_000 => $"{totalTokens / 1_000.0:F1}K tokens",
            _ => $"{totalTokens} tokens",
        };

    internal static string FormatUsageLabel(
        string subscriptionType,
        long totalTokens,
        double equivalentCost,
        ClaudeAccountInfo? accountInfo)
    {
        var parts = new List<string> { $"{subscriptionType} plan" };

        if (equivalentCost > 0)
        {
            parts.Add($"~${equivalentCost:F2} equiv.");
        }
        else if (totalTokens > 0)
        {
            parts.Add(FormatTokenCount(totalTokens));
        }

        if (accountInfo?.HasExtraUsageEnabled == true)
        {
            parts.Add("extra usage on");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Probes the Anthropic Messages API with the local OAuth token and parses
    /// the <c>anthropic-ratelimit-unified-*</c> response headers for per-window utilization.
    /// Results are cached for <see cref="CacheTtl"/> to avoid excessive API calls.
    /// </summary>
    private async Task<UnifiedRateLimits?> FetchRateLimitsAsync(string? accessToken, CancellationToken ct)
    {
        if (this.TryGetFreshCachedLimits(out var cached))
        {
            return cached;
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            this.logger.LogWarning("Claude: no OAuth access token available for rate-limit probe");
            return this.GetFallbackCachedLimits();
        }

        await this.cacheLock.WaitAsync(ct);
        try
        {
            if (this.TryGetFreshCachedLimits(out cached))
            {
                return cached;
            }

            return await this.ProbeAndCacheRateLimitsAsync(accessToken, ct);
        }
        finally
        {
            this.cacheLock.Release();
        }
    }

    private async Task<UnifiedRateLimits?> FetchOAuthUsageAsync(string? accessToken, CancellationToken ct)
    {
        if (this.TryGetFreshCachedLimits(out var cached, requireAuthoritative: true))
        {
            return cached;
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            this.logger.LogWarning("Claude: no OAuth access token available for usage endpoint");
            return this.GetFallbackCachedLimits();
        }

        await this.cacheLock.WaitAsync(ct);
        try
        {
            if (this.TryGetFreshCachedLimits(out cached, requireAuthoritative: true))
            {
                return cached;
            }

            using var httpClient = this.httpClientFactory.CreateClient();
            httpClient.Timeout = ApiTimeout;

            using var request = BuildOAuthUsageRequest(accessToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ApiTimeout);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            this.logger.LogDebug("Claude OAuth usage endpoint returned status {StatusCode}", (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning("Claude OAuth usage endpoint failed with status {StatusCode}", (int)response.StatusCode);
                return this.GetFallbackCachedLimits();
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var usage = JsonSerializer.Deserialize<ClaudeOAuthUsageResponse>(json);
            var result = MapOAuthUsageToRateLimits(usage);
            return this.CacheAndReturnUsageLimits(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            this.logger.LogDebug("Claude OAuth usage endpoint timed out");
            return this.GetFallbackCachedLimits();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Claude OAuth usage endpoint failed: {Message}", ex.Message);
            return this.GetFallbackCachedLimits();
        }
        finally
        {
            this.cacheLock.Release();
        }
    }

    internal static HttpRequestMessage BuildOAuthUsageRequest(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");
        return request;
    }

    private bool TryGetFreshCachedLimits(out UnifiedRateLimits? cached, bool requireAuthoritative = false)
    {
        // Read ticks first — Volatile.Read provides an acquire fence that
        // guarantees the subsequent read of cachedLimits sees the value
        // written before the corresponding Volatile.Write in CacheAndReturnLimits.
        var cachedTicks = Volatile.Read(ref this.limitsCachedAtTicks);
        cached = this.cachedLimits;
        return cached is not null &&
               (!requireAuthoritative || this.cachedLimitsAreAuthoritative) &&
               (this.cachedLimitsAreAuthoritative || !IsEmptyRateLimitSnapshot(cached)) &&
               cachedTicks > 0 &&
               DateTimeOffset.UtcNow.UtcTicks - cachedTicks < CacheTtl.Ticks;
    }

    private UnifiedRateLimits? GetFallbackCachedLimits()
    {
        var cached = this.cachedLimits;
        return cached is not null && !IsEmptyRateLimitSnapshot(cached)
            ? cached
            : null;
    }

    private async Task<UnifiedRateLimits?> ProbeAndCacheRateLimitsAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var httpClient = this.httpClientFactory.CreateClient();
            httpClient.Timeout = ApiTimeout;

            using var request = BuildRateLimitProbeRequest(accessToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ApiTimeout);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            this.logger.LogDebug("Claude API probe returned status {StatusCode}", (int)response.StatusCode);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                this.logger.LogWarning("Claude API returned 401 — OAuth token may be expired");
                return null;
            }

            var result = ParseRateLimitHeaders(response.Headers);
            return this.CacheAndReturnLimits(result, response.Headers);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            this.logger.LogDebug("Claude API probe timed out");
            return this.GetFallbackCachedLimits();
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Claude API probe failed (HTTP): {Message}", ex.Message);
            return this.GetFallbackCachedLimits();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Claude API probe failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return this.GetFallbackCachedLimits();
        }
    }

    internal static HttpRequestMessage BuildRateLimitProbeRequest(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Content = new StringContent(ProbeRequestBody, Encoding.UTF8, "application/json");
        return request;
    }

    private UnifiedRateLimits? CacheAndReturnLimits(UnifiedRateLimits? result, HttpResponseHeaders responseHeaders)
    {
        if (result is not null)
        {
            if (IsEmptyRateLimitSnapshot(result))
            {
                this.logger.LogWarning("Claude API rate-limit probe returned 0% for both windows; ignoring non-authoritative empty snapshot");
                return this.GetFallbackCachedLimits();
            }

            this.cachedLimits = result;
            this.cachedLimitsAreAuthoritative = false;
            Volatile.Write(ref this.limitsCachedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            this.logger.LogDebug(
                "Claude rate limits: 5h={FiveH:P0}, 7d={SevenD:P0}",
                result.FiveHourUtilization, result.SevenDayUtilization);
        }
        else
        {
            var headerNames = string.Join(", ", responseHeaders.Select(h => h.Key));
            this.logger.LogWarning(
                "Claude API rate-limit headers not found. Response headers: {Headers}",
                headerNames);
        }

        return result ?? this.GetFallbackCachedLimits();
    }

    private UnifiedRateLimits? CacheAndReturnUsageLimits(UnifiedRateLimits? result)
    {
        if (result is null)
        {
            this.logger.LogWarning("Claude OAuth usage payload did not include usage windows");
            return this.GetFallbackCachedLimits();
        }

        this.cachedLimits = result;
        this.cachedLimitsAreAuthoritative = true;
        Volatile.Write(ref this.limitsCachedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        this.logger.LogDebug(
            "Claude OAuth usage: 5h={FiveH:P0}, 7d={SevenD:P0}",
            result.FiveHourUtilization,
            result.SevenDayUtilization);
        return result;
    }

    internal static bool IsEmptyRateLimitSnapshot(UnifiedRateLimits limits) =>
        limits.FiveHourUtilization <= 0 && limits.SevenDayUtilization <= 0;

    private async Task<ClaudeCredentials?> TryRefreshTokenAsync(ClaudeCredentials credentials, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credentials.RefreshToken))
        {
            this.logger.LogDebug("No refresh token available, cannot refresh");
            return null;
        }

        try
        {
            using var httpClient = this.httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            using var request = BuildTokenRefreshRequest(credentials.RefreshToken);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                this.logger.LogWarning("Claude token refresh failed with status {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return this.ApplyRefreshedToken(credentials, json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Claude token refresh failed: {Message}", ex.Message);
            return null;
        }
    }

    private static HttpRequestMessage BuildTokenRefreshRequest(string refreshToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = refreshToken,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, TokenRefreshUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return request;
    }

    private ClaudeCredentials? ApplyRefreshedToken(ClaudeCredentials credentials, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var (newAccessToken, newRefreshToken, newExpiresAt) = ParseTokenRefreshResponse(doc.RootElement);

        if (string.IsNullOrEmpty(newAccessToken))
        {
            this.logger.LogWarning("Claude token refresh response missing access_token");
            return null;
        }

        var updatedCreds = credentials with
        {
            AccessToken = newAccessToken,
            RefreshToken = newRefreshToken ?? credentials.RefreshToken,
            ExpiresAt = newExpiresAt,
        };

        this.PersistCredentials(updatedCreds);

        this.logger.LogInformation(
            "Claude OAuth token refreshed successfully, valid until {Expiry}",
            newExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(NormalizeEpochToSeconds(newExpiresAt)).ToString("o") : "unknown");

        return updatedCreds;
    }

    internal static (string? AccessToken, string? RefreshToken, long ExpiresAt) ParseTokenRefreshResponse(JsonElement root) =>
        (root.TryGetProperty("access_token", out var at) ? at.GetString() : null,
         root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
         root.TryGetProperty("expires_at", out var ea) ? ea.GetInt64() : 0);

    private void PersistCredentials(ClaudeCredentials credentials)
    {
        try
        {
            if (!File.Exists(CredentialsPath))
            {
                return;
            }

            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tempPath = CredentialsPath + ".tmp";
            using (var stream = FileSecurityHelper.CreateRestrictedFileStream(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("claudeAiOauth"))
                    {
                        writer.WritePropertyName("claudeAiOauth");
                        WriteOAuthSection(writer, prop.Value, credentials);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, CredentialsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to persist refreshed Claude credentials");
        }
    }

    internal static void WriteOAuthSection(Utf8JsonWriter writer, JsonElement oauthElement, ClaudeCredentials credentials)
    {
        writer.WriteStartObject();

        foreach (var oauthProp in oauthElement.EnumerateObject())
        {
            if (oauthProp.NameEquals("accessToken"))
            {
                writer.WriteString("accessToken", credentials.AccessToken);
            }
            else if (oauthProp.NameEquals("refreshToken") && credentials.RefreshToken is not null)
            {
                writer.WriteString("refreshToken", credentials.RefreshToken);
            }
            else if (oauthProp.NameEquals("expiresAt"))
            {
                writer.WriteNumber("expiresAt", credentials.ExpiresAt);
            }
            else
            {
                oauthProp.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    internal static UnifiedRateLimits? ParseRateLimitHeaders(HttpResponseHeaders headers)
    {
        static string? GetHeader(HttpResponseHeaders h, string name) =>
            h.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        static double ParseDouble(string? value, double fallback = 0) =>
            double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

        static long ParseLong(string? value, long fallback = 0) =>
            long.TryParse(value, out var l) ? l : fallback;

        var fiveHUtil = GetHeader(headers, "anthropic-ratelimit-unified-5h-utilization");
        var sevenDUtil = GetHeader(headers, "anthropic-ratelimit-unified-7d-utilization");

        // If neither utilization header is present, the response didn't include rate-limit data
        if (fiveHUtil is null && sevenDUtil is null)
        {
            return null;
        }

        return new UnifiedRateLimits
        {
            FiveHourUtilization = ParseDouble(fiveHUtil),
            FiveHourReset = ParseLong(GetHeader(headers, "anthropic-ratelimit-unified-5h-reset")),
            FiveHourStatus = GetHeader(headers, "anthropic-ratelimit-unified-5h-status") ?? "unknown",

            SevenDayUtilization = ParseDouble(sevenDUtil),
            SevenDayReset = ParseLong(GetHeader(headers, "anthropic-ratelimit-unified-7d-reset")),
            SevenDayStatus = GetHeader(headers, "anthropic-ratelimit-unified-7d-status") ?? "unknown",
        };
    }

    internal static UnifiedRateLimits? MapOAuthUsageToRateLimits(ClaudeOAuthUsageResponse? usage)
    {
        var fiveHour = usage?.FiveHour;
        var sevenDay = usage?.SevenDay
            ?? usage?.SevenDayOAuthApps
            ?? usage?.SevenDaySonnet
            ?? usage?.SevenDayOpus;

        if (fiveHour?.Utilization is null && sevenDay?.Utilization is null)
        {
            return null;
        }

        return new UnifiedRateLimits
        {
            FiveHourUtilization = NormalizeUtilization(fiveHour?.Utilization),
            FiveHourReset = ParseOAuthResetToEpochSeconds(fiveHour?.ResetsAt),
            FiveHourStatus = "active",
            SevenDayUtilization = NormalizeUtilization(sevenDay?.Utilization),
            SevenDayReset = ParseOAuthResetToEpochSeconds(sevenDay?.ResetsAt),
            SevenDayStatus = "active",
        };
    }

    internal static double NormalizeUtilization(double? utilization)
    {
        var value = utilization ?? 0;
        if (value > 1)
        {
            value /= 100;
        }

        return Math.Clamp(value, 0, 1);
    }

    internal static long ParseOAuthResetToEpochSeconds(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var reset)
            ? reset.ToUnixTimeSeconds()
            : 0;

    private ClaudeCredentials? ReadCredentials()
    {
        var environmentCredentials = ReadEnvironmentCredentials();
        if (environmentCredentials is not null)
        {
            return environmentCredentials;
        }

        if (!File.Exists(CredentialsPath))
        {
            return this.ReadClaudeDesktopTokenCacheCredentials();
        }

        try
        {
            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("claudeAiOauth", out var oauth))
            {
                return this.ReadClaudeDesktopTokenCacheCredentials();
            }

            return ParseCredentials(oauth);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to read Claude credentials from {Path}", CredentialsPath);
            return this.ReadClaudeDesktopTokenCacheCredentials();
        }
    }

    private static ClaudeCredentials? ReadEnvironmentCredentials()
    {
        var accessToken = EnvironmentAccessTokenOverride;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            return CreateEnvironmentCredentials(accessToken);
        }

        if (CredentialsPathOverride is not null)
        {
            return null;
        }

        accessToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            accessToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", EnvironmentVariableTarget.User);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        return CreateEnvironmentCredentials(accessToken);
    }

    private static ClaudeCredentials CreateEnvironmentCredentials(string accessToken) => new()
    {
        AccessToken = accessToken,
        SubscriptionType = "subscription",
    };

    internal static ClaudeCredentials ParseCredentials(JsonElement oauth) => new()
    {
        SubscriptionType = oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null,
        RateLimitTier = oauth.TryGetProperty("rateLimitTier", out var rlt) ? rlt.GetString() : null,
        ExpiresAt = oauth.TryGetProperty("expiresAt", out var ea) ? ea.GetInt64() : 0,
        AccessToken = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null,
        RefreshToken = oauth.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null,
    };

    private ClaudeAccountInfo? ReadAccountInfo()
    {
        if (!File.Exists(ClaudeJsonPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(ClaudeJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("oauthAccount", out var account))
            {
                return null;
            }

            return ParseAccountInfo(account);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to read Claude account info from {Path}", ClaudeJsonPath);
            return null;
        }
    }

    internal static ClaudeAccountInfo ParseAccountInfo(JsonElement account) => new()
    {
        DisplayName = account.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
        BillingType = account.TryGetProperty("billingType", out var bt) ? bt.GetString() : null,
        OrganizationUuid = account.TryGetProperty("organizationUuid", out var org) ? org.GetString() : null,
        HasExtraUsageEnabled = account.TryGetProperty("hasExtraUsageEnabled", out var eu) && eu.GetBoolean(),
    };

    private ClaudeStatsCache? ReadStatsCache()
    {
        if (!File.Exists(StatsCachePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(StatsCachePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var stats = new ClaudeStatsCache
            {
                TotalSessions = root.TryGetProperty("totalSessions", out var ts) ? ts.GetInt32() : 0,
                TotalMessages = root.TryGetProperty("totalMessages", out var tm) ? tm.GetInt32() : 0,
            };

            if (root.TryGetProperty("modelUsage", out var modelUsage))
            {
                stats.ModelUsages.AddRange(ParseModelUsages(modelUsage));
            }

            return stats;
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to read Claude stats from {Path}", StatsCachePath);
            return null;
        }
    }

    internal static List<ClaudeModelUsage> ParseModelUsages(JsonElement modelUsage)
    {
        if (modelUsage.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var usages = new List<ClaudeModelUsage>();

        foreach (var prop in modelUsage.EnumerateObject())
        {
            usages.Add(ParseSingleModelUsage(prop.Name, prop.Value));
        }

        return usages;
    }

    internal static ClaudeModelUsage ParseSingleModelUsage(string modelId, JsonElement value) => new()
    {
        ModelId = modelId,
        InputTokens = value.TryGetProperty("inputTokens", out var it) ? it.GetInt64() : 0,
        OutputTokens = value.TryGetProperty("outputTokens", out var ot) ? ot.GetInt64() : 0,
        CacheReadInputTokens = value.TryGetProperty("cacheReadInputTokens", out var crit) ? crit.GetInt64() : 0,
        CacheCreationInputTokens = value.TryGetProperty("cacheCreationInputTokens", out var ccit) ? ccit.GetInt64() : 0,
    };

    internal static long CalculateTotalTokens(ClaudeStatsCache? stats)
    {
        if (stats is null)
        {
            return 0;
        }

        return stats.ModelUsages.Sum(m =>
            m.InputTokens + m.OutputTokens + m.CacheReadInputTokens + m.CacheCreationInputTokens);
    }

    /// <summary>
    /// Calculates what the token usage would have cost at standard API pricing.
    /// </summary>
    /// <returns></returns>
    internal static double CalculateEquivalentCost(ClaudeStatsCache? stats)
    {
        if (stats is null)
        {
            return 0;
        }

        double totalCost = 0;

        foreach (var usage in stats.ModelUsages)
        {
            var pricing = ResolvePricing(usage.ModelId);

            totalCost += usage.InputTokens / 1_000_000.0 * pricing.InputPerMTok;
            totalCost += usage.OutputTokens / 1_000_000.0 * pricing.OutputPerMTok;
            totalCost += usage.CacheCreationInputTokens / 1_000_000.0 * pricing.CacheWritePerMTok;
            totalCost += usage.CacheReadInputTokens / 1_000_000.0 * pricing.CacheReadPerMTok;
        }

        return totalCost;
    }

    /// <summary>
    /// Resolves pricing for a model ID by matching against known model families.
    /// Falls back to Sonnet pricing for unknown models.
    /// </summary>
    /// <returns></returns>
    internal static (double InputPerMTok, double OutputPerMTok, double CacheWritePerMTok, double CacheReadPerMTok) ResolvePricing(string modelId)
    {
        if (ModelPricing.TryGetValue(modelId, out var exactMatch))
        {
            return exactMatch;
        }

        // Longest-prefix match (deterministic: longest prefix wins)
        (double InputPerMTok, double OutputPerMTok, double CacheWritePerMTok, double CacheReadPerMTok)? bestMatch = null;
        int bestLength = 0;
        foreach (var (prefix, pricing) in ModelPricing)
        {
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > bestLength)
            {
                bestMatch = pricing;
                bestLength = prefix.Length;
            }
        }

        return bestMatch ?? ResolvePricingByFamily(modelId);
    }

    private static (double InputPerMTok, double OutputPerMTok, double CacheWritePerMTok, double CacheReadPerMTok) ResolvePricingByFamily(string modelId)
    {
        if (modelId.Contains("opus", StringComparison.OrdinalIgnoreCase))
        {
            return ModelPricing["claude-opus-4-7"];
        }

        if (modelId.Contains("haiku", StringComparison.OrdinalIgnoreCase))
        {
            return ModelPricing["claude-haiku-4-5"];
        }

        return ModelPricing["claude-sonnet-4-6"];
    }

    internal sealed record UnifiedRateLimits
    {
        public double FiveHourUtilization { get; init; }

        public long FiveHourReset { get; init; }

        public string FiveHourStatus { get; init; } = "unknown";

        public double SevenDayUtilization { get; init; }

        public long SevenDayReset { get; init; }

        public string SevenDayStatus { get; init; } = "unknown";
    }

    internal sealed record ClaudeOAuthUsageResponse
    {
        [JsonPropertyName("five_hour")]
        public ClaudeOAuthUsageWindow? FiveHour { get; init; }

        [JsonPropertyName("seven_day")]
        public ClaudeOAuthUsageWindow? SevenDay { get; init; }

        [JsonPropertyName("seven_day_oauth_apps")]
        public ClaudeOAuthUsageWindow? SevenDayOAuthApps { get; init; }

        [JsonPropertyName("seven_day_opus")]
        public ClaudeOAuthUsageWindow? SevenDayOpus { get; init; }

        [JsonPropertyName("seven_day_sonnet")]
        public ClaudeOAuthUsageWindow? SevenDaySonnet { get; init; }
    }

    internal sealed record ClaudeOAuthUsageWindow
    {
        [JsonPropertyName("utilization")]
        public double? Utilization { get; init; }

        [JsonPropertyName("resets_at")]
        public string? ResetsAt { get; init; }
    }

    internal sealed record ClaudeCredentials
    {
        public string? SubscriptionType { get; init; }

        public string? RateLimitTier { get; init; }

        public long ExpiresAt { get; init; }

        public string? AccessToken { get; init; }

        public string? RefreshToken { get; init; }
    }

    internal sealed record ClaudeAccountInfo
    {
        public string? DisplayName { get; init; }

        public string? BillingType { get; init; }

        public string? OrganizationUuid { get; init; }

        public bool HasExtraUsageEnabled { get; init; }
    }

    internal sealed class ClaudeStatsCache
    {
        public int TotalSessions { get; init; }

        public int TotalMessages { get; init; }

        public List<ClaudeModelUsage> ModelUsages { get; } = [];
    }

    internal sealed record ClaudeModelUsage
    {
        public string ModelId { get; init; } = string.Empty;

        public long InputTokens { get; init; }

        public long OutputTokens { get; init; }

        public long CacheReadInputTokens { get; init; }

        public long CacheCreationInputTokens { get; init; }
    }
}
