//! Usage, balance and severity alerts (#87): which conditions hold after a refresh, which are new and should notify,
//! and which have recovered. Pure, so deduplication, recovery and failure handling are tested without Windows.
//!
//! The caller keeps the set of active alert keys (persisted, so a restart doesn't notify again), passes only the
//! accounts that refreshed successfully (so a failed provider neither clears nor repeats its alerts), marks a key
//! active once its notification is delivered, and drops recovered keys.

use std::collections::BTreeSet;

use chrono::{DateTime, Utc};

use crate::{AccountSnapshot, Metric, Severity, assess};

/// A usage alert recovers once usage falls this far below the threshold, so a value hovering at the line doesn't
/// notify on every refresh.
pub const USAGE_RECOVERY_MARGIN: f64 = 0.05;
/// A balance alert recovers once the balance is this far above the threshold (a top-up), in dollars or 10%.
pub const BALANCE_RECOVERY_DOLLARS: f64 = 0.5;

#[derive(Clone, Debug, PartialEq)]
pub struct AlertSettings {
    pub enabled: bool,
    /// Fraction used at which a limit alerts, such as 0.8.
    pub usage_threshold: f64,
    /// Dollars left below which a balance alerts.
    pub balance_threshold: f64,
    /// Notify when a limit is projected to run out before it resets ("At risk").
    pub warning: bool,
    /// Notify when a limit is about to run out ("Limit soon").
    pub critical: bool,
}

impl Default for AlertSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            usage_threshold: 0.8,
            balance_threshold: 5.0,
            warning: true,
            critical: true,
        }
    }
}

/// What kind of condition an alert reports.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AlertKind {
    Usage,
    Balance,
    Warning,
    Critical,
}

impl AlertKind {
    fn slug(self) -> &'static str {
        match self {
            Self::Usage => "usage",
            Self::Balance => "balance",
            Self::Warning => "warning",
            Self::Critical => "critical",
        }
    }
}

/// One notification to send.
#[derive(Clone, Debug, PartialEq)]
pub struct Alert {
    /// Stable identity of the condition: account, metric and kind. Active while the condition holds.
    pub key: String,
    pub kind: AlertKind,
    /// Account and provider, as the dashboard names them ("Claude · personal").
    pub account: String,
    /// The affected metric ("Weekly").
    pub metric: String,
    /// The current value ("82%", "$3.10 left").
    pub value: String,
    pub title: String,
    pub body: String,
    /// Other conditions this notification also stands for: a "Limit soon" alert covers "At risk" on the same metric,
    /// so one metric never sends both at once. Mark these active together with `key`.
    pub covers: Vec<String>,
}

impl Alert {
    /// Every key to mark active once this alert is shown.
    pub fn keys(&self) -> impl Iterator<Item = &String> {
        std::iter::once(&self.key).chain(&self.covers)
    }
}

/// The outcome of one refresh.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Evaluation {
    /// Conditions that hold and aren't active yet: notify, then mark active.
    pub notify: Vec<Alert>,
    /// Active keys whose condition cleared on a refreshed account: drop them so they can alert again. A metric
    /// missing from a result keeps its keys, since providers can return partial results (one OpenCode half failed).
    pub recovered: Vec<String>,
    /// Conditions that hold but are covered by an active stronger alert (At risk while Limit soon is active): mark
    /// them active without notifying.
    pub covered: Vec<String>,
}

pub fn alert_key(account: &str, metric: &str, kind: AlertKind) -> String {
    format!("{account}|{metric}|{}", kind.slug())
}

/// Splits `account|slot|kind` from the right, so account ids that contain `|` stay whole.
pub fn split_key(key: &str) -> Option<(&str, &str, &str)> {
    let mut parts = key.rsplitn(3, '|');
    let (kind, slot, account) = (parts.next()?, parts.next()?, parts.next()?);
    Some((account, slot, kind))
}

/// The metric part of an alert key. Limits that reset carry their window (the reset hour), so a new window can alert
/// again even when CodexBar never saw usage fall in between; balances don't reset and use the bare metric key.
pub fn metric_slot(metric: &Metric) -> String {
    match metric.resets_at() {
        Some(resets_at) => format!("{}@{}", metric.key(), resets_at.timestamp().div_euclid(3600)),
        None => metric.key(),
    }
}

/// True when two slots are the same window of the same metric. Reset times derived from a countdown move with request
/// latency, so reset hours within an hour of each other are one window; windows last at least five hours.
fn same_window(a: &str, b: &str) -> bool {
    if a == b {
        return true;
    }
    match (a.split_once('@'), b.split_once('@')) {
        (Some((metric_a, hour_a)), Some((metric_b, hour_b))) if metric_a == metric_b => {
            match (hour_a.parse::<i64>(), hour_b.parse::<i64>()) {
                (Ok(hour_a), Ok(hour_b)) => (hour_a - hour_b).abs() <= 1,
                _ => false,
            }
        }
        _ => false,
    }
}

/// Evaluates the accounts that refreshed successfully against the settings and the active keys.
pub fn evaluate(
    settings: &AlertSettings,
    active: &BTreeSet<String>,
    refreshed: &[AccountSnapshot],
    now: DateTime<Utc>,
) -> Evaluation {
    let mut out = Evaluation::default();
    for account in refreshed {
        let id = account.id().as_str();
        for metric in account.metrics() {
            let first_new = out.notify.len();
            let current = metric_slot(metric);
            // An alert already held for this window keeps its slot, so a reset estimate that drifts by a few seconds
            // across an hour boundary doesn't look like a new window.
            let slot = active
                .iter()
                .filter_map(|key| split_key(key))
                .find(|(account, key_slot, _)| *account == id && same_window(key_slot, &current))
                .map_or(current, |(_, key_slot, _)| key_slot.to_owned());
            // Keys from an earlier window of this metric (or from before windows were part of the key) recover: that
            // window is over.
            let base = metric.key();
            out.recovered.extend(
                active
                    .iter()
                    .filter(|key| {
                        // Compared by parts, not prefixes: account ids may contain `|` or `@`.
                        let Some((account, key_slot, _)) = split_key(key) else {
                            return false;
                        };
                        let key_metric = key_slot.split_once('@').map_or(key_slot, |(metric, _)| metric);
                        account == id && key_metric == base && !same_window(key_slot, &slot)
                    })
                    .cloned(),
            );
            for kind in [
                AlertKind::Usage,
                AlertKind::Balance,
                AlertKind::Warning,
                AlertKind::Critical,
            ] {
                let key = alert_key(id, &slot, kind);
                let is_active = active.contains(&key);
                match condition(settings, metric, kind, now) {
                    Condition::Triggered if settings.enabled && !is_active => {
                        out.notify.push(alert(account, metric, kind, key, settings));
                    }
                    Condition::Clear if is_active => out.recovered.push(key),
                    // Inside the recovery margin, triggered while already active, or not applicable: no change.
                    _ => {}
                }
            }
            // "Limit soon" supersedes "At risk" on the same metric: one notification, both conditions handled. That
            // holds whether Limit soon is new now or already active (say At risk was just switched on).
            let new = &mut out.notify[first_new..];
            if let Some(warning) = new.iter().position(|alert| alert.kind == AlertKind::Warning) {
                let critical_key = alert_key(id, &slot, AlertKind::Critical);
                let critical_held = matches!(
                    condition(settings, metric, AlertKind::Critical, now),
                    Condition::Triggered | Condition::Holding
                );
                if let Some(critical) = new.iter().position(|alert| alert.kind == AlertKind::Critical) {
                    let warning_key = new[warning].key.clone();
                    new[critical].covers.push(warning_key);
                    out.notify.remove(first_new + warning);
                } else if active.contains(&critical_key) && critical_held {
                    out.covered.push(new[warning].key.clone());
                    out.notify.remove(first_new + warning);
                }
            }
        }
    }
    out
}

enum Condition {
    Triggered,
    /// Inside the margin between triggering and recovery.
    Holding,
    Clear,
    /// The kind doesn't apply to this metric or is switched off.
    NotApplicable,
}

fn condition(settings: &AlertSettings, metric: &Metric, kind: AlertKind, now: DateTime<Utc>) -> Condition {
    match kind {
        AlertKind::Usage => match metric.used_fraction() {
            Some(_) if matches!(metric, Metric::Balance { .. }) => Condition::NotApplicable,
            Some(used) if used >= settings.usage_threshold => Condition::Triggered,
            // The recovery line stays above zero for low thresholds, so an alert can always recover.
            Some(used)
                if used > (settings.usage_threshold - USAGE_RECOVERY_MARGIN).max(settings.usage_threshold / 2.0) =>
            {
                Condition::Holding
            }
            Some(_) => Condition::Clear,
            None => Condition::NotApplicable,
        },
        AlertKind::Balance => match metric {
            Metric::Balance { remaining, .. } => {
                let dollars = remaining.cents() as f64 / 100.0;
                let recovery =
                    (settings.balance_threshold * 1.1).max(settings.balance_threshold + BALANCE_RECOVERY_DOLLARS);
                if dollars < settings.balance_threshold {
                    Condition::Triggered
                } else if dollars < recovery {
                    Condition::Holding
                } else {
                    Condition::Clear
                }
            }
            _ => Condition::NotApplicable,
        },
        AlertKind::Warning | AlertKind::Critical => {
            let level = if kind == AlertKind::Warning {
                Severity::AtRisk
            } else {
                Severity::LimitSoon
            };
            let included = if kind == AlertKind::Warning {
                settings.warning
            } else {
                settings.critical
            };
            if !included {
                // Switched off: an alert raised before stays until it clears, so turning the option back on
                // doesn't repeat it.
                return if assess(metric, now).severity() >= level {
                    Condition::Holding
                } else {
                    Condition::Clear
                };
            }
            if assess(metric, now).severity() >= level {
                Condition::Triggered
            } else {
                Condition::Clear
            }
        }
    }
}

fn alert(account: &AccountSnapshot, metric: &Metric, kind: AlertKind, key: String, settings: &AlertSettings) -> Alert {
    let name = account.display_name();
    let label = metric.label().to_owned();
    let value = metric.used_display();
    let (title, reason) = match kind {
        AlertKind::Usage => (
            format!("{name}: {label} at {value}"),
            format!("Above your {:.0}% alert.", settings.usage_threshold * 100.0),
        ),
        AlertKind::Balance => (
            format!("{name}: {value}"),
            format!("{label} is below your ${:.2} alert.", settings.balance_threshold),
        ),
        // Balances have no reset; their severity comes from how long the credit lasts at the current spend.
        AlertKind::Warning | AlertKind::Critical if matches!(metric, Metric::Balance { .. }) => {
            let lasts = metric
                .days_of_credit()
                .map(|days| match days {
                    d if d < 1.0 => "less than a day".to_owned(),
                    d if d < 1.5 => "about a day".to_owned(),
                    d => format!("about {d:.0} days"),
                })
                .unwrap_or_else(|| "a short while".to_owned());
            let state = if kind == AlertKind::Critical {
                "running out"
            } else {
                "running low"
            };
            (
                format!("{name}: {label} {state}"),
                format!("{value} lasts {lasts} at the current spend."),
            )
        }
        AlertKind::Warning => (
            format!("{name}: {label} at risk"),
            format!("At {value}, it is on pace to run out before it resets."),
        ),
        AlertKind::Critical => (
            format!("{name}: {label} limit soon"),
            format!("At {value}, it is about to run out."),
        ),
    };
    Alert {
        key,
        kind,
        body: format!("{} · {label}: {value}. {reason}", account.provider().display_name()),
        account: name,
        metric: label,
        value,
        title,
        covers: Vec::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AccountId, Money, Pace, Provider};
    use chrono::{Duration, TimeZone};

    fn now() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 10, 7, 12, 0, 0).unwrap()
    }

    fn window(id: &str, used: f64) -> AccountSnapshot {
        AccountSnapshot::new(
            AccountId::new(id),
            Provider::Claude,
            vec![Metric::Window {
                label: "Weekly".into(),
                used,
                resets_at: now() + Duration::days(3),
                pace: None,
            }],
            now(),
        )
        .with_label("personal")
    }

    fn balance(id: &str, cents: i64) -> AccountSnapshot {
        AccountSnapshot::new(
            AccountId::new(id),
            Provider::OpenRouter,
            vec![Metric::Balance {
                label: "Credits".into(),
                remaining: Money::from_cents(cents),
                burn_per_day: None,
            }],
            now(),
        )
    }

    /// The slot of the `window()` helper's Weekly metric.
    fn weekly_slot() -> String {
        metric_slot(&window("c", 0.5).metrics()[0])
    }

    fn on() -> AlertSettings {
        AlertSettings {
            enabled: true,
            warning: false,
            critical: false,
            ..AlertSettings::default()
        }
    }

    /// Runs refreshes in order, delivering every notification, and returns the titles sent per refresh.
    fn run(
        settings: &AlertSettings,
        active: &mut BTreeSet<String>,
        refreshes: &[Vec<AccountSnapshot>],
    ) -> Vec<Vec<String>> {
        refreshes
            .iter()
            .map(|accounts| {
                let evaluation = evaluate(settings, active, accounts, now());
                for key in &evaluation.recovered {
                    active.remove(key);
                }
                active.extend(evaluation.covered.iter().cloned());
                for alert in &evaluation.notify {
                    active.extend(alert.keys().cloned());
                }
                evaluation.notify.into_iter().map(|alert| alert.title).collect()
            })
            .collect()
    }

    #[test]
    fn usage_over_threshold_notifies_once_until_it_recovers() {
        let mut active = BTreeSet::new();
        let sent = run(
            &on(),
            &mut active,
            &[
                vec![window("c", 0.7)],
                vec![window("c", 0.82)],
                vec![window("c", 0.9)],
                // Inside the recovery margin: still active, no repeat.
                vec![window("c", 0.77)],
                vec![window("c", 0.85)],
                // Recovered (below 75%), then over again: a new alert.
                vec![window("c", 0.6)],
                vec![window("c", 0.81)],
            ],
        );
        let counts: Vec<usize> = sent.iter().map(Vec::len).collect();
        assert_eq!(counts, vec![0, 1, 0, 0, 0, 0, 1]);
        assert_eq!(sent[1][0], "Claude · personal: Weekly at 82%");
    }

    #[test]
    fn balance_below_threshold_notifies_and_recovers_after_a_top_up() {
        let mut active = BTreeSet::new();
        let sent = run(
            &on(),
            &mut active,
            &[
                vec![balance("o", 900)],
                vec![balance("o", 420)],
                vec![balance("o", 380)],
                // $5.20 is above the line but inside the recovery margin.
                vec![balance("o", 520)],
                vec![balance("o", 450)],
                vec![balance("o", 2000)],
                vec![balance("o", 300)],
            ],
        );
        let counts: Vec<usize> = sent.iter().map(Vec::len).collect();
        assert_eq!(counts, vec![0, 1, 0, 0, 0, 0, 1]);
    }

    #[test]
    fn alert_names_account_provider_metric_and_value() {
        let evaluation = evaluate(&on(), &BTreeSet::new(), &[balance("o", 310)], now());
        let alert = &evaluation.notify[0];
        assert_eq!(alert.account, "OpenRouter");
        assert_eq!(alert.metric, "Credits");
        assert_eq!(alert.value, "$3.10 left");
        assert_eq!(alert.title, "OpenRouter: $3.10 left");
        assert_eq!(
            alert.body,
            "OpenRouter · Credits: $3.10 left. Credits is below your $5.00 alert."
        );
        assert_eq!(alert.key, "o|credits|balance", "balances have no window");
    }

    #[test]
    fn disabled_alerts_send_nothing_but_still_recover() {
        let settings = AlertSettings::default();
        let active: BTreeSet<String> = [alert_key("c", &weekly_slot(), AlertKind::Usage)].into();
        let evaluation = evaluate(&settings, &active, &[window("c", 0.95)], now());
        assert!(evaluation.notify.is_empty());
        let evaluation = evaluate(&settings, &active, &[window("c", 0.1)], now());
        assert_eq!(
            evaluation.recovered,
            vec![alert_key("c", &weekly_slot(), AlertKind::Usage)]
        );
    }

    #[test]
    fn accounts_missing_from_a_refresh_keep_their_alerts() {
        // A failed provider's accounts aren't passed in: their active keys are neither recovered nor repeated.
        let active: BTreeSet<String> = [alert_key("c", &weekly_slot(), AlertKind::Usage)].into();
        let evaluation = evaluate(&on(), &active, &[balance("o", 900)], now());
        assert_eq!(evaluation, Evaluation::default());
    }

    #[test]
    fn active_keys_survive_a_restart() {
        // The active set is what persists; reloaded, the same condition doesn't notify again.
        let mut active = BTreeSet::new();
        run(&on(), &mut active, &[vec![window("c", 0.9)]]);
        let reloaded: BTreeSet<String> = active.iter().cloned().collect();
        assert!(evaluate(&on(), &reloaded, &[window("c", 0.9)], now()).notify.is_empty());
    }

    #[test]
    fn limit_soon_covers_at_risk_on_the_same_metric() {
        let metric = Metric::Window {
            label: "5-hour window".into(),
            used: 0.97,
            resets_at: now() + Duration::hours(2),
            pace: Some(Pace::per_hour(0.1)),
        };
        assert_eq!(assess(&metric, now()).severity(), Severity::LimitSoon);
        let account = AccountSnapshot::new(AccountId::new("x"), Provider::Codex, vec![metric], now());
        let settings = AlertSettings {
            usage_threshold: 1.0,
            ..AlertSettings {
                enabled: true,
                ..AlertSettings::default()
            }
        };
        let mut active = BTreeSet::new();
        let sent = run(&settings, &mut active, &[vec![account.clone()], vec![account]]);
        assert_eq!(
            sent[0],
            vec!["ChatGPT · Codex: 5-hour window limit soon".to_owned()],
            "one notification, not two"
        );
        assert!(sent[1].is_empty(), "At risk was marked handled too");
        assert!(
            active
                .iter()
                .any(|key| key.starts_with("x|5-hour-window@") && key.ends_with("|warning"))
        );
    }

    #[test]
    fn at_risk_switched_on_while_limit_soon_is_active_stays_quiet() {
        let metric = Metric::Window {
            label: "5-hour window".into(),
            used: 0.97,
            resets_at: now() + Duration::hours(2),
            pace: Some(Pace::per_hour(0.1)),
        };
        let account = AccountSnapshot::new(AccountId::new("x"), Provider::Codex, vec![metric], now());
        let critical_only = AlertSettings {
            enabled: true,
            usage_threshold: 1.0,
            warning: false,
            ..AlertSettings::default()
        };
        let mut active = BTreeSet::new();
        run(&critical_only, &mut active, &[vec![account.clone()]]);
        let both = AlertSettings {
            warning: true,
            ..critical_only
        };
        let sent = run(&both, &mut active, &[vec![account]]);
        assert!(sent[0].is_empty(), "no downgrade to At risk while Limit soon is active");
        assert!(
            active
                .iter()
                .any(|key| key.starts_with("x|5-hour-window@") && key.ends_with("|warning")),
            "At risk is marked handled"
        );
    }

    #[test]
    fn balance_severity_alerts_describe_spend_not_a_reset() {
        let account = AccountSnapshot::new(
            AccountId::new("o"),
            Provider::OpenRouter,
            vec![Metric::Balance {
                label: "Credits".into(),
                remaining: Money::from_cents(300),
                burn_per_day: Some(Money::from_cents(200)),
            }],
            now(),
        );
        let settings = AlertSettings {
            balance_threshold: 0.0,
            ..AlertSettings {
                enabled: true,
                ..AlertSettings::default()
            }
        };
        let evaluation = evaluate(&settings, &BTreeSet::new(), &[account], now());
        let alert = evaluation
            .notify
            .iter()
            .find(|alert| matches!(alert.kind, AlertKind::Warning | AlertKind::Critical))
            .expect("a day and a half of credit is a severity alert");
        assert!(!alert.body.contains("reset"), "{}", alert.body);
        assert!(
            alert.body.ends_with("lasts about 2 days at the current spend."),
            "{}",
            alert.body
        );
    }

    #[test]
    fn low_usage_thresholds_can_still_recover() {
        let low = AlertSettings {
            usage_threshold: 0.05,
            ..on()
        };
        let mut active = BTreeSet::new();
        let sent = run(
            &low,
            &mut active,
            &[
                vec![window("c", 0.06)],
                vec![window("c", 0.02)],
                vec![window("c", 0.06)],
            ],
        );
        let counts: Vec<usize> = sent.iter().map(Vec::len).collect();
        assert_eq!(counts, vec![1, 0, 1], "2% is below the recovery line for a 5% alert");
    }

    #[test]
    fn warning_and_critical_follow_their_own_switches() {
        let pace = |per_hour| Metric::Window {
            label: "5-hour window".into(),
            used: 0.6,
            resets_at: now() + Duration::hours(4),
            pace: Some(Pace::per_hour(per_hour)),
        };
        let account = |metric| AccountSnapshot::new(AccountId::new("x"), Provider::Codex, vec![metric], now());
        let kinds = |settings: &AlertSettings, per_hour| {
            evaluate(settings, &BTreeSet::new(), &[account(pace(per_hour))], now())
                .notify
                .iter()
                .map(|alert| alert.kind)
                .collect::<Vec<_>>()
        };
        let only_warning = AlertSettings {
            usage_threshold: 1.0,
            warning: true,
            critical: false,
            ..on()
        };
        let only_critical = AlertSettings {
            warning: false,
            critical: true,
            ..only_warning.clone()
        };
        // Running out within the window: at least At risk.
        let fast = 0.5;
        assert!(assess(&pace(fast), now()).severity() >= Severity::AtRisk);
        let warning_kinds = kinds(&only_warning, fast);
        assert!(warning_kinds.contains(&AlertKind::Warning));
        assert!(!warning_kinds.contains(&AlertKind::Critical));
        assert!(!kinds(&only_critical, fast).contains(&AlertKind::Warning));
        // A slow pace triggers neither.
        assert!(kinds(&only_warning, 0.01).is_empty());
    }
}

#[cfg(test)]
mod window_tests {
    use super::*;
    use crate::{AccountId, Provider};
    use chrono::{Duration, TimeZone};

    fn now() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 10, 7, 12, 0, 0).unwrap()
    }

    fn weekly(used: f64, resets_in_days: i64) -> AccountSnapshot {
        AccountSnapshot::new(
            AccountId::new("c"),
            Provider::Claude,
            vec![Metric::Window {
                label: "Weekly".into(),
                used,
                resets_at: now() + Duration::days(resets_in_days),
                pace: None,
            }],
            now(),
        )
    }

    #[test]
    fn a_new_window_alerts_again_without_seeing_usage_fall() {
        let settings = AlertSettings {
            enabled: true,
            warning: false,
            critical: false,
            ..AlertSettings::default()
        };
        let mut active = BTreeSet::new();
        // Over the threshold in this window...
        let first = evaluate(&settings, &active, &[weekly(0.9, 3)], now());
        assert_eq!(first.notify.len(), 1);
        active.extend(first.notify[0].keys().cloned());
        // ...and over again in the next one, with no refresh in between that saw it reset.
        let next = evaluate(&settings, &active, &[weekly(0.85, 10)], now());
        assert_eq!(next.notify.len(), 1, "the new window notifies");
        assert_eq!(
            next.recovered,
            vec![first.notify[0].key.clone()],
            "the old window's key ends"
        );
    }

    #[test]
    fn another_accounts_key_is_never_taken_for_an_old_window() {
        let settings = AlertSettings {
            enabled: true,
            ..AlertSettings::default()
        };
        // `team|weekly@west` is a different account whose key starts like `team`'s weekly window.
        let other: BTreeSet<String> = ["team|weekly@west|weekly|usage".to_owned()].into();
        let account = AccountSnapshot::new(
            AccountId::new("team"),
            Provider::Claude,
            vec![Metric::Window {
                label: "Weekly".into(),
                used: 0.1,
                resets_at: now() + Duration::days(3),
                pace: None,
            }],
            now(),
        );
        assert!(evaluate(&settings, &other, &[account], now()).recovered.is_empty());
    }

    #[test]
    fn a_reset_estimate_drifting_across_an_hour_stays_one_window() {
        let settings = AlertSettings {
            enabled: true,
            warning: false,
            critical: false,
            ..AlertSettings::default()
        };
        let at = |resets_at: DateTime<Utc>| {
            AccountSnapshot::new(
                AccountId::new("c"),
                Provider::OpenCode,
                vec![Metric::Window {
                    label: "Go usage".into(),
                    used: 0.9,
                    resets_at,
                    pace: None,
                }],
                now(),
            )
        };
        // The countdown puts the reset a second before the hour, then (one slow request later) a second after.
        let boundary = Utc.with_ymd_and_hms(2026, 10, 9, 15, 0, 0).unwrap();
        let mut active = BTreeSet::new();
        let first = evaluate(&settings, &active, &[at(boundary - Duration::seconds(1))], now());
        assert_eq!(first.notify.len(), 1);
        active.extend(first.notify[0].keys().cloned());
        let drifted = evaluate(&settings, &active, &[at(boundary + Duration::seconds(1))], now());
        assert!(
            drifted.notify.is_empty() && drifted.recovered.is_empty(),
            "same window, no repeat"
        );
    }

    #[test]
    fn keys_from_before_windows_were_keyed_recover_once() {
        let settings = AlertSettings {
            enabled: true,
            warning: false,
            critical: false,
            ..AlertSettings::default()
        };
        let legacy: BTreeSet<String> = ["c|weekly|usage".to_owned()].into();
        let evaluation = evaluate(&settings, &legacy, &[weekly(0.9, 3)], now());
        assert_eq!(evaluation.recovered, vec!["c|weekly|usage".to_owned()]);
    }
}
