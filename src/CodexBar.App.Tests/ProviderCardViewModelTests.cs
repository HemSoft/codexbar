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
}
