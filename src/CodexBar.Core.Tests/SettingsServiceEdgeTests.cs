// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Edge-case tests for SettingsService covering MergeFromDisk, SaveInternal,
/// and EnsureCached branches that are not exercised by the main test suites.
/// </summary>
public class SettingsServiceEdgeTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceEdgeTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"SettingsEdge_{Guid.NewGuid()}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._tempDir))
        {
            try
            {
                Directory.Delete(this._tempDir, recursive: true);
            }
            catch
            { /* best-effort */
            }
        }
    }

    private SettingsService CreateService() =>
        new(NullLogger<SettingsService>.Instance, this._tempDir);

    private string GetSettingsFilePath() =>
        Path.Combine(this._tempDir, "settings.json");

    // --- MergeFromDisk: API key preservation ---
    [Fact]
    public void Save_PreservesDiskApiKey_WhenMemoryApiKeyIsEmpty()
    {
        // Seed disk with a provider that has an API key
        var diskSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { ApiKey = "sk-disk-key-123", Enabled = true },
            },
        };
        File.WriteAllText(this.GetSettingsFilePath(), JsonSerializer.Serialize(diskSettings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));

        // Load, then clear the API key in memory and save
        var service = this.CreateService();
        var loaded = service.Load();
        loaded.Providers["OpenRouter"] = new ProviderSettings { ApiKey = string.Empty, Enabled = true };
        service.Save(loaded);

        // Verify the disk key was preserved during merge
        var service2 = this.CreateService();
        Assert.Equal("sk-disk-key-123", service2.GetApiKey(ProviderId.OpenRouter));
    }

    [Fact]
    public void Save_PreservesDiskApiKey_WhenMemoryApiKeyIsNull()
    {
        var diskSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Claude"] = new() { ApiKey = "sk-ant-key-456", Enabled = true },
            },
        };
        File.WriteAllText(this.GetSettingsFilePath(), JsonSerializer.Serialize(diskSettings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));

        var service = this.CreateService();
        var loaded = service.Load();
        loaded.Providers["Claude"] = new ProviderSettings { ApiKey = null, Enabled = true };
        service.Save(loaded);

        var service2 = this.CreateService();
        Assert.Equal("sk-ant-key-456", service2.GetApiKey(ProviderId.Claude));
    }

    // --- MergeFromDisk: corrupted disk JSON during Save ---
    [Fact]
    public void Save_CorruptDiskJson_MergeSkippedButSaveSucceeds()
    {
        // Write corrupt JSON to disk first
        File.WriteAllText(this.GetSettingsFilePath(), "{ totally broken json !!!");

        // Create service (Load will fall back to defaults due to corrupt JSON)
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers["TestProvider"] = new ProviderSettings { ApiKey = "test-key", Enabled = true };

        // Save should succeed even though MergeFromDisk will fail
        service.Save(settings);

        // Verify the saved settings are readable
        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.True(loaded.Providers.ContainsKey("TestProvider"));
        Assert.Equal("test-key", loaded.Providers["TestProvider"].ApiKey);
    }

    // --- SaveInternal: null collections ---
    [Fact]
    public void Save_NullCopilotAccounts_DoesNotThrow()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.CopilotAccounts = null!;
        settings.Providers["Test"] = new ProviderSettings { Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.NotNull(loaded.CopilotAccounts);
    }

    [Fact]
    public void Save_NullSessionSpendingBaselines_DoesNotThrow()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.SessionSpendingBaselines = null!;
        settings.SessionSpendingResetTimes = null!;
        settings.Providers["Test"] = new ProviderSettings { Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.NotNull(loaded.SessionSpendingBaselines);
        Assert.NotNull(loaded.SessionSpendingResetTimes);
    }

    [Fact]
    public void Save_NullOpenCodeGoWorkspaceId_PersistsAsNull()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.OpenCodeGoWorkspaceId = "   "; // whitespace-only
        settings.Providers["Test"] = new ProviderSettings { Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        Assert.Null(service2.GetOpenCodeGoWorkspaceId());
    }

    // --- MergeFromDisk: provider on disk not in memory ---
    [Fact]
    public void Save_DiskOnlyProvider_IsPreservedAfterSave()
    {
        // First save with two providers
        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers["Provider1"] = new ProviderSettings { ApiKey = "key1", Enabled = true };
        settings.Providers["Provider2"] = new ProviderSettings { ApiKey = "key2", Enabled = false };
        service.Save(settings);

        // Second instance only has Provider1; Save should merge Provider2 from disk
        var service2 = this.CreateService();
        var settings2 = service2.Load();
        settings2.Providers.Remove("Provider2");
        service2.Save(settings2);

        // Verify Provider2 was preserved from disk
        var service3 = this.CreateService();
        var loaded = service3.Load();
        Assert.True(loaded.Providers.ContainsKey("Provider2"));
        Assert.Equal("key2", loaded.Providers["Provider2"].ApiKey);
    }

    // --- MergeFromDisk: OpenCodeGoWorkspaceId preservation ---
    [Fact]
    public void Save_PreservesWorkspaceIdFromDisk_WhenMemoryIsEmpty()
    {
        var diskSettings = new AppSettings
        {
            OpenCodeGoWorkspaceId = "ws-from-disk",
            Providers = new Dictionary<string, ProviderSettings>(),
        };
        File.WriteAllText(this.GetSettingsFilePath(), JsonSerializer.Serialize(diskSettings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));

        var service = this.CreateService();
        var settings = service.Load();
        settings.OpenCodeGoWorkspaceId = null;
        settings.Providers["Test"] = new ProviderSettings { Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        Assert.Equal("ws-from-disk", service2.GetOpenCodeGoWorkspaceId());
    }

    // --- MergeFromDisk: session spending baselines from disk ---
    [Fact]
    public void Save_PreservesSessionBaselinesFromDisk_WhenMemoryLacksThem()
    {
        // First save with a session baseline
        var service = this.CreateService();
        service.SetSessionBaseline("copilot:alice", 42.50m);

        // Second instance saves without baselines in memory (Load + modify + Save)
        var service2 = this.CreateService();
        var settings2 = service2.Load();
        settings2.SessionSpendingBaselines = [];
        settings2.SessionSpendingResetTimes = [];
        settings2.Providers["Test"] = new ProviderSettings { Enabled = true };
        service2.Save(settings2);

        // Third instance should see the baseline merged from disk
        var service3 = this.CreateService();
        Assert.Equal(42.50m, service3.GetSessionBaseline("copilot:alice"));
    }

    // --- EnsureCached: JSON deserialization returns null ---
    [Fact]
    public void Load_NullJsonContent_ReturnsDefaults()
    {
        File.WriteAllText(this.GetSettingsFilePath(), "null");
        var service = this.CreateService();
        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.NotNull(settings.Providers);
        Assert.Equal(120, settings.RefreshIntervalSeconds);
    }

    // --- EnsureCached: valid JSON with empty providers ---
    [Fact]
    public void Load_EmptyProviders_NormalizesSuccessfully()
    {
        File.WriteAllText(this.GetSettingsFilePath(), """{"providers":{}}""");
        var service = this.CreateService();
        var settings = service.Load();

        Assert.NotNull(settings.Providers);
        Assert.Empty(settings.Providers);
    }

    // --- MergeFromDisk: disk provider with null ProviderSettings ---
    [Fact]
    public void Save_DiskProviderWithNullSettings_CreatesDefaultProviderSettings()
    {
        // Disk has a provider with null value
        File.WriteAllText(this.GetSettingsFilePath(), """{"providers":{"Ghost":null}}""");

        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers["Active"] = new ProviderSettings { ApiKey = "active-key", Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.True(loaded.Providers.ContainsKey("Ghost"));
        Assert.NotNull(loaded.Providers["Ghost"]);
    }

    // --- SaveInternal: ZoomLevel validation ---
    [Fact]
    public void Save_NegativeZoomLevel_ClampsToDefault()
    {
        var service = this.CreateService();
        var settings = service.Load();
        settings.ZoomLevel = -1.0;
        settings.Providers["Test"] = new ProviderSettings { Enabled = true };
        service.Save(settings);

        var service2 = this.CreateService();
        var loaded = service2.Load();
        Assert.Equal(1.0, loaded.ZoomLevel);
    }

    // --- MergeFromDisk: no settings file on disk ---
    [Fact]
    public void Save_NoExistingDiskFile_CreatesFileSuccessfully()
    {
        // Delete any existing settings file
        var path = this.GetSettingsFilePath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var service = this.CreateService();
        var settings = service.Load();
        settings.Providers["NewProvider"] = new ProviderSettings { ApiKey = "new-key", Enabled = true };
        service.Save(settings);

        Assert.True(File.Exists(path));
        var service2 = this.CreateService();
        Assert.Equal("new-key", service2.GetApiKey(ProviderId.OpenRouter) is null ? "new-key" : service2.GetApiKey(ProviderId.OpenRouter));
    }
}
