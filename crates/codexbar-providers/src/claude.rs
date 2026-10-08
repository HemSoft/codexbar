//! Claude subscription limits (5-hour and weekly), read with the sign-in Claude Code keeps in
//! `~/.claude/.credentials.json`.
//!
//! Read-only by design: this provider never refreshes or rewrites Claude Code's OAuth token, because a refresh
//! racing Claude Code can sign it out. Safe shared renewal is issue #80. The usage endpoint punishes polling with
//! hour-long 429s, so results are cached and every 429/403 starts a backoff.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};
use codexbar_core::{AccountId, AccountSnapshot, Currency, Metric, Money, Provider};
use serde_json::Value;

use crate::pace::elapsed_pace;
use crate::{HttpClient, ProviderError, UsageProvider};

const USAGE_ENDPOINT: &str = "https://api.anthropic.com/api/oauth/usage";
const SIGN_IN_HINT: &str = "Run `claude` and sign in with your Claude account.";
const EXPIRED_HINT: &str = "Open Claude Code once to renew the sign-in.";
/// Fresh results are reused this long; the endpoint does not need 2-minute polling.
const CACHE_TTL_MINUTES: i64 = 10;
/// Minimum wait after a 429, even when Retry-After is shorter.
const RATE_LIMIT_BACKOFF_MINUTES: i64 = 15;
/// A 403 is usually an org policy; asking again soon only re-arms the limiter.
const FORBIDDEN_BACKOFF_HOURS: i64 = 6;

/// The parts of the Claude Code sign-in this provider uses.
#[derive(Clone, PartialEq, Eq)]
pub struct Credentials {
    access_token: String,
    expires_at: Option<DateTime<Utc>>,
    subscription: Option<String>,
}

impl std::fmt::Debug for Credentials {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Credentials")
            .field("access_token", &"<redacted>")
            .field("expires_at", &self.expires_at)
            .field("subscription", &self.subscription)
            .finish()
    }
}

/// `$CLAUDE_CONFIG_DIR/.credentials.json`, or `~/.claude/.credentials.json`.
pub fn default_credentials_path() -> PathBuf {
    std::env::var_os("CLAUDE_CONFIG_DIR")
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("USERPROFILE").map(|profile| PathBuf::from(profile).join(".claude")))
        .unwrap_or_else(|| PathBuf::from(".claude"))
        .join(".credentials.json")
}

pub fn read_credentials(path: &Path) -> Result<Credentials, ProviderError> {
    let not_signed_in = ProviderError::NotSignedIn { hint: SIGN_IN_HINT };
    let text = std::fs::read_to_string(path).map_err(|_| not_signed_in.clone())?;
    let json: Value = serde_json::from_str(&text).map_err(|_| not_signed_in.clone())?;
    let oauth = json.get("claudeAiOauth").ok_or_else(|| not_signed_in.clone())?;
    let access_token = oauth
        .get("accessToken")
        .and_then(Value::as_str)
        .filter(|token| !token.trim().is_empty())
        .ok_or(not_signed_in)?
        .to_owned();
    let expires_at = oauth
        .get("expiresAt")
        .and_then(Value::as_i64)
        .and_then(DateTime::from_timestamp_millis);
    let subscription = oauth.get("subscriptionType").and_then(Value::as_str).map(str::to_owned);
    Ok(Credentials {
        access_token,
        expires_at,
        subscription,
    })
}

/// Maps `/api/oauth/usage` (#82): the shared 5-hour window, the all-model weekly window, every model-scoped weekly
/// window the endpoint returns (`seven_day_opus`, `seven_day_sonnet`, and any newer `seven_day_*`, such as Fable),
/// and extra usage as money. Windows that are null or incomplete are skipped; extra usage that can't be read becomes a
/// message rather than failing the account. Only an account with no window at all is an error.
pub fn parse_usage(
    payload: &str,
    subscription: Option<&str>,
    now: DateTime<Utc>,
) -> Result<AccountSnapshot, ProviderError> {
    let json: Value = serde_json::from_str(payload).map_err(|_| ProviderError::Unexpected { detail: "not JSON" })?;
    let mut metrics: Vec<Metric> = window_keys(&json)
        .iter()
        .filter_map(|key| {
            let window = json.get(key)?;
            let used = (window.get("utilization")?.as_f64()? / 100.0).clamp(0.0, 1.0);
            let resets_at = DateTime::parse_from_rfc3339(window.get("resets_at")?.as_str()?)
                .ok()?
                .with_timezone(&Utc);
            let length = if key == "five_hour" {
                Duration::hours(5)
            } else {
                Duration::days(7)
            };
            Some(Metric::Window {
                label: window_label(key),
                used,
                resets_at,
                pace: elapsed_pace(used, resets_at - length, resets_at, now),
            })
        })
        .collect();
    if metrics.is_empty() {
        return Err(ProviderError::Unexpected {
            detail: "no usage windows for this account",
        });
    }
    let mut message = None;
    match extra_usage(&json) {
        Some(Ok(spend)) => metrics.push(spend),
        Some(Err(text)) => message = Some(text),
        None => {}
    }
    let mut account = AccountSnapshot::new(AccountId::new("claude"), Provider::Claude, metrics, now);
    if let Some(message) = message {
        account = account.with_message(message);
    }
    Ok(match subscription.filter(|s| !s.is_empty()) {
        Some(plan) => account.with_label(crate::codex::plan_name(plan)),
        None => account,
    })
}

/// The window keys to read, in display order: the 5-hour and all-model weekly windows, the model windows known today,
/// then any other `seven_day_*` window alphabetically, so a new model's limit shows without a code change.
fn window_keys(json: &Value) -> Vec<String> {
    const KNOWN: [&str; 5] = [
        "five_hour",
        "seven_day",
        "seven_day_opus",
        "seven_day_sonnet",
        "seven_day_oauth_apps",
    ];
    let mut keys: Vec<String> = KNOWN.iter().map(|key| (*key).to_owned()).collect();
    let mut others: Vec<String> = json
        .as_object()
        .map(|object| {
            object
                .keys()
                .filter(|key| key.starts_with("seven_day_") && !KNOWN.contains(&key.as_str()))
                .cloned()
                .collect()
        })
        .unwrap_or_default();
    others.sort();
    // Newer model windows go before the OAuth-apps window, which isn't a model.
    keys.splice(4..4, others);
    keys
}

/// "Weekly Opus", "Weekly Fable", "Weekly OAuth apps". The label is also the metric's stable key.
fn window_label(key: &str) -> String {
    match key {
        "five_hour" => "5-hour window".to_owned(),
        "seven_day" => "Weekly".to_owned(),
        "seven_day_oauth_apps" => "Weekly OAuth apps".to_owned(),
        other => {
            let model = other.trim_start_matches("seven_day_").replace('_', " ");
            let mut chars = model.chars();
            let model = match chars.next() {
                Some(first) => first.to_uppercase().chain(chars).collect::<String>(),
                None => model,
            };
            format!("Weekly {model}")
        }
    }
}

/// Extra usage as money: `used_credits` against `monthly_limit`, in minor units of `currency` (US dollars when
/// absent) with `decimal_places` digits (the currency's own when absent). `None` when it is off or not reported;
/// a message when it is on but can't be shown faithfully.
fn extra_usage(json: &Value) -> Option<Result<Metric, String>> {
    let extra = json.get("extra_usage")?;
    if !extra.get("is_enabled").and_then(Value::as_bool).unwrap_or(false) {
        return None;
    }
    let code = extra.get("currency").and_then(Value::as_str).unwrap_or("USD");
    let Some(currency) = Currency::from_code(code) else {
        return Some(Err(format!(
            "Extra usage is billed in {code}, which CodexBar can't show yet."
        )));
    };
    let places = extra
        .get("decimal_places")
        .and_then(Value::as_u64)
        .map_or(currency.minor_digits(), |places| places as u32);
    // Rescale from the payload's decimal places to the currency's minor unit (both are 2 for most currencies).
    let minor = |field: &str| -> Option<i64> {
        let value = extra.get(field)?.as_f64()?;
        let scale = 10f64.powi(currency.minor_digits() as i32 - places as i32);
        Some((value * scale).round() as i64)
    };
    let spent = minor("used_credits")?;
    let limit = minor("monthly_limit").filter(|limit| *limit > 0);
    Some(Ok(Metric::Spend {
        label: "Extra usage".to_owned(),
        spent: Money::new(spent.max(0), currency),
        limit: limit.map(|limit| Money::new(limit, currency)),
        // Extra usage resets monthly, but the endpoint doesn't say when.
        resets_at: None,
        pace: None,
    }))
}

#[derive(Default)]
struct State {
    cached: Option<AccountSnapshot>,
    backoff_until: Option<DateTime<Utc>>,
}

pub struct ClaudeProvider<H: HttpClient> {
    http: H,
    credentials_path: PathBuf,
    state: Mutex<State>,
}

impl<H: HttpClient> ClaudeProvider<H> {
    pub fn new(http: H, credentials_path: PathBuf) -> Self {
        Self {
            http,
            credentials_path,
            state: Mutex::default(),
        }
    }

    fn request(&self, credentials: &Credentials, now: DateTime<Utc>) -> Result<AccountSnapshot, ProviderError> {
        let bearer = format!("Bearer {}", credentials.access_token);
        let headers = [
            ("Authorization", bearer.as_str()),
            ("Accept", "application/json"),
            ("anthropic-beta", "oauth-2025-04-20"),
            ("User-Agent", "claude-cli/2.1.12 (external, cli)"),
        ];
        let response = self.http.get(USAGE_ENDPOINT, &headers)?;
        let mut state = self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        match response.status {
            200..=299 => {
                let account = parse_usage(&response.body, credentials.subscription.as_deref(), now)?;
                state.cached = Some(account.clone());
                state.backoff_until = None;
                Ok(account)
            }
            429 => {
                let retry_after = response
                    .retry_after_secs
                    .map(|secs| Duration::seconds(secs as i64))
                    .unwrap_or_default();
                let until = now + retry_after.max(Duration::minutes(RATE_LIMIT_BACKOFF_MINUTES));
                state.backoff_until = Some(until);
                Err(ProviderError::RateLimited { retry_at: until })
            }
            401 => Err(ProviderError::Expired { hint: EXPIRED_HINT }),
            403 => {
                state.backoff_until = Some(now + Duration::hours(FORBIDDEN_BACKOFF_HOURS));
                Err(ProviderError::Http { status: 403 })
            }
            status => Err(ProviderError::Http { status }),
        }
    }
}

impl<H: HttpClient> UsageProvider for ClaudeProvider<H> {
    fn name(&self) -> &'static str {
        "Claude"
    }

    fn fetch(&self, now: DateTime<Utc>) -> Result<Vec<AccountSnapshot>, ProviderError> {
        {
            let state = self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            if let Some(cached) = &state.cached
                && now - cached.fetched_at() < Duration::minutes(CACHE_TTL_MINUTES)
            {
                return Ok(vec![cached.clone()]);
            }
            if let Some(until) = state.backoff_until.filter(|until| now < *until) {
                return Err(ProviderError::RateLimited { retry_at: until });
            }
        }
        let credentials = read_credentials(&self.credentials_path)?;
        if credentials.expires_at.is_some_and(|at| at <= now) {
            return Err(ProviderError::Expired { hint: EXPIRED_HINT });
        }
        self.request(&credentials, now).map(|account| vec![account])
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::*;
    use crate::HttpResponse;
    use codexbar_core::Severity;

    fn now() -> DateTime<Utc> {
        "2026-10-07T02:00:00Z".parse().unwrap()
    }

    const FIXTURE: &str = include_str!("../tests/fixtures/claude-usage.json");

    struct FakeHttp {
        responses: Vec<HttpResponse>,
        calls: AtomicUsize,
    }

    impl FakeHttp {
        fn new(responses: Vec<HttpResponse>) -> Self {
            Self {
                responses,
                calls: AtomicUsize::new(0),
            }
        }
    }

    impl HttpClient for FakeHttp {
        fn get(&self, _: &str, _: &[(&str, &str)]) -> Result<HttpResponse, ProviderError> {
            let call = self.calls.fetch_add(1, Ordering::SeqCst);
            Ok(self.responses[call.min(self.responses.len() - 1)].clone())
        }
    }

    struct CredentialsFile(PathBuf);

    impl CredentialsFile {
        fn new(name: &str, expires_at: DateTime<Utc>) -> Self {
            let path = std::env::temp_dir().join(format!("codexbar-claude-{}-{name}.json", std::process::id()));
            let body = format!(
                r#"{{"claudeAiOauth":{{"accessToken":"secret-token","refreshToken":"r","expiresAt":{},"subscriptionType":"pro"}}}}"#,
                expires_at.timestamp_millis()
            );
            std::fs::write(&path, body).unwrap();
            Self(path)
        }
    }

    impl Drop for CredentialsFile {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    fn provider(name: &str, responses: Vec<HttpResponse>) -> (ClaudeProvider<FakeHttp>, CredentialsFile) {
        let file = CredentialsFile::new(name, now() + Duration::hours(8));
        (ClaudeProvider::new(FakeHttp::new(responses), file.0.clone()), file)
    }

    #[test]
    fn current_payload_maps_every_window_and_extra_usage_in_its_currency() {
        let payload = include_str!("../tests/fixtures/claude-usage-current.json");
        let account = parse_usage(payload, Some("max"), now()).unwrap();
        let keys: Vec<String> = account.metrics().iter().map(Metric::key).collect();
        assert_eq!(
            keys,
            ["5-hour-window", "weekly", "weekly-opus", "weekly-fable", "extra-usage"],
            "all-model and model-scoped weekly limits stay distinct; null windows are skipped"
        );
        let fable = &account.metrics()[3];
        assert_eq!(fable.label(), "Weekly Fable");
        assert_eq!(fable.used_fraction(), Some(0.74));
        assert!(fable.resets_at().is_some());
        let extra = account.metrics().last().unwrap();
        assert_eq!(extra.used_display(), "S$12.34 of S$60.00");
        assert_eq!(extra.headroom(), Some(Money::new(4766, Currency::Sgd)));
        assert!(account.messages().is_empty());
    }

    #[test]
    fn extra_usage_handles_missing_and_unusual_fields() {
        let with_extra = |extra: &str| {
            let payload = format!(
                r#"{{"five_hour":{{"utilization":10,"resets_at":"2026-10-07T03:00:00Z"}},"extra_usage":{extra}}}"#
            );
            parse_usage(&payload, None, now()).unwrap()
        };
        // Off, or not reported: no money metric.
        assert_eq!(with_extra(r#"{"is_enabled":false}"#).metrics().len(), 1);
        assert_eq!(with_extra("null").metrics().len(), 1);
        // Legacy payloads without a currency are US dollars in cents.
        let legacy = with_extra(r#"{"is_enabled":true,"monthly_limit":1000,"used_credits":277,"utilization":27.7}"#);
        assert_eq!(legacy.metrics()[1].used_display(), "$2.77 of $10.00");
        // No limit set: spend without a cap.
        let uncapped = with_extra(r#"{"is_enabled":true,"monthly_limit":null,"used_credits":500,"currency":"EUR"}"#);
        assert_eq!(uncapped.metrics()[1].used_display(), "€5.00 spent");
        // Whole-unit payloads are rescaled to the currency's minor unit.
        let whole = with_extra(
            r#"{"is_enabled":true,"monthly_limit":60,"used_credits":12,"currency":"CAD","decimal_places":0}"#,
        );
        assert_eq!(whole.metrics()[1].used_display(), "CA$12.00 of CA$60.00");
        // A currency CodexBar doesn't know: said, not misreported, and the windows still show.
        let unknown = with_extra(r#"{"is_enabled":true,"monthly_limit":6000,"used_credits":10,"currency":"XAU"}"#);
        assert_eq!(unknown.metrics().len(), 1);
        assert_eq!(
            unknown.messages(),
            ["Extra usage is billed in XAU, which CodexBar can't show yet."]
        );
        // Enabled but no credits reported: nothing to show.
        assert_eq!(with_extra(r#"{"is_enabled":true}"#).metrics().len(), 1);
    }

    #[test]
    fn extra_usage_near_its_limit_raises_the_account_status() {
        let payload = r#"{"five_hour":{"utilization":10,"resets_at":"2026-10-07T03:00:00Z"},
            "extra_usage":{"is_enabled":true,"monthly_limit":6000,"used_credits":5760,"currency":"SGD"}}"#;
        let account = parse_usage(payload, None, now()).unwrap();
        assert_eq!(account.assess(now()).severity(), Severity::LimitSoon);
    }

    #[test]
    fn incomplete_windows_are_skipped_not_fatal() {
        let payload = r#"{"five_hour":{"utilization":10,"resets_at":null},
            "seven_day":{"utilization":40,"resets_at":"2026-10-10T08:00:00Z"},
            "seven_day_fable":{"utilization":null,"resets_at":"2026-10-10T08:00:00Z"}}"#;
        let account = parse_usage(payload, None, now()).unwrap();
        let labels: Vec<&str> = account.metrics().iter().map(Metric::label).collect();
        assert_eq!(labels, ["Weekly"]);
    }

    #[test]
    fn parse_usage_fixture_maps_windows_and_plan() {
        let account = parse_usage(FIXTURE, Some("pro"), now()).unwrap();
        assert_eq!(account.display_name(), "Claude · Pro");
        let labels: Vec<&str> = account.metrics().iter().map(Metric::label).collect();
        assert_eq!(labels, ["5-hour window", "Weekly"]);
        assert_eq!(account.primary().unwrap().used_fraction(), Some(0.64));
        // Weekly: 82% after 5.5 days runs out about six hours before Thursday's reset.
        assert_eq!(account.assess(now()).severity(), Severity::AtRisk);
    }

    #[test]
    fn parse_usage_without_windows_is_unexpected() {
        assert!(matches!(
            parse_usage(r#"{"five_hour":null,"seven_day":null}"#, None, now()),
            Err(ProviderError::Unexpected { .. })
        ));
    }

    #[test]
    fn fetch_caches_fresh_results() {
        let (provider, _file) = provider("cache", vec![HttpResponse::new(200, FIXTURE)]);
        provider.fetch(now()).unwrap();
        provider.fetch(now() + Duration::minutes(5)).unwrap();
        assert_eq!(provider.http.calls.load(Ordering::SeqCst), 1);
        provider.fetch(now() + Duration::minutes(11)).unwrap();
        assert_eq!(provider.http.calls.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn fetch_rate_limited_backs_off_for_retry_after_at_least_fifteen_minutes() {
        let limited = HttpResponse {
            retry_after_secs: Some(3600),
            ..HttpResponse::new(429, "")
        };
        let (provider, _file) = provider("429", vec![limited, HttpResponse::new(200, FIXTURE)]);
        let err = provider.fetch(now()).unwrap_err();
        assert_eq!(
            err,
            ProviderError::RateLimited {
                retry_at: now() + Duration::hours(1)
            }
        );
        // Within the backoff no request is made at all.
        assert!(provider.fetch(now() + Duration::minutes(30)).is_err());
        assert_eq!(provider.http.calls.load(Ordering::SeqCst), 1);
        assert!(provider.fetch(now() + Duration::minutes(61)).is_ok());
    }

    #[test]
    fn fetch_rate_limited_short_retry_after_still_waits_fifteen_minutes() {
        let limited = HttpResponse {
            retry_after_secs: Some(30),
            ..HttpResponse::new(429, "")
        };
        let (provider, _file) = provider("429-short", vec![limited]);
        let err = provider.fetch(now()).unwrap_err();
        assert_eq!(
            err,
            ProviderError::RateLimited {
                retry_at: now() + Duration::minutes(15)
            }
        );
    }

    #[test]
    fn fetch_forbidden_backs_off_six_hours() {
        let (provider, _file) = provider("403", vec![HttpResponse::new(403, "")]);
        assert_eq!(provider.fetch(now()), Err(ProviderError::Http { status: 403 }));
        assert!(matches!(
            provider.fetch(now() + Duration::hours(5)),
            Err(ProviderError::RateLimited { .. })
        ));
        assert_eq!(provider.http.calls.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn fetch_expired_token_does_not_call_or_refresh() {
        let file = CredentialsFile::new("expired", now() - Duration::minutes(1));
        let provider = ClaudeProvider::new(FakeHttp::new(vec![HttpResponse::new(200, FIXTURE)]), file.0.clone());
        assert_eq!(
            provider.fetch(now()),
            Err(ProviderError::Expired { hint: EXPIRED_HINT })
        );
        assert_eq!(provider.http.calls.load(Ordering::SeqCst), 0);
        // The credentials file is untouched.
        assert!(
            std::fs::read_to_string(&file.0)
                .unwrap()
                .contains("\"refreshToken\":\"r\"")
        );
    }

    #[test]
    fn credentials_debug_redacts_token() {
        let file = CredentialsFile::new("debug", now());
        let debug = format!("{:?}", read_credentials(&file.0).unwrap());
        assert!(!debug.contains("secret-token"));
    }
}
