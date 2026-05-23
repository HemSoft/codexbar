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
    public void BestEffortKillAndDrain_ProcessNotStarted_SwallowsKillException()
    {
        using var process = new Process();

        var stderrTask = Task.FromResult(string.Empty);
        var stdoutTask = Task.FromResult(string.Empty);

        var ex = Record.Exception(() =>
            CopilotProvider.BestEffortKillAndDrain(process, stderrTask, stdoutTask));
        Assert.Null(ex);
    }

    [Fact]
    public void BestEffortKillAndDrain_FaultedTasks_SwallowsTaskExceptions()
    {
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

        var faultedStderr = Task.FromException<string>(new InvalidOperationException("stderr fault"));
        var faultedStdout = Task.FromException<string>(new InvalidOperationException("stdout fault"));

        var ex = Record.Exception(() =>
            CopilotProvider.BestEffortKillAndDrain(process, faultedStderr, faultedStdout));
        Assert.Null(ex);
    }

    [Fact]
    public void BestEffortKillAndDrain_AllThrow_SwallowsAllExceptions()
    {
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

        var faultedStderr = Task.FromException<string>(new InvalidOperationException("stderr"));
        var faultedStdout = Task.FromException<string>(new InvalidOperationException("stdout"));

        var ex = Record.Exception(() =>
            CopilotProvider.BestEffortKillAndDrain(process, faultedStderr, faultedStdout));
        Assert.Null(ex);
    }

    private static (string FileName, string Arguments) GetShellCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", "/c exit 0")
            : ("/bin/sh", "-c \"exit 0\"");
}
