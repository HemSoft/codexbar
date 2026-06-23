// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests for the decomposed MergeFromDisk helper methods:
/// MergeProviders, MergeWorkspaceId, MergeSessionBaselines, MergeSessionResetTimes.
/// Covers partial settings files, missing fields, and merge conflict scenarios.
/// </summary>
public class SettingsServiceMergeHelpersTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceMergeHelpersTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-merge-{Guid.NewGuid():N}");
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
        }
    }

    private SettingsService CreateService() =>
        new(NullLogger<SettingsService>.Instance, this._tempDir);

    private string SettingsPath => Path.Combine(this._tempDir, "settings.json");

    // --- MergeProviders: partial file with only some providers ---
    [Fact]
    public void Save_PartialDiskProviders_MergesMissingProviders()
    {
        // Disk has ProviderA and ProviderB
        var diskSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["ProviderA"] = new() { ApiKey = "key-a", Enabled = true },
                ["ProviderB"] = new() { ApiKey = "key-b", Enabled = false },
            },
        };
        this.WriteDisk(diskSettings);

        // Memory only has ProviderA (ProviderB should be merged)
        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["ProviderA"] = new() { ApiKey = "key-a-mem", Enabled = true },
            },
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.True(loaded.Providers.ContainsKey("ProviderB"));
        Assert.Equal("key-b", loaded.Providers["ProviderB"].ApiKey);
        Assert.False(loaded.Providers["ProviderB"].Enabled);
    }

    // --- MergeProviders: disk has null ProviderSettings entry ---
    [Fact]
    public void Save_DiskNullProviderSettings_CreatesEmptyProviderSettings()
    {
        File.WriteAllText(this.SettingsPath, """{"providers":{"NullProvider":null,"Active":{"enabled":true}}}""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Active"] = new() { Enabled = true },
            },
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.True(loaded.Providers.ContainsKey("NullProvider"));
        Assert.NotNull(loaded.Providers["NullProvider"]);
    }

    // --- MergeProviders: memory provider has ApiKey, disk also has ApiKey → memory wins ---
    [Fact]
    public void Save_MemoryHasApiKey_DiskApiKeyNotOverwritten()
    {
        var diskSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { ApiKey = "old-disk-key", Enabled = true },
            },
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["OpenRouter"] = new() { ApiKey = "new-memory-key", Enabled = true },
            },
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal("new-memory-key", loaded.Providers["OpenRouter"].ApiKey);
    }

    // --- MergeWorkspaceId: memory has workspace ID → disk not merged ---
    [Fact]
    public void Save_MemoryHasWorkspaceId_DiskValueIgnored()
    {
        var diskSettings = new AppSettings
        {
            OpenCodeGoWorkspaceId = "disk-workspace",
            Providers = [],
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            OpenCodeGoWorkspaceId = "memory-workspace",
            Providers = [],
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal("memory-workspace", loaded.OpenCodeGoWorkspaceId);
    }

    // --- MergeWorkspaceId: both null → stays null ---
    [Fact]
    public void Save_BothWorkspaceIdNull_RemainsNull()
    {
        var diskSettings = new AppSettings
        {
            OpenCodeGoWorkspaceId = null,
            Providers = [],
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            OpenCodeGoWorkspaceId = null,
            Providers = [],
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Null(loaded.OpenCodeGoWorkspaceId);
    }

    // --- MergeCopilotBillingSettings: memory is missing values → disk values are preserved ---
    [Fact]
    public void Save_MissingCopilotBillingSettings_PreservesDiskValues()
    {
        var diskSettings = new AppSettings
        {
            Providers = [],
            CopilotEnterprise = "disk-enterprise",
            CopilotOrganization = "disk-org",
            CopilotPoolTotal = 1234.50m,
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotEnterprise = string.Empty,
            CopilotOrganization = " ",
            CopilotPoolTotal = null,
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal("disk-enterprise", loaded.CopilotEnterprise);
        Assert.Equal("disk-org", loaded.CopilotOrganization);
        Assert.Equal(1234.50m, loaded.CopilotPoolTotal);
    }

    // --- MergeCopilotBillingSettings: partial memory values → only missing values are preserved ---
    [Fact]
    public void Save_PartialCopilotBillingSettings_PreservesOnlyMissingValues()
    {
        var diskSettings = new AppSettings
        {
            Providers = [],
            CopilotEnterprise = "disk-enterprise",
            CopilotOrganization = "disk-org",
            CopilotPoolTotal = 1234.50m,
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotEnterprise = "memory-enterprise",
            CopilotOrganization = string.Empty,
            CopilotPoolTotal = null,
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal("memory-enterprise", loaded.CopilotEnterprise);
        Assert.Equal("disk-org", loaded.CopilotOrganization);
        Assert.Equal(1234.50m, loaded.CopilotPoolTotal);
    }

    // --- MergeCopilotBillingSettings: memory has values → disk does not replace them ---
    [Fact]
    public void Save_ConfiguredCopilotBillingSettings_KeepsMemoryValues()
    {
        var diskSettings = new AppSettings
        {
            Providers = [],
            CopilotEnterprise = "disk-enterprise",
            CopilotOrganization = "disk-org",
            CopilotPoolTotal = 1234.50m,
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotEnterprise = "memory-enterprise",
            CopilotOrganization = "memory-org",
            CopilotPoolTotal = 42m,
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal("memory-enterprise", loaded.CopilotEnterprise);
        Assert.Equal("memory-org", loaded.CopilotOrganization);
        Assert.Equal(42m, loaded.CopilotPoolTotal);
    }

    // --- MergeCopilotBillingSettings: disk has null or blank values → save falls back normally ---
    [Fact]
    public void Save_NullOrBlankDiskCopilotBillingSettings_UsesNormalizedDefaults()
    {
        File.WriteAllText(
            this.SettingsPath,
            """
            {
              "providers": {},
              "copilotEnterprise": null,
              "copilotOrganization": " ",
              "copilotPoolTotal": null
            }
            """);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotEnterprise = string.Empty,
            CopilotOrganization = string.Empty,
            CopilotPoolTotal = null,
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal("bertelsmann", loaded.CopilotEnterprise);
        Assert.Equal("Relias-Engineering", loaded.CopilotOrganization);
        Assert.Null(loaded.CopilotPoolTotal);
    }

    [Fact]
    public void Save_MissingCopilotKnownAccounts_PreservesDiskAccounts()
    {
        var diskSettings = new AppSettings
        {
            Providers = [],
            CopilotKnownAccounts = ["alice", "bob"],
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotKnownAccounts = [],
        };

        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal(["alice", "bob"], loaded.CopilotKnownAccounts);
    }

    [Fact]
    public void Save_NullCopilotKnownAccounts_PreservesDiskAccounts()
    {
        var diskSettings = new AppSettings
        {
            Providers = [],
            CopilotKnownAccounts = ["alice"],
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotKnownAccounts = null!,
        };

        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal(["alice"], loaded.CopilotKnownAccounts);
    }

    [Fact]
    public void Save_ConfiguredCopilotKnownAccounts_KeepsMemoryAccounts()
    {
        var diskSettings = new AppSettings
        {
            Providers = [],
            CopilotKnownAccounts = ["disk"],
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotKnownAccounts = ["memory"],
        };

        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal(["memory"], loaded.CopilotKnownAccounts);
    }

    [Fact]
    public void Save_MissingCopilotKnownAccounts_DiskNullOrEmptyKeepsMemoryEmpty()
    {
        File.WriteAllText(this.SettingsPath, """{"providers":{},"copilotKnownAccounts":null}""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            CopilotKnownAccounts = [],
        };

        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Empty(loaded.CopilotKnownAccounts);
    }

    // --- MergeSessionBaselines: memory has baseline, disk has same key → memory wins ---
    [Fact]
    public void Save_MemoryBaseline_TakesPrecedenceOverDisk()
    {
        var service = this.CreateService();
        service.SetSessionBaseline("key1", 100m);

        // Re-save with a different baseline in memory for same key
        var service2 = this.CreateService();
        var settings = service2.Load();
        settings.SessionSpendingBaselines["key1"] = 200m;
        service2.Save(settings);

        var loaded = this.CreateService().Load();
        Assert.Equal(200m, loaded.SessionSpendingBaselines["key1"]);
    }

    // --- MergeSessionBaselines: disk has extra keys → merged into memory ---
    [Fact]
    public void Save_DiskHasExtraBaselines_MergedIntoResult()
    {
        var diskSettings = new AppSettings
        {
            SessionSpendingBaselines = new Dictionary<string, decimal>
            {
                ["key1"] = 50m,
                ["key2"] = 75m,
            },
            SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset>
            {
                ["key1"] = DateTimeOffset.UtcNow.AddHours(-1),
                ["key2"] = DateTimeOffset.UtcNow.AddHours(-2),
            },
            Providers = [],
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            SessionSpendingBaselines = new Dictionary<string, decimal>
            {
                ["key1"] = 60m, // Memory wins for key1
            },
            SessionSpendingResetTimes = [],
            Providers = [],
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.Equal(60m, loaded.SessionSpendingBaselines["key1"]);
        Assert.Equal(75m, loaded.SessionSpendingBaselines["key2"]);
    }

    // --- MergeSessionResetTimes: disk has extra times → merged ---
    [Fact]
    public void Save_DiskHasExtraResetTimes_MergedIntoResult()
    {
        var diskTime = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var diskSettings = new AppSettings
        {
            SessionSpendingResetTimes = new Dictionary<string, DateTimeOffset>
            {
                ["provider1"] = diskTime,
            },
            Providers = [],
        };
        this.WriteDisk(diskSettings);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            SessionSpendingResetTimes = [],
            Providers = [],
        };
        service.Save(memSettings);

        var loaded = this.CreateService().Load();
        Assert.True(loaded.SessionSpendingResetTimes.ContainsKey("provider1"));
    }

    // --- Corrupt file: invalid JSON → merge skipped, save succeeds ---
    [Fact]
    public void Save_TruncatedJsonOnDisk_MergeSkippedGracefully()
    {
        File.WriteAllText(this.SettingsPath, """{"providers":{"OpenRouter":{"apiKey":"trunc""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Copilot"] = new() { Enabled = true },
            },
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);

        var loaded = this.CreateService().Load();
        Assert.True(loaded.Providers.ContainsKey("Copilot"));
    }

    // --- Empty file on disk ---
    [Fact]
    public void Save_EmptyFileOnDisk_MergeSkippedGracefully()
    {
        File.WriteAllText(this.SettingsPath, string.Empty);

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Test"] = new() { Enabled = true, ApiKey = "key" },
            },
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);

        var loaded = this.CreateService().Load();
        Assert.Equal("key", loaded.Providers["Test"].ApiKey);
    }

    // --- MergeProviders: null Providers on disk ---
    [Fact]
    public void Save_DiskWithNullProviders_NoExceptionDuringMerge()
    {
        File.WriteAllText(this.SettingsPath, """{"refreshIntervalSeconds":60}""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderSettings>
            {
                ["Test"] = new() { Enabled = true },
            },
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);
    }

    // --- MergeSessionBaselines: null baselines on disk ---
    [Fact]
    public void Save_DiskWithNullBaselines_NoExceptionDuringMerge()
    {
        File.WriteAllText(this.SettingsPath, """{"providers":{},"sessionSpendingBaselines":null}""");

        var service = this.CreateService();
        var memSettings = new AppSettings
        {
            Providers = [],
            SessionSpendingBaselines = new Dictionary<string, decimal> { ["k"] = 10m },
        };

        var ex = Record.Exception(() => service.Save(memSettings));
        Assert.Null(ex);

        var loaded = this.CreateService().Load();
        Assert.Equal(10m, loaded.SessionSpendingBaselines["k"]);
    }

    private void WriteDisk(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(this.SettingsPath, json);
    }
}
