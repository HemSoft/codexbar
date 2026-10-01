# Account configuration

CodexBar settings now include `accountConfigurationVersion: 1` and an `accounts`
array. Each record has a stable `id`, `providerId`, `displayLabel`, `enabled` flag,
and `authenticationMethod`. Optional GitHub usernames and OpenCode Go workspace
IDs are metadata, not credentials.

Use **Configure** to add, rename, enable, disable or remove account records.
Save writes the draft. Cancel leaves the saved settings unchanged. Invalid
labels or failed writes keep the window open so the draft can be corrected or
retried.

## Migration and compatibility

Legacy provider settings, API keys, Copilot selections and known usernames,
manual card order, OpenCode Go workspace settings and session data remain in the
settings file. Migration creates deterministic IDs for legacy accounts and does
not add them again after a version-1 record is removed.

Copilot identities are case-insensitive. Unselected known usernames stay in the
configuration as disabled records when an explicit selection exists. No explicit
selection retains the existing automatic-discovery behavior.

The existing provider switches still control dashboard visibility. Removing or
disabling all accounts for a provider disables that provider. The migrated
OpenCode Go record updates the existing workspace setting. Copilot usernames
and enabled selections update the legacy CLI-selection fields.

A newer account schema cannot be overwritten by this application, including
when another process upgrades the file after it was loaded. Failed persistence
leaves the last saved account state available for rollback.

## Scope

This foundation stores multiple account records for every supported provider.
Authentication-method selection is configuration, not a completed sign-in.
Provider-specific sign-in, secure credential references and account-scoped usage
routing are separate parity tasks under [tracker #70](https://github.com/HemSoft/codexbar/issues/70).
The existing provider refresh implementations continue to use their compatible
legacy settings until those dependent tasks connect the new records.
