// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Services;

using System.Collections.ObjectModel;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Microsoft.Extensions.Logging;

/// <summary>
/// Coordinates periodic usage fetches across all enabled providers.
/// </summary>
public sealed class UsageRefreshService(
    IEnumerable<IUsageProvider> providers,
    ILogger<UsageRefreshService> logger) : IDisposable
{
    private readonly IReadOnlyList<IUsageProvider> providers = providers.ToList();
    private readonly ILogger<UsageRefreshService> logger = logger;
    private readonly object resultsLock = new();
    private readonly Dictionary<ProviderId, ProviderUsageResult> latestResults = [];
    private CancellationTokenSource? cts;
    private Task? refreshLoop;

    public event Action<ProviderId, ProviderUsageResult>? UsageUpdated;

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(2);

    public IReadOnlyDictionary<ProviderId, ProviderUsageResult> LatestResults
    {
        get
        {
            lock (this.resultsLock)
            {
                return new ReadOnlyDictionary<ProviderId, ProviderUsageResult>(
                    new Dictionary<ProviderId, ProviderUsageResult>(this.latestResults));
            }
        }
    }

    public void Start()
    {
        if (this.cts is not null)
        {
            return;
        }

        this.cts = new CancellationTokenSource();
        this.refreshLoop = this.RefreshLoopAsync(this.cts.Token);
        this.logger.LogInformation("Usage refresh service started with {Interval} interval", this.RefreshInterval);
    }

    public async Task StopAsync()
    {
        if (this.cts is null)
        {
            return;
        }

        await this.cts.CancelAsync();
        if (this.refreshLoop is not null)
        {
            try
            {
                await this.refreshLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.cts.Dispose();
        this.cts = null;
        this.logger.LogInformation("Usage refresh service stopped");
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var tasks = this.providers.Select(p => this.FetchSafeAsync(p, ct));
        await Task.WhenAll(tasks);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        // Initial fetch immediately
        await this.RefreshAllAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(this.RefreshInterval, ct);
                await this.RefreshAllAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task FetchSafeAsync(IUsageProvider provider, CancellationToken ct)
    {
        try
        {
            var available = await provider.IsAvailableAsync(ct);
            if (!available)
            {
                this.logger.LogDebug("{Provider} is not available, skipping", provider.Metadata.DisplayName);
                lock (this.resultsLock)
                {
                    this.latestResults.Remove(provider.Metadata.Id);
                }

                return;
            }

            var result = await provider.FetchUsageAsync(ct);
            lock (this.resultsLock)
            {
                this.latestResults[provider.Metadata.Id] = result;
            }

            this.RaiseUsageUpdated(provider.Metadata.Id, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Failed to fetch usage for {Provider}", provider.Metadata.DisplayName);
            var failure = ProviderUsageResult.Failure(provider.Metadata.Id, ex.Message);
            lock (this.resultsLock)
            {
                this.latestResults[provider.Metadata.Id] = failure;
            }

            this.RaiseUsageUpdated(provider.Metadata.Id, failure);
        }
    }

    private void RaiseUsageUpdated(ProviderId id, ProviderUsageResult result)
    {
        try
        {
            this.UsageUpdated?.Invoke(id, result);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "UsageUpdated subscriber threw for {Provider}", id);
        }
    }

    public void Dispose()
    {
        this.cts?.Cancel();
        this.cts?.Dispose();
    }
}
