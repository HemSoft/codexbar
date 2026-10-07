//! GitHub Copilot premium-request quotas for every account signed in to the GitHub CLI.

use std::time::Duration as StdDuration;

use chrono::{DateTime, Months, NaiveDate, Utc};
use codexbar_core::{AccountId, AccountSnapshot, Metric, Provider};
use serde_json::Value;

use crate::pace::elapsed_pace;
use crate::{CommandError, CommandRunner, HttpClient, ProviderError, UsageProvider};

const USER_ENDPOINT: &str = "https://api.github.com/copilot_internal/user";
const HOST: &str = "github.com";
const GH_TIMEOUT: StdDuration = StdDuration::from_secs(10);
const GH_MISSING: &str = "Install the GitHub CLI and run `gh auth login`.";
const GH_LOGIN: &str = "Run `gh auth login` for this account.";

/// Usernames from `gh auth status` output ("✓ Logged in to github.com account HemSoft (keyring)").
pub fn parse_gh_accounts(status: &str) -> Vec<String> {
    let mut accounts = Vec::new();
    for line in status.lines() {
        let line = line.trim();
        if !line.to_lowercase().contains("logged in to github.com") {
            continue;
        }
        let name = ["account ", " as "].iter().find_map(|marker| {
            let start = line.to_lowercase().find(marker)? + marker.len();
            line[start..].split_whitespace().next()
        });
        if let Some(name) = name.filter(|name| !name.is_empty())
            && !accounts.iter().any(|known: &String| known.eq_ignore_ascii_case(name))
        {
            accounts.push(name.to_owned());
        }
    }
    accounts
}

/// Maps a `copilot_internal/user` response to one account. Unlimited and absent quotas are not limits, so they
/// produce no metric rather than a false 0%.
pub fn parse_user(payload: &str, username: &str, now: DateTime<Utc>) -> Result<AccountSnapshot, ProviderError> {
    let json: Value = serde_json::from_str(payload).map_err(|_| ProviderError::Unexpected { detail: "not JSON" })?;
    let resets_at = reset_date(&json);
    let mut metrics = Vec::new();
    if let Some(resets_at) = resets_at {
        for (key, label) in QUOTAS {
            if let Some(metric) = json
                .pointer(&format!("/quota_snapshots/{key}"))
                .and_then(|quota| quota_metric(quota, label, resets_at, now))
            {
                metrics.push(metric);
            }
        }
    }

    Ok(AccountSnapshot::new(
        AccountId::new(format!("copilot-{}", username.to_lowercase())),
        Provider::Copilot,
        metrics,
        now,
    )
    .with_label(username))
}

/// Quotas in display order. Paid plans meter premium requests; Copilot Free meters chat and completions.
const QUOTAS: [(&str, &str); 3] = [
    ("premium_interactions", "Premium requests"),
    ("chat", "Chat messages"),
    ("completions", "Code completions"),
];

/// A monthly quota with a real ceiling. Unlimited or zero entitlements are not limits.
fn quota_metric(quota: &Value, label: &str, resets_at: DateTime<Utc>, now: DateTime<Utc>) -> Option<Metric> {
    let entitlement = quota.get("entitlement").and_then(Value::as_u64).filter(|e| *e > 0)?;
    if quota.get("unlimited").and_then(Value::as_bool).unwrap_or(false) {
        return None;
    }
    let remaining = quota.get("remaining").and_then(Value::as_i64).unwrap_or(0).max(0) as u64;
    let used = entitlement.saturating_sub(remaining);
    let start = resets_at.checked_sub_months(Months::new(1)).unwrap_or(resets_at);
    let pace = elapsed_pace(used as f64 / entitlement as f64, start, resets_at, now);
    Some(Metric::Quota {
        label: label.into(),
        used,
        limit: entitlement,
        resets_at,
        pace,
    })
}

/// `quota_reset_date_utc` (RFC 3339), falling back to the date-only `quota_reset_date` at midnight UTC.
fn reset_date(json: &Value) -> Option<DateTime<Utc>> {
    if let Some(stamp) = json.get("quota_reset_date_utc").and_then(Value::as_str)
        && let Ok(at) = DateTime::parse_from_rfc3339(stamp)
    {
        return Some(at.with_timezone(&Utc));
    }
    let date = json.get("quota_reset_date").and_then(Value::as_str)?;
    let date = NaiveDate::parse_from_str(date, "%Y-%m-%d").ok()?;
    Some(date.and_hms_opt(0, 0, 0)?.and_utc())
}

pub struct CopilotProvider<H: HttpClient, C: CommandRunner> {
    http: H,
    commands: C,
    /// When set, only these gh usernames are fetched (case-insensitive); otherwise every signed-in account.
    only: Option<Vec<String>>,
}

impl<H: HttpClient, C: CommandRunner> CopilotProvider<H, C> {
    pub fn new(http: H, commands: C) -> Self {
        Self {
            http,
            commands,
            only: None,
        }
    }

    pub fn only_accounts(mut self, usernames: Vec<String>) -> Self {
        self.only = Some(usernames);
        self
    }

    fn gh(&self, args: &[&str]) -> Result<String, ProviderError> {
        match self.commands.run("gh", args, GH_TIMEOUT) {
            Ok(output) if output.success => Ok(format!("{}\n{}", output.stdout, output.stderr)),
            Ok(_) => Err(ProviderError::NotSignedIn { hint: GH_LOGIN }),
            Err(CommandError::NotFound) => Err(ProviderError::NotSignedIn { hint: GH_MISSING }),
            Err(CommandError::TimedOut | CommandError::Failed) => Err(ProviderError::Network),
        }
    }

    fn token(&self, username: &str) -> Result<String, ProviderError> {
        let output = self.commands.run(
            "gh",
            &["auth", "token", "--user", username, "--hostname", HOST],
            GH_TIMEOUT,
        );
        match output {
            Ok(output) if output.success => {
                let token = output.stdout.trim();
                if token.is_empty() {
                    Err(ProviderError::NotSignedIn { hint: GH_LOGIN })
                } else {
                    Ok(token.to_owned())
                }
            }
            Ok(_) => Err(ProviderError::NotSignedIn { hint: GH_LOGIN }),
            Err(CommandError::NotFound) => Err(ProviderError::NotSignedIn { hint: GH_MISSING }),
            Err(_) => Err(ProviderError::Network),
        }
    }

    fn fetch_account(&self, username: &str, now: DateTime<Utc>) -> Result<AccountSnapshot, ProviderError> {
        let token = self.token(username)?;
        let authorization = format!("token {token}");
        let headers = [
            ("Authorization", authorization.as_str()),
            ("Accept", "application/json"),
            ("Editor-Version", "vscode/1.96.2"),
            ("Editor-Plugin-Version", "copilot-chat/0.26.7"),
            ("X-Github-Api-Version", "2025-04-01"),
        ];
        let response = self.http.get(USER_ENDPOINT, &headers)?;
        match response.status {
            200..=299 => parse_user(&response.body, username, now),
            401 | 403 => Err(ProviderError::Expired { hint: GH_LOGIN }),
            // 404: this account has no Copilot seat.
            404 => Err(ProviderError::Unexpected {
                detail: "no Copilot subscription on this account",
            }),
            status => Err(ProviderError::Http { status }),
        }
    }
}

impl<H: HttpClient, C: CommandRunner> UsageProvider for CopilotProvider<H, C> {
    fn name(&self) -> &'static str {
        "Copilot"
    }

    /// Every signed-in account. One failing account doesn't hide the others; the call fails only if all do.
    fn fetch(&self, now: DateTime<Utc>) -> Result<Vec<AccountSnapshot>, ProviderError> {
        let mut accounts = parse_gh_accounts(&self.gh(&["auth", "status", "--hostname", HOST])?);
        if let Some(only) = &self.only {
            accounts.retain(|name| only.iter().any(|wanted| wanted.eq_ignore_ascii_case(name)));
        }
        if accounts.is_empty() {
            return Err(ProviderError::NotSignedIn { hint: GH_MISSING });
        }
        let mut first_error = None;
        let mut snapshots = Vec::new();
        for username in &accounts {
            match self.fetch_account(username, now) {
                Ok(snapshot) => snapshots.push(snapshot),
                Err(err) => {
                    first_error.get_or_insert(err);
                }
            }
        }
        match (snapshots.is_empty(), first_error) {
            (true, Some(err)) => Err(err),
            _ => Ok(snapshots),
        }
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::sync::Mutex;

    use super::*;
    use crate::{CommandOutput, HttpResponse};
    use codexbar_core::Severity;

    fn now() -> DateTime<Utc> {
        "2026-10-07T02:00:00Z".parse().unwrap()
    }

    const STATUS: &str = "github.com\n  \u{2713} Logged in to github.com account HemSoft (keyring)\n  - Active account: true\n  - Token: gho_************\n\n  \u{2713} Logged in to github.com account fhemmerrelias (keyring)\n  - Active account: false\n";
    const USER: &str = include_str!("../tests/fixtures/copilot-user.json");

    struct FakeGh {
        tokens: HashMap<&'static str, &'static str>,
    }

    impl CommandRunner for FakeGh {
        fn run(&self, _: &str, args: &[&str], _: StdDuration) -> Result<CommandOutput, CommandError> {
            let ok = |stdout: &str| {
                Ok(CommandOutput {
                    success: true,
                    stdout: stdout.into(),
                    stderr: String::new(),
                })
            };
            match args {
                ["auth", "status", ..] => ok(STATUS),
                ["auth", "token", "--user", user, ..] => match self.tokens.get(user) {
                    Some(token) => ok(&format!("{token}\n")),
                    None => Ok(CommandOutput {
                        success: false,
                        stdout: String::new(),
                        stderr: "no token".into(),
                    }),
                },
                _ => Err(CommandError::Failed),
            }
        }
    }

    struct FakeHttp {
        by_token: HashMap<String, HttpResponse>,
        seen: Mutex<Vec<String>>,
    }

    impl HttpClient for FakeHttp {
        fn get(&self, _: &str, headers: &[(&str, &str)]) -> Result<HttpResponse, ProviderError> {
            let auth = headers
                .iter()
                .find(|(name, _)| *name == "Authorization")
                .map(|(_, v)| v.to_string())
                .unwrap();
            self.seen.lock().unwrap().push(auth.clone());
            self.by_token.get(&auth).cloned().ok_or(ProviderError::Network)
        }
    }

    fn ok(body: &str) -> HttpResponse {
        HttpResponse::new(200, body)
    }

    #[test]
    fn parse_gh_accounts_reads_every_logged_in_account_once() {
        let doubled = format!("{STATUS}{STATUS}");
        assert_eq!(parse_gh_accounts(&doubled), ["HemSoft", "fhemmerrelias"]);
        assert_eq!(
            parse_gh_accounts("  ✓ Logged in to github.com as octocat (oauth_token)"),
            ["octocat"]
        );
        assert!(parse_gh_accounts("You are not logged into any GitHub hosts.").is_empty());
    }

    #[test]
    fn parse_user_premium_quota_maps_used_limit_and_reset() {
        let account = parse_user(USER, "HemSoft", now()).unwrap();
        assert_eq!(account.display_name(), "Copilot · HemSoft");
        let Some(Metric::Quota {
            used, limit, resets_at, ..
        }) = account.primary()
        else {
            panic!("quota")
        };
        assert_eq!((*used, *limit), (1284, 1500));
        assert_eq!(*resets_at, "2026-11-01T00:00:00Z".parse::<DateTime<Utc>>().unwrap());
        // 86% used six days into the month projects exhaustion well before Nov 1.
        assert_eq!(account.assess(now()).severity(), Severity::AtRisk);
    }

    #[test]
    fn parse_user_unlimited_quota_has_no_metric() {
        let payload = r#"{"quota_reset_date":"2026-11-01","quota_snapshots":{"premium_interactions":{"entitlement":0,"remaining":0,"unlimited":true}}}"#;
        assert!(parse_user(payload, "x", now()).unwrap().metrics().is_empty());
    }

    #[test]
    fn parse_user_free_plan_reports_chat_and_completions() {
        let payload = r#"{"copilot_plan":"individual","quota_reset_date":"2026-11-01","quota_snapshots":{
            "chat":{"entitlement":200,"remaining":199,"unlimited":false},
            "completions":{"entitlement":2000,"remaining":2000,"unlimited":false},
            "premium_interactions":{"entitlement":0,"remaining":0,"unlimited":false}}}"#;
        let account = parse_user(payload, "HemSoft", now()).unwrap();
        let labels: Vec<&str> = account.metrics().iter().map(Metric::label).collect();
        assert_eq!(labels, ["Chat messages", "Code completions"]);
        assert_eq!(account.primary().unwrap().used_display(), "1");
    }

    #[test]
    fn parse_user_date_only_reset_falls_back_to_midnight_utc() {
        let payload = r#"{"quota_reset_date":"2026-11-01","quota_snapshots":{"premium_interactions":{"entitlement":300,"remaining":290}}}"#;
        let account = parse_user(payload, "x", now()).unwrap();
        assert_eq!(
            account.primary().unwrap().resets_at(),
            Some("2026-11-01T00:00:00Z".parse().unwrap())
        );
    }

    #[test]
    fn fetch_all_accounts_uses_each_accounts_token() {
        let gh = FakeGh {
            tokens: HashMap::from([("HemSoft", "tok-a"), ("fhemmerrelias", "tok-b")]),
        };
        let http = FakeHttp {
            by_token: HashMap::from([("token tok-a".into(), ok(USER)), ("token tok-b".into(), ok(USER))]),
            seen: Mutex::default(),
        };
        let provider = CopilotProvider::new(http, gh);
        let accounts = provider.fetch(now()).unwrap();
        let ids: Vec<&str> = accounts.iter().map(|a| a.id().as_str()).collect();
        assert_eq!(ids, ["copilot-hemsoft", "copilot-fhemmerrelias"]);
        assert_eq!(*provider.http.seen.lock().unwrap(), ["token tok-a", "token tok-b"]);
    }

    #[test]
    fn fetch_only_selected_accounts() {
        let gh = FakeGh {
            tokens: HashMap::from([("HemSoft", "tok-a"), ("fhemmerrelias", "tok-b")]),
        };
        let http = FakeHttp {
            by_token: HashMap::from([("token tok-a".into(), ok(USER))]),
            seen: Mutex::default(),
        };
        let provider = CopilotProvider::new(http, gh).only_accounts(vec!["hemsoft".into()]);
        let accounts = provider.fetch(now()).unwrap();
        assert_eq!(accounts.len(), 1);
        assert_eq!(*provider.http.seen.lock().unwrap(), ["token tok-a"]);
    }

    #[test]
    fn fetch_one_account_failing_keeps_the_other() {
        let gh = FakeGh {
            tokens: HashMap::from([("HemSoft", "tok-a")]),
        };
        let http = FakeHttp {
            by_token: HashMap::from([("token tok-a".into(), ok(USER))]),
            seen: Mutex::default(),
        };
        let accounts = CopilotProvider::new(http, gh).fetch(now()).unwrap();
        assert_eq!(accounts.len(), 1);
    }

    #[test]
    fn fetch_gh_missing_returns_install_hint() {
        struct NoGh;
        impl CommandRunner for NoGh {
            fn run(&self, _: &str, _: &[&str], _: StdDuration) -> Result<CommandOutput, CommandError> {
                Err(CommandError::NotFound)
            }
        }
        let http = FakeHttp {
            by_token: HashMap::new(),
            seen: Mutex::default(),
        };
        let err = CopilotProvider::new(http, NoGh).fetch(now()).unwrap_err();
        assert_eq!(err, ProviderError::NotSignedIn { hint: GH_MISSING });
    }

    #[test]
    fn fetch_unauthorized_for_every_account_returns_expired() {
        let gh = FakeGh {
            tokens: HashMap::from([("HemSoft", "tok-a"), ("fhemmerrelias", "tok-b")]),
        };
        let denied = HttpResponse::new(401, String::new());
        let http = FakeHttp {
            by_token: HashMap::from([("token tok-a".into(), denied.clone()), ("token tok-b".into(), denied)]),
            seen: Mutex::default(),
        };
        assert!(matches!(
            CopilotProvider::new(http, gh).fetch(now()),
            Err(ProviderError::Expired { .. })
        ));
    }
}
