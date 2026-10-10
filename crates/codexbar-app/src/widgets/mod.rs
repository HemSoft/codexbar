//! The Windows widget provider (#94). Windows starts `codexbar.exe -RegisterProcessAsComServer` when the Widgets
//! board needs CodexBar's widgets; that process registers the provider class, answers the board's calls and pushes
//! cards built from `widgets.json`. It never opens a window or reads credentials, and it runs beside the tray app
//! (which writes the snapshot), so it skips the single-instance check.
//!
//! The class and the widget definition are declared in `packaging/AppxManifest.xml`; only packaged apps can be
//! widget providers. The bindings in `bindings.rs` are generated from the Windows App SDK's Widgets metadata by
//! `scripts/Update-WidgetBindings.ps1`.

#[rustfmt::skip]
#[allow(clippy::all, clippy::pedantic, unsafe_op_in_unsafe_fn)]
mod bindings;
pub mod cards;
pub mod host;

use std::path::PathBuf;
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant, SystemTime};

use bindings::Microsoft::Windows::Widgets::Providers::{
    IWidgetProvider, IWidgetProvider_Impl, IWidgetProvider2, IWidgetProvider2_Impl, WidgetActionInvokedArgs,
    WidgetContext, WidgetContextChangedArgs, WidgetCustomizationRequestedArgs, WidgetManager,
    WidgetUpdateRequestOptions,
};
use bindings::Microsoft::Windows::Widgets::WidgetSize;
use chrono::Utc;
use codexbar_store::widgets::{LoadError, WidgetSnapshot, load_widget_snapshot};
use windows::Win32::Foundation::CLASS_E_NOAGGREGATION;
use windows::Win32::System::Com::{
    CLSCTX_LOCAL_SERVER, COINIT_MULTITHREADED, CoInitializeEx, CoRegisterClassObject, CoRevokeClassObject,
    IClassFactory, IClassFactory_Impl, REGCLS_MULTIPLEUSE,
};
use windows::core::{BOOL, GUID, HSTRING, IUnknown, Interface, Ref, implement};

use host::{Host, Outcome, Update};

/// The provider's COM class, `com:Class` in the manifest.
pub const CLSID: GUID = GUID::from_u128(0x6a9b22c1_0ca4_41f3_911a_fd4724d8e0b2);

/// The argument Windows starts the COM server with (`com:ExeServer Arguments` in the manifest).
pub const SERVER_ARG: &str = "-RegisterProcessAsComServer";

/// How often the snapshot file is checked while a widget is on screen. Cards also redraw once a minute for their
/// "Updated 3 min ago" text.
const POLL: Duration = Duration::from_secs(10);
const REDRAW: Duration = Duration::from_secs(60);
/// With no widget on screen for this long the process exits; Windows starts it again when the board needs it.
const IDLE_EXIT: Duration = Duration::from_secs(10 * 60);
/// With no widget pinned at all, it exits sooner.
const EMPTY_EXIT: Duration = Duration::from_secs(60);

fn size(size: WidgetSize) -> cards::Size {
    match size {
        WidgetSize::Small => cards::Size::Small,
        WidgetSize::Large => cards::Size::Large,
        _ => cards::Size::Medium,
    }
}

/// Everything the provider's COM calls and the poll loop share.
struct Shared {
    dir: PathBuf,
    host: Mutex<Host>,
    /// Wakes the poll loop early (a widget was pinned or shown).
    wake: Condvar,
    last_active: Mutex<Instant>,
}

impl Shared {
    fn snapshot(&self) -> Result<WidgetSnapshot, LoadError> {
        load_widget_snapshot(&self.dir)
    }

    fn host(&self) -> std::sync::MutexGuard<'_, Host> {
        self.host.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn touch(&self) {
        *self.last_active.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = Instant::now();
        self.wake.notify_all();
    }
}

/// Sends a card to the widget host. A failure is dropped: the next poll sends a newer card anyway.
fn send(update: Update) {
    let result = (|| -> windows::core::Result<()> {
        let options = WidgetUpdateRequestOptions::CreateInstance(&HSTRING::from(&update.id))?;
        options.SetTemplate(&HSTRING::from(update.card.to_string()))?;
        options.SetData(&HSTRING::from("{}"))?;
        options.SetCustomState(&HSTRING::from(&update.custom_state))?;
        WidgetManager::GetDefault()?.UpdateWidget(&options)
    })();
    if let Err(err) = result {
        eprintln!("codexbar: widget update failed: {}", err.message());
    }
}

/// Starts CodexBar's window, as a separate process so it outlives the provider.
fn open_codexbar() {
    if let Ok(exe) = std::env::current_exe() {
        let _ = std::process::Command::new(exe).spawn();
    }
}

#[implement(IWidgetProvider, IWidgetProvider2)]
struct Provider(Arc<Shared>);

impl IWidgetProvider_Impl for Provider_Impl {
    fn CreateWidget(&self, context: Ref<WidgetContext>) -> windows::core::Result<()> {
        let context = context.ok()?;
        let (id, definition, widget_size) = (context.Id()?, context.DefinitionId()?, context.Size()?);
        let update = self.0.host().create(
            &id.to_string(),
            &definition.to_string(),
            size(widget_size),
            &self.0.snapshot(),
            Utc::now(),
        );
        self.0.touch();
        update.into_iter().for_each(send);
        Ok(())
    }

    fn DeleteWidget(&self, id: &HSTRING, _custom_state: &HSTRING) -> windows::core::Result<()> {
        self.0.host().delete(&id.to_string());
        self.0.touch();
        Ok(())
    }

    fn OnActionInvoked(&self, args: Ref<WidgetActionInvokedArgs>) -> windows::core::Result<()> {
        let args = args.ok()?;
        let id = args.WidgetContext()?.Id()?.to_string();
        let (verb, data) = (args.Verb()?.to_string(), args.Data()?.to_string());
        let outcome = self.0.host().action(&id, &verb, &data, &self.0.snapshot(), Utc::now());
        match outcome {
            Outcome::Nothing => {}
            Outcome::Update(update) => send(update),
            Outcome::Open => open_codexbar(),
        }
        Ok(())
    }

    fn OnWidgetContextChanged(&self, args: Ref<WidgetContextChangedArgs>) -> windows::core::Result<()> {
        let context = args.ok()?.WidgetContext()?;
        let update = self.0.host().resize(
            &context.Id()?.to_string(),
            size(context.Size()?),
            &self.0.snapshot(),
            Utc::now(),
        );
        update.into_iter().for_each(send);
        Ok(())
    }

    fn Activate(&self, context: Ref<WidgetContext>) -> windows::core::Result<()> {
        let context = context.ok()?;
        let update = self.0.host().activate(
            &context.Id()?.to_string(),
            size(context.Size()?),
            &self.0.snapshot(),
            Utc::now(),
        );
        self.0.touch();
        update.into_iter().for_each(send);
        Ok(())
    }

    fn Deactivate(&self, id: &HSTRING) -> windows::core::Result<()> {
        self.0.host().deactivate(&id.to_string());
        self.0.touch();
        Ok(())
    }
}

impl IWidgetProvider2_Impl for Provider_Impl {
    fn OnCustomizationRequested(&self, args: Ref<WidgetCustomizationRequestedArgs>) -> windows::core::Result<()> {
        let id = args.ok()?.WidgetContext()?.Id()?.to_string();
        let update = self.0.host().customize(&id, &self.0.snapshot(), Utc::now());
        update.into_iter().for_each(send);
        Ok(())
    }
}

/// Hands out the one provider object, whatever the host asks for.
#[implement(IClassFactory)]
struct Factory(IWidgetProvider);

impl IClassFactory_Impl for Factory_Impl {
    fn CreateInstance(
        &self,
        outer: Ref<IUnknown>,
        iid: *const GUID,
        object: *mut *mut core::ffi::c_void,
    ) -> windows::core::Result<()> {
        if outer.is_some() {
            return Err(CLASS_E_NOAGGREGATION.into());
        }
        // SAFETY: COM passes a valid interface id and out-pointer.
        unsafe { self.0.query(iid, object).ok() }
    }

    fn LockServer(&self, _lock: BOOL) -> windows::core::Result<()> {
        Ok(())
    }
}

/// The widgets the host already has, after the provider was restarted, with their saved settings.
fn restore(shared: &Shared) -> windows::core::Result<()> {
    for info in WidgetManager::GetDefault()?.GetWidgetInfos()?.iter().flatten() {
        let context = info.WidgetContext()?;
        shared.host().restore(
            &context.Id()?.to_string(),
            &context.DefinitionId()?.to_string(),
            size(context.Size()?),
            &info.CustomState()?.to_string(),
            context.IsActive()?,
        );
    }
    Ok(())
}

/// Runs the COM server until no widget has been on screen for a while.
pub fn serve() -> windows::core::Result<()> {
    // SAFETY: the process's main thread joins the multithreaded apartment once, before any COM use.
    unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.ok()?;
    let shared = Arc::new(Shared {
        dir: codexbar_store::settings::Settings::default_dir(),
        host: Mutex::new(Host::default()),
        wake: Condvar::new(),
        last_active: Mutex::new(Instant::now()),
    });
    if let Err(err) = restore(&shared) {
        eprintln!("codexbar: couldn't list pinned widgets: {}", err.message());
    }
    let provider: IWidgetProvider = Provider(shared.clone()).into();
    let factory: IClassFactory = Factory(provider).into();
    // SAFETY: a valid class id and factory; the registration is revoked before the factory is dropped.
    let cookie = unsafe { CoRegisterClassObject(&CLSID, &factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE) }?;

    let modified = || {
        std::fs::metadata(shared.dir.join(codexbar_store::widgets::WIDGETS_FILE))
            .and_then(|m| m.modified())
            .ok()
    };
    let mut seen: Option<SystemTime> = modified();
    let mut drawn = Instant::now();
    loop {
        {
            let host = shared.host();
            let _ = shared.wake.wait_timeout(host, POLL);
        }
        let changed = modified();
        let active = shared.host().any_active();
        if active {
            *shared
                .last_active
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner()) = Instant::now();
            if changed != seen || drawn.elapsed() >= REDRAW {
                let updates = shared.host().refresh(&shared.snapshot(), Utc::now());
                updates.into_iter().for_each(send);
                drawn = Instant::now();
            }
        }
        seen = changed;
        let idle = shared
            .last_active
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .elapsed();
        if !active && (idle >= IDLE_EXIT || (idle >= EMPTY_EXIT && shared.host().is_empty())) {
            break;
        }
    }
    // SAFETY: the cookie came from CoRegisterClassObject above.
    unsafe { CoRevokeClassObject(cookie) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_manifest_declares_the_class_and_definition() {
        let manifest = include_str!("../../../../packaging/AppxManifest.xml").to_lowercase();
        let clsid = format!("{CLSID:?}").to_lowercase();
        assert_eq!(
            manifest.matches(&clsid).count(),
            2,
            "com:Class and CreateInstance ({clsid})"
        );
        assert!(manifest.contains(&format!(r#"arguments="{}""#, SERVER_ARG.to_lowercase())));
        assert!(manifest.contains(&format!(r#"<definition id="{}""#, host::DEFINITION.to_lowercase())));
    }

    #[test]
    fn widget_sizes_map_to_card_sizes() {
        assert_eq!(size(WidgetSize::Small), cards::Size::Small);
        assert_eq!(size(WidgetSize::Medium), cards::Size::Medium);
        assert_eq!(size(WidgetSize::Large), cards::Size::Large);
    }
}
