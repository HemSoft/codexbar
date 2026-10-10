//! Signing Cursor accounts in and out through Cursor's own CLI, `cursor-agent` (#81), each in a folder of its own.
//!
//! `cursor-agent` keeps its sign-in in `%APPDATA%\Cursor\auth.json`, so pointing `APPDATA` (and `CURSOR_CONFIG_DIR`,
//! for its settings) at an account's folder keeps that account's sign-in there, apart from the Cursor app's and from
//! every other account's. With `NO_OPEN_BROWSER` set, `cursor-agent login` prints the sign-in page as plain text and
//! waits for the browser; CodexBar opens the page. CodexBar holds no Cursor client id and never writes `auth.json`.
//! Every process runs in a kill-on-close job.

use std::io::{BufRead as _, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, RecvTimeoutError, channel};
use std::time::{Duration, Instant};

use serde_json::Value;

const START_TIMEOUT: Duration = Duration::from_secs(60);
const SHORT_TIMEOUT: Duration = Duration::from_secs(30);
const POLL: Duration = Duration::from_millis(200);
/// What `cursor-agent login` prints before the sign-in page.
const LINK_MARKER: &str = "navigate to this link:";

/// Why a `cursor-agent` command failed. Never carries tokens or the CLI's output.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CursorCliError {
    NotInstalled,
    Failed,
    TimedOut,
    Cancelled,
}

impl std::fmt::Display for CursorCliError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::NotInstalled => "The Cursor CLI (cursor-agent) isn't installed.",
            Self::Failed => "The Cursor CLI didn't complete the sign-in.",
            Self::TimedOut => "The sign-in took too long and was stopped.",
            Self::Cancelled => "Sign-in was cancelled.",
        })
    }
}

impl std::error::Error for CursorCliError {}

/// The sign-in file of an account's folder.
pub fn auth_path(home: &Path) -> PathBuf {
    home.join("Cursor").join("auth.json")
}

/// The sign-in page in a line of `cursor-agent login` output.
fn sign_in_link(line: &str) -> Option<String> {
    let start = line.find(LINK_MARKER)? + LINK_MARKER.len();
    line[start..]
        .split_whitespace()
        .next()
        .filter(|url| url.starts_with("https://"))
        .map(str::to_owned)
}

/// A `cursor-agent login` waiting for the browser.
pub struct PendingLogin {
    process: crate::contained::Contained,
    url: String,
    auth: PathBuf,
}

impl PendingLogin {
    /// Starts `cursor-agent login` for `home` and waits for the sign-in page.
    pub fn start(home: &Path) -> Result<Self, CursorCliError> {
        let mut command = cursor_agent(home)?;
        command
            .arg("login")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        let mut process = crate::contained::spawn(&mut command).map_err(|err| match err.kind() {
            std::io::ErrorKind::NotFound => CursorCliError::NotInstalled,
            _ => CursorCliError::Failed,
        })?;
        let (sender, lines) = channel();
        for stream in [
            process
                .child
                .stdout
                .take()
                .map(|s| Box::new(s) as Box<dyn std::io::Read + Send>),
            process
                .child
                .stderr
                .take()
                .map(|s| Box::new(s) as Box<dyn std::io::Read + Send>),
        ]
        .into_iter()
        .flatten()
        {
            let sender = sender.clone();
            std::thread::spawn(move || {
                for line in BufReader::new(stream).lines() {
                    let Ok(line) = line else { break };
                    if sender.send(line).is_err() {
                        break;
                    }
                }
            });
        }
        drop(sender);
        let url = wait_for_link(&lines, START_TIMEOUT);
        let mut pending = Self {
            process,
            url: String::new(),
            auth: auth_path(home),
        };
        match url {
            Ok(url) => {
                pending.url = url;
                Ok(pending)
            }
            Err(err) => {
                pending.stop();
                Err(err)
            }
        }
    }

    pub fn url(&self) -> &str {
        &self.url
    }

    /// Waits until `cursor-agent` exits after the browser sign-in, and checks it succeeded and left a sign-in.
    /// Cancellation or the timeout stops it.
    pub fn finish(mut self, timeout: Duration, cancel: &AtomicBool) -> Result<(), CursorCliError> {
        let deadline = Instant::now() + timeout;
        loop {
            if cancel.load(Ordering::SeqCst) {
                self.stop();
                return Err(CursorCliError::Cancelled);
            }
            match self.process.child.try_wait() {
                Ok(Some(status)) if status.success() => break,
                Ok(Some(_)) | Err(_) => return Err(CursorCliError::Failed),
                Ok(None) if Instant::now() >= deadline => {
                    self.stop();
                    return Err(CursorCliError::TimedOut);
                }
                Ok(None) => std::thread::sleep(POLL),
            }
        }
        crate::cursor::read_access_token(&self.auth)
            .map(|_| ())
            .map_err(|_| CursorCliError::Failed)
    }

    fn stop(&mut self) {
        let _ = self.process.child.kill();
        let _ = self.process.child.wait();
    }
}

fn wait_for_link(lines: &Receiver<String>, timeout: Duration) -> Result<String, CursorCliError> {
    let deadline = Instant::now() + timeout;
    loop {
        match lines.recv_timeout(deadline.saturating_duration_since(Instant::now())) {
            Ok(line) => {
                if let Some(url) = sign_in_link(&line) {
                    return Ok(url);
                }
            }
            Err(RecvTimeoutError::Timeout) => return Err(CursorCliError::TimedOut),
            Err(RecvTimeoutError::Disconnected) => return Err(CursorCliError::Failed),
        }
    }
}

/// Signs the folder out; `cursor-agent` deletes its sign-in.
pub fn sign_out(home: &Path) -> Result<(), CursorCliError> {
    run(home, &["logout"]).map(|_| ())
}

/// The signed-in email, from `cursor-agent status`.
pub fn email(home: &Path) -> Option<String> {
    let output = run(home, &["status", "--format", "json"]).ok()?;
    let text = String::from_utf8(output).ok()?;
    let json: Value = serde_json::from_str(&text[text.find('{')?..]).ok()?;
    json.pointer("/userInfo/email")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|email| !email.is_empty())
        .map(str::to_owned)
}

fn run(home: &Path, args: &[&str]) -> Result<Vec<u8>, CursorCliError> {
    use std::io::Read as _;
    let mut command = cursor_agent(home)?;
    command
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    let mut process = crate::contained::spawn(&mut command).map_err(|_| CursorCliError::Failed)?;
    let mut stdout = process.child.stdout.take().ok_or(CursorCliError::Failed)?;
    let reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).map(|_| bytes)
    });
    let deadline = Instant::now() + SHORT_TIMEOUT;
    loop {
        match process.child.try_wait() {
            Ok(Some(status)) if status.success() => break,
            Ok(Some(_)) | Err(_) => return Err(CursorCliError::Failed),
            Ok(None) if Instant::now() >= deadline => {
                let _ = process.child.kill();
                let _ = process.child.wait();
                return Err(CursorCliError::TimedOut);
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(25)),
        }
    }
    reader.join().ok().and_then(Result::ok).ok_or(CursorCliError::Failed)
}

fn cursor_agent(home: &Path) -> Result<Command, CursorCliError> {
    let program = program().ok_or(CursorCliError::NotInstalled)?;
    Ok(command_for(&program, home))
}

/// `program` set up to act on `home` only.
fn command_for(program: &Path, home: &Path) -> Command {
    let mut command = Command::new(program);
    command
        .env("APPDATA", home)
        .env("CURSOR_CONFIG_DIR", home)
        .env("NO_OPEN_BROWSER", "1")
        // An API key in CodexBar's environment would sign the CLI in as someone else.
        .env_remove("CURSOR_API_KEY");
    command
}

/// `cursor-agent` from PATH, else where Cursor's installer puts it (`%LOCALAPPDATA%\cursor-agent`).
fn program() -> Option<PathBuf> {
    let names = ["cursor-agent.exe", "cursor-agent.cmd"];
    let on_path = std::env::var_os("PATH").and_then(|path| {
        std::env::split_paths(&path)
            .flat_map(|dir| names.map(|name| dir.join(name)))
            .find(|candidate| candidate.is_file())
    });
    on_path.or_else(|| {
        let dir = PathBuf::from(std::env::var_os("LOCALAPPDATA")?).join("cursor-agent");
        names
            .map(|name| dir.join(name))
            .into_iter()
            .find(|candidate| candidate.is_file())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_sign_in_page_is_read_from_the_login_output() {
        assert_eq!(
            sign_in_link(
                "Open a browser and navigate to this link: https://cursor.com/loginDeepControl?challenge=x&uuid=y"
            ),
            Some("https://cursor.com/loginDeepControl?challenge=x&uuid=y".to_owned())
        );
        assert_eq!(sign_in_link("Waiting for browser authentication..."), None);
        assert_eq!(sign_in_link("navigate to this link: http://insecure.example"), None);
    }

    #[test]
    fn every_cursor_command_acts_on_its_own_folder_only() {
        let home = std::env::temp_dir().join("codexbar-cursor-home");
        let command = command_for(Path::new("cursor-agent.cmd"), &home);
        let value = |name: &str| {
            command
                .get_envs()
                .find(|(known, _)| *known == name)
                .map(|(_, value)| value.map(|value| value.to_string_lossy().into_owned()))
        };
        let folder = Some(Some(home.to_string_lossy().into_owned()));
        assert_eq!(value("APPDATA"), folder);
        assert_eq!(value("CURSOR_CONFIG_DIR"), folder);
        assert_eq!(value("NO_OPEN_BROWSER"), Some(Some("1".to_owned())));
        assert_eq!(value("CURSOR_API_KEY"), Some(None));
        assert_eq!(auth_path(&home), home.join("Cursor").join("auth.json"));
    }

    /// Starts the installed Cursor CLI's sign-in for an empty temporary folder and stops it once the page is shown:
    /// checks the page is printed and nothing is signed in. Opens no browser. Run with `--ignored`.
    #[test]
    #[ignore = "starts the installed Cursor CLI"]
    fn the_installed_cursor_cli_shows_a_sign_in_page() {
        let home = std::env::temp_dir().join(format!("codexbar-live-cursor-{}", std::process::id()));
        std::fs::create_dir_all(&home).unwrap();
        let login = PendingLogin::start(&home).expect("cursor-agent prints the page");
        assert!(login.url().starts_with("https://cursor.com/"), "{}", login.url());
        assert_eq!(
            login.finish(Duration::from_secs(5), &AtomicBool::new(true)),
            Err(CursorCliError::Cancelled)
        );
        assert!(!auth_path(&home).exists());
        assert_eq!(email(&home), None);
        let _ = std::fs::remove_dir_all(&home);
    }
}
