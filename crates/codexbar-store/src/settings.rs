//! The shared `~/.codexbar/settings.json` (or the older `codexbar-settings.json`), in the account-scoped format the
//! WPF app introduced in #114 (`docs/ACCOUNT_CONFIGURATION.md`). Both apps read and write the same file during the
//! migration, so this module follows that contract exactly:
//!
//! - The document is kept as raw JSON, so every field this app does not own round-trips untouched.
//! - `accountConfigurationVersion` above 1, or a versioned file without a valid `accounts` array, is never
//!   overwritten.
//! - Legacy settings (version 0) migrate in memory with the same deterministic IDs as the WPF app.
//! - Every write holds the exclusive `settings.write.lock`, re-reads the file under it, refuses account edits that
//!   conflict with changes another process made since load, and replaces the file through a temp file.
//! - Legacy provider switches, Copilot selections and the OpenCode Go workspace stay in sync with the accounts.
//!
//! Secrets are never written here. Keys go to Windows Credential Manager (`credentials`). Plaintext keys an older
//! CodexBar left in the file move there once (#74, `move_plaintext_keys`) and are removed from the file only after
//! Credential Manager holds them.

use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};

/// The account schema version this app understands.
pub const ACCOUNT_CONFIGURATION_VERSION: i64 = 1;
const PRIMARY_FILE: &str = "settings.json";
const FALLBACK_FILE: &str = "codexbar-settings.json";
const LOCK_FILE: &str = "settings.write.lock";
/// Zoom range and step shared with the WPF app (`ZoomHelper`).
pub const MIN_ZOOM: f64 = 0.5;
pub const MAX_ZOOM: f64 = 3.0;
pub const ZOOM_STEP: f64 = 0.1;

/// Provider names as the WPF app stores them (`ProviderId` enum names).
pub mod names {
    pub const CODEX: &str = "Codex";
    pub const COPILOT: &str = "Copilot";
    pub const CLAUDE: &str = "Claude";
    pub const CURSOR: &str = "Cursor";
    pub const OPENROUTER: &str = "OpenRouter";
    pub const OPENCODE_GO: &str = "OpenCodeGo";
    pub const OPENCODE_ZEN: &str = "OpenCodeZen";
    pub const MOONSHOT: &str = "Moonshot";

    /// Every provider, in the WPF enum's order.
    pub const ALL: [&str; 8] = [
        OPENROUTER,
        COPILOT,
        CLAUDE,
        CODEX,
        CURSOR,
        OPENCODE_GO,
        OPENCODE_ZEN,
        MOONSHOT,
    ];
}

/// How an account signs in. Matches the WPF `ProviderAuthenticationMethod` names.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AuthMethod {
    Automatic,
    ApiKey,
    CommandLine,
    OAuth,
    BrowserSession,
}

impl AuthMethod {
    pub const ALL: [Self; 5] = [
        Self::Automatic,
        Self::ApiKey,
        Self::CommandLine,
        Self::OAuth,
        Self::BrowserSession,
    ];

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Automatic => "Automatic",
            Self::ApiKey => "ApiKey",
            Self::CommandLine => "CommandLine",
            Self::OAuth => "OAuth",
            Self::BrowserSession => "BrowserSession",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Automatic => "Automatic",
            Self::ApiKey => "API key",
            Self::CommandLine => "Command line",
            Self::OAuth => "OAuth",
            Self::BrowserSession => "Browser session",
        }
    }

    fn parse(value: &str) -> Option<Self> {
        Self::ALL
            .into_iter()
            .find(|method| method.as_str().eq_ignore_ascii_case(value))
    }
}

/// One configured account. Holds identity and preferences, never credentials.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AccountRecord {
    pub id: String,
    pub provider: String,
    pub label: String,
    pub enabled: bool,
    pub method: AuthMethod,
    /// Provider identity, such as a Copilot CLI username.
    pub external_id: Option<String>,
    pub workspace_id: Option<String>,
    /// The card key kept for the WPF app's manual ordering.
    pub legacy_card_key: Option<String>,
}

impl AccountRecord {
    /// A new account with a random id, as the WPF app's `AccountConfiguration.Create` does.
    pub fn new(provider: &str, label: &str, method: AuthMethod) -> Self {
        Self {
            id: random_id(),
            provider: provider.to_owned(),
            label: label.trim().to_owned(),
            enabled: true,
            method,
            external_id: None,
            workspace_id: None,
            legacy_card_key: None,
        }
    }

    fn to_json(&self) -> Value {
        let mut map = Map::new();
        map.insert("id".into(), json!(self.id));
        map.insert("providerId".into(), json!(self.provider));
        map.insert("displayLabel".into(), json!(self.label));
        map.insert("enabled".into(), json!(self.enabled));
        map.insert("authenticationMethod".into(), json!(self.method.as_str()));
        map.insert("externalAccountId".into(), opt(&self.external_id));
        map.insert("workspaceId".into(), opt(&self.workspace_id));
        map.insert("legacyCardKey".into(), opt(&self.legacy_card_key));
        Value::Object(map)
    }

    fn from_json(value: &Value) -> Result<Self, SettingsError> {
        let invalid = || SettingsError::InvalidAccounts;
        let object = value.as_object().ok_or_else(invalid)?;
        let text = |key: &str| {
            object
                .get(key)
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|s| !s.is_empty())
        };
        let provider = text("providerId").and_then(canonical_provider).ok_or_else(invalid)?;
        let method = text("authenticationMethod")
            .and_then(AuthMethod::parse)
            .ok_or_else(invalid)?;
        Ok(Self {
            id: text("id").ok_or_else(invalid)?.to_owned(),
            provider: provider.to_owned(),
            label: text("displayLabel").ok_or_else(invalid)?.to_owned(),
            enabled: object.get("enabled").and_then(Value::as_bool).ok_or_else(invalid)?,
            method,
            external_id: text("externalAccountId").map(str::to_owned),
            workspace_id: text("workspaceId").map(str::to_owned),
            legacy_card_key: text("legacyCardKey").map(str::to_owned),
        })
    }
}

fn opt(value: &Option<String>) -> Value {
    value.as_deref().map_or(Value::Null, |v| json!(v))
}

fn canonical_provider(name: &str) -> Option<&'static str> {
    names::ALL.into_iter().find(|known| known.eq_ignore_ascii_case(name))
}

/// Why settings could not be read or written. None of these overwrite the file.
#[derive(Debug, PartialEq, Eq)]
pub enum SettingsError {
    /// The file uses a newer account schema.
    NewerVersion,
    /// The versioned account list is missing or has an invalid record.
    InvalidAccounts,
    /// Two provider keys differ only by case, so credentials or visibility would be ambiguous.
    AmbiguousProviderKeys,
    /// Another process is writing settings right now; retry shortly.
    Busy,
    /// Another process changed the accounts since this copy was loaded.
    Conflict,
    /// The account id or label is invalid, or an account tried to change provider.
    InvalidEdit(&'static str),
    Io(String),
}

impl std::fmt::Display for SettingsError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NewerVersion => {
                write!(
                    f,
                    "Settings were written by a newer CodexBar. The file was not overwritten."
                )
            }
            Self::InvalidAccounts => {
                write!(
                    f,
                    "The account list in settings can't be read. The file was not overwritten."
                )
            }
            Self::AmbiguousProviderKeys => write!(
                f,
                "Settings list the same provider twice with different capitalization. The file was not overwritten."
            ),
            Self::Busy => write!(f, "Another CodexBar window is saving settings. Try again in a moment."),
            Self::Conflict => write!(
                f,
                "Accounts were changed elsewhere since this window opened. Reopen Settings to see the latest accounts."
            ),
            Self::InvalidEdit(reason) => write!(f, "{reason}"),
            Self::Io(message) => write!(f, "Couldn't save settings: {message}"),
        }
    }
}

impl std::error::Error for SettingsError {}

/// The settings document plus the account list it held when loaded (to detect conflicting edits).
#[derive(Clone, Debug)]
pub struct Settings {
    dir: PathBuf,
    path: PathBuf,
    doc: Value,
    accounts: Vec<AccountRecord>,
    loaded_accounts: Vec<AccountRecord>,
    /// Top-level scalar settings this copy changed. Only these are written back, so a value another process
    /// saved since load (the WPF app's zoom, say) is never overwritten with this copy's stale one.
    edited: Vec<&'static str>,
    /// Providers whose plaintext `apiKey` moved to Credential Manager; the next save removes it from the file.
    forgotten_keys: Vec<String>,
}

impl Settings {
    /// `CODEXBAR_SETTINGS_DIR` when set (isolated testing), otherwise `~/.codexbar`.
    pub fn default_dir() -> PathBuf {
        if let Some(dir) = std::env::var_os("CODEXBAR_SETTINGS_DIR").filter(|dir| !dir.is_empty()) {
            return PathBuf::from(dir);
        }
        std::env::var_os("USERPROFILE")
            .map(PathBuf::from)
            .unwrap_or_default()
            .join(".codexbar")
    }

    /// Loads and validates settings. A missing or syntax-invalid file behaves like an empty one, as in the WPF app;
    /// schema problems are errors so callers never save over them.
    pub fn load(dir: &Path) -> Result<Self, SettingsError> {
        let (path, doc) = read_document(dir);
        let accounts = accounts_of(&doc)?;
        Ok(Self {
            dir: dir.to_owned(),
            path,
            doc,
            loaded_accounts: accounts.clone(),
            accounts,
            edited: Vec::new(),
            forgotten_keys: Vec::new(),
        })
    }

    /// `load`, falling back to defaults on schema errors. For read-only use (deciding what to show).
    pub fn load_or_default(dir: &Path) -> Self {
        Self::load(dir).unwrap_or_else(|_| Self {
            dir: dir.to_owned(),
            path: dir.join(PRIMARY_FILE),
            doc: Value::Null,
            accounts: Vec::new(),
            loaded_accounts: Vec::new(),
            edited: Vec::new(),
            forgotten_keys: Vec::new(),
        })
    }

    #[cfg(test)]
    fn from_json(json: &str) -> Self {
        let doc: Value = serde_json::from_str(json).unwrap();
        let accounts = accounts_of(&doc).unwrap();
        Self {
            dir: PathBuf::new(),
            path: PathBuf::new(),
            doc,
            loaded_accounts: accounts.clone(),
            accounts,
            edited: Vec::new(),
            forgotten_keys: Vec::new(),
        }
    }

    pub fn accounts(&self) -> &[AccountRecord] {
        &self.accounts
    }

    /// Accounts for one provider, in file order.
    pub fn accounts_for<'a>(&'a self, provider: &'a str) -> impl Iterator<Item = &'a AccountRecord> + 'a {
        self.accounts
            .iter()
            .filter(move |account| account.provider.eq_ignore_ascii_case(provider))
    }

    /// A provider is on while its legacy switch is on (kept in sync with its accounts on save). WPF defaults apply
    /// when the provider is absent: on, except Moonshot.
    pub fn is_enabled(&self, provider: &str) -> bool {
        match provider_entry(&self.doc, provider) {
            Some(Value::Object(entry)) => entry.get("enabled").and_then(Value::as_bool).unwrap_or(true),
            Some(_) => true,
            None => provider != names::MOONSHOT,
        }
    }

    /// A plaintext key still in the file from the WPF app. Read-only: new keys go to Credential Manager.
    pub fn api_key(&self, provider: &str) -> Option<String> {
        provider_entry(&self.doc, provider)
            .and_then(|entry| entry.get("apiKey"))
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|key| !key.is_empty())
            .map(str::to_owned)
    }

    /// Providers whose entry still holds a plaintext `apiKey`, by their canonical name.
    pub fn plaintext_key_providers(&self) -> Vec<&'static str> {
        names::ALL
            .into_iter()
            .filter(|provider| self.api_key(provider).is_some())
            .collect()
    }

    /// Drops a provider's plaintext `apiKey` (after it moved to Credential Manager). The next save removes it from the
    /// file, whatever else changed there since load.
    pub fn forget_api_key(&mut self, provider: &str) {
        remove_api_key(&mut self.doc, provider);
        if !self
            .forgotten_keys
            .iter()
            .any(|known| known.eq_ignore_ascii_case(provider))
        {
            self.forgotten_keys.push(provider.to_owned());
        }
    }

    pub fn opencode_workspace_id(&self) -> Option<String> {
        self.doc
            .get("openCodeGoWorkspaceId")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|id| !id.is_empty())
            .map(str::to_owned)
    }

    /// Copilot usernames selected for fetching; empty means "every gh CLI account" (automatic discovery).
    pub fn copilot_selection(&self) -> Vec<String> {
        self.doc
            .get("copilotAccounts")
            .and_then(Value::as_array)
            .map(|names| {
                names
                    .iter()
                    .filter_map(Value::as_str)
                    .map(str::trim)
                    .filter(|n| !n.is_empty())
                    .map(str::to_owned)
                    .collect()
            })
            .unwrap_or_default()
    }

    /// Refresh interval in seconds; `None` means automatic refresh is off.
    pub fn refresh_interval_secs(&self) -> Option<u64> {
        match self.doc.get("refreshIntervalSeconds").and_then(Value::as_i64) {
            Some(secs) if secs <= 0 => None,
            Some(secs) => Some(secs as u64),
            None => Some(120),
        }
    }

    pub fn set_refresh_interval_secs(&mut self, secs: Option<u64>) {
        let value = secs.map_or(0, |secs| secs as i64);
        self.set_scalar("refreshIntervalSeconds", json!(value));
    }

    /// Interface zoom as a factor (1.0 is 100%), clamped to the shared range. Missing or invalid means 100%.
    pub fn zoom_level(&self) -> f64 {
        self.doc
            .get("zoomLevel")
            .and_then(Value::as_f64)
            .filter(|zoom| zoom.is_finite() && *zoom > 0.0)
            .map_or(1.0, clamp_zoom)
    }

    pub fn set_zoom_level(&mut self, zoom: f64) {
        self.set_scalar("zoomLevel", json!(clamp_zoom(zoom)));
    }

    fn set_scalar(&mut self, key: &'static str, value: Value) {
        object_mut(&mut self.doc).insert(key.into(), value);
        if !self.edited.contains(&key) {
            self.edited.push(key);
        }
    }

    /// Adds or replaces an account by id. An account can't move to another provider.
    pub fn upsert(&mut self, account: AccountRecord) -> Result<(), SettingsError> {
        let account = validate(account)?;
        match self.accounts.iter_mut().find(|existing| existing.id == account.id) {
            Some(existing) if !existing.provider.eq_ignore_ascii_case(&account.provider) => Err(
                SettingsError::InvalidEdit("An account can't change provider. Add a new account instead."),
            ),
            Some(existing) => {
                *existing = account;
                Ok(())
            }
            None => {
                self.accounts.push(account);
                Ok(())
            }
        }
    }

    pub fn remove(&mut self, id: &str) -> bool {
        let before = self.accounts.len();
        self.accounts.retain(|account| account.id != id);
        self.accounts.len() != before
    }

    /// Removes every account (Reset Accounts). Credentials are removed by the caller.
    pub fn clear_accounts(&mut self) {
        self.accounts.clear();
    }

    /// Writes the settings under the shared write lock. Re-reads the file first; refuses to overwrite a newer or
    /// invalid schema, and refuses account edits when another process changed the accounts since load.
    pub fn save(&mut self) -> Result<(), SettingsError> {
        fs::create_dir_all(&self.dir).map_err(io)?;
        let _lock = WriteLock::acquire(&self.dir.join(LOCK_FILE))?;

        let (path, disk) = read_document(&self.dir);
        let disk_accounts = accounts_of(&disk)?;
        let edited = self.accounts != self.loaded_accounts;
        if edited && disk_accounts != self.loaded_accounts {
            return Err(SettingsError::Conflict);
        }
        let accounts = if edited { self.accounts.clone() } else { disk_accounts };

        // Start from the latest disk document so fields another process wrote since load survive; then apply this
        // window's own scalar edits (refresh interval) and the account list.
        let mut doc = if disk.is_object() { disk } else { json!({}) };
        for key in &self.edited {
            if let Some(value) = self.doc.get(*key) {
                object_mut(&mut doc).insert((*key).into(), value.clone());
            }
        }
        apply_accounts(&mut doc, &accounts)?;
        for provider in &self.forgotten_keys {
            remove_api_key(&mut doc, provider);
        }

        let json = serde_json::to_string_pretty(&doc).map_err(|err| SettingsError::Io(err.to_string()))?;
        write_replacing(&path, &json)?;
        self.path = path;
        self.doc = doc;
        self.accounts = accounts.clone();
        self.loaded_accounts = accounts;
        self.edited.clear();
        self.forgotten_keys.clear();
        Ok(())
    }

    pub fn path(&self) -> &Path {
        &self.path
    }
}

/// Clamps to the shared range and rounds to the step, so repeated 0.1 steps don't accumulate float error.
pub fn clamp_zoom(zoom: f64) -> f64 {
    ((zoom.clamp(MIN_ZOOM, MAX_ZOOM)) * 10.0).round() / 10.0
}

fn read_document(dir: &Path) -> (PathBuf, Value) {
    for name in [PRIMARY_FILE, FALLBACK_FILE] {
        let path = dir.join(name);
        if let Ok(text) = fs::read_to_string(&path) {
            let doc = serde_json::from_str(&text).unwrap_or(Value::Null);
            return (path, doc);
        }
    }
    (dir.join(PRIMARY_FILE), Value::Null)
}

/// Accounts as stored (version 1) or as migrated in memory (version 0).
fn accounts_of(doc: &Value) -> Result<Vec<AccountRecord>, SettingsError> {
    if let Some(providers) = doc.get("providers").and_then(Value::as_object) {
        let mut seen = HashSet::new();
        for key in providers.keys() {
            if !seen.insert(key.to_lowercase()) {
                return Err(SettingsError::AmbiguousProviderKeys);
            }
        }
    }
    match doc.get("accountConfigurationVersion") {
        None => Ok(migrate_legacy(doc)),
        Some(version) => match version.as_i64() {
            Some(0) => Ok(migrate_legacy(doc)),
            Some(ACCOUNT_CONFIGURATION_VERSION) => {
                let list = doc
                    .get("accounts")
                    .and_then(Value::as_array)
                    .ok_or(SettingsError::InvalidAccounts)?;
                let accounts = list
                    .iter()
                    .map(AccountRecord::from_json)
                    .collect::<Result<Vec<_>, _>>()?;
                let mut ids = HashSet::new();
                if accounts.iter().any(|account| !ids.insert(account.id.clone())) {
                    return Err(SettingsError::InvalidAccounts);
                }
                Ok(accounts)
            }
            Some(newer) if newer > ACCOUNT_CONFIGURATION_VERSION => Err(SettingsError::NewerVersion),
            _ => Err(SettingsError::NewerVersion),
        },
    }
}

/// Deterministic legacy id, identical to the WPF app's `LegacyId`.
pub fn legacy_id(provider: &str, identity: &str) -> String {
    let digest = Sha256::digest(format!("codexbar-account-v1:{provider}:{}", identity.to_lowercase()).as_bytes());
    digest.iter().take(16).map(|byte| format!("{byte:02x}")).collect()
}

fn migrate_legacy(doc: &Value) -> Vec<AccountRecord> {
    let mut accounts: Vec<AccountRecord> = Vec::new();
    let mut add = |account: AccountRecord| {
        if !accounts.iter().any(|existing| existing.id == account.id) {
            accounts.push(account);
        }
    };
    let providers = doc.get("providers").and_then(Value::as_object);
    for (key, entry) in providers.into_iter().flatten() {
        let Some(provider) = canonical_provider(key) else {
            continue;
        };
        if provider == names::COPILOT {
            continue;
        }
        let enabled = entry.get("enabled").and_then(Value::as_bool).unwrap_or(true);
        let has_key = entry
            .get("apiKey")
            .and_then(Value::as_str)
            .is_some_and(|k| !k.trim().is_empty());
        let method = match (has_key, provider) {
            (false, _) => AuthMethod::Automatic,
            (true, names::OPENCODE_GO) => AuthMethod::BrowserSession,
            (true, _) => AuthMethod::ApiKey,
        };
        let workspace = (provider == names::OPENCODE_GO)
            .then(|| {
                doc.get("openCodeGoWorkspaceId")
                    .and_then(Value::as_str)
                    .map(str::trim)
                    .filter(|s| !s.is_empty())
                    .map(str::to_owned)
            })
            .flatten();
        add(AccountRecord {
            id: legacy_id(provider, ""),
            provider: provider.to_owned(),
            label: provider.to_owned(),
            enabled,
            method,
            external_id: None,
            workspace_id: workspace,
            legacy_card_key: Some(provider.to_owned()),
        });
    }

    // Copilot: one record per known or selected username; unselected ones stay as disabled records.
    let strings = |key: &str| -> Vec<String> {
        doc.get(key)
            .and_then(Value::as_array)
            .map(|list| {
                list.iter()
                    .filter_map(Value::as_str)
                    .map(str::trim)
                    .filter(|s| !s.is_empty())
                    .map(str::to_owned)
                    .collect()
            })
            .unwrap_or_default()
    };
    let selected = strings("copilotAccounts");
    let mut known: Vec<String> = Vec::new();
    for name in strings("copilotKnownAccounts")
        .into_iter()
        .chain(selected.iter().cloned())
    {
        if !known.iter().any(|existing| existing.eq_ignore_ascii_case(&name)) {
            known.push(name);
        }
    }
    let copilot_entry = providers.and_then(|map| map.iter().find(|(key, _)| key.eq_ignore_ascii_case(names::COPILOT)));
    let copilot_on = copilot_entry
        .and_then(|(_, entry)| entry.get("enabled"))
        .and_then(Value::as_bool)
        != Some(false);
    for username in &known {
        let chosen = selected.is_empty() || selected.iter().any(|s| s.eq_ignore_ascii_case(username));
        add(AccountRecord {
            id: legacy_id(names::COPILOT, username),
            provider: names::COPILOT.to_owned(),
            label: format!("Copilot {username}"),
            enabled: copilot_on && chosen,
            method: AuthMethod::CommandLine,
            external_id: Some(username.clone()),
            workspace_id: None,
            legacy_card_key: Some(format!("copilot:{username}")),
        });
    }
    if known.is_empty() && copilot_entry.is_some() {
        add(AccountRecord {
            id: legacy_id(names::COPILOT, ""),
            provider: names::COPILOT.to_owned(),
            label: "Copilot".to_owned(),
            enabled: copilot_on,
            method: AuthMethod::CommandLine,
            external_id: None,
            workspace_id: None,
            legacy_card_key: Some("Copilot".to_owned()),
        });
    }
    accounts
}

fn validate(mut account: AccountRecord) -> Result<AccountRecord, SettingsError> {
    account.id = account.id.trim().to_owned();
    account.label = account.label.trim().to_owned();
    if account.id.is_empty() || account.label.is_empty() {
        return Err(SettingsError::InvalidEdit("Every account needs a name."));
    }
    let provider = canonical_provider(&account.provider).ok_or(SettingsError::InvalidEdit("Unknown provider."))?;
    account.provider = provider.to_owned();
    let trim = |value: Option<String>| value.map(|v| v.trim().to_owned()).filter(|v| !v.is_empty());
    account.external_id = trim(account.external_id);
    account.workspace_id = trim(account.workspace_id);
    Ok(account)
}

/// Writes accounts and keeps the WPF app's legacy fields consistent with them.
fn apply_accounts(doc: &mut Value, accounts: &[AccountRecord]) -> Result<(), SettingsError> {
    let root = object_mut(doc);
    root.insert(
        "accountConfigurationVersion".into(),
        json!(ACCOUNT_CONFIGURATION_VERSION),
    );
    root.insert(
        "accounts".into(),
        Value::Array(accounts.iter().map(AccountRecord::to_json).collect()),
    );

    // A provider with records is visible only while one of them is enabled.
    let providers = root.entry("providers").or_insert_with(|| json!({}));
    let providers = providers.as_object_mut().ok_or(SettingsError::InvalidAccounts)?;
    for provider in names::ALL {
        let records: Vec<&AccountRecord> = accounts.iter().filter(|a| a.provider == provider).collect();
        if records.is_empty() {
            continue;
        }
        let on = records.iter().any(|a| a.enabled);
        let key = providers
            .keys()
            .find(|k| k.eq_ignore_ascii_case(provider))
            .cloned()
            .unwrap_or_else(|| provider.to_owned());
        let entry = providers.entry(key).or_insert_with(|| json!({}));
        if !entry.is_object() {
            *entry = json!({});
        }
        object_mut(entry).insert("enabled".into(), json!(on));
    }

    // Copilot: enabled usernames are the explicit selection; all usernames are known.
    let copilot: Vec<&AccountRecord> = accounts.iter().filter(|a| a.provider == names::COPILOT).collect();
    let usernames: Vec<&str> = copilot.iter().filter_map(|a| a.external_id.as_deref()).collect();
    if !usernames.is_empty() {
        let selected: Vec<&str> = copilot
            .iter()
            .filter(|a| a.enabled)
            .filter_map(|a| a.external_id.as_deref())
            .collect();
        root.insert("copilotKnownAccounts".into(), json!(usernames));
        root.insert("copilotAccounts".into(), json!(selected));
    }

    // OpenCode Go: the account's workspace is the legacy workspace setting.
    if let Some(workspace) = accounts
        .iter()
        .filter(|a| a.provider == names::OPENCODE_GO)
        .find_map(|a| a.workspace_id.as_deref())
    {
        root.insert("openCodeGoWorkspaceId".into(), json!(workspace));
    }
    Ok(())
}

fn provider_entry<'a>(doc: &'a Value, provider: &str) -> Option<&'a Value> {
    doc.get("providers")?
        .as_object()?
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(provider))
        .map(|(_, v)| v)
}

/// Removes `providers.<provider>.apiKey`, matching the provider name case-insensitively like `provider_entry`.
fn remove_api_key(doc: &mut Value, provider: &str) {
    let Some(providers) = doc.get_mut("providers").and_then(Value::as_object_mut) else {
        return;
    };
    for (key, entry) in providers.iter_mut() {
        if key.eq_ignore_ascii_case(provider)
            && let Some(entry) = entry.as_object_mut()
        {
            entry.remove("apiKey");
        }
    }
}

/// The outcome of moving plaintext keys out of the settings file (#74).
#[derive(Debug, Default, PartialEq, Eq)]
pub struct KeyMigration {
    /// Providers whose key now lives only in Credential Manager.
    pub moved: Vec<&'static str>,
    /// Providers whose key stayed in the file, with why. Retried at the next start.
    pub failed: Vec<(&'static str, String)>,
}

/// Moves every plaintext `apiKey` in the file to Credential Manager, under the account that uses it today (the
/// provider's first account, or its implicit one when it has none), then removes it from the file.
///
/// Nothing is lost on failure: a key leaves the file only after Credential Manager returns it, and if the save fails the
/// file keeps every key (the copies in Credential Manager are used first, so a retry is harmless). A key Credential
/// Manager already holds for that account is the one in use, so the stale plaintext copy is simply removed.
pub fn move_plaintext_keys(settings: &mut Settings, store: &dyn crate::credentials::CredentialStore) -> KeyMigration {
    let mut outcome = KeyMigration::default();
    for provider in settings.plaintext_key_providers() {
        let Some(key) = settings.api_key(provider) else {
            continue;
        };
        let account = settings
            .accounts_for(provider)
            .next()
            .map_or_else(|| legacy_id(provider, ""), |account| account.id.clone());
        let stored = match store.read(&account) {
            Ok(Some(_)) => Ok(()),
            Ok(None) => store.write(&account, &key).and_then(|()| match store.read(&account) {
                Ok(Some(back)) if back == key => Ok(()),
                Ok(_) => Err(crate::credentials::CredentialError::verification()),
                Err(err) => Err(err),
            }),
            Err(err) => Err(err),
        };
        match stored {
            Ok(()) => {
                settings.forget_api_key(provider);
                outcome.moved.push(provider);
            }
            Err(err) => outcome.failed.push((provider, err.to_string())),
        }
    }
    if !outcome.moved.is_empty()
        && let Err(err) = settings.save()
    {
        // The file keeps its keys; Credential Manager's copies are used meanwhile, and the next start tries again.
        let moved = std::mem::take(&mut outcome.moved);
        outcome
            .failed
            .extend(moved.into_iter().map(|provider| (provider, err.to_string())));
    }
    outcome
}

fn object_mut(value: &mut Value) -> &mut Map<String, Value> {
    if !value.is_object() {
        *value = json!({});
    }
    value.as_object_mut().expect("just made an object")
}

fn io(err: std::io::Error) -> SettingsError {
    SettingsError::Io(err.to_string())
}

/// Temp file plus rename, so a crash never leaves a torn settings file.
fn write_replacing(path: &Path, contents: &str) -> Result<(), SettingsError> {
    let tmp = PathBuf::from(format!("{}.tmp", path.display()));
    fs::write(&tmp, contents).map_err(io)?;
    fs::rename(&tmp, path).map_err(|err| {
        let _ = fs::remove_file(&tmp);
        io(err)
    })
}

/// The exclusive lock every settings writer holds; a competing writer reports Busy instead of blocking the UI.
struct WriteLock {
    _lock: crate::lock::FileLock,
}

impl WriteLock {
    fn acquire(path: &Path) -> Result<Self, SettingsError> {
        match crate::lock::FileLock::acquire(path) {
            Ok(lock) => Ok(Self { _lock: lock }),
            Err(err) if err.kind() == std::io::ErrorKind::WouldBlock => Err(SettingsError::Busy),
            Err(err) => Err(io(err)),
        }
    }
}

fn random_id() -> String {
    use std::hash::{BuildHasher, Hasher};
    // 128 random bits from two independently seeded SipHash states plus time, rendered like Guid "N".
    let mut bits = [0u64; 2];
    for (ix, slot) in bits.iter_mut().enumerate() {
        let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
        hasher.write_u128(
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos(),
        );
        hasher.write_usize(ix);
        *slot = hasher.finish();
    }
    format!("{:016x}{:016x}", bits[0], bits[1])
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicU32, Ordering};

    use super::*;

    static COUNTER: AtomicU32 = AtomicU32::new(0);

    struct TempDir(PathBuf);

    impl TempDir {
        fn with(file: &str, contents: &str) -> Self {
            let n = COUNTER.fetch_add(1, Ordering::Relaxed);
            let dir = std::env::temp_dir().join(format!("codexbar-settings-{}-{n}", std::process::id()));
            let _ = fs::remove_dir_all(&dir);
            fs::create_dir_all(&dir).unwrap();
            if !contents.is_empty() {
                fs::write(dir.join(file), contents).unwrap();
            }
            Self(dir)
        }

        fn read(&self, file: &str) -> Value {
            serde_json::from_str(&fs::read_to_string(self.0.join(file)).unwrap()).unwrap()
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    const LEGACY: &str = r#"{
        "refreshIntervalSeconds": 120,
        "openCodeGoWorkspaceId": " wrk_123 ",
        "copilotAccounts": ["HemSoft"],
        "copilotKnownAccounts": ["HemSoft", "fhemmerrelias"],
        "providerCardOrder": ["Codex", "Claude"],
        "zoomLevel": 1.25,
        "providers": {
            "Cursor": { "enabled": false },
            "OpenCodeGo": { "enabled": true, "apiKey": "cookie" },
            "OpenRouter": { "enabled": true, "apiKey": "" },
            "Copilot": { "enabled": true },
            "Codex": null
        }
    }"#;

    #[test]
    fn legacy_id_matches_wpf_algorithm() {
        // Values computed with .NET's SHA256 exactly as the WPF app's AccountConfiguration.LegacyId does.
        assert_eq!(legacy_id("Copilot", "HemSoft"), "1116feb81dac5c86cd94af70bbe14777");
        assert_eq!(legacy_id("Codex", ""), "c82a6320d72329f92f2c36f3bd39c986");
    }

    #[test]
    fn migrate_legacy_creates_wpf_compatible_records() {
        let settings = Settings::from_json(LEGACY);
        let accounts = settings.accounts();
        let find = |provider: &str| accounts.iter().find(|a| a.provider == provider).unwrap();
        assert_eq!(find("OpenCodeGo").method, AuthMethod::BrowserSession);
        assert_eq!(find("OpenCodeGo").workspace_id.as_deref(), Some("wrk_123"));
        assert_eq!(
            find("OpenRouter").method,
            AuthMethod::Automatic,
            "blank key is automatic"
        );
        assert!(!find("Cursor").enabled);
        let copilot: Vec<_> = accounts.iter().filter(|a| a.provider == "Copilot").collect();
        assert_eq!(copilot.len(), 2);
        assert!(
            copilot
                .iter()
                .any(|a| a.external_id.as_deref() == Some("HemSoft") && a.enabled)
        );
        assert!(
            copilot
                .iter()
                .any(|a| a.external_id.as_deref() == Some("fhemmerrelias") && !a.enabled)
        );
    }

    #[test]
    fn load_newer_version_refuses_and_save_never_overwrites() {
        let dir = TempDir::with(PRIMARY_FILE, r#"{"accountConfigurationVersion":2,"accounts":[]}"#);
        assert_eq!(Settings::load(&dir.0).unwrap_err(), SettingsError::NewerVersion);
    }

    #[test]
    fn load_versioned_without_accounts_array_is_invalid() {
        let dir = TempDir::with(PRIMARY_FILE, r#"{"accountConfigurationVersion":1}"#);
        assert_eq!(Settings::load(&dir.0).unwrap_err(), SettingsError::InvalidAccounts);
    }

    #[test]
    fn load_case_variant_provider_keys_is_ambiguous() {
        let dir = TempDir::with(PRIMARY_FILE, r#"{"providers":{"Codex":{},"codex":{}}}"#);
        assert_eq!(
            Settings::load(&dir.0).unwrap_err(),
            SettingsError::AmbiguousProviderKeys
        );
    }

    #[test]
    fn save_round_trips_unknown_fields_and_syncs_legacy_fields() {
        let dir = TempDir::with(FALLBACK_FILE, LEGACY);
        let mut settings = Settings::load(&dir.0).unwrap();
        let cursor = settings
            .accounts()
            .iter()
            .find(|a| a.provider == "Cursor")
            .unwrap()
            .clone();
        settings
            .upsert(AccountRecord {
                enabled: true,
                ..cursor
            })
            .unwrap();
        settings.save().unwrap();

        let saved = dir.read(FALLBACK_FILE);
        assert_eq!(saved["accountConfigurationVersion"], 1);
        assert_eq!(saved["zoomLevel"], 1.25, "fields this app doesn't own survive");
        assert_eq!(saved["providerCardOrder"], json!(["Codex", "Claude"]));
        assert_eq!(
            saved["providers"]["OpenCodeGo"]["apiKey"], "cookie",
            "plaintext keys stay until cutover"
        );
        assert_eq!(
            saved["providers"]["Cursor"]["enabled"], true,
            "provider switch follows its accounts"
        );
        assert_eq!(saved["copilotAccounts"], json!(["HemSoft"]));
        assert_eq!(saved["openCodeGoWorkspaceId"], "wrk_123");
        // The saved file reloads to the same accounts.
        assert_eq!(Settings::load(&dir.0).unwrap().accounts(), settings.accounts());
    }

    #[test]
    fn save_refuses_account_edits_when_disk_changed_since_load() {
        let dir = TempDir::with(PRIMARY_FILE, LEGACY);
        let mut mine = Settings::load(&dir.0).unwrap();
        let mut theirs = Settings::load(&dir.0).unwrap();
        theirs
            .upsert(AccountRecord::new("Claude", "Work", AuthMethod::OAuth))
            .unwrap();
        theirs.save().unwrap();

        mine.upsert(AccountRecord::new("Codex", "Second", AuthMethod::Automatic))
            .unwrap();
        assert_eq!(mine.save().unwrap_err(), SettingsError::Conflict);
        // An unrelated save (no account edits) still succeeds and keeps the other process's accounts.
        let mut unrelated = Settings::load(&dir.0).unwrap();
        unrelated.set_refresh_interval_secs(Some(300));
        unrelated.save().unwrap();
        assert!(
            Settings::load(&dir.0)
                .unwrap()
                .accounts()
                .iter()
                .any(|a| a.label == "Work")
        );
    }

    #[test]
    fn save_while_another_writer_holds_lock_is_busy() {
        let dir = TempDir::with(PRIMARY_FILE, LEGACY);
        let _held = WriteLock::acquire(&dir.0.join(LOCK_FILE)).unwrap();
        let mut settings = Settings::load(&dir.0).unwrap();
        settings.set_refresh_interval_secs(None);
        assert_eq!(settings.save().unwrap_err(), SettingsError::Busy);
    }

    #[test]
    fn upsert_rejects_provider_change_and_blank_label() {
        let mut settings = Settings::from_json(LEGACY);
        let existing = settings.accounts()[0].clone();
        let moved = AccountRecord {
            provider: if existing.provider == "Claude" {
                "Codex".into()
            } else {
                "Claude".into()
            },
            ..existing.clone()
        };
        assert!(matches!(settings.upsert(moved), Err(SettingsError::InvalidEdit(_))));
        assert!(matches!(
            settings.upsert(AccountRecord {
                label: "  ".into(),
                ..existing
            }),
            Err(SettingsError::InvalidEdit(_))
        ));
    }

    #[test]
    fn remove_and_disable_all_accounts_turns_provider_off() {
        let dir = TempDir::with(PRIMARY_FILE, LEGACY);
        let mut settings = Settings::load(&dir.0).unwrap();
        let ids: Vec<String> = settings.accounts_for("Copilot").map(|a| a.id.clone()).collect();
        for id in &ids {
            let record = settings.accounts().iter().find(|a| &a.id == id).unwrap().clone();
            settings
                .upsert(AccountRecord {
                    enabled: false,
                    ..record
                })
                .unwrap();
        }
        settings.save().unwrap();
        assert_eq!(dir.read(PRIMARY_FILE)["providers"]["Copilot"]["enabled"], false);
        assert!(!Settings::load(&dir.0).unwrap().is_enabled("Copilot"));
    }

    #[test]
    fn zoom_level_defaults_clamps_and_rounds() {
        assert_eq!(Settings::from_json("{}").zoom_level(), 1.0);
        assert_eq!(Settings::from_json(r#"{"zoomLevel":1.25}"#).zoom_level(), 1.3);
        assert_eq!(Settings::from_json(r#"{"zoomLevel":9}"#).zoom_level(), 3.0);
        assert_eq!(Settings::from_json(r#"{"zoomLevel":0.1}"#).zoom_level(), 0.5);
        assert_eq!(Settings::from_json(r#"{"zoomLevel":"big"}"#).zoom_level(), 1.0);
        let mut settings = Settings::from_json("{}");
        settings.set_zoom_level(0.1 + 0.2 + 1.0);
        assert_eq!(settings.zoom_level(), 1.3);
    }

    #[test]
    fn save_zoom_round_trips_and_keeps_other_writers_values() {
        let dir = TempDir::with(PRIMARY_FILE, LEGACY);
        let mut mine = Settings::load(&dir.0).unwrap();
        // Another process (the WPF app) changes the refresh interval after this copy loaded.
        let mut wpf = dir.read(PRIMARY_FILE);
        wpf["refreshIntervalSeconds"] = json!(900);
        fs::write(dir.0.join(PRIMARY_FILE), serde_json::to_string(&wpf).unwrap()).unwrap();

        mine.set_zoom_level(1.5);
        mine.save().unwrap();
        let saved = dir.read(PRIMARY_FILE);
        assert_eq!(saved["zoomLevel"], 1.5);
        assert_eq!(
            saved["refreshIntervalSeconds"], 900,
            "an untouched setting keeps the other writer's value"
        );
        assert_eq!(Settings::load(&dir.0).unwrap().zoom_level(), 1.5);
    }

    #[test]
    fn refresh_interval_off_and_default() {
        let mut settings = Settings::from_json("{}");
        assert_eq!(settings.refresh_interval_secs(), Some(120));
        settings.set_refresh_interval_secs(None);
        assert_eq!(settings.refresh_interval_secs(), None);
    }

    #[test]
    fn load_missing_file_applies_wpf_defaults() {
        let dir = TempDir::with(PRIMARY_FILE, "");
        let settings = Settings::load(&dir.0).unwrap();
        assert!(settings.is_enabled(names::CODEX));
        assert!(!settings.is_enabled(names::MOONSHOT));
        assert!(settings.accounts().is_empty());
    }

    #[test]
    fn random_ids_are_distinct_32_hex() {
        let a = random_id();
        let b = random_id();
        assert_eq!(a.len(), 32);
        assert!(a.chars().all(|c| c.is_ascii_hexdigit()));
        assert_ne!(a, b);
    }

    mod key_migration {
        use super::*;
        use crate::credentials::{CredentialError, CredentialStore, MemoryCredentialStore};

        const WITH_KEYS: &str = r#"{
            "accountConfigurationVersion": 1,
            "accounts": [
                { "id": "acct-or", "providerId": "OpenRouter", "displayLabel": "Work", "enabled": true,
                  "authenticationMethod": "ApiKey" }
            ],
            "providers": {
                "OpenRouter": { "enabled": true, "apiKey": "sk-or-plain" },
                "opencodezen": { "enabled": true, "apiKey": "zen-cookie-plain" },
                "Cursor": { "enabled": true }
            },
            "zoomLevel": 1.2
        }"#;

        fn file_text(dir: &TempDir) -> String {
            fs::read_to_string(dir.0.join(PRIMARY_FILE)).unwrap()
        }

        #[test]
        fn keys_move_to_their_accounts_and_leave_the_file() {
            let dir = TempDir::with(PRIMARY_FILE, WITH_KEYS);
            let store = MemoryCredentialStore::default();
            let mut settings = Settings::load(&dir.0).unwrap();
            assert_eq!(
                settings.plaintext_key_providers(),
                [names::OPENROUTER, names::OPENCODE_ZEN]
            );
            let outcome = move_plaintext_keys(&mut settings, &store);
            assert_eq!(outcome.moved, [names::OPENROUTER, names::OPENCODE_ZEN]);
            assert!(outcome.failed.is_empty());
            // Under the configured account, or the implicit one for a provider without records.
            assert_eq!(store.read("acct-or").unwrap().as_deref(), Some("sk-or-plain"));
            let implicit = legacy_id(names::OPENCODE_ZEN, "");
            assert_eq!(store.read(&implicit).unwrap().as_deref(), Some("zen-cookie-plain"));
            let text = file_text(&dir);
            assert!(
                !text.contains("sk-or-plain") && !text.contains("zen-cookie-plain"),
                "no secret left in the file"
            );
            assert!(!text.contains("apiKey"));
            // Everything else is kept.
            let saved = dir.read(PRIMARY_FILE);
            assert_eq!(saved["zoomLevel"], json!(1.2));
            assert_eq!(saved["providers"]["Cursor"]["enabled"], json!(true));
            assert_eq!(saved["accounts"][0]["id"], json!("acct-or"));
            // Done once: a second run finds nothing to move.
            let mut again = Settings::load(&dir.0).unwrap();
            assert_eq!(move_plaintext_keys(&mut again, &store), KeyMigration::default());
        }

        #[test]
        fn a_failing_store_keeps_every_key_in_the_file() {
            let dir = TempDir::with(PRIMARY_FILE, WITH_KEYS);
            let mut settings = Settings::load(&dir.0).unwrap();
            let outcome = move_plaintext_keys(&mut settings, &MemoryCredentialStore::failing());
            assert!(outcome.moved.is_empty());
            assert_eq!(outcome.failed.len(), 2);
            assert!(
                outcome.failed.iter().all(|(_, why)| !why.contains("plain")),
                "errors never carry the secret"
            );
            let text = file_text(&dir);
            assert!(text.contains("sk-or-plain") && text.contains("zen-cookie-plain"));
            assert_eq!(
                settings.api_key(names::OPENROUTER).as_deref(),
                Some("sk-or-plain"),
                "still usable"
            );
        }

        /// Accepts writes but hands back something else, like a store that silently truncates.
        struct Garbling;

        impl CredentialStore for Garbling {
            fn read(&self, _: &str) -> Result<Option<String>, CredentialError> {
                Ok(None)
            }

            fn write(&self, _: &str, _: &str) -> Result<(), CredentialError> {
                Ok(())
            }

            fn delete(&self, _: &str) -> Result<(), CredentialError> {
                Ok(())
            }
        }

        #[test]
        fn a_key_that_does_not_read_back_stays_in_the_file() {
            let dir = TempDir::with(PRIMARY_FILE, WITH_KEYS);
            let mut settings = Settings::load(&dir.0).unwrap();
            let outcome = move_plaintext_keys(&mut settings, &Garbling);
            assert!(outcome.moved.is_empty());
            assert_eq!(
                outcome.failed[0].1,
                "Windows Credential Manager didn't return the saved secret."
            );
            assert!(file_text(&dir).contains("sk-or-plain"));
        }

        #[test]
        fn a_key_already_in_credential_manager_wins_and_the_plaintext_goes() {
            let dir = TempDir::with(PRIMARY_FILE, WITH_KEYS);
            let store = MemoryCredentialStore::default();
            store.write("acct-or", "sk-or-newer").unwrap();
            let mut settings = Settings::load(&dir.0).unwrap();
            let outcome = move_plaintext_keys(&mut settings, &store);
            assert!(outcome.moved.contains(&names::OPENROUTER));
            assert_eq!(
                store.read("acct-or").unwrap().as_deref(),
                Some("sk-or-newer"),
                "never overwritten"
            );
            assert!(!file_text(&dir).contains("sk-or-plain"));
        }

        #[test]
        fn a_busy_file_keeps_its_keys_for_the_next_start() {
            let dir = TempDir::with(PRIMARY_FILE, WITH_KEYS);
            let store = MemoryCredentialStore::default();
            let mut settings = Settings::load(&dir.0).unwrap();
            let _held = crate::lock::FileLock::acquire(&dir.0.join(LOCK_FILE)).unwrap();
            let outcome = move_plaintext_keys(&mut settings, &store);
            assert!(
                outcome.moved.is_empty(),
                "nothing counts as moved until the file is saved"
            );
            assert_eq!(outcome.failed.len(), 2);
            assert!(file_text(&dir).contains("sk-or-plain"));
            // The copies in Credential Manager are already in use; the retry completes it.
            drop(_held);
            let mut retry = Settings::load(&dir.0).unwrap();
            assert_eq!(move_plaintext_keys(&mut retry, &store).moved.len(), 2);
            assert!(!file_text(&dir).contains("sk-or-plain"));
        }

        #[test]
        fn a_removal_survives_another_writer_saving_in_between() {
            let dir = TempDir::with(PRIMARY_FILE, WITH_KEYS);
            let mut mine = Settings::load(&dir.0).unwrap();
            let mut theirs = Settings::load(&dir.0).unwrap();
            theirs.set_zoom_level(2.0);
            theirs.save().unwrap();
            mine.forget_api_key(names::OPENROUTER);
            mine.save().unwrap();
            let saved = dir.read(PRIMARY_FILE);
            assert_eq!(saved["zoomLevel"], json!(2.0), "their change kept");
            assert!(
                saved["providers"]["OpenRouter"].get("apiKey").is_none(),
                "my removal applied"
            );
        }
    }
}
