// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.ComponentModel;
using CodexBar.App.ViewModels;

public sealed class UsageBarViewModelTests
{
    [Fact]
    public void Label_Default_IsEmpty()
    {
        var vm = new UsageBarViewModel();
        Assert.Equal(string.Empty, vm.Label);
    }

    [Fact]
    public void Label_Set_RaisesPropertyChanged()
    {
        var vm = new UsageBarViewModel();
        var raised = AssertPropertyChanged(vm, nameof(UsageBarViewModel.Label), () => vm.Label = "Test");
        Assert.True(raised);
    }

    [Fact]
    public void Label_SetSameValue_DoesNotRaisePropertyChanged()
    {
        var vm = new UsageBarViewModel { Label = "Test" };
        var raised = AssertPropertyChanged(vm, nameof(UsageBarViewModel.Label), () => vm.Label = "Test");
        Assert.False(raised);
    }

    [Fact]
    public void UsedPercent_Default_IsZero()
    {
        var vm = new UsageBarViewModel();
        Assert.Equal(0, vm.UsedPercent);
    }

    [Fact]
    public void UsedPercent_Set_RaisesPropertyChanged()
    {
        var vm = new UsageBarViewModel();
        var raised = AssertPropertyChanged(vm, nameof(UsageBarViewModel.UsedPercent), () => vm.UsedPercent = 0.5);
        Assert.True(raised);
    }

    [Fact]
    public void UsedPercent_SetSameValue_DoesNotRaise()
    {
        var vm = new UsageBarViewModel { UsedPercent = 0.5 };
        var raised = AssertPropertyChanged(vm, nameof(UsageBarViewModel.UsedPercent), () => vm.UsedPercent = 0.5);
        Assert.False(raised);
    }

    [Fact]
    public void ProjectedPercent_Default_IsZero()
    {
        var vm = new UsageBarViewModel();
        Assert.Equal(0, vm.ProjectedPercent);
    }

    [Fact]
    public void ProjectedPercent_WhenAboveUsed_ShowsProjectedUsage()
    {
        var vm = new UsageBarViewModel { UsedPercent = 0.5 };

        vm.ProjectedPercent = 1.0;

        Assert.True(vm.ShowProjectedUsage);
    }

    [Fact]
    public void ProjectedPercent_Set_RaisesShowProjectedUsageChanged()
    {
        var vm = new UsageBarViewModel { UsedPercent = 0.5 };
        var raised = AssertPropertyChanged(
            vm,
            nameof(UsageBarViewModel.ShowProjectedUsage),
            () => vm.ProjectedPercent = 0.9);

        Assert.True(raised);
    }

    [Fact]
    public void ResetDescription_Default_IsNull()
    {
        var vm = new UsageBarViewModel();
        Assert.Null(vm.ResetDescription);
    }

    [Fact]
    public void ResetDescription_Set_RaisesPropertyChanged()
    {
        var vm = new UsageBarViewModel();
        var raised = AssertPropertyChanged(vm, nameof(UsageBarViewModel.ResetDescription), () => vm.ResetDescription = "2h");
        Assert.True(raised);
    }

    [Fact]
    public void IsHighUsage_Default_IsFalse()
    {
        var vm = new UsageBarViewModel();
        Assert.False(vm.IsHighUsage);
    }

    [Fact]
    public void IsHighUsage_Set_RaisesPropertyChanged()
    {
        var vm = new UsageBarViewModel();
        var raised = AssertPropertyChanged(vm, nameof(UsageBarViewModel.IsHighUsage), () => vm.IsHighUsage = true);
        Assert.True(raised);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void UpdateProjection_WhenAnyProjectionDataIsMissing_LeavesDisplayUnchanged(
        bool hasCurrent,
        bool hasLimit,
        bool hasPeriodStart,
        bool hasPeriodEnd)
    {
        var vm = new UsageBarViewModel
        {
            Label = "5h",
            UsedPercent = 0.25,
            ResetDescription = "resets soon",
            ProjectionCurrent = hasCurrent ? 10m : null,
            ProjectionLimit = hasLimit ? 100m : null,
            ProjectionPeriodStart = hasPeriodStart ? new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero) : null,
            ProjectionPeriodEnd = hasPeriodEnd ? new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) : null,
        };

        vm.UpdateProjection(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("5h", vm.Label);
        Assert.Equal(0.25, vm.UsedPercent);
        Assert.Equal("resets soon", vm.ResetDescription);
    }

    [Fact]
    public void UpdateProjection_WhenNowBeforePeriodStart_KeepsCurrentProjectionAndUnknownHitTime()
    {
        var vm = new UsageBarViewModel
        {
            ProjectionCurrent = 10m,
            ProjectionLimit = 100m,
            ProjectionPeriodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ProjectionPeriodEnd = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        vm.UpdateProjection(new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("Month end est. · 10 / 100", vm.Label);
        Assert.Equal(0.1, vm.UsedPercent, 3);
        Assert.Equal("Limit hit unknown", vm.ResetDescription);
    }

    [Fact]
    public void UpdateProjection_WhenNowAtPeriodEnd_KeepsCurrentProjectionAndShowsLimitNotReached()
    {
        var vm = new UsageBarViewModel
        {
            ProjectionCurrent = 10m,
            ProjectionLimit = 100m,
            ProjectionPeriodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ProjectionPeriodEnd = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        vm.UpdateProjection(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("Month end est. · 10 / 100", vm.Label);
        Assert.Equal(0.1, vm.UsedPercent, 3);
        Assert.Equal("Limit not reached", vm.ResetDescription);
    }

    [Fact]
    public void UpdateProjection_WhenCurrentAlreadyMeetsLimit_ShowsLimitReached()
    {
        var vm = new UsageBarViewModel
        {
            ProjectionCurrent = 100m,
            ProjectionLimit = 100m,
            ProjectionPeriodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ProjectionPeriodEnd = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        vm.UpdateProjection(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("Month end est. · 300 / 100", vm.Label);
        Assert.Equal(1.0, vm.UsedPercent);
        Assert.Equal("Limit reached", vm.ResetDescription);
    }

    [Fact]
    public void UpdateProjection_WhenShownOnCurrentBar_PreservesActualAndSetsProjection()
    {
        var bar = new UsageBarViewModel
        {
            Label = "5 hour usage limit",
            UsedPercent = 0.5,
            ResetDescription = "Resets 2h 30m",
            ProjectionCurrent = 0.5m,
            ProjectionLimit = 1m,
            ProjectionPeriodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ProjectionPeriodEnd = new DateTimeOffset(2026, 6, 1, 5, 0, 0, TimeSpan.Zero),
            ShowProjectionOnCurrentBar = true,
        };

        bar.UpdateProjection(new DateTimeOffset(2026, 6, 1, 2, 30, 0, TimeSpan.Zero));

        Assert.Equal("5 hour usage limit", bar.Label);
        Assert.Equal(0.5, bar.UsedPercent, 3);
        Assert.Equal(1.0, bar.ProjectedPercent);
        Assert.True(bar.ShowProjectedUsage);
        Assert.Equal("Resets 2h 30m", bar.ResetDescription);
        Assert.StartsWith("Projected 100% at current pace · Limit hit", bar.ProjectionDescription);
    }

    [Fact]
    public void UpdateProjection_WhenProjectionDataMissing_ClearsProjectionOverlay()
    {
        var bar = new UsageBarViewModel
        {
            UsedPercent = 0.5,
            ProjectedPercent = 1.0,
            ProjectionDescription = "Projected 100%",
            ShowProjectionOnCurrentBar = true,
        };

        bar.UpdateProjection(new DateTimeOffset(2026, 6, 1, 2, 30, 0, TimeSpan.Zero));

        Assert.Equal(0, bar.ProjectedPercent);
        Assert.Null(bar.ProjectionDescription);
        Assert.False(bar.ShowProjectedUsage);
    }

    [Fact]
    public void FormatEasternTime_WhenTimeZoneMissing_UsesUtc()
    {
        var timestamp = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);

        var result = UsageBarViewModel.FormatEasternTime(timestamp, easternTimeZone: null);

        Assert.Equal("Jun 20 12:00 AM UTC", result);
    }

    [Fact]
    public void FormatLimitHit_WhenElapsedIsNonPositive_ReturnsUnknown()
    {
        var periodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var result = UsageBarViewModel.FormatLimitHit(
            current: 10m,
            limit: 100m,
            periodStart,
            periodEnd,
            nowUtc: periodStart);

        Assert.Equal("Limit hit unknown", result);
    }

    [Fact]
    public void FormatLimitHit_WhenRateIsNonPositive_ReturnsUnknown()
    {
        var periodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var result = UsageBarViewModel.FormatLimitHit(
            current: 0m,
            limit: 100m,
            periodStart,
            periodEnd,
            nowUtc: periodStart.AddDays(1));

        Assert.Equal("Limit hit unknown", result);
    }

    [Fact]
    public void ResolveEasternTimeZone_WhenFirstZoneMissing_ReturnsNextZone()
    {
        var resolved = UsageBarViewModel.ResolveEasternTimeZone(
            ["missing-zone", "UTC"],
            id => id == "UTC" ? TimeZoneInfo.Utc : throw new TimeZoneNotFoundException(id));

        Assert.Same(TimeZoneInfo.Utc, resolved);
    }

    [Fact]
    public void ResolveEasternTimeZone_WhenZoneInvalid_ReturnsNull()
    {
        var resolved = UsageBarViewModel.ResolveEasternTimeZone(
            ["invalid-zone"],
            id => throw new InvalidTimeZoneException(id));

        Assert.Null(resolved);
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
