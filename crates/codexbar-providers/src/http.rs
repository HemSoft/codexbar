use std::time::Duration;

use crate::ProviderError;

/// A completed HTTP exchange: any status, with the body read as text.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HttpResponse {
    pub status: u16,
    pub body: String,
}

/// The one HTTP operation providers need. A seam so parsers and status handling are tested without the network.
pub trait HttpClient: Send + Sync {
    fn get(&self, url: &str, headers: &[(&str, &str)]) -> Result<HttpResponse, ProviderError>;
}

/// Blocking HTTPS client with a short timeout. Non-2xx statuses are returned, not raised.
pub struct UreqClient {
    agent: ureq::Agent,
}

impl UreqClient {
    pub fn new() -> Self {
        let config = ureq::Agent::config_builder()
            .timeout_global(Some(Duration::from_secs(20)))
            .http_status_as_error(false)
            .user_agent("CodexBar")
            .build();
        Self { agent: config.into() }
    }
}

impl Default for UreqClient {
    fn default() -> Self {
        Self::new()
    }
}

impl HttpClient for UreqClient {
    fn get(&self, url: &str, headers: &[(&str, &str)]) -> Result<HttpResponse, ProviderError> {
        let mut request = self.agent.get(url);
        for (name, value) in headers {
            request = request.header(*name, *value);
        }
        let mut response = request.call().map_err(|_| ProviderError::Network)?;
        let status = response.status().as_u16();
        let body = response
            .body_mut()
            .read_to_string()
            .map_err(|_| ProviderError::Network)?;
        Ok(HttpResponse { status, body })
    }
}
