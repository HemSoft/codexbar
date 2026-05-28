// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class MainViewModelOrderingTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-order-test-{Guid.NewGuid():N}");

    public MainViewModelOrderingTests()
    {
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void MoveProviderCard_ReordersCardsAndPersistsOrder()
    {
        var settings = this.CreateSettingsService();
        using var refresh = CreateRefreshService();
        using var viewModel = new MainViewModel(refresh, settings);

        Assert.True(viewModel.MoveProviderCard("cursor", "openrouter", insertAfter: false));

        Assert.Equal("cursor", viewModel.Providers[0].CardKey);
        Assert.Equal("cursor", settings.Load().ProviderCardOrder[0]);
    }

    [Fact]
    public void Constructor_AppliesPersistedProviderCardOrder()
    {
        var settings = this.CreateSettingsService();
        var stored = settings.Load();
        stored.ProviderCardOrder = ["cursor", "openrouter"];
        settings.Save(stored);

        using var refresh = CreateRefreshService();
        using var viewModel = new MainViewModel(refresh, settings);

        Assert.Equal("cursor", viewModel.Providers[0].CardKey);
    }

    private SettingsService CreateSettingsService() =>
        new(NullLogger<SettingsService>.Instance, this.tempDir);

    private static UsageRefreshService CreateRefreshService() =>
        new([], NullLogger<UsageRefreshService>.Instance);
}
