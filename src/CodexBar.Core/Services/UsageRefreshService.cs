using System.Collections.ObjectModel;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Services;

/// <summary>
/// Coordinates periodic usage fetches across all enabled providers.
/// </summary>
public sealed class UsageRefreshService : IDisposable
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly ILogger<UsageRefreshService> _logger;
    private readonly object _resultsLock = new();
    private readonly Dictionary<ProviderId, ProviderUsageResult> _latestResults = new();
    private CancellationTokenSource? _cts;
    private Task? _refreshLoop;

    public event Action<ProviderId, ProviderUsageResult>? UsageUpdated;

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(2);

    public UsageRefreshService(
        IEnumerable<IUsageProvider> providers,
        ILogger<UsageRefreshService> logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public IReadOnlyDictionary<ProviderId, ProviderUsageResult> LatestResults
    {
        get
        {
            lock (_resultsLock)
            {
                return new ReadOnlyDictionary<ProviderId, ProviderUsageResult>(
                    new Dictionary<ProviderId, ProviderUsageResult>(_latestResults));
            }
        }
    }

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _refreshLoop = RefreshLoopAsync(_cts.Token);
        _logger.LogInformation("Usage refresh service started with {Interval} interval", RefreshInterval);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync();
        if (_refreshLoop is not null)
        {
            try { await _refreshLoop; }
            catch (OperationCanceledException) { }
        }

        _cts.Dispose();
        _cts = null;
        _logger.LogInformation("Usage refresh service stopped");
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var tasks = _providers.Select(p => FetchSafeAsync(p, ct));
        await Task.WhenAll(tasks);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        // Initial fetch immediately
        await RefreshAllAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, ct);
                await RefreshAllAsync(ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FetchSafeAsync(IUsageProvider provider, CancellationToken ct)
    {
        try
        {
            var available = await provider.IsAvailableAsync(ct);
            if (!available)
            {
                _logger.LogDebug("{Provider} is not available, skipping", provider.Metadata.DisplayName);
                lock (_resultsLock)
                {
                    _latestResults.Remove(provider.Metadata.Id);
                }
                return;
            }

            var result = await provider.FetchUsageAsync(ct);
            lock (_resultsLock)
            {
                _latestResults[provider.Metadata.Id] = result;
            }
            RaiseUsageUpdated(provider.Metadata.Id, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch usage for {Provider}", provider.Metadata.DisplayName);
            var failure = ProviderUsageResult.Failure(provider.Metadata.Id, ex.Message);
            lock (_resultsLock)
            {
                _latestResults[provider.Metadata.Id] = failure;
            }
            RaiseUsageUpdated(provider.Metadata.Id, failure);
        }
    }

    private void RaiseUsageUpdated(ProviderId id, ProviderUsageResult result)
    {
        try
        {
            UsageUpdated?.Invoke(id, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UsageUpdated subscriber threw for {Provider}", id);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
