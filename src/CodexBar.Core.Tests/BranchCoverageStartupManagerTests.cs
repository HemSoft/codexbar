// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;

/// <summary>
/// StartupManager branch coverage tests extracted from BranchCoverageTests to
/// participate in the [Collection("StartupManager")] serialization group and
/// prevent race conditions on the static TestStore property.
/// </summary>
[Collection("StartupManager")]
public class BranchCoverageStartupManagerTests : IDisposable
{
    private readonly InMemoryStartupStore _store = new();

    public BranchCoverageStartupManagerTests()
    {
        StartupManager.TestStore = this._store;
    }

    public void Dispose()
    {
        StartupManager.TestStore = null;
    }

    /// <summary>
    /// Exercises IsEnabled when TestStore.GetValue returns null (line 37: the null branch).
    /// </summary>
    [Fact]
    public void IsEnabled_TestStoreReturnsNull_ReturnsFalse()
    {
        StartupManager.TestStore = new NullReturningStore();
        Assert.False(StartupManager.IsEnabled());
    }

    /// <summary>
    /// Exercises SetEnabled(true) then SetEnabled(false) through TestStore path (complete branch coverage).
    /// </summary>
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

        public object? GetValue(string name) => this._values.GetValueOrDefault(name);

        public void SetValue(string name, string value) => this._values[name] = value;

        public void DeleteValue(string name) => this._values.Remove(name);
    }
}
