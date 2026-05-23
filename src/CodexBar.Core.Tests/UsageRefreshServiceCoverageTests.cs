// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Coverage tests targeting previously uncovered branches in UsageRefreshService:
/// Dispose catch blocks (OperationCanceledException/AggregateException),
/// provider availability transitions, and subscriber exception handling.
/// </summary>
public class UsageRefreshServiceCoverageTests
{
    [Fact]
    public void Dispose_WhileRunning_CompletesWithoutError()
    {
        var slow = new SlowProvider();
        var service = new UsageRefreshService([slow], NullLogger<UsageRefreshService>.Instance)
        {
            RefreshInterval = TimeSpan.FromMilliseconds(50),
        };

        service.Start();

        // Allow loop to begin executing
        Thread.Sleep(100);

        // Dispose while loop is running triggers OperationCanceledException catch (line 216-218)
        var ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_AfterStop_CompletesWithoutError()
    {
        var provider = new ControllableProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance)
        {
            RefreshInterval = TimeSpan.FromMilliseconds(50),
        };

        service.Start();
        await service.StopAsync();
        var ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_NotStarted_CompletesWithoutError()
    {
        var service = new UsageRefreshService([], NullLogger<UsageRefreshService>.Instance);
        var ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task RefreshAllAsync_SubscriberThrows_SwallowsException()
    {
        var provider = new ControllableProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
        service.UsageUpdated += (_, _) => throw new InvalidOperationException("subscriber boom");

        // Should not throw despite subscriber exception (lines 180-183)
        var ex = await Record.ExceptionAsync(() => service.RefreshAllAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task RefreshAllAsync_PreviouslyAvailableThenUnavailable_NotifiesUnavailable()
    {
        var provider = new ControllableProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);

        // First refresh: provider is available
        await service.RefreshAllAsync();
        Assert.True(service.LatestResults.ContainsKey(ProviderId.Claude));
        Assert.True(service.LatestResults[ProviderId.Claude].Success);

        // Switch provider to unavailable
        provider.SetAvailable(false);

        ProviderId? notifiedProvider = null;
        ProviderUsageResult? notifiedResult = null;
        service.UsageUpdated += (id, result) =>
        {
            notifiedProvider = id;
            notifiedResult = result;
        };

        // Second refresh: provider now unavailable → transition fires (lines 138-148)
        await service.RefreshAllAsync();
        Assert.Equal(ProviderId.Claude, notifiedProvider);
        Assert.NotNull(notifiedResult);
        Assert.False(notifiedResult!.Success);
    }

    [Fact]
    public async Task RefreshAllAsync_UnavailableProviderNeverPreviouslySeen_DoesNotNotify()
    {
        var provider = new ControllableProvider(available: false, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);

        bool notified = false;
        service.UsageUpdated += (_, _) => notified = true;

        await service.RefreshAllAsync();

        // No notification because provider was never available (no transition)
        Assert.False(notified);
        Assert.Empty(service.LatestResults);
    }

    [Fact]
    public async Task DisposeAsync_StopsService()
    {
        var provider = new ControllableProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance)
        {
            RefreshInterval = TimeSpan.FromMilliseconds(50),
        };

        service.Start();
        await service.DisposeAsync();

        // NextRefreshAtUtc should be cleared
        Assert.Null(service.NextRefreshAtUtc);
    }

    [Fact]
    public async Task NextRefreshChanged_SubscriberThrows_SwallowsException()
    {
        var provider = new ControllableProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance)
        {
            RefreshInterval = TimeSpan.FromMilliseconds(50),
        };

        service.NextRefreshChanged += _ => throw new InvalidOperationException("next refresh boom");

        service.Start();

        // Give the loop a moment to fire NextRefreshChanged
        await Task.Delay(150);

        // Should not have crashed
        var ex = await Record.ExceptionAsync(() => service.StopAsync());
        Assert.Null(ex);
    }

    private sealed class ControllableProvider(bool available, ProviderUsageResult result) : IUsageProvider
    {
        private bool _available = available;
        private readonly ProviderUsageResult _result = result;

        public ProviderMetadata Metadata => new() { Id = this._result.Provider, DisplayName = "Controllable", Description = "c" };

        public void SetAvailable(bool value) => this._available = value;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(this._available);

        public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default) => Task.FromResult(this._result);
    }

    private sealed class SlowProvider : IUsageProvider
    {
        public ProviderMetadata Metadata => new() { Id = ProviderId.Claude, DisplayName = "Slow", Description = "s" };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
        {
            await Task.Delay(5000, ct);
            return ProviderUsageResult.EmptySuccess(ProviderId.Claude);
        }
    }
}
