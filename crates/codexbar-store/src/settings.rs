//! Read-only view of the WPF app's `~/.codexbar/settings.json` (or the older `codexbar-settings.json`), so the Rust
//! app honors the same provider switches and keys during the migration. This module never writes the file; account
//! configuration with migration is #73, and moving secrets to Credential Manager is #74.

use std::path::{Path, PathBuf};

use serde_json::Value;

/// Provider names as the WPF app stores them under `providers`.
pub mod names {
    pub const CODEX: &str = "Codex";
    pub const COPILOT: &str = "Copilot";
    pub const CLAUDE: &str = "Claude";
    pub const CURSOR: &str = "Cursor";
    pub const OPENROUTER: &str = "OpenRouter";
    pub const OPENCODE_GO: &str = "OpenCodeGo";
    pub const OPENCODE_ZEN: &str = "OpenCodeZen";
    pub const MOONSHOT: &str = "Moonshot";
}

#[derive(Clone, Debug, Default)]
pub struct LegacySettings {
    json: Value,
}

impl LegacySettings {
    /// Loads settings from `dir`; a missing or unreadable file behaves like an empty one (every default applies).
    pub fn load(dir: &Path) -> Self {
        let json = ["settings.json", "codexbar-settings.json"]
            .iter()
            .find_map(|name| std::fs::read_to_string(dir.join(name)).ok())
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or(Value::Null);
        Self { json }
    }

    pub fn default_dir() -> PathBuf {
        std::env::var_os("USERPROFILE")
            .map(PathBuf::from)
            .unwrap_or_default()
            .join(".codexbar")
    }

    #[cfg(test)]
    fn from_json(json: &str) -> Self {
        Self {
            json: serde_json::from_str(json).unwrap(),
        }
    }

    /// Same defaults as the WPF app: providers are on unless switched off, except Moonshot, which needs opting in.
    pub fn is_enabled(&self, provider: &str) -> bool {
        match self.json.pointer(&format!("/providers/{provider}")) {
            Some(Value::Object(entry)) => entry.get("enabled").and_then(Value::as_bool).unwrap_or(true),
            Some(_) => true,
            None => provider != names::MOONSHOT,
        }
    }

    /// The stored key (an API key, or a dashboard cookie for OpenCode), trimmed, if any.
    pub fn api_key(&self, provider: &str) -> Option<String> {
        self.json
            .pointer(&format!("/providers/{provider}/apiKey"))
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|key| !key.is_empty())
            .map(str::to_owned)
    }

    pub fn opencode_workspace_id(&self) -> Option<String> {
        self.json
            .get("openCodeGoWorkspaceId")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|id| !id.is_empty())
            .map(str::to_owned)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SETTINGS: &str = r#"{
        "openCodeGoWorkspaceId": " wrk_123 ",
        "providers": {
            "Cursor": { "enabled": false },
            "OpenCodeZen": { "enabled": true, "apiKey": " cookie-value " },
            "OpenRouter": { "enabled": true, "apiKey": "" },
            "Codex": null
        }
    }"#;

    #[test]
    fn is_enabled_uses_flags_and_wpf_defaults() {
        let settings = LegacySettings::from_json(SETTINGS);
        assert!(!settings.is_enabled(names::CURSOR));
        assert!(settings.is_enabled(names::OPENCODE_ZEN));
        assert!(settings.is_enabled(names::CODEX), "null entry means enabled");
        assert!(settings.is_enabled(names::CLAUDE), "missing entry means enabled");
        assert!(!settings.is_enabled(names::MOONSHOT), "Moonshot is opt-in");
    }

    #[test]
    fn api_key_trims_and_ignores_blank() {
        let settings = LegacySettings::from_json(SETTINGS);
        assert_eq!(settings.api_key(names::OPENCODE_ZEN).as_deref(), Some("cookie-value"));
        assert_eq!(settings.api_key(names::OPENROUTER), None);
        assert_eq!(settings.opencode_workspace_id().as_deref(), Some("wrk_123"));
    }

    #[test]
    fn load_missing_file_applies_defaults() {
        let settings = LegacySettings::load(Path::new("Z:/definitely/not/here"));
        assert!(settings.is_enabled(names::CODEX));
        assert!(!settings.is_enabled(names::MOONSHOT));
        assert_eq!(settings.api_key(names::OPENROUTER), None);
    }
}
