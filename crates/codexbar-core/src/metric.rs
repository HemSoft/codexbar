use chrono::{DateTime, Duration, Utc};

/// An amount of money in minor units (cents) so totals never pick up floating-point drift.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Money {
    cents: i64,
}

impl Money {
    pub const fn from_cents(cents: i64) -> Self {
        Self { cents }
    }

    pub const fn cents(self) -> i64 {
        self.cents
    }

    /// US-dollar display ("$18.42"). Locale-aware currency formatting arrives with issue #84.
    pub fn display(self) -> String {
        let sign = if self.cents < 0 { "-" } else { "" };
        let abs = self.cents.unsigned_abs();
        format!("{sign}${}.{:02}", abs / 100, abs % 100)
    }
}

/// How fast a limit is being consumed, as a fraction of the whole limit per hour.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Pace {
    fraction_per_hour: f64,
}

impl Pace {
    pub fn per_hour(fraction_per_hour: f64) -> Self {
        Self {
            fraction_per_hour: fraction_per_hour.max(0.0),
        }
    }

    pub fn per_day(fraction_per_day: f64) -> Self {
        Self::per_hour(fraction_per_day / 24.0)
    }

    pub fn fraction_per_hour(self) -> f64 {
        self.fraction_per_hour
    }
}

/// One limit an account reports.
#[derive(Clone, Debug, PartialEq)]
pub enum Metric {
    /// A rolling or calendar window measured as a fraction used, such as a 5-hour or weekly window.
    Window {
        label: String,
        used: f64,
        resets_at: DateTime<Utc>,
        pace: Option<Pace>,
    },
    /// A counted allowance, such as premium requests per month.
    Quota {
        label: String,
        used: u64,
        limit: u64,
        resets_at: DateTime<Utc>,
        pace: Option<Pace>,
    },
    /// Prepaid credit that runs down with spend.
    Balance {
        label: String,
        remaining: Money,
        burn_per_day: Option<Money>,
    },
}

impl Metric {
    pub fn label(&self) -> &str {
        match self {
            Self::Window { label, .. } | Self::Quota { label, .. } | Self::Balance { label, .. } => label,
        }
    }

    /// Fraction of the limit used, clamped to 0..=1. Balances have no fixed limit and return `None`.
    pub fn used_fraction(&self) -> Option<f64> {
        match self {
            Self::Window { used, .. } => Some(used.clamp(0.0, 1.0)),
            Self::Quota { used, limit, .. } if *limit > 0 => Some((*used as f64 / *limit as f64).clamp(0.0, 1.0)),
            Self::Quota { .. } | Self::Balance { .. } => None,
        }
    }

    pub fn resets_at(&self) -> Option<DateTime<Utc>> {
        match self {
            Self::Window { resets_at, .. } | Self::Quota { resets_at, .. } => Some(*resets_at),
            Self::Balance { .. } => None,
        }
    }

    /// Time until the limit is exhausted at the current pace, when the pace is known.
    pub fn time_to_limit(&self) -> Option<Duration> {
        let (used, pace) = match self {
            Self::Window { pace: Some(pace), .. } | Self::Quota { pace: Some(pace), .. } => {
                (self.used_fraction()?, *pace)
            }
            _ => return None,
        };
        if pace.fraction_per_hour() <= 0.0 {
            return None;
        }
        let hours = (1.0 - used) / pace.fraction_per_hour();
        Some(Duration::seconds((hours * 3600.0).round() as i64))
    }

    /// True when the current pace exhausts the limit before it resets.
    pub fn exhausts_before_reset(&self, now: DateTime<Utc>) -> bool {
        match (self.time_to_limit(), self.resets_at()) {
            (Some(to_limit), Some(resets_at)) => now + to_limit < resets_at,
            _ => false,
        }
    }

    /// Days of credit left at the current burn rate.
    pub fn days_of_credit(&self) -> Option<f64> {
        match self {
            Self::Balance {
                remaining,
                burn_per_day: Some(burn),
                ..
            } if burn.cents() > 0 => Some(remaining.cents() as f64 / burn.cents() as f64),
            _ => None,
        }
    }

    /// The value column ("91%", "1,284", "$18.42 left").
    pub fn used_display(&self) -> String {
        match self {
            Self::Window { .. } => format!("{:.0}%", self.used_fraction().unwrap_or_default() * 100.0),
            Self::Quota { used, .. } => group_thousands(*used),
            Self::Balance { remaining, .. } => format!("{} left", remaining.display()),
        }
    }
}

/// "1284" -> "1,284".
pub fn group_thousands(value: u64) -> String {
    let digits = value.to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3);
    for (i, ch) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(ch);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn now() -> DateTime<Utc> {
        "2026-10-07T02:00:00Z".parse().unwrap()
    }

    #[test]
    fn money_display_negative_and_cents_formats_with_sign() {
        assert_eq!(Money::from_cents(1842).display(), "$18.42");
        assert_eq!(Money::from_cents(-905).display(), "-$9.05");
        assert_eq!(Money::from_cents(7).display(), "$0.07");
    }

    #[test]
    fn group_thousands_large_values_inserts_commas() {
        assert_eq!(group_thousands(0), "0");
        assert_eq!(group_thousands(612), "612");
        assert_eq!(group_thousands(1284), "1,284");
        assert_eq!(group_thousands(1_234_567), "1,234,567");
    }

    #[test]
    fn used_fraction_quota_without_limit_returns_none() {
        let metric = Metric::Quota {
            label: "x".into(),
            used: 5,
            limit: 0,
            resets_at: now(),
            pace: None,
        };
        assert_eq!(metric.used_fraction(), None);
    }

    #[test]
    fn exhausts_before_reset_fast_pace_returns_true() {
        let metric = Metric::Window {
            label: "5-hour window".into(),
            used: 0.91,
            resets_at: now() + Duration::minutes(38),
            pace: Some(Pace::per_hour(0.216)),
        };
        let to_limit = metric.time_to_limit().unwrap();
        assert_eq!(to_limit.num_minutes(), 25);
        assert!(metric.exhausts_before_reset(now()));
    }

    #[test]
    fn exhausts_before_reset_without_pace_returns_false() {
        let metric = Metric::Window {
            label: "Weekly".into(),
            used: 0.99,
            resets_at: now() + Duration::days(1),
            pace: None,
        };
        assert!(!metric.exhausts_before_reset(now()));
    }

    #[test]
    fn days_of_credit_with_burn_divides_remaining() {
        let metric = Metric::Balance {
            label: "Credits".into(),
            remaining: Money::from_cents(1842),
            burn_per_day: Some(Money::from_cents(310)),
        };
        assert!((metric.days_of_credit().unwrap() - 5.94).abs() < 0.01);
        assert_eq!(metric.used_display(), "$18.42 left");
    }
}
