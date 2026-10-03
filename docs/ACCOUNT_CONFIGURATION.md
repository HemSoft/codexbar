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
when another process upgrades the file after it was loaded. All settings writers,
including session-baseline updates, hold the exclusive `settings.write.lock`
file while reading the schema and replacing settings. Other CodexBar versions
must honor the same lock protocol. A competing writer causes a recoverable save
failure instead of a blocked UI. The raw version is checked before enum
conversion, so unknown future provider names cannot bypass the guard. Failed
persistence leaves the last saved account state available for rollback.

Version-1 disk accounts are validated before any write. Background baseline
updates change only their own value and reset time in the latest locked disk
snapshot. `Load` attaches an opaque, immutable `AccountSnapshot` that is not
serialized. Keep that snapshot when copying a draft. An unrelated save retains
current disk accounts and unchanged legacy account fields; conflicting account
edits are refused rather than silently replacing another process's changes.
The configuration window keeps its original snapshot even when a background
refresh advances the cache. Detected unsupported versions, invalid versioned
account records, ambiguous case-variant provider keys and account-edit conflicts
explain that the file was not overwritten, preserve the draft, and announce the
error through WPF UI Automation's polite live region. A single provider key is
case-insensitive; colliding spellings are refused on load or write instead of
silently discarding credentials or visibility. Syntax-invalid JSON retains the existing
legacy defaults/fallback behavior; it is not a recognized account-schema error.

## Scope

This foundation stores multiple account records for every supported provider.
Authentication-method selection is configuration, not a completed sign-in.
Provider-specific sign-in, secure credential references and account-scoped usage
routing are separate parity tasks under [tracker #70](https://github.com/HemSoft/codexbar/issues/70).
The existing provider refresh implementations continue to use their compatible
legacy settings until those dependent tasks connect the new records.
