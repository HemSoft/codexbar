#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod account_table;
mod catalog;
mod dashboard;
mod focus_cards;
mod history_view;
mod notifications;
mod prefs_hub;
mod providers;
mod settings_hub;
mod settings_view;
mod status;
mod theme;
mod tray;
#[cfg(test)]
mod ui_tests;
mod zoom;

use std::sync::{Arc, Mutex};

use codexbar_store::{HistoryStore, default_history_path};
use gpui_kit::component::TitleBar;
use gpui_kit::{AppContext as _, Bounds, WindowBounds, WindowOptions, px, size};

use crate::dashboard::{Dashboard, DashboardView, DataSource};
use crate::tray::TrayCommand;

/// How long usage history is kept (#85).
const HISTORY_RETENTION: chrono::Duration = chrono::Duration::days(30);

fn is_demo() -> bool {
    std::env::var_os("CODEXBAR_DEMO").is_some_and(|value| value == "1") || std::env::args().any(|arg| arg == "--demo")
}

fn data_source() -> DataSource {
    let demo = is_demo();
    if demo {
        return DataSource::Demo;
    }
    let history = HistoryStore::open(default_history_path(), HISTORY_RETENTION, chrono::Utc::now());
    DataSource::Live {
        history: Arc::new(Mutex::new(history)),
    }
}

fn main() {
    gpui_kit::application().with_assets(gpui_kit::assets::Assets).run(|cx| {
        gpui_kit::init(cx);
        theme::init(cx);
        settings_hub::SettingsHub::init(cx);
        let dir = settings_hub::SettingsHub::global(cx).dir().to_owned();
        prefs_hub::PrefsHub::init(cx, &dir);
        // The demo never pops real notifications; its alerts are kept in memory.
        let notifier: std::sync::Arc<dyn notifications::Notifier> = if is_demo() {
            std::sync::Arc::new(notifications::RecordingNotifier::default())
        } else {
            std::sync::Arc::new(notifications::WindowsNotifier::new())
        };
        notifications::Notifications::init(cx, notifier);
        zoom::init(cx);

        let bounds = Bounds::centered(None, size(px(1440.), px(960.)), cx);
        let options = WindowOptions {
            window_bounds: Some(WindowBounds::Windowed(bounds)),
            window_min_size: Some(size(px(1040.), px(720.))),
            ..TitleBar::window_options()
        };

        let (handle, dashboard) = gpui_kit::open_window(options, cx, |window, cx| {
            // Closing hides: the window lives as long as the app, so the tray reopens the last view instantly.
            window.on_window_should_close(cx, |window, _| {
                tray::hide(window);
                false
            });
            cx.new(|cx| Dashboard::new(data_source(), window, cx))
        })
        .expect("failed to open the dashboard window");
        cx.activate(true);
        if std::env::args().any(|arg| arg == "--settings") {
            dashboard.update(cx, |dashboard, cx| dashboard.show_view(DashboardView::Settings, cx));
        }

        let result = tray::init(cx, move |command, cx| match command {
            TrayCommand::Toggle | TrayCommand::Open => {
                let _ = cx.update_window(handle, |_, window, cx| {
                    let hide = command == TrayCommand::Toggle && dashboard.read(cx).should_hide_on_toggle(window);
                    if hide { tray::hide(window) } else { tray::show(window) }
                });
            }
            TrayCommand::Refresh => dashboard.update(cx, |dashboard, cx| dashboard.refresh(cx)),
            TrayCommand::Settings => {
                dashboard.update(cx, |dashboard, cx| dashboard.show_view(DashboardView::Settings, cx));
                let _ = cx.update_window(handle, |_, window, _| tray::show(window));
            }
            TrayCommand::Quit => cx.quit(),
        });
        if let Err(err) = result {
            eprintln!("codexbar: tray icon unavailable: {err}");
        }
    });
}
