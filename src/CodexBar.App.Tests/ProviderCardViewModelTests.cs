// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;

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
    public void ShowStatusTextLine_WhenCardHasBars_ReturnsFalse()
    {
        var card = new ProviderCardViewModel
        {
            HasBars = true,
            IsCreditsDisplay = false,
            IsPairedCredits = false,
        };

        Assert.False(card.ShowStatusTextLine);
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
}
