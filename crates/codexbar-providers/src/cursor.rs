//! Cursor included-usage for the current billing period, read with the sign-in Cursor keeps in
//! `%APPDATA%\Cursor\auth.json`.

use std::path::{Path, PathBuf};

use chrono::{DateTime, Months, Utc};
use codexbar_core::{AccountId, AccountSnapshot, Metric, Money, Pace, Provider};
use serde_json::Value;

use crate::pace::elapsed_pace;
use crate::{HttpClient, ProviderError, UsageProvider};

const USAGE_ENDPOINT: &str = "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage";
const SIGN_IN_HINT: &str = "Sign in to Cursor, then refresh CodexBar.";

pub fn default_auth_path() -> PathBuf {
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_default()
        .join("Cursor")
        .join("auth.json")
}

pub fn read_access_token(path: &Path) -> Result<String, ProviderError> {
    let not_signed_in = ProviderError::NotSignedIn { hint: SIGN_IN_HINT };
    let text = std::fs::read_to_string(path).map_err(|_| not_signed_in.clone())?;
    let json: Value = serde_json::from_str(&text).map_err(|_| not_signed_in.clone())?;
    json.get("accessToken")
        .and_then(Value::as_str)
        .filter(|token| !token.trim().is_empty())
        .map(str::to_owned)
        .ok_or(not_signed_in)
}

/// Maps `GetCurrentPeriodUsage`: `planUsage.*PercentUsed` are percentages, `billingCycleEnd` (and
/// `billingCycleStart` when sent) are Unix milliseconds as a string or number, and `spendLimitUsage` carries the
/// on-demand limit and what is left of it in US cents.
///
/// Projections (#83) use the real billing period: its start when Cursor sends one, otherwise one month before the
/// end (Cursor bills monthly, also on annual plans). A start that can't be read or isn't before the end gives no
/// projection rather than a guessed one.
pub fn parse_usage(payload: &str, now: DateTime<Utc>) -> Result<AccountSnapshot, ProviderError> {
    let json: Value = serde_json::from_str(payload).map_err(|_| ProviderError::Unexpected { detail: "not JSON" })?;
    let resets_at = json
        .get("billingCycleEnd")
        .and_then(millis)
        .ok_or(ProviderError::Unexpected {
            detail: "missing billing cycle end",
        })?;
    let start = match json.get("billingCycleStart") {
        Some(value) => millis(value),
        None => resets_at.checked_sub_months(Months::new(1)),
    }
    .filter(|start| *start < resets_at);
    let pace = |used: f64| start.and_then(|start| elapsed_pace(used, start, resets_at, now));
    let plan = json.get("planUsage").ok_or(ProviderError::Unexpected {
        detail: "missing plan usage",
    })?;

    let mut metrics: Vec<Metric> = [
        ("totalPercentUsed", "Included usage"),
        ("autoPercentUsed", "Auto"),
        ("apiPercentUsed", "API"),
    ]
    .iter()
    .filter_map(|(key, label)| {
        let used = (plan.get(*key)?.as_f64()? / 100.0).clamp(0.0, 1.0);
        Some(Metric::Window {
            label: (*label).to_owned(),
            used,
            resets_at,
            pace: pace(used),
        })
    })
    .collect();
    if metrics.is_empty() {
        return Err(ProviderError::Unexpected {
            detail: "no usage percentages",
        });
    }
    if let Some(on_demand) = on_demand(&json, resets_at, pace) {
        metrics.push(on_demand);
    }
    Ok(AccountSnapshot::new(
        AccountId::new("cursor"),
        Provider::Cursor,
        metrics,
        now,
    ))
}

/// Unix milliseconds, as a number or a numeric string.
fn millis(value: &Value) -> Option<DateTime<Utc>> {
    value
        .as_i64()
        .or_else(|| value.as_str()?.trim().parse().ok())
        .and_then(DateTime::from_timestamp_millis)
}

/// On-demand spend against its limit, when an individual limit is set.
fn on_demand(json: &Value, resets_at: DateTime<Utc>, pace: impl Fn(f64) -> Option<Pace>) -> Option<Metric> {
    let usage = json.get("spendLimitUsage")?;
    let limit = usage.get("individualLimit")?.as_f64()?.round();
    let remaining = usage.get("individualRemaining")?.as_f64()?.round();
    if !(limit.is_finite() && remaining.is_finite()) || limit <= 0.0 {
        return None;
    }
    let spent = (limit - remaining).max(0.0);
    Some(Metric::Spend {
        label: "On-demand".to_owned(),
        spent: Money::from_cents(spent as i64),
        limit: Some(Money::from_cents(limit as i64)),
        resets_at: Some(resets_at),
        pace: pace((spent / limit).clamp(0.0, 1.0)),
    })
}

pub struct CursorProvider<H: HttpClient> {
    http: H,
    auth_path: PathBuf,
}

impl<H: HttpClient> CursorProvider<H> {
    pub fn new(http: H, auth_path: PathBuf) -> Self {
        Self { http, auth_path }
    }
}

impl<H: HttpClient> UsageProvider for CursorProvider<H> {
    fn name(&self) -> &'static str {
        "Cursor"
    }

    fn fetch(&self, now: DateTime<Utc>) -> Result<Vec<AccountSnapshot>, ProviderError> {
        let token = read_access_token(&self.auth_path)?;
        let bearer = format!("Bearer {token}");
        let headers = [
            ("Authorization", bearer.as_str()),
            ("Content-Type", "application/json"),
            ("Accept", "application/json"),
            ("Connect-Protocol-Version", "1"),
        ];
        let response = self.http.post_json(USAGE_ENDPOINT, &headers, "{}")?;
        match response.status {
            200..=299 => parse_usage(&response.body, now).map(|account| vec![account]),
            401 | 403 => Err(ProviderError::Expired { hint: SIGN_IN_HINT }),
            status => Err(ProviderError::Http { status }),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::HttpResponse;
    use codexbar_core::Severity;

    fn now() -> DateTime<Utc> {
        "2026-10-07T02:00:00Z".parse().unwrap()
    }

    const USAGE: &str = r#"{"billingCycleEnd":"1792540800000","planUsage":{"totalPercentUsed":71,"autoPercentUsed":40,"apiPercentUsed":12.5}}"#;

    struct Fixed(HttpResponse);

    impl HttpClient for Fixed {
        fn get(&self, _: &str, _: &[(&str, &str)]) -> Result<HttpResponse, ProviderError> {
            unreachable!("Cursor posts")
        }

        fn post_json(&self, _: &str, _: &[(&str, &str)], body: &str) -> Result<HttpResponse, ProviderError> {
            assert_eq!(body, "{}");
            Ok(self.0.clone())
        }
    }

    #[test]
    fn parse_usage_maps_percentages_and_cycle_end() {
        let account = parse_usage(USAGE, now()).unwrap();
        let labels: Vec<&str> = account.metrics().iter().map(Metric::label).collect();
        assert_eq!(labels, ["Included usage", "Auto", "API"]);
        assert_eq!(account.primary().unwrap().used_fraction(), Some(0.71));
        assert_eq!(
            account.primary().unwrap().resets_at(),
            Some("2026-10-21T00:00:00Z".parse().unwrap())
        );
        // 71% in 16 days of a 30-day cycle runs out before Oct 21.
        assert_eq!(account.assess(now()).severity(), Severity::AtRisk);
    }

    /// A payload shaped like Cursor's, with the fields CodexBar doesn't read left in.
    const FULL: &str = r#"{
        "billingCycleStart": "1790553600000",
        "billingCycleEnd": "1792540800000",
        "planUsage": {"totalSpend": 1420, "includedSpend": 1420, "limit": 2000,
                      "totalPercentUsed": 71, "autoPercentUsed": 40, "apiPercentUsed": 12.5},
        "spendLimitUsage": {"individualLimit": 2000, "individualRemaining": 1500},
        "displayMessage": "You've used 71% of your included usage"
    }"#;

    fn with_start(start: &str) -> String {
        format!(
            r#"{{"billingCycleStart":{start},"billingCycleEnd":"1792540800000","planUsage":{{"totalPercentUsed":40}}}}"#
        )
    }

    #[test]
    fn parse_usage_projects_over_the_real_billing_period() {
        let account = parse_usage(FULL, now()).unwrap();
        let included = account.primary().unwrap();
        // Sep 28 to Oct 21: 71% in 9 days 2 hours runs out well before the reset.
        let projected = included.projected_at_reset(now()).unwrap();
        assert!((projected - (0.71 + 0.71 / 218.0 * 334.0)).abs() < 1e-6, "{projected}");
        assert!(included.exhausts_before_reset(now()));
        // The default one-month period projects less from the same usage.
        let monthly = parse_usage(USAGE, now()).unwrap();
        let projected = monthly.primary().unwrap().projected_at_reset(now()).unwrap();
        assert!((projected - (0.71 + 0.71 / 386.0 * 334.0)).abs() < 1e-6, "{projected}");
    }

    #[test]
    fn parse_usage_maps_on_demand_spend_with_its_projection() {
        let account = parse_usage(FULL, now()).unwrap();
        let on_demand = account.metrics().last().unwrap();
        assert_eq!(on_demand.key(), "on-demand");
        assert_eq!(on_demand.used_display(), "$5.00 of $20.00");
        assert_eq!(on_demand.headroom(), Some(Money::from_cents(1500)));
        let projected = on_demand.projected_at_reset(now()).unwrap();
        assert!((projected - (0.25 + 0.25 / 218.0 * 334.0)).abs() < 1e-6, "{projected}");
        assert!(!on_demand.exhausts_before_reset(now()));
    }

    #[test]
    fn parse_usage_without_an_on_demand_limit_adds_no_spend() {
        let zero = r#"{"billingCycleEnd":"1792540800000","planUsage":{"totalPercentUsed":10},
            "spendLimitUsage":{"individualLimit":0,"individualRemaining":0}}"#;
        assert_eq!(parse_usage(zero, now()).unwrap().metrics().len(), 1);
        let partial = r#"{"billingCycleEnd":"1792540800000","planUsage":{"totalPercentUsed":10},
            "spendLimitUsage":{"individualLimit":2000}}"#;
        assert_eq!(parse_usage(partial, now()).unwrap().metrics().len(), 1);
    }

    #[test]
    fn invalid_or_zero_length_periods_omit_projections() {
        for start in [r#""soon""#, "null", r#""1792540800000""#, r#""1795000000000""#] {
            let account = parse_usage(&with_start(start), now()).unwrap();
            let metric = account.primary().unwrap();
            assert_eq!(metric.used_fraction(), Some(0.4), "{start}: usage is still shown");
            assert_eq!(metric.projected_at_reset(now()), None, "{start}");
            assert_eq!(account.assess(now()).severity(), Severity::Normal, "{start}");
        }
    }

    #[test]
    fn projections_respect_the_clock() {
        // Before the period starts, in its first tenth, and after it ended: nothing to project from.
        let start = "2026-09-28T00:00:00Z".parse::<DateTime<Utc>>().unwrap();
        let end = "2026-10-21T00:00:00Z".parse::<DateTime<Utc>>().unwrap();
        for at in [
            start - chrono::Duration::hours(1),
            start + chrono::Duration::hours(12),
            end,
            end + chrono::Duration::days(1),
        ] {
            let account = parse_usage(FULL, at).unwrap();
            assert_eq!(account.primary().unwrap().projected_at_reset(at), None, "{at}");
        }
        let later = start + chrono::Duration::days(5);
        assert!(
            parse_usage(FULL, later)
                .unwrap()
                .primary()
                .unwrap()
                .projected_at_reset(later)
                .is_some()
        );
    }

    #[test]
    fn parse_usage_numeric_cycle_end_and_missing_plan() {
        let numeric = r#"{"billingCycleEnd":1792540800000,"planUsage":{"totalPercentUsed":10}}"#;
        assert_eq!(parse_usage(numeric, now()).unwrap().metrics().len(), 1);
        assert!(parse_usage(r#"{"billingCycleEnd":1792540800000}"#, now()).is_err());
    }

    #[test]
    fn fetch_missing_auth_is_not_signed_in() {
        let provider = CursorProvider::new(Fixed(HttpResponse::new(200, USAGE)), PathBuf::from("Z:/nope/auth.json"));
        assert!(matches!(provider.fetch(now()), Err(ProviderError::NotSignedIn { .. })));
    }

    #[test]
    fn fetch_posts_and_maps_rejection() {
        let path = std::env::temp_dir().join(format!("codexbar-cursor-{}.json", std::process::id()));
        std::fs::write(&path, r#"{"accessToken":"tok"}"#).unwrap();
        let ok = CursorProvider::new(Fixed(HttpResponse::new(200, USAGE)), path.clone());
        assert_eq!(ok.fetch(now()).unwrap().len(), 1);
        let rejected = CursorProvider::new(Fixed(HttpResponse::new(401, "")), path.clone());
        assert!(matches!(rejected.fetch(now()), Err(ProviderError::Expired { .. })));
        let _ = std::fs::remove_file(path);
    }
}
