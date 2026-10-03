// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Configuration;

using System.Collections.ObjectModel;

/// <summary>
/// Opaque, immutable account state captured by <see cref="SettingsService.Load"/>.
/// Carry it with a copied settings draft to detect conflicting account edits.
/// </summary>
public sealed class AccountConfigurationSnapshot
{
    internal AccountConfigurationSnapshot(AppSettings settings)
    {
        this.Accounts = Array.AsReadOnly(settings.Accounts.ToArray());
        this.ProviderStates = new ReadOnlyDictionary<string, bool>(settings.Providers.ToDictionary(entry => entry.Key, entry => entry.Value.Enabled));
        this.WorkspaceId = settings.OpenCodeGoWorkspaceId;
        this.CopilotAccounts = Array.AsReadOnly(settings.CopilotAccounts.ToArray());
        this.CopilotKnownAccounts = Array.AsReadOnly(settings.CopilotKnownAccounts.ToArray());
    }

    internal IReadOnlyList<ProviderAccountSettings> Accounts { get; }

    internal IReadOnlyDictionary<string, bool> ProviderStates { get; }

    internal string? WorkspaceId { get; }

    internal IReadOnlyList<string> CopilotAccounts { get; }

    internal IReadOnlyList<string> CopilotKnownAccounts { get; }
}
