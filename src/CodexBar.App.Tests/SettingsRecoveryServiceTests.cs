// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.IO;
using CodexBar.App.Services;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class SettingsRecoveryServiceTests
{
    [Fact]
    public void Load_ValidSettings_ForwardsReadsAndWrites()
    {
        var inner = Substitute.For<ISettingsService>();
        var settings = new AppSettings();
        inner.Load().Returns(settings);
        inner.GetApiKey(ProviderId.Claude).Returns("synthetic");
        inner.IsProviderEnabled(ProviderId.Claude).Returns(true);
        inner.GetOpenCodeGoWorkspaceId().Returns("workspace");
        inner.GetCopilotAccounts().Returns(new[] { "synthetic-user" });
        inner.GetSessionBaseline(ProviderId.Claude).Returns(2m);
        inner.GetSessionBaseline("key").Returns(3m);
        var now = DateTimeOffset.UtcNow;
        inner.GetSessionResetTime(ProviderId.Claude).Returns(now);
        inner.GetSessionResetTime("key").Returns(now);
        var recovery = new SettingsRecoveryService(inner, NullLogger<SettingsRecoveryService>.Instance);

        Assert.Same(settings, recovery.Load());
        Assert.False(recovery.IsRecovering);
        Assert.Equal("synthetic", recovery.GetApiKey(ProviderId.Claude));
        Assert.True(recovery.IsProviderEnabled(ProviderId.Claude));
        Assert.Equal("workspace", recovery.GetOpenCodeGoWorkspaceId());
        Assert.Equal(new[] { "synthetic-user" }, recovery.GetCopilotAccounts());
        Assert.Equal(2m, recovery.GetSessionBaseline(ProviderId.Claude));
        Assert.Equal(3m, recovery.GetSessionBaseline("key"));
        Assert.Equal(now, recovery.GetSessionResetTime(ProviderId.Claude));
        Assert.Equal(now, recovery.GetSessionResetTime("key"));
        recovery.Save(settings);
        recovery.SetSessionBaseline(ProviderId.Claude, 4m);
        recovery.SetSessionBaseline("key", 5m);
        inner.Received().Save(settings);
        inner.Received().SetSessionBaseline(ProviderId.Claude, 4m);
        inner.Received().SetSessionBaseline("key", 5m);
    }

    [Fact]
    public void Load_SuccessfulReadClearsSharedFlagBeforeFallbackReturns_KeepsConfigurationDraftReadOnly()
    {
        var inner = Substitute.For<ISettingsService>();
        var reads = 0;
        inner.Load().Returns(_ => ++reads == 1 ? throw new InvalidOperationException("Synthetic integrity failure") : new AppSettings { AccountConfigurationVersion = 1, Accounts = [], Providers = [] });
        SettingsRecoveryService? recovery = null;
        var logger = new CallbackLogger(() => recovery!.Load());
        recovery = new SettingsRecoveryService(inner, logger);
        var closed = false;

        var viewModel = new ProviderConfigurationViewModel(recovery, [], () => closed = true);

        Assert.False(recovery.IsRecovering);
        Assert.Equal(SettingsRecoveryService.RecoveryMessage, viewModel.ErrorMessage);
        viewModel.SaveCommand.Execute(null);
        Assert.False(closed);
        inner.DidNotReceive().Save(Arg.Any<AppSettings>());
    }

    [Fact]
    public void Load_LegacyValidationException_UsesMarkedRecoveryAndSuppressesCredentials()
    {
        var inner = Substitute.For<ISettingsService>();
        inner.Load().Returns(_ => throw new ArgumentException("Invalid synthetic account"));
        inner.GetApiKey(ProviderId.Claude).Returns("synthetic-should-not-be-read");
        var recovery = new SettingsRecoveryService(inner, NullLogger<SettingsRecoveryService>.Instance);

        var fallback = recovery.Load();

        Assert.True(recovery.IsRecoveryDraft(fallback));
        Assert.True(recovery.IsRecovering);
        Assert.Null(recovery.GetApiKey(ProviderId.Claude));
        inner.DidNotReceive().GetApiKey(ProviderId.Claude);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("future")]
    [InlineData("accounts")]
    [InlineData("legacy-accounts")]
    public void Load_InvalidDisk_KeepsRecoveryAvailableWithoutPermittingStaleRecoveryDraftSave(string corruption)
    {
        var directory = Directory.CreateTempSubdirectory("codexbar-read-only-recovery-").FullName;
        try
        {
            var inner = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            var initial = new AppSettings { Providers = new() { ["Claude"] = new() { Enabled = true, ApiKey = "synthetic-original" } } };
            inner.Save(initial);
            var path = Path.Combine(directory, "settings.json");
            var original = File.ReadAllText(path);
            var invalid = corruption == "future" ? "{\"accountConfigurationVersion\":2,\"accounts\":[]}"
                : corruption == "accounts" ? "{\"accountConfigurationVersion\":1,\"accounts\":null}"
                : corruption == "legacy-accounts" ? "{\"accounts\":[{\"id\":\"\",\"providerId\":\"Claude\",\"displayLabel\":\"Malformed\",\"enabled\":false,\"authenticationMethod\":\"Automatic\"}],\"providers\":{\"Claude\":{\"enabled\":true,\"apiKey\":\"synthetic-preserve\"}}}"
                : "{\"accountConfigurationVersion\":1,\"accounts\":[],\"providers\":{\"Claude\":null,\"claude\":{\"enabled\":false,\"apiKey\":\"synthetic-preserve\"}}}";
            File.WriteAllText(path, invalid);
            var strict = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            Assert.Throws<InvalidOperationException>(() => strict.Load());
            var recovery = new SettingsRecoveryService(strict, NullLogger<SettingsRecoveryService>.Instance);
            var fallback = recovery.Load();
            Assert.True(recovery.IsRecovering);
            Assert.Empty(fallback.Accounts);
            Assert.All(fallback.Providers.Values, provider => Assert.False(provider.Enabled));
            Assert.Null(recovery.GetApiKey(ProviderId.Claude));
            Assert.False(recovery.IsProviderEnabled(ProviderId.Claude));
            Assert.Null(recovery.GetOpenCodeGoWorkspaceId());
            Assert.Empty(recovery.GetCopilotAccounts());
            Assert.Null(recovery.GetSessionBaseline(ProviderId.Claude));
            Assert.Null(recovery.GetSessionBaseline("key"));
            Assert.Null(recovery.GetSessionResetTime(ProviderId.Claude));
            Assert.Null(recovery.GetSessionResetTime("key"));
            Assert.Throws<InvalidOperationException>(() => recovery.Save(fallback));
            Assert.Throws<InvalidOperationException>(() => recovery.SetSessionBaseline(ProviderId.Claude, 4m));
            Assert.Throws<InvalidOperationException>(() => recovery.SetSessionBaseline("key", 5m));
            var provider = Substitute.For<IUsageProvider>();
            provider.Metadata.Returns(new ProviderMetadata { Id = ProviderId.Claude, DisplayName = "Claude", Description = "Synthetic" });
            var closed = false;
            var viewModel = new ProviderConfigurationViewModel(recovery, [provider], () => closed = true);
            Assert.Equal(SettingsRecoveryService.RecoveryMessage, viewModel.ErrorMessage);
            viewModel.SaveCommand.Execute(null);
            Assert.False(closed);
            Assert.Equal(invalid, File.ReadAllText(path));
            File.WriteAllText(path, original);
            Assert.NotEmpty(recovery.Load().Accounts);
            Assert.False(recovery.IsRecovering);
            Assert.True(recovery.IsRecoveryDraft(fallback));
            Assert.Throws<InvalidOperationException>(() => recovery.Save(fallback));
            viewModel.SaveCommand.Execute(null);
            Assert.False(closed);
            Assert.Equal(original, File.ReadAllText(path));
            var reopened = new ProviderConfigurationViewModel(recovery, [provider], () => closed = true);
            Assert.Empty(reopened.ErrorMessage);
            reopened.SaveCommand.Execute(null);
            Assert.True(closed);
            Assert.NotEmpty(strict.Load().Accounts);
            Assert.Equal("synthetic-original", strict.GetApiKey(ProviderId.Claude));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CallbackLogger(Action callback) : ILogger<SettingsRecoveryService>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => callback();
    }
}
