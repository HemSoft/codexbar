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
    /// Active keys whose condition cleared on a refreshed account: drop them so they can alert again.
    pub recovered: Vec<String>,
}

pub fn alert_key(account: &str, metric: &str, kind: AlertKind) -> String {
    format!("{account}|{metric}|{}", kind.slug())
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
            for kind in [
                AlertKind::Usage,
                AlertKind::Balance,
                AlertKind::Warning,
                AlertKind::Critical,
            ] {
                let key = alert_key(id, &metric.key(), kind);
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
            // "Limit soon" supersedes "At risk" on the same metric: one notification, both conditions handled.
            let new = &mut out.notify[first_new..];
            if let Some(critical) = new.iter().position(|alert| alert.kind == AlertKind::Critical)
                && let Some(warning) = new.iter().position(|alert| alert.kind == AlertKind::Warning)
            {
                let warning_key = new[warning].key.clone();
                new[critical].covers.push(warning_key);
                out.notify.remove(first_new + warning);
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
            Some(used) if used > settings.usage_threshold - USAGE_RECOVERY_MARGIN => Condition::Holding,
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
        assert_eq!(alert.key, "o|credits|balance");
    }

    #[test]
    fn disabled_alerts_send_nothing_but_still_recover() {
        let settings = AlertSettings::default();
        let active: BTreeSet<String> = [alert_key("c", "weekly", AlertKind::Usage)].into();
        let evaluation = evaluate(&settings, &active, &[window("c", 0.95)], now());
        assert!(evaluation.notify.is_empty());
        let evaluation = evaluate(&settings, &active, &[window("c", 0.1)], now());
        assert_eq!(evaluation.recovered, vec!["c|weekly|usage".to_owned()]);
    }

    #[test]
    fn accounts_missing_from_a_refresh_keep_their_alerts() {
        // A failed provider's accounts aren't passed in: their active keys are neither recovered nor repeated.
        let active: BTreeSet<String> = [alert_key("c", "weekly", AlertKind::Usage)].into();
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
        assert!(active.contains("x|5-hour-window|warning"));
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
