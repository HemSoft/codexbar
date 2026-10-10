//! What the app knows about each provider: its name, how it signs in, and which secret (if any) it needs.

use codexbar_store::settings::{AuthMethod, names};

/// A secret the user can paste into Settings.
pub struct SecretSpec {
    /// Field label, e.g. "API key".
    pub label: &'static str,
    /// Environment variable that overrides the stored secret.
    pub env: &'static str,
}

pub struct ProviderInfo {
    /// The WPF `ProviderId` name used in settings.
    pub id: &'static str,
    pub display: &'static str,
    /// How this provider signs in when it needs no pasted secret.
    pub sign_in: &'static str,
    pub secret: Option<SecretSpec>,
    pub default_method: AuthMethod,
    /// Whether more than one account can be fetched (key-based providers).
    pub multi_account: bool,
}

pub const PROVIDERS: [ProviderInfo; 8] = [
    ProviderInfo {
        id: names::CODEX,
        display: "ChatGPT · Codex",
        sign_in: "Automatic uses the Codex CLI's own ChatGPT sign-in (`codex`). OAuth signs an account in with your browser \
                  through the Codex CLI and keeps it separate, so several ChatGPT accounts can be added.",
        secret: None,
        default_method: AuthMethod::Automatic,
        multi_account: true,
    },
    ProviderInfo {
        id: names::CLAUDE,
        display: "Claude",
        sign_in: "Uses the sign-in from Claude Code (`claude`).",
        secret: None,
        default_method: AuthMethod::OAuth,
        multi_account: false,
    },
    ProviderInfo {
        id: names::COPILOT,
        display: "Copilot",
        sign_in: "Automatic and Command line use the GitHub CLI's accounts (`gh auth login`); set a username to fetch \
                  one. OAuth signs an account in with your browser through the GitHub CLI, kept apart from its own \
                  accounts. Enterprise seats can also show their organization's AI credits.",
        secret: None,
        default_method: AuthMethod::CommandLine,
        multi_account: true,
    },
    ProviderInfo {
        id: names::CURSOR,
        display: "Cursor",
        sign_in: "Uses the sign-in from the Cursor app.",
        secret: None,
        default_method: AuthMethod::Automatic,
        multi_account: false,
    },
    ProviderInfo {
        id: names::OPENROUTER,
        display: "OpenRouter",
        sign_in: "Needs an API key from openrouter.ai/keys.",
        secret: Some(SecretSpec {
            label: "API key",
            env: "OPENROUTER_API_KEY",
        }),
        default_method: AuthMethod::ApiKey,
        multi_account: true,
    },
    ProviderInfo {
        id: names::OPENCODE_GO,
        display: "OpenCode Go",
        sign_in: "Needs the opencode.ai dashboard auth cookie and workspace id.",
        secret: Some(SecretSpec {
            label: "Dashboard auth cookie",
            env: "OPENCODE_GO_AUTH_COOKIE",
        }),
        default_method: AuthMethod::BrowserSession,
        multi_account: false,
    },
    ProviderInfo {
        id: names::OPENCODE_ZEN,
        display: "OpenCode Zen",
        sign_in: "Needs the opencode.ai dashboard auth cookie (shared with OpenCode Go when unset).",
        secret: Some(SecretSpec {
            label: "Dashboard auth cookie",
            env: "OPENCODE_ZEN_AUTH_COOKIE",
        }),
        default_method: AuthMethod::BrowserSession,
        multi_account: false,
    },
    ProviderInfo {
        id: names::MOONSHOT,
        display: "Moonshot (Kimi)",
        sign_in: "Needs an API key from platform.kimi.ai.",
        secret: Some(SecretSpec {
            label: "API key",
            env: "MOONSHOT_API_KEY",
        }),
        default_method: AuthMethod::ApiKey,
        multi_account: true,
    },
];

pub fn info(provider: &str) -> &'static ProviderInfo {
    PROVIDERS
        .iter()
        .find(|info| info.id.eq_ignore_ascii_case(provider))
        .unwrap_or(&PROVIDERS[0])
}
