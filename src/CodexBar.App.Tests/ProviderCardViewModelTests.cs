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
}
