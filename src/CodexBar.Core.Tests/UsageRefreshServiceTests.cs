// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class UsageRefreshServiceTests
{
    [Fact]
    public void Constructor_WithNoProviders_Succeeds()
    {
        var service = new UsageRefreshService([], NullLogger<UsageRefreshService>.Instance);
        Assert.NotNull(service.LatestResults);
        Assert.Empty(service.LatestResults);
    }

    [Fact]
    public void LatestResults_InitiallyEmpty()
    {
        var provider = new DummyProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
        Assert.Empty(service.LatestResults);
    }

    [Fact]
    public async Task RefreshAllAsync_InvokesFetchForAvailableProvider()
    {
        var provider = new DummyProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.Claude));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        Assert.Single(service.LatestResults);
        Assert.True(service.LatestResults[ProviderId.Claude].Success);
    }

    [Fact]
    public async Task RefreshAllAsync_SkipsUnavailableProvider()
    {
        var provider = new DummyProvider(available: false, result: ProviderUsageResult.EmptySuccess(ProviderId.Copilot));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        Assert.Empty(service.LatestResults);
    }

    [Fact]
    public async Task RefreshAllAsync_CapturesFailureResult()
    {
        var provider = new ThrowingProvider();
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        Assert.Single(service.LatestResults);
        Assert.False(service.LatestResults[ProviderId.OpenRouter].Success);
    }

    [Fact]
    public async Task StartStop_DoesNotThrow()
    {
        var service = new UsageRefreshService([], NullLogger<UsageRefreshService>.Instance);
        service.Start();
        await service.StopAsync();
    }

    [Fact]
    public async Task UsageUpdated_EventFires()
    {
        var provider = new DummyProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.OpenCodeGo));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
        ProviderId? firedId = null;
        service.UsageUpdated += (id, _) => firedId = id;

        await service.RefreshAllAsync();

        Assert.Equal(ProviderId.OpenCodeGo, firedId);
    }

    [Fact]
    public async Task Start_SchedulesNextAutomaticRefresh()
    {
        var provider = new DummyProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.OpenRouter));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance)
        {
            RefreshInterval = TimeSpan.FromMilliseconds(50),
        };
        var tcs = new TaskCompletionSource<DateTimeOffset?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NextRefreshChanged += nextRefreshAtUtc =>
        {
            if (nextRefreshAtUtc is not null)
            {
                tcs.TrySetResult(nextRefreshAtUtc);
            }
        };

        service.Start();
        var nextRefreshAtUtc = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(nextRefreshAtUtc.HasValue);
        Assert.True(nextRefreshAtUtc.Value > DateTimeOffset.UtcNow);

        await service.StopAsync();
    }

    [Fact]
    public async Task RefreshAllAsync_DoesNotSetNextAutomaticRefreshWhenNotRunning()
    {
        var provider = new DummyProvider(available: true, result: ProviderUsageResult.EmptySuccess(ProviderId.OpenRouter));
        var service = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        Assert.Null(service.NextRefreshAtUtc);
    }

    private sealed class DummyProvider(bool available, ProviderUsageResult result) : IUsageProvider
    {
        private readonly bool available = available;
        private readonly ProviderUsageResult result = result;

        public ProviderMetadata Metadata => new() { Id = this.result.Provider, DisplayName = "Dummy", Description = "d" };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(this.available);

        public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default) => Task.FromResult(this.result);
    }

    private sealed class ThrowingProvider : IUsageProvider
    {
        public ProviderMetadata Metadata => new() { Id = ProviderId.OpenRouter, DisplayName = "Thrower", Description = "t" };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }
}
