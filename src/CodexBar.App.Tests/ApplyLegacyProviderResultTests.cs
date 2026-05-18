// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;
using CodexBar.Core.Models;

/// <summary>
/// Tests for MainViewModel.ApplyLegacyProviderResult — the extracted static helper
/// that applies provider usage results to legacy single-card view models.
/// </summary>
public sealed class ApplyLegacyProviderResultTests
{
    private static ProviderCardViewModel CreateCard() => new()
    {
        ProviderId = ProviderId.OpenRouter,
        CardKey = "openrouter",
        DisplayName = "OpenRouter",
        StatusText = "Initial",
        UsedPercent = 0.5,
    };

    // --- Error path ---
    [Fact]
    public void ApplyLegacyProviderResult_ErrorResult_SetsIsError()
    {
        var card = CreateCard();
        var result = ProviderUsageResult.Failure(ProviderId.OpenRouter, "Network error");

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.True(card.IsError);
        Assert.Equal("Network error", card.StatusText);
    }

    [Fact]
    public void ApplyLegacyProviderResult_ErrorResult_ResetsUsageFields()
    {
        var card = CreateCard();
        card.UsedPercent = 0.9;
        card.IsHighUsage = true;
        card.WeeklyText = "Old weekly";
        var result = ProviderUsageResult.Failure(ProviderId.OpenRouter, "Timeout");

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.Equal(0, card.UsedPercent);
        Assert.False(card.IsHighUsage);
        Assert.Null(card.WeeklyText);
        Assert.Equal(0, card.WeeklyPercent);
        Assert.Null(card.ResetText);
    }

    [Fact]
    public void ApplyLegacyProviderResult_NullErrorMessage_UsesDefaultText()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = false,
            ErrorMessage = null,
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.Equal("Error", card.StatusText);
    }

    [Fact]
    public void ApplyLegacyProviderResult_ErrorResult_ShowsPercentBar()
    {
        var card = CreateCard();
        var result = ProviderUsageResult.Failure(ProviderId.OpenRouter, "Error");

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.True(card.ShowUsagePercent);
    }

    [Fact]
    public void ApplyLegacyProviderResult_ErrorResult_ClearsBars()
    {
        var card = CreateCard();
        card.Bars.Add(new UsageBarViewModel { Label = "test" });
        card.HasBars = true;
        var result = ProviderUsageResult.Failure(ProviderId.OpenRouter, "Error");

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.Empty(card.Bars);
        Assert.False(card.HasBars);
    }

    // --- Success with SessionUsage ---
    [Fact]
    public void ApplyLegacyProviderResult_SuccessWithSession_SetsUsedPercent()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot
            {
                UsedPercent = 0.75,
                UsageLabel = "750 / 1000",
            },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.False(card.IsError);
        Assert.Equal(0.75, card.UsedPercent);
        Assert.Equal("750 / 1000", card.StatusText);
    }

    [Fact]
    public void ApplyLegacyProviderResult_SessionHighUsage_SetsFlag()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot
            {
                UsedPercent = 0.85,
                UsageLabel = "High usage",
            },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.True(card.IsHighUsage);
    }

    [Fact]
    public void ApplyLegacyProviderResult_SessionLowUsage_ClearsFlag()
    {
        var card = CreateCard();
        card.IsHighUsage = true;
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot
            {
                UsedPercent = 0.3,
                UsageLabel = "Low",
            },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.False(card.IsHighUsage);
    }

    [Fact]
    public void ApplyLegacyProviderResult_SessionUnlimited_HidesPercentBar()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot
            {
                UsedPercent = 0,
                IsUnlimited = true,
                UsageLabel = "Unlimited",
            },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.False(card.ShowUsagePercent);
    }

    [Fact]
    public void ApplyLegacyProviderResult_SessionResetDescription_AppliesResetText()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot
            {
                UsedPercent = 0.5,
                UsageLabel = "50%",
                ResetDescription = "Resets in 2h",
            },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.Equal("Resets in 2h", card.ResetText);
    }

    [Fact]
    public void ApplyLegacyProviderResult_SessionNullLabel_UsesPercentFormat()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot
            {
                UsedPercent = 0.42,
                UsageLabel = null,
            },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.Contains("42", card.StatusText);
    }

    // --- Success with Credits ---
    [Fact]
    public void ApplyLegacyProviderResult_SuccessWithCredits_SetsCreditsDisplay()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            CreditsRemaining = 15.75m,
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.True(card.IsCreditsDisplay);
        Assert.Equal("$15.75", card.StatusText);
        Assert.False(card.ShowUsagePercent);
        Assert.Equal(15.75m, card.CreditsBalance);
    }

    // --- Success with no session/credits ---
    [Fact]
    public void ApplyLegacyProviderResult_SuccessNoData_ShowsNoDataText()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.False(card.IsError);
        Assert.Equal("No data", card.StatusText);
    }

    // --- Weekly usage ---
    [Fact]
    public void ApplyLegacyProviderResult_SuccessWithWeekly_SetsWeeklyFields()
    {
        var card = CreateCard();
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot { UsedPercent = 0.5, UsageLabel = "50%" },
            WeeklyUsage = new UsageSnapshot { UsedPercent = 0.65, UsageLabel = "Weekly: 65%" },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.Equal("Weekly: 65%", card.WeeklyText);
        Assert.Equal(0.65, card.WeeklyPercent);
    }

    [Fact]
    public void ApplyLegacyProviderResult_SuccessNoWeekly_ClearsWeeklyFields()
    {
        var card = CreateCard();
        card.WeeklyText = "old";
        card.WeeklyPercent = 0.9;
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot { UsedPercent = 0.5, UsageLabel = "50%" },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.Null(card.WeeklyText);
        Assert.Equal(0, card.WeeklyPercent);
    }

    // --- State transitions ---
    [Fact]
    public void ApplyLegacyProviderResult_ErrorToSuccess_ClearsErrorFlag()
    {
        var card = CreateCard();
        card.IsError = true;
        card.StatusText = "Previous error";
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot { UsedPercent = 0.3, UsageLabel = "Healthy" },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.False(card.IsError);
        Assert.Equal("Healthy", card.StatusText);
    }

    [Fact]
    public void ApplyLegacyProviderResult_CreditsToSession_ClearsCreditsState()
    {
        var card = CreateCard();
        card.IsCreditsDisplay = true;
        card.CreditsBalance = 10m;
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = true,
            SessionUsage = new UsageSnapshot { UsedPercent = 0.6, UsageLabel = "Session" },
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.False(card.IsCreditsDisplay);
        Assert.Null(card.CreditsBalance);
        Assert.True(card.ShowUsagePercent);
    }

    [Fact]
    public void ApplyLegacyProviderResult_CreditsToError_ClearsCreditsBalance()
    {
        var card = CreateCard();
        card.IsCreditsDisplay = true;
        card.CreditsBalance = 25m;
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenRouter,
            Success = false,
            ErrorMessage = "API down",
        };

        MainViewModel.ApplyLegacyProviderResult(card, result);

        Assert.True(card.IsError);
        Assert.Null(card.CreditsBalance);
    }
}
