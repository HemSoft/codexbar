using CodexBar.Core.Models;
using Xunit;

namespace CodexBar.Core.Tests;

public class UsageItemTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var item = new UsageItem { Key = "k", DisplayName = "D" };
        Assert.Equal("k", item.Key);
        Assert.Equal("D", item.DisplayName);
        Assert.True(item.Success);
        Assert.Null(item.ErrorMessage);
        Assert.Null(item.PrimaryUsage);
        Assert.Null(item.SecondaryUsage);
        Assert.Null(item.Bars);
        Assert.Null(item.CreditsRemaining);
    }

    [Fact]
    public void Create_WithBars_SetsBars()
    {
        var bars = new[] { new UsageBar { Label = "L", UsedPercent = 0.2 } };
        var item = new UsageItem { Key = "k", DisplayName = "D", Bars = bars };
        Assert.Equal(bars, item.Bars);
    }
}
