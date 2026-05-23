// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;

/// <summary>
/// Tests for StartupManager error and edge-case paths: store failures,
/// already-enabled/disabled states, and exception propagation behavior.
/// </summary>
[Collection("StartupManager")]
public class StartupManagerErrorPathTests : IDisposable
{
    private readonly InMemoryStartupStore _store = new();

    public StartupManagerErrorPathTests()
    {
        StartupManager.TestStore = this._store;
    }

    public void Dispose()
    {
        StartupManager.TestStore = null;
    }

    [Fact]
    public void SetEnabled_True_WhenSetValueThrows_PropagatesException()
    {
        var throwingStore = new ThrowingStartupStore(throwOnSet: true);
        StartupManager.TestStore = throwingStore;

        var ex = Assert.Throws<InvalidOperationException>(() => StartupManager.SetEnabled(true));
        Assert.Contains("SetValue failed", ex.Message);
    }

    [Fact]
    public void SetEnabled_False_WhenDeleteValueThrows_PropagatesException()
    {
        var throwingStore = new ThrowingStartupStore(throwOnDelete: true);
        StartupManager.TestStore = throwingStore;

        var ex = Assert.Throws<InvalidOperationException>(() => StartupManager.SetEnabled(false));
        Assert.Contains("DeleteValue failed", ex.Message);
    }

    [Fact]
    public void IsEnabled_WhenGetValueThrows_PropagatesException()
    {
        var throwingStore = new ThrowingStartupStore(throwOnGet: true);
        StartupManager.TestStore = throwingStore;

        var ex = Assert.Throws<InvalidOperationException>(() => StartupManager.IsEnabled());
        Assert.Contains("GetValue failed", ex.Message);
    }

    [Fact]
    public void SetEnabled_True_WhenAlreadyEnabled_OverwritesValue()
    {
        StartupManager.SetEnabled(true);
        var firstValue = this._store.GetValue("CodexBar") as string;

        StartupManager.SetEnabled(true);
        var secondValue = this._store.GetValue("CodexBar") as string;

        Assert.Equal(firstValue, secondValue);
        Assert.True(StartupManager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_False_WhenAlreadyDisabled_RemainsDisabled()
    {
        Assert.False(StartupManager.IsEnabled());

        var ex = Record.Exception(() => StartupManager.SetEnabled(false));
        Assert.Null(ex);
        Assert.False(StartupManager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_True_StoresQuotedPath()
    {
        StartupManager.SetEnabled(true);
        var value = this._store.GetValue("CodexBar") as string;

        Assert.NotNull(value);
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);
    }

    [Fact]
    public void SetEnabled_True_ThenFalse_ThenTrue_TogglesCorrectly()
    {
        StartupManager.SetEnabled(true);
        Assert.True(StartupManager.IsEnabled());

        StartupManager.SetEnabled(false);
        Assert.False(StartupManager.IsEnabled());

        StartupManager.SetEnabled(true);
        Assert.True(StartupManager.IsEnabled());
    }

    [Fact]
    public void IsEnabled_AfterSetEnabledTrue_ReturnsTrue()
    {
        StartupManager.SetEnabled(true);
        Assert.True(StartupManager.IsEnabled());
    }

    [Fact]
    public void IsEnabled_AfterSetEnabledFalse_ReturnsFalse()
    {
        StartupManager.SetEnabled(true);
        StartupManager.SetEnabled(false);
        Assert.False(StartupManager.IsEnabled());
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

    private sealed class ThrowingStartupStore(
        bool throwOnGet = false,
        bool throwOnSet = false,
        bool throwOnDelete = false) : IStartupStore
    {
        public object? GetValue(string name) =>
            throwOnGet
                ? throw new InvalidOperationException("GetValue failed")
                : null;

        public void SetValue(string name, string value)
        {
            if (throwOnSet)
            {
                throw new InvalidOperationException("SetValue failed");
            }
        }

        public void DeleteValue(string name)
        {
            if (throwOnDelete)
            {
                throw new InvalidOperationException("DeleteValue failed");
            }
        }
    }
}
