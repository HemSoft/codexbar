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
