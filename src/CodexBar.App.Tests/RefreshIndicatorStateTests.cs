namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;

public sealed class RefreshIndicatorStateTests
{
    [Fact]
    public void Calculate_WhenNoSchedule_ReturnsHiddenState()
    {
        var state = RefreshIndicatorState.Calculate(DateTimeOffset.UtcNow, null, TimeSpan.FromMinutes(2));

        Assert.False(state.IsVisible);
        Assert.Equal(0, state.Progress);
        Assert.Equal("Next auto refresh unavailable", state.ToolTipText);
    }

    [Fact]
    public void Calculate_WhenHalfwayToRefresh_ReturnsMidpointProgress()
    {
        var nowUtc = new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero);
        var state = RefreshIndicatorState.Calculate(nowUtc, nowUtc.AddMinutes(1), TimeSpan.FromMinutes(2));

        Assert.True(state.IsVisible);
        Assert.Equal(0.5d, state.Progress, 3);
        Assert.Equal("Next auto refresh in 1:00", state.ToolTipText);
    }

    [Fact]
    public void Calculate_WhenRefreshIsDue_ReturnsRefreshingState()
    {
        var nowUtc = new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero);
        var state = RefreshIndicatorState.Calculate(nowUtc, nowUtc.AddSeconds(-1), TimeSpan.FromMinutes(2));

        Assert.True(state.IsVisible);
        Assert.Equal(1d, state.Progress);
        Assert.Equal("Refreshing now...", state.ToolTipText);
    }
}
