use std::fmt;

/// Why a provider could not report usage. Messages are user-facing and never contain credentials.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ProviderError {
    /// No local sign-in was found; `hint` says how to create one.
    NotSignedIn { hint: &'static str },
    /// The provider rejected the stored credentials.
    Expired { hint: &'static str },
    /// The provider answered with an unexpected HTTP status.
    Http { status: u16 },
    /// The request never completed (DNS, TLS, timeout, offline).
    Network,
    /// The provider asked us to wait; no request is made before `retry_at`.
    RateLimited { retry_at: chrono::DateTime<chrono::Utc> },
    /// The response did not have the expected shape.
    Unexpected { detail: &'static str },
}

impl fmt::Display for ProviderError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotSignedIn { hint } => write!(f, "Not signed in. {hint}"),
            Self::Expired { hint } => write!(f, "Sign-in expired. {hint}"),
            Self::Http { status } => write!(f, "The usage service returned HTTP {status}."),
            Self::Network => write!(f, "Couldn't reach the usage service. Check your connection."),
            Self::RateLimited { retry_at } => write!(
                f,
                "Rate-limited by the provider; retrying at {}.",
                retry_at.with_timezone(&chrono::Local).format("%H:%M")
            ),
            Self::Unexpected { detail } => write!(f, "Unexpected usage response: {detail}."),
        }
    }
}

impl std::error::Error for ProviderError {}
