// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.IO;
using System.Text.Json;
using CodexBar.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Coverage tests for SettingsService MergeFromDisk targeting previously
/// uncovered branches: file-not-found early return, null deserialization,
/// and session spending baseline/reset merging.
/// </summary>
public class SettingsServiceCoverageTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceCoverageTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-cov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this._tempDir))
            {
                Directory.Delete(this._tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private SettingsService CreateService() =>
        new(NullLogger<SettingsService>.Instance, this._tempDir);

    [Fact]
    public void Save_NoExistingFile_MergeFromDiskReturnsEarly()
    {
        // This exercises MergeFromDisk when File.Exists returns false (lines 82-83)
        var service = this.CreateService();

        // Create settings directly without loading (no file on disk yet)
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Copilot"] = new() { Enabled = true },
            },
        };

        // Save should succeed even though no file exists for merge
        var ex = Record.Exception(() => service.Save(settings));
        Assert.Null(ex);

        // File should now exist
        var filePath = Path.Combine(this._tempDir, "settings.json");
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void Save_NullJsonOnDisk_MergeFromDiskHandlesGracefully()
    {
        // This exercises MergeFromDisk when disk JSON deserializes to null (lines 91-92)
        var service = this.CreateService();
        var filePath = Path.Combine(this._tempDir, "settings.json");

        // Write "null" to disk
        File.WriteAllText(filePath, "null");

        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 60,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = "test-key" },
            },
        };

        // Save should succeed, MergeFromDisk returns early when disk is null
        var ex = Record.Exception(() => service.Save(settings));
        Assert.Null(ex);
    }

    [Fact]
    public void Save_DiskHasSessionBaselines_MergedIntoMemory()
    {
        var service = this.CreateService();
        var filePath = Path.Combine(this._tempDir, "settings.json");

        // Write settings with session spending baselines and reset times to disk
        var diskSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            Providers = new Dictionary<string, ProviderSettings>(),
            SessionSpendingBaselines = new Dictionary<string, decimal> { ["copilot"] = 5.50m },
            SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset>
            {
                ["copilot"] = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
            },
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(diskSettings));

        // Save settings without baselines — disk values should be merged
        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            Providers = new Dictionary<string, ProviderSettings>(),
        };

        service.Save(memSettings);

        // Reload and verify disk baselines survived
        var loaded = service.Load();
        Assert.True(loaded.SessionSpendingBaselines?.ContainsKey("copilot") ?? false);
        Assert.True(loaded.SessionSpendingResetTimes?.ContainsKey("copilot") ?? false);
    }

    [Fact]
    public void Save_DiskHasExtraProviderApiKey_MergedIntoMemory()
    {
        var service = this.CreateService();
        var filePath = Path.Combine(this._tempDir, "settings.json");

        // Write settings with an API key for OpenRouter to disk
        var diskSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = "disk-secret-key" },
            },
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(diskSettings));

        // Save settings with empty API key — disk key should be preserved
        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { Enabled = true, ApiKey = null },
            },
        };

        service.Save(memSettings);

        var loaded = service.Load();
        Assert.Equal("disk-secret-key", loaded.Providers?["OpenRouter"]?.ApiKey);
    }

    [Fact]
    public void Save_DiskHasWorkspaceId_MergedWhenMemoryEmpty()
    {
        var service = this.CreateService();
        var filePath = Path.Combine(this._tempDir, "settings.json");

        var diskSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            OpenCodeGoWorkspaceId = "disk-workspace",
            Providers = new Dictionary<string, ProviderSettings>(),
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(diskSettings));

        var memSettings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            OpenCodeGoWorkspaceId = null,
            Providers = new Dictionary<string, ProviderSettings>(),
        };

        service.Save(memSettings);

        var loaded = service.Load();
        Assert.Equal("disk-workspace", loaded.OpenCodeGoWorkspaceId);
    }
}
