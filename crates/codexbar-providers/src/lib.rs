//! Provider adapters. Each reads the credentials its provider's own client already maintains, fetches usage over
//! HTTPS, and maps the response onto `codexbar_core` snapshots. Fetches are blocking; callers run them off the UI thread.

pub mod balance;
pub mod claude;
pub mod codex;
mod command;
pub mod copilot;
pub mod cursor;
mod error;
mod http;
pub mod opencode;
mod pace;

pub use command::{CommandError, CommandOutput, CommandRunner, SystemCommandRunner};
pub use error::ProviderError;
pub use http::{HttpClient, HttpResponse, UreqClient};

use chrono::{DateTime, Utc};
use codexbar_core::AccountSnapshot;

/// One source of accounts.
pub trait UsageProvider: Send + Sync {
    /// Stable identifier used for logging and error rows.
    fn name(&self) -> &'static str;

    /// Fetches every account this provider can see at `now`.
    fn fetch(&self, now: DateTime<Utc>) -> Result<Vec<AccountSnapshot>, ProviderError>;

    /// The configured account this adapter reports, when it serves exactly one (several OpenRouter or Moonshot
    /// accounts each get their own adapter under the same name). Lets the dashboard tell their results apart.
    fn account_id(&self) -> Option<&str> {
        None
    }

    /// The configured account's label, when this adapter serves one labelled account ("Team").
    fn account_label(&self) -> Option<&str> {
        None
    }
}
