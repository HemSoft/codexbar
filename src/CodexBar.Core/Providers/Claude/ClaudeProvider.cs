using System.Diagnostics;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Claude;

/// <summary>
/// Fetches Claude Code Pro subscription usage by invoking the Claude CLI
/// to obtain real-time rate-limit data (session and weekly utilization)
/// and reading local data files for account and token stats.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly ILogger<ClaudeProvider> _logger;
    private readonly SettingsService _settings;

    private static readonly string ClaudeDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static readonly string CredentialsPath = Path.Combine(ClaudeDir, ".credentials.json");
    private static readonly string StatsCachePath = Path.Combine(ClaudeDir, "stats-cache.json");

    private static readonly string ClaudeJsonPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(30);

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

    // Cache the last known rate-limit info so we don't call the CLI on every refresh
    private RateLimitInfo? _cachedRateLimit;
    private DateTimeOffset _rateLimitCachedAt;
    private static readonly TimeSpan RateLimitCacheTtl = TimeSpan.FromMinutes(5);

    public ClaudeProvider(ILogger<ClaudeProvider> logger, SettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Claude,
        DisplayName = "Claude",
        Description = "Claude Code — subscription usage via CLI + local data",
        DashboardUrl = "https://claude.ai/settings/usage",
        StatusPageUrl = "https://status.anthropic.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = true,
        SupportsCredits = true
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_settings.IsProviderEnabled(ProviderId.Claude))
            return Task.FromResult(false);

        return Task.FromResult(File.Exists(CredentialsPath));
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var credentials = ReadCredentials();
            if (credentials is null)
                return ProviderUsageResult.Failure(ProviderId.Claude,
                    "No Claude Code credentials found. Run 'claude' and sign in.");

            var accountInfo = ReadAccountInfo();
            var stats = ReadStatsCache();

            var subscriptionType = credentials.SubscriptionType ?? "unknown";
            var displaySub = char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];
            var accountName = accountInfo?.DisplayName;

            var equivalentCost = CalculateEquivalentCost(stats);
            var totalTokens = CalculateTotalTokens(stats);

            // Fetch real-time rate-limit data from the Claude CLI
            var rateLimit = await GetRateLimitInfoAsync(ct);

            var sessionSnapshot = BuildSessionSnapshot(rateLimit, displaySub, totalTokens, accountInfo);
            var weeklySnapshot = BuildWeeklySnapshot(rateLimit);

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
                CreditsRemaining = equivalentCost > 0 ? (decimal)equivalentCost : null,
                Success = true
            };

            _logger.LogDebug(
                "Claude: subscription={Sub}, equivalentCost=${Cost:F2}, totalTokens={Tokens:N0}, rateLimitStatus={Status}",
                subscriptionType, equivalentCost, totalTokens, rateLimit?.Status ?? "unknown");

            return new ProviderUsageResult
            {
                Provider = ProviderId.Claude,
                Success = true,
                SessionUsage = sessionSnapshot,
                WeeklyUsage = weeklySnapshot,
                CreditsRemaining = equivalentCost > 0 ? (decimal)equivalentCost : null,
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
        RateLimitInfo? rateLimit,
        string subscriptionType,
        long totalTokens,
        ClaudeAccountInfo? accountInfo)
    {
        if (rateLimit is not null)
        {
            var usedPercent = rateLimit.Utilization ?? 0;
            var statusParts = new List<string> { $"{subscriptionType} plan" };

            if (totalTokens > 0)
                statusParts.Add(FormatTokenCount(totalTokens));
            if (rateLimit.IsUsingOverage)
                statusParts.Add("extra usage active");
            else if (accountInfo?.HasExtraUsageEnabled == true)
                statusParts.Add("extra usage on");

            var windowLabel = rateLimit.RateLimitType switch
            {
                "five_hour" => "Session (5hr)",
                "seven_day" or "seven_day_opus" or "seven_day_sonnet" => "Weekly (7d)",
                "overage" => "Overage",
                _ => "Limit"
            };

            var statusText = rateLimit.Status switch
            {
                "allowed" => "within limits",
                "allowed_warning" => $"approaching limit ({usedPercent:P0})",
                "rejected" => "at limit",
                _ => "unknown"
            };

            var resetDesc = rateLimit.ResetsAt.HasValue
                ? FormatResetCountdown(rateLimit.ResetsAt.Value, windowLabel)
                : null;

            return new UsageSnapshot
            {
                UsedPercent = usedPercent,
                UsageLabel = $"{string.Join(" · ", statusParts)} · {windowLabel}: {statusText}",
                ResetsAt = rateLimit.ResetsAt.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(rateLimit.ResetsAt.Value)
                    : null,
                ResetDescription = resetDesc,
                IsUnlimited = rateLimit.Utilization is null,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }

        // Fallback: no CLI data available
        return new UsageSnapshot
        {
            IsUnlimited = true,
            UsageLabel = FormatUsageLabel(subscriptionType, totalTokens, accountInfo),
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private static UsageSnapshot? BuildWeeklySnapshot(RateLimitInfo? rateLimit)
    {
        if (rateLimit is null)
            return null;

        // Overage info is always present; use it for the weekly display
        if (rateLimit.OverageResetsAt.HasValue)
        {
            var overageResetDesc = FormatResetCountdown(rateLimit.OverageResetsAt.Value, "Overage");

            return new UsageSnapshot
            {
                UsedPercent = 0,
                UsageLabel = rateLimit.OverageStatus switch
                {
                    "allowed" => "Overage: within limits",
                    "allowed_warning" => "Overage: approaching limit",
                    "rejected" => "Overage: at limit",
                    _ => "Overage"
                },
                ResetsAt = DateTimeOffset.FromUnixTimeSeconds(rateLimit.OverageResetsAt.Value),
                ResetDescription = overageResetDesc,
                IsUnlimited = true,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }

        return null;
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
        ClaudeAccountInfo? accountInfo)
    {
        var parts = new List<string> { $"{subscriptionType} plan" };

        if (totalTokens > 0)
            parts.Add(FormatTokenCount(totalTokens));

        if (accountInfo?.HasExtraUsageEnabled == true)
            parts.Add("extra usage on");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Gets rate-limit data by invoking <c>claude -p</c> with stream-json output.
    /// Results are cached for <see cref="RateLimitCacheTtl"/> to avoid excessive CLI calls.
    /// </summary>
    private async Task<RateLimitInfo?> GetRateLimitInfoAsync(CancellationToken ct)
    {
        // Return cached value if still fresh
        if (_cachedRateLimit is not null &&
            DateTimeOffset.UtcNow - _rateLimitCachedAt < RateLimitCacheTtl)
        {
            return _cachedRateLimit;
        }

        try
        {
            var claudePath = FindClaudeCli();
            if (claudePath is null)
            {
                _logger.LogDebug("Claude CLI not found on PATH");
                return _cachedRateLimit; // Return stale cache if available
            }

            var psi = new ProcessStartInfo
            {
                FileName = claudePath,
                Arguments = "-p x --model haiku --verbose --output-format stream-json --no-session-persistence",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogDebug("Failed to start claude process");
                return _cachedRateLimit;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CliTimeout);

            RateLimitInfo? result = null;

            // Read stream-json lines looking for rate_limit_event
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null) break;

                if (!line.Contains("rate_limit_event", StringComparison.Ordinal))
                    continue;

                result = ParseRateLimitEvent(line);
                if (result is not null)
                    break;
            }

            // Don't wait forever for the process to exit
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }

            if (result is not null)
            {
                _cachedRateLimit = result;
                _rateLimitCachedAt = DateTimeOffset.UtcNow;
                _logger.LogDebug("Claude rate limit: status={Status}, type={Type}, resetsAt={Reset}",
                    result.Status, result.RateLimitType, result.ResetsAt);
            }

            return result ?? _cachedRateLimit;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Claude CLI call timed out");
            return _cachedRateLimit;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Claude CLI call failed");
            return _cachedRateLimit;
        }
    }

    private static string? FindClaudeCli()
    {
        var userLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "claude.exe");

        if (File.Exists(userLocal))
            return userLocal;

        // Check PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "claude.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private RateLimitInfo? ParseRateLimitEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("rate_limit_info", out var info))
                return null;

            return new RateLimitInfo
            {
                Status = info.TryGetProperty("status", out var s) ? s.GetString() : null,
                ResetsAt = info.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : null,
                RateLimitType = info.TryGetProperty("rateLimitType", out var t) ? t.GetString() : null,
                Utilization = info.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null,
                OverageStatus = info.TryGetProperty("overageStatus", out var os) ? os.GetString() : null,
                OverageResetsAt = info.TryGetProperty("overageResetsAt", out var or) && or.ValueKind == JsonValueKind.Number ? or.GetInt64() : null,
                IsUsingOverage = info.TryGetProperty("isUsingOverage", out var uo) && uo.ValueKind == JsonValueKind.True
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse rate_limit_event");
            return null;
        }
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
                ExpiresAt = oauth.TryGetProperty("expiresAt", out var ea) ? ea.GetInt64() : 0
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
        // Try exact match first from known prefixes
        foreach (var (prefix, pricing) in ModelPricing)
        {
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return pricing;
        }

        // Fallback by model family
        if (modelId.Contains("opus", StringComparison.OrdinalIgnoreCase))
            return ModelPricing["claude-opus-4-7"];
        if (modelId.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            return ModelPricing["claude-haiku-4-5"];

        // Default to Sonnet pricing
        return ModelPricing["claude-sonnet-4-6"];
    }

    private sealed record RateLimitInfo
    {
        public string? Status { get; init; }
        public long? ResetsAt { get; init; }
        public string? RateLimitType { get; init; }
        public double? Utilization { get; init; }
        public string? OverageStatus { get; init; }
        public long? OverageResetsAt { get; init; }
        public bool IsUsingOverage { get; init; }
    }

    private sealed record ClaudeCredentials
    {
        public string? SubscriptionType { get; init; }
        public string? RateLimitTier { get; init; }
        public long ExpiresAt { get; init; }
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
