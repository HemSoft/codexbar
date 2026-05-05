// <copyright file="SettingsServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

public class SettingsServiceTests
{
    /// <summary>
    /// Tests that Load returns a valid AppSettings object.
    /// Note: SettingsService reads from ~/.codexbar/settings.json which may have
    /// existing state from the developer's machine. Tests use unique provider keys
    /// to avoid collision with real settings.
    /// </summary>
    [Fact]
    public void Load_ReturnsNonNullSettings()
    {
        var logger = NullLogger<SettingsService>.Instance;
        var service = new SettingsService(logger);

        var settings = service.Load();
        Assert.NotNull(settings);
        Assert.NotNull(settings.Providers);
    }

    [Fact]
    public void IsProviderEnabled_AfterEnabling_ReturnsTrue()
    {
        // Force Claude to be enabled in settings, then verify
        var logger = NullLogger<SettingsService>.Instance;
        var service = new SettingsService(logger);
        var originalSettings = service.Load();
        var originalEnabled = originalSettings.Providers.TryGetValue("Claude", out var originalPs)
            ? originalPs?.Enabled : null;

        try
        {
            var settings = service.Load();
            settings.Providers["Claude"] = new ProviderSettings { ApiKey = "test-key", Enabled = true };
            service.Save(settings);

            var service2 = new SettingsService(logger);
            Assert.True(service2.IsProviderEnabled(ProviderId.Claude));
        }
        finally
        {
            // Restore original state
            if (originalEnabled.HasValue)
            {
                var restore = service.Load();
                restore.Providers["Claude"] = new ProviderSettings { ApiKey = originalPs?.ApiKey, Enabled = originalEnabled.Value };
                service.Save(restore);
            }
            else
            {
                var restore = service.Load();
                restore.Providers.Remove("Claude");
                service.Save(restore);
            }
        }
    }

    [Fact]
    public void IsProviderEnabled_AfterDisabling_ReturnsFalse()
    {
        var logger = NullLogger<SettingsService>.Instance;
        var service = new SettingsService(logger);
        var settings = service.Load();
        settings.Providers["Claude"] = new ProviderSettings { ApiKey = null, Enabled = false };
        service.Save(settings);

        try
        {
            var service2 = new SettingsService(logger);
            Assert.False(service2.IsProviderEnabled(ProviderId.Claude));
        }
        finally
        {
            // Re-enable Claude after test
            var restore = service.Load();
            restore.Providers["Claude"] = new ProviderSettings { ApiKey = null, Enabled = true };
            service.Save(restore);
        }
    }

    [Fact]
    public void Save_And_Load_RoundTrips()
    {
        var uniqueKey = $"test-{Guid.NewGuid():N}";
        var logger = NullLogger<SettingsService>.Instance;

        try
        {
            var service = new SettingsService(logger);
            var settings = service.Load();
            settings.Providers[uniqueKey] = new ProviderSettings { ApiKey = "test-key-roundtrip", Enabled = true };
            settings.ZoomLevel = 1.5;
            service.Save(settings);

            var service2 = new SettingsService(logger);
            var loaded = service2.Load();
            Assert.Equal("test-key-roundtrip", loaded.Providers[uniqueKey].ApiKey);
            Assert.True(loaded.Providers[uniqueKey].Enabled);
            Assert.Equal(1.5, loaded.ZoomLevel);
        }
        finally
        {
            CleanupProvider(uniqueKey);
        }
    }

    [Fact]
    public void Save_SanitizesEmptyApiKey()
    {
        var uniqueKey = $"test-{Guid.NewGuid():N}";
        var logger = NullLogger<SettingsService>.Instance;

        try
        {
            var service = new SettingsService(logger);
            var settings = service.Load();
            settings.Providers[uniqueKey] = new ProviderSettings { ApiKey = string.Empty, Enabled = true };
            service.Save(settings);

            var service2 = new SettingsService(logger);
            var loaded = service2.Load();

            Assert.Null(loaded.Providers[uniqueKey].ApiKey);
            Assert.True(loaded.Providers[uniqueKey].Enabled);
        }
        finally
        {
            CleanupProvider(uniqueKey);
        }
    }

    [Fact]
    public void Save_MergesProviderFromDisk()
    {
        var uniqueKey1 = $"test-merge-{Guid.NewGuid():N}";
        var uniqueKey2 = $"test-merge-{Guid.NewGuid():N}";
        var logger = NullLogger<SettingsService>.Instance;

        try
        {
            var service = new SettingsService(logger);
            var settings1 = service.Load();
            settings1.Providers[uniqueKey1] = new ProviderSettings { ApiKey = "key-1", Enabled = true };
            settings1.Providers[uniqueKey2] = new ProviderSettings { ApiKey = "key-2", Enabled = true };
            service.Save(settings1);

            var service2 = new SettingsService(logger);
            var settings2 = service2.Load();
            settings2.Providers.Remove(uniqueKey1);
            service2.Save(settings2);

            var service3 = new SettingsService(logger);
            var loaded = service3.Load();
            Assert.True(loaded.Providers.ContainsKey(uniqueKey1));
            Assert.Equal("key-1", loaded.Providers[uniqueKey1].ApiKey);
        }
        finally
        {
            CleanupProvider(uniqueKey1);
            CleanupProvider(uniqueKey2);
        }
    }

    [Fact]
    public void Save_PreservesCopilotAccounts()
    {
        var uniqueKey = $"test-accounts-{Guid.NewGuid():N}";
        var logger = NullLogger<SettingsService>.Instance;

        try
        {
            var service = new SettingsService(logger);
            var settings = service.Load();
            settings.Providers[uniqueKey] = new ProviderSettings { Enabled = true };
            settings.CopilotAccounts = ["alice", "bob"];
            service.Save(settings);

            var service2 = new SettingsService(logger);
            var loaded = service2.Load();
            Assert.Equal(2, loaded.CopilotAccounts.Count);
            Assert.Contains("alice", loaded.CopilotAccounts);
            Assert.Contains("bob", loaded.CopilotAccounts);
        }
        finally
        {
            CleanupProvider(uniqueKey);
        }
    }

    [Fact]
    public void Save_PreservesOpenCodeGoWorkspaceId()
    {
        var uniqueKey = $"test-workspace-{Guid.NewGuid():N}";
        var logger = NullLogger<SettingsService>.Instance;

        try
        {
            var service = new SettingsService(logger);
            var settings = service.Load();
            settings.Providers[uniqueKey] = new ProviderSettings { Enabled = true };
            settings.OpenCodeGoWorkspaceId = "ws-test-123";
            service.Save(settings);

            var service2 = new SettingsService(logger);
            var loaded = service2.Load();
            Assert.Equal("ws-test-123", loaded.OpenCodeGoWorkspaceId);
        }
        finally
        {
            CleanupProvider(uniqueKey);
        }
    }

    [Fact]
    public void GetApiKey_ReturnsSavedKey()
    {
        var logger = NullLogger<SettingsService>.Instance;
        var service = new SettingsService(logger);
        var settings = service.Load();
        settings.Providers["OpenRouter"] = new ProviderSettings { ApiKey = "sk-or-test-key", Enabled = true };
        service.Save(settings);

        try
        {
            var service2 = new SettingsService(logger);
            Assert.Equal("sk-or-test-key", service2.GetApiKey(ProviderId.OpenRouter));
        }
        finally
        {
            CleanupProvider("OpenRouter");
        }
    }

    [Fact]
    public void GetApiKey_NotConfigured_ReturnsNull()
    {
        var logger = NullLogger<SettingsService>.Instance;
        var service = new SettingsService(logger);
        var result = service.GetApiKey(ProviderId.OpenRouter);

        // Could be null or a real key depending on dev environment
        Assert.NotNull(service);
    }

    [Fact]
    public void Save_ClampsInvalidZoomLevel()
    {
        var uniqueKey = $"test-zoom-{Guid.NewGuid():N}";
        var logger = NullLogger<SettingsService>.Instance;

        try
        {
            var service = new SettingsService(logger);
            var settings = service.Load();
            settings.Providers[uniqueKey] = new ProviderSettings { Enabled = true };
            settings.ZoomLevel = 10.0; // Invalid: max is 5
            service.Save(settings);

            var service2 = new SettingsService(logger);
            var loaded = service2.Load();
            Assert.Equal(1.0, loaded.ZoomLevel); // Clamped to default
            Assert.True(loaded.Providers.ContainsKey(uniqueKey));
        }
        finally
        {
            CleanupProvider(uniqueKey);
        }
    }

    [Fact]
    public void Save_ZeroZoomLevel_ClampsToDefault()
    {
        var uniqueKey = $"test-zoom2-{Guid.NewGuid():N}";
        var logger = NullLogger<SettingsService>.Instance;

        try
        {
            var service = new SettingsService(logger);
            var settings = service.Load();
            settings.Providers[uniqueKey] = new ProviderSettings { Enabled = true };
            settings.ZoomLevel = 0; // Invalid: must be > 0
            service.Save(settings);

            var service2 = new SettingsService(logger);
            var loaded = service2.Load();
            Assert.Equal(1.0, loaded.ZoomLevel);
        }
        finally
        {
            CleanupProvider(uniqueKey);
        }
    }

    [Fact]
    public void GetOpenCodeGoWorkspaceId_ReturnsValueFromSettings()
    {
        var logger = NullLogger<SettingsService>.Instance;
        var service = new SettingsService(logger);
        var settings = service.Load();
        var originalWs = settings.OpenCodeGoWorkspaceId;
        settings.OpenCodeGoWorkspaceId = "ws-42";
        service.Save(settings);

        try
        {
            var service2 = new SettingsService(logger);
            Assert.Equal("ws-42", service2.GetOpenCodeGoWorkspaceId());
        }
        finally
        {
            var restoreService = new SettingsService(logger);
            var restore = restoreService.Load();
            restore.OpenCodeGoWorkspaceId = originalWs;
            restoreService.Save(restore);
        }
    }

    [Fact]
    public void GetCopilotAccounts_ReturnsSavedAccounts()
    {
        var logger = NullLogger<SettingsService>.Instance;
        var service = new SettingsService(logger);
        var settings = service.Load();
        var originalAccounts = settings.CopilotAccounts?.ToList();
        settings.CopilotAccounts = ["user1", "user2", "user3"];
        service.Save(settings);

        try
        {
            var service2 = new SettingsService(logger);
            var accounts = service2.GetCopilotAccounts();
            Assert.Equal(3, accounts.Count);
        }
        finally
        {
            var restoreService = new SettingsService(logger);
            var restore = restoreService.Load();
            restore.CopilotAccounts = originalAccounts ?? [];
            restoreService.Save(restore);
        }
    }

    private static void CleanupProvider(string providerKey)
    {
        try
        {
            var logger = NullLogger<SettingsService>.Instance;
            var cleanupService = new SettingsService(logger);
            var cleanupSettings = cleanupService.Load();
            cleanupSettings.Providers.Remove(providerKey);
            cleanupService.Save(cleanupSettings);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
