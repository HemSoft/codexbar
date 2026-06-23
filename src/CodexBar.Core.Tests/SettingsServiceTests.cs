// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.IO;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

public class SettingsServiceTests : IDisposable
{
    private readonly string tempDir;

    public SettingsServiceTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private SettingsService CreateService() =>
        new(NullLogger<SettingsService>.Instance, this.tempDir);

    [Fact]
    public void Load_ReturnsNonNullSettings()
    {
        var service = this.CreateService();
        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.NotNull(settings.Providers);
    }

    [Fact]
    public void IsProviderEnabled_AfterEnabling_ReturnsTrue()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers["Claude"] = new ProviderSettings { ApiKey = "test-key", Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        Assert.True(service2.IsProviderEnabled(ProviderId.Claude));
    }

    [Fact]
    public void IsProviderEnabled_AfterDisabling_ReturnsFalse()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers["Claude"] = new ProviderSettings { ApiKey = null, Enabled = false };
        service.Save(settings);

        var service2 = this.CreateService();
        Assert.False(service2.IsProviderEnabled(ProviderId.Claude));
    }

    [Fact]
    public void Save_And_Load_RoundTrips()
    {
        var uniqueKey = $"test-{Guid.NewGuid():N}";
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers[uniqueKey] = new ProviderSettings { ApiKey = "test-key-roundtrip", Enabled = true };
        settings.ZoomLevel = 1.5;
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.Equal("test-key-roundtrip", loaded.Providers[uniqueKey].ApiKey);
        Assert.True(loaded.Providers[uniqueKey].Enabled);
        Assert.Equal(1.5, loaded.ZoomLevel);
    }

    [Fact]
    public void Save_WhenPrimarySettingsPathIsUnavailable_WritesFallbackSettingsFile()
    {
        Directory.CreateDirectory(Path.Combine(this.tempDir, "settings.json"));
        var service = this.CreateService();

        var settings = new AppSettings();
        settings.Providers["Claude"] = new ProviderSettings { Enabled = false };
        service.Save(settings);

        var fallbackPath = Path.Combine(this.tempDir, "codexbar-settings.json");
        Assert.True(File.Exists(fallbackPath));

        var service2 = this.CreateService();
        Assert.False(service2.IsProviderEnabled(ProviderId.Claude));
    }

    [Fact]
    public void Load_WhenFallbackSettingsFileExists_UsesFallbackSettingsFile()
    {
        var fallbackPath = Path.Combine(this.tempDir, "codexbar-settings.json");
        File.WriteAllText(fallbackPath, """
            {
              "providers": {
                "Claude": {
                  "enabled": false
                }
              }
            }
            """);

        var service = this.CreateService();

        Assert.False(service.IsProviderEnabled(ProviderId.Claude));
    }

    [Fact]
    public void Save_SanitizesEmptyApiKey()
    {
        var uniqueKey = $"test-{Guid.NewGuid():N}";
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers[uniqueKey] = new ProviderSettings { ApiKey = string.Empty, Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();

        Assert.Null(loaded.Providers[uniqueKey].ApiKey);
        Assert.True(loaded.Providers[uniqueKey].Enabled);
    }

    [Fact]
    public void Save_MergesProviderFromDisk()
    {
        var uniqueKey1 = $"test-merge-{Guid.NewGuid():N}";
        var uniqueKey2 = $"test-merge-{Guid.NewGuid():N}";
        var service = this.CreateService();
        var settings1 = service.Load();
        settings1.Providers[uniqueKey1] = new ProviderSettings { ApiKey = "key-1", Enabled = true };
        settings1.Providers[uniqueKey2] = new ProviderSettings { ApiKey = "key-2", Enabled = true };
        service.Save(settings1);

        var service2 = this.CreateService();
        var settings2 = service2.Load();
        settings2.Providers.Remove(uniqueKey1);
        service2.Save(settings2);

        var service3 = this.CreateService();
        var loaded = service3.Load();
        Assert.True(loaded.Providers.ContainsKey(uniqueKey1));
        Assert.Equal("key-1", loaded.Providers[uniqueKey1].ApiKey);
    }

    [Fact]
    public void Save_PreservesCopilotAccounts()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers[$"test-accounts-{Guid.NewGuid():N}"] = new ProviderSettings { Enabled = true };
        settings.CopilotAccounts = ["alice", "bob"];
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.Equal(2, loaded.CopilotAccounts.Count);
        Assert.Contains("alice", loaded.CopilotAccounts);
        Assert.Contains("bob", loaded.CopilotAccounts);
    }

    [Fact]
    public void Save_PreservesOpenCodeGoWorkspaceId()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers[$"test-workspace-{Guid.NewGuid():N}"] = new ProviderSettings { Enabled = true };
        settings.OpenCodeGoWorkspaceId = "ws-test-123";
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.Equal("ws-test-123", loaded.OpenCodeGoWorkspaceId);
    }

    [Fact]
    public void GetApiKey_ReturnsSavedKey()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers["OpenRouter"] = new ProviderSettings { ApiKey = "sk-or-test-key", Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        Assert.Equal("sk-or-test-key", service2.GetApiKey(ProviderId.OpenRouter));
    }

    [Fact]
    public void GetApiKey_NotConfigured_ReturnsNull()
    {
        var service = this.CreateService();
        var result = service.GetApiKey(ProviderId.OpenRouter);

        Assert.Null(result);
    }

    [Fact]
    public void Save_ClampsInvalidZoomLevel()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers[$"test-zoom-{Guid.NewGuid():N}"] = new ProviderSettings { Enabled = true };
        settings.ZoomLevel = 10.0; // Invalid: max is 5
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.Equal(1.0, loaded.ZoomLevel); // Clamped to default
    }

    [Fact]
    public void Save_ZeroZoomLevel_ClampsToDefault()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers[$"test-zoom2-{Guid.NewGuid():N}"] = new ProviderSettings { Enabled = true };
        settings.ZoomLevel = 0; // Invalid: must be > 0
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.Equal(1.0, loaded.ZoomLevel);
    }

    [Fact]
    public void GetOpenCodeGoWorkspaceId_ReturnsValueFromSettings()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.OpenCodeGoWorkspaceId = "ws-42";
        service.Save(settings);

        var service2 = this.CreateService();
        Assert.Equal("ws-42", service2.GetOpenCodeGoWorkspaceId());
    }

    [Fact]
    public void GetCopilotAccounts_ReturnsSavedAccounts()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.CopilotAccounts = ["user1", "user2", "user3"];
        service.Save(settings);

        var service2 = this.CreateService();
        var accounts = service2.GetCopilotAccounts();
        Assert.Equal(3, accounts.Count);
    }

    [Fact]
    public void GetSessionBaseline_WhenNotSet_ReturnsNull()
    {
        var service = this.CreateService();
        Assert.Null(service.GetSessionBaseline(ProviderId.OpenRouter));
    }

    [Fact]
    public void SetSessionBaseline_PersistsAcrossInstances()
    {
        var service = this.CreateService();
        service.SetSessionBaseline(ProviderId.OpenRouter, 178.14m);

        var service2 = this.CreateService();
        Assert.Equal(178.14m, service2.GetSessionBaseline(ProviderId.OpenRouter));
    }

    [Fact]
    public void SetSessionBaseline_IndependentPerProvider()
    {
        var service = this.CreateService();
        service.SetSessionBaseline(ProviderId.OpenRouter, 100m);
        service.SetSessionBaseline(ProviderId.OpenCodeZen, 200m);

        var service2 = this.CreateService();
        Assert.Equal(100m, service2.GetSessionBaseline(ProviderId.OpenRouter));
        Assert.Equal(200m, service2.GetSessionBaseline(ProviderId.OpenCodeZen));
    }

    [Fact]
    public void SetSessionBaseline_OverwritesPrevious()
    {
        var service = this.CreateService();
        service.SetSessionBaseline(ProviderId.OpenRouter, 100m);
        service.SetSessionBaseline(ProviderId.OpenRouter, 90m);

        Assert.Equal(90m, service.GetSessionBaseline(ProviderId.OpenRouter));
    }

    [Fact]
    public void Save_PreservesSessionBaselinesFromDisk()
    {
        var service = this.CreateService();
        service.SetSessionBaseline(ProviderId.OpenRouter, 150m);

        // Second instance loads but saves without baselines in memory
        var service2 = this.CreateService();
        var settings2 = service2.Load();
        service2.Save(settings2);

        // Third instance should still see the baseline via disk merge
        var service3 = this.CreateService();
        Assert.Equal(150m, service3.GetSessionBaseline(ProviderId.OpenRouter));
    }

    [Fact]
    public void GetSessionBaseline_StringKey_WhenNotSet_ReturnsNull()
    {
        var service = this.CreateService();
        Assert.Null(service.GetSessionBaseline("copilot:testuser"));
    }

    [Fact]
    public void SetSessionBaseline_StringKey_PersistsAcrossInstances()
    {
        var service = this.CreateService();
        service.SetSessionBaseline("copilot:testuser", 4.80m);

        var service2 = this.CreateService();
        Assert.Equal(4.80m, service2.GetSessionBaseline("copilot:testuser"));
    }

    [Fact]
    public void SetSessionBaseline_StringKey_IndependentPerAccount()
    {
        var service = this.CreateService();
        service.SetSessionBaseline("copilot:alice", 1.20m);
        service.SetSessionBaseline("copilot:bob", 3.60m);

        var service2 = this.CreateService();
        Assert.Equal(1.20m, service2.GetSessionBaseline("copilot:alice"));
        Assert.Equal(3.60m, service2.GetSessionBaseline("copilot:bob"));
    }

    [Fact]
    public void SetSessionBaseline_ProviderAndStringKeys_Coexist()
    {
        var service = this.CreateService();
        service.SetSessionBaseline(ProviderId.OpenRouter, 100m);
        service.SetSessionBaseline("copilot:testuser", 5.00m);

        Assert.Equal(100m, service.GetSessionBaseline(ProviderId.OpenRouter));
        Assert.Equal(5.00m, service.GetSessionBaseline("copilot:testuser"));
    }
}
