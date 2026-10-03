// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AccountConfigurationGuardTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codexbar-account-guards-").FullName;

    [Fact]
    public void Save_FutureSchemaWithUnknownEnum_RefusesOverwrite()
    {
        var service = this.CreateService();
        var draft = service.Load();
        const string future = """{"accountConfigurationVersion":2,"accounts":[{"id":"future","providerId":"FutureProvider","displayLabel":"Future"}]}""";
        File.WriteAllText(this.SettingsPath, future);
        Assert.Throws<InvalidOperationException>(() => service.Save(draft));
        Assert.Equal(future, File.ReadAllText(this.SettingsPath));
    }

    [Fact]
    public void SetSessionBaseline_FutureSchema_RefusesOverwrite()
    {
        var service = this.CreateService();
        service.Load();
        const string future = """{"accountConfigurationVersion":2,"accounts":[],"futureAccountField":"keep"}""";
        File.WriteAllText(this.SettingsPath, future);
        Assert.Throws<InvalidOperationException>(() => service.SetSessionBaseline(ProviderId.Claude, 1m));
        Assert.Equal(future, File.ReadAllText(this.SettingsPath));
    }

    [Fact]
    public void Save_AnotherWriterHoldsCrossProcessLock_LeavesFileUnchanged()
    {
        var service = this.CreateService();
        var draft = service.Load();
        var original = File.ReadAllText(this.SettingsPath);
        using var otherWriter = new FileStream(Path.Combine(this._directory, "settings.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => service.Save(draft));
        Assert.Equal(original, File.ReadAllText(this.SettingsPath));
    }

    [Fact]
    public void Save_CurrentSchemaWithUnknownEnum_RefusesOverwrite()
    {
        var service = this.CreateService();
        var draft = service.Load();
        const string unreadable = """{"accountConfigurationVersion":1,"accounts":[{"id":"unknown","providerId":"UnknownProvider","displayLabel":"Unknown"}]}""";
        File.WriteAllText(this.SettingsPath, unreadable);
        Assert.Throws<InvalidOperationException>(() => service.Save(draft));
        Assert.Equal(unreadable, File.ReadAllText(this.SettingsPath));
    }

    [Fact]
    public void Deserialize_AccountMissingProvider_RejectsInsteadOfAssumingOpenRouter()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<ProviderAccountSettings>("""{"id":"account","displayLabel":"Missing provider"}"""));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Migrate_CopilotKeyCasing_PreservesDisabledStateAndPlaceholder(bool knownUser)
    {
        var settings = new AppSettings { Providers = new() { ["cOpIlOt"] = new() { Enabled = false } }, CopilotKnownAccounts = knownUser ? ["octocat"] : [] };
        AccountConfiguration.Migrate(settings);
        var account = Assert.Single(settings.Accounts);
        Assert.Equal(ProviderId.Copilot, account.ProviderId);
        Assert.False(account.Enabled);
    }

    [Fact]
    public void Save_DiskLockReleasedAfterFutureWrite_RefusesNewerVersion()
    {
        var service = this.CreateService();
        var draft = service.Load();
        const string future = """{"accountConfigurationVersion":2,"accounts":[]} """;
        using (var otherWriter = new FileStream(Path.Combine(this._directory, "settings.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            File.WriteAllText(this.SettingsPath, future);
            Assert.Throws<IOException>(() => service.Save(draft));
        }

        Assert.Throws<InvalidOperationException>(() => service.Save(draft));
        Assert.Equal(future, File.ReadAllText(this.SettingsPath));
    }

    [Fact]
    public void SetSessionBaseline_LockFailure_RetainsCachedAndPersistedBaselineThenRetries()
    {
        var service = this.CreateService();
        service.SetSessionBaseline(ProviderId.Codex, 5m);
        var before = File.ReadAllText(this.SettingsPath);
        var reset = service.GetSessionResetTime(ProviderId.Codex);
        using (var otherWriter = new FileStream(Path.Combine(this._directory, "settings.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => service.SetSessionBaseline(ProviderId.Codex, 9m));
            Assert.Equal(5m, service.GetSessionBaseline(ProviderId.Codex));
            Assert.Equal(reset, service.GetSessionResetTime(ProviderId.Codex));
            Assert.Equal(before, File.ReadAllText(this.SettingsPath));
        }

        service.SetSessionBaseline(ProviderId.Codex, 9m);
        Assert.Equal(9m, service.GetSessionBaseline(ProviderId.Codex));
        Assert.Equal(9m, this.CreateService().GetSessionBaseline(ProviderId.Codex));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("\t")]
    public void SetSessionBaseline_UnsanitizedLegacyFields_KeepsCacheConsistentWithDisk(string workspaceId)
    {
        File.WriteAllText(this.SettingsPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            openCodeGoWorkspaceId = workspaceId,
            providers = new { Claude = new { enabled = true, apiKey = " " } },
            copilotAccounts = new[] { " octocat ", "octocat" },
            zoomLevel = 0,
        }));
        var service = this.CreateService();
        service.Load();

        service.SetSessionBaseline(ProviderId.Codex, 12m);

        var persisted = this.CreateService().Load();
        var cached = service.Load();
        Assert.Null(persisted.OpenCodeGoWorkspaceId);
        Assert.Equal(persisted.OpenCodeGoWorkspaceId, cached.OpenCodeGoWorkspaceId);
        Assert.Null(service.GetOpenCodeGoWorkspaceId());
        Assert.Equal(persisted.Providers["Claude"].ApiKey, service.GetApiKey(ProviderId.Claude));
        Assert.Equal(persisted.CopilotAccounts, cached.CopilotAccounts);
        Assert.Equal(persisted.ZoomLevel, cached.ZoomLevel);
        Assert.Equal(12m, service.GetSessionBaseline(ProviderId.Codex));
        Assert.Equal(persisted.SessionSpendingResetTimes, cached.SessionSpendingResetTimes);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("edit")]
    [InlineData("remove")]
    public void SetSessionBaseline_AnotherInstanceChangedAccounts_PreservesLatestDiskSettings(string change)
    {
        var baselineWriter = this.CreateService();
        baselineWriter.SetSessionBaseline("other-background", 1m);
        var editor = this.CreateService();
        var edited = editor.Load();
        var account = edited.Accounts.First();
        if (change == "add")
        {
            AccountConfiguration.Upsert(edited, AccountConfiguration.Create(ProviderId.Claude, "Concurrent account"));
        }
        else if (change == "edit")
        {
            AccountConfiguration.Upsert(edited, account with { DisplayLabel = "Concurrent label", Enabled = !account.Enabled });
        }
        else
        {
            AccountConfiguration.Remove(edited, account.Id);
        }

        edited.Providers["Claude"].Enabled = true;
        edited.OpenCodeGoWorkspaceId = "concurrent-workspace";
        edited.CopilotAccounts = ["concurrent-user"];
        edited.CopilotKnownAccounts = ["concurrent-user"];
        edited.ZoomLevel = 1.75;
        editor.Save(edited);
        editor.SetSessionBaseline("other-background", 4m);
        var expected = editor.Load();

        baselineWriter.SetSessionBaseline("current-background", 12m);

        var cached = baselineWriter.Load();
        var persisted = this.CreateService().Load();
        Assert.Equal(expected.Accounts, cached.Accounts);
        Assert.Equal(expected.Accounts, persisted.Accounts);
        Assert.Equal(expected.Providers["Claude"].Enabled, baselineWriter.IsProviderEnabled(ProviderId.Claude));
        Assert.Equal(expected.OpenCodeGoWorkspaceId, baselineWriter.GetOpenCodeGoWorkspaceId());
        Assert.Equal(expected.CopilotAccounts, baselineWriter.GetCopilotAccounts());
        Assert.Equal(expected.CopilotKnownAccounts, cached.CopilotKnownAccounts);
        Assert.Equal(expected.ZoomLevel, cached.ZoomLevel);
        Assert.Equal(4m, baselineWriter.GetSessionBaseline("other-background"));
        Assert.Equal(12m, baselineWriter.GetSessionBaseline("current-background"));
        Assert.Equal(expected.SessionSpendingResetTimes["other-background"], cached.SessionSpendingResetTimes["other-background"]);
        Assert.Equal(cached.SessionSpendingBaselines, persisted.SessionSpendingBaselines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_StaleNonAccountDraft_PreservesLatestAccountsEvenAfterCacheAdvances(bool advanceCache)
    {
        var service = this.CreateService();
        var draft = service.Load();
        var editor = this.CreateService();
        var edited = editor.Load();
        AccountConfiguration.Upsert(edited, edited.Accounts.First() with { DisplayLabel = "Concurrent label" });
        edited.Providers["Claude"].Enabled = true;
        edited.OpenCodeGoWorkspaceId = "concurrent-workspace";
        edited.CopilotAccounts = ["concurrent-user"];
        edited.CopilotKnownAccounts = ["concurrent-user"];
        editor.Save(edited);
        if (advanceCache)
        {
            service.SetSessionBaseline("background", 3m);
        }

        draft.WindowWidth = 900;
        service.Save(draft);

        var saved = this.CreateService().Load();
        Assert.Equal(edited.Accounts, saved.Accounts);
        Assert.Equal(900, saved.WindowWidth);
        Assert.True(saved.Providers["Claude"].Enabled);
        Assert.Equal(edited.OpenCodeGoWorkspaceId, saved.OpenCodeGoWorkspaceId);
        Assert.Equal(edited.CopilotAccounts, saved.CopilotAccounts);
        Assert.Equal(edited.CopilotKnownAccounts, saved.CopilotKnownAccounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_ConcurrentAccountEdits_RefusesConflictEvenAfterCacheAdvances(bool advanceCache)
    {
        var service = this.CreateService();
        var draft = service.Load();
        var editor = this.CreateService();
        var edited = editor.Load();
        AccountConfiguration.Upsert(edited, edited.Accounts.First() with { DisplayLabel = "Concurrent label" });
        editor.Save(edited);
        if (advanceCache)
        {
            service.SetSessionBaseline("background", 3m);
        }

        var before = File.ReadAllText(this.SettingsPath);
        AccountConfiguration.Upsert(draft, draft.Accounts.First() with { DisplayLabel = "My draft" });
        Assert.Throws<InvalidOperationException>(() => service.Save(draft));
        Assert.Equal(before, File.ReadAllText(this.SettingsPath));
        Assert.Equal("My draft", draft.Accounts.First().DisplayLabel);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Save_StructurallyInvalidDiskAccounts_RefusesOverwriteAndRetainsCache(bool baselineUpdate, bool duplicateIds)
    {
        var service = this.CreateService();
        var draft = service.Load();
        var invalid = service.Load();
        var account = invalid.Accounts.First();
        invalid.Accounts = duplicateIds ? [account, account] : [account with { DisplayLabel = " " }];
        var json = System.Text.Json.JsonSerializer.Serialize(invalid, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        File.WriteAllText(this.SettingsPath, json);

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (baselineUpdate)
            {
                service.SetSessionBaseline(ProviderId.Claude, 12m);
            }
            else
            {
                service.Save(draft);
            }
        });

        Assert.Equal(json, File.ReadAllText(this.SettingsPath));
        Assert.Equal(draft.Accounts, service.Load().Accounts);
        Assert.Null(service.GetSessionBaseline(ProviderId.Claude));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_NullVersionedDiskAccounts_RefusesOverwriteAndRetainsCache(bool baselineUpdate)
    {
        var service = this.CreateService();
        var draft = service.Load();
        const string json = """{"accountConfigurationVersion":1,"accounts":null}""";
        File.WriteAllText(this.SettingsPath, json);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (baselineUpdate)
            {
                service.SetSessionBaseline(ProviderId.Claude, 12m);
            }
            else
            {
                service.Save(draft);
            }
        });
        Assert.Equal(json, File.ReadAllText(this.SettingsPath));
        Assert.Equal(draft.Accounts, service.Load().Accounts);
        Assert.Throws<InvalidOperationException>(() => this.CreateService().Load());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_MissingVersionedDiskAccountArray_RefusesOverwriteAndRetainsCache(bool baselineUpdate)
    {
        var service = this.CreateService();
        var draft = service.Load();
        const string json = """{"accountConfigurationVersion":1,"syntheticMetadata":"keep"}""";
        File.WriteAllText(this.SettingsPath, json);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (baselineUpdate)
            {
                service.SetSessionBaseline(ProviderId.Claude, 12m);
            }
            else
            {
                service.Save(draft);
            }
        });
        Assert.Equal(json, File.ReadAllText(this.SettingsPath));
        Assert.Equal(draft.Accounts, service.Load().Accounts);
        Assert.Throws<InvalidOperationException>(() => this.CreateService().Load());
    }

    [Theory]
    [InlineData(false, "\"authenticationMethod\":\"Automatic\"")]
    [InlineData(true, "\"authenticationMethod\":\"Automatic\"")]
    [InlineData(false, "\"enabled\":true")]
    [InlineData(true, "\"enabled\":true")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    public void Save_VersionedAccountMissingStateFields_RefusesOverwriteAndRetainsCache(bool baselineUpdate, string fields)
    {
        var service = this.CreateService();
        var draft = service.Load();
        var suffix = fields.Length == 0 ? string.Empty : "," + fields;
        var json = "{\"accountConfigurationVersion\":1,\"accounts\":[{\"id\":\"one\",\"providerId\":\"Claude\",\"displayLabel\":\"Known\"" + suffix + "}]}";
        File.WriteAllText(this.SettingsPath, json);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (baselineUpdate)
            {
                service.SetSessionBaseline(ProviderId.Claude, 12m);
            }
            else
            {
                service.Save(draft);
            }
        });
        Assert.Equal(json, File.ReadAllText(this.SettingsPath));
        Assert.Equal(draft.Accounts, service.Load().Accounts);
        Assert.Throws<InvalidOperationException>(() => this.CreateService().Load());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unchanged")]
    [InlineData("new")]
    public void Save_DiskDeletedProviderOverride_PreservesDeletionUnlessDraftChangedCredential(string credential)
    {
        var service = this.CreateService();
        var initial = service.Load();
        initial.Providers["Claude"].Enabled = false;
        initial.Providers["Claude"].ApiKey = credential == "none" ? null : "synthetic-previous";
        service.Save(initial);
        var draft = service.Load();
        var disk = this.CreateService().Load();
        disk.Providers.Remove("Claude");
        File.WriteAllText(this.SettingsPath, System.Text.Json.JsonSerializer.Serialize(disk, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        if (credential == "new")
        {
            draft.Providers["Claude"].ApiKey = "synthetic-new";
        }

        draft.WindowWidth = 900;
        service.Save(draft);

        var saved = this.CreateService().Load();
        Assert.Equal(900, saved.WindowWidth);
        Assert.Equal(disk.Accounts, saved.Accounts);
        if (credential == "new")
        {
            Assert.True(saved.Providers["Claude"].Enabled);
            Assert.Equal("synthetic-new", saved.Providers["Claude"].ApiKey);
        }
        else
        {
            Assert.False(saved.Providers.ContainsKey("Claude"));
        }
    }

    [Fact]
    public void Load_AccountSnapshot_IsDetachedAndNeverSerialized()
    {
        var draft = this.CreateService().Load();
        var snapshot = Assert.IsType<AccountConfigurationSnapshot>(draft.AccountSnapshot);
        var count = snapshot.Accounts.Count;
        var originalEnabled = snapshot.ProviderStates["Claude"];
        draft.Accounts.Clear();
        draft.CopilotAccounts.Add("draft-user");
        draft.CopilotKnownAccounts.Add("draft-user");
        draft.Providers["Claude"].Enabled = !originalEnabled;

        Assert.Equal(count, snapshot.Accounts.Count);
        Assert.Empty(snapshot.CopilotAccounts);
        Assert.Empty(snapshot.CopilotKnownAccounts);
        Assert.Equal(originalEnabled, snapshot.ProviderStates["Claude"]);
        Assert.DoesNotContain("AccountSnapshot", System.Text.Json.JsonSerializer.Serialize(draft), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_ReusedAccountDraft_RefreshesSnapshotAfterSuccessfulSave()
    {
        var service = this.CreateService();
        var draft = service.Load();
        var initial = draft.AccountSnapshot;
        AccountConfiguration.Upsert(draft, draft.Accounts.First() with { DisplayLabel = "First edit" });
        service.Save(draft);
        AccountConfiguration.Upsert(draft, draft.Accounts.First() with { DisplayLabel = "Second edit" });
        service.Save(draft);

        Assert.NotSame(initial, draft.AccountSnapshot);
        Assert.Equal("Second edit", this.CreateService().Load().Accounts.First().DisplayLabel);
    }

    [Theory]
    [InlineData("""{"accountConfigurationVersion":1,"accounts":[{"id":"same","providerId":"Claude","displayLabel":"First"},{"id":"same","providerId":"Claude","displayLabel":"Second"}]}""")]
    [InlineData("""{"accountConfigurationVersion":1,"accounts":[{"id":"one","providerId":"Claude","displayLabel":" "}]}""")]
    [InlineData("""{"accountConfigurationVersion":1,"accounts":[{"id":"one","providerId":"UnknownProvider","displayLabel":"Unknown"}]}""")]
    public void Load_InvalidVersionedDisk_RefusesRepeatedLoadsWithoutPublishingBadCache(string json)
    {
        File.WriteAllText(this.SettingsPath, json);
        var service = this.CreateService();
        Assert.Throws<InvalidOperationException>(() => service.Load());
        Assert.Throws<InvalidOperationException>(() => service.Load());
        Assert.Equal(json, File.ReadAllText(this.SettingsPath));
        File.WriteAllText(this.SettingsPath, """{"accountConfigurationVersion":1,"accounts":[]}""");
        Assert.Empty(service.Load().Accounts);
    }

    [Fact]
    public void Load_LowercaseProviderKeys_AllReadersAgreeWithMigration()
    {
        const string legacy = """{"providers":{"copilot":{"enabled":false,"apiKey":"synthetic"},"moonshot":{"enabled":true}}}""";
        File.WriteAllText(this.SettingsPath, legacy);
        var service = this.CreateService();
        Assert.False(service.IsProviderEnabled(ProviderId.Copilot));
        Assert.True(service.IsProviderEnabled(ProviderId.Moonshot));
        Assert.Equal("synthetic", service.GetApiKey(ProviderId.Copilot));
        Assert.False(service.Load().Accounts.Single(account => account.ProviderId == ProviderId.Copilot).Enabled);
    }

    public void Dispose() => Directory.Delete(this._directory, true);

    private string SettingsPath => Path.Combine(this._directory, "settings.json");

    private SettingsService CreateService() => new(NullLogger<SettingsService>.Instance, this._directory);
}
