// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.ComponentModel;
using System.IO;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

public sealed class AccountConfigurationViewModelTests
{
    [Theory]
    [InlineData(ProviderId.OpenRouter)]
    [InlineData(ProviderId.Copilot)]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.Codex)]
    [InlineData(ProviderId.Cursor)]
    [InlineData(ProviderId.OpenCodeGo)]
    [InlineData(ProviderId.OpenCodeZen)]
    [InlineData(ProviderId.Moonshot)]
    public void AddAccount_EveryProvider_CreatesMultipleIndependentStableRecords(ProviderId id)
    {
        var fixture = new Fixture();
        fixture.ViewModel.NewAccountProvider = id;
        fixture.ViewModel.AddAccountCommand.Execute(null);
        fixture.ViewModel.AddAccountCommand.Execute(null);
        var accounts = fixture.ViewModel.Accounts.Where(account => account.ProviderId == id).ToList();
        Assert.Equal(3, accounts.Count);
        Assert.Equal(3, accounts.Select(account => account.Id).Distinct().Count());
        fixture.ViewModel.SaveCommand.Execute(null);
        Assert.Equal(accounts.Select(account => account.Id), fixture.Saved!.Accounts.Where(account => account.ProviderId == id).Select(account => account.Id));
        Assert.Equal(1, fixture.Closed);
    }

    [Fact]
    public void Save_RenameDisableAndChangeAuthentication_PreservesIdentityAndLegacySettings()
    {
        var fixture = new Fixture();
        var account = fixture.ViewModel.Accounts.Single(option => option.ProviderId == ProviderId.Claude);
        var id = account.Id;
        account.DisplayLabel = "  Research  ";
        account.Enabled = false;
        account.AuthenticationMethod = ProviderAuthenticationMethod.BrowserSession;
        fixture.ViewModel.SaveCommand.Execute(null);
        var saved = fixture.Saved!.Accounts.Single(option => option.Id == id);
        Assert.Equal("Research", saved.DisplayLabel);
        Assert.False(saved.Enabled);
        Assert.Equal(ProviderAuthenticationMethod.BrowserSession, saved.AuthenticationMethod);
        Assert.False(fixture.Saved.Providers["Claude"].Enabled);
        Assert.Equal("legacy-key", fixture.Saved.Providers["Claude"].ApiKey);
        Assert.Equal(new[] { "Cursor", "Claude" }, fixture.Saved.ProviderCardOrder);
        Assert.Equal("workspace", fixture.Saved.OpenCodeGoWorkspaceId);
    }

    [Fact]
    public void Cancel_AddEditAndRemove_LeavesLoadedSettingsUntouched()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Accounts[0].DisplayLabel = "changed";
        fixture.ViewModel.RemoveAccountCommand.Execute(fixture.ViewModel.Accounts[1]);
        fixture.ViewModel.AddAccountCommand.Execute(null);
        fixture.ViewModel.CancelCommand.Execute(null);
        fixture.Service.DidNotReceive().Save(Arg.Any<AppSettings>());
        Assert.Empty(fixture.Original.Accounts);
        Assert.Equal(0, fixture.Original.AccountConfigurationVersion);
        Assert.Equal("legacy-key", fixture.Original.Providers["Claude"].ApiKey);
        Assert.Equal(1, fixture.Closed);
    }

    [Fact]
    public void RemoveAccount_SaveAndReopen_DoesNotResurrectLegacyRecord()
    {
        var fixture = new Fixture();
        var account = fixture.ViewModel.Accounts.Single(option => option.ProviderId == ProviderId.Cursor);
        fixture.ViewModel.RemoveAccountCommand.Execute(account);
        fixture.ViewModel.RemoveAccountCommand.Execute(null);
        fixture.ViewModel.SaveCommand.Execute(null);
        fixture.Service.Load().Returns(fixture.Saved!);
        var reopened = new ProviderConfigurationViewModel(fixture.Service, fixture.Providers, () => { });
        Assert.DoesNotContain(reopened.Accounts, option => option.Id == account.Id);
        Assert.False(fixture.Saved!.Providers["Cursor"].Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_PersistenceFailure_KeepsDraftOpenAndAllowsRetry(bool unauthorized)
    {
        var fixture = new Fixture();
        var fail = true;
        fixture.Service.When(service => service.Save(Arg.Any<AppSettings>())).Do(_ =>
        {
            if (fail)
            {
                throw unauthorized ? new UnauthorizedAccessException("sensitive path") : new IOException("sensitive path");
            }
        });
        fixture.ViewModel.Accounts[0].DisplayLabel = "Retry me";
        var savedEvents = 0;
        fixture.ViewModel.Saved += (_, _) => savedEvents++;
        var errors = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => errors.Add(args.PropertyName);
        fixture.ViewModel.SaveCommand.Execute(null);
        Assert.Equal(0, fixture.Closed);
        Assert.Equal(0, savedEvents);
        Assert.Contains("try again", fixture.ViewModel.ErrorMessage);
        Assert.DoesNotContain("sensitive", fixture.ViewModel.ErrorMessage);
        Assert.Equal("Retry me", fixture.ViewModel.Accounts[0].DisplayLabel);
        Assert.Contains(nameof(ProviderConfigurationViewModel.ErrorMessage), errors);
        fixture.Service.ClearReceivedCalls();
        fail = false;
        fixture.ViewModel.SaveCommand.Execute(null);
        Assert.Empty(fixture.ViewModel.ErrorMessage);
        Assert.Equal(1, fixture.Closed);
        Assert.Equal(1, savedEvents);
        Assert.Equal("Retry me", fixture.Saved!.Accounts[0].DisplayLabel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_AnotherInstanceEditedAccounts_PreservesDiskAndDraftEvenAfterBackgroundRefresh(bool advanceCache)
    {
        var directory = Directory.CreateTempSubdirectory("codexbar-account-conflict-").FullName;
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            var fixture = new Fixture();
            var closed = 0;
            var viewModel = new ProviderConfigurationViewModel(service, fixture.Providers, () => closed++);
            var account = viewModel.Accounts.First();
            account.DisplayLabel = "My draft";
            var editor = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            var edited = editor.Load();
            AccountConfiguration.Upsert(edited, edited.Accounts.First() with { DisplayLabel = "Concurrent label" });
            editor.Save(edited);
            if (advanceCache)
            {
                service.SetSessionBaseline(ProviderId.Claude, 3m);
            }

            var path = Path.Combine(directory, "settings.json");
            var before = File.ReadAllText(path);
            viewModel.SaveCommand.Execute(null);

            Assert.Equal(0, closed);
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal("My draft", account.DisplayLabel);
            Assert.Contains("changed in another process", viewModel.ErrorMessage);
            Assert.Contains("not overwritten", viewModel.ErrorMessage);
            viewModel.SaveCommand.Execute(null);
            Assert.Equal(0, closed);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_VersionedAccountsAndProviderEntriesRemoved_DoesNotRecreateRecordsOrDisableLegacyDefaults(bool removeAll)
    {
        var directory = Directory.CreateTempSubdirectory("codexbar-account-deletion-").FullName;
        try
        {
            var initial = new SettingsService(NullLogger<SettingsService>.Instance, directory).Load();
            initial.Accounts.RemoveAll(account => removeAll || account.ProviderId == ProviderId.Claude);
            foreach (var key in initial.Providers.Keys.Where(key => removeAll || key == "Claude").ToList())
            {
                initial.Providers.Remove(key);
            }

            File.WriteAllText(Path.Combine(directory, "settings.json"), System.Text.Json.JsonSerializer.Serialize(initial, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            var service = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            var fixture = new Fixture();
            var closed = 0;
            var viewModel = new ProviderConfigurationViewModel(service, fixture.Providers, () => closed++);
            Assert.DoesNotContain(viewModel.Accounts, account => account.ProviderId == ProviderId.Claude);
            if (removeAll)
            {
                Assert.Empty(viewModel.Accounts);
            }

            viewModel.SaveCommand.Execute(null);
            Assert.Equal(1, closed);
            Assert.Equal(initial.Accounts, service.Load().Accounts);
            Assert.True(service.IsProviderEnabled(ProviderId.Claude));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_AddThenRemoveFirstAccount_DisablesProviderWithoutRecreatingRecord()
    {
        var fixture = new Fixture();
        fixture.Service.Load().Returns(new AppSettings { AccountConfigurationVersion = 1 });
        var viewModel = new ProviderConfigurationViewModel(fixture.Service, fixture.Providers, () => { });
        viewModel.NewAccountProvider = ProviderId.Claude;
        viewModel.AddAccountCommand.Execute(null);
        viewModel.RemoveAccountCommand.Execute(viewModel.Accounts.Single());
        viewModel.SaveCommand.Execute(null);
        Assert.Empty(fixture.Saved!.Accounts);
        Assert.False(fixture.Saved.Providers["Claude"].Enabled);
    }

    [Fact]
    public void Save_BlankLabel_ReportsValidationWithoutPersisting()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Accounts[0].DisplayLabel = " ";
        fixture.ViewModel.SaveCommand.Execute(null);
        Assert.Contains("display label", fixture.ViewModel.ErrorMessage);
        fixture.Service.DidNotReceive().Save(Arg.Any<AppSettings>());
        Assert.Equal(0, fixture.Closed);
    }

    [Fact]
    public void Save_UnsupportedFutureVersion_RefusesOverwrite()
    {
        var fixture = new Fixture();
        fixture.Service.Load().Returns(new AppSettings { AccountConfigurationVersion = 2 });
        fixture.ViewModel.SaveCommand.Execute(null);
        fixture.Service.DidNotReceive().Save(Arg.Any<AppSettings>());
        Assert.Contains("not overwritten", fixture.ViewModel.ErrorMessage);
        Assert.Contains("newer", fixture.ViewModel.ErrorMessage);
        Assert.DoesNotContain("file access", fixture.ViewModel.ErrorMessage);
        Assert.Equal(0, fixture.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_SchemaIntegrityFailure_PreservesDraftAndExplainsNonOverwrite(bool failOnSave)
    {
        var fixture = new Fixture();
        var account = fixture.ViewModel.Accounts[0];
        var id = account.Id;
        account.DisplayLabel = "Preserve my draft";
        var savedEvents = 0;
        fixture.ViewModel.Saved += (_, _) => savedEvents++;
        if (failOnSave)
        {
            fixture.Service.When(service => service.Save(Arg.Any<AppSettings>())).Do(_ => throw new InvalidOperationException("sensitive schema details"));
        }
        else
        {
            fixture.Service.Load().Throws(new InvalidOperationException("sensitive schema details"));
        }

        fixture.ViewModel.SaveCommand.Execute(null);

        Assert.Equal(0, fixture.Closed);
        Assert.Equal(0, savedEvents);
        Assert.Equal(id, fixture.ViewModel.Accounts[0].Id);
        Assert.Equal("Preserve my draft", fixture.ViewModel.Accounts[0].DisplayLabel);
        Assert.Contains("not overwritten", fixture.ViewModel.ErrorMessage);
        Assert.Contains("newer or unreadable", fixture.ViewModel.ErrorMessage);
        Assert.DoesNotContain("file access", fixture.ViewModel.ErrorMessage);
        Assert.DoesNotContain("sensitive", fixture.ViewModel.ErrorMessage);
    }

    [Fact]
    public void Save_CopilotIdentityAndWorkspace_UpdatesCompatibilityFields()
    {
        var fixture = new Fixture();
        var copilot = fixture.ViewModel.Accounts.Single(option => option.IsCopilot);
        copilot.ExternalAccountId = "  octocat  ";
        var workspace = fixture.ViewModel.Accounts.Single(option => option.IsOpenCodeGo);
        workspace.WorkspaceId = "  updated-workspace  ";
        fixture.ViewModel.SaveCommand.Execute(null);
        Assert.Equal(new[] { "octocat" }, fixture.Saved!.CopilotAccounts);
        Assert.Equal(new[] { "octocat" }, fixture.Saved.CopilotKnownAccounts);
        Assert.Equal("updated-workspace", fixture.Saved.OpenCodeGoWorkspaceId);
    }

    [Fact]
    public void AccountOption_Setters_NotifyOnlyChangedValuesAndPreserveUneditedMetadata()
    {
        var record = AccountConfiguration.Create(ProviderId.OpenCodeGo, "first") with { LegacyCardKey = "legacy", ExternalAccountId = "external", WorkspaceId = "workspace" };
        var option = new AccountOptionViewModel(record);
        var notifications = new List<string?>();
        option.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        option.DisplayLabel = option.DisplayLabel;
        option.Enabled = option.Enabled;
        option.AuthenticationMethod = option.AuthenticationMethod;
        option.ExternalAccountId = option.ExternalAccountId;
        option.WorkspaceId = option.WorkspaceId;
        Assert.Empty(notifications);
        option.DisplayLabel = "second";
        option.Enabled = false;
        option.AuthenticationMethod = ProviderAuthenticationMethod.OAuth;
        option.ExternalAccountId = "new-external";
        option.WorkspaceId = "new-workspace";
        Assert.Equal(5, notifications.Count);
        Assert.Equal(ProviderId.OpenCodeGo, option.ProviderId);
        Assert.Equal("OpenCodeGo", option.ProviderName);
        Assert.True(option.IsOpenCodeGo);
        Assert.False(option.IsCopilot);
        Assert.Equal(5, option.AuthenticationMethods.Count);
        Assert.Equal("Browser session", option.AuthenticationMethods.Last().Label);
        Assert.Equal(ProviderAuthenticationMethod.Automatic, option.AuthenticationMethods.First().Method);
        Assert.Equal("legacy", option.ToSettings().LegacyCardKey);
        Assert.Equal(record.Id, option.Id);
        Assert.Equal("new-workspace", option.ToSettings().WorkspaceId);
        var unobserved = new AccountOptionViewModel(AccountConfiguration.Create(ProviderId.Copilot, "CLI"));
        unobserved.DisplayLabel = "new";
        Assert.True(unobserved.IsCopilot);
        Assert.False(unobserved.IsOpenCodeGo);
    }

    [Fact]
    public void Save_ClearOrDeleteCopilotIdentities_ClearsCompatibilitySelection()
    {
        var fixture = new Fixture();
        fixture.Original.CopilotAccounts = ["octocat"];
        fixture.Original.CopilotKnownAccounts = ["octocat"];
        var vm = new ProviderConfigurationViewModel(fixture.Service, fixture.Providers, () => { });
        var account = vm.Accounts.Single(option => option.IsCopilot);
        account.ExternalAccountId = null;
        vm.SaveCommand.Execute(null);
        Assert.Empty(fixture.Saved!.CopilotAccounts);
        Assert.Empty(fixture.Saved.CopilotKnownAccounts);
        vm.RemoveAccountCommand.Execute(account);
        vm.SaveCommand.Execute(null);
        Assert.Empty(fixture.Saved.CopilotAccounts);
        Assert.False(fixture.Saved.Providers["Copilot"].Enabled);
    }

    [Fact]
    public void Save_ReplaceOpenCodeGoAccount_UsesNewWorkspaceThenClearsRemovedWorkspace()
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.RemoveAccountCommand.Execute(vm.Accounts.Single(option => option.IsOpenCodeGo));
        vm.NewAccountProvider = ProviderId.OpenCodeGo;
        vm.AddAccountCommand.Execute(null);
        vm.Accounts[0].WorkspaceId = "replacement";
        vm.SaveCommand.Execute(null);
        Assert.Equal("replacement", fixture.Saved!.OpenCodeGoWorkspaceId);
        vm.RemoveAccountCommand.Execute(vm.Accounts[0]);
        vm.SaveCommand.Execute(null);
        Assert.Null(fixture.Saved.OpenCodeGoWorkspaceId);
        Assert.False(fixture.Saved.Providers["OpenCodeGo"].Enabled);
    }

    [Fact]
    public void Save_PartialLegacyProviderSettings_PreservesEffectiveDefaults()
    {
        var fixture = new Fixture();
        fixture.Original.Providers = new() { ["Claude"] = new() { Enabled = false } };
        var vm = new ProviderConfigurationViewModel(fixture.Service, fixture.Providers, () => { });
        vm.SaveCommand.Execute(null);
        Assert.Equal(8, fixture.Saved!.Accounts.Count);
        Assert.True(fixture.Saved.Providers["Codex"].Enabled);
        Assert.False(fixture.Saved.Providers["Claude"].Enabled);
        Assert.False(fixture.Saved.Providers["Moonshot"].Enabled);
    }

    [Fact]
    public void AccountOption_MetadataDisplayName_UsesProviderName()
    {
        var option = new AccountOptionViewModel(AccountConfiguration.Create(ProviderId.Codex, "Research"), "ChatGPT Codex");
        Assert.Equal("ChatGPT Codex", option.ProviderName);
    }

    private sealed class Fixture
    {
        public AppSettings Original { get; } = new()
        {
            Providers = Enum.GetValues<ProviderId>().ToDictionary(id => id.ToString(), _ => new ProviderSettings { Enabled = true, ApiKey = "legacy-key" }),
            ProviderCardOrder = ["Cursor", "Claude"],
            OpenCodeGoWorkspaceId = "workspace",
        };

        public ISettingsService Service { get; } = Substitute.For<ISettingsService>();

        public List<IUsageProvider> Providers { get; } = [];

        public ProviderConfigurationViewModel ViewModel { get; }

        public AppSettings? Saved { get; private set; }

        public int Closed { get; private set; }

        public Fixture()
        {
            this.Service.Load().Returns(this.Original);
            this.Service.When(service => service.Save(Arg.Any<AppSettings>())).Do(call => this.Saved = call.Arg<AppSettings>());
            foreach (var id in Enum.GetValues<ProviderId>())
            {
                var provider = Substitute.For<IUsageProvider>();
                provider.Metadata.Returns(new ProviderMetadata { Id = id, DisplayName = id.ToString(), Description = "Test provider" });
                this.Providers.Add(provider);
            }

            this.ViewModel = new ProviderConfigurationViewModel(this.Service, this.Providers, () => this.Closed++);
        }
    }
}
