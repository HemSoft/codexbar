using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Claude;

/// <summary>
/// Fetches Claude Code Pro subscription usage by making a lightweight API call
/// to the Anthropic Messages endpoint using the local OAuth token, then parsing
/// the rate-limit response headers for per-window utilization data.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly ILogger<ClaudeProvider> _logger;
    private readonly SettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly string ClaudeDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static readonly string CredentialsPath = Path.Combine(ClaudeDir, ".credentials.json");
    private static readonly string StatsCachePath = Path.Combine(ClaudeDir, "stats-cache.json");

    private static readonly string ClaudeJsonPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);

    // Minimal request body: haiku model, 1 token, trivial prompt — just enough to trigger rate-limit headers
    private static readonly string ProbeRequestBody = JsonSerializer.Serialize(new
    {
        model = "claude-haiku-4-5",
        max_tokens = 1,
        messages = new[] { new { role = "user", content = "x" } }
    });

    // Anthropic API pricing per million tokens (used to calculate equivalent cost)
    private static readonly Dictionary<string, (double InputPerMTok, double OutputPerMTok, double CacheWritePerMTok, double CacheReadPerMTok)> ModelPricing =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-4-7"]   = (5.0,  25.0, 6.25,  0.50),
            ["claude-opus-4-6"]   = (5.0,  25.0, 6.25,  0.50),
            ["claude-opus-4-5"]   = (5.0,  25.0, 6.25,  0.50),
            ["claude-sonnet-4-6"] = (3.0,  15.0, 3.75,  0.30),
            ["claude-sonnet-4-5"] = (3.0,  15.0, 3.75,  0.30),
            ["claude-sonnet-4"]   = (3.0,  15.0, 3.75,  0.30),
            ["claude-haiku-4-5"]  = (1.0,  5.0,  1.25,  0.10),
            ["claude-haiku-3-5"]  = (0.80, 4.0,  1.0,   0.08),
        };

    // Cache the last known rate-limit data so we don't probe the API on every refresh.
    // TTL is 30 min to avoid burning subscription quota with frequent probes (~48 req/day vs ~288).
    private UnifiedRateLimits? _cachedLimits;
    private long _limitsCachedAtTicks; // stored as ticks for atomic reads via Volatile
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

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
        Description = "Claude Code — subscription usage via API rate-limit headers",
        DashboardUrl = "https://claude.ai/settings/usage",
        StatusPageUrl = "https://status.anthropic.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = true,
        SupportsCredits = false
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_settings.IsProviderEnabled(ProviderId.Claude))
            return Task.FromResult(false);

        // Return true when enabled even if credentials are missing so that
        // FetchUsageAsync runs and produces an actionable error card in the UI
        // (rather than leaving Claude completely invisible).
        return Task.FromResult(true);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var credentials = ReadCredentials();
            if (credentials is null)
            {
                // Distinguish "file missing" from "file exists but could not be read"
                if (File.Exists(CredentialsPath))
                    return ProviderUsageResult.Failure(ProviderId.Claude,
                        $"Claude credentials file exists but could not be read or is corrupted ({CredentialsPath}). Delete the file and run 'claude' to re-authenticate.");

                return ProviderUsageResult.Failure(ProviderId.Claude,
                    "No Claude Code credentials found. Run 'claude' and sign in.");
            }

            var accountInfo = ReadAccountInfo();
            var stats = ReadStatsCache();

            var subscriptionType = credentials.SubscriptionType;
            string displaySub;
            if (string.IsNullOrWhiteSpace(subscriptionType))
                displaySub = "Unknown";
            else
                displaySub = char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];
            var accountName = accountInfo?.DisplayName;

            var equivalentCost = CalculateEquivalentCost(stats);
            var totalTokens = CalculateTotalTokens(stats);

            // Fetch real-time rate-limit data via a lightweight API probe
            var limits = await FetchRateLimitsAsync(credentials.AccessToken, ct);

            var sessionSnapshot = BuildSessionSnapshot(limits, displaySub, totalTokens, equivalentCost, accountInfo);
            var weeklySnapshot = BuildWeeklySnapshot(limits);
            var bars = BuildUsageBars(limits);

            var itemKey = "claude:code";
            var itemDisplayName = accountName is not null
                ? $"Claude · {accountName}"
                : "Claude Code";

            var item = new UsageItem
            {
                Key = itemKey,
                DisplayName = itemDisplayName,
                PrimaryUsage = sessionSnapshot,
                SecondaryUsage = weeklySnapshot,
                Bars = bars,
                Success = true
            };

            _logger.LogDebug(
                "Claude: subscription={Sub}, equivalentCost=${Cost:F2}, totalTokens={Tokens:N0}, 5h={FiveH:P0}, 7d={SevenD:P0}",
                subscriptionType ?? "unknown", equivalentCost, totalTokens,
                limits?.FiveHourUtilization ?? -1, limits?.SevenDayUtilization ?? -1);

            return new ProviderUsageResult
            {
                Provider = ProviderId.Claude,
                Success = true,
                SessionUsage = sessionSnapshot,
                WeeklyUsage = weeklySnapshot,
                Items = [item]
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude fetch failed");
            return ProviderUsageResult.Failure(ProviderId.Claude, ex.Message);
        }
    }

    private UsageSnapshot BuildSessionSnapshot(
        UnifiedRateLimits? limits,
        string subscriptionType,
        long totalTokens,
        double equivalentCost,
        ClaudeAccountInfo? accountInfo)
    {
        if (limits is not null)
        {
            var usedPercent = limits.FiveHourUtilization;

            var statusParts = new List<string> { $"{subscriptionType} plan" };
            if (equivalentCost > 0)
                statusParts.Add($"~${equivalentCost:F2} equiv.");
            else if (totalTokens > 0)
                statusParts.Add(FormatTokenCount(totalTokens));
            if (accountInfo?.HasExtraUsageEnabled == true)
                statusParts.Add("extra usage on");

            var resetDesc = limits.FiveHourReset > 0
                ? FormatResetCountdown(limits.FiveHourReset, "5-hour limit")
                : null;

            return new UsageSnapshot
            {
                UsedPercent = usedPercent,
                UsageLabel = string.Join(" · ", statusParts),
                ResetsAt = limits.FiveHourReset > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(limits.FiveHourReset)
                    : null,
                ResetDescription = resetDesc,
                IsUnlimited = false,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }

        // Fallback: no API data available
        return new UsageSnapshot
        {
            IsUnlimited = true,
            UsageLabel = FormatUsageLabel(subscriptionType, totalTokens, equivalentCost, accountInfo),
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private static UsageSnapshot? BuildWeeklySnapshot(UnifiedRateLimits? limits)
    {
        if (limits is null)
            return null;

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
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Builds the list of labelled usage bars matching Claude's own UI:
    /// 1. 5-hour limit  2. Weekly · all models  3. Weekly · [model class] (from representative claim)
    /// </summary>
    private static List<UsageBar>? BuildUsageBars(UnifiedRateLimits? limits)
    {
        if (limits is null)
            return null;

        var bars = new List<UsageBar>(3);

        // Bar 1: 5-hour limit
        bars.Add(new UsageBar
        {
            Label = "5-hour limit",
            UsedPercent = limits.FiveHourUtilization,
            ResetDescription = limits.FiveHourReset > 0
                ? FormatBarReset(limits.FiveHourReset)
                : null,
            ResetsAt = limits.FiveHourReset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(limits.FiveHourReset)
                : null
        });

        // Bar 2: Weekly · all models
        bars.Add(new UsageBar
        {
            Label = "Weekly · all models",
            UsedPercent = limits.SevenDayUtilization,
            ResetDescription = limits.SevenDayReset > 0
                ? FormatBarReset(limits.SevenDayReset)
                : null,
            ResetsAt = limits.SevenDayReset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(limits.SevenDayReset)
                : null
        });

        // Bar 3: Weekly · [model class] (representative claim / per-model-class limit)
        var claimLabel = PrettifyRepresentativeClaim(limits.RepresentativeClaim);
        bars.Add(new UsageBar
        {
            Label = $"Weekly · {claimLabel}",
            UsedPercent = limits.OverageUtilization,
            ResetDescription = limits.OverageReset > 0
                ? FormatBarReset(limits.OverageReset)
                : null,
            ResetsAt = limits.OverageReset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(limits.OverageReset)
                : null
        });

        return bars;
    }

    /// <summary>
    /// Formats a compact reset string for the bar's right-side display (e.g., "Resets 2h", "Resets 2d").
    /// </summary>
    private static string FormatBarReset(long resetsAtEpoch)
    {
        var remaining = DateTimeOffset.FromUnixTimeSeconds(resetsAtEpoch) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "Resets now";
        if (remaining.TotalDays >= 1) return $"Resets {remaining.Days}d";
        if (remaining.TotalHours >= 1) return $"Resets {(int)remaining.TotalHours}h";
        return $"Resets {remaining.Minutes}m";
    }

    /// <summary>
    /// Converts a raw representative claim slug (e.g., "claude_design", "claude-design")
    /// into a human-friendly label (e.g., "Claude Design").
    /// </summary>
    private static string PrettifyRepresentativeClaim(string? claim)
    {
        if (string.IsNullOrWhiteSpace(claim))
            return "Model class";

        // Strip "claude_" / "claude-" prefix if present, then title-case
        var normalized = claim
            .Replace("claude_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("claude-", "", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();

        if (normalized.Length == 0)
            return claim;

        // Title-case each word
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();

        return string.Join(' ', words);
    }

    private static string FormatResetCountdown(long resetsAtEpoch, string windowName)
    {
        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAtEpoch);
        var remaining = resetsAt - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
            return $"{windowName} resets now";

        if (remaining.TotalDays >= 1)
            return $"{windowName} resets in {remaining.Days}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1)
            return $"{windowName} resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";

        return $"{windowName} resets in {remaining.Minutes}m";
    }

    private static string FormatTokenCount(long totalTokens) =>
        totalTokens switch
        {
            >= 1_000_000_000 => $"{totalTokens / 1_000_000_000.0:F1}B tokens",
            >= 1_000_000 => $"{totalTokens / 1_000_000.0:F1}M tokens",
            >= 1_000 => $"{totalTokens / 1_000.0:F1}K tokens",
            _ => $"{totalTokens} tokens"
        };

    private static string FormatUsageLabel(
        string subscriptionType,
        long totalTokens,
        double equivalentCost,
        ClaudeAccountInfo? accountInfo)
    {
        var parts = new List<string> { $"{subscriptionType} plan" };

        if (equivalentCost > 0)
            parts.Add($"~${equivalentCost:F2} equiv.");
        else if (totalTokens > 0)
            parts.Add(FormatTokenCount(totalTokens));

        if (accountInfo?.HasExtraUsageEnabled == true)
            parts.Add("extra usage on");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Probes the Anthropic Messages API with the local OAuth token and parses
    /// the <c>anthropic-ratelimit-unified-*</c> response headers for per-window utilization.
    /// Results are cached for <see cref="CacheTtl"/> to avoid excessive API calls.
    /// </summary>
    private async Task<UnifiedRateLimits?> FetchRateLimitsAsync(string? accessToken, CancellationToken ct)
    {
        // Return cached value if still fresh (lock-free read using atomic ticks)
        var cachedTicks = Volatile.Read(ref _limitsCachedAtTicks);
        if (_cachedLimits is not null &&
            cachedTicks > 0 &&
            DateTimeOffset.UtcNow.UtcTicks - cachedTicks < CacheTtl.Ticks)
        {
            return _cachedLimits;
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogDebug("No OAuth access token available for rate-limit probe");
            return _cachedLimits;
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock — another call may have refreshed the cache
            cachedTicks = Volatile.Read(ref _limitsCachedAtTicks);
            if (_cachedLimits is not null &&
                cachedTicks > 0 &&
                DateTimeOffset.UtcNow.UtcTicks - cachedTicks < CacheTtl.Ticks)
            {
                return _cachedLimits;
            }

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = ApiTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Content = new StringContent(ProbeRequestBody, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ApiTimeout);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Claude API returned 401 — OAuth token may be expired");
                return null; // Don't return stale cache on auth failure
            }

            // Rate-limit headers are present on success and most error responses
            var result = ParseRateLimitHeaders(response.Headers);

            if (result is not null)
            {
                _cachedLimits = result;
                Volatile.Write(ref _limitsCachedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                _logger.LogDebug("Claude rate limits: 5h={FiveH:P0}, 7d={SevenD:P0}, overage={Ovg:P0}",
                    result.FiveHourUtilization, result.SevenDayUtilization, result.OverageUtilization);
            }

            return result ?? _cachedLimits;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Claude API probe timed out");
            return _cachedLimits;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Claude API probe failed");
            return _cachedLimits;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static UnifiedRateLimits? ParseRateLimitHeaders(HttpResponseHeaders headers)
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
            return null;

        return new UnifiedRateLimits
        {
            FiveHourUtilization = ParseDouble(fiveHUtil),
            FiveHourReset = ParseLong(GetHeader(headers, "anthropic-ratelimit-unified-5h-reset")),
            FiveHourStatus = GetHeader(headers, "anthropic-ratelimit-unified-5h-status") ?? "unknown",

            SevenDayUtilization = ParseDouble(sevenDUtil),
            SevenDayReset = ParseLong(GetHeader(headers, "anthropic-ratelimit-unified-7d-reset")),
            SevenDayStatus = GetHeader(headers, "anthropic-ratelimit-unified-7d-status") ?? "unknown",

            OverageUtilization = ParseDouble(GetHeader(headers, "anthropic-ratelimit-unified-overage-utilization")),
            OverageReset = ParseLong(GetHeader(headers, "anthropic-ratelimit-unified-overage-reset")),
            OverageStatus = GetHeader(headers, "anthropic-ratelimit-unified-overage-status") ?? "unknown",

            RepresentativeClaim = GetHeader(headers, "anthropic-ratelimit-unified-representative-claim")
        };
    }

    private ClaudeCredentials? ReadCredentials()
    {
        if (!File.Exists(CredentialsPath))
            return null;

        try
        {
            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("claudeAiOauth", out var oauth))
                return null;

            return new ClaudeCredentials
            {
                SubscriptionType = oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null,
                RateLimitTier = oauth.TryGetProperty("rateLimitTier", out var rlt) ? rlt.GetString() : null,
                ExpiresAt = oauth.TryGetProperty("expiresAt", out var ea) ? ea.GetInt64() : 0,
                AccessToken = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Claude credentials from {Path}", CredentialsPath);
            return null;
        }
    }

    private ClaudeAccountInfo? ReadAccountInfo()
    {
        if (!File.Exists(ClaudeJsonPath))
            return null;

        try
        {
            var json = File.ReadAllText(ClaudeJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("oauthAccount", out var account))
                return null;

            return new ClaudeAccountInfo
            {
                DisplayName = account.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                BillingType = account.TryGetProperty("billingType", out var bt) ? bt.GetString() : null,
                HasExtraUsageEnabled = account.TryGetProperty("hasExtraUsageEnabled", out var eu) && eu.GetBoolean()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Claude account info from {Path}", ClaudeJsonPath);
            return null;
        }
    }

    private ClaudeStatsCache? ReadStatsCache()
    {
        if (!File.Exists(StatsCachePath))
            return null;

        try
        {
            var json = File.ReadAllText(StatsCachePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var stats = new ClaudeStatsCache
            {
                TotalSessions = root.TryGetProperty("totalSessions", out var ts) ? ts.GetInt32() : 0,
                TotalMessages = root.TryGetProperty("totalMessages", out var tm) ? tm.GetInt32() : 0
            };

            if (root.TryGetProperty("modelUsage", out var modelUsage) &&
                modelUsage.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in modelUsage.EnumerateObject())
                {
                    var usage = new ClaudeModelUsage
                    {
                        ModelId = prop.Name,
                        InputTokens = prop.Value.TryGetProperty("inputTokens", out var it) ? it.GetInt64() : 0,
                        OutputTokens = prop.Value.TryGetProperty("outputTokens", out var ot) ? ot.GetInt64() : 0,
                        CacheReadInputTokens = prop.Value.TryGetProperty("cacheReadInputTokens", out var crit) ? crit.GetInt64() : 0,
                        CacheCreationInputTokens = prop.Value.TryGetProperty("cacheCreationInputTokens", out var ccit) ? ccit.GetInt64() : 0
                    };
                    stats.ModelUsages.Add(usage);
                }
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Claude stats from {Path}", StatsCachePath);
            return null;
        }
    }

    private static long CalculateTotalTokens(ClaudeStatsCache? stats)
    {
        if (stats is null)
            return 0;

        return stats.ModelUsages.Sum(m =>
            m.InputTokens + m.OutputTokens + m.CacheReadInputTokens + m.CacheCreationInputTokens);
    }

    /// <summary>
    /// Calculates what the token usage would have cost at standard API pricing.
    /// </summary>
    private static double CalculateEquivalentCost(ClaudeStatsCache? stats)
    {
        if (stats is null)
            return 0;

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
    private static (double InputPerMTok, double OutputPerMTok, double CacheWritePerMTok, double CacheReadPerMTok) ResolvePricing(string modelId)
    {
        // Exact match first
        if (ModelPricing.TryGetValue(modelId, out var exactMatch))
            return exactMatch;

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
        if (bestMatch is not null)
            return bestMatch.Value;

        // Fallback by model family
        if (modelId.Contains("opus", StringComparison.OrdinalIgnoreCase))
            return ModelPricing["claude-opus-4-7"];
        if (modelId.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            return ModelPricing["claude-haiku-4-5"];

        // Default to Sonnet pricing
        return ModelPricing["claude-sonnet-4-6"];
    }

    private sealed record UnifiedRateLimits
    {
        public double FiveHourUtilization { get; init; }
        public long FiveHourReset { get; init; }
        public string FiveHourStatus { get; init; } = "unknown";

        public double SevenDayUtilization { get; init; }
        public long SevenDayReset { get; init; }
        public string SevenDayStatus { get; init; } = "unknown";

        public double OverageUtilization { get; init; }
        public long OverageReset { get; init; }
        public string OverageStatus { get; init; } = "unknown";

        public string? RepresentativeClaim { get; init; }
    }

    private sealed record ClaudeCredentials
    {
        public string? SubscriptionType { get; init; }
        public string? RateLimitTier { get; init; }
        public long ExpiresAt { get; init; }
        public string? AccessToken { get; init; }
    }

    private sealed record ClaudeAccountInfo
    {
        public string? DisplayName { get; init; }
        public string? BillingType { get; init; }
        public bool HasExtraUsageEnabled { get; init; }
    }

    private sealed class ClaudeStatsCache
    {
        public int TotalSessions { get; init; }
        public int TotalMessages { get; init; }
        public List<ClaudeModelUsage> ModelUsages { get; } = [];
    }

    private sealed record ClaudeModelUsage
    {
        public string ModelId { get; init; } = "";
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long CacheReadInputTokens { get; init; }
        public long CacheCreationInputTokens { get; init; }
    }
}
