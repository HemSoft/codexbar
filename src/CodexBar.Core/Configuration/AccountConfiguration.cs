// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Configuration;

using System.Security.Cryptography;
using System.Text;
using CodexBar.Core.Models;

/// <summary>Owns lossless legacy migration and account configuration invariants.</summary>
public static class AccountConfiguration
{
    public const int CurrentVersion = 1;

    /// <summary>Migrates only account fields; all legacy fields remain available for rollback.</summary>
    public static void Migrate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AccountConfigurationVersion > CurrentVersion)
        {
            throw new InvalidOperationException("Account configuration was written by a newer application. Do not overwrite it.");
        }

        var accounts = Normalize(settings.Accounts);
        if (settings.AccountConfigurationVersion == CurrentVersion)
        {
            settings.Accounts = accounts;
            return;
        }

        foreach (var pair in settings.Providers ?? [])
        {
            if (!Enum.TryParse<ProviderId>(pair.Key, ignoreCase: true, out var provider) || !Enum.IsDefined(provider))
            {
                continue;
            }

            if (provider == ProviderId.Copilot)
            {
                continue;
            }

            var legacy = pair.Value ?? new ProviderSettings();
            var account = new ProviderAccountSettings
            {
                Id = LegacyId(provider, string.Empty),
                ProviderId = provider,
                DisplayLabel = provider.ToString(),
                Enabled = legacy.Enabled,
                AuthenticationMethod = string.IsNullOrWhiteSpace(legacy.ApiKey) ? ProviderAuthenticationMethod.Automatic
                    : provider == ProviderId.OpenCodeGo ? ProviderAuthenticationMethod.BrowserSession : ProviderAuthenticationMethod.ApiKey,
                WorkspaceId = provider == ProviderId.OpenCodeGo ? settings.OpenCodeGoWorkspaceId : null,
                LegacyCardKey = provider.ToString(),
            };
            AddMigratedAccount(accounts, account);
        }

        MigrateCopilot(settings, accounts);
        settings.Accounts = accounts;
        settings.AccountConfigurationVersion = CurrentVersion;
    }

    public static ProviderAccountSettings Create(ProviderId provider, string label, ProviderAuthenticationMethod method = ProviderAuthenticationMethod.Automatic) =>
        Validate(new ProviderAccountSettings { Id = Guid.NewGuid().ToString("N"), ProviderId = provider, DisplayLabel = label, AuthenticationMethod = method });

    public static void Upsert(AppSettings settings, ProviderAccountSettings account)
    {
        Migrate(settings);
        var normalized = Validate(account);
        var index = settings.Accounts.FindIndex(existing => existing.Id == normalized.Id);
        if (index < 0)
        {
            settings.Accounts.Add(normalized);
            return;
        }

        if (settings.Accounts[index].ProviderId != normalized.ProviderId)
        {
            throw new ArgumentException("An account cannot change provider. Add a new account instead.", nameof(account));
        }

        settings.Accounts[index] = normalized;
    }

    public static bool Remove(AppSettings settings, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Migrate(settings);
        return settings.Accounts.RemoveAll(account => account.Id == id) > 0;
    }

    public static List<ProviderAccountSettings> Normalize(IEnumerable<ProviderAccountSettings>? accounts)
    {
        var result = new List<ProviderAccountSettings>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts ?? [])
        {
            var normalized = Validate(account);
            if (!ids.Add(normalized.Id))
            {
                throw new ArgumentException("Account IDs must be unique.", nameof(accounts));
            }

            result.Add(normalized);
        }

        return result;
    }

    public static ProviderAccountSettings Validate(ProviderAccountSettings account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.Id) || string.IsNullOrWhiteSpace(account.DisplayLabel))
        {
            throw new ArgumentException("Accounts require an ID and display label.", nameof(account));
        }

        if (!Enum.IsDefined(account.ProviderId) || !Enum.IsDefined(account.AuthenticationMethod))
        {
            throw new ArgumentException("Account provider and authentication method must be supported.", nameof(account));
        }

        return account with { Id = account.Id.Trim(), DisplayLabel = account.DisplayLabel.Trim(), ExternalAccountId = TrimOptional(account.ExternalAccountId), WorkspaceId = TrimOptional(account.WorkspaceId) };
    }

    private static string? TrimOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LegacyId(ProviderId provider, string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"codexbar-account-v1:{provider}:{identity.ToLowerInvariant()}")))[..32].ToLowerInvariant();

    private static void AddMigratedAccount(List<ProviderAccountSettings> accounts, ProviderAccountSettings account)
    {
        if (!accounts.Any(existing => existing.Id == account.Id))
        {
            accounts.Add(account);
        }
    }

    private static void MigrateCopilot(AppSettings settings, List<ProviderAccountSettings> accounts)
    {
        var selected = (settings.CopilotAccounts ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = (settings.CopilotKnownAccounts ?? []).Concat(selected).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var providerEntry = (settings.Providers ?? []).FirstOrDefault(pair => string.Equals(pair.Key, ProviderId.Copilot.ToString(), StringComparison.OrdinalIgnoreCase));
        var enabled = providerEntry.Value?.Enabled != false;
        foreach (var username in known)
        {
            AddMigratedAccount(accounts, new ProviderAccountSettings
            {
                Id = LegacyId(ProviderId.Copilot, username),
                ProviderId = ProviderId.Copilot,
                DisplayLabel = $"Copilot {username}",
                Enabled = enabled && (selected.Count == 0 || selected.Contains(username)),
                AuthenticationMethod = ProviderAuthenticationMethod.CommandLine,
                ExternalAccountId = username,
                LegacyCardKey = $"copilot:{username}",
            });
        }

        if (known.Count == 0 && providerEntry.Key is not null)
        {
            AddMigratedAccount(accounts, new ProviderAccountSettings { Id = LegacyId(ProviderId.Copilot, string.Empty), ProviderId = ProviderId.Copilot, DisplayLabel = "Copilot", Enabled = enabled, AuthenticationMethod = ProviderAuthenticationMethod.CommandLine, LegacyCardKey = "Copilot" });
        }
    }
}
