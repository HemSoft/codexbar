// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using Microsoft.Extensions.Logging;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public FileLoggerTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-logger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this._tempDir))
            {
                Directory.Delete(this._tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void CreateLogger_WhenMessageLogged_CreatesFile()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("TestCategory");
            logger.LogInformation("Hello world");
        }

        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public void Log_InfoLevel_WritesTimestampAndMessage()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("TestCategory");
            logger.LogInformation("Hello world");
        }

        var content = File.ReadAllText(logPath);
        Assert.Matches(@"\d{2}:\d{2}:\d{2}\.\d{3}", content);
        Assert.Contains("[INFO]", content);
        Assert.Contains("TestCategory", content);
        Assert.Contains("Hello world", content);
    }

    [Fact]
    public void Log_WarningLevel_WritesWarnMarker()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("Warn");
            logger.LogWarning("Danger");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("[WARN]", content);
        Assert.Contains("Danger", content);
    }

    [Fact]
    public void Log_ErrorLevel_WritesErrorMarker()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("Err");
            logger.LogError("Boom");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("[EROR]", content);
        Assert.Contains("Boom", content);
    }

    [Fact]
    public void Log_DebugLevel_WritesDebugMarker()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("Dbg");
            logger.LogDebug("Debug msg");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("[DBUG]", content);
        Assert.Contains("Debug msg", content);
    }

    [Fact]
    public void Log_TraceLevel_WritesTraceMarker()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("Trace");
            logger.LogTrace("Trace msg");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("[TRCE]", content);
    }

    [Fact]
    public void Log_CriticalLevel_WritesCriticalMarker()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("Crit");
            logger.LogCritical("Critical msg");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("[CRIT]", content);
    }

    [Fact]
    public void Log_WithException_IncludesExceptionDetails()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("TestCategory");
            var ex = new InvalidOperationException("test failure");
            logger.LogError(ex, "Something went wrong");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("Something went wrong", content);
        Assert.Contains("test failure", content);
        Assert.Contains("InvalidOperationException", content);
    }

    [Fact]
    public void Log_AfterProviderDisposed_DoesNotThrow()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");
        var provider = new FileLoggerProvider(logPath);
        var logger = provider.CreateLogger("TestCategory");
        provider.Dispose();

        // Should not throw — disposed provider silently ignores writes
        logger.LogInformation("After dispose");
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");
        var provider = new FileLoggerProvider(logPath);
        provider.Dispose();
        provider.Dispose(); // Should not throw
    }

    [Fact]
    public void IsEnabled_AnyLevel_ReturnsTrue()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");
        using var provider = new FileLoggerProvider(logPath);
        var logger = provider.CreateLogger("TestCategory");

        Assert.True(logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace));
        Assert.True(logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.None));
    }

    [Fact]
    public void BeginScope_WhenCalled_ReturnsNull()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");
        using var provider = new FileLoggerProvider(logPath);
        var logger = provider.CreateLogger("TestCategory");

        Assert.Null(logger.BeginScope("scope"));
    }

    [Fact]
    public void DefaultLogPath_WhenAccessed_ContainsCodexBar()
    {
        Assert.Contains("CodexBar", FileLoggerProvider.DefaultLogPath);
        Assert.Contains("log.txt", FileLoggerProvider.DefaultLogPath);
    }

    [Fact]
    public void CreateLogger_DirectoryMissing_CreatesDirectory()
    {
        var logDir = Path.Combine(this._tempDir, "sub", "dir");
        var logPath = Path.Combine(logDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("TestCategory");
            logger.LogInformation("Works");
        }

        Assert.True(Directory.Exists(logDir));
        Assert.True(File.Exists(logPath));
    }
}
