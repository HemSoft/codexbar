// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Diagnostics;
using System.Net;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Codex;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.Cursor;
using Microsoft.Extensions.Logging;
using NSubstitute;

[Collection("ClaudeProviderFileIo")]
public sealed class ProviderAuthenticationRedactionTests : IDisposable
{
    private const string Secret = "SENSITIVE_PAYLOAD_MARKER";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"auth-redaction-{Guid.NewGuid():N}");
    private readonly string _authPath;

    public ProviderAuthenticationRedactionTests()
    {
        Directory.CreateDirectory(this._directory);
        this._authPath = Path.Combine(this._directory, "auth.json");
        File.WriteAllText(this._authPath, """
            {"claudeAiOauth":{"accessToken":"synthetic-token","refreshToken":"synthetic-refresh","expiresAt":1},"tokens":{"access_token":"synthetic-token"},"accessToken":"synthetic-token"}
            """);
        ClaudeProvider.CredentialsPathOverride = this._authPath;
        ClaudeProvider.StatsCachePathOverride = Path.Combine(this._directory, "stats.json");
        ClaudeProvider.ClaudeJsonPathOverride = Path.Combine(this._directory, "account.json");
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = string.Empty;
        ClaudeProvider.WebSessionCachePathOverride = Path.Combine(this._directory, "session.bin");
        ClaudeProvider.EnvironmentVariableProvider = _ => null;
        ClaudeProvider.TargetEnvironmentVariableProvider = (_, _) => null;
        CursorProvider.AuthPathOverride = this._authPath;
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;
        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;
        ClaudeProvider.WebSessionCachePathOverride = null;
        ClaudeProvider.ResetEnvironmentProvidersForTests();
        CursorProvider.AuthPathOverride = null;
        Directory.Delete(this._directory, recursive: true);
    }

    [Theory]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.Codex)]
    [InlineData(ProviderId.Cursor)]
    [InlineData(ProviderId.Copilot)]
    public async Task FetchUsageAsync_TransportContainsSecret_DiscardsExceptionAndMessage(ProviderId id)
    {
        var logs = new List<string>();
        var exceptions = new List<Exception>();
        var provider = this.CreateProvider(id, _ => throw new HttpRequestException(Secret, new Exception(Secret)), logs, exceptions);

        var result = await provider.FetchUsageAsync();

        Assert.DoesNotContain(Secret, result.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain(logs, message => message.Contains(Secret, StringComparison.Ordinal));
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task FetchUsageAsync_ClaudeRefreshContainsSecret_LogsOnlyStatusAndSafeCode()
    {
        var logs = new List<string>();
        var exceptions = new List<Exception>();
        var provider = this.CreateProvider(ProviderId.Claude, _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($$"""{"error":"invalid_grant","error_description":"{{Secret}}","access_token":"{{Secret}}","refresh_token":"{{Secret}}","authorization_code":"{{Secret}}","cookie":"session={{Secret}}"}"""),
        }, logs, exceptions);

        await provider.FetchUsageAsync();

        Assert.Contains(logs, message => message.Contains("HTTP 400; invalid_grant", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, message => message.Contains(Secret, StringComparison.Ordinal));
        Assert.Empty(exceptions);
    }

    [Theory]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.Codex)]
    [InlineData(ProviderId.Cursor)]
    [InlineData(ProviderId.Copilot)]
    public async Task FetchUsageAsync_ProviderErrorBody_DiscardsUntrustedFields(ProviderId id)
    {
        var logs = new List<string>();
        var exceptions = new List<Exception>();
        var provider = this.CreateProvider(id, _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent($$"""{"error":"{{Secret}}","access_token":"{{Secret}}","cookie":"{{Secret}}"}"""),
        }, logs, exceptions);

        var result = await provider.FetchUsageAsync();

        Assert.DoesNotContain(Secret, result.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain(logs, message => message.Contains(Secret, StringComparison.Ordinal));
        Assert.Empty(exceptions);
    }

    [Theory]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.Codex)]
    [InlineData(ProviderId.Cursor)]
    [InlineData(ProviderId.Copilot)]
    public async Task FetchUsageAsync_MalformedSuccessBody_DiscardsParserDetails(ProviderId id)
    {
        var logs = new List<string>();
        var exceptions = new List<Exception>();
        var provider = this.CreateProvider(id, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Secret),
        }, logs, exceptions);

        var result = await provider.FetchUsageAsync();

        Assert.DoesNotContain(Secret, result.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain(logs, message => message.Contains(Secret, StringComparison.Ordinal));
        Assert.Empty(exceptions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FetchUsageAsync_CliStderrContainsSecret_DiscardsDiscoveryAndTokenOutput(bool discovery)
    {
        var logs = new List<string>();
        var exceptions = new List<Exception>();
        var settings = Substitute.For<ISettingsService>();
        settings.GetCopilotAccounts().Returns(discovery ? [] : ["synthetic-user"]);
        var provider = new CopilotProvider(new CaptureLogger<CopilotProvider>(logs, exceptions), Substitute.For<IHttpClientFactory>(), settings)
        {
            GhStatusProcessOverride = CreateFailingProcess,
            GhTokenProcessOverride = _ => CreateFailingProcess(),
            DiscoveryTimeoutOverride = TimeSpan.FromSeconds(30),
            TokenTimeoutOverride = TimeSpan.FromSeconds(30),
        };

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains(logs, message => message.Contains("Authentication command failed (exit 1)", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, result.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain(logs, message => message.Contains(Secret, StringComparison.Ordinal));
        Assert.Empty(exceptions);
    }

    private static Process CreateFailingProcess() => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd" : "sh",
            Arguments = OperatingSystem.IsWindows() ? $"/c \"echo {Secret} 1>&2 && exit 1\"" : $"-c \"echo '{Secret}' 1>&2; exit 1\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        },
    };

    private IUsageProvider CreateProvider(ProviderId id, Func<HttpRequestMessage, HttpResponseMessage> respond, List<string> logs, List<Exception> exceptions)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Load().Returns(new AppSettings { CopilotAccounts = ["synthetic-user"], CopilotEnterprise = string.Empty, CopilotOrganization = string.Empty });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Handler(respond)));
        return id switch
        {
            ProviderId.Claude => new ClaudeProvider(new CaptureLogger<ClaudeProvider>(logs, exceptions), factory, settings),
            ProviderId.Codex => new CodexProvider(new CaptureLogger<CodexProvider>(logs, exceptions), factory, settings, this._authPath),
            ProviderId.Cursor => new CursorProvider(new CaptureLogger<CursorProvider>(logs, exceptions), factory, settings),
            ProviderId.Copilot => new CopilotProvider(new CaptureLogger<CopilotProvider>(logs, exceptions), factory, settings) { TokenResolverOverride = (_, _) => Task.FromResult<string?>("synthetic-token"), AccountDiscoveryOverride = _ => Task.FromResult(new List<string> { "synthetic-user" }) },
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class CaptureLogger<T>(List<string> messages, List<Exception> exceptions) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                exceptions.Add(exception);
            }
        }
    }
}
