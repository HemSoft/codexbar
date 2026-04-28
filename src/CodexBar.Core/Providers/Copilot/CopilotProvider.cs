using System.Collections.Concurrent;
using System.ComponentModel;
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
    private readonly ISettingsService _settings;

    // Cached account list — refreshed on startup or auth failure
    private readonly SemaphoreSlim _accountsLock = new(1, 1);
    private List<string>? _cachedAccounts;
    private DateTime _emptyDiscoveryCachedUntil;
    private string? _lastDiscoveryError;
    private readonly ConcurrentDictionary<string, string> _tokenCache = new(StringComparer.OrdinalIgnoreCase);

    public CopilotProvider(ILogger<CopilotProvider> logger, IHttpClientFactory httpClientFactory, ISettingsService settings)
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
        // Note: IsAvailableAsync already gates on IsProviderEnabled — UsageRefreshService
        // skips disabled providers before calling FetchUsageAsync, so no redundant check here.

        var accounts = (await GetAccountsToFetchAsync(ct))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0)
            return ProviderUsageResult.Failure(ProviderId.Copilot,
                _lastDiscoveryError
                ?? "No Copilot accounts found. Add copilotAccounts to ~/.codexbar/settings.json or run 'gh auth login'.");

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
    private async Task<List<string>> GetAccountsToFetchAsync(CancellationToken ct)
    {
        var configured = _settings.GetCopilotAccounts();
        if (configured.Count > 0)
            return configured.ToList();

        // Auto-discover from gh CLI (async-safe via SemaphoreSlim)
        await _accountsLock.WaitAsync(ct);
        try
        {
            if (_cachedAccounts is null or { Count: 0 })
            {
                // Respect negative-result cache to avoid hammering gh when it's not installed/configured
                if (DateTime.UtcNow < _emptyDiscoveryCachedUntil)
                    return [];

                _cachedAccounts = await DiscoverGhAccountsAsync(ct);
            }

            // Cache empty results for 5 minutes to avoid repeated process spawning on persistent failures
            if (_cachedAccounts is { Count: 0 })
            {
                _emptyDiscoveryCachedUntil = DateTime.UtcNow.AddMinutes(5);
                _cachedAccounts = null;
            }

            return _cachedAccounts ?? [];
        }
        finally
        {
            _accountsLock.Release();
        }
    }

    /// <summary>
    /// Discovers all GitHub.com accounts from <c>gh auth status</c>.
    /// Uses async process handling so the calling thread is not blocked and
    /// the provided <paramref name="ct"/> can interrupt a stuck gh process.
    /// </summary>
    private async Task<List<string>> DiscoverGhAccountsAsync(CancellationToken ct)
    {
        _lastDiscoveryError = null;
        var accounts = new List<string>();

        try
        {
            using var process = StartGhAuthStatusProcess();
            var (exitCode, stderr, _) = await WaitForGhProcessAsync(process, TimeSpan.FromSeconds(10), ct);
            if (exitCode is null)
            {
                _lastDiscoveryError = "GitHub CLI (gh) timed out. Ensure 'gh' is responsive.";
                return accounts;
            }

            if (exitCode != 0)
            {
                _logger.LogWarning("gh auth status exited with code {ExitCode}: {Stderr}", exitCode, stderr.Trim());
                _lastDiscoveryError = $"GitHub CLI (gh) auth failed (exit code {exitCode}). Run 'gh auth login'.";
                return accounts;
            }

            accounts = ExtractUsernamesFromGhStatus(stderr);
            _logger.LogDebug("Discovered {Count} gh CLI accounts: {Accounts}", accounts.Count, string.Join(", ", accounts));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "GitHub CLI (gh) not found on PATH");
            _lastDiscoveryError = "GitHub CLI (gh) not found. Install from https://cli.github.com and run 'gh auth login'.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover gh CLI accounts");
            _lastDiscoveryError = $"Failed to discover accounts: {ex.Message}";
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
            }
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
            try { process.Kill(); } catch { }
            try { await stderrTask; } catch { }
            try { await stdoutTask; } catch { }

            if (ct.IsCancellationRequested)
                throw;

            return (null, string.Empty, string.Empty);
        }
    }

    internal static List<string> ExtractUsernamesFromGhStatus(string stderr)
    {
        var accounts = new List<string>();
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("Logged in to github.com", StringComparison.OrdinalIgnoreCase))
                continue;

            var username = ExtractUsername(trimmed);
            if (!string.IsNullOrWhiteSpace(username))
                accounts.Add(username.Trim());
        }
        return accounts;
    }

    internal static string? ExtractUsername(string line)
    {
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
        var token = await ResolveTokenForUserAsync(username, ct);
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
            await InvalidateTokenForUserAsync(username, ct);
            return new CopilotAccountResult
            {
                Username = username,
                Success = false,
                ErrorMessage = "Token expired or invalid. Run 'gh auth login'."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
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
    /// Resolves a GitHub token for a specific user via <c>gh auth token --user --hostname github.com</c>.
    /// Uses async process handling so the calling thread is not blocked and
    /// the provided <paramref name="ct"/> can interrupt a stuck gh process.
    /// </summary>
    private async Task<string?> ResolveTokenForUserAsync(string username, CancellationToken ct)
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
            psi.ArgumentList.Add("--hostname");
            psi.ArgumentList.Add("github.com");

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* best-effort */ }
                try { await stdoutTask; } catch { /* best-effort after kill */ }
                try { await stderrTask; } catch { /* best-effort after kill */ }

                if (ct.IsCancellationRequested)
                    throw; // Propagate caller cancellation after cleanup

                // Timeout only
                _logger.LogWarning("gh auth token --user {User} timed out", username);
                return null;
            }

            var stderrOutput = await stderrTask;

            if (process.ExitCode != 0)
            {
                await stdoutTask; // drain stdout
                var sanitizedStderr = string.IsNullOrWhiteSpace(stderrOutput)
                    ? "(no stderr)"
                    : stderrOutput.Trim().Length > 200
                        ? stderrOutput.Trim()[..200] + "…"
                        : stderrOutput.Trim();
                _logger.LogDebug("gh auth token --user {User} exited {Code}: {Stderr}",
                    username, process.ExitCode, sanitizedStderr);
                return null;
            }

            var token = (await stdoutTask).Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                _tokenCache[username] = token;
                _logger.LogDebug("Resolved token for {User} ({Length} chars)", username, token.Length);
                return token;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get token for {User}", username);
        }

        return null;
    }

    private async Task InvalidateTokenForUserAsync(string username, CancellationToken ct = default)
    {
        _tokenCache.TryRemove(username, out _);
        await _accountsLock.WaitAsync(ct);
        try
        {
            _cachedAccounts = null;
            _emptyDiscoveryCachedUntil = default;
        }
        finally
        {
            _accountsLock.Release();
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

    internal static string FormatDisplayName(string username, string? plan)
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

    internal static string FormatQuotaLabel(string quotaLabel) => quotaLabel switch
    {
        "premium" => "Premium interactions",
        "chat" => "Chat",
        _ => quotaLabel
    };

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
            IsUnlimited = isUnlimited
        };
    }

    internal static (double UsedPercent, string UsageLabel, bool IsUnlimited) ComputeUsageMetrics(CopilotQuotaSnapshot quota, string quotaLabel)
    {
        if (quota.Unlimited)
            return (0, "Unlimited", true);

        if (quota.Entitlement <= 0)
            return (0, "No quota", false);

        var used = Math.Clamp(quota.Entitlement - quota.Remaining, 0, quota.Entitlement);
        var usedPercent = (double)used / quota.Entitlement;
        var label = BuildUsageLabel(used, quota.Entitlement, quotaLabel, ComputeOverageRequests(quota), quota.OveragePermitted);
        return (usedPercent, label, false);
    }

    private static string BuildUsageLabel(int used, int entitlement, string quotaLabel, int overageRequests, bool overagePermitted)
    {
        var baseLabel = $"{used:N0} / {entitlement:N0} {FormatQuotaLabel(quotaLabel)}";
        if (overageRequests <= 0)
            return baseLabel;

        return overagePermitted
            ? $"{baseLabel} (+{overageRequests:N0} overage, ${overageRequests * OverageCostPerRequest:F2})"
            : $"{baseLabel} (over limit)";
    }

    internal static (DateTimeOffset? ResetsAt, string? ResetDescription) ParseReset(string? resetDateUtc)
    {
        if (resetDateUtc is null || !DateTimeOffset.TryParse(resetDateUtc, out var parsed))
            return (null, null);

        var remaining = parsed - DateTimeOffset.UtcNow;
        var description = remaining.TotalDays switch
        {
            < 0 => "Reset overdue",
            < 1 => $"Resets in {remaining.Hours}h {remaining.Minutes}m",
            < 2 => "Resets tomorrow",
            _ => $"Resets in {(int)remaining.TotalDays}d"
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
