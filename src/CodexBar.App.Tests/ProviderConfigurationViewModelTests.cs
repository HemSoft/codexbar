// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using NSubstitute;

public sealed class ProviderConfigurationViewModelTests
{
    [Fact]
    public void Constructor_WhenMoonshotSettingsAreMissing_ShowsProviderDisabled()
    {
        var settings = CreateSettingsService(new AppSettings());

        var viewModel = new ProviderConfigurationViewModel(
            settings,
            [new TestUsageProvider(ProviderId.Moonshot)],
            () => { });

        Assert.False(Assert.Single(viewModel.Providers).IsDisplayed);
    }

    [Fact]
    public void Constructor_WhenCopilotAccountsAreDiscovered_SelectsAllAccounts()
    {
        var settings = CreateSettingsService(new AppSettings());
        var viewModel = CreateViewModel(settings);

        Assert.True(viewModel.HasCopilotAccounts);
        Assert.Collection(
            viewModel.CopilotAccounts.OrderBy(account => account.Username),
            account =>
            {
                Assert.Equal("fhemmerrelias", account.Username);
                Assert.Equal("Copilot Work (fhemmerrelias)", account.DisplayName);
                Assert.True(account.IsEnabled);
            },
            account =>
            {
                Assert.Equal("HemSoft", account.Username);
                Assert.Equal("Copilot Home (HemSoft)", account.DisplayName);
                Assert.True(account.IsEnabled);
            });
    }

    [Fact]
    public void Save_WhenCopilotHomeIsUnchecked_PersistsWorkAccountOnly()
    {
        AppSettings? savedSettings = null;
        var settings = CreateSettingsService(new AppSettings(), saved => savedSettings = saved);
        var viewModel = CreateViewModel(settings);
        viewModel.CopilotAccounts.Single(account => account.Username == "HemSoft").IsEnabled = false;

        viewModel.SaveCommand.Execute(null);

        Assert.NotNull(savedSettings);
        Assert.Equal(["fhemmerrelias"], savedSettings.CopilotAccounts);
        Assert.Equal(["fhemmerrelias", "HemSoft"], savedSettings.CopilotKnownAccounts);
        Assert.True(savedSettings.Providers[ProviderId.Copilot.ToString()].Enabled);
    }

    [Fact]
    public void Save_WhenAllCopilotAccountsAreUnchecked_DisablesCopilotProvider()
    {
        AppSettings? savedSettings = null;
        var settings = CreateSettingsService(new AppSettings(), saved => savedSettings = saved);
        var viewModel = CreateViewModel(settings);
        foreach (var account in viewModel.CopilotAccounts)
        {
            account.IsEnabled = false;
        }

        viewModel.SaveCommand.Execute(null);

        Assert.NotNull(savedSettings);
        Assert.Empty(savedSettings.CopilotAccounts);
        Assert.Equal(["fhemmerrelias", "HemSoft"], savedSettings.CopilotKnownAccounts);
        Assert.False(savedSettings.Providers[ProviderId.Copilot.ToString()].Enabled);
    }

    [Fact]
    public void Constructor_WhenCopilotAccountsAreConfigured_SelectsOnlyConfiguredAccounts()
    {
        var settings = CreateSettingsService(new AppSettings
        {
            CopilotAccounts = ["fhemmerrelias"],
        });

        var viewModel = CreateViewModel(settings);

        Assert.True(viewModel.CopilotAccounts.Single(account => account.Username == "fhemmerrelias").IsEnabled);
        Assert.False(viewModel.CopilotAccounts.Single(account => account.Username == "HemSoft").IsEnabled);
    }

    [Fact]
    public void Constructor_WhenKnownCopilotAccountIsNoLongerFetched_StillShowsAccount()
    {
        var settings = CreateSettingsService(new AppSettings
        {
            CopilotAccounts = ["fhemmerrelias"],
            CopilotKnownAccounts = ["fhemmerrelias", "HemSoft"],
        });
        var cards = new[]
        {
            new ProviderCardViewModel
            {
                ProviderId = ProviderId.Copilot,
                CardKey = "copilot:fhemmerrelias",
                DisplayName = "fhemmerrelias",
            },
        };

        var viewModel = new ProviderConfigurationViewModel(settings, [new TestUsageProvider(ProviderId.Copilot)], () => { }, cards);

        Assert.True(viewModel.CopilotAccounts.Single(account => account.Username == "fhemmerrelias").IsEnabled);
        Assert.False(viewModel.CopilotAccounts.Single(account => account.Username == "HemSoft").IsEnabled);
    }

    private static ProviderConfigurationViewModel CreateViewModel(ISettingsService settings)
    {
        var cards = new[]
        {
            new ProviderCardViewModel
            {
                ProviderId = ProviderId.Copilot,
                CardKey = "copilot:fhemmerrelias",
                DisplayName = "fhemmerrelias",
            },
            new ProviderCardViewModel
            {
                ProviderId = ProviderId.Copilot,
                CardKey = "copilot:HemSoft",
                DisplayName = "HemSoft",
            },
        };

        return new ProviderConfigurationViewModel(settings, [new TestUsageProvider(ProviderId.Copilot)], () => { }, cards);
    }

    private static ISettingsService CreateSettingsService(AppSettings appSettings, Action<AppSettings>? onSave = null)
    {
        appSettings.Providers[ProviderId.Copilot.ToString()] = new ProviderSettings { Enabled = true };

        var settings = Substitute.For<ISettingsService>();
        settings.Load().Returns(_ => appSettings);
        settings
            .When(service => service.Save(Arg.Any<AppSettings>()))
            .Do(callInfo =>
            {
                appSettings = callInfo.Arg<AppSettings>();
                onSave?.Invoke(appSettings);
            });

        return settings;
    }

    private sealed class TestUsageProvider(ProviderId providerId) : IUsageProvider
    {
        public ProviderMetadata Metadata { get; } = new()
        {
            Id = providerId,
            DisplayName = providerId.ToString(),
            Description = providerId.ToString(),
        };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProviderUsageResult { Provider = providerId, Success = true });
    }
}
