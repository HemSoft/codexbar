// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Configuration;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

/// <summary>
/// Manages the "Start with Windows" registry entry under HKCU\...\Run.
/// Only functional on Windows; silently no-ops on other platforms.
/// </summary>
public static class StartupManager
{
    private const string AppName = "CodexBar";

    /// <summary>
    /// Gets or sets an internal hook for tests to supply a custom registry store,
    /// avoiding real-registry mutation. When null (the default), the real Windows Registry is used.
    /// </summary>
    internal static IStartupStore? TestStore { get; set; }

    private static IStartupStore Store => TestStore ?? WindowsStartupStore.Instance;

    /// <summary>
    /// Returns true if the CodexBar autostart entry exists in the registry.
    /// </summary>
    /// <returns>True if the CodexBar entry exists in the startup registry key.</returns>
    public static bool IsEnabled()
    {
        return Store.GetValue(AppName) is not null;
    }

    /// <summary>
    /// Adds or removes the autostart registry entry.
    /// The entry points to the currently running executable.
    /// Throws on failure so callers can revert UI state.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var exePath = TestStore is not null ? "test-exe" : GetExecutablePath();
            Store.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            Store.DeleteValue(AppName);
        }
    }

    [ExcludeFromCodeCoverage]
    private static string GetExecutablePath()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrEmpty(exePath))
        {
            throw new InvalidOperationException("Unable to determine executable path");
        }

        return exePath;
    }
}

/// <summary>
/// Abstraction for registry operations, used internally for hermetic testing.
/// </summary>
internal interface IStartupStore
{
    object? GetValue(string name);

    void SetValue(string name, string value);

    void DeleteValue(string name);
}

/// <summary>
/// Windows Registry implementation of <see cref="IStartupStore"/>.
/// Wraps HKCU\Software\Microsoft\Windows\CurrentVersion\Run operations.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class WindowsStartupStore : IStartupStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static readonly WindowsStartupStore Instance = new();

    public object? GetValue(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodexBar] Failed to check startup state: {ex.Message}");
            return null;
        }
    }

    public void SetValue(string name, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU Run key");

        key.SetValue(name, value);
    }

    public void DeleteValue(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU Run key");

        key.DeleteValue(name, throwOnMissingValue: false);
    }
}
