//! ChatGPT / Codex subscription limits, read with the ChatGPT sign-in the Codex CLI keeps in `~/.codex/auth.json`.

use std::path::{Path, PathBuf};

use chrono::{DateTime, Duration, TimeZone, Utc};
use codexbar_core::{AccountId, AccountSnapshot, Metric, Pace, Provider};
use serde_json::Value;

use crate::{HttpClient, ProviderError, UsageProvider};

const USAGE_ENDPOINT: &str = "https://chatgpt.com/backend-api/wham/usage";
const SIGN_IN_HINT: &str = "Run `codex` and sign in with ChatGPT.";
/// Pace is unreliable early in a window: a few minutes of a weekly window extrapolate to a false lockout.
/// It is only projected once this much time, and this share of the window, has elapsed.
const MIN_ELAPSED_FOR_PACE_MINUTES: i64 = 10;
const MIN_ELAPSED_FOR_PACE_FRACTION: f64 = 0.1;

/// The parts of the Codex sign-in this provider uses.
#[derive(Clone, PartialEq, Eq)]
pub struct Credentials {
    access_token: String,
    account_id: Option<String>,
}

impl std::fmt::Debug for Credentials {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Credentials")
            .field("access_token", &"<redacted>")
            .field("account_id", &self.account_id.as_ref().map(|_| "<present>"))
            .finish()
    }
}

/// `$CODEX_HOME/auth.json`, or `~/.codex/auth.json`.
pub fn default_auth_path() -> PathBuf {
    let home = std::env::var_os("CODEX_HOME")
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("USERPROFILE").map(|profile| PathBuf::from(profile).join(".codex")))
        .unwrap_or_else(|| PathBuf::from(".codex"));
    home.join("auth.json")
}

/// Reads the access token (and account id, when present) from a Codex `auth.json`.
pub fn read_credentials(path: &Path) -> Result<Credentials, ProviderError> {
    let not_signed_in = ProviderError::NotSignedIn { hint: SIGN_IN_HINT };
    let text = std::fs::read_to_string(path).map_err(|_| not_signed_in.clone())?;
    let json: Value = serde_json::from_str(&text).map_err(|_| not_signed_in.clone())?;
    let tokens = json.get("tokens").ok_or_else(|| not_signed_in.clone())?;
    let token = |snake: &str, camel: &str| {
        tokens
            .get(snake)
            .or_else(|| tokens.get(camel))
            .and_then(Value::as_str)
            .filter(|value| !value.trim().is_empty())
            .map(str::to_owned)
    };
    let access_token = token("access_token", "accessToken").ok_or(not_signed_in)?;
    Ok(Credentials {
        access_token,
        account_id: token("account_id", "accountId"),
    })
}

/// Maps a `wham/usage` response onto one account. Windows are ordered shortest first, so the 5-hour window is primary.
pub fn parse_usage(payload: &str, now: DateTime<Utc>) -> Result<AccountSnapshot, ProviderError> {
    let json: Value = serde_json::from_str(payload).map_err(|_| ProviderError::Unexpected { detail: "not JSON" })?;
    let rate_limit = json.get("rate_limit").ok_or(ProviderError::Unexpected {
        detail: "no rate limits",
    })?;

    let mut windows: Vec<Window> = ["primary_window", "secondary_window"]
        .iter()
        .filter_map(|key| rate_limit.get(*key).and_then(Window::from_json))
        .collect();
    if windows.is_empty() {
        return Err(ProviderError::Unexpected {
            detail: "no usage windows for this account",
        });
    }
    windows.sort_by_key(|window| window.duration_secs);

    let metrics = windows.iter().map(|window| window.to_metric(now)).collect();
    let mut account = AccountSnapshot::new(AccountId::new("codex-chatgpt"), Provider::Codex, metrics, now);
    if let Some(plan) = json
        .get("plan_type")
        .and_then(Value::as_str)
        .filter(|plan| !plan.is_empty())
    {
        account = account.with_label(plan_name(plan));
    }
    Ok(account)
}

/// "pro" -> "Pro", "prolite" -> "Pro", "team_plus" -> "Team Plus".
pub fn plan_name(plan: &str) -> String {
    match plan.to_lowercase().as_str() {
        "prolite" => "Pro".to_owned(),
        other => other
            .split('_')
            .filter(|word| !word.is_empty())
            .map(|word| {
                let mut chars = word.chars();
                chars
                    .next()
                    .map(|first| first.to_uppercase().chain(chars).collect::<String>())
                    .unwrap_or_default()
            })
            .collect::<Vec<_>>()
            .join(" "),
    }
}

struct Window {
    used: f64,
    resets_at: DateTime<Utc>,
    duration_secs: i64,
}

impl Window {
    fn from_json(value: &Value) -> Option<Self> {
        let used = value.get("used_percent")?.as_f64()?;
        let resets_at = Utc.timestamp_opt(value.get("reset_at")?.as_i64()?, 0).single()?;
        let duration_secs = value.get("limit_window_seconds")?.as_i64().filter(|secs| *secs > 0)?;
        Some(Self {
            used: (used / 100.0).clamp(0.0, 1.0),
            resets_at,
            duration_secs,
        })
    }

    fn label(&self) -> String {
        match self.duration_secs {
            18_000 => "5-hour window".to_owned(),
            604_800 => "Weekly".to_owned(),
            secs => format!("{}-hour window", (secs / 3600).max(1)),
        }
    }

    /// Usage so far divided by time elapsed in the window.
    fn pace(&self, now: DateTime<Utc>) -> Option<Pace> {
        let started = self.resets_at - Duration::seconds(self.duration_secs);
        let elapsed = now - started;
        let min_elapsed = Duration::minutes(MIN_ELAPSED_FOR_PACE_MINUTES).max(Duration::seconds(
            (self.duration_secs as f64 * MIN_ELAPSED_FOR_PACE_FRACTION) as i64,
        ));
        if elapsed < min_elapsed || now >= self.resets_at {
            return None;
        }
        Some(Pace::per_hour(self.used / (elapsed.num_seconds() as f64 / 3600.0)))
    }

    fn to_metric(&self, now: DateTime<Utc>) -> Metric {
        Metric::Window {
            label: self.label(),
            used: self.used,
            resets_at: self.resets_at,
            pace: self.pace(now),
        }
    }
}

/// The Codex provider, generic over HTTP so tests never touch the network.
pub struct CodexProvider<H: HttpClient> {
    http: H,
    auth_path: PathBuf,
}

impl<H: HttpClient> CodexProvider<H> {
    pub fn new(http: H, auth_path: PathBuf) -> Self {
        Self { http, auth_path }
    }
}

impl<H: HttpClient> UsageProvider for CodexProvider<H> {
    fn name(&self) -> &'static str {
        "ChatGPT · Codex"
    }

    fn fetch(&self, now: DateTime<Utc>) -> Result<Vec<AccountSnapshot>, ProviderError> {
        let credentials = read_credentials(&self.auth_path)?;
        let bearer = format!("Bearer {}", credentials.access_token);
        let mut headers = vec![("Authorization", bearer.as_str()), ("Accept", "application/json")];
        if let Some(account_id) = credentials.account_id.as_deref() {
            headers.push(("ChatGPT-Account-Id", account_id));
        }
        let response = self.http.get(USAGE_ENDPOINT, &headers)?;
        match response.status {
            200..=299 => parse_usage(&response.body, now).map(|account| vec![account]),
            401 | 403 => Err(ProviderError::Expired { hint: SIGN_IN_HINT }),
            status => Err(ProviderError::Http { status }),
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Mutex;

    use super::*;
    use crate::HttpResponse;
    use codexbar_core::Severity;

    fn now() -> DateTime<Utc> {
        "2026-10-07T02:00:00Z".parse().unwrap()
    }

    const FIXTURE: &str = include_str!("../tests/fixtures/codex-usage.json");

    struct FakeHttp {
        response: Result<HttpResponse, ProviderError>,
        seen_headers: Mutex<Vec<(String, String)>>,
    }

    impl FakeHttp {
        fn status(status: u16, body: &str) -> Self {
            Self {
                response: Ok(HttpResponse {
                    status,
                    body: body.into(),
                }),
                seen_headers: Mutex::default(),
            }
        }
    }

    impl HttpClient for FakeHttp {
        fn get(&self, _: &str, headers: &[(&str, &str)]) -> Result<HttpResponse, ProviderError> {
            *self.seen_headers.lock().unwrap() = headers.iter().map(|(k, v)| ((*k).into(), (*v).into())).collect();
            self.response.clone()
        }
    }

    fn auth_file(contents: &str) -> tempfile_lite::TempFile {
        tempfile_lite::TempFile::with_contents(contents)
    }

    #[test]
    fn parse_usage_fixture_orders_windows_and_projects_pace() {
        let account = parse_usage(FIXTURE, now()).unwrap();
        assert_eq!(account.display_name(), "ChatGPT · Codex (Pro)");
        let labels: Vec<&str> = account.metrics().iter().map(Metric::label).collect();
        assert_eq!(labels, ["5-hour window", "Weekly"]);
        assert_eq!(account.primary().unwrap().used_fraction(), Some(0.91));
        // 91% used, 4h 22m into the window: about 21% per hour, so the limit lands in ~26 minutes, before the reset.
        assert_eq!(account.assess(now()).severity(), Severity::LimitSoon);
    }

    #[test]
    fn parse_usage_bad_window_is_skipped() {
        let payload = r#"{"rate_limit":{"primary_window":{"used_percent":"bad"},
            "secondary_window":{"used_percent":22,"reset_at":1893456000,"limit_window_seconds":604800}}}"#;
        let account = parse_usage(payload, now()).unwrap();
        assert_eq!(account.metrics().len(), 1);
        assert_eq!(account.primary().unwrap().label(), "Weekly");
    }

    #[test]
    fn parse_usage_no_windows_returns_error() {
        let payload = r#"{"rate_limit":{"primary_window":null,"secondary_window":null}}"#;
        assert!(matches!(
            parse_usage(payload, now()),
            Err(ProviderError::Unexpected { .. })
        ));
    }

    #[test]
    fn parse_usage_over_limit_and_custom_duration_clamps_and_labels() {
        let payload = r#"{"rate_limit":{"primary_window":{"used_percent":150,"reset_at":1893456000,"limit_window_seconds":32400}}}"#;
        let account = parse_usage(payload, now()).unwrap();
        assert_eq!(account.primary().unwrap().label(), "9-hour window");
        assert_eq!(account.primary().unwrap().used_fraction(), Some(1.0));
    }

    #[test]
    fn parse_usage_window_just_started_has_no_pace() {
        let resets_at = (now() + Duration::hours(5) - Duration::minutes(2)).timestamp();
        let payload = format!(
            r#"{{"rate_limit":{{"primary_window":{{"used_percent":5,"reset_at":{resets_at},"limit_window_seconds":18000}}}}}}"#
        );
        let account = parse_usage(&payload, now()).unwrap();
        assert_eq!(account.primary().unwrap().time_to_limit(), None);
    }

    #[test]
    fn parse_usage_weekly_window_early_has_no_pace_and_stays_normal() {
        // 2% used 20 minutes into a weekly window must not project a lockout.
        let resets_at = (now() + Duration::days(7) - Duration::minutes(20)).timestamp();
        let payload = format!(
            r#"{{"rate_limit":{{"primary_window":{{"used_percent":2,"reset_at":{resets_at},"limit_window_seconds":604800}}}}}}"#
        );
        let account = parse_usage(&payload, now()).unwrap();
        assert_eq!(account.primary().unwrap().time_to_limit(), None);
        assert_eq!(account.assess(now()).severity(), Severity::Normal);
    }

    #[test]
    fn plan_name_known_and_unknown_formats() {
        assert_eq!(plan_name("prolite"), "Pro");
        assert_eq!(plan_name("plus"), "Plus");
        assert_eq!(plan_name("team_plus"), "Team Plus");
    }

    #[test]
    fn read_credentials_snake_and_camel_case_tokens() {
        let snake = auth_file(r#"{"tokens":{"access_token":"tok","account_id":"acct"}}"#);
        let creds = read_credentials(snake.path()).unwrap();
        assert_eq!(creds.access_token, "tok");
        assert_eq!(creds.account_id.as_deref(), Some("acct"));

        let camel = auth_file(r#"{"tokens":{"accessToken":"tok2"}}"#);
        assert_eq!(read_credentials(camel.path()).unwrap().account_id, None);
    }

    #[test]
    fn read_credentials_missing_or_blank_returns_not_signed_in() {
        assert!(matches!(
            read_credentials(Path::new("Z:/definitely/missing/auth.json")),
            Err(ProviderError::NotSignedIn { .. })
        ));
        let blank = auth_file(r#"{"tokens":{"access_token":"  "}}"#);
        assert!(matches!(
            read_credentials(blank.path()),
            Err(ProviderError::NotSignedIn { .. })
        ));
    }

    #[test]
    fn credentials_debug_redacts_token() {
        let file = auth_file(r#"{"tokens":{"access_token":"secret-token","account_id":"acct"}}"#);
        let debug = format!("{:?}", read_credentials(file.path()).unwrap());
        assert!(!debug.contains("secret-token"));
        assert!(!debug.contains("acct"));
    }

    #[test]
    fn fetch_success_sends_bearer_and_account_headers() {
        let file = auth_file(r#"{"tokens":{"access_token":"tok","account_id":"acct"}}"#);
        let provider = CodexProvider::new(FakeHttp::status(200, FIXTURE), file.path().to_path_buf());
        let accounts = provider.fetch(now()).unwrap();
        assert_eq!(accounts.len(), 1);
        let headers = provider.http.seen_headers.lock().unwrap().clone();
        assert!(headers.contains(&("Authorization".into(), "Bearer tok".into())));
        assert!(headers.contains(&("ChatGPT-Account-Id".into(), "acct".into())));
    }

    #[test]
    fn fetch_unauthorized_returns_expired() {
        let file = auth_file(r#"{"tokens":{"access_token":"tok"}}"#);
        let provider = CodexProvider::new(FakeHttp::status(401, ""), file.path().to_path_buf());
        assert!(matches!(provider.fetch(now()), Err(ProviderError::Expired { .. })));
    }

    #[test]
    fn fetch_server_error_returns_status() {
        let file = auth_file(r#"{"tokens":{"access_token":"tok"}}"#);
        let provider = CodexProvider::new(FakeHttp::status(503, ""), file.path().to_path_buf());
        assert_eq!(provider.fetch(now()), Err(ProviderError::Http { status: 503 }));
    }

    #[test]
    fn provider_error_display_never_includes_token() {
        let message = ProviderError::Expired { hint: SIGN_IN_HINT }.to_string();
        assert_eq!(message, "Sign-in expired. Run `codex` and sign in with ChatGPT.");
    }

    /// A minimal self-deleting temp file, to avoid a dev-dependency for three tests.
    mod tempfile_lite {
        use std::path::{Path, PathBuf};
        use std::sync::atomic::{AtomicU32, Ordering};

        static COUNTER: AtomicU32 = AtomicU32::new(0);

        pub struct TempFile(PathBuf);

        impl TempFile {
            pub fn with_contents(contents: &str) -> Self {
                let n = COUNTER.fetch_add(1, Ordering::Relaxed);
                let path = std::env::temp_dir().join(format!("codexbar-test-{}-{n}.json", std::process::id()));
                std::fs::write(&path, contents).unwrap();
                Self(path)
            }

            pub fn path(&self) -> &Path {
                &self.0
            }
        }

        impl Drop for TempFile {
            fn drop(&mut self) {
                let _ = std::fs::remove_file(&self.0);
            }
        }
    }
}
