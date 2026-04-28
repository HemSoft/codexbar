using System;
using System.Threading;
using System.Threading.Tasks;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexBar.Core.Tests;

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

    private sealed class DummyProvider : IUsageProvider
    {
        private readonly bool _available;
        private readonly ProviderUsageResult _result;

        public DummyProvider(bool available, ProviderUsageResult result)
        {
            _available = available;
            _result = result;
        }

        public ProviderMetadata Metadata => new() { Id = _result.Provider, DisplayName = "Dummy", Description = "d" };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(_available);

        public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default) => Task.FromResult(_result);
    }

    private sealed class ThrowingProvider : IUsageProvider
    {
        public ProviderMetadata Metadata => new() { Id = ProviderId.OpenRouter, DisplayName = "Thrower", Description = "t" };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }
}
