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

/// Maps `/api/oauth/usage` (#82), an undocumented endpoint, so every field is read defensively (see codexbar-ios
/// `CLAUDE-USAGE.md`):
///
/// - Windows come from the structured `limits[]` first: `session` (5-hour), `weekly_all` (Weekly) and every
///   `weekly_scoped` entry under the model name the server gives (such as Fable). The flat `five_hour`, `seven_day`
///   (or `seven_day_oauth_apps`), `seven_day_opus` and `seven_day_sonnet` fields only fill in limits `limits[]` doesn't
///   report. Other flat fields are internal codenames and are never shown.
/// - Money comes from `spend` first, then `extra_usage`, in minor units of the reported currency. Without a currency
///   nothing is assumed: the amounts are left out and a message says so. A prepaid `spend.balance` is its own metric,
///   never derived from the limit.
///
/// An account with only money (say, Enterprise credits) is valid; only a response with neither is an error.
pub fn parse_usage(
    payload: &str,
    subscription: Option<&str>,
    now: DateTime<Utc>,
) -> Result<AccountSnapshot, ProviderError> {
    let json: Value = serde_json::from_str(payload).map_err(|_| ProviderError::Unexpected { detail: "not JSON" })?;
    let mut metrics = windows(&json, now);
    let mut messages = Vec::new();
    let (found, notes) = money(&json);
    metrics.extend(found);
    messages.extend(notes);
    if metrics.is_empty() {
        return Err(ProviderError::Unexpected {
            detail: "no usage windows or credits for this account",
        });
    }
    let mut account = AccountSnapshot::new(AccountId::new("claude"), Provider::Claude, metrics, now);
    for message in messages {
        account = account.with_message(message);
    }
    Ok(match subscription.filter(|s| !s.is_empty()) {
        Some(plan) => account.with_label(crate::codex::plan_name(plan)),
        None => account,
    })
}

/// One limit window as reported: a label, percent used (0..=100) and the reset.
struct Window {
    label: String,
    percent: f64,
    resets_at: DateTime<Utc>,
    length: Duration,
}

impl Window {
    fn metric(self, now: DateTime<Utc>) -> Metric {
        let used = (self.percent / 100.0).clamp(0.0, 1.0);
        Metric::Window {
            label: self.label,
            used,
            resets_at: self.resets_at,
            pace: elapsed_pace(used, self.resets_at - self.length, self.resets_at, now),
        }
    }
}

const SESSION: &str = "5-hour window";
const WEEKLY: &str = "Weekly";

/// The windows in display order: 5-hour, Weekly, then model-scoped weekly limits (structured ones in the server's
/// order, then flat Opus and Sonnet when `limits[]` doesn't have them).
fn windows(json: &Value, now: DateTime<Utc>) -> Vec<Metric> {
    let reset = |value: Option<&Value>| {
        DateTime::parse_from_rfc3339(value?.as_str()?)
            .ok()
            .map(|at| at.with_timezone(&Utc))
    };
    let mut found: Vec<Window> = Vec::new();
    for limit in json.get("limits").and_then(Value::as_array).into_iter().flatten() {
        let label = match limit.get("kind").and_then(Value::as_str) {
            Some("session") => SESSION.to_owned(),
            Some("weekly_all") => WEEKLY.to_owned(),
            Some("weekly_scoped") => {
                let Some(model) = limit
                    .pointer("/scope/model/display_name")
                    .and_then(Value::as_str)
                    .map(str::trim)
                    .filter(|name| !name.is_empty())
                else {
                    continue;
                };
                format!("{WEEKLY} {model}")
            }
            // Unknown kinds are skipped, never guessed at.
            _ => continue,
        };
        let (Some(percent), Some(resets_at)) = (
            limit.get("percent").and_then(Value::as_f64),
            reset(limit.get("resets_at")),
        ) else {
            continue;
        };
        if found.iter().any(|window| window.label.eq_ignore_ascii_case(&label)) {
            continue;
        }
        let length = if label == SESSION {
            Duration::hours(5)
        } else {
            Duration::days(7)
        };
        found.push(Window {
            label,
            percent,
            resets_at,
            length,
        });
    }
    // Flat fields fill in what the structured list didn't report.
    let flat: [(&[&str], &str, Duration); 4] = [
        (&["five_hour"], SESSION, Duration::hours(5)),
        (&["seven_day", "seven_day_oauth_apps"], WEEKLY, Duration::days(7)),
        (&["seven_day_opus"], "Weekly Opus", Duration::days(7)),
        (&["seven_day_sonnet"], "Weekly Sonnet", Duration::days(7)),
    ];
    for (keys, label, length) in flat {
        if found.iter().any(|window| window.label.eq_ignore_ascii_case(label)) {
            continue;
        }
        let window = keys.iter().find_map(|key| {
            let value = json.get(*key)?;
            Some((value.get("utilization")?.as_f64()?, reset(value.get("resets_at"))?))
        });
        if let Some((percent, resets_at)) = window {
            found.push(Window {
                label: label.to_owned(),
                percent,
                resets_at,
                length,
            });
        }
    }
    // 5-hour first, then Weekly, then the model-scoped windows in the order found.
    found.sort_by_key(|window| match window.label.as_str() {
        SESSION => 0,
        WEEKLY => 1,
        _ => 2,
    });
    found.into_iter().map(|window| window.metric(now)).collect()
}

/// Money in a provider-owned representation: `{amount_minor, currency, exponent}`. `None` when malformed; an error
/// message when the currency is missing or one CodexBar can't show.
fn amount(value: &Value) -> Option<Result<Money, String>> {
    let minor = value.get("amount_minor")?.as_f64()?;
    let exponent = value
        .get("exponent")
        .and_then(Value::as_u64)
        .map(|places| places as u32);
    Some(to_money(minor, value.get("currency").and_then(Value::as_str), exponent))
}

/// `minor` in units of 10^-`places` of `code`, rescaled to that currency's own minor unit.
fn to_money(minor: f64, code: Option<&str>, places: Option<u32>) -> Result<Money, String> {
    let Some(code) = code.map(str::trim).filter(|code| !code.is_empty()) else {
        return Err("Claude didn't say which currency usage credits are in, so they aren't shown.".to_owned());
    };
    let Some(currency) = Currency::from_code(code) else {
        return Err(format!(
            "Usage credits are billed in {code}, which CodexBar can't show yet."
        ));
    };
    let places = places.unwrap_or(currency.minor_digits());
    let scale = 10f64.powi(currency.minor_digits() as i32 - places as i32);
    Ok(Money::new((minor * scale).round() as i64, currency))
}

/// Usage credits: month-to-date spend against the monthly limit, and the prepaid balance when reported. From `spend`
/// when present, else from `extra_usage`. Nothing while credits are off or not reported. Each amount stands alone: one
/// that can't be read (say, a limit in an unknown currency) is left out with a message, and the others still show.
fn money(json: &Value) -> (Vec<Metric>, Vec<String>) {
    let mut metrics = Vec::new();
    let mut messages: Vec<String> = Vec::new();
    let mut keep = |result: Result<Money, String>| match result {
        Ok(money) => Some(money),
        Err(message) => {
            if !messages.contains(&message) {
                messages.push(message);
            }
            None
        }
    };
    if let Some(spend) = json.get("spend").filter(|spend| spend.is_object()) {
        if !spend.get("enabled").and_then(Value::as_bool).unwrap_or(false) {
            return (metrics, messages);
        }
        if let Some(used) = spend.get("used").and_then(amount).and_then(&mut keep) {
            let limit = spend
                .get("limit")
                .and_then(amount)
                .and_then(&mut keep)
                .filter(|limit| limit.cents() > 0);
            metrics.push(credits(used, limit));
        }
        if let Some(balance) = spend.get("balance").and_then(amount).and_then(&mut keep) {
            metrics.push(Metric::Balance {
                label: "Credit balance".to_owned(),
                remaining: balance,
                burn_per_day: None,
            });
        }
        return (metrics, messages);
    }
    let Some(extra) = json.get("extra_usage").filter(|extra| extra.is_object()) else {
        return (metrics, messages);
    };
    if !extra.get("is_enabled").and_then(Value::as_bool).unwrap_or(false) {
        return (metrics, messages);
    }
    let code = extra.get("currency").and_then(Value::as_str);
    let places = extra
        .get("decimal_places")
        .and_then(Value::as_u64)
        .map(|places| places as u32);
    let reading = |field: &str| {
        extra
            .get(field)
            .and_then(Value::as_f64)
            .map(|value| to_money(value, code, places))
    };
    if let Some(used) = reading("used_credits").and_then(&mut keep) {
        let limit = reading("monthly_limit")
            .and_then(&mut keep)
            .filter(|limit| limit.cents() > 0);
        metrics.push(credits(used, limit));
    }
    (metrics, messages)
}

fn credits(used: Money, limit: Option<Money>) -> Metric {
    Metric::Spend {
        label: "Extra usage".to_owned(),
        spent: Money::new(used.cents().max(0), used.currency()),
        limit,
        // Credits reset monthly, but no verified response says when.
        resets_at: None,
        pace: None,
    }
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
    fn structured_limits_and_spend_map_every_window_and_credit_in_its_currency() {
        let payload = include_str!("../tests/fixtures/claude-usage-current.json");
        let account = parse_usage(payload, Some("max"), now()).unwrap();
        let keys: Vec<String> = account.metrics().iter().map(Metric::key).collect();
        assert_eq!(
            keys,
            [
                "5-hour-window",
                "weekly",
                "weekly-fable",
                "extra-usage",
                "credit-balance"
            ],
            "the scoped Fable limit stays distinct from Weekly; unknown kinds are skipped"
        );
        let fable = &account.metrics()[2];
        assert_eq!(fable.used_fraction(), Some(0.74));
        assert!(fable.resets_at().is_some());
        let credits = &account.metrics()[3];
        assert_eq!(credits.used_display(), "S$12.34 of S$60.00");
        assert_eq!(credits.headroom(), Some(Money::new(4766, Currency::Sgd)));
        assert_eq!(
            account.metrics()[4].used_display(),
            "S$100.00 left",
            "a prepaid balance, not derived"
        );
        assert!(account.messages().is_empty());
    }

    fn with(body: &str) -> Result<AccountSnapshot, ProviderError> {
        parse_usage(&format!("{{{body}}}"), None, now())
    }

    const SESSION_FLAT: &str = r#""five_hour":{"utilization":10,"resets_at":"2026-10-07T03:00:00Z"}"#;

    #[test]
    fn flat_fields_fill_in_without_promoting_codenames() {
        let account = with(&format!(
            r#"{SESSION_FLAT},
            "seven_day":{{"utilization":40,"resets_at":"2026-10-10T08:00:00Z"}},
            "seven_day_opus":{{"utilization":12,"resets_at":"2026-10-10T08:00:00Z"}},
            "seven_day_omelette":{{"utilization":90,"resets_at":"2026-10-10T08:00:00Z"}},
            "limits":[{{"kind":"weekly_scoped","percent":5,"resets_at":"2026-10-10T08:00:00Z",
                "scope":{{"model":{{"display_name":"Opus"}}}}}}]"#
        ))
        .unwrap();
        let shown: Vec<(&str, Option<f64>)> = account
            .metrics()
            .iter()
            .map(|metric| (metric.label(), metric.used_fraction()))
            .collect();
        // The structured Opus limit wins over the flat one; the codename never shows.
        assert_eq!(
            shown,
            [
                ("5-hour window", Some(0.1)),
                ("Weekly", Some(0.4)),
                ("Weekly Opus", Some(0.05))
            ]
        );
        // The OAuth-apps window stands in for Weekly when that's all there is.
        let apps = with(r#""seven_day_oauth_apps":{"utilization":20,"resets_at":"2026-10-10T08:00:00Z"}"#).unwrap();
        assert_eq!(apps.metrics()[0].label(), "Weekly");
    }

    #[test]
    fn extra_usage_is_read_when_spend_is_absent() {
        let usd = with(&format!(
            r#"{SESSION_FLAT},"extra_usage":{{"is_enabled":true,"monthly_limit":5000,"used_credits":0.0,
                "utilization":null,"currency":"USD","decimal_places":2}}"#
        ))
        .unwrap();
        assert_eq!(usd.metrics()[1].used_display(), "$0.00 of $50.00");
        let whole = with(&format!(
            r#"{SESSION_FLAT},"extra_usage":{{"is_enabled":true,"monthly_limit":60,"used_credits":12,
                "currency":"CAD","decimal_places":0}}"#
        ))
        .unwrap();
        assert_eq!(whole.metrics()[1].used_display(), "CA$12.00 of CA$60.00");
        let uncapped = with(&format!(
            r#"{SESSION_FLAT},"extra_usage":{{"is_enabled":true,"monthly_limit":null,"used_credits":500,"currency":"EUR"}}"#
        ))
        .unwrap();
        assert_eq!(uncapped.metrics()[1].used_display(), "€5.00 spent");
        // Off or not reported: nothing.
        for extra in [r#"{"is_enabled":false}"#, "null", r#"{"is_enabled":true}"#] {
            let account = with(&format!(r#"{SESSION_FLAT},"extra_usage":{extra}"#)).unwrap();
            assert_eq!(account.metrics().len(), 1, "{extra}");
        }
    }

    #[test]
    fn credits_without_a_known_currency_are_never_shown_as_dollars() {
        let missing = with(&format!(
            r#"{SESSION_FLAT},"extra_usage":{{"is_enabled":true,"monthly_limit":1000,"used_credits":277}}"#
        ))
        .unwrap();
        assert_eq!(missing.metrics().len(), 1, "no amount without a currency");
        assert_eq!(
            missing.messages(),
            ["Claude didn't say which currency usage credits are in, so they aren't shown."]
        );
        let unknown = with(&format!(
            r#"{SESSION_FLAT},"spend":{{"enabled":true,"used":{{"amount_minor":10,"currency":"XAU","exponent":2}}}}"#
        ))
        .unwrap();
        assert_eq!(unknown.metrics().len(), 1);
        assert_eq!(
            unknown.messages(),
            ["Usage credits are billed in XAU, which CodexBar can't show yet."]
        );
    }

    #[test]
    fn a_bad_optional_amount_leaves_the_others() {
        // The limit's currency is unknown: the spend shows without a cap, the balance still shows.
        let account = with(
            r#""spend":{"enabled":true,
                "used":{"amount_minor":2500,"currency":"USD","exponent":2},
                "limit":{"amount_minor":10000,"currency":"XAU","exponent":2},
                "balance":{"amount_minor":4000,"currency":"USD","exponent":2}}"#,
        )
        .unwrap();
        let shown: Vec<String> = account.metrics().iter().map(Metric::used_display).collect();
        assert_eq!(shown, ["$25.00 spent", "$40.00 left"]);
        assert_eq!(
            account.messages(),
            ["Usage credits are billed in XAU, which CodexBar can't show yet."]
        );
    }

    #[test]
    fn an_account_with_only_credits_is_shown() {
        // Enterprise and credit-based accounts can report money without subscription windows.
        let account = with(
            r#""five_hour":null,"seven_day":null,"spend":{"enabled":true,
                "used":{"amount_minor":2500,"currency":"USD","exponent":2},
                "limit":{"amount_minor":10000,"currency":"USD","exponent":2}}"#,
        )
        .unwrap();
        assert_eq!(account.metrics().len(), 1);
        assert_eq!(account.primary().unwrap().used_display(), "$25.00 of $100.00");
        assert!(
            with(r#""five_hour":null,"spend":{"enabled":false}"#).is_err(),
            "nothing at all is still an error"
        );
    }

    #[test]
    fn credits_near_their_limit_raise_the_account_status() {
        let account = with(&format!(
            r#"{SESSION_FLAT},"spend":{{"enabled":true,"used":{{"amount_minor":5760,"currency":"SGD","exponent":2}},
                "limit":{{"amount_minor":6000,"currency":"SGD","exponent":2}}}}"#
        ))
        .unwrap();
        assert_eq!(account.assess(now()).severity(), Severity::LimitSoon);
    }

    #[test]
    fn incomplete_windows_are_skipped_not_fatal() {
        let account = with(
            r#""five_hour":{"utilization":10,"resets_at":null},
            "seven_day":{"utilization":40,"resets_at":"2026-10-10T08:00:00Z"},
            "limits":[{"kind":"session","percent":0,"resets_at":null},
                {"kind":"weekly_scoped","percent":5,"resets_at":"2026-10-10T08:00:00Z","scope":{"model":{"display_name":" "}}}]"#,
        )
        .unwrap();
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
