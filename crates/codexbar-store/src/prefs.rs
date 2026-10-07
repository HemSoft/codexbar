//! Dashboard preferences that only the Rust app uses, such as which accounts show history (#86).
//!
//! They live in `dashboard.json`, not `settings.json`: the WPF app reads `settings.json` into typed settings and writes
//! them back, so a key it doesn't know would be dropped on its next save. Keys this version doesn't know are kept, and
//! a file from a newer version is never overwritten.

use std::collections::BTreeSet;
use std::io;
use std::path::Path;

use serde_json::{Map, Value, json};

pub const PREFS_FILE: &str = "dashboard.json";
const VERSION: u64 = 1;

#[derive(Clone, Debug, Default, PartialEq)]
pub struct DashboardPrefs {
    hidden_history: BTreeSet<String>,
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
            read_only: version > VERSION,
        }
    }

    pub fn shows_history(&self, account: &str) -> bool {
        !self.hidden_history.contains(account)
    }

    pub fn set_shows_history(&mut self, account: &str, show: bool) {
        if show {
            self.hidden_history.remove(account);
        } else {
            self.hidden_history.insert(account.to_owned());
        }
    }

    pub fn is_read_only(&self) -> bool {
        self.read_only
    }

    /// Writes the preferences, keeping keys this version doesn't know. Written to a temp file and swapped in, so a
    /// crash never leaves a torn file.
    pub fn save(&self, dir: &Path) -> io::Result<()> {
        if self.read_only {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "dashboard.json is from a newer CodexBar and was not changed",
            ));
        }
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
        doc.insert("version".into(), json!(VERSION));
        doc.insert("hiddenHistory".into(), json!(self.hidden_history));
        let text = serde_json::to_string_pretty(&Value::Object(doc)).map_err(io::Error::other)?;
        std::fs::create_dir_all(dir)?;
        let path = dir.join(PREFS_FILE);
        let tmp = path.with_extension("json.tmp");
        std::fs::write(&tmp, text)?;
        std::fs::rename(&tmp, &path)
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
