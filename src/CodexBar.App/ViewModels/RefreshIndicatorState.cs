// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.ViewModels;

internal static class RefreshIndicatorState
{
    private const string UnavailableToolTip = "Next auto refresh unavailable";
    private const string RefreshingToolTip = "Refreshing now...";

    public static RefreshIndicatorSnapshot Calculate(DateTimeOffset nowUtc, DateTimeOffset? nextRefreshAtUtc, TimeSpan refreshInterval)
    {
        if (nextRefreshAtUtc is null || refreshInterval <= TimeSpan.Zero)
        {
            return new RefreshIndicatorSnapshot(false, 0, UnavailableToolTip);
        }

        var remaining = nextRefreshAtUtc.Value - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return new RefreshIndicatorSnapshot(true, 1, RefreshingToolTip);
        }

        var elapsed = refreshInterval - remaining;
        var progress = Math.Clamp(elapsed.TotalMilliseconds / refreshInterval.TotalMilliseconds, 0, 1);

        return new RefreshIndicatorSnapshot(true, progress, $"Next auto refresh in {FormatRemaining(remaining)}");
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        return $"{(int)rounded.TotalMinutes}:{rounded.Seconds:00}";
    }
}

internal readonly record struct RefreshIndicatorSnapshot(bool IsVisible, double Progress, string ToolTipText);
