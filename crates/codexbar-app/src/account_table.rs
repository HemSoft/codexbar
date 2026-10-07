//! The urgency-ordered account table on the Usage view.

use std::collections::HashMap;

use chrono::{DateTime, Local, Utc};
use codexbar_core::{AccountSnapshot, Metric, format};
use codexbar_store::summary::HistorySummary;
use gpui_kit::component::chart::AreaChart;
use gpui_kit::component::progress::Progress;
use gpui_kit::component::table::{Column, TableDelegate, TableState};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, IconName, Sizable as _, h_flex};
use gpui_kit::{
    Context, ElementId, InteractiveElement as _, IntoElement, ParentElement as _, Role, SharedString,
    StatefulInteractiveElement as _, Styled as _, Window, div, linear_color_stop, linear_gradient, rems,
};

use crate::history_view::{ValueKind, describe};
use crate::prefs_hub::PrefsHub;
use gpui_kit::TestSupportExt as _;

use crate::status::{severity_color, severity_dot_color, severity_tag};
use crate::zoom::scaled;

#[derive(Clone, Copy)]
enum Col {
    Account,
    Limit,
    Used,
    Percent,
    Progress,
    Resets,
    Trend,
}

const COLUMNS: [Col; 7] = [
    Col::Account,
    Col::Limit,
    Col::Used,
    Col::Percent,
    Col::Progress,
    Col::Resets,
    Col::Trend,
];

/// Each account's stored history summary for its primary metric, by account id.
pub type Compact = HashMap<String, (HistorySummary, ValueKind)>;

/// Supplies rows for the gpui-kit `DataTable`. Rows arrive already sorted by urgency.
pub struct AccountTable {
    rows: Vec<AccountSnapshot>,
    now: DateTime<Utc>,
    compact: Compact,
}

impl AccountTable {
    pub fn new(rows: Vec<AccountSnapshot>, now: DateTime<Utc>, compact: Compact) -> Self {
        Self { rows, now, compact }
    }

    pub fn set_compact(&mut self, compact: Compact) {
        self.compact = compact;
    }

    pub fn row(&self, ix: usize) -> Option<&AccountSnapshot> {
        self.rows.get(ix)
    }
}

impl TableDelegate for AccountTable {
    fn columns_count(&self, _: &gpui_kit::App) -> usize {
        COLUMNS.len()
    }

    fn rows_count(&self, _: &gpui_kit::App) -> usize {
        self.rows.len()
    }

    fn column(&self, col_ix: usize, cx: &gpui_kit::App) -> Column {
        match COLUMNS[col_ix] {
            // DataTable widths are pixels, so they follow the interface zoom explicitly (#116).
            Col::Account => Column::new("account", "Account")
                .width(scaled(320., cx))
                .min_width(scaled(220., cx)),
            Col::Limit => Column::new("limit", "Limit").width(scaled(150., cx)),
            Col::Used => Column::new("used", "Used").width(scaled(110., cx)).text_right(),
            Col::Percent => Column::new("percent", "%").width(scaled(64., cx)).text_right(),
            Col::Progress => Column::new("progress", "Progress").width(scaled(200., cx)),
            Col::Resets => Column::new("resets", "Resets").width(scaled(110., cx)),
            Col::Trend => Column::new("trend", "14-day trend").width(scaled(200., cx)),
        }
    }

    fn render_td(
        &mut self,
        row_ix: usize,
        col_ix: usize,
        _: &mut Window,
        cx: &mut Context<TableState<Self>>,
    ) -> impl IntoElement {
        let Some(row) = self.rows.get(row_ix) else {
            return div().into_any_element();
        };
        let assessment = row.assess(self.now);
        let severity = assessment.severity();
        let primary = row.primary();
        let muted = cx.theme().muted_foreground;

        match COLUMNS[col_ix] {
            Col::Account => h_flex()
                .gap_2()
                .items_center()
                .min_w_0()
                .child(
                    div()
                        .size_2()
                        .flex_shrink_0()
                        .rounded_full()
                        .bg(severity_dot_color(severity, cx)),
                )
                .child(div().truncate().child(row.display_name()))
                .children(severity_tag(severity).map(|tag| div().flex_shrink_0().child(tag)))
                .into_any_element(),
            Col::Limit => div()
                .text_color(muted)
                .truncate()
                .child(primary.map(limit_label).unwrap_or_default())
                .into_any_element(),
            Col::Used => div()
                .w_full()
                .text_right()
                .child(primary.map(Metric::used_display).unwrap_or_default())
                .into_any_element(),
            Col::Percent => div()
                .w_full()
                .text_right()
                .child(
                    primary
                        .and_then(Metric::used_fraction)
                        .map(|used| format!("{:.0}%", used * 100.0))
                        .unwrap_or_else(|| "—".into()),
                )
                .into_any_element(),
            Col::Progress => {
                let value = primary
                    .and_then(Metric::used_fraction)
                    .unwrap_or_else(|| assessment.pressure());
                h_flex()
                    .size_full()
                    .items_center()
                    .child(
                        Progress::new(ElementId::Name(format!("progress-{}", row.id().as_str()).into()))
                            .small()
                            .value(value as f32 * 100.0)
                            .color(severity_color(severity, cx))
                            .accessibility_label(format!("{} used", row.display_name())),
                    )
                    .into_any_element()
            }
            Col::Resets => div()
                .text_color(muted)
                .child(
                    primary
                        .and_then(Metric::resets_at)
                        .map(|at| format::reset_label(at, self.now, &Local))
                        .unwrap_or_else(|| "—".into()),
                )
                .into_any_element(),
            Col::Trend => self.trend_cell(row, cx),
        }
    }
}

impl AccountTable {
    /// The 14-day sparkline with the change over it. Hover (or a screen reader) gives the full compact summary:
    /// latest value, change, range and sample window. Hidden accounts say so instead.
    fn trend_cell(&self, row: &AccountSnapshot, cx: &Context<TableState<AccountTable>>) -> gpui_kit::AnyElement {
        let id = row.id().as_str();
        let muted = cx.theme().muted_foreground;
        if !PrefsHub::shows(cx, id) {
            return h_flex()
                .gap_1p5()
                .items_center()
                .text_sm()
                .text_color(muted)
                .child(Icon::new(IconName::EyeOff).small())
                .child("Hidden")
                .into_any_element();
        }
        let Some((summary, kind)) = self.compact.get(id) else {
            return sparkline(row, cx);
        };
        let text: SharedString = format!("Last 14 days: {}", describe(summary, *kind)).into();
        h_flex()
            .id(ElementId::Name(format!("trend-cell-{id}").into()))
            .w_full()
            .gap_2()
            .items_center()
            .role(Role::Group)
            .test_support()
            .aria_label(format!("{} trend. {text}", row.display_name()))
            .tooltip({
                let text = text.clone();
                move |window, cx| Tooltip::new(text.clone()).build(window, cx)
            })
            .child(div().flex_1().min_w_0().child(sparkline(row, cx)))
            .child(
                div()
                    .flex_shrink_0()
                    .text_xs()
                    .text_color(muted)
                    .child(kind.change(summary.change)),
            )
            .into_any_element()
    }
}

/// What the primary metric limits: "5-hour window", "1,500 requests", "$ credits".
fn limit_label(metric: &Metric) -> String {
    match metric {
        Metric::Window { label, .. } => label.clone(),
        Metric::Quota { limit, label, .. } => {
            // The unit is the label's last word: "Premium requests" -> requests, "Chat messages" -> messages.
            let unit = label.rsplit(' ').next().unwrap_or("requests").to_lowercase();
            format!("{} {unit}", codexbar_core::group_thousands(*limit))
        }
        Metric::Balance { label, .. } => format!("$ {}", label.to_lowercase()),
    }
}

/// A 14-day pressure trend. A sparkline shows shape, not level (the progress column shows level),
/// so each row fits its own range.
fn sparkline(row: &AccountSnapshot, cx: &Context<TableState<AccountTable>>) -> gpui_kit::AnyElement {
    if row.trend().len() < 2 {
        // Real providers have no history until #85 stores it.
        return div()
            .text_color(cx.theme().muted_foreground)
            .child("—")
            .into_any_element();
    }
    let color = cx.theme().chart_1;
    let points: Vec<(SharedString, f64)> = row
        .trend()
        .iter()
        .enumerate()
        .map(|(ix, value)| (SharedString::from(ix.to_string()), *value))
        .collect();
    let (low, high) = row.trend().iter().fold((f64::MAX, f64::MIN), |(low, high), value| {
        (low.min(*value), high.max(*value))
    });
    let pad = ((high - low) * 0.15).max(0.01);
    div()
        .w_full()
        .h(rems(1.625))
        .child(
            AreaChart::new(points)
                .id(ElementId::Name(format!("trend-{}", row.id().as_str()).into()))
                .x(|(label, _)| label.clone())
                .y(|(_, value)| *value)
                .y_domain((low - pad).max(0.0), high + pad)
                .linear()
                .stroke(color)
                .fill(linear_gradient(
                    0.,
                    linear_color_stop(color.opacity(0.35), 1.),
                    linear_color_stop(color.opacity(0.), 0.),
                ))
                .x_axis(false)
                .grid(false)
                .y_axis(false)
                .interactive(false)
                .appear(false),
        )
        .into_any_element()
}
