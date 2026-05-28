// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.ComponentModel;
using CodexBar.App.ViewModels;
using CodexBar.Core.Models;

/// <summary>
/// Tests verifying PropertyChanged cascading and deduplication in ProviderCardViewModel.
/// These ensure WPF data bindings update correctly when interdependent properties change.
/// </summary>
public sealed class ProviderCardViewModelPropertyChangedTests
{
    // ---------------------------------------------------------------
    // HasBars setter — cascading PropertyChanged
    // ---------------------------------------------------------------
    [Fact]
    public void HasBars_WhenChanged_FiresShowProgressBarChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.HasBars = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowProgressBar), firedProperties);
    }

    [Fact]
    public void HasBars_WhenChanged_FiresShowStatusTextLineChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.HasBars = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowStatusTextLine), firedProperties);
    }

    [Fact]
    public void HasBars_WhenSetToSameValue_DoesNotFire()
    {
        var card = new ProviderCardViewModel { HasBars = true };
        var firedProperties = CapturePropertyChanges(card);

        card.HasBars = true;

        Assert.Empty(firedProperties);
    }

    // ---------------------------------------------------------------
    // IsCreditsDisplay setter — cascading PropertyChanged
    // ---------------------------------------------------------------
    [Fact]
    public void IsCreditsDisplay_WhenChanged_FiresShowProgressBarChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsCreditsDisplay = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowProgressBar), firedProperties);
    }

    [Fact]
    public void IsCreditsDisplay_WhenChanged_FiresShowSingleCreditsDisplayChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsCreditsDisplay = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowSingleCreditsDisplay), firedProperties);
    }

    [Fact]
    public void IsCreditsDisplay_WhenChanged_FiresShowStatusTextLineChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsCreditsDisplay = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowStatusTextLine), firedProperties);
    }

    [Fact]
    public void IsCreditsDisplay_WhenSetToSameValue_DoesNotFire()
    {
        var card = new ProviderCardViewModel { IsCreditsDisplay = true };
        var firedProperties = CapturePropertyChanges(card);

        card.IsCreditsDisplay = true;

        Assert.Empty(firedProperties);
    }

    // ---------------------------------------------------------------
    // IsPairedCredits setter — cascading PropertyChanged
    // ---------------------------------------------------------------
    [Fact]
    public void IsPairedCredits_WhenChanged_FiresShowSingleCreditsDisplayChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsPairedCredits = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowSingleCreditsDisplay), firedProperties);
    }

    [Fact]
    public void IsPairedCredits_WhenChanged_FiresShowProgressBarChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsPairedCredits = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowProgressBar), firedProperties);
    }

    [Fact]
    public void IsPairedCredits_WhenChanged_FiresShowStatusTextLineChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsPairedCredits = true;

        Assert.Contains(nameof(ProviderCardViewModel.ShowStatusTextLine), firedProperties);
    }

    [Fact]
    public void IsPairedCredits_WhenSetToSameValue_DoesNotFire()
    {
        var card = new ProviderCardViewModel { IsPairedCredits = true };
        var firedProperties = CapturePropertyChanges(card);

        card.IsPairedCredits = true;

        Assert.Empty(firedProperties);
    }

    // ---------------------------------------------------------------
    // SetField deduplication — standard properties
    // ---------------------------------------------------------------
    [Fact]
    public void StatusText_WhenSetToSameValue_DoesNotFire()
    {
        var card = new ProviderCardViewModel { StatusText = "Test" };
        var firedProperties = CapturePropertyChanges(card);

        card.StatusText = "Test";

        Assert.Empty(firedProperties);
    }

    [Fact]
    public void UsedPercent_WhenSetToSameValue_DoesNotFire()
    {
        var card = new ProviderCardViewModel { UsedPercent = 0.5 };
        var firedProperties = CapturePropertyChanges(card);

        card.UsedPercent = 0.5;

        Assert.Empty(firedProperties);
    }

    [Fact]
    public void IsHighUsage_WhenSetToSameValue_DoesNotFire()
    {
        var card = new ProviderCardViewModel { IsHighUsage = true };
        var firedProperties = CapturePropertyChanges(card);

        card.IsHighUsage = true;

        Assert.Empty(firedProperties);
    }

    [Fact]
    public void DisplayName_WhenChanged_FiresPropertyChanged()
    {
        var card = new ProviderCardViewModel { DisplayName = "Old" };
        var firedProperties = CapturePropertyChanges(card);

        card.DisplayName = "New";

        Assert.Contains(nameof(ProviderCardViewModel.DisplayName), firedProperties);
    }

    [Fact]
    public void CreditsBalance_WhenChanged_FiresPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.CreditsBalance = 25.00m;

        Assert.Contains(nameof(ProviderCardViewModel.CreditsBalance), firedProperties);
    }

    [Fact]
    public void CreditsBalance_WhenSetToSameValue_DoesNotFire()
    {
        var card = new ProviderCardViewModel { CreditsBalance = 25.00m };
        var firedProperties = CapturePropertyChanges(card);

        card.CreditsBalance = 25.00m;

        Assert.Empty(firedProperties);
    }

    [Fact]
    public void OverageCost_WhenChanged_FiresPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.OverageCost = 3.50m;

        Assert.Contains(nameof(ProviderCardViewModel.OverageCost), firedProperties);
    }

    [Fact]
    public void SessionSpending_WhenChanged_FiresPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.SessionSpending = "$1.50";

        Assert.Contains(nameof(ProviderCardViewModel.SessionSpending), firedProperties);
    }

    [Fact]
    public void CompanionCard_WhenChanged_FiresPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.CompanionCard = new ProviderCardViewModel();

        Assert.Contains(nameof(ProviderCardViewModel.CompanionCard), firedProperties);
    }

    [Fact]
    public void IsCompactCard_WhenChanged_FiresPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsCompactCard = true;

        Assert.Contains(nameof(ProviderCardViewModel.IsCompactCard), firedProperties);
    }

    [Fact]
    public void IsHiddenCompanion_WhenChanged_FiresPropertyChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsHiddenCompanion = true;

        Assert.Contains(nameof(ProviderCardViewModel.IsHiddenCompanion), firedProperties);
    }

    [Fact]
    public void IsHiddenCompanion_WhenChanged_FiresIsCardVisibleChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsHiddenCompanion = true;

        Assert.Contains(nameof(ProviderCardViewModel.IsCardVisible), firedProperties);
    }

    [Fact]
    public void IsProviderDisplayed_WhenChanged_FiresIsCardVisibleChanged()
    {
        var card = new ProviderCardViewModel();
        var firedProperties = CapturePropertyChanges(card);

        card.IsProviderDisplayed = false;

        Assert.Contains(nameof(ProviderCardViewModel.IsCardVisible), firedProperties);
    }

    // ---------------------------------------------------------------
    // Computed property correctness after state changes
    // ---------------------------------------------------------------
    [Fact]
    public void ShowProgressBar_AfterHasBarsSetTrue_ReturnsFalse()
    {
        var card = new ProviderCardViewModel();
        Assert.True(card.ShowProgressBar);

        card.HasBars = true;

        Assert.False(card.ShowProgressBar);
    }

    [Fact]
    public void ShowProgressBar_AfterIsCreditsDisplaySetTrue_ReturnsFalse()
    {
        var card = new ProviderCardViewModel();
        Assert.True(card.ShowProgressBar);

        card.IsCreditsDisplay = true;

        Assert.False(card.ShowProgressBar);
    }

    [Fact]
    public void ShowProgressBar_AfterIsPairedCreditsSetTrue_ReturnsFalse()
    {
        var card = new ProviderCardViewModel();
        Assert.True(card.ShowProgressBar);

        card.IsPairedCredits = true;

        Assert.False(card.ShowProgressBar);
    }

    [Fact]
    public void ShowSingleCreditsDisplay_BecomesTrue_WhenCreditsSetAndNotPaired()
    {
        var card = new ProviderCardViewModel();
        Assert.False(card.ShowSingleCreditsDisplay);

        card.IsCreditsDisplay = true;

        Assert.True(card.ShowSingleCreditsDisplay);
    }

    [Fact]
    public void ShowSingleCreditsDisplay_BecomesFalse_WhenPairedAfterCredits()
    {
        var card = new ProviderCardViewModel { IsCreditsDisplay = true };
        Assert.True(card.ShowSingleCreditsDisplay);

        card.IsPairedCredits = true;

        Assert.False(card.ShowSingleCreditsDisplay);
    }

    [Fact]
    public void ShowStatusTextLine_DefaultState_IsTrue()
    {
        var card = new ProviderCardViewModel();
        Assert.True(card.ShowStatusTextLine);
    }

    [Fact]
    public void ShowStatusTextLine_WhenHasBars_IsFalse()
    {
        var card = new ProviderCardViewModel { HasBars = true };
        Assert.False(card.ShowStatusTextLine);
    }

    [Fact]
    public void ShowStatusTextLine_WhenIsCreditsDisplay_IsFalse()
    {
        var card = new ProviderCardViewModel { IsCreditsDisplay = true };
        Assert.False(card.ShowStatusTextLine);
    }

    [Fact]
    public void ShowStatusTextLine_WhenIsPairedCredits_IsFalse()
    {
        var card = new ProviderCardViewModel { IsPairedCredits = true };
        Assert.False(card.ShowStatusTextLine);
    }

    [Fact]
    public void IsCardVisible_WhenProviderDisplayedAndNotCompanion_IsTrue()
    {
        var card = new ProviderCardViewModel();
        Assert.True(card.IsCardVisible);
    }

    [Fact]
    public void IsCardVisible_WhenProviderNotDisplayed_IsFalse()
    {
        var card = new ProviderCardViewModel { IsProviderDisplayed = false };
        Assert.False(card.IsCardVisible);
    }

    [Fact]
    public void IsCardVisible_WhenHiddenCompanion_IsFalse()
    {
        var card = new ProviderCardViewModel { IsHiddenCompanion = true };
        Assert.False(card.IsCardVisible);
    }

    private static List<string> CapturePropertyChanges(INotifyPropertyChanged vm)
    {
        var fired = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                fired.Add(e.PropertyName);
            }
        };

        return fired;
    }
}
