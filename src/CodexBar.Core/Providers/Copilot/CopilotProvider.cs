using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Providers.Copilot;

/// <summary>
/// Fetches GitHub Copilot usage for one or more accounts via the Copilot internal API.
/// <para>
/// Multi-account: configured accounts in settings or auto-discovered from <c>gh auth status</c>.
/// Per-account tokens resolved via <c>gh auth token --user &lt;name&gt;</c>.
/// </para>
/// </summary>
public sealed class CopilotProvider : IUsageProvider
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

    private static string GetConfiguredValue(string environmentVariableName, string fallbackValue)
    {
        var configuredValue = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(configuredValue) ? fallbackValue : configuredValue.Trim();
    }

    private readonly ILogger<CopilotProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;

    // Cached account list — refreshed on startup, auth failure, or explicit refresh
    private readonly object _accountsLock = new();
    private List<string>? _cachedAccounts;
    private readonly ConcurrentDictionary<string, string> _tokenCache = new(StringComparer.OrdinalIgnoreCase);

    public CopilotProvider(ILogger<CopilotProvider> logger, IHttpClientFactory httpClientFactory, SettingsService settings)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.Copilot,
        DisplayName = "Copilot",
        Description = "GitHub Copilot — usage limits via GitHub API",
        DashboardUrl = "https://github.com/settings/copilot",
        StatusPageUrl = "https://www.githubstatus.com",
        SupportsSessionUsage = true,
        SupportsWeeklyUsage = false,
        SupportsCredits = false
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var isEnabled = _settings.IsProviderEnabled(ProviderId.Copilot);
        return Task.FromResult(isEnabled);
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        if (!_settings.IsProviderEnabled(ProviderId.Copilot))
            return ProviderUsageResult.Failure(ProviderId.Copilot, "Disabled in settings");

        var accounts = GetAccountsToFetch()
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0)
            return ProviderUsageResult.Failure(ProviderId.Copilot,
                "No Copilot accounts found. Add copilotAccounts to ~/.codexbar/settings.json or run 'gh auth login'.");

        var items = new List<UsageItem>();
        var accountResults = new List<CopilotAccountResult>();

        var tasks = accounts.Select(async username =>
        {
            var result = await FetchAccountQuotaAsync(username, ct);
            return (username, result);
        });

        foreach (var (username, result) in await Task.WhenAll(tasks))
        {
            accountResults.Add(result);
            items.Add(ToUsageItem(username, result));
        }

        // Aggregate: use the first successful premium snapshot for the legacy SessionUsage field
        var firstSuccess = accountResults.FirstOrDefault(r => r.Success && r.PremiumInteractions is not null);
        UsageSnapshot? aggregateSession = null;
        if (firstSuccess?.PremiumInteractions is not null)
        {
            aggregateSession = BuildUsageSnapshot(firstSuccess.PremiumInteractions,
                firstSuccess.QuotaResetDateUtc);
        }

        return new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = accountResults.Any(r => r.Success),
            SessionUsage = aggregateSession,
            Items = items,
            ErrorMessage = accountResults.All(r => !r.Success)
                ? string.Join("; ", accountResults.Select(r => $"{r.Username}: {r.ErrorMessage}"))
                : null
        };
    }

    /// <summary>
    /// Returns the list of accounts to fetch: from settings if configured, otherwise auto-discovered.
    /// </summary>
    private List<string> GetAccountsToFetch()
    {
        var configured = _settings.GetCopilotAccounts();
        if (configured.Count > 0)
            return configured.ToList();

        // Auto-discover from gh CLI (thread-safe)
        lock (_accountsLock)
        {
            _cachedAccounts ??= DiscoverGhAccounts();
            return _cachedAccounts ?? [];
        }
    }

    /// <summary>
    /// Discovers all GitHub.com accounts from <c>gh auth status</c>.
    /// </summary>
    private List<string> DiscoverGhAccounts()
    {
        var accounts = new List<string>();
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    Arguments = "auth status --hostname github.com",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            // Read streams asynchronously to avoid deadlock: ReadToEnd() before WaitForExit()
            // can block indefinitely if the process produces enough output to fill the buffer.
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { /* best-effort */ }
                // Drain outstanding stream reads to prevent unobserved task exceptions
                try { stderrTask.GetAwaiter().GetResult(); } catch { /* best-effort after kill */ }
                try { stdoutTask.GetAwaiter().GetResult(); } catch { /* best-effort after kill */ }
                _logger.LogWarning("gh auth status timed out");
                return accounts;
            }

            var stderr = stderrTask.GetAwaiter().GetResult();
            stdoutTask.GetAwaiter().GetResult(); // drain stdout

            // Parse "Logged in to github.com account <username>"
            foreach (var line in stderr.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.Contains("Logged in to github.com account", StringComparison.OrdinalIgnoreCase))
                    continue;

                var idx = trimmed.IndexOf("account ", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var rest = trimmed[(idx + "account ".Length)..];
                var spaceIdx = rest.IndexOf(' ');
                var username = spaceIdx >= 0 ? rest[..spaceIdx] : rest;
                if (!string.IsNullOrWhiteSpace(username))
                    accounts.Add(username.Trim());
            }

            _logger.LogDebug("Discovered {Count} gh CLI accounts: {Accounts}",
                accounts.Count, string.Join(", ", accounts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover gh CLI accounts");
        }

        return accounts;
    }

    /// <summary>
    /// Fetches quota for a single account.
    /// </summary>
    private async Task<CopilotAccountResult> FetchAccountQuotaAsync(string username, CancellationToken ct)
    {
        var token = ResolveTokenForUser(username);
        if (token is null)
        {
            return new CopilotAccountResult
            {
                Username = username,
                Success = false,
                ErrorMessage = $"No token for '{username}'. Run 'gh auth login'."
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/copilot_internal/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Editor-Version", EditorVersion);
            request.Headers.Add("Editor-Plugin-Version", EditorPluginVersion);
            request.Headers.UserAgent.ParseAdd(UserAgentProduct);
            request.Headers.Add("X-Github-Api-Version", GitHubApiVersion);

            using var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<CopilotUserResponse>(json);

            if (data is null)
            {
                return new CopilotAccountResult
                {
                    Username = username,
                    Success = false,
                    ErrorMessage = "Empty API response"
                };
            }

            _logger.LogDebug("Copilot {User} ({Plan}): premium remaining={Remaining}/{Entitlement}",
                username, data.CopilotPlan ?? "unknown",
                data.QuotaSnapshots?.PremiumInteractions?.Remaining ?? 0,
                data.QuotaSnapshots?.PremiumInteractions?.Entitlement ?? 0);

            return new CopilotAccountResult
            {
                Username = username,
                Plan = data.CopilotPlan,
                Organizations = data.OrganizationLoginList?.AsReadOnly(),
                PremiumInteractions = data.QuotaSnapshots?.PremiumInteractions,
                Chat = data.QuotaSnapshots?.Chat,
                QuotaResetDateUtc = data.QuotaResetDateUtc,
                Success = true
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            InvalidateTokenForUser(username);
            return new CopilotAccountResult
            {
                Username = username,
                Success = false,
                ErrorMessage = "Token expired or invalid. Run 'gh auth login'."
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot fetch failed for {User}", username);
            return new CopilotAccountResult
            {
                Username = username,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Resolves a GitHub token for a specific user via <c>gh auth token --user</c>.
    /// </summary>
    private string? ResolveTokenForUser(string username)
    {
        if (_tokenCache.TryGetValue(username, out var cached))
            return cached;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add(username);

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read streams asynchronously to avoid deadlock when output fills the buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                _logger.LogWarning("gh auth token --user {User} timed out", username);
                try { process.Kill(); } catch { /* best-effort */ }
                // Drain outstanding stream reads to prevent unobserved task exceptions
                try { stdoutTask.GetAwaiter().GetResult(); } catch { /* best-effort after kill */ }
                try { stderrTask.GetAwaiter().GetResult(); } catch { /* best-effort after kill */ }
                return null;
            }

            stderrTask.GetAwaiter().GetResult(); // drain stderr

            if (process.ExitCode != 0)
            {
                stdoutTask.GetAwaiter().GetResult(); // drain stdout to prevent unobserved task exceptions
                return null;
            }

            var token = stdoutTask.GetAwaiter().GetResult().Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                _tokenCache[username] = token;
                _logger.LogDebug("Resolved token for {User} ({Length} chars)", username, token.Length);
                return token;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get token for {User}", username);
        }

        return null;
    }

    private void InvalidateTokenForUser(string username)
    {
        _tokenCache.TryRemove(username, out _);
        lock (_accountsLock)
        {
            _cachedAccounts = null;
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
                ErrorMessage = result.ErrorMessage
            };
        }

        var premium = result.PremiumInteractions;
        UsageSnapshot? primaryUsage = null;
        UsageSnapshot? secondaryUsage = null;

        if (premium is not null)
        {
            primaryUsage = BuildUsageSnapshot(premium, result.QuotaResetDateUtc, "premium");
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
            Success = true
        };
    }

    private static string FormatDisplayName(string username, string? plan)
    {
        var planLabel = plan switch
        {
            "enterprise" => "Ent",
            "individual_pro" => "Pro",
            "business" => "Biz",
            _ => plan?.Replace("_", " ")
        };

        return planLabel is not null ? $"Copilot · {username} ({planLabel})" : $"Copilot · {username}";
    }

    private static UsageSnapshot BuildUsageSnapshot(CopilotQuotaSnapshot premium, string? resetDateUtc, string quotaLabel = "premium")
    {
        double usedPercent;
        string usageLabel;

        if (premium.Unlimited)
        {
            usedPercent = 0;
            usageLabel = "Unlimited";
        }
        else if (premium.Entitlement <= 0)
        {
            usedPercent = 0;
            usageLabel = "No quota";
        }
        else
        {
            var used = premium.Entitlement - premium.Remaining;
            usedPercent = (double)used / premium.Entitlement;

            var overageRequests = ComputeOverageRequests(premium);
            if (overageRequests > 0)
            {
                var overageCost = overageRequests * OverageCostPerRequest;
                usageLabel = $"{used:N0} / {premium.Entitlement:N0} (+{overageRequests:N0} overage, ${overageCost:F2})";
            }
            else
            {
                usageLabel = $"{used:N0} / {premium.Entitlement:N0} {quotaLabel}";
            }
        }

        DateTimeOffset? resetsAt = null;
        string? resetDescription = null;
        if (resetDateUtc is not null && DateTimeOffset.TryParse(resetDateUtc, out var parsed))
        {
            resetsAt = parsed;
            var remaining = parsed - DateTimeOffset.UtcNow;
            resetDescription = remaining.TotalDays switch
            {
                < 0 => "Reset overdue",
                < 1 => $"Resets in {remaining.Hours}h {remaining.Minutes}m",
                < 2 => "Resets tomorrow",
                _ => $"Resets in {(int)remaining.TotalDays}d"
            };
        }

        return new UsageSnapshot
        {
            UsedPercent = Math.Clamp(usedPercent, 0.0, 1.0),
            UsageLabel = usageLabel,
            ResetsAt = resetsAt,
            ResetDescription = resetDescription
        };
    }

    private static int ComputeOverageRequests(CopilotQuotaSnapshot premium)
    {
        var overageByCount = Math.Max(0, premium.OverageCount);
        var overageByRemaining = Math.Max(0, -premium.Remaining);
        return Math.Max(overageByCount, overageByRemaining);
    }
}
