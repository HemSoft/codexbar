// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;

public sealed class RelayCommandTests
{
    [Fact]
    public void CanExecute_AlwaysReturnsTrue()
    {
        var command = new RelayCommand(_ => { });

        Assert.True(command.CanExecute(null));
        Assert.True(command.CanExecute("param"));
    }

    [Fact]
    public void Execute_InvokesAction()
    {
        var invoked = false;
        var command = new RelayCommand(_ => invoked = true);

        command.Execute(null);

        Assert.True(invoked);
    }

    [Fact]
    public void Execute_PassesParameter()
    {
        object? receivedParam = null;
        var command = new RelayCommand(p => receivedParam = p);

        command.Execute("test");

        Assert.Equal("test", receivedParam);
    }

    [Fact]
    public void Execute_PassesNullParameter()
    {
        object? receivedParam = "not null";
        var command = new RelayCommand(p => receivedParam = p);

        command.Execute(null);

        Assert.Null(receivedParam);
    }
}
