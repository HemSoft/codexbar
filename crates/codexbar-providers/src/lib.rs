//! Provider adapters. Each reads the credentials its provider's own client already maintains, fetches usage over
//! HTTPS, and maps the response onto `codexbar_core` snapshots. Fetches are blocking; callers run them off the UI thread.

pub mod claude;
pub mod codex;
mod command;
pub mod copilot;
mod error;
mod http;
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
}
