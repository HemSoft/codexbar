//! Last-good account snapshots (#76), kept in `snapshots.json` next to the settings so the dashboard shows every
//! account at startup before the first fetch returns, and keeps usage visible while a provider is failing.
//!
//! Only usage is stored: account ids, provider keys, labels, metric names, numbers and times. Never keys, tokens or
//! cookies. Written to a temp file and swapped in; an unreadable or foreign file is ignored, never misread.

use std::io;
use std::path::Path;

use chrono::{DateTime, Utc};
use codexbar_core::{AccountId, AccountSnapshot, Metric, Money, Pace, Provider};
use serde_json::{Value, json};

pub const SNAPSHOTS_FILE: &str = "snapshots.json";
const VERSION: u64 = 1;

/// Loads the last-good snapshots in `dir`. Missing, unreadable or newer files give none; entries that can't be read
/// are skipped.
pub fn load_snapshots(dir: &Path) -> Vec<AccountSnapshot> {
    let Ok(text) = std::fs::read_to_string(dir.join(SNAPSHOTS_FILE)) else {
        return Vec::new();
    };
    let Ok(doc) = serde_json::from_str::<Value>(&text) else {
        return Vec::new();
    };
    if doc.get("version").and_then(Value::as_u64) != Some(VERSION) {
        return Vec::new();
    }
    doc.get("accounts")
        .and_then(Value::as_array)
        .map(|accounts| accounts.iter().filter_map(account_from_json).collect())
        .unwrap_or_default()
}

/// Saves snapshots, replacing the file atomically. A file from a newer CodexBar is left alone, so running an older
/// build once doesn't destroy the newer one's data.
pub fn save_snapshots(dir: &Path, accounts: &[AccountSnapshot]) -> io::Result<()> {
    let existing = std::fs::read_to_string(dir.join(SNAPSHOTS_FILE))
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(&text).ok())
        .and_then(|doc| doc.get("version").and_then(Value::as_u64));
    if existing.is_some_and(|version| version > VERSION) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "snapshots.json is from a newer CodexBar and was not changed",
        ));
    }
    let doc = json!({
        "version": VERSION,
        "accounts": accounts.iter().map(account_to_json).collect::<Vec<_>>(),
    });
    let text = serde_json::to_string_pretty(&doc).map_err(io::Error::other)?;
    std::fs::create_dir_all(dir)?;
    let path = dir.join(SNAPSHOTS_FILE);
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, text)?;
    std::fs::rename(&tmp, &path)
}

fn account_to_json(account: &AccountSnapshot) -> Value {
    json!({
        "id": account.id().as_str(),
        "provider": account.provider().key(),
        "label": account.label(),
        "fetchedAt": account.fetched_at().to_rfc3339(),
        "metrics": account.metrics().iter().map(metric_to_json).collect::<Vec<_>>(),
    })
}

fn account_from_json(value: &Value) -> Option<AccountSnapshot> {
    let id = value.get("id")?.as_str()?;
    let provider = Provider::from_key(value.get("provider")?.as_str()?)?;
    let fetched_at = time(value.get("fetchedAt")?)?;
    let metrics = value
        .get("metrics")?
        .as_array()?
        .iter()
        .map(metric_from_json)
        .collect::<Option<Vec<_>>>()?;
    let account = AccountSnapshot::new(AccountId::new(id), provider, metrics, fetched_at);
    Some(match value.get("label").and_then(Value::as_str) {
        Some(label) => account.with_label(label),
        None => account,
    })
}

fn metric_to_json(metric: &Metric) -> Value {
    match metric {
        Metric::Window {
            label,
            used,
            resets_at,
            pace,
        } => json!({
            "kind": "window", "label": label, "used": used, "resetsAt": resets_at.to_rfc3339(),
            "pacePerHour": pace.map(Pace::fraction_per_hour),
        }),
        Metric::Quota {
            label,
            used,
            limit,
            resets_at,
            pace,
        } => json!({
            "kind": "quota", "label": label, "used": used, "limit": limit, "resetsAt": resets_at.to_rfc3339(),
            "pacePerHour": pace.map(Pace::fraction_per_hour),
        }),
        Metric::Balance {
            label,
            remaining,
            burn_per_day,
        } => json!({
            "kind": "balance", "label": label, "remainingCents": remaining.cents(),
            "burnPerDayCents": burn_per_day.map(Money::cents),
        }),
    }
}

fn metric_from_json(value: &Value) -> Option<Metric> {
    let label = value.get("label")?.as_str()?.to_owned();
    let pace = value.get("pacePerHour").and_then(Value::as_f64).map(Pace::per_hour);
    Some(match value.get("kind")?.as_str()? {
        "window" => Metric::Window {
            label,
            used: value.get("used")?.as_f64()?,
            resets_at: time(value.get("resetsAt")?)?,
            pace,
        },
        "quota" => Metric::Quota {
            label,
            used: value.get("used")?.as_u64()?,
            limit: value.get("limit")?.as_u64()?,
            resets_at: time(value.get("resetsAt")?)?,
            pace,
        },
        "balance" => Metric::Balance {
            label,
            remaining: Money::from_cents(value.get("remainingCents")?.as_i64()?),
            burn_per_day: value
                .get("burnPerDayCents")
                .and_then(Value::as_i64)
                .map(Money::from_cents),
        },
        _ => return None,
    })
}

fn time(value: &Value) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value.as_str()?)
        .ok()
        .map(|time| time.with_timezone(&Utc))
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::{Local, TimeZone};
    use codexbar_core::demo::demo_accounts;

    struct Dir(std::path::PathBuf);

    impl Dir {
        fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!("codexbar-snapshots-{}-{name}", std::process::id()));
            let _ = std::fs::remove_dir_all(&dir);
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }
    }

    impl Drop for Dir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn now() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 10, 7, 14, 0, 0).unwrap()
    }

    #[test]
    fn snapshots_round_trip_every_metric_kind() {
        let dir = Dir::new("round-trip");
        // Trends and detail series are derived from history, not stored.
        let accounts: Vec<AccountSnapshot> = demo_accounts(now(), &Local)
            .into_iter()
            .map(|account| {
                let bare = AccountSnapshot::new(
                    account.id().clone(),
                    account.provider(),
                    account.metrics().to_vec(),
                    account.fetched_at(),
                );
                match account.label() {
                    Some(label) => bare.with_label(label),
                    None => bare,
                }
            })
            .collect();
        save_snapshots(&dir.0, &accounts).unwrap();
        assert_eq!(load_snapshots(&dir.0), accounts);
    }

    #[test]
    fn missing_unreadable_or_newer_files_give_no_snapshots() {
        let dir = Dir::new("bad");
        assert!(load_snapshots(&dir.0).is_empty());
        std::fs::write(dir.0.join(SNAPSHOTS_FILE), "{ nope").unwrap();
        assert!(load_snapshots(&dir.0).is_empty());
        std::fs::write(dir.0.join(SNAPSHOTS_FILE), r#"{ "version": 2, "accounts": [] }"#).unwrap();
        assert!(load_snapshots(&dir.0).is_empty());
    }

    #[test]
    fn a_newer_file_is_never_overwritten() {
        let dir = Dir::new("newer");
        let newer = r#"{ "version": 2, "accounts": [] }"#;
        std::fs::write(dir.0.join(SNAPSHOTS_FILE), newer).unwrap();
        let account = AccountSnapshot::new(AccountId::new("c"), Provider::Cursor, Vec::new(), now());
        assert!(save_snapshots(&dir.0, &[account]).is_err());
        assert_eq!(std::fs::read_to_string(dir.0.join(SNAPSHOTS_FILE)).unwrap(), newer);
    }

    #[test]
    fn an_unreadable_entry_is_skipped_not_the_whole_file() {
        let dir = Dir::new("entry");
        let good = AccountSnapshot::new(AccountId::new("c"), Provider::Cursor, Vec::new(), now());
        save_snapshots(&dir.0, std::slice::from_ref(&good)).unwrap();
        let text = std::fs::read_to_string(dir.0.join(SNAPSHOTS_FILE)).unwrap();
        let mut doc: Value = serde_json::from_str(&text).unwrap();
        doc["accounts"]
            .as_array_mut()
            .unwrap()
            .push(json!({ "id": "x", "provider": "unknown" }));
        std::fs::write(dir.0.join(SNAPSHOTS_FILE), doc.to_string()).unwrap();
        assert_eq!(load_snapshots(&dir.0), vec![good]);
    }
}
