#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod account_table;
mod dashboard;
mod focus_cards;
mod status;
mod theme;

use gpui_kit::component::TitleBar;
use gpui_kit::{AppContext as _, Bounds, WindowBounds, WindowOptions, px, size};

use crate::dashboard::Dashboard;

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
        gpui_kit::open_window(options, cx, |window, cx| cx.new(|cx| Dashboard::new(window, cx)))
            .expect("failed to open the dashboard window");
        cx.activate(true);
    });
}
