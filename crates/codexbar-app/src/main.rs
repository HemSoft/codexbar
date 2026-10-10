#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod account_table;
mod alert_details;
mod brand;
mod catalog;
mod codex_sign_in;
mod dashboard;
mod focus_cards;
mod github_sign_in;
mod groups_page;
mod history_view;
mod locale;
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
        providers: Arc::new(providers::enabled),
    }
}

/// One CodexBar per Windows user, across sessions (two Remote Desktop sessions share one profile), so two instances
/// never send the same alert twice or write history and preferences over each other. The mutex is in the global
/// namespace and named per user, so other users on the machine run their own. The demo has its own name, so design
/// work can run beside the real app. A second launch exits; the first one is already in the notification area.
fn already_running() -> bool {
    use windows::Win32::Foundation::{ERROR_ALREADY_EXISTS, GetLastError};
    use windows::Win32::System::Threading::CreateMutexW;
    use windows::core::HSTRING;
    let user: String = format!(
        "{}.{}",
        std::env::var("USERDOMAIN").unwrap_or_default(),
        std::env::var("USERNAME").unwrap_or_default()
    )
    .chars()
    .filter(|ch| *ch != '\\')
    .collect();
    let name = if is_demo() {
        format!(r"Global\HemSoft.CodexBar.Demo.{user}")
    } else {
        format!(r"Global\HemSoft.CodexBar.{user}")
    };
    // SAFETY: a named mutex with default security; the handle is kept open for the life of the process.
    match unsafe { CreateMutexW(None, false, &HSTRING::from(name)) } {
        // The handle is never closed: Windows releases the mutex when the process exits.
        Ok(_handle) => (unsafe { GetLastError() }) == ERROR_ALREADY_EXISTS,
        // Without the mutex, run anyway rather than refuse to start.
        Err(_) => false,
    }
}

fn main() {
    if already_running() {
        eprintln!("codexbar: already running; open it from the notification area");
        return;
    }
    gpui_kit::application().with_assets(gpui_kit::assets::Assets).run(|cx| {
        gpui_kit::init(cx);
        settings_hub::SettingsHub::init(cx);
        codex_sign_in::init(cx);
        github_sign_in::init(cx);
        github_sign_in::clean_up(cx);
        let dir = settings_hub::SettingsHub::global(cx).dir().to_owned();
        if is_demo() {
            prefs_hub::PrefsHub::init_in_memory(cx);
        } else {
            prefs_hub::PrefsHub::init(cx, &dir);
        }
        // After the preferences, so the saved appearance applies from the first frame.
        theme::init(cx);
        // The demo never pops real notifications; its alerts are kept in memory.
        let notifier: std::sync::Arc<dyn notifications::Notifier> = if is_demo() {
            std::sync::Arc::new(notifications::RecordingNotifier::default())
        } else {
            std::sync::Arc::new(notifications::WindowsNotifier::new())
        };
        notifications::Notifications::init(cx, notifier, !is_demo());
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

        let tooltip_source = dashboard.clone();
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
        // Restored accounts were shown before the tray icon existed; give it their text now.
        tooltip_source.update(cx, |dashboard, cx| dashboard.publish_tooltip(cx));
    });
}
