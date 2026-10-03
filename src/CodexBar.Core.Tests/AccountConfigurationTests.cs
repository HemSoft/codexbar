// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Text.Json;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AccountConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"account-configuration-{Guid.NewGuid():N}");

    public AccountConfigurationTests() => Directory.CreateDirectory(this._directory);

    public void Dispose() => Directory.Delete(this._directory, recursive: true);

    [Theory]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.Codex)]
    [InlineData(ProviderId.Copilot)]
    [InlineData(ProviderId.Cursor)]
    [InlineData(ProviderId.OpenRouter)]
    [InlineData(ProviderId.OpenCodeGo)]
    [InlineData(ProviderId.OpenCodeZen)]
    [InlineData(ProviderId.Moonshot)]
    public void Upsert_TwoAccountsForProvider_PreservesIndependentStableIds(ProviderId provider)
    {
        var settings = new AppSettings();
        var first = AccountConfiguration.Create(provider, "Home");
        var second = AccountConfiguration.Create(provider, "Work", ProviderAuthenticationMethod.OAuth);
        AccountConfiguration.Upsert(settings, first);
        AccountConfiguration.Upsert(settings, second);
        AccountConfiguration.Upsert(settings, first with { DisplayLabel = "Renamed", Enabled = false });

        Assert.Equal(2, settings.Accounts.Count);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, settings.Accounts[0].Id);
        Assert.Equal("Renamed", settings.Accounts[0].DisplayLabel);
        Assert.False(settings.Accounts[0].Enabled);
        Assert.True(settings.Accounts[1].Enabled);
        Assert.Equal(ProviderAuthenticationMethod.OAuth, settings.Accounts[1].AuthenticationMethod);
    }

    [Fact]
    public void Migrate_LegacyProviders_PreservesCredentialsWorkspaceAndOrder()
    {
        var settings = new AppSettings
        {
            Providers = new() { ["OpenRouter"] = new() { ApiKey = "synthetic-api-value", Enabled = false }, ["OpenCodeGo"] = new(), ["unsupported"] = new(), ["12345"] = new(), ["Claude"] = null! },
            OpenCodeGoWorkspaceId = "workspace-123",
            ProviderCardOrder = ["copilot:alice", "OpenCodeGo", "custom-key", "OpenRouter"],
        };
        var beforeOrder = settings.ProviderCardOrder.ToArray();

        AccountConfiguration.Migrate(settings);
        var beforeAccounts = settings.Accounts.ToArray();
        AccountConfiguration.Migrate(settings);

        Assert.Equal(1, settings.AccountConfigurationVersion);
        Assert.Equal(3, settings.Accounts.Count);
        Assert.Equal(beforeAccounts, settings.Accounts);
        Assert.Equal(beforeOrder, settings.ProviderCardOrder);
        Assert.Equal("synthetic-api-value", settings.Providers["OpenRouter"].ApiKey);
        Assert.Equal("workspace-123", settings.OpenCodeGoWorkspaceId);
        Assert.Equal("workspace-123", settings.Accounts.Single(a => a.ProviderId == ProviderId.OpenCodeGo).WorkspaceId);
        var router = settings.Accounts.Single(a => a.ProviderId == ProviderId.OpenRouter);
        Assert.False(router.Enabled);
        Assert.Equal(ProviderAuthenticationMethod.ApiKey, router.AuthenticationMethod);
    }

    [Fact]
    public void Migrate_OpenCodeGoLegacyCredential_PreservesBrowserSessionAuthentication()
    {
        var settings = new AppSettings
        {
            Providers = new() { ["OpenCodeGo"] = new() { ApiKey = "synthetic-session-value", Enabled = false } },
            OpenCodeGoWorkspaceId = "synthetic-workspace",
        };

        AccountConfiguration.Migrate(settings);
        var account = Assert.Single(settings.Accounts);
        AccountConfiguration.Migrate(settings);

        Assert.Equal(ProviderAuthenticationMethod.BrowserSession, account.AuthenticationMethod);
        Assert.False(account.Enabled);
        Assert.Equal("synthetic-workspace", account.WorkspaceId);
        Assert.Equal("synthetic-session-value", settings.Providers["OpenCodeGo"].ApiKey);
        Assert.Equal(account, Assert.Single(settings.Accounts));
    }

    [Fact]
    public void Migrate_CopilotSelections_PreservesKnownDisabledAccountsAndCaseInsensitiveIds()
    {
        var settings = new AppSettings { CopilotAccounts = [" Alice ", "alice", " "], CopilotKnownAccounts = ["Alice", "Bob", "bob", " "], Providers = new() { ["Copilot"] = new() } };
        AccountConfiguration.Migrate(settings);
        var alice = settings.Accounts.Single(a => a.ExternalAccountId!.Equals("Alice", StringComparison.OrdinalIgnoreCase));
        var bob = settings.Accounts.Single(a => a.ExternalAccountId == "Bob");
        Assert.True(alice.Enabled);
        Assert.False(bob.Enabled);
        Assert.Equal("copilot:Alice", alice.LegacyCardKey);
        Assert.Equal(ProviderAuthenticationMethod.CommandLine, alice.AuthenticationMethod);
        var another = new AppSettings { CopilotAccounts = ["ALICE"] };
        AccountConfiguration.Migrate(another);
        Assert.Equal(alice.Id, Assert.Single(another.Accounts).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Migrate_CopilotAutodiscovery_RetainsProviderEnabledState(bool enabled)
    {
        var settings = new AppSettings { Providers = new() { ["Copilot"] = new() { Enabled = enabled } } };
        AccountConfiguration.Migrate(settings);
        var account = Assert.Single(settings.Accounts);
        Assert.Equal(enabled, account.Enabled);
        Assert.Null(account.ExternalAccountId);
        Assert.Equal("Copilot", account.LegacyCardKey);
    }

    [Fact]
    public void Migrate_KnownCopilotWithoutSelection_EnablesKnownAccounts()
    {
        var settings = new AppSettings { CopilotKnownAccounts = ["alice", "bob"] };
        AccountConfiguration.Migrate(settings);
        Assert.All(settings.Accounts, account => Assert.True(account.Enabled));
    }

    [Fact]
    public void Migrate_PartiallyMigratedLegacy_DoesNotDuplicateOrOverwriteAccounts()
    {
        var settings = new AppSettings { Providers = new() { ["Claude"] = new() } };
        AccountConfiguration.Migrate(settings);
        var original = Assert.Single(settings.Accounts) with { DisplayLabel = "User label" };
        settings.Accounts = [original];
        settings.AccountConfigurationVersion = 0;
        AccountConfiguration.Migrate(settings);
        Assert.Equal(original, Assert.Single(settings.Accounts));
    }

    [Fact]
    public void Remove_MigratedAccount_DoesNotResurrectFromLegacyFields()
    {
        var settings = new AppSettings { Providers = new() { ["Claude"] = new() } };
        AccountConfiguration.Migrate(settings);
        var id = Assert.Single(settings.Accounts).Id;
        Assert.True(AccountConfiguration.Remove(settings, id));
        Assert.False(AccountConfiguration.Remove(settings, id));
        AccountConfiguration.Migrate(settings);
        Assert.Empty(settings.Accounts);
        Assert.True(settings.Providers.ContainsKey("Claude"));
    }

    [Fact]
    public void Upsert_ProviderChange_RejectsWithoutOverwritingAccount()
    {
        var account = AccountConfiguration.Create(ProviderId.Claude, "Claude");
        var settings = new AppSettings { AccountConfigurationVersion = 1, Accounts = [account] };
        Assert.Throws<ArgumentException>(() => AccountConfiguration.Upsert(settings, account with { ProviderId = ProviderId.Codex }));
        Assert.Equal(account, Assert.Single(settings.Accounts));
    }

    [Fact]
    public void Normalize_InvalidAndDuplicateRecords_Rejects()
    {
        var valid = AccountConfiguration.Create(ProviderId.Claude, " Home ");
        Assert.Equal("Home", valid.DisplayLabel);
        Assert.Throws<ArgumentNullException>(() => AccountConfiguration.Validate(null!));
        Assert.Throws<ArgumentException>(() => AccountConfiguration.Validate(valid with { Id = " " }));
        Assert.Throws<ArgumentException>(() => AccountConfiguration.Validate(valid with { DisplayLabel = " " }));
        Assert.Throws<ArgumentException>(() => AccountConfiguration.Validate(valid with { ProviderId = (ProviderId)999 }));
        Assert.Throws<ArgumentException>(() => AccountConfiguration.Validate(valid with { AuthenticationMethod = (ProviderAuthenticationMethod)999 }));
        Assert.Throws<ArgumentException>(() => AccountConfiguration.Normalize([valid, valid]));
        Assert.Throws<ArgumentException>(() => AccountConfiguration.Remove(new AppSettings(), " "));
        Assert.Throws<ArgumentNullException>(() => AccountConfiguration.Migrate(null!));
        Assert.Empty(AccountConfiguration.Normalize(null));
    }

    [Fact]
    public void Validate_OptionalIdentityAndWorkspace_TrimsWithoutCredentialData()
    {
        var account = AccountConfiguration.Create(ProviderId.OpenCodeGo, "Go") with { ExternalAccountId = " identity ", WorkspaceId = " workspace " };
        var normalized = AccountConfiguration.Validate(account);
        Assert.Equal("identity", normalized.ExternalAccountId);
        Assert.Equal("workspace", normalized.WorkspaceId);
        var serialized = JsonSerializer.Serialize(normalized);
        Assert.Equal(normalized, JsonSerializer.Deserialize<ProviderAccountSettings>(serialized));
    }

    [Fact]
    public void Migrate_FutureSchema_RejectsWithoutChangingLegacyOrAccountData()
    {
        var settings = new AppSettings { AccountConfigurationVersion = 2, Accounts = [AccountConfiguration.Create(ProviderId.Claude, "Future")] };
        var original = settings.Accounts;
        Assert.Throws<InvalidOperationException>(() => AccountConfiguration.Migrate(settings));
        Assert.Same(original, settings.Accounts);
        Assert.Equal(2, settings.AccountConfigurationVersion);
    }

    [Fact]
    public void Load_LegacyFile_DeterministicMigrationDoesNotOverwriteOriginal()
    {
        var path = Path.Combine(this._directory, "settings.json");
        const string original = """{"providers":{"OpenCodeGo":{"enabled":true}},"openCodeGoWorkspaceId":"workspace-123","providerCardOrder":["custom","OpenCodeGo"]}""";
        File.WriteAllText(path, original);
        var first = new SettingsService(NullLogger<SettingsService>.Instance, this._directory).Load();
        var second = new SettingsService(NullLogger<SettingsService>.Instance, this._directory).Load();
        Assert.Equal(first.Accounts, second.Accounts);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(new[] { "custom", "OpenCodeGo" }, first.ProviderCardOrder);
    }

    [Fact]
    public void Save_AccountsRoundTripAndRemoval_PreservesIdsWithoutAliasingOrResurrection()
    {
        var service = new SettingsService(NullLogger<SettingsService>.Instance, this._directory);
        var settings = service.Load();
        var account = AccountConfiguration.Create(ProviderId.Claude, "Second Claude");
        AccountConfiguration.Upsert(settings, account);
        service.Save(settings);
        settings.Accounts.Clear();
        var loaded = service.Load();
        Assert.Contains(loaded.Accounts, a => a.Id == account.Id);
        AccountConfiguration.Remove(loaded, account.Id);
        service.Save(loaded);
        Assert.DoesNotContain(new SettingsService(NullLogger<SettingsService>.Instance, this._directory).Load().Accounts, a => a.Id == account.Id);
    }

    [Fact]
    public void Save_LegacyCaller_PreservesNewAccountRecordsFromDisk()
    {
        var service = new SettingsService(NullLogger<SettingsService>.Instance, this._directory);
        var settings = service.Load();
        var account = AccountConfiguration.Create(ProviderId.Cursor, "Second Cursor");
        AccountConfiguration.Upsert(settings, account);
        service.Save(settings);
        service.Save(new AppSettings());
        Assert.Contains(service.Load().Accounts, a => a.Id == account.Id);
    }

    [Fact]
    public void Save_FailedPersistence_LeavesPreviouslyLoadedAccountsAvailableForRollback()
    {
        var service = new SettingsService(NullLogger<SettingsService>.Instance, this._directory);
        var original = service.Load();
        var draft = service.Load();
        AccountConfiguration.Upsert(draft, AccountConfiguration.Create(ProviderId.Claude, "Unsaved"));
        File.Delete(Path.Combine(this._directory, "settings.json"));
        Directory.CreateDirectory(Path.Combine(this._directory, "settings.json"));
        Directory.CreateDirectory(Path.Combine(this._directory, "codexbar-settings.json"));
        var error = Record.Exception(() => service.Save(draft));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(original.Accounts, service.Load().Accounts);
    }
}
