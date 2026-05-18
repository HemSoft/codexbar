// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

/// <summary>
/// Mutation-killing tests for UsageRefreshService:
/// FetchSafe transitions, event firing, Start/Stop idempotency, and timer logic.
/// </summary>
public class UsageRefreshServiceMutationTests : IAsyncDisposable
{
    private readonly UsageRefreshService _sut;
    private readonly IUsageProvider _provider1;
    private readonly IUsageProvider _provider2;

    public UsageRefreshServiceMutationTests()
    {
        this._provider1 = Substitute.For<IUsageProvider>();
        this._provider1.Metadata.Returns(new ProviderMetadata
        {
            Id = ProviderId.Copilot,
            DisplayName = "Copilot",
            Description = "GitHub Copilot usage",
        });
        this._provider1.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        this._provider1.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(new ProviderUsageResult { Provider = ProviderId.Copilot, Success = true });

        this._provider2 = Substitute.For<IUsageProvider>();
        this._provider2.Metadata.Returns(new ProviderMetadata
        {
            Id = ProviderId.Claude,
            DisplayName = "Claude",
            Description = "Anthropic Claude usage",
        });
        this._provider2.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        this._provider2.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(new ProviderUsageResult { Provider = ProviderId.Claude, Success = true });

        this._sut = new UsageRefreshService(
            [this._provider1, this._provider2],
            NullLogger<UsageRefreshService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this._sut.DisposeAsync();
    }

    // === RefreshAllAsync ===
    [Fact]
    public async Task RefreshAllAsync_FetchesBothProviders()
    {
        await this._sut.RefreshAllAsync();

        await this._provider1.Received(1).FetchUsageAsync(Arg.Any<CancellationToken>());
        await this._provider2.Received(1).FetchUsageAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAllAsync_StoresResults()
    {
        await this._sut.RefreshAllAsync();

        var results = this._sut.LatestResults;
        Assert.True(results.ContainsKey(ProviderId.Copilot));
        Assert.True(results.ContainsKey(ProviderId.Claude));
    }

    [Fact]
    public async Task RefreshAllAsync_RaisesUsageUpdatedForEachProvider()
    {
        var updated = new List<ProviderId>();
        this._sut.UsageUpdated += (id, _) => updated.Add(id);

        await this._sut.RefreshAllAsync();

        Assert.Contains(ProviderId.Copilot, updated);
        Assert.Contains(ProviderId.Claude, updated);
    }

    // === FetchSafe: unavailable provider ===
    [Fact]
    public async Task RefreshAllAsync_UnavailableProvider_SkipsAndDoesNotStore()
    {
        this._provider1.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        await this._sut.RefreshAllAsync();

        await this._provider1.DidNotReceive().FetchUsageAsync(Arg.Any<CancellationToken>());
        Assert.False(this._sut.LatestResults.ContainsKey(ProviderId.Copilot));
    }

    [Fact]
    public async Task RefreshAllAsync_AvailableBecomesUnavailable_RaisesUnavailableResult()
    {
        // First: available
        await this._sut.RefreshAllAsync();
        Assert.True(this._sut.LatestResults.ContainsKey(ProviderId.Copilot));

        // Then: becomes unavailable
        this._provider1.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        var updated = new List<(ProviderId Id, ProviderUsageResult Result)>();
        this._sut.UsageUpdated += (id, result) => updated.Add((id, result));

        await this._sut.RefreshAllAsync();

        var copilotUpdate = updated.Find(u => u.Id == ProviderId.Copilot);
        Assert.NotNull(copilotUpdate.Result);
        Assert.False(copilotUpdate.Result.Success);
        Assert.Contains("unavailable", copilotUpdate.Result.ErrorMessage);
    }

    // === FetchSafe: exception handling ===
    [Fact]
    public async Task RefreshAllAsync_ProviderThrows_StoresFailure()
    {
        this._provider1.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("test error"));

        await this._sut.RefreshAllAsync();

        var results = this._sut.LatestResults;
        Assert.True(results.ContainsKey(ProviderId.Copilot));
        Assert.False(results[ProviderId.Copilot].Success);
        Assert.Contains("test error", results[ProviderId.Copilot].ErrorMessage);
    }

    [Fact]
    public async Task RefreshAllAsync_ProviderThrows_RaisesUsageUpdatedWithFailure()
    {
        this._provider1.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        var received = new List<ProviderUsageResult>();
        this._sut.UsageUpdated += (id, result) =>
        {
            if (id == ProviderId.Copilot)
            {
                received.Add(result);
            }
        };

        await this._sut.RefreshAllAsync();

        Assert.Single(received);
        Assert.False(received[0].Success);
    }

    [Fact]
    public async Task RefreshAllAsync_AvailabilityCheckThrows_TreatedAsException()
    {
        this._provider1.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("availability error"));

        await this._sut.RefreshAllAsync();

        var results = this._sut.LatestResults;
        Assert.True(results.ContainsKey(ProviderId.Copilot));
        Assert.False(results[ProviderId.Copilot].Success);
    }

    // === Start/Stop ===
    [Fact]
    public void Start_SetsNextRefreshAtUtc()
    {
        this._sut.RefreshInterval = TimeSpan.FromMinutes(5);
        this._sut.Start();

        // Give it a moment to complete the initial fetch
        Thread.Sleep(200);
        Assert.NotNull(this._sut.NextRefreshAtUtc);
    }

    [Fact]
    public void Start_CalledTwice_DoesNotThrow()
    {
        this._sut.Start();
        this._sut.Start();
    }

    [Fact]
    public async Task StopAsync_ClearsNextRefreshAtUtc()
    {
        this._sut.Start();
        Thread.Sleep(200);
        await this._sut.StopAsync();

        Assert.Null(this._sut.NextRefreshAtUtc);
    }

    [Fact]
    public async Task StopAsync_WhenNotStarted_DoesNotThrow()
    {
        await this._sut.StopAsync();
    }

    // === NextRefreshChanged event ===
    [Fact]
    public async Task StopAsync_RaisesNextRefreshChangedWithNull()
    {
        this._sut.Start();
        Thread.Sleep(200);

        DateTimeOffset? lastValue = DateTimeOffset.UtcNow;
        this._sut.NextRefreshChanged += value => lastValue = value;

        await this._sut.StopAsync();

        Assert.Null(lastValue);
    }

    [Fact]
    public void Start_RaisesNextRefreshChanged()
    {
        DateTimeOffset? received = null;
        this._sut.NextRefreshChanged += value => received = value;
        this._sut.RefreshInterval = TimeSpan.FromMinutes(5);

        this._sut.Start();
        Thread.Sleep(300);

        Assert.NotNull(received);
    }

    // === RefreshInterval ===
    [Fact]
    public void RefreshInterval_DefaultIsTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), this._sut.RefreshInterval);
    }

    // === LatestResults is snapshot ===
    [Fact]
    public async Task LatestResults_ReturnsCopy()
    {
        await this._sut.RefreshAllAsync();
        var snapshot1 = this._sut.LatestResults;
        var snapshot2 = this._sut.LatestResults;
        Assert.NotSame(snapshot1, snapshot2);
    }

    // === UsageUpdated event handler throws ===
    [Fact]
    public async Task RefreshAllAsync_UsageUpdatedHandlerThrows_DoesNotCrash()
    {
        this._sut.UsageUpdated += (_, _) => throw new InvalidOperationException("handler boom");

        // Should not throw
        await this._sut.RefreshAllAsync();
    }

    // === Dispose ===
    [Fact]
    public void Dispose_ClearsNextRefreshAtUtc()
    {
        this._sut.Start();
        Thread.Sleep(200);
        this._sut.Dispose();
        Assert.Null(this._sut.NextRefreshAtUtc);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        this._sut.Start();
        this._sut.Dispose();
        this._sut.Dispose();
    }
}
