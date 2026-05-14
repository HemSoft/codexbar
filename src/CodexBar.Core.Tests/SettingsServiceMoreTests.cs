// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

public class SettingsServiceMoreTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceMoreTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"SettingsTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._tempDir))
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
    }

    private SettingsService CreateSettingsService()
    {
        return new SettingsService(NullLogger<SettingsService>.Instance, this._tempDir);
    }

    private string GetSettingsFilePath()
    {
        return Path.Combine(this._tempDir, "settings.json");
    }

    [Fact]
    public void GetSessionResetTime_WhenNotSet_ReturnsNull()
    {
        var service = this.CreateSettingsService();
        var result = service.GetSessionResetTime(ProviderId.OpenRouter);
        Assert.Null(result);
    }

    [Fact]
    public void GetSessionResetTime_AfterSetBaseline_ReturnsTime()
    {
        var service = this.CreateSettingsService();
        var before = DateTimeOffset.Now;
        service.SetSessionBaseline(ProviderId.Claude, 10.0m);
        var result = service.GetSessionResetTime(ProviderId.Claude);

        Assert.NotNull(result);
        Assert.True(result.Value >= before.AddSeconds(-1));
    }

    [Fact]
    public void GetSessionResetTime_StringKey_WhenNotSet_ReturnsNull()
    {
        var service = this.CreateSettingsService();
        var result = service.GetSessionResetTime("UnknownProvider");
        Assert.Null(result);
    }

    [Fact]
    public void GetSessionResetTime_StringKey_AfterSetBaseline_ReturnsTime()
    {
        var service = this.CreateSettingsService();
        var before = DateTimeOffset.Now;
        service.SetSessionBaseline("custom-key", 25.0m);
        var result = service.GetSessionResetTime("custom-key");

        Assert.NotNull(result);
        Assert.True(result.Value >= before.AddSeconds(-1));
    }

    [Fact]
    public void Load_CorruptedJson_ReturnsDefaults()
    {
        File.WriteAllText(this.GetSettingsFilePath(), "{ invalid json content ]");
        var service = this.CreateSettingsService();
        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.Equal(120, settings.RefreshIntervalSeconds);
    }

    [Fact]
    public void Load_NullProviderEntries_NormalizesToDefaults()
    {
        // Write JSON where a provider value is null
        var json = """{"providers":{"Claude":null,"OpenRouter":{"enabled":true,"apiKey":"test-key"}}}""";
        File.WriteAllText(this.GetSettingsFilePath(), json);

        var service = this.CreateSettingsService();
        var settings = service.Load();

        Assert.NotNull(settings.Providers);
        Assert.True(settings.Providers.ContainsKey("Claude"));
        Assert.NotNull(settings.Providers["Claude"]);
    }

    [Fact]
    public void IsProviderEnabled_UnknownProvider_ReturnsTrue()
    {
        var service = this.CreateSettingsService();
        var result = service.IsProviderEnabled(ProviderId.OpenCodeZen);
        Assert.True(result);
    }

    [Fact]
    public void Save_PreservesSessionSpendingResetTimes()
    {
        var service = this.CreateSettingsService();
        service.SetSessionBaseline(ProviderId.Claude, 50.0m);
        var resetTimeBeforeSave = service.GetSessionResetTime(ProviderId.Claude);

        // Load via a new service instance to verify persistence
        var newService = this.CreateSettingsService();
        var resetTimeAfterLoad = newService.GetSessionResetTime(ProviderId.Claude);

        Assert.NotNull(resetTimeBeforeSave);
        Assert.NotNull(resetTimeAfterLoad);
        Assert.Equal(resetTimeBeforeSave!.Value.UtcTicks, resetTimeAfterLoad!.Value.UtcTicks);
    }

    [Fact]
    public void Save_PreservesWindowDimensions()
    {
        var service = this.CreateSettingsService();
        var settings = service.Load();
        settings.WindowWidth = 1920;
        settings.WindowHeight = 1080;
        settings.WindowLeft = 100;
        settings.WindowTop = 50;
        service.Save(settings);

        var newService = this.CreateSettingsService();
        var loaded = newService.Load();

        Assert.Equal(1920, loaded.WindowWidth);
        Assert.Equal(1080, loaded.WindowHeight);
        Assert.Equal(100, loaded.WindowLeft);
        Assert.Equal(50, loaded.WindowTop);
    }

    [Fact]
    public void MergeFromDisk_PreservesOpenCodeGoWorkspaceId()
    {
        // Write initial settings with workspace ID
        var json = """{"openCodeGoWorkspaceId":"workspace-123-abc","providers":{}}""";
        File.WriteAllText(this.GetSettingsFilePath(), json);

        var service = this.CreateSettingsService();
        var settings = service.Load();

        Assert.Equal("workspace-123-abc", settings.OpenCodeGoWorkspaceId);
    }

    [Fact]
    public void Save_PreservesSessionSpendingBaselines()
    {
        var service = this.CreateSettingsService();
        service.SetSessionBaseline(ProviderId.Copilot, 75.0m);

        var newService = this.CreateSettingsService();
        var result = newService.GetSessionBaseline(ProviderId.Copilot);

        Assert.Equal(75.0m, result);
    }

    [Fact]
    public void Save_PreservesCopilotAccounts()
    {
        var service = this.CreateSettingsService();
        var settings = service.Load();
        settings.CopilotAccounts = ["user1", "user2"];
        service.Save(settings);

        var newService = this.CreateSettingsService();
        var loaded = newService.Load();

        Assert.Equal(2, loaded.CopilotAccounts.Count);
        Assert.Contains("user1", loaded.CopilotAccounts);
        Assert.Contains("user2", loaded.CopilotAccounts);
    }

    [Fact]
    public void Save_PreservesZoomLevel()
    {
        var service = this.CreateSettingsService();
        var settings = service.Load();
        settings.ZoomLevel = 1.5;
        service.Save(settings);

        var newService = this.CreateSettingsService();
        var loaded = newService.Load();

        Assert.Equal(1.5, loaded.ZoomLevel);
    }

    [Fact]
    public void GetApiKey_UnknownProvider_ReturnsNull()
    {
        var service = this.CreateSettingsService();
        var result = service.GetApiKey(ProviderId.OpenCodeZen);
        Assert.Null(result);
    }

    [Fact]
    public void SetSessionBaseline_StoresAndRetrievesValue()
    {
        var service = this.CreateSettingsService();
        service.SetSessionBaseline(ProviderId.OpenRouter, 100.0m);
        var result = service.GetSessionBaseline(ProviderId.OpenRouter);

        Assert.Equal(100.0m, result);
    }
}
