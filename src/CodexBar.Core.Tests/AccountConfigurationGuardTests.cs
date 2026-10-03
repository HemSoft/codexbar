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
