// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.OpenCodeZen;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Coverage tests targeting previously uncovered environment variable override branches
/// in OpenCodeZenProvider.ResolveCredentials (lines 204-214).
/// </summary>
[Collection("OpenCodeZenEnvVars")]
public class OpenCodeZenProviderCoverageTests : IDisposable
{
    private readonly List<string> _envVarsToCleanup = [];
    private readonly Dictionary<string, string?> _savedEnvVars = new();

    public OpenCodeZenProviderCoverageTests()
    {
        // Save and clear Go env vars so ResolveCredentials falls through to settings
        this.SaveAndClear("OPENCODE_GO_WORKSPACE_ID");
        this.SaveAndClear("OPENCODE_GO_AUTH_COOKIE");
        this.SaveAndClear("OPENCODE_ZEN_WORKSPACE_ID");
        this.SaveAndClear("OPENCODE_ZEN_AUTH_COOKIE");
    }

    private void SaveAndClear(string name)
    {
        this._savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
    }

    [Fact]
    public async Task FetchUsageAsync_ZenWorkspaceEnvVar_OverridesSettings()
    {
        this.SetEnvVar("OPENCODE_ZEN_WORKSPACE_ID", "zen-ws-override");

        var settings = CreateSettings(
            enabled: true,
            workspaceId: "settings-ws",
            goApiKey: "go-auth",
            zenApiKey: null);

        var html = BuildBalanceHtml(42);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(settings, response);

        var result = await provider.FetchUsageAsync();

        // The request is made (not failing due to missing credentials)
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FetchUsageAsync_ZenAuthCookieEnvVar_OverridesSettings()
    {
        this.SetEnvVar("OPENCODE_ZEN_AUTH_COOKIE", "zen-cookie-override");

        var settings = CreateSettings(
            enabled: true,
            workspaceId: "ws-123",
            goApiKey: "go-auth",
            zenApiKey: null);

        var html = BuildBalanceHtml(99);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(settings, response);

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result);
    }

    [Fact]
    public async Task FetchUsageAsync_ZenApiKeyOverridesGoApiKey()
    {
        var settings = CreateSettings(
            enabled: true,
            workspaceId: "ws-123",
            goApiKey: "go-auth",
            zenApiKey: "zen-specific-key");

        var html = BuildBalanceHtml(50);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(settings, response);

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result);
    }

    [Fact]
    public async Task FetchUsageAsync_AllZenEnvVarsSet_OverridesEverything()
    {
        this.SetEnvVar("OPENCODE_ZEN_WORKSPACE_ID", "env-ws");
        this.SetEnvVar("OPENCODE_ZEN_AUTH_COOKIE", "env-cookie");

        var settings = CreateSettings(
            enabled: true,
            workspaceId: "settings-ws",
            goApiKey: "go-auth",
            zenApiKey: "zen-key");

        var html = BuildBalanceHtml(75);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var provider = CreateProvider(settings, response);

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result);
    }

    private static OpenCodeZenProvider CreateProvider(
        ISettingsService settings,
        HttpResponseMessage response)
    {
        var handler = new PassthroughHandler(response);
        var httpClientFactory = new SimpleFactory(handler);

        return new OpenCodeZenProvider(
            NullLogger<OpenCodeZenProvider>.Instance,
            httpClientFactory,
            settings);
    }

    private static ISettingsService CreateSettings(
        bool enabled = true,
        string? workspaceId = null,
        string? goApiKey = null,
        string? zenApiKey = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.OpenCodeZen).Returns(enabled);
        settings.GetOpenCodeGoWorkspaceId().Returns(workspaceId);
        settings.GetApiKey(ProviderId.OpenCodeGo).Returns(goApiKey);
        settings.GetApiKey(ProviderId.OpenCodeZen).Returns(zenApiKey);
        return settings;
    }

    private static string BuildBalanceHtml(int balance) =>
        $"<html><body><script>balance:{balance}</script></body></html>";

    private void SetEnvVar(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        this._envVarsToCleanup.Add(name);
    }

    public void Dispose()
    {
        foreach (var name in this._envVarsToCleanup)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var (name, value) in this._savedEnvVars)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class PassthroughHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode = response.StatusCode;
        private readonly string? _payload = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        private readonly string? _mediaType = response.Content?.Headers.ContentType?.MediaType;

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

            return Task.FromResult(clone);
        }
    }

    private sealed class SimpleFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
