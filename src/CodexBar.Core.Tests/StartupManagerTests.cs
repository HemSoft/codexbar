// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using Xunit;

[Collection("StartupManager")]
public class StartupManagerTests : IDisposable
{
    private readonly InMemoryStartupStore _store = new();

    public StartupManagerTests()
    {
        StartupManager.TestStore = this._store;
    }

    public void Dispose()
    {
        StartupManager.TestStore = null;
    }

    [Fact]
    public void IsEnabled_WhenNotSet_ReturnsFalse()
    {
        Assert.False(StartupManager.IsEnabled());
    }

    [Fact]
    public void IsEnabled_AfterEnabling_ReturnsTrue()
    {
        StartupManager.SetEnabled(true);
        Assert.True(StartupManager.IsEnabled());
    }

    [Fact]
    public void IsEnabled_AfterDisabling_ReturnsFalse()
    {
        StartupManager.SetEnabled(true);
        StartupManager.SetEnabled(false);
        Assert.False(StartupManager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_True_SetsValue()
    {
        StartupManager.SetEnabled(true);
        Assert.NotNull(this._store.GetValue("CodexBar"));
    }

    [Fact]
    public void SetEnabled_False_RemovesValue()
    {
        StartupManager.SetEnabled(true);
        StartupManager.SetEnabled(false);
        Assert.Null(this._store.GetValue("CodexBar"));
    }

    [Fact]
    public void IsEnabled_TestStoreReturnsNull_ReturnsFalse()
    {
        StartupManager.TestStore = new NullReturningStore();
        Assert.False(StartupManager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_Toggle_CoversBothBranches()
    {
        StartupManager.SetEnabled(true);
        Assert.True(StartupManager.IsEnabled());

        StartupManager.SetEnabled(false);
        Assert.False(StartupManager.IsEnabled());
    }

    private sealed class NullReturningStore : IStartupStore
    {
        public object? GetValue(string name) => null;

        public void SetValue(string name, string value)
        {
        }

        public void DeleteValue(string name)
        {
        }
    }

    private sealed class InMemoryStartupStore : IStartupStore
    {
        private readonly Dictionary<string, string> _values = [];

        public object? GetValue(string name) =>
            this._values.TryGetValue(name, out var value) ? value : null;

        public void SetValue(string name, string value) =>
            this._values[name] = value;

        public void DeleteValue(string name) =>
            this._values.Remove(name);
    }
}
