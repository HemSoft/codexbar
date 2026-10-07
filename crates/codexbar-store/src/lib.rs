//! Local persistence for CodexBar. Today that is usage history (#85): bounded, deduplicated samples per account
//! and metric, in a versioned JSON Lines file. Only account ids, metric keys, numbers and times are stored.

mod enrich;
mod history;

pub use enrich::{TREND_DAYS, enrich};
pub use history::{HistoryStore, Sample, default_history_path};
