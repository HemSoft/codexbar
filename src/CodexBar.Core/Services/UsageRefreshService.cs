// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Services;

using System.Collections.ObjectModel;
using System.Threading;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Microsoft.Extensions.Logging;

/// <summary>
/// Coordinates periodic usage fetches across all enabled providers.
/// </summary>
public sealed class UsageRefreshService(
    IEnumerable<IUsageProvider> providers,
    ILogger<UsageRefreshService> logger) : IDisposable, IAsyncDisposable
{
    private readonly IReadOnlyList<IUsageProvider> _providers = providers.ToList();
    private readonly ILogger<UsageRefreshService> _logger = logger;
    private readonly object _resultsLock = new();
    private readonly Dictionary<ProviderId, ProviderUsageResult> _latestResults = [];
    private CancellationTokenSource? _cts;
    private Task? _refreshLoop;

    public event Action<ProviderId, ProviderUsageResult>? UsageUpdated;

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(2);

    public IReadOnlyDictionary<ProviderId, ProviderUsageResult> LatestResults
    {
        get
        {
            lock (this._resultsLock)
            {
                return new ReadOnlyDictionary<ProviderId, ProviderUsageResult>(
                    new Dictionary<ProviderId, ProviderUsageResult>(this._latestResults));
            }
        }
    }

    public void Start()
    {
        if (this._cts is not null)
        {
            return;
        }

        this._cts = new CancellationTokenSource();
        this._refreshLoop = this.RefreshLoopAsync(this._cts.Token);
        this._logger.LogInformation("Usage refresh service started with {Interval} interval", this.RefreshInterval);
    }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref this._cts, null);
        var refreshLoop = Interlocked.Exchange(ref this._refreshLoop, null);
        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync();
        if (refreshLoop is not null)
        {
            try
            {
                await refreshLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        this._logger.LogInformation("Usage refresh service stopped");
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var tasks = this._providers.Select(p => this.FetchSafeAsync(p, ct));
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
                this._logger.LogDebug("{Provider} is not available, skipping", provider.Metadata.DisplayName);
                ProviderUsageResult? removed = null;
                lock (this._resultsLock)
                {
                    if (this._latestResults.Remove(provider.Metadata.Id, out var old))
                    {
                        removed = old;
                    }
                }

                if (removed is not null)
                {
                    // State changed from available → unavailable; notify UI so it clears stale data
                    var unavailableResult = ProviderUsageResult.Failure(provider.Metadata.Id, "Provider unavailable");
                    lock (this._resultsLock)
                    {
                        this._latestResults[provider.Metadata.Id] = unavailableResult;
                    }

                    this.RaiseUsageUpdated(provider.Metadata.Id, unavailableResult);
                }

                return;
            }

            var result = await provider.FetchUsageAsync(ct);
            lock (this._resultsLock)
            {
                this._latestResults[provider.Metadata.Id] = result;
            }

            this.RaiseUsageUpdated(provider.Metadata.Id, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this._logger.LogWarning(ex, "Failed to fetch usage for {Provider}", provider.Metadata.DisplayName);
            var failure = ProviderUsageResult.Failure(provider.Metadata.Id, ex.Message);
            lock (this._resultsLock)
            {
                this._latestResults[provider.Metadata.Id] = failure;
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
            this._logger.LogWarning(ex, "UsageUpdated subscriber threw for {Provider}", id);
        }
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref this._cts, null);
        var refreshLoop = Interlocked.Exchange(ref this._refreshLoop, null);
        if (cts is not null)
        {
            cts.Cancel();
            if (refreshLoop is not null)
            {
                try
                {
                    refreshLoop.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                catch (AggregateException)
                {
                    // Swallow any remaining exceptions during disposal
                }
            }

            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync();
    }
}
