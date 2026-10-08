use chrono::{DateTime, Utc};

use crate::metric::Metric;
use crate::severity::{Assessment, assess};

/// An AI provider whose usage CodexBar reads.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Provider {
    Codex,
    Claude,
    Copilot,
    Cursor,
    OpenRouter,
    OpenCode,
    Moonshot,
}

impl Provider {
    /// The product name shown before the account label.
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Codex => "ChatGPT · Codex",
            Self::Claude => "Claude",
            Self::Copilot => "Copilot",
            Self::Cursor => "Cursor",
            Self::OpenRouter => "OpenRouter",
            Self::OpenCode => "OpenCode Go + Zen",
            Self::Moonshot => "Moonshot (Kimi)",
        }
    }

    pub const ALL: [Self; 7] = [
        Self::Codex,
        Self::Claude,
        Self::Copilot,
        Self::Cursor,
        Self::OpenRouter,
        Self::OpenCode,
        Self::Moonshot,
    ];

    /// A stable key for storage; unlike the display name it never changes with wording.
    pub fn key(self) -> &'static str {
        match self {
            Self::Codex => "codex",
            Self::Claude => "claude",
            Self::Copilot => "copilot",
            Self::Cursor => "cursor",
            Self::OpenRouter => "openrouter",
            Self::OpenCode => "opencode",
            Self::Moonshot => "moonshot",
        }
    }

    pub fn from_key(key: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|provider| provider.key() == key)
    }

    /// The provider an adapter reports as, by its display name (`UsageProvider::name`).
    pub fn from_display_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|provider| provider.display_name() == name)
    }
}

/// Stable identity of one provider account, independent of display order.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct AccountId(String);

impl AccountId {
    pub fn new(id: impl Into<String>) -> Self {
        Self(id.into())
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// Usage within one window, sampled over time, comparing the current window with the previous one.
#[derive(Clone, Debug, PartialEq)]
pub struct WindowCurve {
    labels: Vec<String>,
    current: Vec<f64>,
    previous: Vec<f64>,
}

impl WindowCurve {
    /// Builds a curve. `current` may be shorter than `labels` while the window is still running.
    pub fn new(labels: Vec<String>, current: Vec<f64>, previous: Vec<f64>) -> Self {
        Self {
            labels,
            current,
            previous,
        }
    }

    pub fn labels(&self) -> &[String] {
        &self.labels
    }

    pub fn current(&self) -> &[f64] {
        &self.current
    }

    pub fn previous(&self) -> &[f64] {
        &self.previous
    }
}

/// Detail series shown when an account is focused.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct AccountDetail {
    window_curve: Option<WindowCurve>,
    requests_by_hour: Vec<u32>,
}

impl AccountDetail {
    pub fn with_window_curve(mut self, curve: WindowCurve) -> Self {
        self.window_curve = Some(curve);
        self
    }

    pub fn with_requests_by_hour(mut self, requests: Vec<u32>) -> Self {
        self.requests_by_hour = requests;
        self
    }

    pub fn window_curve(&self) -> Option<&WindowCurve> {
        self.window_curve.as_ref()
    }

    pub fn requests_by_hour(&self) -> &[u32] {
        &self.requests_by_hour
    }
}

/// The latest known state of one account.
#[derive(Clone, Debug, PartialEq)]
pub struct AccountSnapshot {
    id: AccountId,
    provider: Provider,
    label: Option<String>,
    metrics: Vec<Metric>,
    trend: Vec<f64>,
    detail: AccountDetail,
    fetched_at: DateTime<Utc>,
    /// What the provider said about the account besides numbers ("Usage is delayed by up to an hour").
    messages: Vec<String>,
}

impl AccountSnapshot {
    /// Creates a snapshot. The first metric is the account's primary limit.
    pub fn new(id: AccountId, provider: Provider, metrics: Vec<Metric>, fetched_at: DateTime<Utc>) -> Self {
        Self {
            id,
            provider,
            label: None,
            metrics,
            trend: Vec::new(),
            detail: AccountDetail::default(),
            fetched_at,
            messages: Vec::new(),
        }
    }

    /// Adds a provider message, shown with the account and kept with its last-good snapshot.
    pub fn with_message(mut self, message: impl Into<String>) -> Self {
        self.messages.push(message.into());
        self
    }

    pub fn messages(&self) -> &[String] {
        &self.messages
    }

    pub fn with_label(mut self, label: impl Into<String>) -> Self {
        self.label = Some(label.into());
        self
    }

    /// Daily pressure for the trend sparkline, oldest first.
    pub fn with_trend(mut self, trend: Vec<f64>) -> Self {
        self.trend = trend;
        self
    }

    pub fn with_detail(mut self, detail: AccountDetail) -> Self {
        self.detail = detail;
        self
    }

    pub fn id(&self) -> &AccountId {
        &self.id
    }

    pub fn provider(&self) -> Provider {
        self.provider
    }

    pub fn label(&self) -> Option<&str> {
        self.label.as_deref()
    }

    /// "Copilot · work", or the provider name alone when the account has no label.
    pub fn display_name(&self) -> String {
        match &self.label {
            Some(label) if self.provider == Provider::Codex => format!("{} ({label})", self.provider.display_name()),
            Some(label) => format!("{} · {label}", self.provider.display_name()),
            None => self.provider.display_name().to_owned(),
        }
    }

    pub fn metrics(&self) -> &[Metric] {
        &self.metrics
    }

    pub fn primary(&self) -> Option<&Metric> {
        self.metrics.first()
    }

    pub fn trend(&self) -> &[f64] {
        &self.trend
    }

    pub fn detail(&self) -> &AccountDetail {
        &self.detail
    }

    pub fn fetched_at(&self) -> DateTime<Utc> {
        self.fetched_at
    }

    /// The most urgent assessment across all of the account's metrics.
    pub fn assess(&self, now: DateTime<Utc>) -> Assessment {
        self.metrics
            .iter()
            .map(|metric| assess(metric, now))
            .max()
            .unwrap_or_default()
    }
}

/// Orders accounts so whatever blocks the user soonest comes first. Ties keep provider order stable by id.
pub fn sort_by_urgency(accounts: &mut [AccountSnapshot], now: DateTime<Utc>) {
    accounts.sort_by(|a, b| {
        b.assess(now)
            .cmp(&a.assess(now))
            .then_with(|| a.id.as_str().cmp(b.id.as_str()))
    });
}
