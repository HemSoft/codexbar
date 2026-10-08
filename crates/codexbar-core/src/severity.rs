use std::cmp::Ordering;

use chrono::{DateTime, Duration, Utc};

use crate::metric::Metric;

/// How urgently a limit needs the user's attention. Ordered from calm to urgent.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum Severity {
    #[default]
    Normal,
    Watch,
    AtRisk,
    LimitSoon,
}

impl Severity {
    /// The status label shown beside the account. Normal accounts carry no label.
    pub fn label(self) -> Option<&'static str> {
        match self {
            Self::Normal => None,
            Self::Watch => Some("Watch"),
            Self::AtRisk => Some("At risk"),
            Self::LimitSoon => Some("Limit soon"),
        }
    }

    pub fn needs_attention(self) -> bool {
        self >= Self::AtRisk
    }
}

/// A severity plus the pressure used to order accounts that share it.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Assessment {
    severity: Severity,
    pressure: f64,
}

impl Assessment {
    pub fn severity(self) -> Severity {
        self.severity
    }

    /// 0..=1, higher is closer to blocking.
    pub fn pressure(self) -> f64 {
        self.pressure
    }
}

impl Eq for Assessment {}

impl PartialOrd for Assessment {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Assessment {
    fn cmp(&self, other: &Self) -> Ordering {
        self.severity
            .cmp(&other.severity)
            .then_with(|| self.pressure.total_cmp(&other.pressure))
    }
}

/// A limit hit within this horizon before its reset is "Limit soon" rather than "At risk".
const LIMIT_SOON_HORIZON_HOURS: i64 = 3;
/// Usage at or above this fraction is worth watching even when the pace is safe.
const WATCH_FRACTION: f64 = 0.8;
/// Usage at or above this fraction is "Limit soon" whatever the pace.
const LIMIT_SOON_FRACTION: f64 = 0.95;
/// Credit balances with fewer days left than these are "At risk" or "Watch".
const BALANCE_AT_RISK_DAYS: f64 = 2.0;
const BALANCE_WATCH_DAYS: f64 = 5.0;

/// The severity from observed usage alone, without the pace projection (#88): what `assess` would say if usage
/// stopped now. When `assess` is higher, the projection raised it. Balances are judged by days of credit either way.
pub fn observed_severity(metric: &Metric) -> Severity {
    if metric.days_of_credit().is_some() {
        return assess(metric, DateTime::<Utc>::MIN_UTC).severity();
    }
    match metric.used_fraction() {
        Some(used) if used >= LIMIT_SOON_FRACTION => Severity::LimitSoon,
        Some(used) if used >= WATCH_FRACTION => Severity::Watch,
        _ => Severity::Normal,
    }
}

/// Assesses one metric at `now`.
pub fn assess(metric: &Metric, now: DateTime<Utc>) -> Assessment {
    if let Some(days) = metric.days_of_credit() {
        let severity = if days < BALANCE_AT_RISK_DAYS {
            Severity::AtRisk
        } else if days < BALANCE_WATCH_DAYS {
            Severity::Watch
        } else {
            Severity::Normal
        };
        // Map the balance's watch boundary onto the window's, so 5 days of credit ranks like 80% used.
        let pressure = (WATCH_FRACTION * BALANCE_WATCH_DAYS / days.max(f64::EPSILON)).clamp(0.0, 1.0);
        return Assessment { severity, pressure };
    }

    let Some(used) = metric.used_fraction() else {
        return Assessment::default();
    };

    let exhausts = metric.exhausts_before_reset(now);
    let soon = metric
        .time_to_limit()
        .is_some_and(|to_limit| to_limit <= Duration::hours(LIMIT_SOON_HORIZON_HOURS));

    let severity = if used >= LIMIT_SOON_FRACTION || (exhausts && soon) {
        Severity::LimitSoon
    } else if exhausts {
        Severity::AtRisk
    } else if used >= WATCH_FRACTION {
        Severity::Watch
    } else {
        Severity::Normal
    };
    Assessment {
        severity,
        pressure: used,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::metric::{Money, Pace};

    fn now() -> DateTime<Utc> {
        "2026-10-07T02:00:00Z".parse().unwrap()
    }

    fn window(used: f64, resets_in: Duration, pace: Option<Pace>) -> Metric {
        Metric::Window {
            label: "w".into(),
            used,
            resets_at: now() + resets_in,
            pace,
        }
    }

    #[test]
    fn assess_exhausting_within_horizon_returns_limit_soon() {
        let metric = window(0.91, Duration::minutes(38), Some(Pace::per_hour(0.216)));
        assert_eq!(assess(&metric, now()).severity(), Severity::LimitSoon);
    }

    #[test]
    fn assess_exhausting_days_before_reset_returns_at_risk() {
        let metric = Metric::Quota {
            label: "Premium requests".into(),
            used: 1284,
            limit: 1500,
            resets_at: now() + Duration::days(25),
            pace: Some(Pace::per_day(0.0064)),
        };
        assert_eq!(assess(&metric, now()).severity(), Severity::AtRisk);
    }

    #[test]
    fn assess_high_usage_safe_pace_returns_watch() {
        let metric = window(0.82, Duration::days(2), Some(Pace::per_day(0.05)));
        assert_eq!(assess(&metric, now()).severity(), Severity::Watch);
    }

    #[test]
    fn assess_nearly_full_without_pace_returns_limit_soon() {
        assert_eq!(
            assess(&window(0.96, Duration::days(3), None), now()).severity(),
            Severity::LimitSoon
        );
    }

    #[test]
    fn projected_exhaustion_raises_severity_without_changing_usage() {
        // The same 50% used: safe without a pace, at risk when the pace runs out days before the reset.
        let steady = window(0.5, Duration::days(5), None);
        let fast = window(0.5, Duration::days(5), Some(Pace::per_day(0.2)));
        assert_eq!(assess(&steady, now()).severity(), Severity::Normal);
        assert_eq!(assess(&fast, now()).severity(), Severity::AtRisk);
        assert_eq!(
            steady.used_fraction(),
            fast.used_fraction(),
            "observed usage is unchanged"
        );
    }

    #[test]
    fn observed_severity_ignores_the_projection() {
        let fast = window(0.5, Duration::days(5), Some(Pace::per_day(0.2)));
        assert_eq!(assess(&fast, now()).severity(), Severity::AtRisk);
        assert_eq!(observed_severity(&fast), Severity::Normal);
        let full = window(0.96, Duration::days(5), None);
        assert_eq!(observed_severity(&full), Severity::LimitSoon);
        assert_eq!(
            observed_severity(&window(0.85, Duration::days(5), None)),
            Severity::Watch
        );
    }

    #[test]
    fn spend_against_a_limit_is_assessed_like_usage() {
        let spend = Metric::Spend {
            label: "Budget".into(),
            spent: Money::from_cents(4800),
            limit: Some(Money::from_cents(5000)),
            resets_at: None,
            pace: None,
        };
        assert_eq!(
            assess(&spend, now()).severity(),
            Severity::LimitSoon,
            "96% of the budget"
        );
    }

    #[test]
    fn assess_moderate_usage_returns_normal() {
        assert_eq!(
            assess(&window(0.41, Duration::days(3), None), now()).severity(),
            Severity::Normal
        );
    }

    #[test]
    fn assess_balance_few_days_left_escalates() {
        let balance = |cents, burn| Metric::Balance {
            label: "Credits".into(),
            remaining: Money::from_cents(cents),
            burn_per_day: Some(Money::from_cents(burn)),
        };
        assert_eq!(assess(&balance(1842, 310), now()).severity(), Severity::Normal);
        assert_eq!(assess(&balance(1000, 310), now()).severity(), Severity::Watch);
        assert_eq!(assess(&balance(500, 310), now()).severity(), Severity::AtRisk);
    }

    #[test]
    fn assessment_ordering_severity_outranks_pressure() {
        let watch = Assessment {
            severity: Severity::Watch,
            pressure: 0.99,
        };
        let at_risk = Assessment {
            severity: Severity::AtRisk,
            pressure: 0.5,
        };
        assert!(at_risk > watch);
    }

    #[test]
    fn severity_label_normal_has_none() {
        assert_eq!(Severity::Normal.label(), None);
        assert_eq!(Severity::LimitSoon.label(), Some("Limit soon"));
        assert!(Severity::AtRisk.needs_attention());
        assert!(!Severity::Watch.needs_attention());
    }
}
