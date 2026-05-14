// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

public class UsageRefreshServiceMoreTests
{
    private sealed class TestUsageProvider : IUsageProvider
    {
        private bool _available = true;
        private ProviderUsageResult? _result;

        public TestUsageProvider(ProviderId id, string displayName)
        {
            this.Metadata = new ProviderMetadata
            {
                Id = id,
                DisplayName = displayName,
                Description = $"{displayName} test provider",
            };
            this._result = ProviderUsageResult.EmptySuccess(id);
        }

        public ProviderMetadata Metadata { get; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(this._available);

        public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default)
            => Task.FromResult(this._result ?? ProviderUsageResult.EmptySuccess(this.Metadata.Id));

        public void SetAvailable(bool available) => this._available = available;

        public void SetResult(ProviderUsageResult result) => this._result = result;
    }

    private static UsageRefreshService CreateService(params IUsageProvider[] providers)
        => new(providers, NullLogger<UsageRefreshService>.Instance);

    [Fact]
    public void Dispose_WhenStarted_StopsLoop()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);
        service.Start();
        service.Dispose();
    }

    [Fact]
    public void Dispose_WhenNotStarted_DoesNotThrow()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);
        service.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_WhenStarted_StopsLoop()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);
        service.Start();
        await Task.Delay(50);
        await service.DisposeAsync();
    }

    [Fact]
    public void Start_CalledTwice_DoesNotDuplicateLoop()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);

        service.Start();
        var ctsAfterFirst = typeof(UsageRefreshService)
            .GetField("_cts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(service);

        service.Start();
        var ctsAfterSecond = typeof(UsageRefreshService)
            .GetField("_cts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(service);

        Assert.Same(ctsAfterFirst, ctsAfterSecond);
        service.Dispose();
    }

    [Fact]
    public async Task RefreshAllAsync_AvailableThenUnavailable_TransitionsToFailure()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);

        await service.RefreshAllAsync();
        Assert.True(service.LatestResults.ContainsKey(ProviderId.OpenRouter));
        Assert.True(service.LatestResults[ProviderId.OpenRouter].Success);

        provider.SetAvailable(false);
        await service.RefreshAllAsync();

        // Provider transitions to unavailable → failure result replaces success
        Assert.True(service.LatestResults.ContainsKey(ProviderId.OpenRouter));
        Assert.False(service.LatestResults[ProviderId.OpenRouter].Success);
    }

    [Fact]
    public async Task UsageUpdated_SubscriberThrows_DoesNotCrashService()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);

        var eventFired = false;
        service.UsageUpdated += (_, _) =>
        {
            eventFired = true;
            throw new InvalidOperationException("Subscriber exception");
        };

        await service.RefreshAllAsync();
        Assert.True(eventFired);
    }

    [Fact]
    public async Task NextRefreshChanged_SubscriberThrows_DoesNotCrashService()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);

        var eventFired = false;
        service.NextRefreshChanged += _ =>
        {
            eventFired = true;
            throw new InvalidOperationException("Subscriber exception");
        };

        service.Start();
        await Task.Delay(200);
        await service.StopAsync();
        Assert.True(eventFired);
    }

    [Fact]
    public async Task StopAsync_SetsNextRefreshToNull()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);

        service.Start();
        await Task.Delay(200);
        Assert.NotNull(service.NextRefreshAtUtc);

        await service.StopAsync();
        Assert.Null(service.NextRefreshAtUtc);
    }

    [Fact]
    public async Task RefreshAllAsync_MultipleProviders_FetchesAll()
    {
        var p1 = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var p2 = new TestUsageProvider(ProviderId.Copilot, "Copilot");
        var p3 = new TestUsageProvider(ProviderId.Claude, "Claude");
        var service = CreateService(p1, p2, p3);

        await service.RefreshAllAsync();

        Assert.Equal(3, service.LatestResults.Count);
        Assert.Contains(ProviderId.OpenRouter, service.LatestResults.Keys);
        Assert.Contains(ProviderId.Copilot, service.LatestResults.Keys);
        Assert.Contains(ProviderId.Claude, service.LatestResults.Keys);
    }

    [Fact]
    public async Task StopAsync_CalledWithoutStart_DoesNotThrow()
    {
        var provider = new TestUsageProvider(ProviderId.OpenRouter, "OpenRouter");
        var service = CreateService(provider);
        await service.StopAsync();
    }
}
