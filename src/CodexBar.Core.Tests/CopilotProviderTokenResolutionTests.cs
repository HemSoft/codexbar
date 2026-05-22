// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests for the refactored ResolveTokenForUserAsync and its extracted helpers:
/// ResolveTokenViaOverrideAsync, ResolveTokenViaGhCliAsync, CreateGhTokenProcess,
/// LogNonZeroGhTokenExit, and CacheTokenIfValid.
/// Covers success, missing token, invalid token, and multi-user branches.
/// </summary>
public sealed class CopilotProviderTokenResolutionTests : IDisposable
{
    private readonly CopilotProvider _provider;

    public CopilotProviderTokenResolutionTests()
    {
        this._provider = CreateProvider();
    }

    // --- ResolveTokenViaOverrideAsync: valid token is cached ---
    [Fact]
    public async Task ResolveToken_OverrideReturnsValidToken_CachesAndReturnsTokenAsync()
    {
        var callCount = 0;
        var json = BuildCopilotJson();
        var settings = CreateSettings("alice");
        var httpFactory = CreateFactory(json);
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) =>
        {
            callCount++;
            return Task.FromResult<string?>("gho_valid_token");
        };

        var result1 = await provider.FetchUsageAsync();
        var result2 = await provider.FetchUsageAsync();

        Assert.True(result1.Success);
        Assert.True(result2.Success);
        Assert.Equal(1, callCount); // Cached after first call
    }

    // --- ResolveTokenViaOverrideAsync: null token not cached ---
    [Fact]
    public async Task ResolveToken_OverrideReturnsNull_NotCachedRetriedOnNextCallAsync()
    {
        var callCount = 0;
        var settings = CreateSettings("bob");
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(settings, httpFactory);
        provider.TokenResolverOverride = (_, _) =>
        {
            callCount++;
            return Task.FromResult<string?>(null);
        };

        await provider.FetchUsageAsync();
        await provider.FetchUsageAsync();

        Assert.Equal(2, callCount); // Not cached, retried each time
    }

    // --- ResolveTokenViaGhCliAsync: success with process override ---
    [Fact]
    public async Task ResolveToken_GhCliSuccess_TokenCachedAndReturnedAsync()
    {
        this._provider.GhTokenProcessOverride = _ => CreateCommandProcess("/c echo my-test-token-123");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "cli-user", CancellationToken.None);

        Assert.Equal("my-test-token-123", token);

        // Verify it was cached: second call with throwing override should still succeed
        this._provider.GhTokenProcessOverride = _ => throw new InvalidOperationException("Should use cache");

        var cachedToken = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "cli-user", CancellationToken.None);

        Assert.Equal("my-test-token-123", cachedToken);
    }

    // --- ResolveTokenViaGhCliAsync: non-zero exit with short stderr ---
    [Fact]
    public async Task ResolveToken_GhCliNonZeroExit_ShortStderr_ReturnsNullAsync()
    {
        // Exercises the "short stderr" branch in LogNonZeroGhTokenExit (≤200 chars, non-empty)
        this._provider.GhTokenProcessOverride = _ =>
            CreateCommandProcess("/c echo short error message>&2 & exit 1");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "short-stderr-user", CancellationToken.None);

        Assert.Null(token);
    }

    // --- ResolveTokenViaGhCliAsync: non-zero exit with no stderr ---
    [Fact]
    public async Task ResolveToken_GhCliNonZeroExit_EmptyStderr_ReturnsNullAsync()
    {
        // Exercises the "(no stderr)" branch in LogNonZeroGhTokenExit
        this._provider.GhTokenProcessOverride = _ =>
            CreateCommandProcess("/c exit 1");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "no-stderr-user", CancellationToken.None);

        Assert.Null(token);
    }

    // --- ResolveTokenViaGhCliAsync: non-zero exit with long stderr (truncation) ---
    [Fact]
    public async Task ResolveToken_GhCliNonZeroExit_LongStderr_ReturnsNullAsync()
    {
        // Exercises the truncation branch (>200 chars) in LogNonZeroGhTokenExit
        var longMessage = new string('E', 300);
        this._provider.GhTokenProcessOverride = _ =>
            CreateCommandProcess($"/c echo {longMessage}>&2 & exit 1");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "long-stderr-user", CancellationToken.None);

        Assert.Null(token);
    }

    // --- CacheTokenIfValid: whitespace-only stdout ---
    [Fact]
    public async Task ResolveToken_GhCliReturnsWhitespace_ReturnsNullAsync()
    {
        // echo. on Windows outputs just a newline; after Trim() it's empty
        this._provider.GhTokenProcessOverride = _ =>
            CreateCommandProcess("/c echo.");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "whitespace-user", CancellationToken.None);

        Assert.Null(token);
    }

    // --- ResolveTokenViaGhCliAsync: timeout ---
    [Fact]
    public async Task ResolveToken_GhCliTimeout_ReturnsNullAsync()
    {
        this._provider.GhTokenProcessOverride = _ =>
            CreateCommandProcess("/c ping 127.0.0.1 -n 30 >nul");
        this._provider.TokenTimeoutOverride = TimeSpan.FromMilliseconds(200);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "timeout-user", CancellationToken.None);

        Assert.Null(token);
    }

    // --- ResolveTokenViaGhCliAsync: caller cancellation propagated ---
    [Fact]
    public async Task ResolveToken_GhCliCallerCancelled_ThrowsOperationCanceledAsync()
    {
        this._provider.GhTokenProcessOverride = _ =>
            CreateCommandProcess("/c ping 127.0.0.1 -n 30 >nul");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReflectionTestHelpers.InvokePrivateAsync<string?>(
                this._provider, "ResolveTokenForUserAsync", "cancel-user", cts.Token));
    }

    // --- ResolveTokenViaGhCliAsync: generic exception swallowed ---
    [Fact]
    public async Task ResolveToken_GhCliThrowsException_ReturnsNullAsync()
    {
        this._provider.GhTokenProcessOverride = _ =>
            throw new InvalidOperationException("gh not available");
        this._provider.TokenTimeoutOverride = TimeSpan.FromSeconds(5);

        var token = await ReflectionTestHelpers.InvokePrivateAsync<string?>(
            this._provider, "ResolveTokenForUserAsync", "exception-user", CancellationToken.None);

        Assert.Null(token);
    }

    // --- Multi-user: different users get independent tokens ---
    [Fact]
    public async Task ResolveToken_MultipleUsers_IndependentCacheEntriesAsync()
    {
        var json = BuildCopilotJson();
        var settings = CreateSettings("user-a", "user-b");
        var httpFactory = CreateFactory(json);
        var provider = CreateProvider(settings, httpFactory);

        var tokens = new Dictionary<string, string>();
        provider.TokenResolverOverride = (user, _) =>
        {
            var token = $"token-for-{user}";
            tokens[user] = token;
            return Task.FromResult<string?>(token);
        };

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.Items!.Count);
        Assert.Equal(2, tokens.Count);
        Assert.True(tokens.ContainsKey("user-a"));
        Assert.True(tokens.ContainsKey("user-b"));
    }

    // --- CreateGhTokenProcess: without override creates real process info ---
    [Fact]
    public async Task ResolveToken_NoOverrides_UsesGhCliDirectlyAsync()
    {
        // This exercises CreateGhTokenProcess without override.
        // On CI without gh installed, it should fail gracefully (return null, not throw).
        var provider = CreateProvider();
        provider.TokenTimeoutOverride = TimeSpan.FromMilliseconds(500);

        var exception = await Record.ExceptionAsync(() =>
            ReflectionTestHelpers.InvokePrivateAsync<string?>(
                provider, "ResolveTokenForUserAsync", $"no-override-{Guid.NewGuid():N}", CancellationToken.None));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        this._provider.GhTokenProcessOverride = null;
        this._provider.TokenTimeoutOverride = null;
    }

    private static CopilotProvider CreateProvider(
        ISettingsService? settings = null,
        IHttpClientFactory? httpFactory = null)
    {
        settings ??= CreateSettings();
        httpFactory ??= Substitute.For<IHttpClientFactory>();
        return new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            httpFactory,
            settings);
    }

    private static ISettingsService CreateSettings(params string[] accounts)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(accounts.ToList());
        return settings;
    }

    private static IHttpClientFactory CreateFactory(string json)
    {
        var handler = new FixedJsonHandler(json);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private sealed class FixedJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
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
        var cmd = windowsArgs.TrimStart();
        if (cmd.StartsWith("/c ", StringComparison.OrdinalIgnoreCase))
        {
            cmd = cmd[3..];
        }

        cmd = cmd.Replace(">nul", ">/dev/null");
        cmd = cmd.Replace(">&2", "1>&2");
        cmd = cmd.Replace("echo.", "echo ''");

        cmd = Regex.Replace(
            cmd, @"ping\s+127\.0\.0\.1\s+-n\s+(\d+)", "sleep $1");

        return $"\"{cmd.Replace("\"", "\\\"")}\"";
    }

    private static string BuildCopilotJson()
    {
        var resetDate = DateTimeOffset.UtcNow.AddDays(15).ToString("o");
        return $$"""
        {
            "login": "testuser",
            "copilot_plan": "individual_pro",
            "organization_login_list": ["org1"],
            "quota_reset_date_utc": "{{resetDate}}",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 2000,
                    "remaining": 500,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "percent_remaining": 25.0,
                    "unlimited": false,
                    "quota_id": "premium-test",
                    "timestamp_utc": "2026-05-14T00:00:00Z"
                }
            }
        }
        """;
    }
}
