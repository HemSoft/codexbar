// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Mutation-killing tests for SettingsService:
/// Load/Save, ZoomLevel clamping, NormalizeProviders, MergeFromDisk, and credential sanitization.
/// </summary>
public class SettingsServiceMutationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsService _sut;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public SettingsServiceMutationTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
        this._sut = new SettingsService(NullLogger<SettingsService>.Instance, this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._tempDir, true);
        }
        catch
        { /* ignore in test cleanup */
        }
    }

    // === Load returns defaults when no file ===
    [Fact]
    public void Load_NoFileExists_ReturnsDefaults()
    {
        var settings = this._sut.Load();
        Assert.NotNull(settings);
        Assert.Equal(120, settings.RefreshIntervalSeconds);
        Assert.NotNull(settings.Providers);
    }

    [Fact]
    public void Load_NoFileExists_CreatesDefaultProviders()
    {
        var settings = this._sut.Load();
        Assert.True(settings.Providers!.ContainsKey(ProviderId.OpenRouter.ToString()));
        Assert.True(settings.Providers.ContainsKey(ProviderId.Copilot.ToString()));
        Assert.True(settings.Providers.ContainsKey(ProviderId.Claude.ToString()));
        Assert.True(settings.Providers.ContainsKey(ProviderId.OpenCodeGo.ToString()));
    }

    [Fact]
    public void Load_NoFileExists_ClaudeDisabledByDefault()
    {
        var settings = this._sut.Load();
        Assert.False(settings.Providers![ProviderId.Claude.ToString()]!.Enabled);
    }

    [Fact]
    public void Load_NoFileExists_CopilotEnabledByDefault()
    {
        var settings = this._sut.Load();
        Assert.True(settings.Providers![ProviderId.Copilot.ToString()]!.Enabled);
    }

    // === Load from disk ===
    [Fact]
    public void Load_ExistingFile_ReturnsParsedSettings()
    {
        var toSave = new AppSettings
        {
            RefreshIntervalSeconds = 300,
            Providers = new Dictionary<string, ProviderSettings>
            {
                [ProviderId.Copilot.ToString()] = new() { Enabled = true, ApiKey = "my-key" }
            }
        };
        this.WriteSettings(toSave);

        var loaded = this._sut.Load();
        Assert.Equal(300, loaded.RefreshIntervalSeconds);
        Assert.Equal("my-key", loaded.Providers![ProviderId.Copilot.ToString()]!.ApiKey);
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsDefaults()
    {
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), "{{invalid json}}");
        var loaded = this._sut.Load();
        Assert.Equal(120, loaded.RefreshIntervalSeconds);
    }

    // === Save round-trip ===
    [Fact]
    public void Save_ThenLoad_PreservesProviders()
    {
        var settings = this._sut.Load();
        settings.Providers![ProviderId.OpenRouter.ToString()]!.ApiKey = "test-api-key";
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal("test-api-key", loaded.Providers![ProviderId.OpenRouter.ToString()]!.ApiKey);
    }

    [Fact]
    public void Save_ThenLoad_PreservesRefreshInterval()
    {
        var settings = this._sut.Load();
        settings.RefreshIntervalSeconds = 60;
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(60, loaded.RefreshIntervalSeconds);
    }

    [Fact]
    public void Save_ThenLoad_PreservesOpenCodeGoWorkspaceId()
    {
        var settings = this._sut.Load();
        settings.OpenCodeGoWorkspaceId = "my-workspace";
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal("my-workspace", loaded.OpenCodeGoWorkspaceId);
    }

    [Fact]
    public void Save_ThenLoad_PreservesCopilotAccounts()
    {
        var settings = this._sut.Load();
        settings.CopilotAccounts = ["user1", "user2"];
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(2, loaded.CopilotAccounts!.Count);
        Assert.Contains("user1", loaded.CopilotAccounts);
        Assert.Contains("user2", loaded.CopilotAccounts);
    }

    // === ZoomLevel clamping ===
    [Fact]
    public void Save_ZoomLevelNegative_ClampedToOne()
    {
        var settings = this._sut.Load();
        settings.ZoomLevel = -0.5;
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(1.0, loaded.ZoomLevel);
    }

    [Fact]
    public void Save_ZoomLevelAboveFive_ClampedToOne()
    {
        var settings = this._sut.Load();
        settings.ZoomLevel = 6.0;
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(1.0, loaded.ZoomLevel);
    }

    [Fact]
    public void Save_ZoomLevelExactlyFive_Preserved()
    {
        var settings = this._sut.Load();
        settings.ZoomLevel = 5.0;
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(5.0, loaded.ZoomLevel);
    }

    [Fact]
    public void Save_ZoomLevelValidMidrange_Preserved()
    {
        var settings = this._sut.Load();
        settings.ZoomLevel = 1.5;
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(1.5, loaded.ZoomLevel);
    }

    [Fact]
    public void Save_ZoomLevelJustAboveZero_Preserved()
    {
        var settings = this._sut.Load();
        settings.ZoomLevel = 0.1;
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(0.1, loaded.ZoomLevel);
    }

    // === API key sanitization ===
    [Fact]
    public void Save_EmptyApiKey_BecomesNull()
    {
        var settings = this._sut.Load();
        settings.Providers![ProviderId.Copilot.ToString()]!.ApiKey = "   ";
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Null(loaded.Providers![ProviderId.Copilot.ToString()]!.ApiKey);
    }

    [Fact]
    public void Save_WhitespaceOpenCodeGoWorkspaceId_BecomesNull()
    {
        var settings = this._sut.Load();
        settings.OpenCodeGoWorkspaceId = "   ";
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Null(loaded.OpenCodeGoWorkspaceId);
    }

    // === GetApiKey ===
    [Fact]
    public void GetApiKey_MissingProvider_ReturnsNull()
    {
        Assert.Null(this._sut.GetApiKey(ProviderId.OpenCodeZen));
    }

    // === IsProviderEnabled ===
    [Fact]
    public void IsProviderEnabled_NotConfigured_ReturnsTrue()
    {
        // Provider not in settings → defaults to enabled
        Assert.True(this._sut.IsProviderEnabled(ProviderId.OpenCodeZen));
    }

    [Fact]
    public void IsProviderEnabled_ExplicitlyEnabled_ReturnsTrue()
    {
        var settings = this._sut.Load();
        settings.Providers![ProviderId.Copilot.ToString()]!.Enabled = true;
        this._sut.Save(settings);

        Assert.True(this._sut.IsProviderEnabled(ProviderId.Copilot));
    }

    // === GetOpenCodeGoWorkspaceId ===
    [Fact]
    public void GetOpenCodeGoWorkspaceId_SetValue_ReturnsIt()
    {
        var settings = this._sut.Load();
        settings.OpenCodeGoWorkspaceId = "ws-123";
        this._sut.Save(settings);

        Assert.Equal("ws-123", this._sut.GetOpenCodeGoWorkspaceId());
    }

    [Fact]
    public void GetOpenCodeGoWorkspaceId_NotSet_ReturnsNull()
    {
        Assert.Null(this._sut.GetOpenCodeGoWorkspaceId());
    }

    // === GetCopilotAccounts ===
    [Fact]
    public void GetCopilotAccounts_NoFile_ReturnsEmpty()
    {
        var accounts = this._sut.GetCopilotAccounts();
        Assert.Empty(accounts);
    }

    // === SessionBaseline ===
    [Fact]
    public void GetSessionBaseline_NotSet_ReturnsNull()
    {
        Assert.Null(this._sut.GetSessionBaseline(ProviderId.Copilot));
    }

    [Fact]
    public void SetSessionBaseline_ThenGet_ReturnsValue()
    {
        this._sut.SetSessionBaseline(ProviderId.Copilot, 42.5m);
        Assert.Equal(42.5m, this._sut.GetSessionBaseline(ProviderId.Copilot));
    }

    [Fact]
    public void SetSessionBaseline_SetsResetTime()
    {
        this._sut.SetSessionBaseline(ProviderId.Claude, 10m);
        var resetTime = this._sut.GetSessionResetTime(ProviderId.Claude);
        Assert.NotNull(resetTime);
        Assert.True(resetTime > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void SetSessionBaseline_String_ThenGet_ReturnsValue()
    {
        this._sut.SetSessionBaseline("custom-key", 99.9m);
        Assert.Equal(99.9m, this._sut.GetSessionBaseline("custom-key"));
    }

    // === MergeFromDisk ===
    [Fact]
    public void Save_PreservesCredentialsFromDisk_WhenMemoryHasEmptyKey()
    {
        // Save initial settings with API key
        var initial = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers = new Dictionary<string, ProviderSettings>
            {
                [ProviderId.Copilot.ToString()] = new() { Enabled = true, ApiKey = "disk-secret" }
            }
        };
        this.WriteSettings(initial);

        // Load, wipe the key, then save — MergeFromDisk should restore
        var loaded = this._sut.Load();
        loaded.Providers![ProviderId.Copilot.ToString()]!.ApiKey = string.Empty;
        this._sut.Save(loaded);

        var final = this._sut.Load();

        // Key should be preserved from disk (merge) even though memory had empty string
        // However, the sanitization step converts empty to null, so disk value gets merged
        Assert.Equal("disk-secret", final.Providers![ProviderId.Copilot.ToString()]!.ApiKey);
    }

    [Fact]
    public void Save_PreservesWorkspaceIdFromDisk_WhenMemoryHasEmpty()
    {
        var initial = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            OpenCodeGoWorkspaceId = "ws-from-disk",
            Providers = new Dictionary<string, ProviderSettings>()
        };
        this.WriteSettings(initial);

        var loaded = this._sut.Load();
        loaded.OpenCodeGoWorkspaceId = string.Empty;
        this._sut.Save(loaded);

        var final = this._sut.Load();

        // After merge from disk, the value should be preserved
        Assert.Equal("ws-from-disk", final.OpenCodeGoWorkspaceId);
    }

    [Fact]
    public void Save_PreservesSessionBaselineFromDisk()
    {
        var initial = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            SessionSpendingBaselines = new Dictionary<string, decimal> { ["Copilot"] = 42.5m },
            SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset> { ["Copilot"] = DateTimeOffset.UtcNow },
            Providers = new Dictionary<string, ProviderSettings>()
        };
        this.WriteSettings(initial);

        var loaded = this._sut.Load();
        this._sut.Save(loaded);

        var final = this._sut.Load();
        Assert.Equal(42.5m, final.SessionSpendingBaselines!["Copilot"]);
    }

    // === NormalizeProviders ===
    [Fact]
    public void Load_NullProviderSettings_NormalizedToDefaults()
    {
        var json = """
        {
            "refreshIntervalSeconds": 120,
            "providers": {
                "Copilot": null,
                "Claude": { "enabled": true }
            }
        }
        """;
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);

        var loaded = this._sut.Load();
        Assert.NotNull(loaded.Providers![ProviderId.Copilot.ToString()]);
        Assert.True(loaded.Providers[ProviderId.Copilot.ToString()]!.Enabled);
    }

    // === DeepCopy isolation ===
    [Fact]
    public void Load_ReturnsCopy_MutationsDoNotAffectStored()
    {
        this._sut.Load();
        var copy = this._sut.Load();
        copy.RefreshIntervalSeconds = 999;

        var fresh = this._sut.Load();
        Assert.NotEqual(999, fresh.RefreshIntervalSeconds);
    }

    // === Window dimensions ===
    [Fact]
    public void Save_ThenLoad_PreservesWindowDimensions()
    {
        var settings = this._sut.Load();
        settings.WindowWidth = 800;
        settings.WindowHeight = 600;
        settings.WindowLeft = 100;
        settings.WindowTop = 50;
        this._sut.Save(settings);

        var loaded = this._sut.Load();
        Assert.Equal(800, loaded.WindowWidth);
        Assert.Equal(600, loaded.WindowHeight);
        Assert.Equal(100, loaded.WindowLeft);
        Assert.Equal(50, loaded.WindowTop);
    }

    private void WriteSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(Path.Combine(this._tempDir, "settings.json"), json);
    }
}
