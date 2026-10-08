//! The app-wide Rust-only preferences in `dashboard.json`: which accounts show history (#86), alert settings and
//! the alerts already notified (#87). Changes apply at once and save under the shared lock.

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

use codexbar_core::alerts::AlertSettings;
use codexbar_store::prefs::DashboardPrefs;
use gpui_kit::{App, BorrowAppContext as _, Global, SharedString};

pub struct PrefsHub {
    prefs: DashboardPrefs,
    dir: PathBuf,
    error: Option<SharedString>,
}

impl Global for PrefsHub {}

impl PrefsHub {
    pub fn init(cx: &mut App, dir: &Path) {
        cx.set_global(Self {
            prefs: DashboardPrefs::load(dir),
            dir: dir.to_owned(),
            error: None,
        });
    }

    pub fn shows(cx: &App, account: &str) -> bool {
        cx.try_global::<Self>()
            .is_none_or(|hub| hub.prefs.shows_history(account))
    }

    /// Shows or hides an account's history and saves at once. Hiding never deletes stored samples.
    pub fn set(cx: &mut App, account: &str, show: bool) {
        Self::edit(cx, |prefs| prefs.set_shows_history(account, show));
        cx.refresh_windows();
    }

    /// Moves preferences saved under old account ids to the new ones, saving once if anything moved.
    pub fn rename_accounts(cx: &mut App, renames: &[(&str, String)]) {
        Self::edit(cx, |prefs| {
            for (from, to) in renames {
                prefs.rename_account(from, to);
            }
        });
    }

    pub fn alert_settings(cx: &App) -> AlertSettings {
        cx.try_global::<Self>()
            .map(|hub| hub.prefs.alert_settings().clone())
            .unwrap_or_default()
    }

    /// Changes alert settings with `change` and saves.
    /// Changes alert settings and saves. A changed usage or balance threshold clears that kind's active alerts, so
    /// they are judged against the new threshold rather than held by the old one.
    pub fn update_alert_settings(cx: &mut App, change: impl FnOnce(&mut AlertSettings)) {
        Self::edit(cx, |prefs| {
            let old = prefs.alert_settings().clone();
            let mut settings = old.clone();
            change(&mut settings);
            let mut stale = Vec::new();
            if settings.usage_threshold != old.usage_threshold {
                stale.push("|usage");
            }
            if settings.balance_threshold != old.balance_threshold {
                stale.push("|balance");
            }
            if !stale.is_empty() {
                let active = prefs
                    .active_alerts()
                    .iter()
                    .filter(|key| !stale.iter().any(|kind| key.ends_with(kind)))
                    .cloned()
                    .collect();
                prefs.set_active_alerts(active);
            }
            prefs.set_alert_settings(settings);
        });
        cx.refresh_windows();
    }

    pub fn active_alerts(cx: &App) -> BTreeSet<String> {
        cx.try_global::<Self>()
            .map(|hub| hub.prefs.active_alerts().clone())
            .unwrap_or_default()
    }

    /// Records the active alerts and saves. Also retries an earlier save that failed (the lock was busy), even
    /// when the set itself hasn't changed since.
    pub fn set_active_alerts(cx: &mut App, active: BTreeSet<String>) {
        let dirty = cx.try_global::<Self>().is_some_and(|hub| hub.prefs.is_dirty());
        if dirty || Self::active_alerts(cx) != active {
            Self::edit(cx, |prefs| prefs.set_active_alerts(active));
        }
    }

    /// The last save problem, for the Settings and History views.
    pub fn error(cx: &App) -> Option<SharedString> {
        cx.try_global::<Self>().and_then(|hub| hub.error.clone())
    }

    /// Applies a change in memory at once; a failed save keeps it pending for the next save and says so.
    fn edit(cx: &mut App, change: impl FnOnce(&mut DashboardPrefs)) {
        if cx.try_global::<Self>().is_none() {
            return;
        }
        cx.update_global(|hub: &mut Self, _| {
            change(&mut hub.prefs);
            if !hub.prefs.is_dirty() {
                return;
            }
            hub.error = hub
                .prefs
                .save(&hub.dir)
                .err()
                .map(|err| format!("Setting not saved: {err}").into());
        });
    }
}
