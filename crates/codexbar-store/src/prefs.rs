//! Dashboard preferences that only the Rust app uses: which accounts show history (#86), alert settings and the
//! alerts currently active (#87).
//!
//! They live in `dashboard.json`, not `settings.json`: the WPF app reads `settings.json` into typed settings and writes
//! them back, so a key it doesn't know would be dropped on its next save. Keys this version doesn't know are kept, and
//! a file from a newer version is never overwritten.

use std::collections::{BTreeMap, BTreeSet};
use std::io;
use std::path::Path;

use codexbar_core::alerts::AlertSettings;
use serde_json::{Map, Value, json};

pub const PREFS_FILE: &str = "dashboard.json";
const LOCK_FILE: &str = "dashboard.write.lock";
const VERSION: u64 = 1;

#[derive(Clone, Debug, Default, PartialEq)]
pub struct DashboardPrefs {
    hidden_history: BTreeSet<String>,
    /// Changes not yet saved, by account: true shows history, false hides it. A save applies only these to the
    /// file's current contents, so another process's changes made meanwhile are kept.
    pending: BTreeMap<String, bool>,
    alerts: AlertSettings,
    /// Alert conditions already notified and not yet recovered, so a restart doesn't notify them again.
    active_alerts: BTreeSet<String>,
    /// Whole values not yet saved (alert settings, active alerts), by key; a save replaces these keys.
    pending_values: BTreeMap<&'static str, Value>,
    /// The file came from a newer version: read what we understand, never write it.
    read_only: bool,
}

impl DashboardPrefs {
    /// Loads the preferences in `dir`. A missing or unreadable file gives the defaults (every account shows history).
    pub fn load(dir: &Path) -> Self {
        let Some(doc) = read(dir) else {
            return Self::default();
        };
        let version = doc.get("version").and_then(Value::as_u64).unwrap_or(VERSION);
        let hidden_history = doc
            .get("hiddenHistory")
            .and_then(Value::as_array)
            .map(|ids| ids.iter().filter_map(Value::as_str).map(str::to_owned).collect())
            .unwrap_or_default();
        Self {
            hidden_history,
            pending: BTreeMap::new(),
            alerts: doc.get("alerts").map(alerts_from_json).unwrap_or_default(),
            active_alerts: doc
                .get("activeAlerts")
                .and_then(Value::as_array)
                .map(|keys| keys.iter().filter_map(Value::as_str).map(str::to_owned).collect())
                .unwrap_or_default(),
            pending_values: BTreeMap::new(),
            read_only: version > VERSION,
        }
    }

    pub fn alert_settings(&self) -> &AlertSettings {
        &self.alerts
    }

    pub fn set_alert_settings(&mut self, alerts: AlertSettings) {
        self.pending_values.insert("alerts", alerts_to_json(&alerts));
        self.alerts = alerts;
    }

    pub fn active_alerts(&self) -> &BTreeSet<String> {
        &self.active_alerts
    }

    pub fn set_active_alerts(&mut self, active: BTreeSet<String>) {
        self.pending_values.insert("activeAlerts", json!(active));
        self.active_alerts = active;
    }

    /// True when there are changes to save.
    pub fn is_dirty(&self) -> bool {
        !self.pending.is_empty() || !self.pending_values.is_empty()
    }

    pub fn shows_history(&self, account: &str) -> bool {
        !self.hidden_history.contains(account)
    }

    pub fn set_shows_history(&mut self, account: &str, show: bool) {
        apply(&mut self.hidden_history, account, show);
        self.pending.insert(account.to_owned(), show);
    }

    /// Carries a preference from an account's old id to its new one. Returns true when something changed.
    pub fn rename_account(&mut self, from: &str, to: &str) -> bool {
        if self.hidden_history.contains(from) {
            self.set_shows_history(from, true);
            self.set_shows_history(to, false);
            true
        } else {
            false
        }
    }

    pub fn is_read_only(&self) -> bool {
        self.read_only
    }

    /// Saves the unsaved changes. Under the shared `dashboard.write.lock` it rereads the file, applies only this
    /// instance's changes, keeps keys this version doesn't know, and swaps in a temp file, so concurrent writers
    /// never lose each other's changes and a crash never leaves a torn file. Afterwards this instance holds the
    /// merged result. On failure the changes stay pending for the next save.
    pub fn save(&mut self, dir: &Path) -> io::Result<()> {
        if self.read_only {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "dashboard.json is from a newer CodexBar and was not changed",
            ));
        }
        let _lock = crate::lock::FileLock::acquire(&dir.join(LOCK_FILE))?;
        let mut doc = read(dir).unwrap_or_default();
        // Another (newer) CodexBar may have upgraded the file since it was loaded; never downgrade it.
        if doc
            .get("version")
            .and_then(Value::as_u64)
            .is_some_and(|version| version > VERSION)
        {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "dashboard.json was upgraded by a newer CodexBar and was not changed",
            ));
        }
        let mut hidden: BTreeSet<String> = doc
            .get("hiddenHistory")
            .and_then(Value::as_array)
            .map(|ids| ids.iter().filter_map(Value::as_str).map(str::to_owned).collect())
            .unwrap_or_default();
        for (account, show) in &self.pending {
            apply(&mut hidden, account, *show);
        }
        doc.insert("version".into(), json!(VERSION));
        doc.insert("hiddenHistory".into(), json!(hidden));
        for (key, value) in &self.pending_values {
            doc.insert((*key).into(), value.clone());
        }
        let text = serde_json::to_string_pretty(&Value::Object(doc)).map_err(io::Error::other)?;
        std::fs::create_dir_all(dir)?;
        let path = dir.join(PREFS_FILE);
        let tmp = path.with_extension("json.tmp");
        std::fs::write(&tmp, text)?;
        std::fs::rename(&tmp, &path)?;
        self.hidden_history = hidden;
        self.pending.clear();
        self.pending_values.clear();
        Ok(())
    }
}

fn alerts_from_json(value: &Value) -> AlertSettings {
    let defaults = AlertSettings::default();
    let flag = |name: &str, default: bool| value.get(name).and_then(Value::as_bool).unwrap_or(default);
    let number = |name: &str, default: f64| {
        value
            .get(name)
            .and_then(Value::as_f64)
            .filter(|number| number.is_finite() && *number >= 0.0)
            .unwrap_or(default)
    };
    AlertSettings {
        enabled: flag("enabled", defaults.enabled),
        usage_threshold: number("usageThreshold", defaults.usage_threshold).clamp(0.05, 1.0),
        balance_threshold: number("balanceThreshold", defaults.balance_threshold),
        warning: flag("warning", defaults.warning),
        critical: flag("critical", defaults.critical),
    }
}

fn alerts_to_json(alerts: &AlertSettings) -> Value {
    json!({
        "enabled": alerts.enabled,
        "usageThreshold": alerts.usage_threshold,
        "balanceThreshold": alerts.balance_threshold,
        "warning": alerts.warning,
        "critical": alerts.critical,
    })
}

fn apply(hidden: &mut BTreeSet<String>, account: &str, show: bool) {
    if show {
        hidden.remove(account);
    } else {
        hidden.insert(account.to_owned());
    }
}

fn read(dir: &Path) -> Option<Map<String, Value>> {
    let text = std::fs::read_to_string(dir.join(PREFS_FILE)).ok()?;
    match serde_json::from_str(&text).ok()? {
        Value::Object(map) => Some(map),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Dir(std::path::PathBuf);

    impl Dir {
        fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!("codexbar-prefs-{}-{name}", std::process::id()));
            let _ = std::fs::remove_dir_all(&dir);
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }

        fn write(&self, text: &str) {
            std::fs::write(self.0.join(PREFS_FILE), text).unwrap();
        }

        fn read(&self) -> Value {
            serde_json::from_str(&std::fs::read_to_string(self.0.join(PREFS_FILE)).unwrap()).unwrap()
        }
    }

    impl Drop for Dir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn load_missing_or_unreadable_file_shows_every_account() {
        let dir = Dir::new("missing");
        assert!(DashboardPrefs::load(&dir.0).shows_history("any"));
        dir.write("{ not json");
        assert!(DashboardPrefs::load(&dir.0).shows_history("any"));
    }

    #[test]
    fn save_round_trips_hidden_accounts() {
        let dir = Dir::new("round-trip");
        let mut prefs = DashboardPrefs::load(&dir.0);
        prefs.set_shows_history("cursor", false);
        prefs.set_shows_history("moonshot", false);
        prefs.set_shows_history("moonshot", true);
        prefs.save(&dir.0).unwrap();

        let loaded = DashboardPrefs::load(&dir.0);
        assert!(!loaded.shows_history("cursor"));
        assert!(loaded.shows_history("moonshot"));
        assert_eq!(dir.read()["hiddenHistory"], json!(["cursor"]));
    }

    #[test]
    fn rename_account_moves_a_hidden_preference() {
        let mut prefs = DashboardPrefs::default();
        prefs.set_shows_history("openrouter", false);
        assert!(prefs.rename_account("openrouter", "a1b2"));
        assert!(prefs.shows_history("openrouter"));
        assert!(!prefs.shows_history("a1b2"));
        assert!(!prefs.rename_account("openrouter", "a1b2"), "nothing left to move");
    }

    #[test]
    fn save_merges_with_another_writers_changes() {
        let dir = Dir::new("merge");
        let mut first = DashboardPrefs::load(&dir.0);
        let mut second = DashboardPrefs::load(&dir.0);
        first.set_shows_history("cursor", false);
        first.save(&dir.0).unwrap();
        // The second instance loaded before the first saved; its save must not undo the first's change.
        second.set_shows_history("moonshot", false);
        second.save(&dir.0).unwrap();
        assert_eq!(dir.read()["hiddenHistory"], json!(["cursor", "moonshot"]));
        assert!(!second.shows_history("cursor"), "the saver now holds the merged result");
    }

    #[test]
    fn save_while_another_writer_holds_the_lock_is_busy_and_keeps_the_change() {
        use std::os::windows::fs::OpenOptionsExt as _;
        let dir = Dir::new("busy");
        let lock = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .share_mode(0)
            .open(dir.0.join(LOCK_FILE))
            .unwrap();
        let mut prefs = DashboardPrefs::load(&dir.0);
        prefs.set_shows_history("cursor", false);
        assert_eq!(prefs.save(&dir.0).unwrap_err().kind(), io::ErrorKind::WouldBlock);
        drop(lock);
        prefs.save(&dir.0).unwrap();
        assert_eq!(
            dir.read()["hiddenHistory"],
            json!(["cursor"]),
            "the pending change was kept and saved"
        );
    }

    #[test]
    fn alert_settings_and_active_alerts_round_trip() {
        let dir = Dir::new("alerts");
        let mut prefs = DashboardPrefs::load(&dir.0);
        assert_eq!(prefs.alert_settings(), &AlertSettings::default());
        let settings = AlertSettings {
            enabled: true,
            usage_threshold: 0.9,
            balance_threshold: 2.0,
            warning: false,
            critical: true,
        };
        prefs.set_alert_settings(settings.clone());
        prefs.set_active_alerts(["a|weekly|usage".to_owned()].into());
        assert!(prefs.is_dirty());
        prefs.save(&dir.0).unwrap();
        assert!(!prefs.is_dirty());

        let loaded = DashboardPrefs::load(&dir.0);
        assert_eq!(loaded.alert_settings(), &settings);
        assert!(loaded.active_alerts().contains("a|weekly|usage"));
        assert_eq!(dir.read()["alerts"]["usageThreshold"], json!(0.9));
    }

    #[test]
    fn unreadable_alert_values_fall_back_to_defaults() {
        let dir = Dir::new("alerts-bad");
        dir.write(r#"{ "version": 1, "alerts": { "enabled": "yes", "usageThreshold": -3, "balanceThreshold": "x" } }"#);
        assert_eq!(DashboardPrefs::load(&dir.0).alert_settings(), &AlertSettings::default());
    }

    #[test]
    fn save_keeps_unknown_keys() {
        let dir = Dir::new("unknown");
        dir.write(r#"{ "version": 1, "hiddenHistory": [], "lastView": "history" }"#);
        let mut prefs = DashboardPrefs::load(&dir.0);
        prefs.set_shows_history("cursor", false);
        prefs.save(&dir.0).unwrap();
        assert_eq!(dir.read()["lastView"], json!("history"));
    }

    #[test]
    fn save_refuses_a_file_upgraded_after_load() {
        let dir = Dir::new("upgraded");
        let mut prefs = DashboardPrefs::load(&dir.0);
        let newer = r#"{ "version": 2, "hiddenHistory": [] }"#;
        dir.write(newer);
        prefs.set_shows_history("cursor", false);
        assert!(prefs.save(&dir.0).is_err());
        assert_eq!(std::fs::read_to_string(dir.0.join(PREFS_FILE)).unwrap(), newer);
    }

    #[test]
    fn newer_version_is_read_but_never_overwritten() {
        let dir = Dir::new("newer");
        let newer = r#"{ "version": 2, "hiddenHistory": ["cursor"] }"#;
        dir.write(newer);
        let mut prefs = DashboardPrefs::load(&dir.0);
        assert!(prefs.is_read_only());
        assert!(!prefs.shows_history("cursor"));
        prefs.set_shows_history("cursor", true);
        assert!(prefs.save(&dir.0).is_err());
        assert_eq!(std::fs::read_to_string(dir.0.join(PREFS_FILE)).unwrap(), newer);
    }
}
