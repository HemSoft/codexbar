// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Providers.OpenCodeZen;

using System.Net;
using System.Text.RegularExpressions;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches OpenCode Zen credit balance by scraping the workspace billing dashboard.
/// Auth: workspace ID + auth cookie from OpenCodeGo settings (shared credentials).
/// URL: https://opencode.ai/workspace/{workspaceId}/billing
/// Parses SolidJS SSR data: balance field (in nanodollars → dollars).
/// </summary>
public sealed partial class OpenCodeZenProvider(
    ILogger<OpenCodeZenProvider> logger,
    IHttpClientFactory httpClientFactory,
    ISettingsService settings) : IUsageProvider
{
    private readonly ILogger<OpenCodeZenProvider> logger = logger;
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly ISettingsService settings = settings;

    private const string DashboardPrefix = "https://opencode.ai/workspace/";
    private const string DashboardSuffix = "/billing";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim fetchLock = new(1, 1);
    private DateTimeOffset lastFetch = DateTimeOffset.MinValue;
    private decimal? cached;

    public ProviderMetadata Metadata { get; } = new()
    {
        Id = ProviderId.OpenCodeZen,
        DisplayName = "OpenCode Zen",
        Description = "OpenCode Zen — pay-as-you-go credits for verified AI models",
        DashboardUrl = "https://opencode.ai/auth",
        StatusPageUrl = null,
        SupportsSessionUsage = false,
        SupportsWeeklyUsage = false,
        SupportsCredits = true,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!this.settings.IsProviderEnabled(ProviderId.OpenCodeZen))
        {
            this.logger.LogInformation("OpenCode Zen: disabled in settings");
            return Task.FromResult(false);
        }

        var (workspaceId, authCookie) = this.ResolveCredentials();
        this.logger.LogInformation("OpenCode Zen: available check - workspaceId={Wid}, hasCookie={HasCookie}", workspaceId ?? "(null)", !string.IsNullOrWhiteSpace(authCookie));
        return Task.FromResult(
            !string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(authCookie));
    }

    public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
    {
        var (workspaceId, authCookie) = this.ResolveCredentials();
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return ProviderUsageResult.Failure(
                ProviderId.OpenCodeZen,
                "Configure OPENCODE_GO_WORKSPACE_ID (env var or openCodeGoWorkspaceId in settings.json).");
        }

        if (string.IsNullOrWhiteSpace(authCookie))
        {
            return ProviderUsageResult.Failure(
                ProviderId.OpenCodeZen,
                "Configure OPENCODE_GO_AUTH_COOKIE (env var or providers.OpenCodeGo.apiKey in settings.json).");
        }

        await this.fetchLock.WaitAsync(ct);
        try
        {
            // Serve cached result if still fresh
            if (this.cached is not null && DateTimeOffset.UtcNow - this.lastFetch < CacheTtl)
            {
                this.logger.LogDebug("OpenCode Zen: cached result (${Balance:F2})", this.cached);
                return BuildResult(this.cached.Value);
            }

            var url = $"{DashboardPrefix}{Uri.EscapeDataString(workspaceId)}{DashboardSuffix}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Gecko/20100101 Firefox/148.0");
            request.Headers.Add("Accept", "text/html");
            request.Headers.Add("Cookie", $"auth={authCookie}");

            using var httpClient = this.httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ProviderUsageResult.Failure(
                    ProviderId.OpenCodeZen,
                    "Auth cookie rejected. Refresh OPENCODE_GO_AUTH_COOKIE.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderUsageResult.Failure(
                    ProviderId.OpenCodeZen,
                    $"Dashboard returned HTTP {(int)response.StatusCode}.");
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var balance = ParseBalance(html);

            if (balance is null)
            {
                return ProviderUsageResult.Failure(
                    ProviderId.OpenCodeZen,
                    "Could not parse balance from OpenCode Zen dashboard. " +
                    "The dashboard markup may have changed.");
            }

            this.cached = balance;
            this.lastFetch = DateTimeOffset.UtcNow;
            this.logger.LogDebug("OpenCode Zen: ${Balance:F2} remaining", balance);

            return BuildResult(balance.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ProviderUsageResult.Failure(ProviderId.OpenCodeZen, "Dashboard request timed out.");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "OpenCode Zen fetch failed");
            return ProviderUsageResult.Failure(ProviderId.OpenCodeZen, ex.Message);
        }
        finally
        {
            this.fetchLock.Release();
        }
    }

    private static ProviderUsageResult BuildResult(decimal balance) =>
        new()
        {
            Provider = ProviderId.OpenCodeZen,
            Success = true,
            CreditsRemaining = balance,
        };

    /// <summary>
    /// Parses the balance from SolidJS SSR HTML.
    /// The billing page contains patterns like: balance:2000000000
    /// The value is in nanodollars — divide by 100,000,000 to get USD.
    /// </summary>
    private static decimal? ParseBalance(string html)
    {
        var match = BalanceRegex().Match(html);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var rawBalance))
        {
            return null;
        }

        // Convert from nanodollars to dollars (100,000,000 nanodollars = $1.00)
        return rawBalance / 100_000_000m;
    }

    /// <summary>
    /// Resolves credentials from env vars first, then settings.
    /// Shares the workspace ID and auth cookie with OpenCodeGo since they use the same dashboard.
    /// </summary>
    private (string? workspaceId, string? authCookie) ResolveCredentials()
    {
        var workspaceId =
            Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID")
            ?? this.settings.GetOpenCodeGoWorkspaceId();

        var authCookie =
            Environment.GetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE")
            ?? this.settings.GetApiKey(ProviderId.OpenCodeGo);

        // Also check for Zen-specific overrides
        var zenKey = this.settings.GetApiKey(ProviderId.OpenCodeZen);
        if (!string.IsNullOrWhiteSpace(zenKey))
        {
            authCookie = zenKey;
        }

        var zenWorkspaceEnv = Environment.GetEnvironmentVariable("OPENCODE_ZEN_WORKSPACE_ID");
        if (!string.IsNullOrWhiteSpace(zenWorkspaceEnv))
        {
            workspaceId = zenWorkspaceEnv;
        }

        var zenCookieEnv = Environment.GetEnvironmentVariable("OPENCODE_ZEN_AUTH_COOKIE");
        if (!string.IsNullOrWhiteSpace(zenCookieEnv))
        {
            authCookie = zenCookieEnv;
        }

        return (workspaceId, authCookie);
    }

    [GeneratedRegex(@"balance:(\d+)", RegexOptions.Compiled)]
    private static partial Regex BalanceRegex();
}