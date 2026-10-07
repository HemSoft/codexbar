//! Cursor included-usage for the current billing period, read with the sign-in Cursor keeps in
//! `%APPDATA%\Cursor\auth.json`.

use std::path::{Path, PathBuf};

use chrono::{DateTime, Months, Utc};
use codexbar_core::{AccountId, AccountSnapshot, Metric, Provider};
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

/// Maps `GetCurrentPeriodUsage`: `planUsage.*PercentUsed` are percentages, `billingCycleEnd` is Unix milliseconds
/// (as a string or number).
pub fn parse_usage(payload: &str, now: DateTime<Utc>) -> Result<AccountSnapshot, ProviderError> {
    let json: Value = serde_json::from_str(payload).map_err(|_| ProviderError::Unexpected { detail: "not JSON" })?;
    let resets_at = json
        .get("billingCycleEnd")
        .and_then(|value| value.as_i64().or_else(|| value.as_str()?.parse().ok()))
        .and_then(DateTime::from_timestamp_millis)
        .ok_or(ProviderError::Unexpected {
            detail: "missing billing cycle end",
        })?;
    let start = resets_at.checked_sub_months(Months::new(1)).unwrap_or(resets_at);
    let plan = json.get("planUsage").ok_or(ProviderError::Unexpected {
        detail: "missing plan usage",
    })?;

    let metrics: Vec<Metric> = [
        ("totalPercentUsed", "Included usage"),
        ("autoPercentUsed", "Auto"),
        ("apiPercentUsed", "API"),
    ]
    .iter()
    .filter_map(|(key, label)| {
        let used = (plan.get(*key)?.as_f64()? / 100.0).clamp(0.0, 1.0);
        let pace = elapsed_pace(used, start, resets_at, now);
        Some(Metric::Window {
            label: (*label).to_owned(),
            used,
            resets_at,
            pace,
        })
    })
    .collect();
    if metrics.is_empty() {
        return Err(ProviderError::Unexpected {
            detail: "no usage percentages",
        });
    }
    Ok(AccountSnapshot::new(
        AccountId::new("cursor"),
        Provider::Cursor,
        metrics,
        now,
    ))
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
