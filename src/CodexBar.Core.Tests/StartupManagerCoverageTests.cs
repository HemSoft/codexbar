// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;

/// <summary>
/// Additional coverage tests for StartupManager exercising the real registry paths
/// (on Windows) and exception handling branches.
/// </summary>
[Collection("StartupManager")]
public class StartupManagerCoverageTests : IDisposable
{
    private readonly InMemoryStartupStore _store = new();

    public StartupManagerCoverageTests()
    {
        StartupManager.TestStore = this._store;
    }

    public void Dispose()
    {
        StartupManager.TestStore = null;
    }

    [Fact]
    public void SetEnabled_True_ThenTrue_StaysEnabled()
    {
        StartupManager.SetEnabled(true);
        StartupManager.SetEnabled(true);
        Assert.True(StartupManager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_False_WhenNotSet_DoesNotThrow()
    {
        // Delete on a key that doesn't exist should not throw
        StartupManager.SetEnabled(false);
        Assert.False(StartupManager.IsEnabled());
    }

    [Fact]
    public void IsEnabled_WithTestStore_ReturnsCorrectly()
    {
        Assert.False(StartupManager.IsEnabled());
        this._store.SetValue("CodexBar", "\"test.exe\"");
        Assert.True(StartupManager.IsEnabled());
    }

    [Fact]
    public void SetEnabled_True_SetsTestExeValue()
    {
        StartupManager.SetEnabled(true);
        var value = this._store.GetValue("CodexBar") as string;
        Assert.Equal("\"test-exe\"", value);
    }

    /// <summary>
    /// Tests the real registry code path on Windows.
    /// When TestStore is null, it uses the actual registry.
    /// We test the IsEnabled path which only reads (safe).
    /// </summary>
    [Fact]
    public void IsEnabled_WithoutTestStore_UsesRealRegistry()
    {
        StartupManager.TestStore = null;

        if (!OperatingSystem.IsWindows())
        {
            // On non-Windows, should return false
            Assert.False(StartupManager.IsEnabled());
            return;
        }

        // On Windows, just verify it doesn't throw and returns a bool
        var result = StartupManager.IsEnabled();
        Assert.IsType<bool>(result);
    }

    /// <summary>
    /// Tests SetEnabled with real registry - enable then disable to clean up.
    /// Skips gracefully on environments where HKCU Run key is not writable (e.g., CI runners).
    /// </summary>
    [Fact]
    public void SetEnabled_WithoutTestStore_EnableThenDisable_DoesNotThrow()
    {
        StartupManager.TestStore = null;

        if (!OperatingSystem.IsWindows())
        {
            // On non-Windows, SetEnabled is a no-op
            StartupManager.SetEnabled(true);
            StartupManager.SetEnabled(false);
            return;
        }

        // On Windows, enable then immediately disable to avoid leaving registry entries.
        // In restricted environments (CI), the registry key may not be writable.
        try
        {
            StartupManager.SetEnabled(true);
        }
        catch (InvalidOperationException)
        {
            // Registry not writable in this environment (e.g., CI runner) — skip
            return;
        }

        try
        {
            Assert.True(StartupManager.IsEnabled());
        }
        finally
        {
            StartupManager.SetEnabled(false);
        }

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
}
