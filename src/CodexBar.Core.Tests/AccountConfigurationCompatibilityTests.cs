// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AccountConfigurationCompatibilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Save_DiskUpgradedAfterLoad_RefusesToOverwriteFutureConfiguration(int draftVersion)
    {
        var directory = Directory.CreateTempSubdirectory("codexbar-future-schema-").FullName;
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            var draft = service.Load();
            draft.AccountConfigurationVersion = draftVersion;
            const string future = """{"accountConfigurationVersion":2,"accounts":[],"futureAccountField":"keep"}""";
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, future);
            Assert.Throws<InvalidOperationException>(() => service.Save(draft));
            Assert.Equal(future, File.ReadAllText(path));
            Assert.Throws<InvalidOperationException>(() => new SettingsService(NullLogger<SettingsService>.Instance, directory).Load());

            // The existing instance keeps its last-good cached state, not a new disk load.
            Assert.Equal(AccountConfiguration.CurrentVersion, service.Load().AccountConfigurationVersion);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Save_LegacyCallerWithMalformedVersionedAccounts_RefusesOverwriteWithoutMutatingDraft()
    {
        var directory = Directory.CreateTempSubdirectory("codexbar-account-integrity-").FullName;
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            var draft = service.Load();
            draft.AccountConfigurationVersion = 0;
            const string malformed = """{"accountConfigurationVersion":1,"accounts":[{"id":"same","providerId":"Claude","displayLabel":"First"},{"id":"same","providerId":"Claude","displayLabel":"Second"}]}""";
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, malformed);
            Assert.Throws<ArgumentException>(() => service.Save(draft));
            Assert.Equal(malformed, File.ReadAllText(path));
            Assert.Equal(0, draft.AccountConfigurationVersion);
            Assert.Throws<ArgumentException>(() => new SettingsService(NullLogger<SettingsService>.Instance, directory).Load());

            // Cache rollback and on-disk integrity are separate assertions.
            Assert.Equal(AccountConfiguration.CurrentVersion, service.Load().AccountConfigurationVersion);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Load_FutureVersionOnDisk_RefusesMigrationWithoutWriting()
    {
        var directory = Directory.CreateTempSubdirectory("codexbar-future-schema-").FullName;
        try
        {
            const string future = """{"accountConfigurationVersion":2,"accounts":[],"futureAccountField":"keep"}""";
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, future);
            var service = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            Assert.Throws<InvalidOperationException>(() => service.Load());
            Assert.Equal(future, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
