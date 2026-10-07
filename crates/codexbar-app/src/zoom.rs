//! Interface zoom (#116): Ctrl+mouse wheel and Ctrl+= / Ctrl+- / Ctrl+0, matching the WPF app's 50–300% range in
//! 10% steps. Zoom scales the theme's base font, which gpui-kit uses as the rem for type, spacing, controls and
//! icons, so everything grows together. The level is saved to the shared `zoomLevel` setting after input settles.

use std::time::Duration;

use codexbar_store::settings::{ZOOM_STEP, clamp_zoom};
use gpui_kit::component::Theme;
use gpui_kit::{App, DispatchPhase, Global, KeyBinding, ScrollDelta, ScrollWheelEvent, Window, actions, px};

use crate::settings_hub::SettingsHub;

actions!(codexbar, [ZoomIn, ZoomOut, ResetZoom]);

/// The theme's base font size at 100% (`font.size` in themes/codexbar.json).
const BASE_FONT_SIZE: f32 = 16.0;
/// Precise (touchpad) scrolling is accumulated so one notch-sized gesture is one step.
const PIXELS_PER_STEP: f32 = 50.0;
/// Saving waits for the wheel to settle so a fast scroll writes the settings file once.
const SAVE_DELAY: Duration = Duration::from_millis(600);

struct ZoomState {
    level: f64,
    /// Bumped on every change; a pending save only runs if no newer change followed it.
    generation: u64,
    pixel_carry: f32,
}

impl Global for ZoomState {}

/// The level after `steps` 10% steps from `level`, kept in range.
pub fn stepped(level: f64, steps: i32) -> f64 {
    clamp_zoom(level + f64::from(steps) * ZOOM_STEP)
}

/// Whole zoom steps for a wheel event, carrying any precise-scroll remainder. Positive zooms in.
fn wheel_steps(delta: &ScrollDelta, carry: &mut f32) -> i32 {
    match delta {
        ScrollDelta::Lines(lines) => {
            *carry = 0.0;
            // `signum` of 0.0 is 1.0, so a horizontal-only event must not count as a step.
            if lines.y == 0.0 { 0 } else { lines.y.signum() as i32 }
        }
        ScrollDelta::Pixels(pixels) => {
            *carry += f32::from(pixels.y);
            let steps = (*carry / PIXELS_PER_STEP).trunc();
            *carry -= steps * PIXELS_PER_STEP;
            steps as i32
        }
    }
}

/// Applies the saved level and registers the keyboard shortcuts. Call after `SettingsHub::init`.
pub fn init(cx: &mut App) {
    let level = SettingsHub::global(cx).settings().zoom_level();
    cx.set_global(ZoomState {
        level,
        generation: 0,
        pixel_carry: 0.0,
    });
    apply(level, cx);

    cx.bind_keys([
        KeyBinding::new("ctrl-=", ZoomIn, None),
        KeyBinding::new("ctrl-+", ZoomIn, None),
        KeyBinding::new("ctrl-shift-=", ZoomIn, None),
        KeyBinding::new("ctrl--", ZoomOut, None),
        KeyBinding::new("ctrl-0", ResetZoom, None),
    ]);
    cx.on_action(|_: &ZoomIn, cx| step(1, cx));
    cx.on_action(|_: &ZoomOut, cx| step(-1, cx));
    cx.on_action(|_: &ResetZoom, cx| set_level(1.0, cx));
}

/// Scales a size designed at 100% by the current zoom. For gpui-kit sizes given in pixels (table rows, columns)
/// that don't follow the rem.
pub fn scaled(px_at_100: f32, cx: &App) -> gpui_kit::Pixels {
    px(px_at_100 * level(cx) as f32)
}

/// The gpui-kit size tier that fits text at the current zoom, for components with fixed pixel heights.
pub fn control_size(cx: &App) -> gpui_kit::component::Size {
    size_for(level(cx))
}

fn size_for(level: f64) -> gpui_kit::component::Size {
    use gpui_kit::component::Size;
    if level < 1.2 {
        Size::Small
    } else if level < 1.6 {
        Size::Medium
    } else {
        Size::Large
    }
}

pub fn level(cx: &App) -> f64 {
    cx.try_global::<ZoomState>().map_or(1.0, |state| state.level)
}

pub fn step(steps: i32, cx: &mut App) {
    set_level(stepped(level(cx), steps), cx);
}

pub fn set_level(new_level: f64, cx: &mut App) {
    let new_level = clamp_zoom(new_level);
    let generation = {
        let state = cx.global_mut::<ZoomState>();
        if (state.level - new_level).abs() < f64::EPSILON {
            return;
        }
        state.level = new_level;
        state.generation += 1;
        state.generation
    };
    apply(new_level, cx);

    cx.spawn(async move |cx| {
        cx.background_executor().timer(SAVE_DELAY).await;
        cx.update(|cx| {
            if cx.global::<ZoomState>().generation == generation {
                let _ = SettingsHub::update(cx, |settings| {
                    settings.set_zoom_level(new_level);
                    Ok(())
                });
            }
        });
    })
    .detach();
}

fn apply(level: f64, cx: &mut App) {
    Theme::update(cx, |theme| theme.font_size = px(BASE_FONT_SIZE * level as f32));
}

/// Registers this frame's Ctrl+wheel listener in the capture phase, ahead of scrollable children, so the wheel
/// zooms instead of scrolling. Call from a paint callback; a wheel without Ctrl passes through untouched.
pub fn capture_wheel(window: &mut Window) {
    window.on_mouse_event(|event: &ScrollWheelEvent, phase, _window, cx| {
        if phase != DispatchPhase::Capture || !event.modifiers.control {
            return;
        }
        cx.stop_propagation();
        let steps = {
            let state = cx.global_mut::<ZoomState>();
            wheel_steps(&event.delta, &mut state.pixel_carry)
        };
        if steps != 0 {
            step(steps, cx);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use gpui_kit::point;

    #[test]
    fn stepped_moves_by_ten_percent_and_clamps() {
        assert_eq!(stepped(1.0, 1), 1.1);
        assert_eq!(stepped(1.0, -2), 0.8);
        assert_eq!(stepped(2.95, 1), 3.0);
        assert_eq!(stepped(3.0, 1), 3.0);
        assert_eq!(stepped(0.5, -1), 0.5);
        // Ten steps up and down return exactly to 100%.
        let mut level = 1.0;
        for _ in 0..10 {
            level = stepped(level, 1);
        }
        for _ in 0..10 {
            level = stepped(level, -1);
        }
        assert_eq!(level, 1.0);
    }

    #[test]
    fn size_for_steps_up_with_zoom() {
        use gpui_kit::component::Size;
        assert!(matches!(size_for(1.0), Size::Small));
        assert!(matches!(size_for(1.3), Size::Medium));
        assert!(matches!(size_for(2.0), Size::Large));
    }

    #[test]
    fn wheel_steps_lines_give_one_step_per_notch() {
        let mut carry = 0.0;
        assert_eq!(wheel_steps(&ScrollDelta::Lines(point(0.0, 3.0)), &mut carry), 1);
        assert_eq!(wheel_steps(&ScrollDelta::Lines(point(0.0, -1.0)), &mut carry), -1);
        assert_eq!(wheel_steps(&ScrollDelta::Lines(point(2.0, 0.0)), &mut carry), 0);
    }

    #[test]
    fn wheel_steps_pixels_accumulate_until_a_step() {
        let mut carry = 0.0;
        assert_eq!(
            wheel_steps(&ScrollDelta::Pixels(point(px(0.0), px(30.0))), &mut carry),
            0
        );
        assert_eq!(
            wheel_steps(&ScrollDelta::Pixels(point(px(0.0), px(30.0))), &mut carry),
            1
        );
        assert!((carry - 10.0).abs() < 1e-4);
        assert_eq!(
            wheel_steps(&ScrollDelta::Pixels(point(px(0.0), px(-130.0))), &mut carry),
            -2
        );
    }
}
