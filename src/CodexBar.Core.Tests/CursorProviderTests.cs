// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.ComponentModel;
using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Cursor;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class CursorProviderTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly Func<string, CancellationToken, Task<CursorProvider.CommandResult>> _originalRunner =
        CursorProvider.RunCommandAsync;

    public void Dispose()
    {
        foreach (var tempFile in this._tempFiles)
        {
            DeleteTempFile(tempFile);
        }

        DeleteTempFile(CursorProvider.AuthPathOverride);
        CursorProvider.RunCommandAsync = this._originalRunner;
        CursorProvider.AuthPathOverride = null;
        CursorProvider.CursorAgentCommandOverride = null;
        CursorProvider.LocalAppDataPathOverride = null;
        CursorProvider.CommandTimeout = TimeSpan.FromSeconds(10);
        CursorProvider.KillProcess = static process => process.Kill();
        CursorProvider.WaitForExitAsync = static (process, cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenAuthenticated_ReturnsDashboardUsageCard()
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        CursorProvider.RunCommandAsync = (arguments, _) =>
        {
            if (arguments.StartsWith("status", StringComparison.Ordinal))
            {
                return Task.FromResult(new CursorProvider.CommandResult(
                    0,
                    """
                    {
                        "isAuthenticated": true,
                        "userInfo": {
                            "email": "dev@example.com"
                        }
                    }
                    """,
                    string.Empty));
            }

            return Task.FromResult(new CursorProvider.CommandResult(
                0,
                """
                {
                    "subscriptionTier": "Pro",
                    "model": "Composer 2.5 Fast",
                    "userEmail": "dev@example.com"
                }
                """,
                string.Empty));
        };

        var provider = CreateProvider(CreateResponse(
            """
            {
                "billingCycleEnd": "1780613438000",
                "planUsage": {
                    "totalSpend": 652,
                    "includedSpend": 652,
                    "remaining": 1348,
                    "limit": 2000,
                    "autoPercentUsed": 2.62,
                    "apiPercentUsed": 5.7555555555555555,
                    "totalPercentUsed": 3.3435897435897437
                },
                "spendLimitUsage": {
                    "individualLimit": 2000,
                    "individualRemaining": 2000,
                    "limitType": "user"
                }
            }
            """));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.Cursor, result.Provider);
        Assert.Equal("Cursor (Pro) · dev@example.com", result.Items![0].DisplayName);
        var expectedReset = DateTimeOffset.FromUnixTimeMilliseconds(1780613438000);
        Assert.Equal(expectedReset, result.SessionUsage!.ResetsAt);
        Assert.Equal($"Resets {expectedReset.ToLocalTime():MMM d}", result.SessionUsage.ResetDescription);
        Assert.Equal("Included usage · Auto 3% · API 6%", result.SessionUsage!.UsageLabel);
        Assert.Equal(0.03343589743589744, result.SessionUsage.UsedPercent, 6);
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "Total" } && Math.Abs(b.UsedPercent - 0.03343589743589744) < 0.000001);
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "Auto" } && Math.Abs(b.UsedPercent - 0.0262) < 0.000001);
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "API" } && Math.Abs(b.UsedPercent - 0.05755555555555555) < 0.000001);
        Assert.Contains(result.Items[0].Bars!, b => b.Label == "On-demand $0.00 / $20.00");
    }

    [Fact]
    public async Task FetchUsageAsync_WhenCredentialsMissing_ReturnsSignInError()
    {
        CursorProvider.AuthPathOverride = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        var provider = CreateProvider(CreateResponse("{}"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Cursor credentials", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenCredentialFileMalformed_ReturnsFailureMessageAsync()
    {
        CursorProvider.AuthPathOverride = this.WriteTempFile("{");

        var provider = CreateProvider(CreateResponse("{}"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Cursor usage could not be read", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenDashboardReturnsHttpError_ReturnsFailureMessageAsync()
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");

        var provider = CreateProvider(CreateResponse("{}", HttpStatusCode.InternalServerError));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Cursor usage could not be read", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenCanceled_RethrowsOperationCanceledExceptionAsync()
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        var provider = CreateProvider(new CanceledHttpMessageHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchUsageAsync(cts.Token));
    }

    [Fact]
    public async Task FetchUsageAsync_WhenCursorAgentReturnsMalformedAboutJson_IgnoresOptionalMetadataFailure()
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        CursorProvider.RunCommandAsync = (arguments, _) =>
        {
            if (arguments.StartsWith("status", StringComparison.Ordinal))
            {
                return Task.FromResult(new CursorProvider.CommandResult(
                    0,
                    """
                    {
                        "isAuthenticated": true,
                        "userInfo": {
                            "email": "dev@example.com"
                        }
                    }
                    """,
                    string.Empty));
            }

            return Task.FromResult(new CursorProvider.CommandResult(0, "{", string.Empty));
        };

        var provider = CreateProvider(CreateResponse(
            """
            {
                "billingCycleEnd": "1780613438000",
                "planUsage": {
                    "totalSpend": 652,
                    "includedSpend": 652,
                    "remaining": 1348,
                    "limit": 2000,
                    "autoPercentUsed": 2.62,
                    "apiPercentUsed": 5.7555555555555555,
                    "totalPercentUsed": 3.3435897435897437
                }
            }
            """));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("Cursor · dev@example.com", result.Items![0].DisplayName);
        Assert.Equal("Included usage · Auto 3% · API 6%", result.SessionUsage!.UsageLabel);
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(0, " ")]
    public async Task FetchUsageAsync_WhenStatusCommandCannotBeRead_UsesAboutMetadataAsync(int exitCode, string stdout)
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        CursorProvider.RunCommandAsync = (arguments, _) =>
        {
            if (arguments.StartsWith("status", StringComparison.Ordinal))
            {
                return Task.FromResult(new CursorProvider.CommandResult(exitCode, stdout, string.Empty));
            }

            return Task.FromResult(new CursorProvider.CommandResult(
                0,
                """
                {
                    "subscriptionTier": "team",
                    "userEmail": "about@example.com"
                }
                """,
                string.Empty));
        };

        var provider = CreateProvider(CreateResponse(CreatePlanUsageJson()));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("Cursor (Team) · about@example.com", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenStatusCommandReturnsMalformedJson_UsesAboutMetadataAsync()
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        CursorProvider.RunCommandAsync = (arguments, _) =>
            arguments.StartsWith("status", StringComparison.Ordinal)
                ? Task.FromResult(new CursorProvider.CommandResult(0, "{", string.Empty))
                : Task.FromResult(new CursorProvider.CommandResult(
                    0,
                    """{"subscriptionTier":"team","userEmail":"about@example.com"}""",
                    string.Empty));

        var provider = CreateProvider(CreateResponse(CreatePlanUsageJson()));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("Cursor (Team) · about@example.com", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenStatusCommandThrowsWin32Exception_UsesAboutMetadataAsync()
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        CursorProvider.RunCommandAsync = (arguments, _) =>
            arguments.StartsWith("status", StringComparison.Ordinal)
                ? throw new Win32Exception()
                : Task.FromResult(new CursorProvider.CommandResult(
                    0,
                    """{"subscriptionTier":"team","userEmail":"about@example.com"}""",
                    string.Empty));

        var provider = CreateProvider(CreateResponse(CreatePlanUsageJson()));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("Cursor (Team) · about@example.com", result.Items![0].DisplayName);
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(0, " ")]
    public async Task FetchUsageAsync_WhenAboutCommandCannotBeRead_UsesStatusMetadataAsync(int exitCode, string stdout)
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        CursorProvider.RunCommandAsync = (arguments, _) =>
            arguments.StartsWith("status", StringComparison.Ordinal)
                ? Task.FromResult(new CursorProvider.CommandResult(
                    0,
                    """{"isAuthenticated":true,"userInfo":{"email":"status@example.com"}}""",
                    string.Empty))
                : Task.FromResult(new CursorProvider.CommandResult(exitCode, stdout, string.Empty));

        var provider = CreateProvider(CreateResponse(CreatePlanUsageJson()));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("Cursor · status@example.com", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenAboutCommandThrowsWin32Exception_UsesStatusMetadataAsync()
    {
        CursorProvider.AuthPathOverride = this.WriteAuthFile("test-token");
        CursorProvider.RunCommandAsync = (arguments, _) =>
            arguments.StartsWith("status", StringComparison.Ordinal)
                ? Task.FromResult(new CursorProvider.CommandResult(
                    0,
                    """{"isAuthenticated":true,"userInfo":{"email":"status@example.com"}}""",
                    string.Empty))
                : throw new Win32Exception();

        var provider = CreateProvider(CreateResponse(CreatePlanUsageJson()));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal("Cursor · status@example.com", result.Items![0].DisplayName);
    }

    [Fact]
    public async Task IsAvailableAsync_WhenDisabled_ReturnsFalse()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Cursor).Returns(false);
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = new CursorProvider(NullLogger<CursorProvider>.Instance, factory, settings);

        var result = await provider.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public void BuildResult_WhenPlanUsageMissing_ReturnsPlanLabelWithoutBars()
    {
        var result = CursorProvider.BuildResult(
            new CursorProvider.CursorCurrentPeriodUsage(),
            null,
            new CursorProvider.CursorAbout
            {
                SubscriptionTier = "business",
                UserEmail = " ",
            });

        Assert.True(result.Success);
        Assert.Equal("Cursor (Business)", result.Items![0].DisplayName);
        Assert.Equal("Business plan", result.SessionUsage!.UsageLabel);
        Assert.Equal(0, result.SessionUsage.UsedPercent);
        Assert.Null(result.SessionUsage.ResetDescription);
        Assert.Empty(result.Items[0].Bars!);
    }

    [Fact]
    public void BuildResult_WhenStatusUserInfoMissing_UsesAboutEmail()
    {
        var result = CursorProvider.BuildResult(
            new CursorProvider.CursorCurrentPeriodUsage(),
            new CursorProvider.CursorStatus(),
            new CursorProvider.CursorAbout { UserEmail = "about@example.com" });

        Assert.True(result.Success);
        Assert.Equal("Cursor · about@example.com", result.Items![0].DisplayName);
    }

    [Fact]
    public void BuildResult_WhenOnlyTotalPercentIsPresent_UsesTotalLabelAndClampsReset()
    {
        var result = CursorProvider.BuildResult(
            new CursorProvider.CursorCurrentPeriodUsage
            {
                BillingCycleEnd = "not-a-timestamp",
                PlanUsage = new CursorProvider.CursorPlanUsage
                {
                    TotalPercentUsed = 150,
                },
            },
            new CursorProvider.CursorStatus
            {
                UserInfo = new CursorProvider.CursorUserInfo { Email = " " },
            },
            new CursorProvider.CursorAbout { SubscriptionTier = " " });

        Assert.True(result.Success);
        Assert.Equal("Cursor", result.Items![0].DisplayName);
        Assert.Equal("Included usage · Total 100%", result.SessionUsage!.UsageLabel);
        Assert.Equal(1, result.SessionUsage.UsedPercent);
        Assert.Null(result.SessionUsage.ResetDescription);
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "Total", UsedPercent: 1 });
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "Auto", UsedPercent: 0 });
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "API", UsedPercent: 0 });
    }

    [Fact]
    public void BuildResult_WhenPercentValuesAreOutOfRange_ClampsUsage()
    {
        var result = CursorProvider.BuildResult(
            new CursorProvider.CursorCurrentPeriodUsage
            {
                PlanUsage = new CursorProvider.CursorPlanUsage
                {
                    TotalPercentUsed = -10,
                    AutoPercentUsed = -5,
                    ApiPercentUsed = 250,
                },
                SpendLimitUsage = new CursorProvider.CursorSpendLimitUsage
                {
                    IndividualLimit = 2000,
                    IndividualRemaining = 2500,
                },
            },
            null,
            null);

        Assert.True(result.Success);
        Assert.Equal("Included usage · Auto 0% · API 100%", result.SessionUsage!.UsageLabel);
        Assert.Equal(0, result.SessionUsage.UsedPercent);
        Assert.Contains(result.Items![0].Bars!, b => b is { Label: "Total", UsedPercent: 0 });
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "Auto", UsedPercent: 0 });
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "API", UsedPercent: 1 });
        Assert.Contains(result.Items[0].Bars!, b => b is { Label: "On-demand $0.00 / $20.00", UsedPercent: 0 });
    }

    [Fact]
    public void ParseCurrentPeriodUsage_WhenJsonNull_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CursorProvider.ParseCurrentPeriodUsage("null"));

        Assert.Contains("empty usage response", ex.Message);
    }

    [Fact]
    public void ResolveAuthPath_WhenOverrideMissing_ReturnsCursorAuthPath()
    {
        CursorProvider.AuthPathOverride = null;

        var result = CursorProvider.ResolveAuthPath();

        Assert.EndsWith(Path.Combine("Cursor", "auth.json"), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveCursorAgentCommand_WhenOverrideSet_ReturnsOverride()
    {
        CursorProvider.CursorAgentCommandOverride = "custom-cursor-agent.cmd";

        var result = CursorProvider.ResolveCursorAgentCommand();

        Assert.Equal("custom-cursor-agent.cmd", result);
    }

    [Fact]
    public void ResolveCursorAgentCommand_WhenLocalShimExists_ReturnsLocalShim()
    {
        var root = Directory.CreateTempSubdirectory();
        var shimDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "cursor-agent"));
        var shim = Path.Combine(shimDirectory.FullName, "cursor-agent.cmd");
        File.WriteAllText(shim, "@echo off");
        CursorProvider.LocalAppDataPathOverride = root.FullName;

        try
        {
            var result = CursorProvider.ResolveCursorAgentCommand();

            Assert.Equal(shim, result);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveCursorAgentCommand_WhenLocalShimMissing_ReturnsCommandName()
    {
        var root = Directory.CreateTempSubdirectory();
        CursorProvider.LocalAppDataPathOverride = root.FullName;

        try
        {
            var result = CursorProvider.ResolveCursorAgentCommand();

            Assert.Equal("cursor-agent", result);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveCursorAgentCommand_WhenNoOverrides_ReturnsCommandNameOrRealLocalShim()
    {
        CursorProvider.CursorAgentCommandOverride = null;
        CursorProvider.LocalAppDataPathOverride = null;

        var result = CursorProvider.ResolveCursorAgentCommand();

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(result.EndsWith("cursor-agent", StringComparison.Ordinal) ||
                    result.EndsWith("cursor-agent.cmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunCommandAsync_WhenCommandSucceeds_ReturnsExitCodeAndStreamsAsync()
    {
        CursorProvider.CursorAgentCommandOverride = ResolveShellCommand();

        var result = await CursorProvider.RunCommandAsync(
            CreateShellArguments(
                "echo stdout-value && echo stderr-value 1>&2 && exit /b 7",
                "echo stdout-value; echo stderr-value >&2; exit 7"),
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("stdout-value", result.Stdout);
        Assert.Contains("stderr-value", result.Stderr);
    }

    [Fact]
    public async Task RunCommandAsync_WhenCommandTimesOut_ReturnsTimeoutResultAsync()
    {
        CursorProvider.CursorAgentCommandOverride = ResolveShellCommand();
        CursorProvider.WaitForExitAsync = (_, _) => throw new OperationCanceledException();
        CursorProvider.CommandTimeout = TimeSpan.FromMilliseconds(100);

        var result = await CursorProvider.RunCommandAsync(
            CreateShellArguments("echo timeout", "echo timeout"),
            CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal("cursor-agent timed out.", result.Stderr);
    }

    [Fact]
    public async Task RunCommandAsync_WhenCanceledAndKillFails_RethrowsAsync()
    {
        CursorProvider.CursorAgentCommandOverride = ResolveShellCommand();
        CursorProvider.WaitForExitAsync = (_, cancellationToken) => Task.FromCanceled(cancellationToken);
        CursorProvider.KillProcess = _ => throw new InvalidOperationException("kill failed");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CursorProvider.RunCommandAsync(
                CreateShellArguments("echo canceled", "echo canceled"),
                cts.Token));
    }

    private static CursorProvider CreateProvider(HttpResponseMessage response)
        => CreateProvider(new MockHttpMessageHandler(response));

    private static CursorProvider CreateProvider(HttpMessageHandler handler)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Cursor).Returns(true);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        return new CursorProvider(NullLogger<CursorProvider>.Instance, factory, settings);
    }

    private static HttpResponseMessage CreateResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private string WriteAuthFile(string token)
    {
        var path = this.CreateTempFilePath();
        File.WriteAllText(path, $$"""{"accessToken":"{{token}}"}""");
        return path;
    }

    private string WriteTempFile(string contents)
    {
        var path = this.CreateTempFilePath();
        File.WriteAllText(path, contents);
        return path;
    }

    private string CreateTempFilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        this._tempFiles.Add(path);
        return path;
    }

    private static void DeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static string CreatePlanUsageJson() =>
        """
        {
            "billingCycleEnd": "1780613438000",
            "planUsage": {
                "autoPercentUsed": 2.62,
                "apiPercentUsed": 5.7555555555555555,
                "totalPercentUsed": 3.3435897435897437
            }
        }
        """;

    private static string ResolveShellCommand() =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "/bin/sh";

    private static string CreateShellArguments(string windowsCommand, string unixCommand) =>
        OperatingSystem.IsWindows()
            ? $"/c \"{windowsCommand}\""
            : $"-c \"{unixCommand.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed class MockHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(response);
        }
    }

    private sealed class CanceledHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromCanceled<HttpResponseMessage>(ct);
    }
}
