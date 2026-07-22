// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class MainViewModelOrderingTests
{
    [Fact]
    public void MoveProviderCard_ReordersCardsAndPersistsOrder()
    {
        var settings = CreateSettingsService();
        using var refresh = CreateRefreshService();
        using var viewModel = new MainViewModel(refresh, settings);
        var expectedOrder = new[] { "cursor", "openrouter", "moonshot" };

        Assert.True(viewModel.MoveProviderCard("cursor", "openrouter", insertAfter: false));

        Assert.Equal("cursor", viewModel.Providers[0].CardKey);
        Assert.Equal(expectedOrder, settings.Load().ProviderCardOrder);
        settings.Received(1).Save(Arg.Is<AppSettings>(saved =>
            saved.ProviderCardOrder.SequenceEqual(expectedOrder)));
    }

    [Fact]
    public void Constructor_AppliesPersistedProviderCardOrder()
    {
        var settings = CreateSettingsService("cursor", "openrouter");
        using var refresh = CreateRefreshService();
        using var viewModel = new MainViewModel(refresh, settings);

        Assert.Equal("cursor", viewModel.Providers[0].CardKey);
    }

    [Fact]
    public void Constructor_WithMoonshotProvider_UsesKimiCompactCard()
    {
        var settings = CreateSettingsService();
        using var refresh = CreateRefreshService();
        using var viewModel = new MainViewModel(refresh, settings);

        var card = Assert.Single(viewModel.Providers, provider => provider.ProviderId == ProviderId.Moonshot);
        Assert.Equal("Moonshot (Kimi)", card.DisplayName);
        Assert.True(card.IsCompactCard);
    }

    private static ISettingsService CreateSettingsService(params string[] initialOrder)
    {
        var settings = Substitute.For<ISettingsService>();
        var appSettings = new AppSettings
        {
            ProviderCardOrder = [.. initialOrder],
        };

        settings.Load().Returns(_ => appSettings);
        settings.IsProviderEnabled(Arg.Any<ProviderId>()).Returns(true);
        settings
            .When(x => x.Save(Arg.Any<AppSettings>()))
            .Do(callInfo => appSettings = callInfo.Arg<AppSettings>());

        return settings;
    }

    private static UsageRefreshService CreateRefreshService() =>
        new([], NullLogger<UsageRefreshService>.Instance);
}
