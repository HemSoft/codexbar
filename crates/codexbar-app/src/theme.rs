//! The HemSoft brand theme: gold on black (from the HemSoft site tokens), with orange/red reserved for status.

use gpui_kit::App;
use gpui_kit::component::{Theme, ThemeRegistry};

const THEME_FILE: &str = include_str!("../themes/codexbar.json");
const THEME_NAME: &str = "CodexBar Dark";

/// Registers the embedded brand theme and makes it current. Falls back to the stock dark theme if it fails to parse.
pub fn init(cx: &mut App) {
    if let Err(err) = ThemeRegistry::global_mut(cx).load_themes_from_str(THEME_FILE) {
        eprintln!("codexbar: brand theme failed to load: {err}");
        return;
    }
    let config = ThemeRegistry::global(cx).themes().get(THEME_NAME).cloned();
    if let Some(config) = config {
        Theme::update(cx, |theme| theme.apply_config(&config));
    }
}
