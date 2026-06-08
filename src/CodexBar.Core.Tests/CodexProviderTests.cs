// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class CodexProviderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"codexbar_codex_{Guid.NewGuid():N}");
    private readonly string _authPath;

    public CodexProviderTests()
    {
        Directory.CreateDirectory(this._tempDirectory);
        this._authPath = Path.Combine(this._tempDirectory, "auth.json");
    }

    [Fact]
    public void Metadata_DescribesChatGptCodexLimits()
    {
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, ValidUsageJson));

        Assert.Equal(ProviderId.Codex, provider.Metadata.Id);
        Assert.Equal("ChatGPT / Codex", provider.Metadata.DisplayName);
        Assert.True(provider.Metadata.SupportsSessionUsage);
        Assert.True(provider.Metadata.SupportsWeeklyUsage);
        Assert.False(provider.Metadata.SupportsCredits);
    }

    [Fact]
    public void Constructor_DefaultAuthPath_CreatesProvider()
    {
        var settings = Substitute.For<ISettingsService>();
        var provider = new CodexProvider(
            NullLogger<CodexProvider>.Instance,
            new StubHttpClientFactory(new StubHandler(_ => CreateResponse(HttpStatusCode.OK, ValidUsageJson))),
            settings);

        Assert.Equal(ProviderId.Codex, provider.Metadata.Id);
    }

    [Fact]
    public async Task Constructor_CodexHomeOverride_ReadsAuthFromOverride()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", this._tempDirectory);
            this.WriteAuth();
            var settings = Substitute.For<ISettingsService>();
            var provider = new CodexProvider(
                NullLogger<CodexProvider>.Instance,
                new StubHttpClientFactory(new StubHandler(_ => CreateResponse(HttpStatusCode.OK, ValidUsageJson))),
                settings);

            Assert.True((await provider.FetchUsageAsync()).Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, ValidUsageJson), enabled: false);

        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task FetchUsageAsync_MissingAuthFile_ReturnsLoginFailure()
    {
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, ValidUsageJson));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Run 'codex'", result.ErrorMessage);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tokens":{}}""")]
    [InlineData("not json")]
    public async Task FetchUsageAsync_InvalidAuthContent_ReturnsLoginFailure(string authPayload)
    {
        File.WriteAllText(this._authPath, authPayload);
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, ValidUsageJson));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Run 'codex'", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ValidUsage_ReturnsTwoUsageBars()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, ValidUsageJson));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Equal(ProviderId.Codex, result.Provider);
        var item = Assert.Single(result.Items!);
        Assert.Equal("codex:chatgpt", item.Key);
        Assert.Equal("ChatGPT / Codex (Plus)", item.DisplayName);
        Assert.Collection(
            item.Bars!,
            bar =>
            {
                Assert.Equal("5 hour usage limit", bar.Label);
                Assert.Equal(0.05, bar.UsedPercent);
            },
            bar =>
            {
                Assert.Equal("Weekly usage limit", bar.Label);
                Assert.Equal(0.01, bar.UsedPercent);
            });
    }

    [Theory]
    [InlineData("plus", "ChatGPT / Codex (Plus)")]
    [InlineData("prolite", "ChatGPT / Codex (Pro)")]
    [InlineData("some_custom_plan", "ChatGPT / Codex (Some Custom Plan)")]
    public void FormatDisplayName_MapsKnownPlanCodes(string planType, string expected)
    {
        var result = CodexProvider.FormatDisplayName(planType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task FetchUsageAsync_SendsBearerTokenAndAccountId()
    {
        this.WriteAuth();
        HttpRequestMessage? sentRequest = null;
        var provider = this.CreateProvider(request =>
        {
            sentRequest = request;
            return CreateResponse(HttpStatusCode.OK, ValidUsageJson);
        });

        await provider.FetchUsageAsync();

        Assert.NotNull(sentRequest);
        Assert.Equal("Bearer", sentRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("access-token", sentRequest.Headers.Authorization.Parameter);
        Assert.Equal("account-123", sentRequest.Headers.GetValues("ChatGPT-Account-Id").Single());
    }

    [Fact]
    public async Task FetchUsageAsync_CamelCaseAuthWithoutAccountId_SendsBearerOnly()
    {
        File.WriteAllText(this._authPath, """{"tokens":{"accessToken":"legacy-token"}}""");
        HttpRequestMessage? sentRequest = null;
        var provider = this.CreateProvider(request =>
        {
            sentRequest = request;
            return CreateResponse(HttpStatusCode.OK, ValidUsageJson);
        });

        Assert.True((await provider.FetchUsageAsync()).Success);
        Assert.Equal("legacy-token", sentRequest!.Headers.Authorization!.Parameter);
        Assert.False(sentRequest.Headers.Contains("ChatGPT-Account-Id"));
    }

    [Fact]
    public async Task FetchUsageAsync_Unauthorized_ReturnsReloginFailure()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.Unauthorized, "{}"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("expired", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_HttpFailure_ReturnsStatusFailure()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.InternalServerError, "{}"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("HTTP 500", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_InvalidJson_ReturnsParseFailure()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, "not json"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("parse", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_ResponseWithoutRateLimit_ReturnsFailure()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, """{"plan_type":"plus"}"""));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("no rate limits", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_MalformedPrimaryWindow_KeepsWeeklyWindow()
    {
        this.WriteAuth();
        const string payload = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": "bad" },
                "secondary_window": {
                  "used_percent": 22,
                  "reset_at": 1893456000,
                  "limit_window_seconds": 604800
                }
              }
            }
            """;
        var provider = this.CreateProvider(_ => CreateResponse(HttpStatusCode.OK, payload));

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var bar = Assert.Single(Assert.Single(result.Items!).Bars!);
        Assert.Equal("Weekly usage limit", bar.Label);
        Assert.Equal(0.22, bar.UsedPercent);
    }

    [Fact]
    public async Task FetchUsageAsync_NullWindows_ReturnsNoLimitsFailure()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => CreateResponse(
            HttpStatusCode.OK,
            """{"rate_limit":{"primary_window":null,"secondary_window":null}}"""));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No Codex usage limits", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_CustomDuration_UsesHourLabel()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => CreateResponse(
            HttpStatusCode.OK,
            """
            {"rate_limit":{"primary_window":{"used_percent":150,"reset_at":1893456000,"limit_window_seconds":32400}}}
            """));

        var result = await provider.FetchUsageAsync();

        var bar = Assert.Single(Assert.Single(result.Items!).Bars!);
        Assert.Equal("9 hour usage limit", bar.Label);
        Assert.Equal(1, bar.UsedPercent);
    }

    [Fact]
    public async Task FetchUsageAsync_SecondSuccessfulFetch_UsesCache()
    {
        this.WriteAuth();
        var callCount = 0;
        var provider = this.CreateProvider(_ =>
        {
            callCount++;
            return CreateResponse(HttpStatusCode.OK, ValidUsageJson);
        });

        Assert.True((await provider.FetchUsageAsync()).Success);
        Assert.True((await provider.FetchUsageAsync()).Success);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task FetchUsageAsync_RequestThrows_ReturnsFailure()
    {
        this.WriteAuth();
        var provider = this.CreateProvider(_ => throw new HttpRequestException("offline"));

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("offline", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchUsageAsync_CanceledRequest_RethrowsCancellation()
    {
        this.WriteAuth();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = this.CreateProvider(_ => throw new OperationCanceledException(source.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.FetchUsageAsync(source.Token));
    }

    [Fact]
    public void FormatReset_FormatsShortAndExpiredWindows()
    {
        Assert.Equal("Resets now", CodexProvider.FormatReset(DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.StartsWith("Resets 2h", CodexProvider.FormatReset(DateTimeOffset.UtcNow.AddHours(2.5)));
        Assert.StartsWith("Resets 4m", CodexProvider.FormatReset(DateTimeOffset.UtcNow.AddMinutes(4).AddSeconds(30)));
    }

    public void Dispose()
    {
        Directory.Delete(this._tempDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private CodexProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        bool enabled = true)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Codex).Returns(enabled);
        return new CodexProvider(
            NullLogger<CodexProvider>.Instance,
            new StubHttpClientFactory(new StubHandler(responseFactory)),
            settings,
            this._authPath);
    }

    private void WriteAuth()
    {
        File.WriteAllText(
            this._authPath,
            """
            {
              "tokens": {
                "access_token": "access-token",
                "account_id": "account-123"
              }
            }
            """);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode status, string payload) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    private const string ValidUsageJson = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "primary_window": {
              "used_percent": 5,
              "reset_at": 1893456000,
              "limit_window_seconds": 18000
            },
            "secondary_window": {
              "used_percent": 1,
              "reset_at": 1893974400,
              "limit_window_seconds": 604800
            }
          }
        }
        """;

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
