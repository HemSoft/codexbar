// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.Globalization;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class LocalPresentationTests
{
    [Theory]
    [InlineData(WindowMessageHandler.WmTimeChange)]
    [InlineData(WindowMessageHandler.WmSettingChange)]
    public void HandleMessage_SystemTimeOrSettingsChange_RequestsLocalPresentationRefresh(int message)
    {
        var handler = new WindowMessageHandler();
        Assert.Equal(MessageAction.RefreshLocalPresentation, handler.HandleMessage(message, 0));
        Assert.False(handler.IsDragging);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyPrimaryOrFallbackUsage_SourceInstant_RetainsAndClearsForPresentation(bool primary)
    {
        var instant = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        var card = new ProviderCardViewModel();
        var usage = new UsageSnapshot { UsedPercent = 0.5, ResetsAt = instant, ResetDescription = "Old provider formatting" };
        var item = new UsageItem { Key = "local", DisplayName = "Local", PrimaryUsage = primary ? usage : null, SecondaryUsage = primary ? null : usage };
        ItemCardReconciler.ApplyPrimaryOrFallbackUsage(card, item);
        Assert.Equal(instant, card.ResetAt);
        Assert.Equal($"Resets {LocalTimestampFormatter.Format(instant)}", card.ResetText);
        ItemCardReconciler.ApplyPrimaryOrFallbackUsage(card, new UsageItem { Key = "local", DisplayName = "Local" });
        Assert.Null(card.ResetAt);
        card.ResetAt = instant;
        ItemCardReconciler.ApplyItemError(card, "Synthetic error");
        Assert.Null(card.ResetAt);
        card.ResetAt = instant;
        ItemCardReconciler.ResetCardToError(card, "Synthetic error");
        Assert.Null(card.ResetAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyLegacyProviderResult_ErrorOrEmpty_ClearsRetainedResetInstant(bool success)
    {
        var card = new ProviderCardViewModel { ResetAt = DateTimeOffset.UtcNow, ResetText = "Old reset" };
        MainViewModel.ApplyLegacyProviderResult(card, new ProviderUsageResult { Provider = ProviderId.OpenRouter, Success = success });
        Assert.Null(card.ResetAt);
        Assert.Null(card.ResetText);
    }

    [Fact]
    public void RefreshLocalPresentation_LegacyCreditsAndDynamicOverage_UsesPersistedSessionKeys()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Load().Returns(new AppSettings());
        settings.IsProviderEnabled(Arg.Any<ProviderId>()).Returns(true);
        var legacyTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var itemTime = legacyTime.AddDays(1);
        settings.GetSessionResetTime(ProviderId.OpenRouter).Returns(legacyTime);
        settings.GetSessionResetTime("copilot:octocat").Returns(itemTime);
        using var refresh = new UsageRefreshService([], NullLogger<UsageRefreshService>.Instance);
        using var vm = new MainViewModel(refresh, settings);
        vm.Providers.Clear();
        var legacy = new ProviderCardViewModel { ProviderId = ProviderId.OpenRouter, CardKey = "openrouter", CreditsBalance = 5m };
        var dynamic = new ProviderCardViewModel { ProviderId = ProviderId.Copilot, CardKey = "Copilot:Octocat", OverageCost = 1m };
        vm.Providers.Add(legacy);
        vm.Providers.Add(dynamic);
        vm.RefreshLocalPresentation();
        Assert.Equal(SessionSpendingCalculator.FormatResetTime(legacyTime), legacy.SessionResetTime);
        Assert.Equal(SessionSpendingCalculator.FormatResetTime(itemTime), dynamic.SessionResetTime);
    }

    [Fact]
    public void RefreshLocalPresentation_CultureChanges_RefreshesExistingInstantsWithoutProviderFetch()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var settings = Substitute.For<ISettingsService>();
            settings.Load().Returns(new AppSettings());
            settings.IsProviderEnabled(Arg.Any<ProviderId>()).Returns(true);
            var instant = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);
            settings.GetSessionResetTime(Arg.Any<string>()).Returns(instant);
            var provider = Substitute.For<IUsageProvider>();
            provider.Metadata.Returns(new ProviderMetadata { Id = ProviderId.Codex, DisplayName = "Synthetic Codex", Description = "Fetch recording substitute" });
            using var refresh = new UsageRefreshService([provider], NullLogger<UsageRefreshService>.Instance);
            using var vm = new MainViewModel(refresh, settings);
            var card = vm.Providers.First();
            card.ResetAt = instant;
            var bar = new UsageBarViewModel { ResetsAt = instant };
            card.Bars.Add(bar);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            vm.RefreshLocalPresentation();
            var before = card.ResetText;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            vm.RefreshLocalPresentation();
            Assert.NotEqual(before, card.ResetText);
            Assert.Equal($"Resets {LocalTimestampFormatter.Format(instant)}", card.ResetText);
            Assert.Equal($"Resets {LocalTimestampFormatter.Format(instant)}", bar.ResetDescription);
            Assert.Equal(LocalTimestampFormatter.Format(instant), card.SessionResetTime);
            Assert.Equal(instant, bar.ResetsAt);
            Assert.DoesNotContain(provider.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IUsageProvider.FetchUsageAsync));
            settings.DidNotReceive().Save(Arg.Any<AppSettings>());
            vm.Dispose();
            card.ResetText = "disposed";
            vm.RefreshLocalPresentation();
            Assert.Equal("disposed", card.ResetText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
