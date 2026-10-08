//! Account secrets (API keys, dashboard cookies) in Windows Credential Manager, keyed by stable account id (#74).
//! Each secret is a generic credential named `CodexBar:account:<id>`, persisted for the signed-in user on this machine.

use std::fmt;

const TARGET_PREFIX: &str = "CodexBar:account:";

/// A store failure, kept distinct from "no secret stored" (`Ok(None)`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CredentialError {
    pub code: u32,
}

impl fmt::Display for CredentialError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.code == VERIFY_FAILED {
            return f.write_str("Windows Credential Manager didn't return the saved secret.");
        }
        write!(f, "Windows Credential Manager failed (error {}).", self.code)
    }
}

impl std::error::Error for CredentialError {}

impl CredentialError {
    /// Credential Manager accepted a secret but returned something else when read back.
    pub fn verification() -> Self {
        Self { code: VERIFY_FAILED }
    }
}

/// Not a Windows error code: the code `CredentialError::verification` reports.
const VERIFY_FAILED: u32 = 0xC0DE_0001;

/// Read, add/replace and delete secrets by account id.
pub trait CredentialStore: Send + Sync {
    fn read(&self, account_id: &str) -> Result<Option<String>, CredentialError>;
    fn write(&self, account_id: &str, secret: &str) -> Result<(), CredentialError>;
    /// Deleting a secret that isn't there is not an error.
    fn delete(&self, account_id: &str) -> Result<(), CredentialError>;
}

pub fn target_name(account_id: &str) -> String {
    format!("{TARGET_PREFIX}{account_id}")
}

/// Windows Credential Manager.
#[derive(Clone, Copy, Debug, Default)]
pub struct WindowsCredentialStore {
    /// Prefix for tests so they never touch real CodexBar entries.
    namespace: Option<&'static str>,
}

impl WindowsCredentialStore {
    pub fn new() -> Self {
        Self { namespace: None }
    }

    #[cfg(test)]
    fn testing() -> Self {
        Self {
            namespace: Some("CodexBarTest:"),
        }
    }

    fn target(&self, account_id: &str) -> Vec<u16> {
        let name = match self.namespace {
            Some(namespace) => format!("{namespace}{account_id}"),
            None => target_name(account_id),
        };
        name.encode_utf16().chain(std::iter::once(0)).collect()
    }
}

#[cfg(windows)]
mod win {
    use windows::Win32::Foundation::{ERROR_NOT_FOUND, GetLastError};
    use windows::Win32::Security::Credentials::{
        CRED_FLAGS, CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC, CREDENTIALW, CredDeleteW, CredFree, CredReadW,
        CredWriteW,
    };
    use windows::core::{PCWSTR, PWSTR};

    use super::{CredentialError, CredentialStore, WindowsCredentialStore};

    fn last_error() -> CredentialError {
        CredentialError {
            code: unsafe { GetLastError().0 },
        }
    }

    impl CredentialStore for WindowsCredentialStore {
        fn read(&self, account_id: &str) -> Result<Option<String>, CredentialError> {
            let target = self.target(account_id);
            let mut credential: *mut CREDENTIALW = std::ptr::null_mut();
            // SAFETY: `target` is NUL-terminated UTF-16 that outlives the call; on success the API allocates
            // `credential`, which is freed with CredFree below after its blob is copied.
            let found = unsafe { CredReadW(PCWSTR(target.as_ptr()), CRED_TYPE_GENERIC, None, &mut credential) };
            if let Err(err) = found {
                return if err.code() == ERROR_NOT_FOUND.to_hresult() {
                    Ok(None)
                } else {
                    Err(last_error())
                };
            }
            // SAFETY: CredReadW succeeded, so `credential` points to a valid CREDENTIALW whose blob holds
            // `CredentialBlobSize` bytes.
            let secret = unsafe {
                let blob =
                    std::slice::from_raw_parts((*credential).CredentialBlob, (*credential).CredentialBlobSize as usize);
                let units: Vec<u16> = blob
                    .as_chunks::<2>()
                    .0
                    .iter()
                    .map(|pair| u16::from_le_bytes(*pair))
                    .collect();
                CredFree(credential as *const _);
                String::from_utf16_lossy(&units)
            };
            Ok(Some(secret))
        }

        fn write(&self, account_id: &str, secret: &str) -> Result<(), CredentialError> {
            let mut target = self.target(account_id);
            let mut user: Vec<u16> = "CodexBar".encode_utf16().chain(std::iter::once(0)).collect();
            let mut blob: Vec<u8> = secret.encode_utf16().flat_map(u16::to_le_bytes).collect();
            let credential = CREDENTIALW {
                Flags: CRED_FLAGS(0),
                Type: CRED_TYPE_GENERIC,
                TargetName: PWSTR(target.as_mut_ptr()),
                CredentialBlobSize: blob.len() as u32,
                CredentialBlob: blob.as_mut_ptr(),
                Persist: CRED_PERSIST_LOCAL_MACHINE,
                UserName: PWSTR(user.as_mut_ptr()),
                ..Default::default()
            };
            // SAFETY: every pointer in `credential` refers to a live local buffer for the duration of the call.
            let result = unsafe { CredWriteW(&credential, 0) };
            blob.iter_mut().for_each(|byte| *byte = 0);
            result.map_err(|_| last_error())
        }

        fn delete(&self, account_id: &str) -> Result<(), CredentialError> {
            let target = self.target(account_id);
            // SAFETY: `target` is NUL-terminated UTF-16 that outlives the call.
            match unsafe { CredDeleteW(PCWSTR(target.as_ptr()), CRED_TYPE_GENERIC, None) } {
                Ok(()) => Ok(()),
                Err(err) if err.code() == ERROR_NOT_FOUND.to_hresult() => Ok(()),
                Err(_) => Err(last_error()),
            }
        }
    }
}

/// An in-memory store for tests and for machines without Credential Manager.
#[derive(Debug, Default)]
pub struct MemoryCredentialStore {
    secrets: std::sync::Mutex<std::collections::HashMap<String, String>>,
    fail: bool,
}

impl MemoryCredentialStore {
    /// A store whose every operation fails, to exercise access-failure paths.
    pub fn failing() -> Self {
        Self {
            fail: true,
            ..Self::default()
        }
    }
}

impl CredentialStore for MemoryCredentialStore {
    fn read(&self, account_id: &str) -> Result<Option<String>, CredentialError> {
        if self.fail {
            return Err(CredentialError { code: 5 });
        }
        Ok(self
            .secrets
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .get(account_id)
            .cloned())
    }

    fn write(&self, account_id: &str, secret: &str) -> Result<(), CredentialError> {
        if self.fail {
            return Err(CredentialError { code: 5 });
        }
        self.secrets
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .insert(account_id.to_owned(), secret.to_owned());
        Ok(())
    }

    fn delete(&self, account_id: &str) -> Result<(), CredentialError> {
        if self.fail {
            return Err(CredentialError { code: 5 });
        }
        self.secrets
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .remove(account_id);
        Ok(())
    }
}

/// Where an account's secret currently comes from, for the Settings screen. Never carries the secret itself.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SecretSource {
    Environment(&'static str),
    CredentialManager,
    /// Still plaintext in the WPF settings file; moves to Credential Manager at cutover (#74).
    SettingsFile,
    Missing,
    /// Credential Manager could not be read; distinct from Missing.
    StoreUnavailable(CredentialError),
}

/// Resolves a secret: environment variable, then Credential Manager, then the legacy settings file.
pub fn resolve_secret(
    env: Option<&'static str>,
    store: &dyn CredentialStore,
    account_id: &str,
    legacy: Option<String>,
) -> (Option<String>, SecretSource) {
    if let Some(name) = env
        && let Some(value) = std::env::var(name)
            .ok()
            .map(|v| v.trim().to_owned())
            .filter(|v| !v.is_empty())
    {
        return (Some(value), SecretSource::Environment(name));
    }
    match store.read(account_id) {
        Ok(Some(secret)) if !secret.trim().is_empty() => (Some(secret), SecretSource::CredentialManager),
        Ok(_) => match legacy {
            Some(secret) => (Some(secret), SecretSource::SettingsFile),
            None => (None, SecretSource::Missing),
        },
        // A failed store still lets the legacy key work, but the status shows the failure.
        Err(err) => (legacy, SecretSource::StoreUnavailable(err)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn memory_store_add_replace_read_delete() {
        let store = MemoryCredentialStore::default();
        assert_eq!(store.read("a").unwrap(), None);
        store.write("a", "one").unwrap();
        store.write("a", "two").unwrap();
        assert_eq!(store.read("a").unwrap().as_deref(), Some("two"));
        store.delete("a").unwrap();
        store.delete("a").unwrap();
        assert_eq!(store.read("a").unwrap(), None);
    }

    #[test]
    fn resolve_secret_prefers_store_over_settings_file() {
        let store = MemoryCredentialStore::default();
        store.write("acct", "from-store").unwrap();
        let (secret, source) = resolve_secret(None, &store, "acct", Some("from-file".into()));
        assert_eq!(
            (secret.as_deref(), source),
            (Some("from-store"), SecretSource::CredentialManager)
        );
    }

    #[test]
    fn resolve_secret_falls_back_to_settings_file_then_missing() {
        let store = MemoryCredentialStore::default();
        let (secret, source) = resolve_secret(None, &store, "acct", Some("from-file".into()));
        assert_eq!(
            (secret.as_deref(), source),
            (Some("from-file"), SecretSource::SettingsFile)
        );
        assert_eq!(resolve_secret(None, &store, "acct", None).1, SecretSource::Missing);
    }

    #[test]
    fn resolve_secret_store_failure_is_distinct_from_missing() {
        let (secret, source) = resolve_secret(None, &MemoryCredentialStore::failing(), "acct", Some("file".into()));
        assert_eq!(secret.as_deref(), Some("file"));
        assert!(matches!(source, SecretSource::StoreUnavailable(_)));
    }

    #[cfg(windows)]
    #[test]
    fn windows_store_round_trip() {
        let store = WindowsCredentialStore::testing();
        let id = format!("roundtrip-{}", std::process::id());
        store.delete(&id).unwrap();
        assert_eq!(store.read(&id).unwrap(), None);
        store.write(&id, "sk-test-ünïcode").unwrap();
        store.write(&id, "sk-test-replaced").unwrap();
        assert_eq!(store.read(&id).unwrap().as_deref(), Some("sk-test-replaced"));
        store.delete(&id).unwrap();
        assert_eq!(store.read(&id).unwrap(), None);
    }
}
