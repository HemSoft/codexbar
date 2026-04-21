using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Claude;

/// <summary>
/// Fetches Claude Code Pro subscription usage from local data files.
/// <para>
/// Reads credentials from <c>~/.claude/.credentials.json</c> for subscription type,
/// stats from <c>~/.claude/stats-cache.json</c> for token usage,
/// and account info from <c>~/.claude.json</c>.
/// </para>
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

    public ClaudeProvider(ILogger<ClaudeProvider> logger, SettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Claude,
        DisplayName = "Claude",
        Description = "Claude Code — subscription usage from local data",
        DashboardUrl = "https://claude.ai/settings/usage",
        StatusPageUrl = "https://status.anthropic.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = false,
        SupportsCredits = true
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_settings.IsProviderEnabled(ProviderId.Claude))
            return Task.FromResult(false);

        return Task.FromResult(File.Exists(CredentialsPath));
    }

    public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var credentials = ReadCredentials();
            if (credentials is null)
                return Task.FromResult(ProviderUsageResult.Failure(ProviderId.Claude,
                    "No Claude Code credentials found. Run 'claude' and sign in."));

            var accountInfo = ReadAccountInfo();
            var stats = ReadStatsCache();

            var subscriptionType = credentials.SubscriptionType ?? "unknown";
            var displaySub = char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];
            var accountName = accountInfo?.DisplayName;

            // Calculate equivalent API cost from token usage stats
            var equivalentCost = CalculateEquivalentCost(stats);
            var totalTokens = CalculateTotalTokens(stats);

            // Build the primary usage display showing subscription and token info
            var usageLabel = FormatUsageLabel(displaySub, totalTokens, accountInfo);
            var snapshot = new UsageSnapshot
            {
                // Subscription plans don't expose a remaining %, so show as unlimited
                IsUnlimited = true,
                UsageLabel = usageLabel,
                CapturedAt = DateTimeOffset.UtcNow
            };

            var itemKey = "claude:code";
            var itemDisplayName = accountName is not null
                ? $"Claude · {accountName}"
                : "Claude Code";

            var item = new UsageItem
            {
                Key = itemKey,
                DisplayName = itemDisplayName,
                PrimaryUsage = snapshot,
                CreditsRemaining = equivalentCost > 0 ? (decimal)equivalentCost : null,
                Success = true
            };

            _logger.LogDebug(
                "Claude: subscription={Sub}, equivalentCost=${Cost:F2}, totalTokens={Tokens:N0}",
                subscriptionType, equivalentCost, totalTokens);

            return Task.FromResult(new ProviderUsageResult
            {
                Provider = ProviderId.Claude,
                Success = true,
                SessionUsage = snapshot,
                CreditsRemaining = equivalentCost > 0 ? (decimal)equivalentCost : null,
                Items = [item]
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude fetch failed");
            return Task.FromResult(ProviderUsageResult.Failure(ProviderId.Claude, ex.Message));
        }
    }

    private static string FormatUsageLabel(
        string subscriptionType,
        long totalTokens,
        ClaudeAccountInfo? accountInfo)
    {
        var parts = new List<string> { $"{subscriptionType} plan" };

        if (totalTokens > 0)
        {
            var formatted = totalTokens switch
            {
                >= 1_000_000_000 => $"{totalTokens / 1_000_000_000.0:F1}B tokens",
                >= 1_000_000 => $"{totalTokens / 1_000_000.0:F1}M tokens",
                >= 1_000 => $"{totalTokens / 1_000.0:F1}K tokens",
                _ => $"{totalTokens} tokens"
            };
            parts.Add(formatted);
        }

        if (accountInfo?.HasExtraUsageEnabled == true)
            parts.Add("extra usage on");

        return string.Join(" · ", parts);
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
