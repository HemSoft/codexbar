// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Additional coverage tests for SettingsService: MergeFromDisk, SaveInternal edge paths,
/// RestrictDirectoryPermissions, RestrictFilePermissions, and other uncovered branches.
/// </summary>
public class SettingsServiceFullCoverageTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceFullCoverageTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private SettingsService CreateService() => new(NullLogger<SettingsService>.Instance, this._tempDir);

    // --- Load: creates defaults when no file exists ---
    [Fact]
    public void Load_NoSettingsFile_CreatesDefaults()
    {
        var service = this.CreateService();
        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.Equal(120, settings.RefreshIntervalSeconds);
        Assert.NotNull(settings.Providers);
        Assert.True(settings.Providers.ContainsKey("Copilot"));
    }

    [Fact]
    public void Load_NoSettingsFile_PersistsDefaultsToDisk()
    {
        var service = this.CreateService();
        service.Load();

        var path = Path.Combine(this._tempDir, "settings.json");
        Assert.True(File.Exists(path));
    }

    // --- Load: reads existing file ---
    [Fact]
    public void Load_ExistingFile_ReadsCorrectly()
    {
        var json = """
        {
            "refreshIntervalSeconds": 60,
            "copilotAccounts": ["user1", "user2"],
            "openCodeGoWorkspaceId": "ws-123",
            "zoomLevel": 1.5,
            "windowWidth": 400,
            "windowHeight": 300,
            "windowLeft": 10,
            "windowTop": 20,
            "providers": {
                "OpenRouter": { "enabled": true, "apiKey": "sk-test" },
                "Copilot": { "enabled": true }
            }
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        var settings = service.Load();

        Assert.Equal(60, settings.RefreshIntervalSeconds);
        Assert.Equal(["user1", "user2"], settings.CopilotAccounts);
        Assert.Equal("ws-123", settings.OpenCodeGoWorkspaceId);
        Assert.Equal(1.5, settings.ZoomLevel);
    }

    // --- Load: corrupted file uses defaults ---
    [Fact]
    public void Load_CorruptedFile_UsesDefaults()
    {
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), "not json at all");

        var service = this.CreateService();
        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.Equal(120, settings.RefreshIntervalSeconds);
    }

    // --- Load: null provider values are normalized ---
    [Fact]
    public void Load_NullProviderValue_NormalizedToDefaultSettings()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {
                "OpenRouter": null,
                "Copilot": { "enabled": true }
            }
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        var settings = service.Load();

        Assert.NotNull(settings.Providers["OpenRouter"]);
    }

    // --- Save: MergeFromDisk preserves credentials ---
    [Fact]
    public void Save_MergeFromDisk_PreservesExistingApiKey()
    {
        var existingJson = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {
                "OpenRouter": { "enabled": true, "apiKey": "sk-preserved" }
            }
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), existingJson);

        var service = this.CreateService();
        var newSettings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = null },
            },
        };

        service.Save(newSettings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        Assert.Contains("sk-preserved", saved);
    }

    [Fact]
    public void Save_MergeFromDisk_PreservesEntireProviderEntry()
    {
        var existingJson = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {
                "OpenRouter": { "enabled": true, "apiKey": "sk-existing" },
                "Claude": { "enabled": true, "apiKey": "sk-claude" }
            }
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), existingJson);

        var service = this.CreateService();
        var newSettings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = "sk-existing" },

                // Claude not in memory — should be merged from disk
            },
        };

        service.Save(newSettings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        Assert.Contains("Claude", saved);
    }

    [Fact]
    public void Save_MergeFromDisk_PreservesOpenCodeGoWorkspaceId()
    {
        var existingJson = """
        {
            "refreshIntervalSeconds": 120,
            "openCodeGoWorkspaceId": "ws-from-disk",
            "providers": {}
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), existingJson);

        var service = this.CreateService();
        var newSettings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = [],
        };

        service.Save(newSettings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        Assert.Contains("ws-from-disk", saved);
    }

    [Fact]
    public void Save_MergeFromDisk_PreservesSessionBaselines()
    {
        var existingJson = """
        {
            "refreshIntervalSeconds": 120,
            "sessionSpendingBaselines": { "OpenRouter": 50.0 },
            "sessionSpendingResetTimes": { "OpenRouter": "2026-05-01T00:00:00+00:00" },
            "providers": {}
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), existingJson);

        var service = this.CreateService();
        var newSettings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = [],
        };

        service.Save(newSettings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        Assert.Contains("50", saved);
    }

    [Fact]
    public void Save_MergeFromDisk_CorruptDiskFile_SavesNewSettings()
    {
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), "corrupt json!!");

        var service = this.CreateService();
        var newSettings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Copilot"] = new() { Enabled = true },
            },
        };

        // Should not throw — just logs a warning and skips merge
        service.Save(newSettings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        Assert.Contains("Copilot", saved);
    }

    // --- SaveInternal: sanitization and edge cases ---
    [Fact]
    public void Save_ZoomLevelOutOfRange_ClampedToOne()
    {
        var service = this.CreateService();
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            ZoomLevel = 10.0, // out of range (> 5)
            Providers = [],
        };

        service.Save(settings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        var parsed = JsonSerializer.Deserialize<JsonElement>(saved);
        var zoom = parsed.GetProperty("zoomLevel").GetDouble();
        Assert.Equal(1.0, zoom);
    }

    [Fact]
    public void Save_ZoomLevelZero_ClampedToOne()
    {
        var service = this.CreateService();
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            ZoomLevel = 0,
            Providers = [],
        };

        service.Save(settings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        var parsed = JsonSerializer.Deserialize<JsonElement>(saved);
        var zoom = parsed.GetProperty("zoomLevel").GetDouble();
        Assert.Equal(1.0, zoom);
    }

    [Fact]
    public void Save_EmptyApiKey_StrippedFromOutput()
    {
        var service = this.CreateService();
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = string.Empty },
            },
        };

        service.Save(settings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        Assert.DoesNotContain("apiKey", saved);
    }

    [Fact]
    public void Save_WhitespaceOpenCodeGoWorkspaceId_SavedAsNull()
    {
        var service = this.CreateService();
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            OpenCodeGoWorkspaceId = "  ",
            Providers = [],
        };

        service.Save(settings);

        var saved = File.ReadAllText(Path.Combine(this._tempDir, "settings.json"));
        Assert.DoesNotContain("openCodeGoWorkspaceId", saved);
    }

    // --- GetApiKey ---
    [Fact]
    public void GetApiKey_ExistingProvider_ReturnsKey()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {
                "OpenRouter": { "enabled": true, "apiKey": "sk-test-key" }
            }
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        var key = service.GetApiKey(ProviderId.OpenRouter);

        Assert.Equal("sk-test-key", key);
    }

    [Fact]
    public void GetApiKey_NonexistentProvider_ReturnsNull()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {}
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        var key = service.GetApiKey(ProviderId.OpenRouter);

        Assert.Null(key);
    }

    // --- IsProviderEnabled ---
    [Fact]
    public void IsProviderEnabled_NotInSettings_DefaultsToTrue()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {}
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        Assert.True(service.IsProviderEnabled(ProviderId.Copilot));
    }

    [Fact]
    public void IsProviderEnabled_ExplicitlyDisabled_ReturnsFalse()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {
                "Copilot": { "enabled": false }
            }
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        Assert.False(service.IsProviderEnabled(ProviderId.Copilot));
    }

    // --- GetOpenCodeGoWorkspaceId ---
    [Fact]
    public void GetOpenCodeGoWorkspaceId_Set_ReturnsValue()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "openCodeGoWorkspaceId": "ws-test",
            "providers": {}
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        Assert.Equal("ws-test", service.GetOpenCodeGoWorkspaceId());
    }

    // --- GetCopilotAccounts ---
    [Fact]
    public void GetCopilotAccounts_Configured_ReturnsList()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "copilotAccounts": ["user1", "user2"],
            "providers": {}
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var service = this.CreateService();
        var accounts = service.GetCopilotAccounts();
        Assert.Equal(2, accounts.Count);
    }

    // --- Session baselines ---
    [Fact]
    public void GetSessionBaseline_NotSet_ReturnsNull()
    {
        var service = this.CreateService();
        service.Load();
        Assert.Null(service.GetSessionBaseline(ProviderId.OpenRouter));
    }

    [Fact]
    public void SetSessionBaseline_PersistsAndRetrieves()
    {
        var service = this.CreateService();
        service.Load();
        service.SetSessionBaseline(ProviderId.OpenRouter, 42.5m);

        Assert.Equal(42.5m, service.GetSessionBaseline(ProviderId.OpenRouter));
    }

    [Fact]
    public void GetSessionResetTime_AfterSet_ReturnsNonNull()
    {
        var service = this.CreateService();
        service.Load();
        service.SetSessionBaseline(ProviderId.OpenRouter, 10m);

        var time = service.GetSessionResetTime(ProviderId.OpenRouter);
        Assert.NotNull(time);
    }

    [Fact]
    public void GetSessionBaseline_StringKey_WorksCorrectly()
    {
        var service = this.CreateService();
        service.Load();
        service.SetSessionBaseline("custom-key", 99.9m);

        Assert.Equal(99.9m, service.GetSessionBaseline("custom-key"));
    }

    [Fact]
    public void GetSessionResetTime_StringKey_NotSet_ReturnsNull()
    {
        var service = this.CreateService();
        service.Load();
        Assert.Null(service.GetSessionResetTime("nonexistent"));
    }

    // --- Load returns deep copy ---
    [Fact]
    public void Load_ReturnsDifferentInstanceEachTime()
    {
        var service = this.CreateService();
        var a = service.Load();
        var b = service.Load();

        Assert.NotSame(a, b);
        Assert.NotSame(a.Providers, b.Providers);
    }

    // --- Save then Load round-trip ---
    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var service = this.CreateService();
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 90,
            CopilotAccounts = ["acct1"],
            OpenCodeGoWorkspaceId = "ws-rt",
            ZoomLevel = 2.0,
            WindowWidth = 500,
            WindowHeight = 400,
            WindowLeft = 50,
            WindowTop = 60,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = "sk-round" },
            },
        };

        service.Save(settings);

        // Force a re-read from disk by creating a new instance
        var service2 = this.CreateService();
        var loaded = service2.Load();

        Assert.Equal(90, loaded.RefreshIntervalSeconds);
        Assert.Equal(["acct1"], loaded.CopilotAccounts);
        Assert.Equal("ws-rt", loaded.OpenCodeGoWorkspaceId);
        Assert.Equal(2.0, loaded.ZoomLevel);
        Assert.Equal("sk-round", loaded.Providers["OpenRouter"].ApiKey);
    }
}
