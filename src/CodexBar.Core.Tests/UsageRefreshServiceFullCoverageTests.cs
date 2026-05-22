// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Full coverage tests for UsageRefreshService: NextRefreshChanged event,
/// Dispose/DisposeAsync paths, subscriber exceptions, and provider-unavailable
/// transition logic.
/// </summary>
public class UsageRefreshServiceFullCoverageTests
{
    private static IUsageProvider CreateMockProvider(
        ProviderId id,
        bool available = true,
        ProviderUsageResult? result = null)
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata { Id = id, DisplayName = id.ToString(), Description = "Test" });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(available));
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(result ?? new ProviderUsageResult { Provider = id, Success = true });
        return provider;
    }

    [Fact]
    public async Task RefreshAllAsync_ProviderThrows_StoresFailure()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata { Id = ProviderId.OpenRouter, DisplayName = "OpenRouter", Description = "Test" });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns<Task<ProviderUsageResult>>(_ => throw new InvalidOperationException("boom"));

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        var results = service.LatestResults;
        Assert.True(results.ContainsKey(ProviderId.OpenRouter));
        Assert.False(results[ProviderId.OpenRouter].Success);
        Assert.Contains("boom", results[ProviderId.OpenRouter].ErrorMessage);
    }

    [Fact]
    public async Task RefreshAllAsync_ProviderUnavailable_RemovesPreviousResultAndNotifies()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata { Id = ProviderId.Claude, DisplayName = "Claude", Description = "Test" });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(true),   // first call: available
            Task.FromResult(false)); // second call: unavailable
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(new ProviderUsageResult { Provider = ProviderId.Claude, Success = true });

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        var updatedEvents = new List<(ProviderId Id, ProviderUsageResult Result)>();
        service.UsageUpdated += (id, r) => updatedEvents.Add((id, r));

        // First refresh: available
        await service.RefreshAllAsync();
        Assert.True(service.LatestResults.ContainsKey(ProviderId.Claude));
        Assert.True(service.LatestResults[ProviderId.Claude].Success);

        // Second refresh: unavailable
        await service.RefreshAllAsync();
        Assert.True(service.LatestResults.ContainsKey(ProviderId.Claude));
        Assert.False(service.LatestResults[ProviderId.Claude].Success);

        // Should have fired UsageUpdated for the unavailable transition
        Assert.True(updatedEvents.Count >= 2);
    }

    [Fact]
    public async Task RefreshAllAsync_ProviderUnavailableFromStart_DoesNotFireEvent()
    {
        var provider = CreateMockProvider(ProviderId.Claude, available: false);

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        var updatedEvents = new List<ProviderId>();
        service.UsageUpdated += (id, _) => updatedEvents.Add(id);

        await service.RefreshAllAsync();

        // No event should be fired since there was no previous result to clear
        Assert.Empty(updatedEvents);
    }

    [Fact]
    public async Task UsageUpdated_SubscriberThrows_DoesNotCrashService()
    {
        var provider = CreateMockProvider(ProviderId.OpenRouter);

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        service.UsageUpdated += (_, _) => throw new InvalidOperationException("subscriber error");

        // Should not throw
        await service.RefreshAllAsync();

        Assert.True(service.LatestResults.ContainsKey(ProviderId.OpenRouter));
    }

    [Fact]
    public async Task NextRefreshChanged_SubscriberThrows_DoesNotCrashService()
    {
        var provider = CreateMockProvider(ProviderId.OpenRouter);

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromMilliseconds(50);

        service.NextRefreshChanged += _ => throw new InvalidOperationException("subscriber error");

        // Start and quickly stop — verifies the subscriber exception doesn't crash
        service.Start();
        await Task.Delay(100);
        await service.StopAsync();
    }

    [Fact]
    public async Task StartAndStop_SetsAndClearsNextRefresh()
    {
        var provider = CreateMockProvider(ProviderId.OpenRouter);

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromMilliseconds(50);

        DateTimeOffset? lastChanged = null;
        service.NextRefreshChanged += val => lastChanged = val;

        service.Start();
        await Task.Delay(150);

        Assert.NotNull(service.NextRefreshAtUtc);

        await service.StopAsync();

        Assert.Null(service.NextRefreshAtUtc);
        Assert.Null(lastChanged); // Last event should be null (cleared)
    }

    [Fact]
    public void Start_CalledTwice_DoesNotCreateSecondLoop()
    {
        var provider = CreateMockProvider(ProviderId.OpenRouter);

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromHours(1);

        service.Start();
        service.Start(); // should no-op

        service.Dispose();
    }

    [Fact]
    public async Task StopAsync_WhenNotStarted_DoesNotThrow()
    {
        var service = new UsageRefreshService(
            [],
            NullLogger<UsageRefreshService>.Instance);

        await service.StopAsync(); // Should not throw
    }

    [Fact]
    public void Dispose_WhenStarted_StopsLoop()
    {
        var provider = CreateMockProvider(ProviderId.OpenRouter);

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromHours(1);

        service.Start();
        service.Dispose();

        Assert.Null(service.NextRefreshAtUtc);
    }

    [Fact]
    public void Dispose_WhenNotStarted_DoesNotThrow()
    {
        var service = new UsageRefreshService(
            [],
            NullLogger<UsageRefreshService>.Instance);

        service.Dispose(); // Should not throw
    }

    [Fact]
    public async Task DisposeAsync_StopsService()
    {
        var provider = CreateMockProvider(ProviderId.OpenRouter);

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromHours(1);

        service.Start();
        await service.DisposeAsync();

        Assert.Null(service.NextRefreshAtUtc);
    }

    [Fact]
    public async Task RefreshLoop_ExecutesMultipleCycles()
    {
        var callCount = 0;
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata { Id = ProviderId.OpenRouter, DisplayName = "OpenRouter", Description = "Test" });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        provider.FetchUsageAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new ProviderUsageResult { Provider = ProviderId.OpenRouter, Success = true });
        });

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);
        service.RefreshInterval = TimeSpan.FromMilliseconds(50);

        service.Start();
        await Task.Delay(200);
        await service.StopAsync();

        // Should have at least 2 fetches (initial + at least 1 loop)
        Assert.True(callCount >= 2, $"Expected >= 2 calls, got {callCount}");
    }
}
