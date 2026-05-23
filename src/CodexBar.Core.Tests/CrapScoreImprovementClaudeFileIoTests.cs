// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests from CrapScoreImprovementTests that mutate ClaudeProvider.CredentialsPathOverride.
/// Placed in the "ClaudeProviderFileIo" collection to serialize with other tests that
/// touch the same static state.
/// </summary>
[Collection("ClaudeProviderFileIo")]
public class CrapScoreImprovementClaudeFileIoTests
{
    [Fact]
    public async Task FetchUsageAsync_ExpiredToken_RefreshSucceeds_FetchesUsage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"claude-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credPath = Path.Combine(tempDir, "credentials.json");
            var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            File.WriteAllText(credPath, $$"""
                {
                    "claudeAiOauth": {
                        "subscriptionType": "pro",
                        "expiresAt": {{pastExpiry}},
                        "accessToken": "expired-token",
                        "refreshToken": "valid-refresh"
                    }
                }
                """);

            int refreshCallCount = 0;
            int totalRequestCount = 0;
            var handler = new DelegatingHandlerFunc(req =>
            {
                Interlocked.Increment(ref totalRequestCount);
                if (req.RequestUri!.AbsolutePath.Contains("oauth/token"))
                {
                    Interlocked.Increment(ref refreshCallCount);
                    var refreshResponse = """
                        {
                            "access_token": "new-at",
                            "refresh_token": "new-rt",
                            "expires_at": 9999999999
                        }
                        """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(refreshResponse, Encoding.UTF8, "application/json"),
                    };
                }

                // Messages API probe for rate limits
                var msgResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"msg_123","type":"message","content":[]}""", Encoding.UTF8, "application/json"),
                };
                msgResponse.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.3");
                msgResponse.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-7d-utilization", "0.1");
                return msgResponse;
            });

            var httpFactory = CreateHttpFactory(handler);
            var settings = Substitute.For<ISettingsService>();
            settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

            var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, httpFactory, settings);
            ClaudeProvider.CredentialsPathOverride = credPath;

            var result = await provider.FetchUsageAsync();

            // Verify refresh was called and fetch succeeded with the refreshed token
            Assert.True(refreshCallCount >= 1, "Expected at least one call to /oauth/token for token refresh");
            Assert.True(totalRequestCount >= 2, "Expected at least a refresh call and a usage fetch call");
            Assert.True(result.Success, $"Expected successful fetch after token refresh, got: {result.ErrorMessage}");
        }
        finally
        {
            ClaudeProvider.CredentialsPathOverride = null;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FetchUsageAsync_ExpiredToken_RefreshFails_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"claude-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credPath = Path.Combine(tempDir, "credentials.json");
            var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            File.WriteAllText(credPath, $$"""
                {
                    "claudeAiOauth": {
                        "subscriptionType": "pro",
                        "expiresAt": {{pastExpiry}},
                        "accessToken": "expired-token",
                        "refreshToken": "bad-refresh"
                    }
                }
                """);

            var handler = new DelegatingHandlerFunc(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("oauth/token"))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            });

            var httpFactory = CreateHttpFactory(handler);
            var settings = Substitute.For<ISettingsService>();
            settings.IsProviderEnabled(ProviderId.Claude).Returns(true);

            var provider = new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, httpFactory, settings);
            ClaudeProvider.CredentialsPathOverride = credPath;

            var result = await provider.FetchUsageAsync();

            Assert.False(result.Success);
            Assert.Contains("expired", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ClaudeProvider.CredentialsPathOverride = null;
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static IHttpClientFactory CreateHttpFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private sealed class DelegatingHandlerFunc(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
