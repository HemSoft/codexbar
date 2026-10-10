# CodexBar 🎚️ for Windows

A Windows system tray app that keeps your AI provider usage limits visible. Inspired by [steipete/CodexBar](https://github.com/steipete/CodexBar) (macOS).

Built with Rust, [GPUI](https://www.gpui.rs/) and [gpui-kit](https://gpui-kit.com/): native Windows, no Electron overhead.

## Providers

| Provider | Auth Method | What's Tracked |
|----------|-------------|----------------|
| **ChatGPT / Codex** | Codex CLI ChatGPT login (`~/.codex/auth.json`), or browser sign-in per account through the Codex CLI | 5-hour + weekly usage limits |
| **Claude** | Claude Code login (`~/.claude/.credentials.json`), or a separate sign-in per account through Claude Code | Session + weekly limits, extra-usage spend |
| **Copilot** | GitHub CLI (`gh auth`), or browser sign-in per account through the GitHub CLI | Usage limits per account; organization AI credits for Enterprise seats |
| **Cursor** | Cursor app sign-in (`%APPDATA%\Cursor\auth.json`) | Plan usage and spend |
| **OpenCode Go / Zen** | Dashboard cookie + workspace ID | Usage and balance |
| **OpenRouter** | API Key | Credits, usage across models |
| **Moonshot (Kimi)** | API Key | Remaining API credit balance |

## Features

- **System tray** icon with a usage tooltip
- **Dashboard** with an account table, focus cards, usage history and window curves
- **Alerts** when an account nears or hits its limit
- **Groups and ordering** by urgency or by hand
- **System, light and dark appearance**, following Windows high contrast
- **Privacy-first**: on-device only, no data sent anywhere except provider APIs; keys are kept in Windows Credential Manager

## Getting Started

### Requirements

- Windows 10 or later
- [Rust](https://www.rust-lang.org/tools/install) (the MSVC toolchain; `rust-toolchain.toml` pins stable)
- Visual Studio Build Tools with the "Desktop development with C++" workload
- Node.js 20 or later (required for `npm install` / pre-commit hook setup)

### Build from source

```powershell
git clone https://github.com/hemsoft-dev/codexbar.git
cd codexbar
npm install          # Also configures the pre-commit hook
.\run.ps1            # Builds in release and starts CodexBar in the system tray
```

`run.ps1` copies the app to `%LOCALAPPDATA%\CodexBar\bin\codexbar.exe` and replaces a running instance. For
development, `cargo run -p codexbar-app -- --demo` starts the dashboard with sample data beside the real app.

### Provider setup

1. **ChatGPT / Codex**: Run `codex` and sign in with your ChatGPT account. For more accounts, add a Codex account with the OAuth sign-in method in Settings > Accounts; CodexBar signs it in with your browser through the Codex CLI
2. **Claude**: Run `claude` and sign in. For more accounts, add a Claude account with the Browser session method in Settings > Accounts; CodexBar opens a Claude Code window to sign it in
3. **Copilot**: Uses GitHub CLI tokens — run `gh auth login` for each account, or add a Copilot account with the OAuth sign-in method and sign it in from Settings. For an Enterprise seat, set the account's enterprise and organization (and optionally the pool total) to see the organization's AI credits
4. **Cursor**: Sign in to the Cursor app
5. **OpenRouter**: Get an API key from [openrouter.ai/keys](https://openrouter.ai/keys) and add it in Settings, or set `OPENROUTER_API_KEY`
6. **Moonshot (Kimi)**: Get an API key from [platform.kimi.ai](https://platform.kimi.ai/) and add it in Settings, or set `MOONSHOT_API_KEY`

## Architecture

```text
Cargo.toml
├── crates/codexbar-core/       # Models, metrics, alerts, layout, formatting
├── crates/codexbar-providers/  # One module per provider, HTTP and OAuth helpers
├── crates/codexbar-store/      # Settings, credentials, snapshots, usage history
└── crates/codexbar-app/        # GPUI dashboard and tray host
```

The `src/` folder holds the retired C# / WPF app.

## Credits

- Inspired by [steipete/CodexBar](https://github.com/steipete/CodexBar) (MIT) by Peter Steinberger
- Inspired by [ccusage](https://github.com/ryoppippi/ccusage) for cost tracking

## License

MIT
