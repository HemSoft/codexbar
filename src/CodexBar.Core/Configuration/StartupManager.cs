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
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CodexBar";

    /// <summary>
    /// Gets or sets an internal hook for tests to supply a custom registry store,
    /// avoiding real-registry mutation. When null (the default), the real Windows Registry is used.
    /// </summary>
    internal static IStartupStore? TestStore { get; set; }

    /// <summary>
    /// Returns true if the CodexBar autostart entry exists in the registry.
    /// </summary>
    /// <returns></returns>
    public static bool IsEnabled()
    {
        if (TestStore is not null)
        {
            return TestStore.GetValue(AppName) is not null;
        }

        return IsEnabledFromSystem();
    }

    /// <summary>
    /// Adds or removes the autostart registry entry.
    /// The entry points to the currently running executable.
    /// Throws on failure so callers can revert UI state.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (TestStore is not null)
        {
            if (enabled)
            {
                TestStore.SetValue(AppName, "\"test-exe\"");
            }
            else
            {
                TestStore.DeleteValue(AppName);
            }

            return;
        }

        SetEnabledFromSystem(enabled);
    }

    [ExcludeFromCodeCoverage]
    private static bool IsEnabledFromSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodexBar] Failed to check startup state: {ex.Message}");
            return false;
        }
    }

    [ExcludeFromCodeCoverage]
    private static void SetEnabledFromSystem(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU Run key");

        if (enabled)
        {
            var exePath = GetExecutablePath();
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
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
