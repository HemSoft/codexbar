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

#[gpui_kit::test]
fn a_successful_resend_clears_the_failure_message(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-resend-clears", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let (_, notifier) = open_with_alerts(cx, &settings);
    let lost = notifier.shown.lock().unwrap()[0].clone();

    notifier.failed.lock().unwrap().extend(lost.keys().cloned());
    *notifier.reason.lock().unwrap() = Some("Windows couldn't show a notification: busy".into());
    cx.executor().advance_clock(std::time::Duration::from_secs(2));
    cx.run_until_parked();
    assert_eq!(
        cx.update(|cx| crate::notifications::Notifications::problem(cx)),
        None,
        "the resend went through"
    );
}

#[gpui_kit::test]
fn a_blocked_message_clears_once_notifications_are_on_again(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-unblock", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let notifier = Arc::new(RecordingNotifier::default());
    *notifier.blocked.lock().unwrap() = Some("Notifications are off for CodexBar.".into());
    let recorder = notifier.clone();
    cx.update(|cx| crate::notifications::Notifications::init(cx, recorder, true));
    let (_, dashboard) = open_dashboard(cx, &settings);
    assert!(
        cx.update(|cx| crate::notifications::Notifications::problem(cx))
            .is_some()
    );

    // The user turns alerts off, then notifications back on: nothing to send, but the message is out of date.
    cx.update(|cx| crate::prefs_hub::PrefsHub::update_alert_settings(cx, |alerts| alerts.enabled = false));
    *notifier.blocked.lock().unwrap() = None;
    refresh(cx, &dashboard);
    assert_eq!(cx.update(|cx| crate::notifications::Notifications::problem(cx)), None);
}

#[gpui_kit::test]
fn demo_preferences_stay_in_memory(cx: &mut TestAppContext) {
    let dir = TempSettings::new("prefs-demo", "{}");
    cx.update(|cx| {
        crate::prefs_hub::PrefsHub::init_in_memory(cx);
        crate::prefs_hub::PrefsHub::update_alert_settings(cx, |alerts| alerts.enabled = true);
    });
    assert!(
        cx.update(|cx| crate::prefs_hub::PrefsHub::alert_settings(cx)).enabled,
        "applies in memory"
    );
    assert!(!dir.0.join("dashboard.json").exists(), "nothing is written");
}

#[gpui_kit::test]
fn alerts_pause_while_preferences_cannot_be_saved(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alerts-readonly", "{}");
    std::fs::write(
        settings.0.join("dashboard.json"),
        r#"{ "version": 2, "alerts": { "enabled": true, "usageThreshold": 0.8, "balanceThreshold": 10 } }"#,
    )
    .unwrap();
    let (_, notifier) = open_with_alerts(cx, &settings);
    assert!(
        notifier.shown.lock().unwrap().is_empty(),
        "nothing is sent that couldn't be remembered"
    );
    let problem = cx
        .update(|cx| crate::notifications::Notifications::problem(cx))
        .unwrap();
    assert!(problem.starts_with("Alerts are paused"), "{problem}");
}

// --- Refresh lifecycle (#76): a live dashboard over fake providers.

use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};

use codexbar_core::{AccountId, AccountSnapshot, Metric, Provider};
use codexbar_providers::{ProviderError, UsageProvider};

use crate::account_table::AccountState;

/// A provider whose next result the test controls: `Some(used)` returns one account, `None` fails.
struct FakeProvider {
    name: &'static str,
    /// The configured account this adapter serves, like a second OpenRouter account.
    account: Option<&'static str>,
    kind: Provider,
    id: &'static str,
    next: Mutex<Option<f64>>,
    calls: AtomicUsize,
    /// A credential the adapter holds; it must never reach snapshots.json.
    _key: &'static str,
}

impl FakeProvider {
    fn new(kind: Provider, id: &'static str, used: Option<f64>) -> Arc<Self> {
        Arc::new(Self {
            name: kind.display_name(),
            account: None,
            kind,
            id,
            next: Mutex::new(used),
            calls: AtomicUsize::new(0),
            _key: "sk-test-secret-key",
        })
    }

    /// An adapter for one configured account, reporting under that account's id.
    fn for_account(kind: Provider, id: &'static str, used: Option<f64>) -> Arc<Self> {
        let mut provider = Arc::try_unwrap(Self::new(kind, id, used)).ok().unwrap();
        provider.account = Some(id);
        Arc::new(provider)
    }

    fn set(&self, used: Option<f64>) {
        *self.next.lock().unwrap() = used;
    }

    /// Makes the next fetch succeed with an account that has no metrics (unlimited quotas).
    fn set_empty(&self) {
        *self.next.lock().unwrap() = Some(f64::NAN);
    }

    fn calls(&self) -> usize {
        self.calls.load(Ordering::SeqCst)
    }
}

impl UsageProvider for FakeProvider {
    fn name(&self) -> &'static str {
        self.name
    }

    fn account_id(&self) -> Option<&str> {
        self.account
    }

    fn account_label(&self) -> Option<&str> {
        self.account.map(|id| if id == "or-1" { "Personal" } else { "Team" })
    }

    fn fetch(&self, now: chrono::DateTime<chrono::Utc>) -> Result<Vec<AccountSnapshot>, ProviderError> {
        self.calls.fetch_add(1, Ordering::SeqCst);
        match *self.next.lock().unwrap() {
            Some(used) if used.is_nan() => Ok(vec![AccountSnapshot::new(
                AccountId::new(self.id),
                self.kind,
                Vec::new(),
                now,
            )]),
            Some(used) => Ok(vec![AccountSnapshot::new(
                AccountId::new(self.id),
                self.kind,
                vec![Metric::Window {
                    label: "Weekly".into(),
                    used,
                    resets_at: now + chrono::Duration::days(3),
                    pace: None,
                }],
                now,
            )]),
            None => Err(ProviderError::Network),
        }
    }
}

/// Opens a live dashboard over `providers` without letting its first fetch run yet.
fn open_live(cx: &mut TestAppContext, settings: &TempSettings, providers: Vec<Arc<FakeProvider>>) -> Entity<Dashboard> {
    open_live_with(cx, settings, providers, MemoryCredentialStore::default())
}

/// `open_live` with a chosen credential store, such as one that always fails.
fn open_live_with(
    cx: &mut TestAppContext,
    settings: &TempSettings,
    providers: Vec<Arc<FakeProvider>>,
    store: MemoryCredentialStore,
) -> Entity<Dashboard> {
    use codexbar_store::HistoryStore;
    cx.update(|cx| {
        gpui_kit::init(cx);
        theme::init(cx);
        SettingsHub::init_with(cx, &settings.0, Arc::new(store));
        crate::prefs_hub::PrefsHub::init(cx, &settings.0);
        zoom::init(cx);
        crate::notifications::Notifications::init(cx, Arc::new(RecordingNotifier::default()), false);
    });
    let factory: crate::dashboard::ProviderFactory = Arc::new(move |_| {
        providers
            .iter()
            .map(|provider| provider.clone() as Arc<dyn UsageProvider>)
            .collect()
    });
    let mut dashboard = None;
    cx.open_window(size(px(1440.), px(960.)), |window, cx| {
        let source = DataSource::Live {
            history: Arc::new(Mutex::new(HistoryStore::in_memory(chrono::Duration::days(30)))),
            providers: factory,
        };
        let view = cx.new(|cx| Dashboard::new(source, window, cx));
        dashboard = Some(view.clone());
        Root::new(view, window, cx)
    });
    dashboard.unwrap()
}

fn ids(cx: &mut TestAppContext, dashboard: &Entity<Dashboard>) -> Vec<String> {
    cx.update(|cx| dashboard.read(cx).account_ids())
}

fn state(cx: &mut TestAppContext, dashboard: &Entity<Dashboard>, id: &str) -> AccountState {
    cx.update(|cx| dashboard.read(cx).state(id))
}

#[gpui_kit::test]
fn restored_snapshots_show_before_the_first_fetch(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-restore", "{}");
    let saved = AccountSnapshot::new(
        AccountId::new("claude-1"),
        Provider::Claude,
        Vec::new(),
        chrono::Utc::now(),
    );
    codexbar_store::snapshots::save_snapshots(&settings.0, &[saved]).unwrap();
    let claude = FakeProvider::new(Provider::Claude, "claude-1", Some(0.4));
    let dashboard = open_live(cx, &settings, vec![claude.clone()]);

    assert_eq!(
        ids(cx, &dashboard),
        vec!["claude-1"],
        "the saved account is shown at once"
    );
    assert_eq!(state(cx, &dashboard, "claude-1"), AccountState::Restored);
    cx.run_until_parked();
    assert_eq!(claude.calls(), 1);
    assert_eq!(state(cx, &dashboard, "claude-1"), AccountState::Fresh);
}

#[gpui_kit::test]
fn configured_providers_show_a_placeholder_until_their_first_result(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-placeholder", "{}");
    let cursor = FakeProvider::new(Provider::Cursor, "cursor-1", Some(0.2));
    let dashboard = open_live(cx, &settings, vec![cursor]);

    assert_eq!(ids(cx, &dashboard), vec!["pending:cursor"]);
    assert_eq!(state(cx, &dashboard, "pending:cursor"), AccountState::Loading);
    cx.run_until_parked();
    assert_eq!(
        ids(cx, &dashboard),
        vec!["cursor-1"],
        "the placeholder is replaced by the real account"
    );
}

#[gpui_kit::test]
fn overlapping_refreshes_run_one_at_a_time_with_one_follow_up(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-coalesce", "{}");
    let codex = FakeProvider::new(Provider::Codex, "codex-1", Some(0.3));
    let dashboard = open_live(cx, &settings, vec![codex.clone()]);

    // The startup fetch is running; three more requests coalesce into a single follow-up batch.
    for _ in 0..3 {
        cx.update(|cx| dashboard.update(cx, |dashboard, cx| dashboard.refresh(cx)));
    }
    cx.run_until_parked();
    assert_eq!(codex.calls(), 2, "the startup batch plus one queued follow-up");
}

#[gpui_kit::test]
fn a_failed_refresh_keeps_last_good_usage_marked_stale(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-failure", "{}");
    let claude = FakeProvider::new(Provider::Claude, "claude-1", Some(0.6));
    let dashboard = open_live(cx, &settings, vec![claude.clone()]);
    cx.run_until_parked();

    claude.set(None);
    refresh(cx, &dashboard);
    assert_eq!(ids(cx, &dashboard), vec!["claude-1"], "still shown");
    match state(cx, &dashboard, "claude-1") {
        AccountState::Failed(error) => assert!(!error.is_empty()),
        other => panic!("expected a stale account, got {other:?}"),
    }
    assert_eq!(cx.update(|cx| dashboard.read(cx).failed_providers()), vec!["Claude"]);
    let handle = cx.windows()[0];
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    let tag = label(cx, handle, "state-claude-1").unwrap();
    assert!(tag.starts_with("Stale. Refresh failed:"), "{tag}");
}

#[gpui_kit::test]
fn retry_fetches_only_the_failed_provider(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-retry", "{}");
    let claude = FakeProvider::new(Provider::Claude, "claude-1", None);
    let cursor = FakeProvider::new(Provider::Cursor, "cursor-1", Some(0.2));
    let dashboard = open_live(cx, &settings, vec![claude.clone(), cursor.clone()]);
    cx.run_until_parked();
    assert_eq!(cx.update(|cx| dashboard.read(cx).failed_providers()), vec!["Claude"]);

    claude.set(Some(0.5));
    let handle = cx.windows()[0];
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    click(cx, handle, "retry-Claude");
    assert_eq!((claude.calls(), cursor.calls()), (2, 1), "only Claude is fetched again");
    assert!(cx.update(|cx| dashboard.read(cx).failed_providers()).is_empty());
    let mut shown = ids(cx, &dashboard);
    shown.sort();
    assert_eq!(
        shown,
        vec!["claude-1", "cursor-1"],
        "the other provider's account stays"
    );
    assert_eq!(state(cx, &dashboard, "claude-1"), AccountState::Fresh);
}

#[gpui_kit::test]
fn snapshots_are_saved_after_a_refresh_without_secrets(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-persist", "{}");
    let codex = FakeProvider::new(Provider::Codex, "codex-1", Some(0.3));
    let _dashboard = open_live(cx, &settings, vec![codex]);
    cx.run_until_parked();

    let text = std::fs::read_to_string(settings.0.join("snapshots.json")).unwrap();
    assert!(text.contains("\"codex-1\""));
    assert!(!text.contains("sk-test-secret"), "only usage is stored");
    assert!(!text.contains("pending:"), "placeholders aren't stored");
    let restored = codexbar_store::snapshots::load_snapshots(&settings.0);
    assert_eq!(restored.len(), 1);
}

#[gpui_kit::test]
fn each_configured_account_of_one_provider_loads_and_fails_on_its_own(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-siblings", "{}");
    let first = FakeProvider::for_account(Provider::OpenRouter, "or-1", Some(0.4));
    let second = FakeProvider::for_account(Provider::OpenRouter, "or-2", None);
    let dashboard = open_live(cx, &settings, vec![first, second]);

    let mut before = ids(cx, &dashboard);
    before.sort();
    assert_eq!(before, vec!["or-1", "or-2"], "a Loading row per configured account");
    cx.run_until_parked();

    let mut after = ids(cx, &dashboard);
    after.sort();
    assert_eq!(after, vec!["or-1", "or-2"], "no duplicates");
    assert_eq!(
        state(cx, &dashboard, "or-1"),
        AccountState::Fresh,
        "a sibling's failure doesn't touch it"
    );
    assert!(matches!(state(cx, &dashboard, "or-2"), AccountState::Unavailable(_)));
    let handle = cx.windows()[0];
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    let tag = label(cx, handle, "state-or-2").unwrap();
    assert!(
        tag.starts_with("Unavailable. Couldn't fetch usage:"),
        "a failed first fetch isn't stale: {tag}"
    );
}

#[gpui_kit::test]
fn snapshots_of_accounts_no_longer_enabled_are_not_restored(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-restore-filter", "{}");
    let now = chrono::Utc::now();
    let kept = AccountSnapshot::new(AccountId::new("claude-1"), Provider::Claude, Vec::new(), now);
    let gone = AccountSnapshot::new(AccountId::new("cursor-1"), Provider::Cursor, Vec::new(), now);
    codexbar_store::snapshots::save_snapshots(&settings.0, &[kept, gone]).unwrap();
    // Only Claude is configured now.
    let dashboard = open_live(
        cx,
        &settings,
        vec![FakeProvider::new(Provider::Claude, "claude-1", Some(0.2))],
    );
    assert_eq!(ids(cx, &dashboard), vec!["claude-1"]);
}

#[gpui_kit::test]
fn retrying_one_account_leaves_its_siblings_and_the_schedule_alone(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-retry-account", "{}");
    let first = FakeProvider::for_account(Provider::OpenRouter, "or-1", Some(0.4));
    let second = FakeProvider::for_account(Provider::OpenRouter, "or-2", None);
    let dashboard = open_live(cx, &settings, vec![first.clone(), second.clone()]);
    cx.run_until_parked();
    let refreshed_at = cx.update(|cx| dashboard.read(cx).last_refresh_for_test());

    second.set(Some(0.3));
    let handle = cx.windows()[0];
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    click(cx, handle, "retry-OpenRouter-or-2");
    assert_eq!(
        (first.calls(), second.calls()),
        (1, 2),
        "only the failed account is fetched again"
    );
    assert_eq!(state(cx, &dashboard, "or-2"), AccountState::Fresh);
    assert_eq!(state(cx, &dashboard, "or-1"), AccountState::Fresh);
    assert_eq!(
        cx.update(|cx| dashboard.read(cx).last_refresh_for_test()),
        refreshed_at,
        "a retry doesn't push back the next full refresh"
    );
}

#[gpui_kit::test]
fn a_real_account_without_metrics_is_saved_like_any_other(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-empty-account", "{}");
    // Usage `None` makes the fake fail; an account with no metrics is a success with nothing to show.
    let copilot = FakeProvider::new(Provider::Copilot, "copilot-octocat", Some(0.0));
    copilot.set_empty();
    let _dashboard = open_live(cx, &settings, vec![copilot]);
    cx.run_until_parked();
    let saved = codexbar_store::snapshots::load_snapshots(&settings.0);
    assert_eq!(saved.len(), 1, "it is a real account, not a placeholder");
    assert_eq!(saved[0].id().as_str(), "copilot-octocat");
}

#[gpui_kit::test]
fn configured_accounts_fetched_successfully_are_saved(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-save-configured", "{}");
    let first = FakeProvider::for_account(Provider::OpenRouter, "or-1", Some(0.4));
    let _dashboard = open_live(cx, &settings, vec![first]);
    cx.run_until_parked();
    let saved = codexbar_store::snapshots::load_snapshots(&settings.0);
    let ids: Vec<&str> = saved.iter().map(|account| account.id().as_str()).collect();
    assert_eq!(
        ids,
        vec!["or-1"],
        "its placeholder shared the id, but it is a real account now"
    );
}

#[gpui_kit::test]
fn sibling_accounts_are_named_by_their_labels(cx: &mut TestAppContext) {
    let settings = TempSettings::new("lifecycle-labels", "{}");
    let first = FakeProvider::for_account(Provider::OpenRouter, "or-1", None);
    let second = FakeProvider::for_account(Provider::OpenRouter, "or-2", None);
    let dashboard = open_live(cx, &settings, vec![first, second]);
    let names = |cx: &mut TestAppContext| {
        let mut names = cx.update(|cx| dashboard.read(cx).display_names_for_test());
        names.sort();
        names
    };
    assert_eq!(
        names(cx),
        vec!["OpenRouter · Personal", "OpenRouter · Team"],
        "placeholders carry the labels"
    );
    cx.run_until_parked();
    let handle = cx.windows()[0];
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    let retry = cx
        .update_window(handle, |_, window, _| {
            window.find("retry-OpenRouter-or-2").label().map(str::to_owned)
        })
        .unwrap();
    assert_eq!(retry.as_deref(), Some("Retry OpenRouter · Team"));
}

/// A provider serving several users that reports each user's outcome, like Copilot with two GitHub accounts.
struct FakeMultiProvider {
    outcomes: Mutex<Vec<(&'static str, Option<f64>)>>,
}

impl UsageProvider for FakeMultiProvider {
    fn name(&self) -> &'static str {
        Provider::Copilot.display_name()
    }

    fn fetch(&self, _: chrono::DateTime<chrono::Utc>) -> Result<Vec<AccountSnapshot>, ProviderError> {
        unreachable!("the dashboard asks for per-account outcomes")
    }

    fn fetch_outcomes(
        &self,
        now: chrono::DateTime<chrono::Utc>,
    ) -> Result<Vec<codexbar_providers::AccountOutcome>, ProviderError> {
        use codexbar_providers::AccountOutcome;
        Ok(self
            .outcomes
            .lock()
            .unwrap()
            .iter()
            .map(|(user, used)| match used {
                Some(used) => AccountOutcome::Fresh(
                    AccountSnapshot::new(
                        AccountId::new(format!("copilot-{user}")),
                        Provider::Copilot,
                        vec![Metric::Window {
                            label: "Weekly".into(),
                            used: *used,
                            resets_at: now + chrono::Duration::days(3),
                            pace: None,
                        }],
                        now,
                    )
                    .with_label(*user),
                ),
                None => AccountOutcome::Failed {
                    account: AccountId::new(format!("copilot-{user}")),
                    label: Some((*user).to_owned()),
                    error: ProviderError::Network,
                },
            })
            .collect())
    }
}

#[gpui_kit::test]
fn one_user_failing_keeps_its_last_good_usage_and_snapshot(cx: &mut TestAppContext) {
    use codexbar_store::HistoryStore;
    let settings = TempSettings::new("outcomes-partial", "{}");
    cx.update(|cx| {
        gpui_kit::init(cx);
        theme::init(cx);
        SettingsHub::init_with(cx, &settings.0, Arc::new(MemoryCredentialStore::default()));
        crate::prefs_hub::PrefsHub::init(cx, &settings.0);
        zoom::init(cx);
        crate::notifications::Notifications::init(cx, Arc::new(RecordingNotifier::default()), false);
    });
    let provider = Arc::new(FakeMultiProvider {
        outcomes: Mutex::new(vec![("ada", Some(0.3)), ("bob", Some(0.6))]),
    });
    let factory_provider = provider.clone();
    let factory: crate::dashboard::ProviderFactory =
        Arc::new(move |_| vec![factory_provider.clone() as Arc<dyn UsageProvider>]);
    let mut dashboard = None;
    cx.open_window(size(px(1440.), px(960.)), |window, cx| {
        let source = DataSource::Live {
            history: Arc::new(Mutex::new(HistoryStore::in_memory(chrono::Duration::days(30)))),
            providers: factory,
        };
        let view = cx.new(|cx| Dashboard::new(source, window, cx));
        dashboard = Some(view.clone());
        Root::new(view, window, cx)
    });
    let dashboard = dashboard.unwrap();
    cx.run_until_parked();

    // Bob's next fetch fails while Ada's succeeds.
    *provider.outcomes.lock().unwrap() = vec![("ada", Some(0.4)), ("bob", None)];
    refresh(cx, &dashboard);
    let mut shown = ids(cx, &dashboard);
    shown.sort();
    assert_eq!(
        shown,
        vec!["copilot-ada", "copilot-bob"],
        "Bob stays with his last good usage"
    );
    assert_eq!(state(cx, &dashboard, "copilot-ada"), AccountState::Fresh);
    assert!(matches!(state(cx, &dashboard, "copilot-bob"), AccountState::Failed(_)));
    let saved: Vec<String> = codexbar_store::snapshots::load_snapshots(&settings.0)
        .iter()
        .map(|account| account.id().as_str().to_owned())
        .collect();
    assert!(
        saved.contains(&"copilot-bob".to_owned()),
        "his snapshot survives a restart"
    );
}

#[gpui_kit::test]
fn provider_messages_show_with_the_focused_account(cx: &mut TestAppContext) {
    let settings = TempSettings::new("messages", "{}");
    let saved = AccountSnapshot::new(
        AccountId::new("claude-1"),
        Provider::Claude,
        Vec::new(),
        chrono::Utc::now(),
    )
    .with_message("Usage is delayed by up to an hour");
    codexbar_store::snapshots::save_snapshots(&settings.0, &[saved]).unwrap();
    // The provider is down, so the restored snapshot (and its message) stays.
    let _dashboard = open_live(
        cx,
        &settings,
        vec![FakeProvider::new(Provider::Claude, "claude-1", None)],
    );
    cx.run_until_parked();
    let handle = cx.windows()[0];
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    let message = cx
        .update_window(handle, |_, window, _| {
            window
                .try_find(("provider-message", 0usize))
                .and_then(|found| found.label().map(str::to_owned))
        })
        .unwrap();
    assert_eq!(message.as_deref(), Some("Usage is delayed by up to an hour"));
}

/// The aria label of the element with `id` in the first window, after a fresh frame.
fn label_of(cx: &mut TestAppContext, id: impl Into<gpui_kit::ElementId> + Clone) -> Option<String> {
    let handle = cx.windows()[0];
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    cx.update_window(handle, |_, window, _| {
        window
            .try_find(id.clone())
            .and_then(|found| found.label().map(str::to_owned))
    })
    .unwrap()
}

#[gpui_kit::test]
fn the_focused_account_shows_its_alert_and_keeps_it_after_notifying(cx: &mut TestAppContext) {
    let settings = TempSettings::new("alert-details", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    let provider = FakeProvider::new(Provider::Claude, "claude-1", Some(0.86));
    let dashboard = open_live(cx, &settings, vec![provider.clone()]);
    cx.run_until_parked();
    let strongest = label_of(cx, "alert-strongest").expect("an alert is shown");
    assert!(
        strongest.starts_with("Usage alert: Weekly. 86% · Alert at 80% · resets in "),
        "{strongest}"
    );
    // A refresh that sends no new notification keeps the alert on screen.
    refresh(cx, &dashboard);
    assert!(label_of(cx, "alert-strongest").is_some());
    // Once usage falls below the recovery line, it goes.
    provider.set(Some(0.4));
    refresh(cx, &dashboard);
    assert_eq!(label_of(cx, "alert-strongest"), None);
    assert_eq!(
        label_of(cx, "projected-status"),
        None,
        "observed usage, not a projection"
    );
}

const MANUAL_ORDER: &str = r#"{ "version": 1, "orderMode": "manual" }"#;

fn shown_ids(cx: &mut TestAppContext, dashboard: &Entity<Dashboard>) -> Vec<String> {
    cx.update(|cx| dashboard.read(cx).account_ids())
}

#[gpui_kit::test]
fn groups_arrange_the_table_and_are_saved(cx: &mut TestAppContext) {
    let settings = TempSettings::new("groups", "{}");
    std::fs::write(settings.0.join("dashboard.json"), MANUAL_ORDER).unwrap();
    let dashboard = open_live(
        cx,
        &settings,
        vec![
            FakeProvider::new(Provider::Cursor, "cursor", Some(0.9)),
            FakeProvider::new(Provider::Claude, "claude-1", Some(0.2)),
            FakeProvider::new(Provider::Codex, "codex", Some(0.5)),
        ],
    );
    cx.run_until_parked();
    // Without groups: provider order, not fetch order or urgency.
    assert_eq!(shown_ids(cx, &dashboard), ["codex", "claude-1", "cursor"]);
    cx.update(|cx| {
        crate::prefs_hub::PrefsHub::update_layout(cx, |layout| {
            let work = layout.create_group("Work")?;
            layout.assign("cursor", Some(&work))
        })
    })
    .unwrap();
    cx.run_until_parked();
    assert_eq!(
        shown_ids(cx, &dashboard),
        ["cursor", "codex", "claude-1"],
        "Work, then Ungrouped"
    );
    let saved = codexbar_store::prefs::DashboardPrefs::load(&settings.0);
    assert_eq!(saved.layout().group_of("cursor").map(|g| g.name.as_str()), Some("Work"));
}

#[gpui_kit::test]
fn move_down_reorders_the_focused_account_within_its_group(cx: &mut TestAppContext) {
    let settings = TempSettings::new("groups-move", "{}");
    std::fs::write(settings.0.join("dashboard.json"), MANUAL_ORDER).unwrap();
    let dashboard = open_live(
        cx,
        &settings,
        vec![
            FakeProvider::new(Provider::Codex, "codex", Some(0.5)),
            FakeProvider::new(Provider::Claude, "claude-1", Some(0.2)),
        ],
    );
    cx.run_until_parked();
    let handle = cx.windows()[0];
    cx.update(|cx| dashboard.update(cx, |dashboard, cx| dashboard.select_row(0, cx)));
    cx.run_until_parked();
    click(cx, handle, "move-down");
    assert_eq!(shown_ids(cx, &dashboard), ["claude-1", "codex"]);
    let saved = codexbar_store::prefs::DashboardPrefs::load(&settings.0);
    assert_eq!(saved.layout().order(), ["claude-1".to_owned(), "codex".to_owned()]);
}

#[gpui_kit::test]
fn smart_order_puts_urgent_accounts_first_and_manual_comes_back(cx: &mut TestAppContext) {
    let settings = TempSettings::new("smart-order", "{}");
    let dashboard = open_live(
        cx,
        &settings,
        vec![
            FakeProvider::new(Provider::Codex, "codex", Some(0.2)),
            FakeProvider::new(Provider::Claude, "claude-1", Some(0.97)),
            FakeProvider::new(Provider::Cursor, "cursor", Some(0.85)),
        ],
    );
    cx.run_until_parked();
    // Smart by default: Limit soon, then Watch, then calm.
    assert_eq!(shown_ids(cx, &dashboard), ["claude-1", "cursor", "codex"]);
    // Manual shows the default order; Smart never wrote to it.
    let handle = cx.windows()[0];
    click(cx, handle, ("order-mode", 1usize));
    assert_eq!(shown_ids(cx, &dashboard), ["codex", "claude-1", "cursor"]);
    let saved = codexbar_store::prefs::DashboardPrefs::load(&settings.0);
    assert_eq!(saved.layout().mode(), codexbar_core::layout::OrderMode::Manual);
    assert!(saved.layout().order().is_empty());
    click(cx, handle, ("order-mode", 0usize));
    assert_eq!(shown_ids(cx, &dashboard), ["claude-1", "cursor", "codex"]);
}

#[gpui_kit::test]
fn smart_order_keeps_failed_accounts_visible_after_current_ones(cx: &mut TestAppContext) {
    let settings = TempSettings::new("smart-failed", "{}");
    let codex = FakeProvider::new(Provider::Codex, "codex", Some(0.1));
    let claude = FakeProvider::new(Provider::Claude, "claude-1", Some(0.1));
    let dashboard = open_live(cx, &settings, vec![codex.clone(), claude.clone()]);
    cx.run_until_parked();
    assert_eq!(shown_ids(cx, &dashboard), ["codex", "claude-1"]);
    // Codex fails: its last known usage stays visible, below the account whose usage is current.
    codex.set(None);
    refresh(cx, &dashboard);
    assert_eq!(shown_ids(cx, &dashboard), ["claude-1", "codex"]);
}

#[gpui_kit::test]
fn smart_order_re_ranks_when_held_alerts_change(cx: &mut TestAppContext) {
    let settings = TempSettings::new("smart-alerts", "{}");
    std::fs::write(settings.0.join("dashboard.json"), ALERTS_ON).unwrap();
    // Both under the 80% alert; Codex is busier, so it leads.
    let dashboard = open_live(
        cx,
        &settings,
        vec![
            FakeProvider::new(Provider::Claude, "claude-1", Some(0.77)),
            FakeProvider::new(Provider::Codex, "codex", Some(0.78)),
        ],
    );
    cx.run_until_parked();
    assert_eq!(shown_ids(cx, &dashboard), ["codex", "claude-1"]);
    // Claude's usage alert is held (it dipped inside the recovery margin): it now outranks Codex, without a refresh.
    let key = cx.update(|cx| {
        let account = dashboard.read(cx).account("claude-1").unwrap();
        codexbar_core::alerts::alert_key(
            "claude-1",
            &codexbar_core::alerts::metric_slot(&account.metrics()[0]),
            codexbar_core::alerts::AlertKind::Usage,
        )
    });
    cx.update(|cx| crate::notifications::Notifications::seed_for_test(cx, [key].into_iter().collect()));
    cx.run_until_parked();
    assert_eq!(shown_ids(cx, &dashboard), ["claude-1", "codex"]);
}

// Settings (#91): navigation, refresh choices, the account dialog by keyboard, cancellation, removal, reset and
// secure-store failures, all headless.

fn open_settings_page(
    cx: &mut TestAppContext,
    settings: &TempSettings,
    store: MemoryCredentialStore,
    page: usize,
) -> AnyWindowHandle {
    let dashboard = open_live_with(cx, settings, vec![], store);
    cx.run_until_parked();
    let handle = cx.windows()[0];
    cx.update(|cx| dashboard.update(cx, |dashboard, cx| dashboard.show_view(DashboardView::Settings, cx)));
    cx.update_window(handle, |_, window, cx| window.render_frame(cx))
        .unwrap();
    cx.update_window(handle, |_, window, cx| {
        window.within("settings-sidebar").click(format!("0-{page}"), cx)
    })
    .unwrap();
    cx.run_until_parked();
    handle
}

fn type_text(cx: &mut TestAppContext, handle: AnyWindowHandle, text: &str) {
    cx.update_window(handle, |_, window, cx| window.input(text, cx))
        .unwrap();
    cx.run_until_parked();
}

fn saved_settings(settings: &TempSettings) -> codexbar_store::settings::Settings {
    codexbar_store::settings::Settings::load_or_default(&settings.0)
}

fn secret_of(cx: &mut TestAppContext, id: &str) -> Option<String> {
    cx.update(|cx| SettingsHub::global(cx).credentials().read(id)).unwrap()
}

/// Adds an OpenRouter account through the dialog, by keyboard: the Name field has focus with its text selected, and
/// Tab moves past the sign-in method to the key.
fn add_openrouter(cx: &mut TestAppContext, handle: AnyWindowHandle, name: &str, key: &str) {
    click(cx, handle, "add-OpenRouter");
    type_text(cx, handle, name);
    press(cx, handle, "tab");
    press(cx, handle, "tab");
    type_text(cx, handle, key);
    click(cx, handle, "account-save");
}

fn exists(cx: &mut TestAppContext, handle: AnyWindowHandle, id: impl Into<gpui_kit::ElementId>) -> bool {
    let id = id.into();
    cx.update_window(handle, |_, window, cx| {
        window.render_frame(cx);
        window.try_find(id).is_some()
    })
    .unwrap()
}

#[gpui_kit::test]
fn every_settings_section_can_be_opened(cx: &mut TestAppContext) {
    let settings = TempSettings::new("settings-nav", "{}");
    let handle = open_settings_page(cx, &settings, MemoryCredentialStore::default(), 0);
    // General, Accounts, Alerts, Groups, Appearance, Widgets, About: each page shows its own controls.
    let marks: [(usize, Option<&'static str>); 7] = [
        (0, None),
        (1, Some("add-OpenRouter")),
        (2, Some("alerts-status")),
        (3, Some("group-new")),
        (4, None),
        (5, None),
        (6, Some("open-repo")),
    ];
    for (page, mark) in marks {
        cx.update_window(handle, |_, window, cx| {
            window.within("settings-sidebar").click(format!("0-{page}"), cx)
        })
        .unwrap();
        cx.run_until_parked();
        if let Some(mark) = mark {
            assert!(exists(cx, handle, mark), "page {page} shows {mark}");
        }
    }
    cx.update_window(handle, |_, window, cx| {
        window.within("settings-sidebar").click("0-0", cx)
    })
    .unwrap();
    cx.run_until_parked();
    let refresh = cx
        .update_window(handle, |_, window, _| {
            window
                .within("group-0")
                .within("item-0")
                .within("field")
                .find("btn")
                .label()
                .map(str::to_owned)
        })
        .unwrap();
    assert_eq!(
        refresh.as_deref(),
        Some("Every 2 minutes"),
        "General shows auto refresh"
    );
}

#[gpui_kit::test]
fn auto_refresh_offers_the_standard_choices_and_saves_them(cx: &mut TestAppContext) {
    let settings = TempSettings::new("settings-refresh", "{}");
    let handle = open_settings_page(cx, &settings, MemoryCredentialStore::default(), 0);
    let choose = |cx: &mut TestAppContext, downs: usize| {
        cx.update_window(handle, |_, window, cx| {
            window
                .within("group-0")
                .within("item-0")
                .within("field")
                .click("btn", cx)
        })
        .unwrap();
        cx.run_until_parked();
        for _ in 0..downs {
            press(cx, handle, "down");
        }
        press(cx, handle, "enter");
    };
    // The WPF default of 2 minutes is kept as its own choice: Off, 1, 2, 5, 15, 30, 60. The first is Off.
    choose(cx, 1);
    assert_eq!(saved_settings(&settings).refresh_interval_secs(), None);
    // Now the standard six: Off, 1, 5, 15, 30, 60.
    choose(cx, 6);
    assert_eq!(saved_settings(&settings).refresh_interval_secs(), Some(3600));
    choose(cx, 3);
    assert_eq!(saved_settings(&settings).refresh_interval_secs(), Some(300));
}

#[gpui_kit::test]
fn an_account_added_by_keyboard_is_saved_with_its_key(cx: &mut TestAppContext) {
    let settings = TempSettings::new("settings-add", "{}");
    let handle = open_settings_page(cx, &settings, MemoryCredentialStore::default(), 1);
    add_openrouter(cx, handle, "Work", "sk-or-test");
    let saved = saved_settings(&settings);
    let record = saved
        .accounts()
        .iter()
        .find(|a| a.provider == "OpenRouter")
        .expect("saved");
    assert_eq!(record.label, "Work");
    assert_eq!(secret_of(cx, &record.id).as_deref(), Some("sk-or-test"));
    let text = std::fs::read_to_string(settings.0.join("settings.json")).unwrap();
    assert!(
        !text.contains("sk-or-test"),
        "the key is never written to settings.json"
    );
    let detail = label(
        cx,
        handle,
        Box::leak(format!("account-detail-{}", record.id).into_boxed_str()),
    );
    // An OPENROUTER_API_KEY in the environment wins over the saved key, and the detail says so. Never the key.
    let expected = if std::env::var_os("OPENROUTER_API_KEY").is_some() {
        "API key · From the OPENROUTER_API_KEY environment variable"
    } else {
        "API key · Saved in Windows Credential Manager"
    };
    assert_eq!(detail.as_deref(), Some(expected));
}

#[gpui_kit::test]
fn cancelling_the_account_dialog_saves_nothing(cx: &mut TestAppContext) {
    let settings = TempSettings::new("settings-cancel", "{}");
    let handle = open_settings_page(cx, &settings, MemoryCredentialStore::default(), 1);
    click(cx, handle, "add-OpenRouter");
    type_text(cx, handle, "Draft");
    press(cx, handle, "escape");
    assert!(!exists(cx, handle, "account-save"), "the dialog closed");
    assert!(saved_settings(&settings).accounts().is_empty());
}

#[gpui_kit::test]
fn accounts_can_be_renamed_disabled_and_removed_after_confirming(cx: &mut TestAppContext) {
    let settings = TempSettings::new("settings-edit", "{}");
    let handle = open_settings_page(cx, &settings, MemoryCredentialStore::default(), 1);
    add_openrouter(cx, handle, "Work", "sk-or-test");
    let id = saved_settings(&settings).accounts()[0].id.clone();

    click(cx, handle, format!("edit-{id}"));
    type_text(cx, handle, "Personal");
    click(cx, handle, "account-save");
    assert_eq!(saved_settings(&settings).accounts()[0].label, "Personal");
    assert_eq!(
        secret_of(cx, &id).as_deref(),
        Some("sk-or-test"),
        "a blank key keeps the saved one"
    );

    click(cx, handle, format!("enabled-{id}"));
    assert!(!saved_settings(&settings).accounts()[0].enabled);

    // Removing asks first: Escape keeps the account, Enter removes it and its key.
    click(cx, handle, format!("remove-{id}"));
    press(cx, handle, "escape");
    assert_eq!(saved_settings(&settings).accounts().len(), 1);
    click(cx, handle, format!("remove-{id}"));
    press(cx, handle, "enter");
    assert!(saved_settings(&settings).accounts().is_empty());
    assert_eq!(secret_of(cx, &id), None);
}

#[gpui_kit::test]
fn reset_accounts_asks_first_then_removes_accounts_and_keys(cx: &mut TestAppContext) {
    let settings = TempSettings::new("settings-reset", "{}");
    let handle = open_settings_page(cx, &settings, MemoryCredentialStore::default(), 1);
    add_openrouter(cx, handle, "Work", "sk-one");
    add_openrouter(cx, handle, "Team", "sk-two");
    let ids: Vec<String> = saved_settings(&settings)
        .accounts()
        .iter()
        .map(|a| a.id.clone())
        .collect();
    assert_eq!(ids.len(), 2);
    // Reset is the last group on the Accounts page; its sidebar entry scrolls it into view.
    let show_reset = |cx: &mut TestAppContext| {
        cx.update_window(handle, |_, window, cx| {
            window.within("settings-sidebar").click("0-1-8", cx)
        })
        .unwrap();
        cx.run_until_parked();
    };
    show_reset(cx);
    click(cx, handle, "reset-accounts");
    press(cx, handle, "escape");
    assert_eq!(saved_settings(&settings).accounts().len(), 2, "cancelled");
    show_reset(cx);
    click(cx, handle, "reset-accounts");
    press(cx, handle, "enter");
    assert!(saved_settings(&settings).accounts().is_empty());
    for id in ids {
        assert_eq!(secret_of(cx, &id), None);
    }
}

#[gpui_kit::test]
fn secure_store_failures_are_shown_and_nothing_is_saved_without_its_key(cx: &mut TestAppContext) {
    let settings = TempSettings::new("settings-store-fail", "{}");
    let handle = open_settings_page(cx, &settings, MemoryCredentialStore::failing(), 1);
    add_openrouter(cx, handle, "Work", "sk-secret-value");
    let error = label(cx, handle, "account-error").expect("the dialog says why");
    assert_eq!(error, "Windows Credential Manager failed (error 5).");
    assert!(!error.contains("sk-secret-value"));
    assert!(exists(cx, handle, "account-save"), "the dialog stays open");
    assert!(
        saved_settings(&settings).accounts().is_empty(),
        "no account without its key"
    );
}
