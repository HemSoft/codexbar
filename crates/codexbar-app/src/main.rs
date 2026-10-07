#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod account_table;
mod dashboard;
mod focus_cards;
mod status;
mod theme;
mod tray;

use std::sync::{Arc, Mutex};

use codexbar_providers::claude::{ClaudeProvider, default_credentials_path};
use codexbar_providers::codex::{CodexProvider, default_auth_path};
use codexbar_providers::copilot::CopilotProvider;
use codexbar_providers::{SystemCommandRunner, UreqClient};
use codexbar_store::{HistoryStore, default_history_path};
use gpui_kit::component::TitleBar;
use gpui_kit::{AppContext as _, Bounds, WindowBounds, WindowOptions, px, size};

use crate::dashboard::{Dashboard, DataSource};
use crate::tray::TrayCommand;

/// How long usage history is kept (#85).
const HISTORY_RETENTION: chrono::Duration = chrono::Duration::days(30);

fn data_source() -> DataSource {
    let demo = std::env::var_os("CODEXBAR_DEMO").is_some_and(|value| value == "1")
        || std::env::args().any(|arg| arg == "--demo");
    if demo {
        return DataSource::Demo;
    }
    let history = HistoryStore::open(default_history_path(), HISTORY_RETENTION, chrono::Utc::now());
    DataSource::Live {
        providers: vec![
            Arc::new(CodexProvider::new(UreqClient::new(), default_auth_path())),
            Arc::new(CopilotProvider::new(UreqClient::new(), SystemCommandRunner)),
            Arc::new(ClaudeProvider::new(UreqClient::new(), default_credentials_path())),
        ],
        history: Arc::new(Mutex::new(history)),
    }
}

fn main() {
    gpui_kit::application().with_assets(gpui_kit::assets::Assets).run(|cx| {
        gpui_kit::init(cx);
        theme::init(cx);

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

        let result = tray::init(cx, move |command, cx| match command {
            TrayCommand::Toggle | TrayCommand::Open => {
                let _ = cx.update_window(handle, |_, window, cx| {
                    let hide = command == TrayCommand::Toggle && dashboard.read(cx).should_hide_on_toggle(window);
                    if hide { tray::hide(window) } else { tray::show(window) }
                });
            }
            TrayCommand::Refresh => dashboard.update(cx, |dashboard, cx| dashboard.refresh(cx)),
            TrayCommand::Quit => cx.quit(),
        });
        if let Err(err) = result {
            eprintln!("codexbar: tray icon unavailable: {err}");
        }
    });
}
