// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;

/// <summary>
/// Mutation-killing tests for ProviderUsageResult factory methods and record properties.
/// </summary>
public class ProviderUsageResultMutationTests
{
    // === Failure factory ===
    [Fact]
    public void Failure_SetsProviderCorrectly()
    {
        var result = ProviderUsageResult.Failure(ProviderId.Claude, "error");
        Assert.Equal(ProviderId.Claude, result.Provider);
    }

    [Fact]
    public void Failure_SetsSuccessToFalse()
    {
        var result = ProviderUsageResult.Failure(ProviderId.Copilot, "error");
        Assert.False(result.Success);
    }

    [Fact]
    public void Failure_SetsErrorMessage()
    {
        var result = ProviderUsageResult.Failure(ProviderId.OpenRouter, "something broke");
        Assert.Equal("something broke", result.ErrorMessage);
    }

    [Fact]
    public void Failure_HasNoSessionUsage()
    {
        var result = ProviderUsageResult.Failure(ProviderId.OpenCodeGo, "err");
        Assert.Null(result.SessionUsage);
    }

    [Fact]
    public void Failure_HasNoWeeklyUsage()
    {
        var result = ProviderUsageResult.Failure(ProviderId.OpenCodeGo, "err");
        Assert.Null(result.WeeklyUsage);
    }

    [Fact]
    public void Failure_HasNoCreditsRemaining()
    {
        var result = ProviderUsageResult.Failure(ProviderId.OpenCodeGo, "err");
        Assert.Null(result.CreditsRemaining);
    }

    [Fact]
    public void Failure_HasNoItems()
    {
        var result = ProviderUsageResult.Failure(ProviderId.OpenCodeGo, "err");
        Assert.Null(result.Items);
    }

    // === EmptySuccess factory ===
    [Fact]
    public void EmptySuccess_SetsProviderCorrectly()
    {
        var result = ProviderUsageResult.EmptySuccess(ProviderId.OpenRouter);
        Assert.Equal(ProviderId.OpenRouter, result.Provider);
    }

    [Fact]
    public void EmptySuccess_SetsSuccessToTrue()
    {
        var result = ProviderUsageResult.EmptySuccess(ProviderId.Claude);
        Assert.True(result.Success);
    }

    [Fact]
    public void EmptySuccess_HasNoErrorMessage()
    {
        var result = ProviderUsageResult.EmptySuccess(ProviderId.Copilot);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void EmptySuccess_HasNoSessionUsage()
    {
        var result = ProviderUsageResult.EmptySuccess(ProviderId.Copilot);
        Assert.Null(result.SessionUsage);
    }

    [Fact]
    public void EmptySuccess_HasNoCreditsRemaining()
    {
        var result = ProviderUsageResult.EmptySuccess(ProviderId.Copilot);
        Assert.Null(result.CreditsRemaining);
    }

    // === FetchedAt ===
    [Fact]
    public void Failure_SetsFetchedAtToNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = ProviderUsageResult.Failure(ProviderId.Claude, "err");
        Assert.True(result.FetchedAt >= before);
        Assert.True(result.FetchedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void EmptySuccess_SetsFetchedAtToNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = ProviderUsageResult.EmptySuccess(ProviderId.Claude);
        Assert.True(result.FetchedAt >= before);
        Assert.True(result.FetchedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    // === Different providers produce different results ===
    [Theory]
    [InlineData(ProviderId.Copilot)]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.Codex)]
    [InlineData(ProviderId.OpenRouter)]
    [InlineData(ProviderId.OpenCodeGo)]
    [InlineData(ProviderId.OpenCodeZen)]
    public void Failure_AllProviders_SetCorrectProvider(ProviderId id)
    {
        var result = ProviderUsageResult.Failure(id, "test");
        Assert.Equal(id, result.Provider);
    }

    [Theory]
    [InlineData(ProviderId.Copilot)]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.Codex)]
    [InlineData(ProviderId.OpenRouter)]
    [InlineData(ProviderId.OpenCodeGo)]
    [InlineData(ProviderId.OpenCodeZen)]
    public void EmptySuccess_AllProviders_SetCorrectProvider(ProviderId id)
    {
        var result = ProviderUsageResult.EmptySuccess(id);
        Assert.Equal(id, result.Provider);
    }
}
