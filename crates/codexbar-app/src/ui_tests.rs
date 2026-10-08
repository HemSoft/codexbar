//! Headless UI integration tests: the real dashboard in an in-process test window, driven by GPUI events.
//! Nothing here opens a desktop window, sends OS input or changes focus outside the test.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use codexbar_store::credentials::MemoryCredentialStore;
use gpui_kit::component::Root;
use gpui_kit::test::TestWindowExt as _;
use gpui_kit::{
    AnyWindowHandle, AppContext as _, Entity, InputEvent as _, Modifiers, ScrollDelta, ScrollWheelEvent,
    TestAppContext, point, px, size,
};

use crate::dashboard::{Dashboard, DashboardView, DataSource};
use crate::notifications::RecordingNotifier;
use crate::settings_hub::SettingsHub;
use crate::{theme, zoom};

/// A settings folder for one test, removed when the test ends.
struct TempSettings(PathBuf);

impl TempSettings {
    fn new(name: &str, json: &str) -> Self {
        let dir = std::env::temp_dir().join(format!("codexbar-ui-{}-{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("settings.json"), json).unwrap();
        Self(dir)
    }

    fn zoom_on_disk(&self) -> Option<f64> {
        let text = std::fs::read_to_string(self.0.join("settings.json")).unwrap();
        let json: serde_json::Value = serde_json::from_str(&text).unwrap();
        json.get("zoomLevel").and_then(serde_json::Value::as_f64)
    }
}

impl Drop for TempSettings {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Opens the demo dashboard the way `main` does, with settings from `settings`.
fn open_dashboard(cx: &mut TestAppContext, settings: &TempSettings) -> (AnyWindowHandle, Entity<Dashboard>) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        theme::init(cx);
        SettingsHub::init_with(cx, &settings.0, Arc::new(MemoryCredentialStore::default()));
        crate::prefs_hub::PrefsHub::init(cx, &settings.0);
        zoom::init(cx);
        if cx.try_global::<crate::notifications::Notifications>().is_none() {
            // Demo data never persists alert state; tests that check the file install their own notifier.
            crate::notifications::Notifications::init(cx, Arc::new(RecordingNotifier::default()), false);
        }
    });
    let mut dashboard = None;
    let handle = cx.open_window(size(px(1440.), px(960.)), |window, cx| {
        let view = cx.new(|cx| Dashboard::new(DataSource::Demo, window, cx));
        dashboard = Some(view.clone());
        Root::new(view, window, cx)
    });
    let handle: AnyWindowHandle = handle.into();
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    (handle, dashboard.unwrap())
}

fn press(cx: &mut TestAppContext, handle: AnyWindowHandle, key: &str) {
    cx.update_window(handle, |_, window, cx| window.press(key, cx)).unwrap();
    cx.run_until_parked();
}

/// One Ctrl+wheel event at the middle of the window. The test helpers' `scroll` sends no modifiers.
fn wheel(cx: &mut TestAppContext, handle: AnyWindowHandle, delta: ScrollDelta, control: bool) {
    cx.update_window(handle, |_, window, cx| {
        window.dispatch_event(
            ScrollWheelEvent {
                position: point(px(720.), px(480.)),
                delta,
                modifiers: Modifiers {
                    control,
                    ..Modifiers::default()
                },
                ..Default::default()
            }
            .to_platform_input(),
            cx,
        );
        window.render_frame(cx);
    })
    .unwrap();
    cx.run_until_parked();
}

fn level(cx: &mut TestAppContext) -> f64 {
    cx.update(|cx| zoom::level(cx))
}

#[gpui_kit::test]
fn keyboard_shortcuts_step_and_reset_zoom(cx: &mut TestAppContext) {
    let settings = TempSettings::new("keys", r#"{ "zoomLevel": 1.0 }"#);
    let (handle, _) = open_dashboard(cx, &settings);
    assert_eq!(level(cx), 1.0);

    press(cx, handle, "ctrl-=");
    press(cx, handle, "ctrl-=");
    assert_eq!(level(cx), 1.2);
    press(cx, handle, "ctrl--");
    assert_eq!(level(cx), 1.1);
    press(cx, handle, "ctrl-0");
    assert_eq!(level(cx), 1.0);
}

#[gpui_kit::test]
fn ctrl_wheel_zooms_and_plain_wheel_does_not(cx: &mut TestAppContext) {
    let settings = TempSettings::new("wheel", r#"{ "zoomLevel": 1.0 }"#);
    let (handle, _) = open_dashboard(cx, &settings);
    // Pixel deltas don't depend on the machine's lines-per-notch setting: 50 px is one step.
    let pixels = |y: f32| ScrollDelta::Pixels(point(px(0.), px(y)));

    wheel(cx, handle, pixels(50.), false);
    assert_eq!(level(cx), 1.0, "a wheel without Ctrl scrolls instead of zooming");

    wheel(cx, handle, pixels(50.), true);
    wheel(cx, handle, pixels(50.), true);
    assert_eq!(level(cx), 1.2);
    wheel(cx, handle, pixels(-50.), true);
    assert_eq!(level(cx), 1.1);
}

#[gpui_kit::test]
fn zoom_is_saved_after_input_settles(cx: &mut TestAppContext) {
    let settings = TempSettings::new("save", r#"{ "zoomLevel": 1.0 }"#);
    let (handle, _) = open_dashboard(cx, &settings);

    press(cx, handle, "ctrl-=");
    assert_eq!(settings.zoom_on_disk(), Some(1.0), "the save waits for input to settle");
    cx.executor().advance_clock(Duration::from_millis(700));
    cx.run_until_parked();
    assert_eq!(settings.zoom_on_disk(), Some(1.1));
}

#[gpui_kit::test]
fn pending_zoom_is_saved_on_quit(cx: &mut TestAppContext) {
    let settings = TempSettings::new("quit", r#"{ "zoomLevel": 1.0 }"#);
    let (handle, _) = open_dashboard(cx, &settings);

    press(cx, handle, "ctrl-=");
    cx.update(|cx| cx.shutdown());
    assert_eq!(settings.zoom_on_disk(), Some(1.1));
}

#[gpui_kit::test]
fn view_tabs_switch_views_by_click_and_keyboard(cx: &mut TestAppContext) {
    let settings = TempSettings::new("tabs", "{}");
    let (handle, dashboard) = open_dashboard(cx, &settings);
    let view = |cx: &mut TestAppContext| cx.update(|cx| dashboard.read(cx).view());

    cx.update_window(handle, |_, window, cx| {
        assert_eq!(window.find(("view-tab", 0usize)).selected(), Some(true));
        assert_eq!(window.find(("view-tab", 1usize)).selected(), Some(false));
        window.click(("view-tab", 1usize), cx);
    })
    .unwrap();
    assert_eq!(view(cx), DashboardView::Spend);

    // Keyboard: the arrow keys move between tabs and switch views, wrapping at the ends; Home and End jump.
    let focused = |cx: &mut TestAppContext, ix: usize| {
        cx.update_window(handle, |_, window, _| window.find(("view-tab", ix)).focused())
            .unwrap()
    };
    assert_eq!(focused(cx, 1), Some(true), "clicking a tab focuses it");
    press(cx, handle, "right");
    assert_eq!(view(cx), DashboardView::History);
    assert_eq!(focused(cx, 2), Some(true));
    press(cx, handle, "right");
    assert_eq!(view(cx), DashboardView::Usage);
    press(cx, handle, "left");
    assert_eq!(view(cx), DashboardView::History);
    press(cx, handle, "home");
    assert_eq!(view(cx), DashboardView::Usage);
    assert_eq!(focused(cx, 0), Some(true));
    press(cx, handle, "end");
    assert_eq!(view(cx), DashboardView::History);
    press(cx, handle, "ctrl-left");
    assert_eq!(view(cx), DashboardView::History, "modified arrows don't switch views");

    // Tab leaves the tab row instead of visiting every tab.
    press(cx, handle, "tab");
    assert!((0..3).all(|ix| focused(cx, ix) == Some(false)));
    // Shift+Tab comes back to the selected tab, not the last one in the row.
    press(cx, handle, "shift-tab");
    assert_eq!(focused(cx, 2), Some(true));
}

#[gpui_kit::test]
fn failed_save_reloads_from_the_injected_folder(cx: &mut TestAppContext) {
    use std::os::windows::fs::OpenOptionsExt as _;

    let settings = TempSettings::new("reload", r#"{ "zoomLevel": 1.3 }"#);
    open_dashboard(cx, &settings);
    // Hold the shared write lock the way another writer would, so the save fails with Busy and the hub reloads.
    let _lock = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .share_mode(0)
        .open(settings.0.join("settings.write.lock"))
        .unwrap();

    let result = cx.update(|cx| {
        SettingsHub::update(cx, |settings| {
            settings.set_zoom_level(2.0);
            Ok(())
        })
    });
    assert_eq!(result, Err(codexbar_store::settings::SettingsError::Busy));
    let reloaded = cx.update(|cx| SettingsHub::global(cx).settings().zoom_level());
    assert_eq!(reloaded, 1.3, "the reload reads the test's folder, not ~/.codexbar");
    assert_eq!(settings.zoom_on_disk(), Some(1.3));
}

fn label(cx: &mut TestAppContext, handle: AnyWindowHandle, id: &'static str) -> Option<String> {
    cx.update_window(handle, |_, window, _| {
        window.try_find(id).and_then(|found| found.label().map(str::to_owned))
    })
    .unwrap()
}

fn visible(cx: &mut TestAppContext, handle: AnyWindowHandle, id: &'static str) -> bool {
    cx.update_window(handle, |_, window, _| {
        window.try_find(id).is_some_and(|found| found.visible())
    })
    .unwrap()
}

fn click(cx: &mut TestAppContext, handle: AnyWindowHandle, id: impl Into<gpui_kit::ElementId>) {
    let id = id.into();
    cx.update_window(handle, |_, window, cx| window.click(id, cx)).unwrap();
    cx.run_until_parked();
}

fn open_history(cx: &mut TestAppContext, handle: AnyWindowHandle) {
    click(cx, handle, ("view-tab", 2usize));
}

fn hidden_on_disk(settings: &TempSettings) -> serde_json::Value {
    let text = std::fs::read_to_string(settings.0.join("dashboard.json")).unwrap();
    serde_json::from_str::<serde_json::Value>(&text).unwrap()["hiddenHistory"].clone()
}

#[gpui_kit::test]
fn history_view_shows_summary_and_chart_for_the_selected_account(cx: &mut TestAppContext) {
    let settings = TempSettings::new("history", "{}");
    let (handle, dashboard) = open_dashboard(cx, &settings);
    open_history(cx, handle);

    let (view, _) = cx.update(|cx| dashboard.read(cx).history_parts());
    let shown = cx.update(|cx| view.read(cx).shown()).unwrap();
    // The Usage view's selection (the most urgent account) carries over.
    assert_eq!(shown, ("codex-personal".to_owned(), "5-hour-window".to_owned()));

    let chart = label(cx, handle, "history-chart-region").unwrap();
    assert!(
        chart.starts_with("5-hour window history, last 7 days. Latest 91%"),
        "{chart}"
    );
    assert!(chart.contains("Low ") && chart.contains("samples"), "{chart}");
    let readout = label(cx, handle, "history-readout").unwrap();
    assert!(
        readout.ends_with("91%"),
        "the readout starts on the latest point: {readout}"
    );
}

#[gpui_kit::test]
fn history_chart_points_can_be_read_with_the_keyboard(cx: &mut TestAppContext) {
    let settings = TempSettings::new("history-keys", "{}");
    let (handle, _) = open_dashboard(cx, &settings);
    open_history(cx, handle);

    let latest = label(cx, handle, "history-readout").unwrap();
    click(cx, handle, "history-chart-region");
    let focused = cx
        .update_window(handle, |_, window, _| window.find("history-chart-region").focused())
        .unwrap();
    assert_eq!(focused, Some(true));

    press(cx, handle, "left");
    let previous = label(cx, handle, "history-readout").unwrap();
    assert_ne!(previous, latest, "Left moves to the previous point");
    press(cx, handle, "home");
    let first = label(cx, handle, "history-readout").unwrap();
    assert_ne!(first, previous);
    press(cx, handle, "end");
    assert_eq!(label(cx, handle, "history-readout").unwrap(), latest);
    press(cx, handle, "ctrl-left");
    assert_eq!(
        label(cx, handle, "history-readout").unwrap(),
        latest,
        "modified keys are left alone"
    );
}

#[gpui_kit::test]
fn history_metric_and_range_can_be_changed(cx: &mut TestAppContext) {
    let settings = TempSettings::new("history-controls", "{}");
    let (handle, dashboard) = open_dashboard(cx, &settings);
    open_history(cx, handle);
    let (view, _) = cx.update(|cx| dashboard.read(cx).history_parts());

    click(cx, handle, ("history-metric", 1usize));
    let shown = cx.update(|cx| view.read(cx).shown()).unwrap();
    assert_eq!(shown.1, "weekly");
    let week = label(cx, handle, "history-chart-region").unwrap();
    assert!(week.starts_with("Weekly history, last 7 days."), "{week}");

    click(cx, handle, ("history-range", 0usize));
    let day = label(cx, handle, "history-chart-region").unwrap();
    assert!(day.starts_with("Weekly history, last 24 hours."), "{day}");
}

#[gpui_kit::test]
fn hiding_history_keeps_the_data_and_is_saved(cx: &mut TestAppContext) {
    let settings = TempSettings::new("history-hide", "{}");
    let (handle, dashboard) = open_dashboard(cx, &settings);
    open_history(cx, handle);
    let (_, history) = cx.update(|cx| dashboard.read(cx).history_parts());
    let before = history.lock().unwrap().len();

    click(cx, handle, "history-show");
    assert_eq!(hidden_on_disk(&settings), serde_json::json!(["codex-personal"]));
    assert!(
        label(cx, handle, "history-chart-region").is_none(),
        "the chart is replaced"
    );
    assert!(visible(cx, handle, "history-empty"));
    assert_eq!(history.lock().unwrap().len(), before, "hiding deletes nothing");

    click(cx, handle, "history-show");
    assert!(label(cx, handle, "history-chart-region").is_some());
    assert_eq!(hidden_on_disk(&settings), serde_json::json!([]));
}

#[gpui_kit::test]
fn history_view_handles_empty_and_single_sample_series(cx: &mut TestAppContext) {
    use codexbar_core::demo::demo_accounts;
    use codexbar_store::HistoryStore;
    use codexbar_store::summary::Point;
    use std::sync::Mutex;

    let settings = TempSettings::new("history-edge", "{}");
    cx.update(|cx| {
        gpui_kit::init(cx);
        theme::init(cx);
        crate::prefs_hub::PrefsHub::init(cx, &settings.0);
    });
    let now = chrono::Utc::now();
    let accounts = demo_accounts(now, &chrono::Local);
    let history = Arc::new(Mutex::new(HistoryStore::in_memory(chrono::Duration::days(30))));
    let mut view = None;
    let handle = cx.open_window(size(px(1200.), px(800.)), |window, cx| {
        let entity = cx.new(|cx| crate::history_view::HistoryView::new(history.clone(), cx));
        view = Some(entity.clone());
        Root::new(entity, window, cx)
    });
    let handle: AnyWindowHandle = handle.into();
    let view = view.unwrap();
    cx.update(|cx| view.update(cx, |view, cx| view.set_accounts(accounts.clone(), None, cx)));
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();

    // No samples: an explanation, no chart, no zeros.
    assert!(label(cx, handle, "history-chart-region").is_none());
    assert!(visible(cx, handle, "history-empty"));

    // One sample: shown as a value and described as the only sample.
    history.lock().unwrap().insert_points(
        "codex-personal",
        "5-hour-window",
        &[Point::new(now - chrono::Duration::minutes(10), 0.4)],
    );
    cx.update(|cx| view.update(cx, |_, cx| cx.notify()));
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    let chart = label(cx, handle, "history-chart-region").unwrap();
    assert!(chart.ends_with("Latest 40%. Only one sample so far."), "{chart}");
}

#[gpui_kit::test]
fn table_trend_shows_compact_history_until_hidden(cx: &mut TestAppContext) {
    let settings = TempSettings::new("history-compact", "{}");
    let (handle, _) = open_dashboard(cx, &settings);
    let cell = "trend-cell-codex-personal";

    let compact = label(cx, handle, cell).unwrap();
    assert!(
        compact.starts_with("ChatGPT · Codex (personal) trend. Last 14 days: Latest 91%"),
        "{compact}"
    );
    assert!(compact.contains("Low ") && compact.contains("samples"), "{compact}");

    open_history(cx, handle);
    click(cx, handle, "history-show");
    click(cx, handle, ("view-tab", 0usize));
    assert!(label(cx, handle, cell).is_none(), "a hidden account shows no trend");
}

#[gpui_kit::test]
fn history_follows_the_usage_selection_until_an_account_is_picked(cx: &mut TestAppContext) {
    let settings = TempSettings::new("history-follow", "{}");
    let (handle, dashboard) = open_dashboard(cx, &settings);
    let (view, _) = cx.update(|cx| dashboard.read(cx).history_parts());
    let shown = |cx: &mut TestAppContext| cx.update(|cx| view.read(cx).shown()).unwrap().0;
    let row_id = |cx: &mut TestAppContext, ix: usize| cx.update(|cx| dashboard.read(cx).account_id(ix)).unwrap();

    // Selecting another Usage row after the first load still carries over.
    cx.update(|cx| dashboard.update(cx, |dashboard, cx| dashboard.select_row(2, cx)));
    cx.run_until_parked();
    let third = row_id(cx, 2);
    open_history(cx, handle);
    assert_eq!(shown(cx), third);

    // Picking an account in History pins it; later Usage selections don't move it.
    click(cx, handle, ("history-account", 4usize));
    let picked = row_id(cx, 4);
    assert_eq!(shown(cx), picked);
    cx.update(|cx| dashboard.update(cx, |dashboard, cx| dashboard.select_row(1, cx)));
    cx.run_until_parked();
    assert_eq!(shown(cx), picked);
}

#[gpui_kit::test]
fn single_balance_account_preferences_move_to_its_configured_id(cx: &mut TestAppContext) {
    let settings = TempSettings::new(
        "legacy-ids",
        r#"{ "accountConfigurationVersion": 1, "accounts": [
            { "id": "or-1", "providerId": "OpenRouter", "displayLabel": "OpenRouter", "enabled": true,
              "authenticationMethod": "ApiKey" } ] }"#,
    );
    std::fs::write(
        settings.0.join("dashboard.json"),
        r#"{ "version": 1, "hiddenHistory": ["openrouter"] }"#,
    )
    .unwrap();
    cx.update(|cx| {
        SettingsHub::init_with(cx, &settings.0, Arc::new(MemoryCredentialStore::default()));
        crate::prefs_hub::PrefsHub::init(cx, &settings.0);
    });

    // One configured OpenRouter account owns the legacy history; Moonshot has no records, so its implicit
    // account keeps reporting under the legacy id and nothing moves for it.
    let renames = cx.update(|cx| crate::providers::legacy_ids(SettingsHub::global(cx)));
    assert_eq!(renames, vec![("openrouter", "or-1".to_owned())]);
    let configured = renames[0].1.clone();

    cx.update(|cx| crate::prefs_hub::PrefsHub::rename_accounts(cx, &renames));
    let shows = |cx: &mut TestAppContext, id: &str| cx.update(|cx| crate::prefs_hub::PrefsHub::shows(cx, id));
    assert!(
        !shows(cx, &configured),
        "the hidden preference follows the account to its configured id"
    );
    assert!(shows(cx, "openrouter"));
    assert_eq!(hidden_on_disk(&settings), serde_json::json!([configured]));
}

#[gpui_kit::test]
fn legacy_history_is_not_moved_when_several_accounts_could_own_it(cx: &mut TestAppContext) {
    // Two OpenRouter accounts, only one enabled: the legacy history may belong to either, so it stays put.
    let settings = TempSettings::new(
        "legacy-ambiguous",
        r#"{ "accountConfigurationVersion": 1, "accounts": [
            { "id": "old", "providerId": "OpenRouter", "displayLabel": "Old", "enabled": false,
              "authenticationMethod": "ApiKey" },
            { "id": "new", "providerId": "OpenRouter", "displayLabel": "New", "enabled": true,
              "authenticationMethod": "ApiKey" },
            { "id": "kimi", "providerId": "Moonshot", "displayLabel": "Kimi", "enabled": false,
              "authenticationMethod": "ApiKey" } ] }"#,
    );
    cx.update(|cx| SettingsHub::init_with(cx, &settings.0, Arc::new(MemoryCredentialStore::default())));
    let renames = cx.update(|cx| crate::providers::legacy_ids(SettingsHub::global(cx)));
    // Moonshot's one account is disabled but still the only owner, so its legacy id moves to it.
    assert_eq!(renames, vec![("moonshot", "kimi".to_owned())]);
}

fn legacy_history_after_migration(cx: &mut TestAppContext, settings_json: &str, name: &str) -> (usize, usize) {
    use codexbar_store::HistoryStore;
    use codexbar_store::summary::Point;
    use std::sync::Mutex;

    let settings = TempSettings::new(name, settings_json);
    cx.update(|cx| {
        SettingsHub::init_with(cx, &settings.0, Arc::new(MemoryCredentialStore::default()));
        crate::prefs_hub::PrefsHub::init(cx, &settings.0);
    });
    let mut store = HistoryStore::in_memory(chrono::Duration::days(30));
    store.insert_points("openrouter", "credits", &[Point::new(chrono::Utc::now(), 12.5)]);
    let history = Mutex::new(store);
    cx.update(|cx| crate::dashboard::migrate_legacy_ids(&history, cx));
    let store = history.lock().unwrap();
    let legacy = store.series("openrouter", "credits").count();
    (legacy, store.len() - legacy)
}

#[gpui_kit::test]
fn legacy_history_moves_only_when_settings_are_readable(cx: &mut TestAppContext) {
    // Readable settings with one configured OpenRouter account: it owns the legacy history.
    let one = r#"{ "accountConfigurationVersion": 1, "accounts": [
            { "id": "or-1", "providerId": "OpenRouter", "displayLabel": "OpenRouter", "enabled": true,
              "authenticationMethod": "ApiKey" } ] }"#;
    assert_eq!(legacy_history_after_migration(cx, one, "migrate-readable"), (0, 1));
}

#[gpui_kit::test]
fn legacy_history_stays_put_while_no_account_is_configured(cx: &mut TestAppContext) {
    // The implicit account still reports under the legacy id, so moving its history would orphan it once the first
    // account is configured with a new id.
    assert_eq!(legacy_history_after_migration(cx, "{}", "migrate-implicit"), (1, 0));
}

#[gpui_kit::test]
fn legacy_history_stays_put_when_settings_are_from_a_newer_version(cx: &mut TestAppContext) {
    // A newer schema can't be read, so the hub falls back to defaults; nothing may move on their say-so.
    let newer = r#"{ "accountConfigurationVersion": 2, "accounts": [] }"#;
    assert_eq!(legacy_history_after_migration(cx, newer, "migrate-newer"), (1, 0));
}

const ALERTS_ON: &str = r#"{ "version": 1, "alerts": { "enabled": true, "usageThreshold": 0.8, "balanceThreshold": 10,
    "warning": true, "critical": true } }"#;

/// Opens the demo dashboard with alert settings in `dashboard.json` and a recording notifier.
fn open_with_alerts(cx: &mut TestAppContext, settings: &TempSettings) -> (Entity<Dashboard>, Arc<RecordingNotifier>) {
    let notifier = Arc::new(RecordingNotifier::default());
    let recorder = notifier.clone();
    cx.update(|cx| crate::notifications::Notifications::init(cx, recorder, true));
    let (_, dashboard) = open_dashboard(cx, settings);
    (dashboard, notifier)
}

fn active_on_disk(settings: &TempSettings) -> Vec<String> {
    let text = std::fs::read_to_string(settings.0.join("dashboard.json")).unwrap();
    let json: serde_json::Value = serde_json::from_str(&text).unwrap();
    json["activeAlerts"]
        .as_array()
        .map(|keys| keys.iter().filter_map(|key| key.as_str().map(str::to_owned)).collect())
        .unwrap_or_default()
}

fn refresh(cx: &mut TestAppContext, dashboard: &Entity<Dashboard>) {
    cx.update(|cx| dashboard.update(cx, |dashboard, cx| dashboard.refresh(cx)));
    cx.run_until_parked();
}

#[gpui_kit::test]
fn alerts_notify_once_per_condition_and_survive_a_restart(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let (dashboard, notifier) = open_with_alerts(cx, &settings);

    let shown: Vec<String> = notifier
        .shown
        .lock()
        .unwrap()
        .iter()
        .map(|alert| alert.title.clone())
        .collect();
    // The demo's Codex 5-hour window is at 91% and about to run out; Moonshot has $7.85 left, under $10.
    let has = |title: &str| shown.iter().any(|shown| shown == title);
    assert!(has("ChatGPT · Codex (personal): 5-hour window at 91%"), "{shown:?}");
    assert!(has("ChatGPT · Codex (personal): 5-hour window limit soon"), "{shown:?}");
    assert!(
        !has("ChatGPT · Codex (personal): 5-hour window at risk"),
        "covered by limit soon"
    );
    assert!(has("Moonshot (Kimi): $7.85 left"), "{shown:?}");
    let active = active_on_disk(&settings);
    assert!(
        active.len() > shown.len(),
        "delivered alerts and the conditions they cover are remembered"
    );

    // Another refresh with the same conditions notifies nothing new.
    refresh(cx, &dashboard);
    assert_eq!(notifier.shown.lock().unwrap().len(), shown.len());

    // A restart reads the remembered alerts and stays quiet too.
    cx.update(|cx| crate::prefs_hub::PrefsHub::init(cx, &settings.0));
    refresh(cx, &dashboard);
    assert_eq!(notifier.shown.lock().unwrap().len(), shown.len());
}

#[gpui_kit::test]
fn alerts_are_off_until_enabled(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-off", "{}");
    let (dashboard, notifier) = open_with_alerts(cx, &settings);
    assert!(notifier.shown.lock().unwrap().is_empty());

    cx.update(|cx| crate::prefs_hub::PrefsHub::update_alert_settings(cx, |alerts| alerts.enabled = true));
    refresh(cx, &dashboard);
    assert!(!notifier.shown.lock().unwrap().is_empty());
}

#[gpui_kit::test]
fn blocked_notifications_are_retried_once_allowed(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-blocked", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let notifier = Arc::new(RecordingNotifier::default());
    *notifier.blocked.lock().unwrap() = Some("Notifications are off for CodexBar.".into());
    let recorder = notifier.clone();
    cx.update(|cx| crate::notifications::Notifications::init(cx, recorder, true));
    let (_, dashboard) = open_dashboard(cx, &settings);

    assert!(notifier.shown.lock().unwrap().is_empty());
    assert!(active_on_disk(&settings).is_empty(), "nothing is marked as delivered");
    let problem = cx.update(|cx| crate::notifications::Notifications::problem(cx));
    assert_eq!(problem.as_deref(), Some("Notifications are off for CodexBar."));

    *notifier.blocked.lock().unwrap() = None;
    refresh(cx, &dashboard);
    assert!(
        !notifier.shown.lock().unwrap().is_empty(),
        "allowed again, the alerts arrive"
    );
    assert_eq!(cx.update(|cx| crate::notifications::Notifications::problem(cx)), None);
}

#[gpui_kit::test]
fn reset_lets_active_conditions_notify_again(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-reset", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let (dashboard, notifier) = open_with_alerts(cx, &settings);
    let first = notifier.shown.lock().unwrap().len();
    assert!(first > 0);

    cx.update(crate::notifications::Notifications::reset);
    assert!(active_on_disk(&settings).is_empty());
    refresh(cx, &dashboard);
    assert_eq!(notifier.shown.lock().unwrap().len(), first * 2);
}

#[gpui_kit::test]
fn alerts_settings_page_shows_status_and_actions(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-page", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let (dashboard, _) = open_with_alerts(cx, &settings);
    let handle = cx.windows()[0];
    cx.update(|cx| dashboard.update(cx, |dashboard, cx| dashboard.show_view(DashboardView::Settings, cx)));
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    // The Alerts page is the third item in the settings sidebar.
    cx.update_window(handle, |_, window, cx| {
        window.within("settings-sidebar").click("0-2", cx)
    })
    .unwrap();
    cx.run_until_parked();
    let status = label(cx, handle, "alerts-status").unwrap();
    assert!(
        status.starts_with("Windows notifications are on.") && status.contains("active"),
        "{status}"
    );

    click(cx, handle, "alerts-reset");
    let status = label(cx, handle, "alerts-status").unwrap();
    assert_eq!(status, "Windows notifications are on. No alerts are active.");
}

#[gpui_kit::test]
fn demo_alerts_never_touch_dashboard_json(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-demo", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let notifier = Arc::new(RecordingNotifier::default());
    let recorder = notifier.clone();
    cx.update(|cx| crate::notifications::Notifications::init(cx, recorder, false));
    let (dashboard, _) = {
        let (handle, dashboard) = open_dashboard(cx, &settings);
        (dashboard, handle)
    };
    assert!(
        !notifier.shown.lock().unwrap().is_empty(),
        "the demo still evaluates and records alerts"
    );
    assert!(
        active_on_disk(&settings).is_empty(),
        "but its synthetic keys stay in memory"
    );
    // And remembers them in memory, so a second demo refresh doesn't repeat them.
    let shown = notifier.shown.lock().unwrap().len();
    refresh(cx, &dashboard);
    assert_eq!(notifier.shown.lock().unwrap().len(), shown);
}

#[gpui_kit::test]
fn a_notification_windows_failed_to_raise_is_sent_again(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-failed", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let (dashboard, notifier) = open_with_alerts(cx, &settings);
    let first = notifier.shown.lock().unwrap().len();
    let lost = notifier.shown.lock().unwrap()[0].clone();

    // Windows accepted the first toast but later raised `Failed` for it; the clock resends it without a refresh.
    notifier.failed.lock().unwrap().extend(lost.keys().cloned());
    cx.executor().advance_clock(std::time::Duration::from_secs(2));
    cx.run_until_parked();
    let shown = notifier.shown.lock().unwrap();
    assert_eq!(shown.len(), first + 1, "only the failed one is sent again");
    assert_eq!(shown.last().unwrap().title, lost.title);
    let _ = dashboard;
}

#[gpui_kit::test]
fn changing_a_threshold_judges_held_alerts_afresh(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-threshold", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let (_, _) = open_with_alerts(cx, &settings);
    let has_usage = |keys: &[String]| keys.iter().any(|key| key.ends_with("|usage"));
    assert!(has_usage(&active_on_disk(&settings)));

    cx.update(|cx| crate::prefs_hub::PrefsHub::update_alert_settings(cx, |alerts| alerts.usage_threshold = 0.95));
    let active = active_on_disk(&settings);
    assert!(!has_usage(&active), "usage alerts are re-judged against 95%");
    assert!(
        active.iter().any(|key| key.ends_with("|balance")),
        "other kinds keep theirs"
    );
}

#[gpui_kit::test]
fn alerts_of_an_account_that_disappears_stay_until_reset(cx: &mut TestAppContext) {
    use codexbar_core::alerts::{AlertKind, alert_key};
    let key = alert_key("removed-account", "weekly", AlertKind::Usage);
    let kept = cx.update(|cx| {
        crate::notifications::Notifications::init(cx, Arc::new(RecordingNotifier::default()), false);
        crate::notifications::Notifications::seed_for_test(cx, [key.clone()].into());
        // A refresh without that account (removed, disabled or its provider failed) leaves its key alone.
        crate::notifications::process(cx, &[], chrono::Utc::now());
        crate::notifications::Notifications::active(cx)
    });
    assert!(kept.contains(&key));
    cx.update(crate::notifications::Notifications::reset);
    assert!(
        cx.update(|cx| crate::notifications::Notifications::active(cx))
            .is_empty()
    );
}

#[gpui_kit::test]
fn a_test_notification_windows_failed_to_raise_shows_why(cx: &mut TestAppContext) {
    let notifier = Arc::new(RecordingNotifier::default());
    let recorder = notifier.clone();
    let problem = cx.update(|cx| {
        crate::notifications::Notifications::init(cx, recorder, false);
        crate::notifications::Notifications::send_test(cx);
        // Windows accepted the sample, then raised `Failed` for it (it has no alert key).
        notifier.failed.lock().unwrap().push(String::new());
        *notifier.reason.lock().unwrap() = Some("Windows couldn't show a notification: access denied".into());
        crate::notifications::retry_failed(cx, &[], chrono::Utc::now());
        crate::notifications::Notifications::problem(cx)
    });
    assert_eq!(
        problem.as_deref(),
        Some("Windows couldn't show a notification: access denied")
    );
    assert!(
        cx.update(|cx| crate::notifications::Notifications::active(cx))
            .is_empty(),
        "no empty key is kept"
    );
}
