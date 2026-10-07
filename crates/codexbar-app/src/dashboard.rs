//! The borderless dashboard window: title bar, view rail, urgency table, focus cards and status line.

use std::sync::{Arc, Mutex};
use std::time::Instant;

use chrono::{DateTime, Duration, Local, Utc};
use codexbar_core::{AccountId, AccountSnapshot, Metric, demo::demo_accounts, format, sort_by_urgency};
use codexbar_providers::ProviderError;
use codexbar_store::HistoryStore;

use crate::settings_hub::SettingsHub;
use gpui_kit::TestSupportExt as _;
use gpui_kit::component::sidebar::{Sidebar, SidebarMenu, SidebarMenuItem};
use gpui_kit::component::table::{DataTable, TableEvent, TableState};
use gpui_kit::component::{
    ActiveTheme as _, Icon, IconName, Sizable as _, Size, StyledExt as _, TitleBar, button::Button,
    button::ButtonVariants as _, h_flex, v_flex,
};
use gpui_kit::{
    AppContext as _, Context, Entity, FocusHandle, InteractiveElement as _, IntoElement, KeyDownEvent, MouseButton,
    ParentElement as _, Render, Role, SharedString, StatefulInteractiveElement as _, Styled as _, Subscription, Task,
    Window, canvas, div, px, rems,
};

use crate::account_table::AccountTable;
use crate::focus_cards::focus_cards;
use crate::status::{severity_dot_color, severity_tag};

/// The dashboard views. A tray click restores whichever one was open last.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DashboardView {
    Usage,
    Spend,
    History,
    Settings,
}

impl DashboardView {
    const ALL: [Self; 3] = [Self::Usage, Self::Spend, Self::History];

    fn title(self) -> &'static str {
        match self {
            Self::Usage => "Usage",
            Self::Spend => "Spend",
            Self::History => "History",
            Self::Settings => "Settings",
        }
    }

    fn icon(self) -> IconName {
        match self {
            Self::Usage => IconName::LayoutDashboard,
            Self::Spend => IconName::ChartPie,
            Self::History => IconName::Calendar,
            Self::Settings => IconName::Settings,
        }
    }
}

/// Row height of a large `DataTable` (header and body rows).
/// Row height of the account table at 100% zoom; DataTable rows are pixels, so they're scaled explicitly.
const TABLE_ROW_HEIGHT: f32 = 40.;
/// A tray click this soon after the window lost focus means "hide it": the click itself took the focus away.
const TOGGLE_GRACE: std::time::Duration = std::time::Duration::from_millis(400);

/// Where accounts come from.
pub enum DataSource {
    /// Real provider adapters, fetched off the UI thread, with their history.
    /// Providers are rebuilt from the account settings on every refresh, so edits apply at once.
    Live { history: Arc<Mutex<HistoryStore>> },
    /// Synthetic accounts for design work (`CODEXBAR_DEMO=1`).
    Demo,
}

/// A provider whose last fetch failed. Its last good accounts stay on screen.
struct Failure {
    provider: &'static str,
    message: String,
}

pub struct Dashboard {
    source: DataSource,
    accounts: Vec<AccountSnapshot>,
    failures: Vec<Failure>,
    loading: bool,
    last_refresh: Option<DateTime<Utc>>,
    table: Entity<TableState<AccountTable>>,
    selected: Option<AccountId>,
    view: DashboardView,
    now: DateTime<Utc>,
    deactivated_at: Option<Instant>,
    /// The zoom the table's column widths were last laid out for.
    table_zoom: f64,
    /// One focus handle per view tab, so the tabs are Tab-key stops activated by Enter or Space.
    view_tab_focus: Vec<FocusHandle>,
    _clock: Task<()>,
    _subscriptions: Vec<Subscription>,
}

impl Dashboard {
    pub fn new(source: DataSource, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let now = Utc::now();
        let table = cx.new(|cx| {
            TableState::new(AccountTable::new(Vec::new(), now), window, cx)
                .row_selectable(true)
                .col_selectable(false)
                .col_movable(false)
                .col_resizable(false)
                .sortable(false)
        });

        let selection = cx.subscribe(&table, |this, table, event: &TableEvent, cx| {
            if let TableEvent::SelectRow(row) = event {
                this.selected = table.read(cx).delegate().row(*row).map(|account| account.id().clone());
                cx.notify();
            }
        });
        let activation = cx.observe_window_activation(window, |this, window, _| {
            if !window.is_window_active() {
                this.deactivated_at = Some(Instant::now());
            }
        });

        // Once a second: advance the clock that ages and countdowns read, and refresh when due.
        let clock = cx.spawn(async move |this, cx| {
            loop {
                cx.background_executor().timer(std::time::Duration::from_secs(1)).await;
                let alive = this.update(cx, |this, cx| {
                    this.now = Utc::now();
                    let interval = SettingsHub::global(cx).settings().refresh_interval_secs();
                    let due = match (this.last_refresh, interval) {
                        (None, _) => true,
                        (Some(_), None) => false,
                        (Some(at), Some(secs)) => this.now - at >= Duration::seconds(secs as i64),
                    };
                    if due && !this.loading {
                        this.refresh(cx);
                    }
                    cx.notify();
                });
                if alive.is_err() {
                    break;
                }
            }
        });

        let mut dashboard = Self {
            source,
            accounts: Vec::new(),
            failures: Vec::new(),
            loading: false,
            last_refresh: None,
            table,
            selected: None,
            view: DashboardView::Usage,
            now,
            deactivated_at: None,
            table_zoom: crate::zoom::level(cx),
            view_tab_focus: DashboardView::ALL.iter().map(|_| cx.focus_handle()).collect(),
            _clock: clock,
            _subscriptions: vec![selection, activation],
        };
        dashboard.refresh(cx);
        dashboard
    }

    /// True when a tray click should hide rather than raise: the window is up and was focused until the click.
    pub fn should_hide_on_toggle(&self, window: &Window) -> bool {
        crate::tray::is_shown(window)
            && (window.is_window_active() || self.deactivated_at.is_some_and(|at| at.elapsed() < TOGGLE_GRACE))
    }

    /// Fetches every provider off the UI thread. A failed provider keeps its last good accounts.
    /// Switches the visible view (the tray's Settings… item, the `--settings` flag).
    #[cfg(test)]
    pub fn view(&self) -> DashboardView {
        self.view
    }

    pub fn show_view(&mut self, view: DashboardView, cx: &mut Context<Self>) {
        self.view = view;
        cx.notify();
    }

    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        let providers = match &self.source {
            DataSource::Demo => {
                let now = Utc::now();
                self.last_refresh = Some(now);
                self.set_accounts(demo_accounts(now, &Local), cx);
                return;
            }
            DataSource::Live { history } => history.clone(),
        };
        let history = providers;
        let providers = crate::providers::enabled(SettingsHub::global(cx));
        self.loading = true;
        cx.notify();
        cx.spawn(async move |this, cx| {
            let results = cx
                .background_spawn(async move {
                    let now = Utc::now();
                    let mut history = history.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
                    providers
                        .iter()
                        .map(|provider| {
                            let result = provider.fetch(now).map(|accounts| {
                                // A history write failure must not hide fresh usage; the next refresh retries.
                                let _ = history.record(&accounts, now);
                                accounts
                                    .into_iter()
                                    .map(|account| codexbar_store::enrich(&history, account, &Local, now))
                                    .collect()
                            });
                            (provider.name(), result)
                        })
                        .collect::<Vec<_>>()
                })
                .await;
            this.update(cx, |this, cx| this.apply_results(results, cx)).ok();
        })
        .detach();
    }

    fn apply_results(
        &mut self,
        results: Vec<(&'static str, Result<Vec<AccountSnapshot>, ProviderError>)>,
        cx: &mut Context<Self>,
    ) {
        let mut accounts = Vec::new();
        self.failures.clear();
        for (provider, result) in results {
            match result {
                Ok(fresh) => accounts.extend(fresh),
                Err(error) => {
                    // Keep last good snapshots from this provider; their age shows they are stale.
                    accounts.extend(
                        self.accounts
                            .iter()
                            .filter(|a| a.provider().display_name() == provider)
                            .cloned(),
                    );
                    self.failures.push(Failure {
                        provider,
                        message: error.to_string(),
                    });
                }
            }
        }
        self.loading = false;
        self.last_refresh = Some(Utc::now());
        self.set_accounts(accounts, cx);
    }

    fn set_accounts(&mut self, mut accounts: Vec<AccountSnapshot>, cx: &mut Context<Self>) {
        self.now = Utc::now();
        sort_by_urgency(&mut accounts, self.now);
        let selected_ix = self
            .selected
            .as_ref()
            .and_then(|id| accounts.iter().position(|a| a.id() == id))
            .unwrap_or(0);
        self.selected = accounts.get(selected_ix).map(|a| a.id().clone());
        self.accounts = accounts.clone();
        let now = self.now;
        self.table.update(cx, |table, cx| {
            *table.delegate_mut() = AccountTable::new(accounts, now);
            table.refresh(cx);
            if table.delegate().row(selected_ix).is_some() {
                table.set_selected_row(selected_ix, cx);
            }
        });
        crate::tray::set_tooltip(cx, &self.tooltip());
        cx.notify();
    }

    /// The tray hover text: the most urgent account, or the reason there is none.
    fn tooltip(&self) -> String {
        let Some(top) = self.accounts.first() else {
            return match self.failures.first() {
                Some(failure) => format!("CodexBar: {} - {}", failure.provider, failure.message),
                None => "CodexBar".to_owned(),
            };
        };
        let metric = top.primary();
        let used = metric
            .and_then(Metric::used_fraction)
            .map(|u| format!(" {:.0}%", u * 100.0))
            .unwrap_or_default();
        let reset = metric
            .and_then(Metric::resets_at)
            .map(|at| format!(", resets {}", format::reset_label(at, self.now, &Local)))
            .unwrap_or_default();
        format!("CodexBar - {}{used}{reset}", top.display_name())
    }

    fn focused(&self) -> Option<&AccountSnapshot> {
        let id = self.selected.as_ref()?;
        self.accounts.iter().find(|a| a.id() == id)
    }

    fn render_title_bar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let updated = match (self.loading, self.last_refresh) {
            (true, None) => "Loading…".to_owned(),
            (_, Some(at)) => format!("Updated {}", format::age_label(at, self.now)),
            (false, None) => String::new(),
        };
        let selected = DashboardView::ALL.iter().position(|v| *v == self.view).unwrap_or(0);

        TitleBar::new()
            // The title bar is a fixed 34px; let it grow with zoom so the tabs aren't clipped (#116).
            .h(crate::zoom::scaled(34., cx).max(px(34.)))
            .child(
                h_flex()
                    .gap_2()
                    .items_center()
                    .child(Icon::new(IconName::LayoutDashboard).text_color(cx.theme().primary))
                    .child(div().font_semibold().child("CodexBar"))
                    // The view tabs switch dashboard views; Settings has its own navigation.
                    .when(self.view != DashboardView::Settings, |this| {
                        this.child(
                            div()
                                .pl_6()
                                .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                                // gpui-kit's TabBar heights stop at 44px, which clips labels past ~250% zoom, so these
                                // underline tabs are sized in rems and follow the zoom continuously (#116).
                                .child(
                                    h_flex()
                                        .id("view-tabs")
                                        .role(Role::TabList)
                                        .gap(rems(1.25))
                                        .text_sm()
                                        .children(DashboardView::ALL.into_iter().enumerate().map(|(ix, view)| {
                                            let active = ix == selected;
                                            let ring = cx.theme().ring;
                                            div()
                                                .id(("view-tab", ix))
                                                // Same semantics as gpui-kit's Tab: announced as a selected or unselected
                                                // tab. Tab reaches the selected tab only (a roving tab stop), arrow keys move
                                                // between tabs, and a focused div turns Enter/Space into a click.
                                                .role(Role::Tab)
                                                // Lets headless UI tests find each tab (inert outside tests).
                                                .test_support()
                                                .aria_selected(active)
                                                // A tracked handle carries its own tab stop; the div's `tab_index` and
                                                // `tab_stop` only apply to handles the div creates itself.
                                                .track_focus(
                                                    &self.view_tab_focus[ix].clone().tab_index(0).tab_stop(active),
                                                )
                                                .on_key_down(cx.listener(
                                                    move |this, event: &KeyDownEvent, window, cx| {
                                                        let count = DashboardView::ALL.len();
                                                        let next = match event.keystroke.key.as_str() {
                                                            "right" => (ix + 1) % count,
                                                            "left" => (ix + count - 1) % count,
                                                            "home" => 0,
                                                            "end" => count - 1,
                                                            _ => return,
                                                        };
                                                        cx.stop_propagation();
                                                        this.view = DashboardView::ALL[next];
                                                        window.focus(&this.view_tab_focus[next], cx);
                                                        cx.notify();
                                                    },
                                                ))
                                                .focus_visible(move |style| style.text_color(ring).border_color(ring))
                                                .py(rems(0.125))
                                                .border_b_2()
                                                .border_color(if active {
                                                    cx.theme().primary
                                                } else {
                                                    cx.theme().transparent
                                                })
                                                .text_color(if active {
                                                    cx.theme().tab_active_foreground
                                                } else {
                                                    cx.theme().tab_foreground
                                                })
                                                .cursor_pointer()
                                                .hover(|style| style.text_color(cx.theme().tab_active_foreground))
                                                .on_click(cx.listener(move |this, _, _, cx| {
                                                    this.view = view;
                                                    cx.notify();
                                                }))
                                                .child(view.title())
                                        })),
                                ),
                        )
                    }),
            )
            .child(
                h_flex()
                    .items_center()
                    .gap_2()
                    .pr_2()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                    .child(div().text_sm().text_color(cx.theme().muted_foreground).child(updated))
                    .child(
                        Button::new("refresh")
                            .ghost()
                            .small()
                            .icon(IconName::RefreshCw)
                            .tooltip("Refresh now (F5)")
                            .loading(self.loading)
                            .on_click(cx.listener(|this, _, _, cx| this.refresh(cx))),
                    ),
            )
    }

    fn render_rail(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let items = DashboardView::ALL.map(|view| {
            SidebarMenuItem::new(view.title())
                .icon(view.icon())
                .active(self.view == view)
                .on_click(cx.listener(move |this, _, _, cx| {
                    this.view = view;
                    cx.notify();
                }))
        });
        Sidebar::new("rail")
            .w(rems(13.5))
            .header(
                h_flex()
                    .w_full()
                    .gap_2()
                    .px_2()
                    .py_1p5()
                    .border_1()
                    .border_color(cx.theme().input)
                    .rounded(cx.theme().radius)
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(Icon::new(IconName::Search).small())
                    .child(div().flex_1().child("Search accounts…"))
                    .child(div().text_xs().child("Ctrl+K")),
            )
            .child(
                SidebarMenu::new().children(items).child(
                    SidebarMenuItem::new("Settings")
                        .icon(IconName::Settings)
                        .active(self.view == DashboardView::Settings)
                        .on_click(cx.listener(|this, _, _, cx| {
                            this.view = DashboardView::Settings;
                            cx.notify();
                        })),
                ),
            )
    }

    fn render_usage(&self, cx: &mut Context<Self>) -> gpui_kit::AnyElement {
        let focused = self.focused();
        let cards = focused
            .map(|account| focus_cards(account, self.now, cx))
            .unwrap_or_default();
        let heading = focused.map(|account| {
            let severity = account.assess(self.now).severity();
            h_flex()
                .gap_2()
                .items_center()
                .child(div().text_xl().font_semibold().child(account.display_name()))
                .child(div().size_2().rounded_full().bg(severity_dot_color(severity, cx)))
                .children(severity_tag(severity))
        });

        let failures = self.failures.iter().map(|failure| {
            h_flex()
                .gap_2()
                .items_center()
                .px_3()
                .py_2()
                .rounded(cx.theme().radius)
                .border_1()
                .border_color(cx.theme().border)
                .text_sm()
                .child(
                    Icon::new(IconName::TriangleAlert)
                        .small()
                        .text_color(cx.theme().warning),
                )
                .child(div().font_semibold().child(failure.provider))
                .child(
                    div()
                        .text_color(cx.theme().muted_foreground)
                        .child(failure.message.clone()),
                )
        });
        if self.accounts.is_empty() {
            return v_flex()
                .flex_1()
                .gap_3()
                .children(failures)
                .child(
                    v_flex()
                        .flex_1()
                        .items_center()
                        .justify_center()
                        .gap_2()
                        .text_color(cx.theme().muted_foreground)
                        .child(Icon::new(IconName::Inbox).large())
                        .child(if self.loading {
                            "Fetching usage…"
                        } else {
                            "No accounts connected yet"
                        }),
                )
                .into_any_element();
        }

        v_flex()
            .min_h_full()
            .gap_4()
            .children(failures)
            .child(
                div()
                    .h(crate::zoom::scaled(TABLE_ROW_HEIGHT * (self.accounts.len() + 1) as f32, cx) + px(2.))
                    .flex_shrink_0()
                    .rounded(cx.theme().radius_lg)
                    .border_1()
                    .border_color(cx.theme().border)
                    .overflow_hidden()
                    .child(
                        DataTable::new(&self.table)
                            .with_size(Size::Size(crate::zoom::scaled(TABLE_ROW_HEIGHT, cx)))
                            .bordered(false)
                            .stripe(false)
                            // Zoomed in, the columns can outgrow the window; keep them reachable.
                            .scrollbar_visible(false, true),
                    ),
            )
            .child(
                v_flex()
                    .flex_1()
                    .min_h(rems(20.))
                    .gap_3()
                    .p_4()
                    .rounded(cx.theme().radius_lg)
                    .border_1()
                    .border_color(cx.theme().border)
                    .children(heading)
                    .child(h_flex().flex_1().min_h_0().items_stretch().gap_4().children(cards)),
            )
            .into_any_element()
    }

    fn render_placeholder(&self, cx: &mut Context<Self>) -> impl IntoElement {
        v_flex()
            .flex_1()
            .items_center()
            .justify_center()
            .gap_2()
            .text_color(cx.theme().muted_foreground)
            .child(Icon::new(self.view.icon()).large())
            .child(
                div()
                    .font_semibold()
                    .text_color(cx.theme().foreground)
                    .child(self.view.title()),
            )
            .child(SharedString::from("This view arrives with account history (#85, #86)."))
    }

    fn render_status_line(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let attention = self
            .accounts
            .iter()
            .filter(|a| a.assess(self.now).severity().needs_attention())
            .count();
        let interval = SettingsHub::global(cx).settings().refresh_interval_secs();
        let next_refresh = match (self.last_refresh, interval) {
            (Some(at), Some(secs)) => {
                format!(
                    "next refresh {}",
                    format::clock_countdown(at + Duration::seconds(secs as i64) - self.now)
                )
            }
            (_, None) => "auto refresh off".to_owned(),
            (None, Some(_)) => "refreshing".to_owned(),
        };
        h_flex()
            .gap_2()
            .text_sm()
            .text_color(cx.theme().muted_foreground)
            .child(match self.accounts.len() {
                1 => "1 account".to_owned(),
                n => format!("{n} accounts"),
            })
            .child("·")
            .child(
                div()
                    .when(attention > 0, |this| this.text_color(cx.theme().warning))
                    .child(format!("{attention} need attention")),
            )
            .child("·")
            .child(next_refresh)
    }
}

use gpui_kit::prelude::FluentBuilder as _;

impl Render for Dashboard {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        // DataTable caches column widths; refresh them when the zoom changes.
        let zoom = crate::zoom::level(cx);
        if (zoom - self.table_zoom).abs() > f64::EPSILON {
            self.table_zoom = zoom;
            self.table.update(cx, |table, cx| table.refresh(cx));
        }
        let body = match self.view {
            DashboardView::Usage => self.render_usage(cx),
            DashboardView::Settings => crate::settings_view::render(window, cx).into_any_element(),
            _ => self.render_placeholder(cx).into_any_element(),
        };
        v_flex()
            .relative()
            .size_full()
            .bg(cx.theme().background)
            .text_color(cx.theme().foreground)
            // Ctrl+wheel zoom is caught in the capture phase, before the table or settings list can scroll (#116).
            .child(
                canvas(|_, _, _| {}, |_, _, window, _| crate::zoom::capture_wheel(window))
                    .absolute()
                    .size_full(),
            )
            .child(self.render_title_bar(cx))
            .child(
                h_flex()
                    .flex_1()
                    .min_h_0()
                    .items_stretch()
                    .child(self.render_rail(cx))
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .p_4()
                            .gap_4()
                            // Zoomed in, the dashboard scrolls instead of squeezing its cards (#116).
                            .child(
                                div()
                                    .id("main-scroll")
                                    .flex_1()
                                    .min_h_0()
                                    .overflow_y_scroll()
                                    .child(body),
                            )
                            .child(self.render_status_line(cx)),
                    ),
            )
    }
}
