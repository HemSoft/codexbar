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

    [Fact]
    public void Calculate_WhenZeroInterval_ReturnsHiddenState()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var state = RefreshIndicatorState.Calculate(nowUtc, nowUtc.AddMinutes(1), TimeSpan.Zero);

        Assert.False(state.IsVisible);
        Assert.Equal(0, state.Progress);
    }

    [Fact]
    public void Calculate_WhenNegativeInterval_ReturnsHiddenState()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var state = RefreshIndicatorState.Calculate(nowUtc, nowUtc.AddMinutes(1), TimeSpan.FromMinutes(-1));

        Assert.False(state.IsVisible);
        Assert.Equal(0, state.Progress);
    }

    [Fact]
    public void Calculate_WhenNearEnd_ReturnsHighProgress()
    {
        var nowUtc = new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero);
        var state = RefreshIndicatorState.Calculate(nowUtc, nowUtc.AddSeconds(5), TimeSpan.FromMinutes(2));

        Assert.True(state.IsVisible);
        Assert.True(state.Progress > 0.9);
        Assert.Contains("0:05", state.ToolTipText);
    }

    [Fact]
    public void Calculate_WhenJustStarted_ReturnsLowProgress()
    {
        var nowUtc = new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero);
        var state = RefreshIndicatorState.Calculate(nowUtc, nowUtc.AddSeconds(115), TimeSpan.FromMinutes(2));

        Assert.True(state.IsVisible);
        Assert.True(state.Progress < 0.1);
    }

    [Fact]
    public void Calculate_WhenExactlyAtRefreshTime_ReturnsRefreshing()
    {
        var nowUtc = new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero);
        var state = RefreshIndicatorState.Calculate(nowUtc, nowUtc, TimeSpan.FromMinutes(2));

        Assert.True(state.IsVisible);
        Assert.Equal(1d, state.Progress);
        Assert.Equal("Refreshing now...", state.ToolTipText);
    }

    [Fact]
    public void Snapshot_IsValueType_WithEquality()
    {
        var a = new RefreshIndicatorSnapshot(true, 0.5, "test");
        var b = new RefreshIndicatorSnapshot(true, 0.5, "test");

        Assert.Equal(a, b);
    }
}
