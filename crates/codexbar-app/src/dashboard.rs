//! The borderless dashboard window: title bar, view rail, urgency table, focus cards and status line.

use chrono::{DateTime, Duration, Local, Utc};
use codexbar_core::{AccountSnapshot, demo::demo_accounts, format, sort_by_urgency};
use gpui_kit::component::sidebar::{Sidebar, SidebarMenu, SidebarMenuItem};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::table::{DataTable, TableEvent, TableState};
use gpui_kit::component::{
    ActiveTheme as _, Icon, IconName, Sizable as _, StyledExt as _, TitleBar, button::Button,
    button::ButtonVariants as _, h_flex, v_flex,
};
use gpui_kit::{
    AppContext as _, Context, Entity, InteractiveElement as _, IntoElement, MouseButton, ParentElement as _, Render,
    SharedString, Styled as _, Subscription, Window, div, px,
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
}

impl DashboardView {
    const ALL: [Self; 3] = [Self::Usage, Self::Spend, Self::History];

    fn title(self) -> &'static str {
        match self {
            Self::Usage => "Usage",
            Self::Spend => "Spend",
            Self::History => "History",
        }
    }

    fn icon(self) -> IconName {
        match self {
            Self::Usage => IconName::LayoutDashboard,
            Self::Spend => IconName::ChartPie,
            Self::History => IconName::Calendar,
        }
    }
}

const REFRESH_INTERVAL_SECS: i64 = 120;
/// Row height of a large `DataTable` (header and body rows).
const TABLE_ROW_HEIGHT: gpui_kit::Pixels = px(40.);

pub struct Dashboard {
    accounts: Vec<AccountSnapshot>,
    table: Entity<TableState<AccountTable>>,
    selected: usize,
    view: DashboardView,
    now: DateTime<Utc>,
    _subscriptions: Vec<Subscription>,
}

impl Dashboard {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let now = Utc::now();
        let mut accounts = demo_accounts(now, &Local);
        sort_by_urgency(&mut accounts, now);

        let table = cx.new(|cx| {
            TableState::new(AccountTable::new(accounts.clone(), now), window, cx)
                .row_selectable(true)
                .col_selectable(false)
                .col_movable(false)
                .col_resizable(false)
                .sortable(false)
        });
        table.update(cx, |table, cx| table.set_selected_row(0, cx));

        let subscription = cx.subscribe(&table, |this, _, event: &TableEvent, cx| {
            if let TableEvent::SelectRow(row) = event {
                this.selected = *row;
                cx.notify();
            }
        });

        Self {
            accounts,
            table,
            selected: 0,
            view: DashboardView::Usage,
            now,
            _subscriptions: vec![subscription],
        }
    }

    fn focused(&self) -> Option<&AccountSnapshot> {
        self.accounts.get(self.selected)
    }

    fn render_title_bar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let updated = self
            .accounts
            .iter()
            .map(AccountSnapshot::fetched_at)
            .max()
            .map(|at| format!("Updated {}", format::age_label(at, self.now)))
            .unwrap_or_default();
        let selected = DashboardView::ALL.iter().position(|v| *v == self.view).unwrap_or(0);

        TitleBar::new()
            .child(
                h_flex()
                    .gap_2()
                    .items_center()
                    .child(Icon::new(IconName::LayoutDashboard).text_color(cx.theme().primary))
                    .child(div().font_semibold().child("CodexBar"))
                    .child(
                        div()
                            .pl_6()
                            .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                            .child(
                                TabBar::new("views")
                                    .underline()
                                    .small()
                                    .selected_index(selected)
                                    .on_click(cx.listener(|this, ix: &usize, _, cx| {
                                        this.view = DashboardView::ALL[*ix];
                                        cx.notify();
                                    }))
                                    .children(DashboardView::ALL.map(|view| Tab::new().label(view.title()))),
                            ),
                    ),
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
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.now = Utc::now();
                                cx.notify();
                            })),
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
            .w(px(216.))
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
                SidebarMenu::new()
                    .children(items)
                    .child(SidebarMenuItem::new("Accounts").icon(IconName::CircleUser))
                    .child(SidebarMenuItem::new("Settings").icon(IconName::Settings)),
            )
    }

    fn render_usage(&self, cx: &mut Context<Self>) -> impl IntoElement {
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

        v_flex()
            .flex_1()
            .min_h_0()
            .gap_4()
            .child(
                div()
                    .h(TABLE_ROW_HEIGHT * (self.accounts.len() + 1) as f32 + px(2.))
                    .flex_shrink_0()
                    .rounded(cx.theme().radius_lg)
                    .border_1()
                    .border_color(cx.theme().border)
                    .overflow_hidden()
                    .child(
                        DataTable::new(&self.table)
                            .large()
                            .bordered(false)
                            .stripe(false)
                            .scrollbar_visible(false, false),
                    ),
            )
            .child(
                v_flex()
                    .flex_1()
                    .min_h(px(320.))
                    .gap_3()
                    .p_4()
                    .rounded(cx.theme().radius_lg)
                    .border_1()
                    .border_color(cx.theme().border)
                    .children(heading)
                    .child(h_flex().flex_1().min_h_0().items_stretch().gap_4().children(cards)),
            )
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
        let next_refresh = self
            .accounts
            .iter()
            .map(AccountSnapshot::fetched_at)
            .max()
            .map(|at| at + Duration::seconds(REFRESH_INTERVAL_SECS) - self.now)
            .unwrap_or_default();
        h_flex()
            .gap_2()
            .text_sm()
            .text_color(cx.theme().muted_foreground)
            .child(format!("{} accounts", self.accounts.len()))
            .child("·")
            .child(
                div()
                    .when(attention > 0, |this| this.text_color(cx.theme().warning))
                    .child(format!("{attention} need attention")),
            )
            .child("·")
            .child(format!("next refresh {}", format::clock_countdown(next_refresh)))
    }
}

use gpui_kit::prelude::FluentBuilder as _;

impl Render for Dashboard {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let body = match self.view {
            DashboardView::Usage => self.render_usage(cx).into_any_element(),
            _ => self.render_placeholder(cx).into_any_element(),
        };
        v_flex()
            .size_full()
            .bg(cx.theme().background)
            .text_color(cx.theme().foreground)
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
                            .child(body)
                            .child(self.render_status_line(cx)),
                    ),
            )
    }
}
