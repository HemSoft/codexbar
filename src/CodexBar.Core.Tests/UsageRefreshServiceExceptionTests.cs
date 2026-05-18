// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

/// <summary>
/// Tests for UsageRefreshService exception handling in FetchSafeAsync.
/// Covers the catch branch for non-cancellation exceptions thrown by providers.
/// </summary>
public class UsageRefreshServiceExceptionTests
{
    private static IUsageProvider CreateThrowingProvider(ProviderId id, Exception exception)
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata
        {
            Id = id,
            DisplayName = id.ToString(),
            Description = $"Test {id}",
            SupportsSessionUsage = false,
            SupportsWeeklyUsage = false,
            SupportsCredits = false,
        });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(exception);
        return provider;
    }

    private static IUsageProvider CreateIsAvailableThrowingProvider(ProviderId id, Exception exception)
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata
        {
            Id = id,
            DisplayName = id.ToString(),
            Description = $"Test {id}",
            SupportsSessionUsage = false,
            SupportsWeeklyUsage = false,
            SupportsCredits = false,
        });
        provider.IsAvailableAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(exception);
        return provider;
    }

    [Fact]
    public async Task RefreshAllAsync_ProviderFetchThrows_CapturesFailureResult()
    {
        var provider = CreateThrowingProvider(
            ProviderId.OpenRouter,
            new InvalidOperationException("Provider crashed"));

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        ProviderUsageResult? captured = null;
        service.UsageUpdated += (id, result) => captured = result;

        await service.RefreshAllAsync();

        Assert.NotNull(captured);
        Assert.False(captured!.Success);
        Assert.Equal(ProviderId.OpenRouter, captured.Provider);
        Assert.Contains("Provider crashed", captured.ErrorMessage);
    }

    [Fact]
    public async Task RefreshAllAsync_ProviderFetchThrows_StoresInLatestResults()
    {
        var provider = CreateThrowingProvider(
            ProviderId.Claude,
            new HttpRequestException("Network error"));

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        var latest = service.LatestResults;
        Assert.True(latest.ContainsKey(ProviderId.Claude));
        Assert.False(latest[ProviderId.Claude].Success);
        Assert.Contains("Network error", latest[ProviderId.Claude].ErrorMessage);
    }

    [Fact]
    public async Task RefreshAllAsync_IsAvailableThrows_CapturesFailureResult()
    {
        var provider = CreateIsAvailableThrowingProvider(
            ProviderId.OpenCodeGo,
            new TimeoutException("Check timed out"));

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        ProviderUsageResult? captured = null;
        service.UsageUpdated += (id, result) => captured = result;

        await service.RefreshAllAsync();

        Assert.NotNull(captured);
        Assert.False(captured!.Success);
        Assert.Equal(ProviderId.OpenCodeGo, captured.Provider);
        Assert.Contains("Check timed out", captured.ErrorMessage);
    }

    [Fact]
    public async Task RefreshAllAsync_MultipleProviders_OneThrows_OtherSucceeds()
    {
        var throwingProvider = CreateThrowingProvider(
            ProviderId.OpenRouter,
            new InvalidOperationException("Boom"));

        var successProvider = Substitute.For<IUsageProvider>();
        successProvider.Metadata.Returns(new ProviderMetadata
        {
            Id = ProviderId.Copilot,
            DisplayName = "Copilot",
            Description = "Test",
            SupportsSessionUsage = false,
            SupportsWeeklyUsage = false,
            SupportsCredits = false,
        });
        successProvider.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        successProvider.FetchUsageAsync(Arg.Any<CancellationToken>())
            .Returns(ProviderUsageResult.EmptySuccess(ProviderId.Copilot));

        var service = new UsageRefreshService(
            [throwingProvider, successProvider],
            NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        var latest = service.LatestResults;
        Assert.Equal(2, latest.Count);
        Assert.False(latest[ProviderId.OpenRouter].Success);
        Assert.True(latest[ProviderId.Copilot].Success);
    }

    [Fact]
    public async Task RefreshAllAsync_ProviderThrows_DoesNotSetNextRefresh()
    {
        var provider = CreateThrowingProvider(
            ProviderId.Claude,
            new Exception("Unexpected"));

        var service = new UsageRefreshService(
            [provider],
            NullLogger<UsageRefreshService>.Instance);

        await service.RefreshAllAsync();

        // NextRefreshAtUtc is only set by the refresh loop, not by RefreshAllAsync directly
        Assert.Null(service.NextRefreshAtUtc);
    }
}
