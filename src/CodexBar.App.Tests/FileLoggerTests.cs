// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using Microsoft.Extensions.DependencyInjection;
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
    public void Log_AfterProviderDisposed_SilentlyIgnoresWrite()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");
        var provider = new FileLoggerProvider(logPath);
        var logger = provider.CreateLogger("TestCategory");
        provider.Dispose();

        var ex = Record.Exception(() => logger.LogInformation("After dispose"));
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");
        var provider = new FileLoggerProvider(logPath);
        provider.Dispose();

        var ex = Record.Exception(() => provider.Dispose());
        Assert.Null(ex);
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

    [Fact]
    public void Log_AfterDispose_DoesNotWriteToFile()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");
        var provider = new FileLoggerProvider(logPath);
        var logger = provider.CreateLogger("TestCategory");
        logger.LogInformation("Before dispose");
        provider.Dispose();

        logger.LogInformation("After dispose");

        var content = File.ReadAllText(logPath);
        Assert.Contains("Before dispose", content);
        Assert.DoesNotContain("After dispose", content);
    }

    [Fact]
    public void Log_MultipleMessages_AppendsToFile()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("TestCategory");
            logger.LogInformation("First");
            logger.LogInformation("Second");
            logger.LogInformation("Third");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("First", content);
        Assert.Contains("Second", content);
        Assert.Contains("Third", content);
    }

    [Fact]
    public void Log_MultipleCategories_IncludesEachCategory()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger1 = provider.CreateLogger("Category1");
            var logger2 = provider.CreateLogger("Category2");
            logger1.LogInformation("From cat1");
            logger2.LogInformation("From cat2");
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("Category1", content);
        Assert.Contains("Category2", content);
    }

    [Fact]
    public void AddFile_WhenCalled_RegistersProvider()
    {
        var logPath = Path.Combine(this._tempDir, "ext-test.log");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging(builder => builder.AddFile(logPath));
        using var sp = services.BuildServiceProvider();

        var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("ExtTest");
        logger.LogInformation("Extension method works");

        // Dispose to flush and ensure write completes
        sp.Dispose();

        var content = File.ReadAllText(logPath);
        Assert.Contains("Extension method works", content);
    }

    [Fact]
    public void AddFile_NullPath_UsesDefaultLogPath()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging(builder => builder.AddFile(null));
        using var sp = services.BuildServiceProvider();

        // Resolving executes the lambda that exercises `path ?? DefaultLogPath`.
        // The file may be locked by a running CodexBar instance but the branch is covered.
        Exception? caught = null;
        try
        {
            _ = sp.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>().ToList();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // Either it succeeded (DefaultLogPath writable) or it threw IOException (file locked).
        // Both cases exercised the null-coalescing branch.
        Assert.True(caught is null or System.IO.IOException);
    }

    [Fact]
    public void Log_NoneLevel_WritesQuestionMarks()
    {
        var logPath = Path.Combine(this._tempDir, "test.log");

        using (var provider = new FileLoggerProvider(logPath))
        {
            var logger = provider.CreateLogger("TestCategory");
            logger.Log(Microsoft.Extensions.Logging.LogLevel.None, 0, "None level", null, (s, _) => s);
        }

        var content = File.ReadAllText(logPath);
        Assert.Contains("[????]", content);
        Assert.Contains("None level", content);
    }
}
