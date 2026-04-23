using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexBar.App;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public FileLoggerProvider(string logPath)
    {
        _logPath = logPath;
        var dir = Path.GetDirectoryName(logPath)!;
        Directory.CreateDirectory(dir);
        _writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }

    internal void Write(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine(message);
        }
    }

    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "log.txt");
}

file sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var level = logLevel switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "EROR",
            LogLevel.Critical => "CRIT",
            _ => "????"
        };
        var message = $"{timestamp} [{level}] {categoryName}: {formatter(state, exception)}";
        if (exception is not null)
            message += Environment.NewLine + exception;
        provider.Write(message);
    }
}

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string? path = null)
    {
        builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(path ?? FileLoggerProvider.DefaultLogPath));
        return builder;
    }
}
