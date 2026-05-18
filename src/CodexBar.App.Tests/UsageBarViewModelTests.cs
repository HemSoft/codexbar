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
