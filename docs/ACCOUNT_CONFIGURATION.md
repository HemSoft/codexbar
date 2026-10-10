# Account configuration

CodexBar's accounts live in `settings.json` (in `%USERPROFILE%\.codexbar`) as
`accountConfigurationVersion: 1` and an `accounts` array. Accounts are added,
edited, switched off and removed in **Settings > Accounts**; every change is
saved at once, and destructive actions ask first.

## Account records

| Field | Meaning |
| --- | --- |
| `id` | Stable id (32 hex digits for accounts CodexBar creates) |
| `providerId` | `Codex`, `Claude`, `Copilot`, `Cursor`, `OpenRouter`, `OpenCodeGo`, `OpenCodeZen` or `Moonshot` |
| `displayLabel` | The account's name |
| `enabled` | Whether it is fetched and shown |
| `authenticationMethod` | `Automatic`, `ApiKey`, `CommandLine`, `OAuth` or `BrowserSession` (see below) |
| `externalAccountId` | A Copilot account's GitHub username; for an account CodexBar signed in, the identity it holds |
| `workspaceId` | The OpenCode Go workspace (`wrk_…`) |
| `legacyCardKey` | Kept from older CodexBar versions for ordering |
| `copilotEnterprise`, `copilotOrganization`, `copilotPoolTotal` | Copilot org billing for an Enterprise seat; written only when set |

None of these are secrets. Keys, cookies and tokens are kept in Windows
Credential Manager under `CodexBar:account:<id>` (see [PRIVACY.md](PRIVACY.md)).

## Sign-in methods

| Provider | The provider's own sign-in | Signed in by CodexBar |
| --- | --- | --- |
| ChatGPT / Codex | Automatic (`~/.codex`) | OAuth: its own Codex home, through the Codex CLI |
| Claude | OAuth or Automatic (`~/.claude`) | Browser session: its own Claude Code folder, through a Claude Code window |
| Copilot | Automatic or Command line (the GitHub CLI's accounts; a username limits it to one) | OAuth: the GitHub CLI's device sign-in, token kept by CodexBar |
| Cursor | Automatic (the Cursor app) | OAuth: its own folder, through the Cursor CLI |
| OpenRouter, Moonshot | API key (or the environment variable) | |
| OpenCode Go / Zen | Browser session (the dashboard cookie) | |

Accounts CodexBar signs in show **Sign in…**, **Sign in again…** and **Sign out**
in Settings. [OAUTH.md](OAUTH.md) explains each one. Several accounts can be added
for every provider except OpenCode. Adding the first account CodexBar signs in
for Codex, Claude or Cursor keeps the provider's own sign-in as an account of its
own, so it doesn't disappear.

## Identity and history

Each signed-in identity (a ChatGPT user and workspace, a Claude account and
organization in a CodexBar folder, a Cursor user, a GitHub username) is its own
dashboard account with its own history, groups and alerts. Signing an account in
to another identity starts a new dashboard account; the history CodexBar kept for
the identity it replaced is deleted, so identities never mix. Removing an account
deletes its saved secret, its CodexBar folder and, for an account that is its own
dashboard account, its usage history. Claude Code's own sign-in keeps the
account-neutral `claude` id, because its profile file can name another account
than its credentials.

## Compatibility

- A missing or syntax-invalid `settings.json` behaves like an empty one.
- A file from a newer CodexBar (a higher `accountConfigurationVersion`), an
  invalid account record, or provider keys that differ only in case make
  Settings read-only, and the file is never overwritten.
- API keys an older CodexBar kept in the file are moved to Credential Manager at
  startup; a key that can't be moved stays usable and is tried again next time.
- Settings that only the Rust app uses (groups, order, appearance, alerts) are in
  `dashboard.json`.
