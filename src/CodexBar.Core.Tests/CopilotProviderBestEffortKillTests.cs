// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexBar.Core.Providers.Copilot;

/// <summary>
/// Tests for CopilotProvider.BestEffortKillAndDrain covering the catch blocks
/// that swallow exceptions when process.Kill() or task reads throw.
/// </summary>
public class CopilotProviderBestEffortKillTests
{
    [Fact]
    public void BestEffortKillAndDrain_ProcessNotStarted_DoesNotThrow()
    {
        // A Process object with no associated OS process throws
        // InvalidOperationException from Kill()
        using var process = new Process();

        var stderrTask = Task.FromResult(string.Empty);
        var stdoutTask = Task.FromResult(string.Empty);

        // This should not throw - the Kill() exception is swallowed
        CopilotProvider.BestEffortKillAndDrain(process, stderrTask, stdoutTask);
    }

    [Fact]
    public void BestEffortKillAndDrain_FaultedTasks_DoesNotThrow()
    {
        // Start a short-lived process using a cross-platform shell
        var (fileName, arguments) = GetShellCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        process.WaitForExit();

        // Create faulted tasks that will throw when GetAwaiter().GetResult() is called
        var faultedStderr = Task.FromException<string>(new InvalidOperationException("stderr fault"));
        var faultedStdout = Task.FromException<string>(new InvalidOperationException("stdout fault"));

        // This should not throw - all catches are swallowed
        CopilotProvider.BestEffortKillAndDrain(process, faultedStderr, faultedStdout);
    }

    [Fact]
    public void BestEffortKillAndDrain_AllThrow_DoesNotThrow()
    {
        // Start a process that exits immediately so Kill() throws
        var (fileName, arguments) = GetShellCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        process.WaitForExit();

        // All three operations will throw: Kill() because process exited,
        // and both tasks because they're faulted
        var faultedStderr = Task.FromException<string>(new InvalidOperationException("stderr"));
        var faultedStdout = Task.FromException<string>(new InvalidOperationException("stdout"));

        CopilotProvider.BestEffortKillAndDrain(process, faultedStderr, faultedStdout);
    }

    private static (string FileName, string Arguments) GetShellCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", "/c exit 0")
            : ("/bin/sh", "-c \"exit 0\"");
}
