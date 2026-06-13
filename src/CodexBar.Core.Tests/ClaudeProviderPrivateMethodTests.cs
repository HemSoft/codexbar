// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests for ClaudeProvider private async methods (FetchRateLimitsAsync, TryRefreshTokenAsync)
/// accessed via reflection. These cover HTTP-based paths that are otherwise only reachable
/// through <see cref="ClaudeProvider.FetchUsageAsync"/> which depends on filesystem credentials.
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class ClaudeProviderPrivateMethodTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeProviderPrivateMethodTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"claude_private_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        ClaudeProvider.CredentialsPathOverride = Path.Combine(this._tempDir, "credentials.json");
        ClaudeProvider.StatsCachePathOverride = Path.Combine(this._tempDir, "stats-cache.json");
        ClaudeProvider.ClaudeJsonPathOverride = Path.Combine(this._tempDir, "claude.json");
    }

    public void Dispose()
    {
        ClaudeProvider.CredentialsPathOverride = null;
        ClaudeProvider.StatsCachePathOverride = null;
        ClaudeProvider.ClaudeJsonPathOverride = null;
        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    private static ClaudeProvider CreateProvider(IHttpClientFactory httpFactory)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Claude).Returns(true);
        return new ClaudeProvider(
            NullLogger<ClaudeProvider>.Instance,
            httpFactory,
            settings);
    }

    private static IHttpClientFactory CreateFactory(HttpResponseMessage response)
    {
        var handler = new CloneableResponseHandler(response);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static IHttpClientFactory CreateFactory(Exception exception)
    {
        var handler = new ExceptionHandler(exception);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static IHttpClientFactory CreateFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new DelegatingHandlerFunc(handler)));
        return factory;
    }

    private static string CreateClaudeWebUsageResponse(double fiveHourUtilization = 35, double sevenDayUtilization = 60)
    {
        var now = DateTimeOffset.UtcNow;
        return CreateClaudeWebUsageResponse(
            fiveHourUtilization,
            sevenDayUtilization,
            now.AddHours(1),
            now.AddDays(1));
    }

    private static string CreateClaudeWebUsageResponse(
        double fiveHourUtilization,
        double sevenDayUtilization,
        DateTimeOffset fiveHourResetAt,
        DateTimeOffset sevenDayResetAt) =>
        $$"""
        {
            "five_hour": {
                "utilization": {{fiveHourUtilization}},
                "resets_at": "{{fiveHourResetAt:O}}"
            },
            "seven_day": {
                "utilization": {{sevenDayUtilization}},
                "resets_at": "{{sevenDayResetAt:O}}"
            }
        }
        """;

    private static string CreateClaudeWebUsageResponseWithoutResets(double fiveHourUtilization, double sevenDayUtilization) =>
        $$"""
        {
            "five_hour": {
                "utilization": {{fiveHourUtilization}}
            },
            "seven_day": {
                "utilization": {{sevenDayUtilization}}
            }
        }
        """;

    private static async Task<object?> InvokeFetchRateLimitsAsync(
        ClaudeProvider provider, string? accessToken, CancellationToken ct = default)
    {
        var method = typeof(ClaudeProvider).GetMethod(
            "FetchRateLimitsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(provider, [accessToken, ct])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static async Task<object?> InvokeFetchOAuthUsageAsync(
        ClaudeProvider provider, string? accessToken, CancellationToken ct = default)
    {
        var method = typeof(ClaudeProvider).GetMethod(
            "FetchOAuthUsageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(provider, [accessToken, ct])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static void ExpireCachedLimits(ClaudeProvider provider)
    {
        var field = typeof(ClaudeProvider).GetField(
            "limitsCachedAtTicks",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(provider, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1)).UtcTicks);
    }

    private static async Task<object?> InvokeFetchClaudeWebUsageAsync(
        ClaudeProvider provider,
        ClaudeProvider.ClaudeAccountInfo? accountInfo,
        CancellationToken ct = default)
    {
        var method = typeof(ClaudeProvider).GetMethod(
            "FetchClaudeWebUsageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(ClaudeProvider.ClaudeAccountInfo), typeof(CancellationToken)]);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(provider, [accountInfo, ct])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static object CreateClaudeCredentials(
        string? subscriptionType = null,
        string? accessToken = null,
        string? refreshToken = null,
        long expiresAt = 0)
    {
        var credType = typeof(ClaudeProvider).GetNestedType("ClaudeCredentials", BindingFlags.NonPublic)!;
        Assert.NotNull(credType);
        var instance = Activator.CreateInstance(credType)!;
        credType.GetProperty("SubscriptionType")!.SetValue(instance, subscriptionType);
        credType.GetProperty("AccessToken")!.SetValue(instance, accessToken);
        credType.GetProperty("RefreshToken")!.SetValue(instance, refreshToken);
        credType.GetProperty("ExpiresAt")!.SetValue(instance, expiresAt);
        return instance;
    }

    private static async Task<object?> InvokeTryRefreshTokenAsync(
        ClaudeProvider provider, object credentials, CancellationToken ct = default)
    {
        var method = typeof(ClaudeProvider).GetMethod(
            "TryRefreshTokenAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(provider, [credentials, ct])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    // --- FetchRateLimitsAsync ---
    [Fact]
    public async Task FetchClaudeWebUsageAsync_NullAccount_ReturnsNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(factory);

        var result = await InvokeFetchClaudeWebUsageAsync(provider, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_MissingCookie_ReturnsNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(factory);
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo
        {
            OrganizationUuid = "org-id",
        };

        var result = await InvokeFetchClaudeWebUsageAsync(provider, accountInfo);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_ValidCookie_SendsWebUsageRequestAndCachesResult()
    {
        var requestCount = 0;
        var factory = CreateFactory((request, _) =>
        {
            Interlocked.Increment(ref requestCount);
            Assert.Equal(
                "https://claude.ai/api/organizations/org%2Fid%3Ftest%3Dvalue/usage",
                request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("Cookie", out var cookies));
            Assert.Contains("sessionKey=value", cookies);
            Assert.True(request.Headers.TryGetValues("x-organization-uuid", out var orgs));
            Assert.Contains("org/id?test=value", orgs);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateClaudeWebUsageResponse(), Encoding.UTF8, "application/json"),
            });
        });
        var provider = CreateProvider(factory);
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo
        {
            OrganizationUuid = "org/id?test=value",
        };

        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = "sessionKey=value";
        try
        {
            var result = await InvokeFetchClaudeWebUsageAsync(provider, accountInfo);
            var cached = await InvokeFetchClaudeWebUsageAsync(provider, null);

            Assert.NotNull(result);
            var limits = (ClaudeProvider.UnifiedRateLimits)result!;
            Assert.Equal(0.35, limits.FiveHourUtilization, 2);
            Assert.Equal(0.60, limits.SevenDayUtilization, 2);
            Assert.Same(result, cached);
            Assert.Equal(1, requestCount);
        }
        finally
        {
            ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;
        }
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_NonSuccessStatus_ReturnsNull()
    {
        var factory = CreateFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited", Encoding.UTF8, "text/plain"),
        }));
        var provider = CreateProvider(factory);

        var result = await provider.FetchClaudeWebUsageAsync("org-id", "sessionKey=value", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_InvalidJson_ReturnsNull()
    {
        var factory = CreateFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        }));
        var provider = CreateProvider(factory);

        var result = await provider.FetchClaudeWebUsageAsync("org-id", "sessionKey=value", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchClaudeWebUsageAsync_ConcurrentCalls_UseSingleWebUsageRequest()
    {
        var requestCount = 0;
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = CreateFactory(async (_, ct) =>
        {
            Interlocked.Increment(ref requestCount);
            requestStarted.TrySetResult(true);
            await allowResponse.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateClaudeWebUsageResponse(45, 70), Encoding.UTF8, "application/json"),
            };
        });
        var provider = CreateProvider(factory);
        var accountInfo = new ClaudeProvider.ClaudeAccountInfo
        {
            OrganizationUuid = "org-id",
        };

        ClaudeProvider.ClaudeDesktopCookieHeaderOverride = "sessionKey=value";
        try
        {
            var firstTask = InvokeFetchClaudeWebUsageAsync(provider, accountInfo);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondTask = InvokeFetchClaudeWebUsageAsync(provider, accountInfo);

            allowResponse.SetResult(true);
            var results = await Task.WhenAll(firstTask, secondTask);

            Assert.NotNull(results[0]);
            Assert.Same(results[0], results[1]);
            Assert.Equal(1, requestCount);
        }
        finally
        {
            ClaudeProvider.ClaudeDesktopCookieHeaderOverride = null;
        }
    }

    [Fact]
    public async Task FetchOAuthUsageAsync_StaleAuthoritativeZeroSnapshot_ReturnsFallbackOnFailure()
    {
        var requestCount = 0;
        var factory = CreateFactory((_, _) =>
        {
            var response = Interlocked.Increment(ref requestCount) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        CreateClaudeWebUsageResponseWithoutResets(0, 0),
                        Encoding.UTF8,
                        "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited", Encoding.UTF8, "text/plain"),
                };

            return Task.FromResult(response);
        });
        var provider = CreateProvider(factory);

        var first = await InvokeFetchOAuthUsageAsync(provider, "test-token");
        ExpireCachedLimits(provider);
        var second = await InvokeFetchOAuthUsageAsync(provider, "test-token");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_NullToken_ReturnsNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(factory);

        var result = await InvokeFetchRateLimitsAsync(provider, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_EmptyToken_ReturnsNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(factory);

        var result = await InvokeFetchRateLimitsAsync(provider, string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_ValidToken_WithRateLimitHeaders_ReturnsParsedLimits()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.35");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1750000000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "active");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.60");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-reset", "1751000000");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-status", "active");

        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);

        var result = await InvokeFetchRateLimitsAsync(provider, "test-access-token");

        Assert.NotNull(result);
        var limits = (ClaudeProvider.UnifiedRateLimits)result!;
        Assert.Equal(0.35, limits.FiveHourUtilization, 2);
        Assert.Equal(0.60, limits.SevenDayUtilization, 2);
        Assert.Equal("active", limits.FiveHourStatus);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_ValidToken_401Response_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"unauthorized\"}", Encoding.UTF8, "application/json"),
        };
        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);

        var result = await InvokeFetchRateLimitsAsync(provider, "expired-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_ValidToken_NoRateLimitHeaders_ReturnsCachedOrNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);

        var result = await InvokeFetchRateLimitsAsync(provider, "test-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_HttpException_ReturnsCachedOrNull()
    {
        var factory = CreateFactory(new HttpRequestException("Connection refused"));
        var provider = CreateProvider(factory);

        var result = await InvokeFetchRateLimitsAsync(provider, "test-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_GenericException_ReturnsCachedOrNull()
    {
        var factory = CreateFactory(new InvalidOperationException("Unexpected error"));
        var provider = CreateProvider(factory);

        var result = await InvokeFetchRateLimitsAsync(provider, "test-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_CachesResult_ReturnsSameOnSecondCall()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.25");
        response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.40");

        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);

        var first = await InvokeFetchRateLimitsAsync(provider, "test-token");
        Assert.NotNull(first);

        var second = await InvokeFetchRateLimitsAsync(provider, "test-token");
        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    // --- TryRefreshTokenAsync ---
    [Fact]
    public async Task TryRefreshTokenAsync_NoRefreshToken_ReturnsNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(factory);
        var credentials = CreateClaudeCredentials(
            subscriptionType: "pro",
            accessToken: "old-token",
            refreshToken: null);

        var result = await InvokeTryRefreshTokenAsync(provider, credentials);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_EmptyRefreshToken_ReturnsNull()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var provider = CreateProvider(factory);
        var credentials = CreateClaudeCredentials(
            subscriptionType: "pro",
            accessToken: "old-token",
            refreshToken: string.Empty);

        var result = await InvokeTryRefreshTokenAsync(provider, credentials);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_SuccessfulRefresh_ReturnsUpdatedCredentials()
    {
        var tokenResponse = """
        {
            "access_token": "new-access-token",
            "refresh_token": "new-refresh-token",
            "expires_at": 1750000000
        }
        """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(tokenResponse, Encoding.UTF8, "application/json"),
        };
        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);
        var credentials = CreateClaudeCredentials(
            subscriptionType: "pro",
            accessToken: "old-access-token",
            refreshToken: "old-refresh-token",
            expiresAt: 1740000000);

        var result = await InvokeTryRefreshTokenAsync(provider, credentials);

        Assert.NotNull(result);
        var credType = result!.GetType();
        Assert.Equal("new-access-token", credType.GetProperty("AccessToken")!.GetValue(result) as string);
        Assert.Equal("new-refresh-token", credType.GetProperty("RefreshToken")!.GetValue(result) as string);
        Assert.Equal(1750000000000L, (long)credType.GetProperty("ExpiresAt")!.GetValue(result)!);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_ServerError_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server Error", Encoding.UTF8, "text/plain"),
        };
        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);
        var credentials = CreateClaudeCredentials(
            subscriptionType: "pro",
            accessToken: "token",
            refreshToken: "refresh-token");

        var result = await InvokeTryRefreshTokenAsync(provider, credentials);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_ResponseMissingAccessToken_ReturnsNull()
    {
        var tokenResponse = """{"refresh_token": "new-refresh"}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(tokenResponse, Encoding.UTF8, "application/json"),
        };
        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);
        var credentials = CreateClaudeCredentials(
            subscriptionType: "pro",
            accessToken: "old-token",
            refreshToken: "old-refresh");

        var result = await InvokeTryRefreshTokenAsync(provider, credentials);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_HttpException_ReturnsNull()
    {
        var factory = CreateFactory(new HttpRequestException("Network error"));
        var provider = CreateProvider(factory);
        var credentials = CreateClaudeCredentials(
            subscriptionType: "pro",
            accessToken: "token",
            refreshToken: "refresh-token");

        var result = await InvokeTryRefreshTokenAsync(provider, credentials);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_SuccessfulRefresh_PreservesOldRefreshToken_WhenNewIsNull()
    {
        var tokenResponse = """
        {
            "access_token": "new-access-token",
            "expires_at": 1750000000
        }
        """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(tokenResponse, Encoding.UTF8, "application/json"),
        };
        var factory = CreateFactory(response);
        var provider = CreateProvider(factory);
        var credentials = CreateClaudeCredentials(
            subscriptionType: "pro",
            accessToken: "old-access-token",
            refreshToken: "keep-this-refresh-token",
            expiresAt: 1740000000);

        var result = await InvokeTryRefreshTokenAsync(provider, credentials);

        Assert.NotNull(result);
        var credType = result!.GetType();
        Assert.Equal("keep-this-refresh-token", credType.GetProperty("RefreshToken")!.GetValue(result) as string);
    }

    // --- Delegating handlers ---
    private sealed class CloneableResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _payload;
        private readonly string? _mediaType;
        private readonly List<KeyValuePair<string, IEnumerable<string>>> _responseHeaders;

        public CloneableResponseHandler(HttpResponseMessage response)
        {
            this._statusCode = response.StatusCode;
            this._payload = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            this._mediaType = response.Content?.Headers.ContentType?.MediaType;
            this._responseHeaders = response.Headers.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpResponseMessage(this._statusCode);
            if (this._payload is not null)
            {
                clone.Content = this._mediaType is null
                    ? new StringContent(this._payload, Encoding.UTF8)
                    : new StringContent(this._payload, Encoding.UTF8, this._mediaType);
            }

            foreach (var header in this._responseHeaders)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return Task.FromResult(clone);
        }
    }

    private sealed class ExceptionHandler(Exception exception) : HttpMessageHandler
    {
        private readonly Exception _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw this._exception;
    }

    private sealed class DelegatingHandlerFunc(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            this._handler(request, cancellationToken);
    }
}
