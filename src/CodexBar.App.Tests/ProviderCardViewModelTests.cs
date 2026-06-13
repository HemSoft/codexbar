// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.ComponentModel;
using CodexBar.App.ViewModels;
using CodexBar.Core.Models;

public sealed class ProviderCardViewModelTests
{
    [Fact]
    public void ShowSingleCreditsDisplay_WhenCardIsPaired_ReturnsFalse()
    {
        var card = new ProviderCardViewModel
        {
            IsCreditsDisplay = true,
            IsPairedCredits = true,
        };

        Assert.False(card.ShowSingleCreditsDisplay);
    }

    [Fact]
    public void ShowSingleCreditsDisplay_WhenCardIsUnpairedCredits_ReturnsTrue()
    {
        var card = new ProviderCardViewModel
        {
            IsCreditsDisplay = true,
            IsPairedCredits = false,
        };

        Assert.True(card.ShowSingleCreditsDisplay);
    }

    [Fact]
    public void ShowSingleCreditsDisplay_WhenNotCreditsDisplay_ReturnsFalse()
    {
        var card = new ProviderCardViewModel
        {
            IsCreditsDisplay = false,
            IsPairedCredits = false,
        };

        Assert.False(card.ShowSingleCreditsDisplay);
    }

    [Fact]
    public void ShowProgressBar_WhenCardIsPaired_ReturnsFalse()
    {
        var card = new ProviderCardViewModel
        {
            IsCreditsDisplay = false,
            IsPairedCredits = true,
        };

        Assert.False(card.ShowProgressBar);
    }

    [Fact]
    public void ShowProgressBar_WhenNoBarsAndNotCreditsAndNotPaired_ReturnsTrue()
    {
        var card = new ProviderCardViewModel
        {
            HasBars = false,
            IsCreditsDisplay = false,
            IsPairedCredits = false,
        };

        Assert.True(card.ShowProgressBar);
    }

    [Fact]
    public void ShowProgressBar_WhenHasBars_ReturnsFalse()
    {
        var card = new ProviderCardViewModel { HasBars = true };

        Assert.False(card.ShowProgressBar);
    }

    [Fact]
    public void ShowProgressBar_WhenIsCreditsDisplay_ReturnsFalse()
    {
        var card = new ProviderCardViewModel { IsCreditsDisplay = true };

        Assert.False(card.ShowProgressBar);
    }

    [Fact]
    public void ShowStatusTextLine_WhenCardIsCreditsDisplay_ReturnsFalse()
    {
        var card = new ProviderCardViewModel
        {
            IsCreditsDisplay = true,
        };

        Assert.False(card.ShowStatusTextLine);
    }

    [Fact]
    public void ShowStatusTextLine_WhenCardIsUsageCard_ReturnsTrue()
    {
        var card = new ProviderCardViewModel
        {
            HasBars = false,
            IsCreditsDisplay = false,
            IsPairedCredits = false,
        };

        Assert.True(card.ShowStatusTextLine);
    }

    [Fact]
    public void ShowStatusTextLine_WhenCardHasBars_ReturnsTrue()
    {
        var card = new ProviderCardViewModel
        {
            HasBars = true,
            IsCreditsDisplay = false,
            IsPairedCredits = false,
        };

        Assert.True(card.ShowStatusTextLine);
    }

    [Fact]
    public void UpdateProjection_WhenProjectedUsageExceedsLimit_ShowsEasternLimitHitTime()
    {
        var bar = new UsageBarViewModel
        {
            ProjectionCurrent = 50m,
            ProjectionLimit = 100m,
            ProjectionPeriodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ProjectionPeriodEnd = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        bar.UpdateProjection(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("Month end est. · 150 / 100", bar.Label);
        Assert.Equal(1.0, bar.UsedPercent);
        Assert.Equal("Limit hit Jun 20 8:00 PM ET", bar.ResetDescription);
    }

    [Fact]
    public void UpdateProjection_WhenProjectedUsageStaysUnderLimit_ShowsLimitNotReached()
    {
        var bar = new UsageBarViewModel
        {
            ProjectionCurrent = 10m,
            ProjectionLimit = 100m,
            ProjectionPeriodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ProjectionPeriodEnd = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        bar.UpdateProjection(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("Month end est. · 30 / 100", bar.Label);
        Assert.Equal(0.3, bar.UsedPercent, 3);
        Assert.Equal("Limit not reached", bar.ResetDescription);
    }

    [Fact]
    public void SessionSpending_DefaultIsNull()
    {
        var card = new ProviderCardViewModel();
        Assert.Null(card.SessionSpending);
    }

    [Fact]
    public void SessionSpending_RaisesPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var raised = false;
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProviderCardViewModel.SessionSpending))
            {
                raised = true;
            }
        };

        card.SessionSpending = "$1.23";
        Assert.True(raised);
    }

    [Fact]
    public void CreditsBalance_DefaultIsNull()
    {
        var card = new ProviderCardViewModel();
        Assert.Null(card.CreditsBalance);
    }

    [Fact]
    public void ResetSessionSpendingCommand_CanBeSet()
    {
        var card = new ProviderCardViewModel();
        var invoked = false;
        card.ResetSessionSpendingCommand = new RelayCommand(_ => invoked = true);
        card.ResetSessionSpendingCommand.Execute(null);
        Assert.True(invoked);
    }

    // --- HasBars property-changed fanout ---
    [Fact]
    public void HasBars_SetTrue_NotifiesShowProgressBar()
    {
        var card = new ProviderCardViewModel();
        var notifications = new List<string>();
        card.PropertyChanged += (_, e) => notifications.Add(e.PropertyName!);

        card.HasBars = true;

        Assert.Contains(nameof(ProviderCardViewModel.HasBars), notifications);
        Assert.Contains(nameof(ProviderCardViewModel.ShowProgressBar), notifications);
        Assert.DoesNotContain(nameof(ProviderCardViewModel.ShowStatusTextLine), notifications);
    }

    [Fact]
    public void HasBars_SetSameValue_DoesNotNotify()
    {
        var card = new ProviderCardViewModel { HasBars = true };
        var raised = false;
        card.PropertyChanged += (_, _) => raised = true;

        card.HasBars = true;

        Assert.False(raised);
    }

    // --- IsCreditsDisplay property-changed fanout ---
    [Fact]
    public void IsCreditsDisplay_SetTrue_NotifiesComputedProperties()
    {
        var card = new ProviderCardViewModel();
        var notifications = new List<string>();
        card.PropertyChanged += (_, e) => notifications.Add(e.PropertyName!);

        card.IsCreditsDisplay = true;

        Assert.Contains(nameof(ProviderCardViewModel.IsCreditsDisplay), notifications);
        Assert.Contains(nameof(ProviderCardViewModel.ShowProgressBar), notifications);
        Assert.Contains(nameof(ProviderCardViewModel.ShowSingleCreditsDisplay), notifications);
        Assert.Contains(nameof(ProviderCardViewModel.ShowStatusTextLine), notifications);
    }

    [Fact]
    public void IsCreditsDisplay_SetSameValue_DoesNotNotify()
    {
        var card = new ProviderCardViewModel { IsCreditsDisplay = true };
        var raised = false;
        card.PropertyChanged += (_, _) => raised = true;

        card.IsCreditsDisplay = true;

        Assert.False(raised);
    }

    // --- IsPairedCredits property-changed fanout ---
    [Fact]
    public void IsPairedCredits_SetTrue_NotifiesComputedProperties()
    {
        var card = new ProviderCardViewModel();
        var notifications = new List<string>();
        card.PropertyChanged += (_, e) => notifications.Add(e.PropertyName!);

        card.IsPairedCredits = true;

        Assert.Contains(nameof(ProviderCardViewModel.IsPairedCredits), notifications);
        Assert.Contains(nameof(ProviderCardViewModel.ShowSingleCreditsDisplay), notifications);
        Assert.Contains(nameof(ProviderCardViewModel.ShowProgressBar), notifications);
        Assert.Contains(nameof(ProviderCardViewModel.ShowStatusTextLine), notifications);
    }

    [Fact]
    public void IsPairedCredits_SetSameValue_DoesNotNotify()
    {
        var card = new ProviderCardViewModel { IsPairedCredits = true };
        var raised = false;
        card.PropertyChanged += (_, _) => raised = true;

        card.IsPairedCredits = true;

        Assert.False(raised);
    }

    // --- CompanionCard ---
    [Fact]
    public void CompanionCard_Set_RaisesPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var companion = new ProviderCardViewModel { CardKey = "zen" };
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.CompanionCard), () => card.CompanionCard = companion);

        Assert.True(raised);
        Assert.Same(companion, card.CompanionCard);
    }

    [Fact]
    public void CompanionCard_DefaultIsNull()
    {
        var card = new ProviderCardViewModel();
        Assert.Null(card.CompanionCard);
    }

    // --- OverageCost ---
    [Fact]
    public void OverageCost_Set_RaisesPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.OverageCost), () => card.OverageCost = 5.00m);

        Assert.True(raised);
        Assert.Equal(5.00m, card.OverageCost);
    }

    [Fact]
    public void OverageCost_DefaultIsNull()
    {
        var card = new ProviderCardViewModel();
        Assert.Null(card.OverageCost);
    }

    // --- IsHiddenCompanion ---
    [Fact]
    public void IsHiddenCompanion_Set_RaisesPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.IsHiddenCompanion), () => card.IsHiddenCompanion = true);

        Assert.True(raised);
        Assert.True(card.IsHiddenCompanion);
    }

    // --- SetField deduplication for standard properties ---
    [Fact]
    public void DisplayName_SetSameValue_DoesNotNotify()
    {
        var card = new ProviderCardViewModel { DisplayName = "Test" };
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.DisplayName), () => card.DisplayName = "Test");

        Assert.False(raised);
    }

    [Fact]
    public void StatusText_SetSameValue_DoesNotNotify()
    {
        var card = new ProviderCardViewModel { StatusText = "Waiting…" };
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.StatusText), () => card.StatusText = "Waiting…");

        Assert.False(raised);
    }

    [Fact]
    public void UsedPercent_SetSameValue_DoesNotNotify()
    {
        var card = new ProviderCardViewModel { UsedPercent = 0.5 };
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.UsedPercent), () => card.UsedPercent = 0.5);

        Assert.False(raised);
    }

    [Fact]
    public void IsError_SetSameValue_DoesNotNotify()
    {
        var card = new ProviderCardViewModel { IsError = true };
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.IsError), () => card.IsError = true);

        Assert.False(raised);
    }

    // --- Init properties ---
    [Fact]
    public void ProviderId_WhenSet_ReturnsCorrectValue()
    {
        var card = new ProviderCardViewModel { ProviderId = ProviderId.Copilot };
        Assert.Equal(ProviderId.Copilot, card.ProviderId);
    }

    [Fact]
    public void CardKey_WhenSet_ReturnsCorrectValue()
    {
        var card = new ProviderCardViewModel { CardKey = "gemini" };
        Assert.Equal("gemini", card.CardKey);
    }

    [Fact]
    public void IsCompactCard_Set_RaisesPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.IsCompactCard), () => card.IsCompactCard = true);

        Assert.True(raised);
        Assert.True(card.IsCompactCard);
    }

    [Fact]
    public void Bars_DefaultIsEmpty()
    {
        var card = new ProviderCardViewModel();
        Assert.Empty(card.Bars);
    }

    [Fact]
    public void SessionResetTime_Set_RaisesPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var raised = AssertPropertyChanged(card, nameof(ProviderCardViewModel.SessionResetTime), () => card.SessionResetTime = "2026-05-18 10:00 AM");

        Assert.True(raised);
        Assert.Equal("2026-05-18 10:00 AM", card.SessionResetTime);
    }

    // --- Property defaults ---
    [Fact]
    public void WeeklyText_DefaultIsNull()
    {
        var card = new ProviderCardViewModel();
        Assert.Null(card.WeeklyText);
    }

    [Fact]
    public void ResetText_DefaultIsNull()
    {
        var card = new ProviderCardViewModel();
        Assert.Null(card.ResetText);
    }

    [Fact]
    public void ShowUsagePercent_DefaultIsTrue()
    {
        var card = new ProviderCardViewModel();
        Assert.True(card.ShowUsagePercent);
    }

    private static bool AssertPropertyChanged(INotifyPropertyChanged vm, string propertyName, Action action)
    {
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == propertyName)
            {
                raised = true;
            }
        };

        action();
        return raised;
    }
}
