//! The MSIX package (#93): whether CodexBar runs from its package, which version, and Windows' own update check for a
//! package installed through an App Installer file (`package.ps1` writes one into the local channel folder). The
//! build from source (`run.ps1`) has no package; everything here reports that instead of failing.

use std::sync::OnceLock;

use gpui_kit::{App, Global};

/// The `Application Id` in `packaging/AppxManifest.xml`; with the package family name it forms the app's
/// AppUserModelID.
pub const APPLICATION_ID: &str = "CodexBar";

/// The build `package.ps1` stamps into a packaged executable (its commit), if any.
pub const BUILD: Option<&str> = option_env!("CODEXBAR_BUILD");

/// The package CodexBar runs from.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Installed {
    /// `major.minor.build.revision`.
    pub version: String,
    pub family: String,
    /// The App Installer file Windows checks for updates, when the package was installed from one.
    pub app_installer: Option<String>,
}

impl Installed {
    /// The AppUserModelID Windows gives the packaged app.
    pub fn app_user_model_id(&self) -> String {
        format!("{}!{APPLICATION_ID}", self.family)
    }
}

/// The package of this process, read once. `None` for the unpackaged build.
pub fn installed() -> Option<&'static Installed> {
    static INSTALLED: OnceLock<Option<Installed>> = OnceLock::new();
    INSTALLED.get_or_init(read_installed).as_ref()
}

fn read_installed() -> Option<Installed> {
    use windows::ApplicationModel::Package;
    // Unpackaged processes get APPMODEL_ERROR_NO_PACKAGE here.
    let package = Package::Current().ok()?;
    let id = package.Id().ok()?;
    let version = id.Version().ok()?;
    let family = id.FamilyName().ok()?.to_string();
    let app_installer = package
        .GetAppInstallerInfo()
        .ok()
        .and_then(|info| info.Uri().ok())
        .and_then(|uri| uri.DisplayUri().ok())
        .map(|uri| uri.to_string())
        .filter(|uri| !uri.is_empty());
    Some(Installed {
        version: format!(
            "{}.{}.{}.{}",
            version.Major, version.Minor, version.Build, version.Revision
        ),
        family,
        app_installer,
    })
}

/// Where the update check stands.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum UpdateState {
    /// Built from source: update with `git pull` and `run.ps1`.
    NotPackaged,
    /// Installed from an `.msix` directly, so Windows has no channel to check.
    NoChannel,
    NotChecked,
    Checking,
    UpToDate,
    /// A newer version is in the channel; `required` when the App Installer file marks it mandatory.
    Available {
        required: bool,
    },
    Installing,
    /// The check or the install failed; the message is Windows' own.
    Failed(String),
}

impl UpdateState {
    /// The state before the first check.
    pub fn initial(installed: Option<&Installed>) -> Self {
        match installed {
            None => Self::NotPackaged,
            Some(installed) if installed.app_installer.is_none() => Self::NoChannel,
            Some(_) => Self::NotChecked,
        }
    }

    pub fn label(&self) -> String {
        match self {
            Self::NotPackaged => "Built from source. Update with git pull and run.ps1.".into(),
            Self::NoChannel => "Installed from a package file, so there is no update channel to check.".into(),
            Self::NotChecked => "Not checked yet.".into(),
            Self::Checking => "Checking for updates…".into(),
            Self::UpToDate => "CodexBar is up to date.".into(),
            Self::Available { required: false } => "A new version is ready to install.".into(),
            Self::Available { required: true } => "A required update is ready to install.".into(),
            Self::Installing => "Installing the update; CodexBar restarts when it's done.".into(),
            Self::Failed(message) => format!("Couldn't update: {message}"),
        }
    }

    pub fn can_check(&self) -> bool {
        matches!(
            self,
            Self::NotChecked | Self::UpToDate | Self::Available { .. } | Self::Failed(_)
        )
    }

    pub fn can_install(&self) -> bool {
        matches!(self, Self::Available { .. })
    }
}

/// Asks Windows whether the App Installer channel has a newer version. Blocking: run it off the UI thread.
pub fn check() -> UpdateState {
    use windows::ApplicationModel::{Package, PackageUpdateAvailability};
    let result = (|| -> windows::core::Result<UpdateState> {
        let result = Package::Current()?.CheckUpdateAvailabilityAsync()?.join()?;
        Ok(match result.Availability()? {
            PackageUpdateAvailability::Available => UpdateState::Available { required: false },
            PackageUpdateAvailability::Required => UpdateState::Available { required: true },
            PackageUpdateAvailability::NoUpdates => UpdateState::UpToDate,
            PackageUpdateAvailability::Error => {
                UpdateState::Failed(windows::core::Error::from_hresult(result.ExtendedError()?).message())
            }
            _ => UpdateState::Failed("Windows couldn't tell whether an update is available.".into()),
        })
    })();
    result.unwrap_or_else(|err| UpdateState::Failed(err.message()))
}

/// Installs the channel's newer version. Windows closes CodexBar to replace it and starts it again afterwards, so on
/// success this call doesn't return; it returns the error otherwise. Blocking: run it off the UI thread.
pub fn install() -> Result<(), String> {
    use windows::ApplicationModel::Package;
    use windows::Management::Deployment::{AddPackageByAppInstallerOptions, PackageManager, PackageVolume};
    use windows::Win32::System::Recovery::{REGISTER_APPLICATION_RESTART_FLAGS, RegisterApplicationRestart};
    use windows::core::PCWSTR;
    let result = (|| -> windows::core::Result<Option<String>> {
        let uri = Package::Current()?.GetAppInstallerInfo()?.Uri()?;
        // Asks Windows to start CodexBar again once the update has replaced it.
        // SAFETY: no command line (a null pointer) and no flags.
        unsafe { RegisterApplicationRestart(PCWSTR::null(), REGISTER_APPLICATION_RESTART_FLAGS(0)) }?;
        let deployment = PackageManager::new()?
            .AddPackageByAppInstallerFileAsync(
                &uri,
                AddPackageByAppInstallerOptions::ForceTargetAppShutdown,
                None::<&PackageVolume>,
            )?
            .join()?;
        let text = deployment.ErrorText()?.to_string();
        Ok((!text.is_empty()).then_some(text))
    })();
    match result {
        Ok(None) => Ok(()),
        Ok(Some(text)) => Err(text),
        Err(err) => Err(err.message()),
    }
}

/// `codexbar --package-status <file>`: writes the package identity and a fresh update check to `file` as JSON and
/// exits, so the package can be verified without opening a window (`scripts/Test-MsixChannel.ps1`).
pub fn write_status(path: &std::path::Path) -> std::io::Result<()> {
    let status = match installed() {
        None => serde_json::json!({ "packaged": false }),
        Some(installed) => {
            let update = match UpdateState::initial(Some(installed)) {
                UpdateState::NotChecked => check(),
                state => state,
            };
            serde_json::json!({
                "packaged": true,
                "version": installed.version,
                "family": installed.family,
                "channel": installed.app_installer,
                "update": update.label(),
                "updateAvailable": update.can_install(),
            })
        }
    };
    std::fs::write(path, status.to_string())
}

/// How often the background check runs while CodexBar is open.
const CHECK_INTERVAL: std::time::Duration = std::time::Duration::from_secs(24 * 60 * 60);
/// The first check waits until startup is over.
const FIRST_CHECK_DELAY: std::time::Duration = std::time::Duration::from_secs(30);

/// The update state the About page shows.
pub struct Updates {
    pub state: UpdateState,
}

impl Global for Updates {}

impl Updates {
    pub fn init(cx: &mut App, background: bool) {
        let state = UpdateState::initial(installed());
        let checks = background && state == UpdateState::NotChecked;
        cx.set_global(Self { state });
        if checks {
            cx.spawn(async move |cx| {
                cx.background_executor().timer(FIRST_CHECK_DELAY).await;
                loop {
                    cx.update(Self::check_now);
                    cx.background_executor().timer(CHECK_INTERVAL).await;
                }
            })
            .detach();
        }
    }

    pub fn state(cx: &App) -> UpdateState {
        cx.try_global::<Self>()
            .map_or(UpdateState::NotPackaged, |updates| updates.state.clone())
    }

    #[cfg(test)]
    pub fn set_for_test(cx: &mut App, state: UpdateState) {
        Self::set(cx, state);
    }

    fn set(cx: &mut App, state: UpdateState) {
        cx.set_global(Self { state });
        cx.refresh_windows();
    }

    /// Starts a check, unless one is running or the app has no channel.
    pub fn check_now(cx: &mut App) {
        if !Self::state(cx).can_check() {
            return;
        }
        Self::set(cx, UpdateState::Checking);
        let task = cx.background_executor().spawn(async { check() });
        cx.spawn(async move |cx| {
            let state = task.await;
            cx.update(|cx| Self::set(cx, state));
        })
        .detach();
    }

    pub fn install_now(cx: &mut App) {
        if !Self::state(cx).can_install() {
            return;
        }
        Self::set(cx, UpdateState::Installing);
        let task = cx.background_executor().spawn(async { install() });
        cx.spawn(async move |cx| {
            let state = match task.await {
                Ok(()) => UpdateState::UpToDate,
                Err(message) => UpdateState::Failed(message),
            };
            cx.update(|cx| Self::set(cx, state));
        })
        .detach();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn packaged(app_installer: Option<&str>) -> Installed {
        Installed {
            version: "0.1.0.42".into(),
            family: "HemSoft.CodexBar_abc123".into(),
            app_installer: app_installer.map(str::to_owned),
        }
    }

    #[test]
    fn tests_run_unpackaged() {
        assert_eq!(installed(), None);
        assert_eq!(UpdateState::initial(installed()), UpdateState::NotPackaged);
    }

    #[test]
    fn only_a_package_from_an_app_installer_file_can_check() {
        assert_eq!(UpdateState::initial(Some(&packaged(None))), UpdateState::NoChannel);
        let channel = UpdateState::initial(Some(&packaged(Some(r"C:\channel\CodexBar.appinstaller"))));
        assert_eq!(channel, UpdateState::NotChecked);
        assert!(channel.can_check());
        assert!(!UpdateState::NotPackaged.can_check());
        assert!(!UpdateState::NoChannel.can_check());
        assert!(!UpdateState::Checking.can_check(), "one check at a time");
        assert!(!UpdateState::Installing.can_check());
    }

    #[test]
    fn only_an_available_update_can_be_installed() {
        assert!(UpdateState::Available { required: false }.can_install());
        assert!(UpdateState::Available { required: true }.can_install());
        for state in [
            UpdateState::NotPackaged,
            UpdateState::NoChannel,
            UpdateState::NotChecked,
            UpdateState::Checking,
            UpdateState::UpToDate,
            UpdateState::Installing,
            UpdateState::Failed("offline".into()),
        ] {
            assert!(!state.can_install(), "{state:?}");
        }
    }

    #[test]
    fn labels_say_what_to_do() {
        assert!(UpdateState::NotPackaged.label().contains("run.ps1"));
        assert_eq!(
            UpdateState::Failed("The network path was not found.".into()).label(),
            "Couldn't update: The network path was not found."
        );
        assert!(UpdateState::Available { required: true }.label().contains("required"));
    }

    #[test]
    fn the_app_user_model_id_is_family_and_application() {
        assert_eq!(packaged(None).app_user_model_id(), "HemSoft.CodexBar_abc123!CodexBar");
    }

    #[test]
    fn the_manifest_declares_the_application_id() {
        let manifest = include_str!("../../../packaging/AppxManifest.xml");
        assert!(manifest.contains(&format!(r#"<Application Id="{APPLICATION_ID}""#)));
    }

    #[test]
    fn the_status_file_says_unpackaged() {
        let path = std::env::temp_dir().join(format!("codexbar-package-status-{}.json", std::process::id()));
        write_status(&path).unwrap();
        let written = std::fs::read_to_string(&path).unwrap();
        let _ = std::fs::remove_file(&path);
        assert_eq!(written, r#"{"packaged":false}"#);
    }

    #[test]
    fn unpackaged_checks_and_installs_fail_without_panicking() {
        assert!(matches!(check(), UpdateState::Failed(_)));
        assert!(install().is_err());
    }
}
