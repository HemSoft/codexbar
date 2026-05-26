// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Cursor;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class CursorProviderTests : IDisposable
{
    private readonly Func<string, CancellationToken, Task<CursorProvider.CommandResult>> _originalRunner =
        CursorProvider.RunCommandAsync;

    public void Dispose()
    {
        CursorProvider.RunCommandAsync = this._originalRunner;
        CursorProvider.AuthPathOverride = null;
    }

    [Fact]
    public async Task FetchUsageAsync_WhenAuthenticated_ReturnsDashboardUsageCard()
    {
        CursorProvider.AuthPathOverride = WriteAuthFile("test-token");
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
        Assert.Equal("Pro plan · Auto 3% · API 6%", result.SessionUsage!.UsageLabel);
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
    public void ResolveCursorAgentCommand_ReturnsCommandNameOrLocalShim()
    {
        var result = CursorProvider.ResolveCursorAgentCommand();

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(result.EndsWith("cursor-agent", StringComparison.Ordinal) ||
                    result.EndsWith("cursor-agent.cmd", StringComparison.OrdinalIgnoreCase));
    }

    private static CursorProvider CreateProvider(HttpResponseMessage response)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Cursor).Returns(true);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(new MockHttpMessageHandler(response)));
        return new CursorProvider(NullLogger<CursorProvider>.Instance, factory, settings);
    }

    private static HttpResponseMessage CreateResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string WriteAuthFile(string token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{"accessToken":"{{token}}"}""");
        return path;
    }

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
}
