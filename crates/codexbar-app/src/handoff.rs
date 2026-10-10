//! Opening CodexBar on an account from a widget tile (#95): `codexbar --focus <account> [--metric <key>]`.
//!
//! Only one CodexBar runs per user, so a second launch hands its request to the running one: it writes
//! `focus-request.json` into the settings folder and signals a named event, and the running CodexBar shows its
//! window focused on that account. A first launch applies the request itself once its window is open.

use std::path::Path;

pub const FOCUS_ARG: &str = "--focus";
pub const METRIC_ARG: &str = "--metric";
const REQUEST_FILE: &str = "focus-request.json";

/// The account, and optionally its metric, to show.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FocusRequest {
    pub account: String,
    pub metric: Option<String>,
}

impl FocusRequest {
    /// The request in a command line, if it has one.
    pub fn from_args(args: impl IntoIterator<Item = String>) -> Option<Self> {
        let args: Vec<String> = args.into_iter().collect();
        let value = |name: &str| {
            args.iter()
                .position(|arg| arg == name)
                .and_then(|ix| args.get(ix + 1))
                .filter(|value| !value.is_empty() && !value.starts_with("--"))
                .cloned()
        };
        Some(Self {
            account: value(FOCUS_ARG)?,
            metric: value(METRIC_ARG),
        })
    }

    fn to_json(&self) -> String {
        serde_json::json!({ "account": self.account, "metric": self.metric }).to_string()
    }

    fn from_json(text: &str) -> Option<Self> {
        let value: serde_json::Value = serde_json::from_str(text).ok()?;
        Some(Self {
            account: value.get("account")?.as_str().filter(|a| !a.is_empty())?.to_owned(),
            metric: value
                .get("metric")
                .and_then(serde_json::Value::as_str)
                .map(str::to_owned),
        })
    }
}

/// Leaves `request` for the running CodexBar.
pub fn write_request(dir: &Path, request: &FocusRequest) -> std::io::Result<()> {
    std::fs::create_dir_all(dir)?;
    let path = dir.join(REQUEST_FILE);
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, request.to_json())?;
    std::fs::rename(&tmp, path)
}

/// Takes the waiting request, if there is one, so it is acted on once.
pub fn take_request(dir: &Path) -> Option<FocusRequest> {
    let path = dir.join(REQUEST_FILE);
    let text = std::fs::read_to_string(&path).ok()?;
    let _ = std::fs::remove_file(&path);
    FocusRequest::from_json(&text)
}

/// The named event a second launch signals, per user like the single-instance lock (`main::already_running`).
fn event_name(user: &str) -> windows::core::HSTRING {
    windows::core::HSTRING::from(format!(r"Global\HemSoft.CodexBar.Focus.{user}"))
}

/// The event the running CodexBar waits on. The handle lives as long as the process.
pub fn listen(user: &str) -> Option<windows::Win32::Foundation::HANDLE> {
    use windows::Win32::System::Threading::CreateEventW;
    // SAFETY: an auto-reset, unsignaled event with default security and a valid name.
    unsafe { CreateEventW(None, false, false, &event_name(user)) }.ok()
}

/// Whether a second launch signaled since the last check.
pub fn signaled(event: windows::Win32::Foundation::HANDLE) -> bool {
    use windows::Win32::Foundation::WAIT_OBJECT_0;
    use windows::Win32::System::Threading::WaitForSingleObject;
    // SAFETY: a valid event handle; a zero timeout only polls.
    unsafe { WaitForSingleObject(event, 0) == WAIT_OBJECT_0 }
}

/// Wakes the running CodexBar.
pub fn signal(user: &str) {
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::Threading::{CreateEventW, SetEvent};
    // SAFETY: opens (or creates) the same named event, signals it and closes this process's handle.
    unsafe {
        if let Ok(event) = CreateEventW(None, false, false, &event_name(user)) {
            let _ = SetEvent(event);
            let _ = CloseHandle(event);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(list: &[&str]) -> Vec<String> {
        std::iter::once("codexbar")
            .chain(list.iter().copied())
            .map(str::to_owned)
            .collect()
    }

    #[test]
    fn the_command_line_names_the_account_and_metric() {
        assert_eq!(
            FocusRequest::from_args(args(&["--focus", "claude-1", "--metric", "weekly"])),
            Some(FocusRequest {
                account: "claude-1".into(),
                metric: Some("weekly".into())
            })
        );
        assert_eq!(
            FocusRequest::from_args(args(&["--focus", "or-2"])),
            Some(FocusRequest {
                account: "or-2".into(),
                metric: None
            })
        );
        assert_eq!(FocusRequest::from_args(args(&["--settings"])), None);
        assert_eq!(FocusRequest::from_args(args(&["--focus"])), None, "no account");
        assert_eq!(FocusRequest::from_args(args(&["--focus", "--metric", "weekly"])), None);
    }

    #[test]
    fn a_request_is_taken_once() {
        let dir = std::env::temp_dir().join(format!("codexbar-handoff-{}", std::process::id()));
        let request = FocusRequest {
            account: "codex-1".into(),
            metric: Some("weekly".into()),
        };
        write_request(&dir, &request).unwrap();
        assert_eq!(take_request(&dir), Some(request));
        assert_eq!(take_request(&dir), None);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_signal_reaches_the_listener_once() {
        let user = format!("test-{}", std::process::id());
        let event = listen(&user).expect("event");
        assert!(!signaled(event));
        signal(&user);
        assert!(signaled(event));
        assert!(!signaled(event), "auto-reset");
    }
}
