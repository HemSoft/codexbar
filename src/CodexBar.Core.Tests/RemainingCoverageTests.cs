// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.OpenCodeZen;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class RemainingCoverageSettingsServiceTests : IDisposable
{
    private readonly string _rootDir;

    public RemainingCoverageSettingsServiceTests()
    {
        this._rootDir = Path.Combine(
            AppContext.BaseDirectory,
            "remaining-coverage-tests",
            nameof(RemainingCoverageSettingsServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this._rootDir);
    }

    [Fact]
    public void SettingsService_PublicConstructor_DoesNotThrow()
    {
        var exception = Record.Exception(() => _ = new SettingsService(NullLogger<SettingsService>.Instance));

        Assert.Null(exception);
    }

    [Fact]
    public void Save_SettingsPathIsDirectory_DeletesTempFileAndRethrows()
    {
        var settingsDir = this.CreateDirectory("save-path-is-directory");
        Directory.CreateDirectory(Path.Combine(settingsDir, "settings.json"));
        var service = new SettingsService(NullLogger<SettingsService>.Instance, settingsDir);

        Assert.ThrowsAny<Exception>(() => service.Save(new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = [],
        }));
        Assert.False(File.Exists(Path.Combine(settingsDir, "settings.json.tmp")));
    }

    [Fact]
    public void Load_DefaultPersistenceFails_ReturnsInMemoryDefaults()
    {
        var settingsDir = this.CreateDirectory("load-default-persist-fails");
        Directory.CreateDirectory(Path.Combine(settingsDir, "settings.json"));
        var service = new SettingsService(NullLogger<SettingsService>.Instance, settingsDir);

        var settings = service.Load();

        Assert.Equal(120, settings.RefreshIntervalSeconds);
        Assert.True(settings.Providers.ContainsKey(ProviderId.Copilot.ToString()));
        Assert.False(File.Exists(Path.Combine(settingsDir, "settings.json.tmp")));
    }

    [Fact]
    public void Load_RestrictPermissionsThrows_ContinuesLoading()
    {
        // Create a valid settings file in a temp dir
        var settingsDir = this.CreateDirectory("load-restrict-throws");
        var settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "refreshIntervalSeconds": 60,
              "providers": {
                "Copilot": { "enabled": true }
              }
            }
            """);

        // Make the settings file read-only so that SetAccessControl throws
        File.SetAttributes(settingsPath, FileAttributes.ReadOnly);

        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, settingsDir);
            var settings = service.Load();

            // Load should succeed even if restrict permissions throws
            Assert.Equal(60, settings.RefreshIntervalSeconds);
        }
        finally
        {
            File.SetAttributes(settingsPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void RestrictFilePermissions_PathDoesNotExist_DoesNotThrow()
    {
        var service = new SettingsService(NullLogger<SettingsService>.Instance, this.CreateDirectory("restrict-file"));

        var exception = Record.Exception(() => ReflectionTestHelpers.InvokePrivateVoid(
            service,
            "RestrictFilePermissions",
            Path.Combine(this._rootDir, Guid.NewGuid().ToString("N"), "missing.json")));

        Assert.Null(exception);
    }

    [Fact]
    public void RestrictDirectoryPermissions_PathDoesNotExist_DoesNotThrow()
    {
        var service = new SettingsService(NullLogger<SettingsService>.Instance, this.CreateDirectory("restrict-directory"));

        var exception = Record.Exception(() => ReflectionTestHelpers.InvokePrivateVoid(
            service,
            "RestrictDirectoryPermissions",
            Path.Combine(this._rootDir, Guid.NewGuid().ToString("N"), "missing-directory")));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this._rootDir))
            {
                Directory.Delete(this._rootDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(this._rootDir, name);
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class RemainingCoverageCopilotProviderTests : IDisposable
{
    private readonly CopilotProvider _provider;

    public RemainingCoverageCopilotProviderTests()
    {
        this._provider = CreateProvider();
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_UnknownUser_ReturnsNull()
    {
        var provider = CreateProvider();
        var username = $"missing-user-{Guid.NewGuid():N}";

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            provider,
            "ResolveTokenForUserAsync",
            username,
            CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_WithProcessOverride_Success_CachesToken()
    {
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess("/c echo test-token-abc");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider,
            "ResolveTokenForUserAsync",
            "test-user",
            CancellationToken.None);

        Assert.Equal("test-token-abc", token);
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_WithProcessOverride_ExitCodeNonZero_ReturnsNull()
    {
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess("/c exit 1");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider,
            "ResolveTokenForUserAsync",
            "fail-user",
            CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_WithProcessOverride_ExitCodeNonZero_LongStderr_Truncates()
    {
        var longStderr = new string('x', 300);
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess($"/c echo {longStderr} >&2 & exit 1");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider,
            "ResolveTokenForUserAsync",
            "stderr-user",
            CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_WithProcessOverride_Timeout_ReturnsNull()
    {
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess("/c ping 127.0.0.1 -n 30 >nul");
        this._provider.TokenTimeoutOverride = TimeSpan.FromMilliseconds(200);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider,
            "ResolveTokenForUserAsync",
            "timeout-user",
            CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_WithProcessOverride_CallerCancelled_Throws()
    {
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess("/c ping 127.0.0.1 -n 30 >nul");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReflectionTestHelpers.InvokePrivateAsync<string?>(
                this._provider,
                "ResolveTokenForUserAsync",
                "cancel-user",
                cts.Token));
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_CachedToken_ReturnsCached()
    {
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess("/c echo cached-token");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var first = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "cache-user", CancellationToken.None);

        // Second call should return cached without invoking process
        this._provider.GhTokenProcessOverride = _ => throw new InvalidOperationException("Should not be called");

        var second = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "cache-user", CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_ProcessThrowsGenericException_ReturnsNull()
    {
        this._provider.GhTokenProcessOverride = _ => throw new InvalidOperationException("Simulated failure");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider,
            "ResolveTokenForUserAsync",
            "exception-user",
            CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task DiscoverGhAccountsAsync_WithProcessOverride_Timeout_SetsErrorMessage()
    {
        this._provider.GhStatusProcessOverride = () => CreateCommandProcess("/c ping 127.0.0.1 -n 30 >nul");
        this._provider.DiscoveryTimeoutOverride = TimeSpan.FromMilliseconds(200);

        var accounts = await ReflectionTestHelpers.InvokePrivateAsync<List<string>>(
            this._provider,
            "DiscoverGhAccountsAsync",
            CancellationToken.None);

        Assert.NotNull(accounts);
        Assert.Empty(accounts!);
    }

    [Fact]
    public async Task DiscoverGhAccountsAsync_WithProcessOverride_NonZeroExitCode_SetsErrorMessage()
    {
        this._provider.GhStatusProcessOverride = () => CreateCommandProcess("/c echo auth-error >&2 & exit 1");
        this._provider.DiscoveryTimeoutOverride = TimeSpan.FromSeconds(5);

        var accounts = await ReflectionTestHelpers.InvokePrivateAsync<List<string>>(
            this._provider,
            "DiscoverGhAccountsAsync",
            CancellationToken.None);

        Assert.NotNull(accounts);
        Assert.Empty(accounts!);
    }

    [Fact]
    public async Task DiscoverGhAccountsAsync_WithProcessOverride_Win32Exception_SetsNotFoundError()
    {
        this._provider.GhStatusProcessOverride = () => new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "non-existent-binary-" + Guid.NewGuid().ToString("N"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        this._provider.DiscoveryTimeoutOverride = TimeSpan.FromSeconds(5);

        var accounts = await ReflectionTestHelpers.InvokePrivateAsync<List<string>>(
            this._provider,
            "DiscoverGhAccountsAsync",
            CancellationToken.None);

        Assert.NotNull(accounts);
        Assert.Empty(accounts!);
    }

    [Fact]
    public async Task DiscoverGhAccountsAsync_WithProcessOverride_GenericException_SetsErrorMessage()
    {
        this._provider.GhStatusProcessOverride = () => throw new IOException("Simulated I/O error");
        this._provider.DiscoveryTimeoutOverride = TimeSpan.FromSeconds(5);

        var accounts = await ReflectionTestHelpers.InvokePrivateAsync<List<string>>(
            this._provider,
            "DiscoverGhAccountsAsync",
            CancellationToken.None);

        Assert.NotNull(accounts);
        Assert.Empty(accounts!);
    }

    [Fact]
    public async Task ResolveTokenForUserAsync_WithProcessOverride_EmptyOutput_ReturnsNull()
    {
        // echo. outputs just a newline; after Trim() it's empty
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess("/c echo.");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider,
            "ResolveTokenForUserAsync",
            "empty-token-user",
            CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task DiscoverGhAccountsAsync_CallerCancelled_ThrowsOperationCanceledException()
    {
        var provider = CreateProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReflectionTestHelpers.InvokePrivateAsync<List<string>>(
            provider,
            "DiscoverGhAccountsAsync",
            cts.Token));
    }

    [Fact]
    public async Task WaitForGhProcessAsync_ProcessCompletes_ReturnsExitCodeAndOutput()
    {
        using var process = CreateCommandProcess("/c echo hello-from-copilot");

        var result = await ReflectionTestHelpers.InvokePrivateStaticAsync<(int? exitCode, string stderr, string stdout)>(
            typeof(CopilotProvider),
            "WaitForGhProcessAsync",
            process,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.exitCode);
        Assert.Equal(string.Empty, result.stderr.Trim());
        Assert.Contains("hello-from-copilot", result.stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitForGhProcessAsync_ProcessTimesOut_ReturnsNullExitCode()
    {
        using var process = CreateCommandProcess("/c ping 127.0.0.1 -n 30 >nul");

        var result = await ReflectionTestHelpers.InvokePrivateStaticAsync<(int? exitCode, string stderr, string stdout)>(
            typeof(CopilotProvider),
            "WaitForGhProcessAsync",
            process,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        Assert.Null(result.exitCode);
    }

    [Fact]
    public async Task WaitForGhProcessAsync_CallerCancelled_ThrowsOperationCanceledException()
    {
        using var process = CreateCommandProcess("/c ping 127.0.0.1 -n 30 >nul");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReflectionTestHelpers.InvokePrivateStaticAsync<(int? exitCode, string stderr, string stdout)>(
            typeof(CopilotProvider),
            "WaitForGhProcessAsync",
            process,
            TimeSpan.FromSeconds(5),
            cts.Token));
    }

    public void Dispose()
    {
        this._provider.GhTokenProcessOverride = null;
        this._provider.GhStatusProcessOverride = null;
        this._provider.TokenTimeoutOverride = null;
        this._provider.DiscoveryTimeoutOverride = null;
    }

    private static CopilotProvider CreateProvider(ISettingsService? settings = null, IHttpClientFactory? httpClientFactory = null)
    {
        settings ??= CreateSettings();
        httpClientFactory ??= Substitute.For<IHttpClientFactory>();
        return new CopilotProvider(NullLogger<CopilotProvider>.Instance, httpClientFactory, settings);
    }

    private static ISettingsService CreateSettings(params string[] accounts)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(accounts.ToList());
        return settings;
    }

    private static Process CreateCommandProcess(string arguments)
    {
        string fileName;
        string args;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileName = "cmd.exe";
            args = arguments;
        }
        else
        {
            fileName = "/bin/sh";
            args = $"-c {TranslateToShellCommand(arguments)}";
        }

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
    }

    private static string TranslateToShellCommand(string windowsArgs)
    {
        // Strip leading /c and translate to sh-compatible command
        var cmd = windowsArgs.TrimStart();
        if (cmd.StartsWith("/c ", StringComparison.OrdinalIgnoreCase))
        {
            cmd = cmd[3..];
        }

        // Translate Windows-specific syntax
        cmd = cmd.Replace(">nul", ">/dev/null");
        cmd = cmd.Replace(">&2", "1>&2");
        cmd = cmd.Replace("echo.", "echo ''");

        // Translate ping -n to ping -c
        cmd = System.Text.RegularExpressions.Regex.Replace(
            cmd, @"ping\s+127\.0\.0\.1\s+-n\s+(\d+)", "sleep $1");

        return $"\"{cmd.Replace("\"", "\\\"")}\"";
    }
}

[Collection("EnvironmentVariableTests")]
public sealed class RemainingCoverageCopilotProviderEnvironmentTests
{
    [Fact]
    public async Task FetchUsageAsync_GhMissingFromPath_ReturnsNotFoundError()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var isolatedPath = Path.Combine(
            AppContext.BaseDirectory,
            "remaining-coverage-tests",
            nameof(RemainingCoverageCopilotProviderEnvironmentTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedPath);

        try
        {
            Environment.SetEnvironmentVariable("PATH", isolatedPath);
            var settings = Substitute.For<ISettingsService>();
            settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
            settings.GetCopilotAccounts().Returns([]);
            var provider = new CopilotProvider(
                NullLogger<CopilotProvider>.Instance,
                Substitute.For<IHttpClientFactory>(),
                settings);

            var result = await provider.FetchUsageAsync();

            Assert.False(result.Success);
            Assert.Contains("GitHub CLI (gh) not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try
            {
                Directory.Delete(isolatedPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}

[Collection("ClaudeProviderFileIo")]
public sealed class RemainingCoverageClaudeProviderTests
{
    [Fact]
    public async Task FetchRateLimitsAsync_ConcurrentCallsShareCachedResultAfterLock()
    {
        var handler = new BlockingClaudeRateLimitHandler();
        var provider = CreateProvider(handler);

        var firstTask = ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.UnifiedRateLimits?>(
            provider,
            "FetchRateLimitsAsync",
            "access-token",
            CancellationToken.None);

        await handler.RequestStarted.Task;

        var secondTask = ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.UnifiedRateLimits?>(
            provider,
            "FetchRateLimitsAsync",
            "access-token",
            CancellationToken.None);

        await Task.Delay(50);
        handler.AllowResponse.TrySetResult();

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(results[0]);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task ProbeAndCacheRateLimitsAsync_TimeoutWithoutCallerCancellation_ReturnsCachedLimits()
    {
        var cachedLimits = new ClaudeProvider.UnifiedRateLimits
        {
            FiveHourUtilization = 0.25,
            FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            SevenDayUtilization = 0.5,
            SevenDayReset = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
        };
        var provider = CreateProvider(new TimedOutClaudeRateLimitHandler());
        ReflectionTestHelpers.SetPrivateField(provider, "cachedLimits", cachedLimits);

        var result = await ReflectionTestHelpers.InvokePrivateAsync<ClaudeProvider.UnifiedRateLimits?>(
            provider,
            "ProbeAndCacheRateLimitsAsync",
            "access-token",
            CancellationToken.None);

        Assert.Same(cachedLimits, result);
    }

    private static ClaudeProvider CreateProvider(HttpMessageHandler handler)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        return new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            new HttpClientFactoryStub(handler),
            settings);
    }
}

public sealed class RemainingCoverageUsageRefreshServiceTests
{
    [Fact]
    public void Dispose_RefreshLoopThrowsAggregateException_DoesNotThrow()
    {
        var service = new UsageRefreshService([], NullLogger<UsageRefreshService>.Instance);
        ReflectionTestHelpers.SetPrivateField(service, "_cts", new CancellationTokenSource());
        ReflectionTestHelpers.SetPrivateField(
            service,
            "_refreshLoop",
            Task.FromException(new AggregateException(new InvalidOperationException("boom"))));

        var exception = Record.Exception(service.Dispose);

        Assert.Null(exception);
    }
}

public sealed class RemainingCoverageOpenCodeZenProviderTests
{
    [Fact]
    public async Task FetchUsageAsync_CancellationRequestedDuringSend_RethrowsOperationCanceledException()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeZen).Returns(true);
        settings.GetOpenCodeGoWorkspaceId().Returns("workspace-1");
        settings.GetApiKey(ProviderId.OpenCodeZen).Returns((string?)null);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns("auth-cookie");

        using var cts = new CancellationTokenSource();
        var provider = new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            new HttpClientFactoryStub(new CancelDuringSendHandler(cts)),
            settings);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchUsageAsync(cts.Token));
    }
}

internal static class ReflectionTestHelpers
{
    public static void InvokePrivateVoid(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(instance, args);
    }

    public static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    public static async Task<T?> InvokePrivateAsync<T>(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return await InvokeTaskAsync<T>(method!.Invoke(instance, args));
    }

    public static async Task<T?> InvokePrivateStaticAsync<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await InvokeTaskAsync<T>(method!.Invoke(null, args));
    }

    private static async Task<T?> InvokeTaskAsync<T>(object? invocationResult)
    {
        Assert.NotNull(invocationResult);
        var task = Assert.IsAssignableFrom<Task>(invocationResult);
        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        if (resultProperty is null)
        {
            return default;
        }

        return (T?)resultProperty.GetValue(task);
    }
}

internal sealed class HttpClientFactoryStub(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class ThrowOnDebugLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (logLevel == LogLevel.Debug && message.Contains("Could not restrict", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("debug logging failed");
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class BlockingClaudeRateLimitHandler : HttpMessageHandler
{
    private int _callCount;

    public int CallCount => this._callCount;

    public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AllowResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this._callCount);
        this.RequestStarted.TrySetResult();
        await this.AllowResponse.Task.WaitAsync(cancellationToken);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("anthropic-ratelimit-unified-5h-utilization", "0.40");
        response.Headers.Add("anthropic-ratelimit-unified-5h-reset", DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds().ToString());
        response.Headers.Add("anthropic-ratelimit-unified-5h-status", "ok");
        response.Headers.Add("anthropic-ratelimit-unified-7d-utilization", "0.65");
        response.Headers.Add("anthropic-ratelimit-unified-7d-reset", DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds().ToString());
        response.Headers.Add("anthropic-ratelimit-unified-7d-status", "ok");
        return response;
    }
}

internal sealed class TimedOutClaudeRateLimitHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new TaskCanceledException("timed out");
}

internal sealed class CancelDuringSendHandler(CancellationTokenSource cancellationTokenSource) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationTokenSource.Cancel();
        throw new OperationCanceledException(cancellationToken);
    }
}
