// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using Xunit;

public class StartupManagerTests : IDisposable
{
    private readonly InMemoryStartupStore store = new();

    public StartupManagerTests()
    {
        StartupManager.TestStore = this.store;
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
        Assert.NotNull(this.store.GetValue("CodexBar"));
    }

    [Fact]
    public void SetEnabled_False_RemovesValue()
    {
        StartupManager.SetEnabled(true);
        StartupManager.SetEnabled(false);
        Assert.Null(this.store.GetValue("CodexBar"));
    }

    private sealed class InMemoryStartupStore : IStartupStore
    {
        private readonly Dictionary<string, string> values = [];

        public object? GetValue(string name) =>
            this.values.TryGetValue(name, out var value) ? value : null;

        public void SetValue(string name, string value) =>
            this.values[name] = value;

        public void DeleteValue(string name) =>
            this.values.Remove(name);
    }
}
