# Browser sign-in (OAuth) for CodexBar

CodexBar's Rust app has one shared sign-in path for providers that support it
(`crates/codexbar-providers/src/oauth.rs`, issue #77). It follows
[RFC 8252, OAuth 2.0 for Native Apps](https://www.rfc-editor.org/rfc/rfc8252):

- **System browser.** Sign-in opens the user's default browser. CodexBar never
  shows a provider's sign-in page inside its own window.
- **Authorization code with PKCE.** Every sign-in uses a fresh S256 code
  challenge ([RFC 7636](https://www.rfc-editor.org/rfc/rfc7636)) and a random
  `state`.
- **Loopback redirect.** The callback listener binds only to `127.0.0.1` (or
  `::1`) on a port Windows picks. It lives for one sign-in. Requests to another
  path, with a wrong `state`, or malformed are answered and ignored. Only the
  matching `state` completes the sign-in.
- **Ending a sign-in.** A sign-in ends on success, on a refusal (only the
  provider's short error code is kept), after the timeout, or when the user
  cancels.
- **Token storage.** Tokens are stored as the account's secret in Windows
  Credential Manager (`CodexBar:account:<id>`), never in `settings.json`. A
  renewal replaces them in one write and keeps the old refresh token when the
  provider doesn't rotate it.
- **No secrets in output.** Errors, logs and the UI never show tokens,
  authorization codes or token-endpoint response bodies.

## Sign-in through a provider's own CLI

Where a provider's official CLI can sign in for another program, CodexBar uses
that instead of a client registration of its own. The CLI runs its own sign-in
and keeps writing its own credential file; CodexBar only reads it.

- **ChatGPT / Codex (#78).** An account with the OAuth sign-in method gets its
  own Codex home in the settings folder (`codex\<account id>`, with
  `cli_auth_credentials_store = "file"`). CodexBar starts
  `codex app-server` for that home and uses its JSON-RPC methods:
  `account/login/start` (browser sign-in; CodexBar opens the returned page and
  Codex listens for the redirect), `account/login/cancel`, `account/logout`,
  and `account/read` with `refreshToken: true` to renew tokens. A sign-in is
  renewed when its access token expires within a day, or once after the usage
  endpoint refuses it, at most once per home every 15 minutes. Each ChatGPT
  user and workspace is its own dashboard account, so signing a home in to
  another identity never inherits the previous one's history. An account with
  the Automatic method reads the Codex CLI's own `~/.codex` (or `CODEX_HOME`),
  the import and fallback path. The app-server process tree runs in a Windows
  job that ends with each session.

- **GitHub Copilot (#79).** An account with the OAuth sign-in method is signed
  in by the GitHub CLI's own device sign-in (`gh auth login --web
  --insecure-storage`), run in a private, temporary config folder inside the
  settings folder with the clipboard copy and prompts off. Not in a terminal,
  `gh` prints the one-time code and `https://github.com/login/device`; CodexBar
  shows the code and opens the page. Once `gh` reports the sign-in, CodexBar
  reads the token with `gh auth token`, keeps it in Credential Manager under
  the account (`CodexBar:account:<id>`) and deletes the folder. The user's own
  `gh` accounts, keyring entries and git configuration are never touched.
  GitHub CLI tokens don't expire; Sign out deletes CodexBar's copy (revoke it
  at github.com/settings/applications to end it on GitHub's side). Accounts with
  the Automatic or Command line method use the GitHub CLI's own accounts, the
  import and fallback path.

## Approved client registrations

A provider can use this path only once a human has confirmed its client
registration and redirect. Using another application's client ID (for example
the Codex CLI's or Claude Code's) is not approved. Those tools' own sign-ins
are read instead, as before.

| Provider | Client ID | Redirect | Scopes | Approved by | Date |
| --- | --- | --- | --- | --- | --- |
| None yet | | | | | |

To add a provider:

1. Register CodexBar as a native (public) client with the provider. Allow the
   redirect `http://127.0.0.1/<path>` with any port. RFC 8252 section 7.3
   requires providers to accept any loopback port.
2. Record the registration in the table above, with who approved it.
3. Give the provider adapter an `OAuthClient` with those values. Then wire its
   sign-in (#78 Codex, #79 Copilot, #80 Claude, #81 Cursor).
