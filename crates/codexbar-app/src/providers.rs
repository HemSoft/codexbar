//! Which providers run: the WPF app's switches and keys (read-only), with environment variables taking precedence.

use std::sync::Arc;

use codexbar_providers::balance::{MoonshotProvider, OpenRouterProvider, resolve_key};
use codexbar_providers::claude::{ClaudeProvider, default_credentials_path};
use codexbar_providers::codex::{CodexProvider, default_auth_path};
use codexbar_providers::copilot::CopilotProvider;
use codexbar_providers::cursor::{self, CursorProvider};
use codexbar_providers::opencode::OpenCodeProvider;
use codexbar_providers::{SystemCommandRunner, UreqClient, UsageProvider};
use codexbar_store::settings::{LegacySettings, names};

/// Every enabled provider. A provider whose sign-in is missing still runs, so its row explains how to sign in.
pub fn enabled(settings: &LegacySettings) -> Vec<Arc<dyn UsageProvider>> {
    let mut providers: Vec<Arc<dyn UsageProvider>> = Vec::new();
    if settings.is_enabled(names::CODEX) {
        providers.push(Arc::new(CodexProvider::new(UreqClient::new(), default_auth_path())));
    }
    if settings.is_enabled(names::COPILOT) {
        providers.push(Arc::new(CopilotProvider::new(UreqClient::new(), SystemCommandRunner)));
    }
    if settings.is_enabled(names::CLAUDE) {
        providers.push(Arc::new(ClaudeProvider::new(
            UreqClient::new(),
            default_credentials_path(),
        )));
    }
    if settings.is_enabled(names::CURSOR) {
        providers.push(Arc::new(CursorProvider::new(
            UreqClient::new(),
            cursor::default_auth_path(),
        )));
    }
    if settings.is_enabled(names::OPENROUTER) {
        let key = resolve_key("OPENROUTER_API_KEY", settings.api_key(names::OPENROUTER));
        providers.push(Arc::new(OpenRouterProvider::new(UreqClient::new(), key)));
    }
    if let Some(opencode) = opencode(settings) {
        providers.push(opencode);
    }
    if settings.is_enabled(names::MOONSHOT) {
        let key = resolve_key("MOONSHOT_API_KEY", settings.api_key(names::MOONSHOT));
        providers.push(Arc::new(MoonshotProvider::new(UreqClient::new(), key)));
    }
    providers
}

/// Go and Zen share the dashboard and its cookie; either half can be switched off.
fn opencode(settings: &LegacySettings) -> Option<Arc<dyn UsageProvider>> {
    let env = |name: &str| {
        std::env::var(name)
            .ok()
            .map(|value| value.trim().to_owned())
            .filter(|v| !v.is_empty())
    };
    let go_on = settings.is_enabled(names::OPENCODE_GO);
    let zen_on = settings.is_enabled(names::OPENCODE_ZEN);
    if !go_on && !zen_on {
        return None;
    }
    let workspace = env("OPENCODE_GO_WORKSPACE_ID").or_else(|| settings.opencode_workspace_id());
    let go_cookie = env("OPENCODE_GO_AUTH_COOKIE").or_else(|| settings.api_key(names::OPENCODE_GO));
    let zen_cookie = env("OPENCODE_ZEN_AUTH_COOKIE")
        .or_else(|| settings.api_key(names::OPENCODE_ZEN))
        .or_else(|| go_cookie.clone());
    Some(Arc::new(OpenCodeProvider::new(
        UreqClient::without_redirects(),
        workspace,
        go_on.then_some(go_cookie).flatten(),
        zen_on.then_some(zen_cookie).flatten(),
    )))
}
