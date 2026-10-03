// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Services;

using System.Runtime.CompilerServices;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>Keeps the tray and read-only recovery UI available without relaxing disk-write guards.</summary>
internal sealed class SettingsRecoveryService(ISettingsService settings, ILogger<SettingsRecoveryService> logger) : ISettingsService
{
    internal const string RecoveryMessage = "Settings could not be read safely. Existing settings were not changed. Repair the settings file or use a compatible CodexBar, then close and reopen Configure. This recovery draft cannot be saved.";

    private readonly ConditionalWeakTable<AppSettings, object> _recoveryDrafts = new();

    internal bool IsRecovering { get; private set; }

    public AppSettings Load()
    {
        var value = this.Read(settings.Load, new AppSettings
        {
            AccountConfigurationVersion = AccountConfiguration.CurrentVersion,
            Accounts = [],
            Providers = Enum.GetValues<ProviderId>().ToDictionary(id => id.ToString(), _ => new ProviderSettings { Enabled = false }),
        });
        if (this.IsRecovering)
        {
            this._recoveryDrafts.Add(value, new object());
        }

        return value;
    }

    public void Save(AppSettings value)
    {
        if (this._recoveryDrafts.TryGetValue(value, out _))
        {
            throw new InvalidOperationException(RecoveryMessage);
        }

        settings.Save(value);
    }

    public string? GetApiKey(ProviderId id) => this.Read(() => settings.GetApiKey(id), (string?)null);

    public bool IsProviderEnabled(ProviderId id) => this.Read(() => settings.IsProviderEnabled(id), false);

    public string? GetOpenCodeGoWorkspaceId() => this.Read(settings.GetOpenCodeGoWorkspaceId, (string?)null);

    public IReadOnlyList<string> GetCopilotAccounts() => this.Read(settings.GetCopilotAccounts, Array.Empty<string>());

    public decimal? GetSessionBaseline(ProviderId id) => this.Read(() => settings.GetSessionBaseline(id), (decimal?)null);

    public decimal? GetSessionBaseline(string key) => this.Read(() => settings.GetSessionBaseline(key), (decimal?)null);

    public DateTimeOffset? GetSessionResetTime(ProviderId id) => this.Read(() => settings.GetSessionResetTime(id), (DateTimeOffset?)null);

    public DateTimeOffset? GetSessionResetTime(string key) => this.Read(() => settings.GetSessionResetTime(key), (DateTimeOffset?)null);

    public void SetSessionBaseline(ProviderId id, decimal balance) => settings.SetSessionBaseline(id, balance);

    public void SetSessionBaseline(string key, decimal balance) => settings.SetSessionBaseline(key, balance);

    private T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            var value = read();
            this.IsRecovering = false;
            return value;
        }
        catch (InvalidOperationException error)
        {
            this.IsRecovering = true;
            logger.LogWarning(error, "Settings integrity check failed; only read-only recovery is available");
            return fallback;
        }
    }
}
