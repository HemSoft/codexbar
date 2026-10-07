//! Which providers run, built from the account records in the shared settings file. Environment variables still win
//! for secrets; Credential Manager comes next; plaintext keys from the WPF app are the last fallback.

use std::sync::Arc;

use codexbar_providers::balance::{MoonshotProvider, OpenRouterProvider};
use codexbar_providers::claude::{ClaudeProvider, default_credentials_path};
use codexbar_providers::codex::{CodexProvider, default_auth_path};
use codexbar_providers::copilot::CopilotProvider;
use codexbar_providers::cursor::{self, CursorProvider};
use codexbar_providers::opencode::OpenCodeProvider;
use codexbar_providers::{SystemCommandRunner, UreqClient, UsageProvider};
use codexbar_store::settings::{AccountRecord, names};

use crate::settings_hub::SettingsHub;

/// Enabled accounts for a provider, or its implicit legacy account when it has no records and is switched on.
fn enabled_accounts(hub: &SettingsHub, provider: &str) -> Vec<AccountRecord> {
    let settings = hub.settings();
    if !settings.is_enabled(provider) {
        return Vec::new();
    }
    let records: Vec<AccountRecord> = settings.accounts_for(provider).cloned().collect();
    if records.is_empty() {
        vec![SettingsHub::implicit_account(provider)]
    } else {
        records.into_iter().filter(|account| account.enabled).collect()
    }
}

pub fn enabled(hub: &SettingsHub) -> Vec<Arc<dyn UsageProvider>> {
    let mut providers: Vec<Arc<dyn UsageProvider>> = Vec::new();
    if !enabled_accounts(hub, names::CODEX).is_empty() {
        providers.push(Arc::new(CodexProvider::new(UreqClient::new(), default_auth_path())));
    }

    let copilot = enabled_accounts(hub, names::COPILOT);
    if !copilot.is_empty() {
        let usernames: Vec<String> = copilot.iter().filter_map(|a| a.external_id.clone()).collect();
        let provider = CopilotProvider::new(UreqClient::new(), SystemCommandRunner);
        // Records without usernames mean "every gh account", as the WPF app's automatic discovery does.
        let provider = if usernames.len() == copilot.len() {
            provider.only_accounts(usernames)
        } else {
            provider
        };
        providers.push(Arc::new(provider));
    }

    if !enabled_accounts(hub, names::CLAUDE).is_empty() {
        providers.push(Arc::new(ClaudeProvider::new(
            UreqClient::new(),
            default_credentials_path(),
        )));
    }
    if !enabled_accounts(hub, names::CURSOR).is_empty() {
        providers.push(Arc::new(CursorProvider::new(
            UreqClient::new(),
            cursor::default_auth_path(),
        )));
    }

    let openrouter = enabled_accounts(hub, names::OPENROUTER);
    let multiple = openrouter.len() > 1;
    let configured = hub.settings().accounts_for(names::OPENROUTER).next().is_some();
    for account in openrouter {
        let key = hub.secret_for(&account).0;
        let provider = OpenRouterProvider::new(UreqClient::new(), key);
        // A configured account always reports under its own id, so history and preferences survive adding or
        // removing a second one; the implicit account (no records yet) keeps the legacy id until one is configured.
        let provider = if multiple {
            provider.with_account(account.id.clone(), account.label.clone())
        } else if configured {
            provider.with_id(account.id.clone())
        } else {
            provider
        };
        providers.push(Arc::new(provider));
    }

    if let Some(opencode) = opencode(hub) {
        providers.push(opencode);
    }

    let moonshot = enabled_accounts(hub, names::MOONSHOT);
    let multiple = moonshot.len() > 1;
    let configured = hub.settings().accounts_for(names::MOONSHOT).next().is_some();
    for account in moonshot {
        let key = hub.secret_for(&account).0;
        let provider = MoonshotProvider::new(UreqClient::new(), key);
        // A configured account always reports under its own id, so history and preferences survive adding or
        // removing a second one; the implicit account (no records yet) keeps the legacy id until one is configured.
        let provider = if multiple {
            provider.with_account(account.id.clone(), account.label.clone())
        } else if configured {
            provider.with_id(account.id.clone())
        } else {
            provider
        };
        providers.push(Arc::new(provider));
    }
    providers
}

/// Accounts that used to report under their provider's legacy id: a provider's only OpenRouter or Moonshot account
/// reported as `openrouter`/`moonshot` before it reported under its configured id. Each pair is (legacy, configured)
/// for moving stored history and preferences. The one configured account counts whether or not it is enabled now (a
/// disabled account keeps its history for when it is enabled again). With no records the implicit account still
/// reports under the legacy id, so nothing moves until the first account is configured; with several configured
/// accounts the legacy history's owner is unknown, so nothing moves.
pub fn legacy_ids(hub: &SettingsHub) -> Vec<(&'static str, String)> {
    [("openrouter", names::OPENROUTER), ("moonshot", names::MOONSHOT)]
        .into_iter()
        .filter_map(|(legacy, provider)| {
            let records: Vec<&AccountRecord> = hub.settings().accounts_for(provider).collect();
            match records.as_slice() {
                [only] => Some((legacy, only.id.clone())),
                _ => None,
            }
        })
        .collect()
}

/// Go and Zen share one dashboard account; either half can be switched off. Zen falls back to Go's cookie.
fn opencode(hub: &SettingsHub) -> Option<Arc<dyn UsageProvider>> {
    let go = enabled_accounts(hub, names::OPENCODE_GO).into_iter().next();
    let zen = enabled_accounts(hub, names::OPENCODE_ZEN).into_iter().next();
    if go.is_none() && zen.is_none() {
        return None;
    }
    let env = |name: &str| {
        std::env::var(name)
            .ok()
            .map(|v| v.trim().to_owned())
            .filter(|v| !v.is_empty())
    };
    let workspace = env("OPENCODE_GO_WORKSPACE_ID")
        .or_else(|| go.as_ref().and_then(|a| a.workspace_id.clone()))
        .or_else(|| hub.settings().opencode_workspace_id());
    let go_cookie = go.as_ref().and_then(|account| hub.secret_for(account).0);
    let zen_cookie = zen
        .as_ref()
        .and_then(|account| hub.secret_for(account).0)
        .or_else(|| go_cookie.clone());
    Some(Arc::new(OpenCodeProvider::new(
        UreqClient::without_redirects(),
        workspace,
        go_cookie,
        zen.is_some().then_some(zen_cookie).flatten(),
    )))
}
