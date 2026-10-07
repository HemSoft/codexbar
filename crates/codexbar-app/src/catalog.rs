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
        sign_in: "Uses the ChatGPT sign-in from the Codex CLI (`codex`).",
        secret: None,
        default_method: AuthMethod::Automatic,
        multi_account: false,
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
        sign_in: "Uses the GitHub CLI (`gh auth login`). Set a username to fetch one account.",
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
