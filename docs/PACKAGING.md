# Packaging, updates and rollback

CodexBar ships as an MSIX package (#93). `package.ps1` builds the release
executable, packs it with [`packaging/AppxManifest.xml`](../packaging/AppxManifest.xml),
signs it and publishes it to a **channel folder** together with an App Installer
file (`CodexBar.appinstaller`). Windows installs from that file and checks it for
updates every time CodexBar starts.

## Signing

Packages are signed with a **self-signed certificate**,
`CN=HemSoft CodexBar Self-Signed`. `package.ps1` creates it in your certificate
store (`Cert:\CurrentUser\My`) on first use. Its private key can't be exported,
so nothing secret is written to disk or to the channel. The channel gets only
the public certificate (`CodexBar.cer`).

Windows installs a self-signed package only when its certificate is trusted in
the local machine's **Trusted People** store. `.\package.ps1 -Trust` adds it
there. That needs administrator approval once per PC (Windows shows a prompt).
This trust applies to all users of the PC; remove it when you no longer need it
(see [Uninstall](#uninstall)).

A public release later needs a certificate that every PC already trusts. The
options are the Microsoft Store (free, and it signs the package for you), Azure
Artifact Signing, or an OV code-signing certificate. That decision is open and
belongs to the maintainer. When it is made, the manifest's `Publisher` changes
to the new certificate's subject. Windows treats a new publisher as a different
app, so the self-signed installation is uninstalled once and the new one
installed.

## Versions

The package version is the workspace version in `Cargo.toml` plus the commit
count, for example `0.1.0.312`. Use `-Revision <n>` to override the commit count.
The build ID, the commit with `-modified` when the working tree had uncommitted
changes, appears under **Settings › About › Build**.

`package.ps1` refuses to publish a version the channel already has, because
Windows wouldn't install changed contents under the same version. Commit first,
or pass a higher `-Revision`.

## Procedures

Every command runs from the repository root. The default channel is
`%LOCALAPPDATA%\CodexBar\channel`; pass `-Channel <folder>` to use another
folder, such as a network share other PCs can read.

| Task | Command |
| --- | --- |
| First install on a PC | `.\package.ps1 -Trust -Install` |
| Publish an update | `.\package.ps1` |
| Publish and install an update now | `.\package.ps1 -Install` |
| Install a published update now | **Settings › About › Install and restart**, or `Add-AppxPackage -AppInstallerFile <channel>\CodexBar.appinstaller` |
| Roll back | `.\package.ps1 -Rollback 0.1.0.311 -Install` |
| Install on another PC | `Import-Certificate -FilePath <channel>\CodexBar.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople` as administrator, then `Add-AppxPackage -AppInstallerFile <channel>\CodexBar.appinstaller` |

**Clean install.** `-Trust` trusts the certificate. `-Install` installs from the
App Installer file, which records the channel, so later updates find it. Start
CodexBar from the Start menu. It uses the same settings, keys and history as the
build from source (`%USERPROFILE%\.codexbar` and Credential Manager), and only
one of the two runs at a time. Quit the `run.ps1` copy before starting the
package.

**Updates.** Publishing puts the new package next to the old ones and points the
App Installer file at it. Windows checks the file when CodexBar starts (and in
the background about every 8 hours), then installs the update. CodexBar also
checks quietly 30 seconds after it starts and once a day after that, and shows
the result under **Settings › About › Updates**. When an update is waiting,
**Install and restart** installs it and Windows starts CodexBar again. A failed
check shows Windows' reason and can be retried with **Check now**. A missing or
unreadable channel never stops CodexBar from starting.

**Rollback.** `-Rollback <version>` points the App Installer file at an earlier
package that is still in the channel folder. Nothing is rebuilt. The file allows
moving to any version, so every installation goes back the next time CodexBar
starts; `-Install` does it right away on this PC. To move forward again, publish
a newer build. Keep old packages in the channel for as long as you might roll
back to them.

## Uninstall

1. Quit CodexBar from the tray menu.
2. Remove the app: **Settings › Apps › Installed apps › CodexBar › Uninstall**,
   or `Get-AppxPackage HemSoft.CodexBar | Remove-AppxPackage`.
3. Remove the trust, as administrator:
   `Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=HemSoft CodexBar Self-Signed' | Remove-Item`.
4. Optionally remove the signing certificate:
   `Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=HemSoft CodexBar Self-Signed' | Remove-Item`.
   Then delete the channel folder.

Settings, keys and history stay. The README's uninstall steps remove them.

## Verification

[`scripts/Test-MsixChannel.ps1`](../scripts/Test-MsixChannel.ps1) runs these
procedures end to end in a throwaway channel:

1. Clean install from the App Installer file.
2. The installed app reports its version and channel (`codexbar --package-status`,
   run inside the package).
3. A published build is seen by the app's own update check, then installed.
4. Rollback.
5. Uninstall.

The **Package** workflow runs it on a clean Windows runner for every pull request
that touches packaging. It refuses to run on a PC where CodexBar is installed,
because it would replace and then remove that installation.
