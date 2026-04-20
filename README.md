# CodexBar 🎚️ for Windows

A Windows system tray app that keeps your AI provider usage limits visible. Inspired by [steipete/CodexBar](https://github.com/steipete/CodexBar) (macOS).

Built with C# / WPF / .NET 9 — native Windows, no Electron overhead.

## Providers

| Provider | Auth Method | What's Tracked |
|----------|-------------|----------------|
| **Gemini** | OAuth (Gemini CLI credentials) | Pro + Flash quota |
| **OpenRouter** | API Key | Credits, usage across models |
| **Copilot** | GitHub CLI (`gh auth`) | Usage limits per account |

## Features

- **System tray** icon with per-provider usage meters
- **Popup panel** showing session + weekly limits and reset countdowns
- **Settings** to enable/disable providers and configure auth
- **Auto-refresh** with configurable intervals (1m, 2m, 5m, 15m)
- **Privacy-first**: on-device only, no data sent anywhere except provider APIs

## Getting Started

### Requirements

- Windows 10 or later
- [.NET 9 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)

### Build from source

```powershell
git clone https://github.com/HemSoft/codexbar.git
cd codexbar
dotnet build
dotnet run --project src\CodexBar.App
```

### Provider setup

1. **Gemini**: Install [Gemini CLI](https://github.com/google-gemini/gemini-cli) and run `gemini` to complete OAuth login
2. **OpenRouter**: Get an API key from [openrouter.ai/keys](https://openrouter.ai/keys) and add it in Settings
3. **Copilot**: Uses GitHub CLI tokens — run `gh auth login` for each account

## Architecture

```
CodexBar.sln
├── src/CodexBar.Core/          # Provider abstractions, models, fetch logic
│   ├── Models/                 # UsageSnapshot, ProviderStatus, etc.
│   ├── Providers/              # One folder per provider
│   │   ├── Gemini/
│   │   ├── OpenRouter/
│   │   └── Copilot/
│   └── Services/               # Shared services (HTTP, refresh loop)
└── src/CodexBar.App/           # WPF system tray app
    ├── Views/                  # XAML views
    ├── ViewModels/             # MVVM view models
    └── Resources/              # Icons, assets
```

## Credits

- Inspired by [steipete/CodexBar](https://github.com/steipete/CodexBar) (MIT) by Peter Steinberger
- Inspired by [ccusage](https://github.com/ryoppippi/ccusage) for cost tracking

## License

MIT
