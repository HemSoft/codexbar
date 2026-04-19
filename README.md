# CodexBar 🎚️ for Windows

A Windows system tray app that keeps your AI provider usage limits visible. Inspired by [steipete/CodexBar](https://github.com/steipete/CodexBar) (macOS).

Built with C# / WPF / .NET 9 — native Windows, no Electron overhead.

## Providers

| Provider | Auth Method | What's Tracked |
|----------|-------------|----------------|
| **Claude** | OAuth (Claude CLI credentials) | Session (5h), Weekly usage |
| **Gemini** | OAuth (gcloud CLI credentials) | Quota |
| **OpenRouter** | API Key | Credits, usage across models |
| **Copilot** | GitHub Device Flow | Usage limits |

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

1. **Claude**: Install [Claude CLI](https://docs.anthropic.com/en/docs/claude-cli) and run `claude login`
2. **Gemini**: Install [Google Cloud CLI](https://cloud.google.com/sdk/docs/install) and run `gcloud auth login`
3. **OpenRouter**: Get an API key from [openrouter.ai/keys](https://openrouter.ai/keys) and add it in Settings
4. **Copilot**: Uses GitHub Device Flow — authenticate from Settings

## Architecture

```
CodexBar.sln
├── src/CodexBar.Core/          # Provider abstractions, models, fetch logic
│   ├── Models/                 # UsageSnapshot, ProviderStatus, etc.
│   ├── Providers/              # One folder per provider
│   │   ├── Claude/
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
