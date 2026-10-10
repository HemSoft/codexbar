//! The Windows widget snapshot (#94): `widgets.json` next to the settings. The dashboard writes it after every
//! refresh and layout change; the widget provider (a separate, packaged process) only reads it.
//!
//! It holds display-ready usage and nothing else: account ids, provider keys, names, group names, formatted values,
//! percentages and times. Never keys, tokens, cookies or the paths to them. Written to a temp file and swapped in.
//!
//! Compatibility: readers accept any file whose `version` is at most theirs and ignore fields they don't know, so a
//! newer CodexBar can add fields without breaking an older provider. A breaking change raises `version`; an older
//! provider then reports the snapshot as unavailable rather than misreading it.

use std::io;
use std::path::Path;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

pub const WIDGETS_FILE: &str = "widgets.json";
pub const WIDGETS_VERSION: u32 = 1;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WidgetSnapshot {
    pub version: u32,
    /// When the dashboard wrote the file; a widget reads an old one as CodexBar not running.
    pub generated_at: DateTime<Utc>,
    /// Groups in dashboard order.
    #[serde(default)]
    pub groups: Vec<WidgetGroup>,
    /// Accounts in dashboard order: by group, then the table's order within it.
    #[serde(default)]
    pub accounts: Vec<WidgetAccount>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct WidgetGroup {
    pub id: String,
    pub name: String,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum WidgetHealth {
    /// From the latest refresh.
    #[default]
    Fresh,
    /// Last known usage: the latest refresh failed, or this run hasn't refreshed yet.
    Stale,
    /// Nothing to show yet.
    Unavailable,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WidgetAccount {
    pub id: String,
    /// The provider key (`claude`, `codex`, ...).
    pub provider: String,
    pub provider_name: String,
    /// The name the dashboard shows.
    pub name: String,
    /// The group id, if the account is in one.
    #[serde(default)]
    pub group: Option<String>,
    #[serde(default)]
    pub health: WidgetHealth,
    /// When the shown usage was fetched.
    #[serde(default)]
    pub updated_at: Option<DateTime<Utc>>,
    /// The status label ("Watch", "At risk", "Limit soon"); none for normal accounts.
    #[serde(default)]
    pub status: Option<String>,
    /// Limits and balances, the primary one first.
    #[serde(default)]
    pub metrics: Vec<WidgetMetric>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WidgetMetric {
    /// Stable within the account (`weekly`, `credits`).
    pub key: String,
    pub label: String,
    /// What the dashboard shows for it: "42% used", "$12.30 left", "$4.10 spent".
    pub value: String,
    /// 0 to 100 for limits; none for balances and uncapped spend.
    #[serde(default)]
    pub used_percent: Option<f64>,
    #[serde(default)]
    pub resets_at: Option<DateTime<Utc>>,
}

impl WidgetSnapshot {
    pub fn new(generated_at: DateTime<Utc>, groups: Vec<WidgetGroup>, accounts: Vec<WidgetAccount>) -> Self {
        Self {
            version: WIDGETS_VERSION,
            generated_at,
            groups,
            accounts,
        }
    }
}

/// Why there is no snapshot to show.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum LoadError {
    /// CodexBar hasn't written one yet.
    Missing,
    /// Written by a newer CodexBar with a format this one can't read.
    Newer,
    /// Unreadable or not a snapshot.
    Invalid,
}

pub fn load_widget_snapshot(dir: &Path) -> Result<WidgetSnapshot, LoadError> {
    let text = match std::fs::read_to_string(dir.join(WIDGETS_FILE)) {
        Ok(text) => text,
        Err(err) if err.kind() == io::ErrorKind::NotFound => return Err(LoadError::Missing),
        Err(_) => return Err(LoadError::Invalid),
    };
    let value: serde_json::Value = serde_json::from_str(&text).map_err(|_| LoadError::Invalid)?;
    match value.get("version").and_then(serde_json::Value::as_u64) {
        Some(version) if version > u64::from(WIDGETS_VERSION) => return Err(LoadError::Newer),
        Some(_) => {}
        None => return Err(LoadError::Invalid),
    }
    serde_json::from_value(value).map_err(|_| LoadError::Invalid)
}

/// Replaces `widgets.json` atomically. A file from a newer CodexBar is left alone.
pub fn save_widget_snapshot(dir: &Path, snapshot: &WidgetSnapshot) -> io::Result<()> {
    if load_widget_snapshot(dir) == Err(LoadError::Newer) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "widgets.json is from a newer CodexBar and was not changed",
        ));
    }
    let text = serde_json::to_string_pretty(snapshot).map_err(io::Error::other)?;
    std::fs::create_dir_all(dir)?;
    let path = dir.join(WIDGETS_FILE);
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, text)?;
    std::fs::rename(&tmp, &path)
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TempDir(std::path::PathBuf);

    impl TempDir {
        fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!("codexbar-widgets-{}-{name}", std::process::id()));
            let _ = std::fs::remove_dir_all(&dir);
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }

        fn write(&self, text: &str) {
            std::fs::write(self.0.join(WIDGETS_FILE), text).unwrap();
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn at(text: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(text).unwrap().with_timezone(&Utc)
    }

    fn sample() -> WidgetSnapshot {
        WidgetSnapshot::new(
            at("2026-10-10T12:00:00Z"),
            vec![WidgetGroup {
                id: "g1".into(),
                name: "Work".into(),
            }],
            vec![WidgetAccount {
                id: "claude-abc".into(),
                provider: "claude".into(),
                provider_name: "Claude".into(),
                name: "Claude · Work".into(),
                group: Some("g1".into()),
                health: WidgetHealth::Stale,
                updated_at: Some(at("2026-10-10T11:58:00Z")),
                status: Some("At risk".into()),
                metrics: vec![WidgetMetric {
                    key: "weekly".into(),
                    label: "Weekly".into(),
                    value: "82% used".into(),
                    used_percent: Some(82.0),
                    resets_at: Some(at("2026-10-12T00:00:00Z")),
                }],
            }],
        )
    }

    #[test]
    fn a_saved_snapshot_reads_back() {
        let dir = TempDir::new("roundtrip");
        save_widget_snapshot(&dir.0, &sample()).unwrap();
        assert_eq!(load_widget_snapshot(&dir.0), Ok(sample()));
        assert!(!dir.0.join("widgets.json.tmp").exists());
    }

    #[test]
    fn the_file_format_is_pinned() {
        // The provider of an older CodexBar reads this; renaming a field breaks it.
        let text = serde_json::to_value(sample()).unwrap();
        let expected = serde_json::json!({
            "version": 1,
            "generatedAt": "2026-10-10T12:00:00Z",
            "groups": [{"id": "g1", "name": "Work"}],
            "accounts": [{
                "id": "claude-abc",
                "provider": "claude",
                "providerName": "Claude",
                "name": "Claude · Work",
                "group": "g1",
                "health": "stale",
                "updatedAt": "2026-10-10T11:58:00Z",
                "status": "At risk",
                "metrics": [{
                    "key": "weekly",
                    "label": "Weekly",
                    "value": "82% used",
                    "usedPercent": 82.0,
                    "resetsAt": "2026-10-12T00:00:00Z"
                }]
            }]
        });
        assert_eq!(text, expected);
    }

    #[test]
    fn unknown_fields_from_a_newer_minor_change_are_ignored() {
        let dir = TempDir::new("unknown");
        dir.write(
            r#"{"version":1,"generatedAt":"2026-10-10T12:00:00Z","theme":"gold",
            "accounts":[{"id":"a","provider":"codex","providerName":"Codex","name":"Codex","colour":"green"}]}"#,
        );
        let snapshot = load_widget_snapshot(&dir.0).unwrap();
        assert_eq!(snapshot.accounts.len(), 1);
        assert_eq!(
            snapshot.accounts[0].health,
            WidgetHealth::Fresh,
            "missing fields take defaults"
        );
        assert!(snapshot.groups.is_empty());
    }

    #[test]
    fn missing_newer_and_broken_files_are_told_apart() {
        let dir = TempDir::new("errors");
        assert_eq!(load_widget_snapshot(&dir.0), Err(LoadError::Missing));
        dir.write(r#"{"version":2,"generatedAt":"2026-10-10T12:00:00Z"}"#);
        assert_eq!(load_widget_snapshot(&dir.0), Err(LoadError::Newer));
        dir.write("not json");
        assert_eq!(load_widget_snapshot(&dir.0), Err(LoadError::Invalid));
        dir.write(r#"{"generatedAt":"2026-10-10T12:00:00Z"}"#);
        assert_eq!(load_widget_snapshot(&dir.0), Err(LoadError::Invalid), "no version");
        dir.write(r#"{"version":1,"accounts":[]}"#);
        assert_eq!(load_widget_snapshot(&dir.0), Err(LoadError::Invalid), "no time");
    }

    #[test]
    fn a_newer_file_is_not_overwritten() {
        let dir = TempDir::new("newer");
        let newer = r#"{"version":9,"generatedAt":"2026-10-10T12:00:00Z"}"#;
        dir.write(newer);
        assert!(save_widget_snapshot(&dir.0, &sample()).is_err());
        assert_eq!(std::fs::read_to_string(dir.0.join(WIDGETS_FILE)).unwrap(), newer);
    }

    #[test]
    fn nothing_secret_shaped_is_in_the_contract() {
        let text = serde_json::to_string(&sample()).unwrap().to_lowercase();
        for word in [
            "token",
            "secret",
            "cookie",
            "key\":\"sk",
            "password",
            "credential",
            "path",
        ] {
            assert!(!text.contains(word), "{word}");
        }
    }
}
