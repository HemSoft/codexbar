//! Delivers alerts (#87) as Windows notifications and keeps the active-alert set: an alert is marked active only once
//! it was shown, so a blocked or failed notification is tried again on the next refresh.

use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use codexbar_core::AccountSnapshot;
use codexbar_core::alerts::{Alert, evaluate};
use gpui_kit::{App, BorrowAppContext as _, Global, SharedString};

use crate::prefs_hub::PrefsHub;

/// Whether notifications can be shown right now.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum NotifierStatus {
    Ready,
    /// Windows won't show them; the reason says where to turn them on.
    Blocked(String),
}

pub trait Notifier: Send + Sync {
    fn status(&self) -> NotifierStatus;
    fn show(&self, alert: &Alert) -> Result<(), String>;
}

/// The notifier in use and the last delivery problem.
pub struct Notifications {
    notifier: Arc<dyn Notifier>,
    problem: Option<SharedString>,
}

impl Global for Notifications {}

impl Notifications {
    pub fn init(cx: &mut App, notifier: Arc<dyn Notifier>) {
        cx.set_global(Self {
            notifier,
            problem: None,
        });
    }

    pub fn status(cx: &App) -> Option<NotifierStatus> {
        cx.try_global::<Self>().map(|global| global.notifier.status())
    }

    /// The last delivery problem, cleared by the next successful notification.
    pub fn problem(cx: &App) -> Option<SharedString> {
        cx.try_global::<Self>().and_then(|global| global.problem.clone())
    }

    /// Sends a sample notification from Settings, so the user can see where alerts appear and whether Windows
    /// shows them.
    pub fn send_test(cx: &mut App) {
        let alert = Alert {
            key: String::new(),
            kind: codexbar_core::alerts::AlertKind::Usage,
            account: "CodexBar".into(),
            metric: "Test".into(),
            value: String::new(),
            title: "CodexBar alerts are on".into(),
            body: "Usage and balance alerts will appear like this.".into(),
            covers: Vec::new(),
        };
        let Some(notifier) = cx.try_global::<Self>().map(|global| global.notifier.clone()) else {
            return;
        };
        let problem = match notifier.status() {
            NotifierStatus::Blocked(reason) => Some(reason),
            NotifierStatus::Ready => notifier.show(&alert).err(),
        };
        cx.update_global(|global: &mut Self, _| global.problem = problem.map(Into::into));
        cx.refresh_windows();
    }

    /// Forgets which alerts were already shown, so conditions that still hold notify again on the next refresh.
    pub fn reset(cx: &mut App) {
        PrefsHub::set_active_alerts(cx, Default::default());
        cx.refresh_windows();
    }
}

/// Evaluates the accounts that refreshed successfully and delivers new alerts. Accounts whose provider failed are
/// not passed in, so their alerts are neither cleared nor repeated.
pub fn process(cx: &mut App, refreshed: &[AccountSnapshot], now: DateTime<Utc>) {
    let Some(notifier) = cx.try_global::<Notifications>().map(|global| global.notifier.clone()) else {
        return;
    };
    let settings = PrefsHub::alert_settings(cx);
    let active = PrefsHub::active_alerts(cx);
    let evaluation = evaluate(&settings, &active, refreshed, now);
    if evaluation.notify.is_empty() && evaluation.recovered.is_empty() {
        return;
    }

    let mut next = active;
    for key in &evaluation.recovered {
        next.remove(key);
    }
    let mut problem = None;
    if !evaluation.notify.is_empty() {
        match notifier.status() {
            NotifierStatus::Blocked(reason) => problem = Some(reason),
            NotifierStatus::Ready => {
                for alert in &evaluation.notify {
                    match notifier.show(alert) {
                        Ok(()) => next.extend(alert.keys().cloned()),
                        Err(err) => problem = Some(err),
                    }
                }
            }
        }
    }
    cx.update_global(|global: &mut Notifications, _| global.problem = problem.map(Into::into));
    PrefsHub::set_active_alerts(cx, next);
}

/// Keeps notifications in memory: the demo dashboard (so design work never pops real notifications) and tests.
#[derive(Default)]
pub struct RecordingNotifier {
    pub shown: Mutex<Vec<Alert>>,
    pub blocked: Mutex<Option<String>>,
}

impl Notifier for RecordingNotifier {
    fn status(&self) -> NotifierStatus {
        match self.blocked.lock().unwrap().clone() {
            Some(reason) => NotifierStatus::Blocked(reason),
            None => NotifierStatus::Ready,
        }
    }

    fn show(&self, alert: &Alert) -> Result<(), String> {
        self.shown.lock().unwrap().push(alert.clone());
        Ok(())
    }
}

/// Windows toast notifications for the unpackaged app, under a per-user AppUserModelID registered in
/// `HKCU\Software\Classes\AppUserModelId` so they show CodexBar's name.
pub struct WindowsNotifier;

/// The AppUserModelID notifications are sent under.
pub const APP_ID: &str = "HemSoft.CodexBar";

impl WindowsNotifier {
    /// Registers the app id for this user and this process. Registration failures are reported by `status`.
    pub fn new() -> Self {
        let _ = register_app_id();
        Self
    }
}

impl Notifier for WindowsNotifier {
    fn status(&self) -> NotifierStatus {
        use windows::UI::Notifications::{NotificationSetting, ToastNotificationManager};
        let setting = ToastNotificationManager::CreateToastNotifierWithId(&windows::core::HSTRING::from(APP_ID))
            .and_then(|notifier| notifier.Setting());
        match setting {
            Ok(NotificationSetting::Enabled) => NotifierStatus::Ready,
            Ok(NotificationSetting::DisabledForApplication) => NotifierStatus::Blocked(
                "Notifications are off for CodexBar. Turn them on in Windows Settings › System › Notifications.".into(),
            ),
            Ok(NotificationSetting::DisabledForUser) => NotifierStatus::Blocked(
                "Notifications are off in Windows. Turn them on in Windows Settings › System › Notifications.".into(),
            ),
            Ok(NotificationSetting::DisabledByGroupPolicy) => {
                NotifierStatus::Blocked("Notifications are turned off by your organization's policy.".into())
            }
            Ok(_) => NotifierStatus::Blocked("Windows doesn't allow notifications for CodexBar.".into()),
            Err(err) => NotifierStatus::Blocked(format!("Windows notifications are unavailable: {}", err.message())),
        }
    }

    fn show(&self, alert: &Alert) -> Result<(), String> {
        use windows::Data::Xml::Dom::XmlDocument;
        use windows::UI::Notifications::{ToastNotification, ToastNotificationManager};
        use windows::core::HSTRING;
        let xml = toast_xml(&alert.title, &alert.body);
        let result = (|| {
            let document = XmlDocument::new()?;
            document.LoadXml(&HSTRING::from(xml))?;
            let toast = ToastNotification::CreateToastNotification(&document)?;
            ToastNotificationManager::CreateToastNotifierWithId(&HSTRING::from(APP_ID))?.Show(&toast)
        })();
        result.map_err(|err| format!("Notification not shown: {}", err.message()))
    }
}

/// The toast payload: a title line and a body line, escaped for XML.
fn toast_xml(title: &str, body: &str) -> String {
    format!(
        "<toast><visual><binding template=\"ToastGeneric\"><text>{}</text><text>{}</text></binding></visual></toast>",
        escape(title),
        escape(body)
    )
}

fn escape(text: &str) -> String {
    text.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

/// Registers the AppUserModelID's display name for this user, and claims it for this process, so Windows shows
/// "CodexBar" on notifications from an unpackaged app.
fn register_app_id() -> windows::core::Result<()> {
    use windows::Win32::System::Registry::{
        HKEY, HKEY_CURRENT_USER, KEY_WRITE, REG_OPTION_NON_VOLATILE, REG_SZ, RegCloseKey, RegCreateKeyExW,
        RegSetValueExW,
    };
    use windows::Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID;
    use windows::core::{HSTRING, PCWSTR};

    let path = HSTRING::from(format!("Software\\Classes\\AppUserModelId\\{APP_ID}"));
    let mut key = HKEY::default();
    // SAFETY: valid key handle out-pointer and null-terminated strings that outlive the calls.
    unsafe {
        RegCreateKeyExW(
            HKEY_CURRENT_USER,
            &path,
            None,
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut key,
            None,
        )
        .ok()?;
        let name: Vec<u16> = "CodexBar".encode_utf16().chain(std::iter::once(0)).collect();
        let bytes = std::slice::from_raw_parts(name.as_ptr().cast::<u8>(), name.len() * 2);
        let set = RegSetValueExW(key, &HSTRING::from("DisplayName"), None, REG_SZ, Some(bytes)).ok();
        let _ = RegCloseKey(key);
        set?;
        SetCurrentProcessExplicitAppUserModelID(&HSTRING::from(APP_ID))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn toast_xml_escapes_markup() {
        let xml = toast_xml("A & B", "<5% left> \"quoted\" it's");
        assert!(xml.contains("<text>A &amp; B</text>"));
        assert!(xml.contains("<text>&lt;5% left&gt; &quot;quoted&quot; it&apos;s</text>"));
    }
}
