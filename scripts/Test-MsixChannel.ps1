<#
.SYNOPSIS
Verifies the MSIX release procedures end to end: clean install, update check, upgrade, rollback and uninstall (#93).

.DESCRIPTION
Publishes two builds to a throwaway channel with package.ps1, installs the first from the App Installer file, asks the
installed app (codexbar --package-status, inside its package) for its version and update check, upgrades, rolls
back and uninstalls. CI runs it on a clean runner. It refuses to run where CodexBar is already installed, because it
would replace and then remove that installation. The first run trusts the signing certificate (administrator
approval).
#>
[CmdletBinding()]
param(
    [string]$Channel = (Join-Path ([IO.Path]::GetTempPath()) 'codexbar-channel-test')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$package = Join-Path $root 'package.ps1'
$name = 'HemSoft.CodexBar'

if (Get-AppxPackage -Name $name) {
    throw "CodexBar is installed on this PC; this test would replace and remove it. Run it on a clean machine or in CI."
}

function Assert-Installed([string]$Expected) {
    $installed = Get-AppxPackage -Name $name
    $actual = if ($installed) { $installed.Version } else { '(none)' }
    if ($actual -ne $Expected) { throw "Expected CodexBar $Expected installed, found $actual." }
    Write-Information "OK: CodexBar $Expected is installed." -InformationAction Continue
}

# Runs codexbar --package-status inside the installed package and returns what it wrote.
function Get-PackageStatus {
    $installed = Get-AppxPackage -Name $name
    # Outside AppData, which Windows virtualizes for packaged processes.
    $folder = Join-Path $root 'target\msix'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $file = Join-Path $folder "status-$([guid]::NewGuid()).json"
    $exe = Join-Path $installed.InstallLocation 'codexbar.exe'
    Invoke-CommandInDesktopPackage -PackageFamilyName $installed.PackageFamilyName -AppId 'CodexBar' `
        -Command $exe -Args "--package-status `"$file`""
    $deadline = (Get-Date).AddMinutes(2)
    while (-not (Test-Path -LiteralPath $file)) {
        if ((Get-Date) -gt $deadline) { throw 'codexbar --package-status wrote nothing within 2 minutes.' }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 500
    $status = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath $file -Force
    $status
}

if (Test-Path -LiteralPath $Channel) { Remove-Item -LiteralPath $Channel -Recurse -Force }
try {
    # Clean install from the App Installer file.
    & $package -Channel $Channel -Revision 1 -Trust -Install
    Assert-Installed '0.1.0.1'
    $status = Get-PackageStatus
    if (-not $status.packaged -or $status.version -ne '0.1.0.1' -or -not $status.channel) {
        throw "The installed app reports $($status | ConvertTo-Json -Compress)."
    }
    if ($status.updateAvailable) { throw "No update was published yet, but the app reports: $($status.update)" }
    Write-Information "OK: the app runs from its package with the channel $($status.channel): $($status.update)" -InformationAction Continue

    # A newer build in the channel: the app's own update check sees it, and the App Installer file installs it.
    & $package -Channel $Channel -Revision 2
    $status = Get-PackageStatus
    if (-not $status.updateAvailable) { throw "The app didn't see the published update: $($status.update)" }
    Write-Information "OK: the app sees the update: $($status.update)" -InformationAction Continue
    Add-AppxPackage -AppInstallerFile (Join-Path $Channel 'CodexBar.appinstaller') -ForceApplicationShutdown
    Assert-Installed '0.1.0.2'

    # Rollback to the earlier build in the channel.
    & $package -Channel $Channel -Rollback '0.1.0.1' -Install
    Assert-Installed '0.1.0.1'
    $status = Get-PackageStatus
    if ($status.updateAvailable) { throw "After the rollback the channel points at this version, but: $($status.update)" }
    Write-Information 'OK: rolled back, and the channel no longer offers the newer build.' -InformationAction Continue
}
finally {
    Get-AppxPackage -Name $name | Remove-AppxPackage
    if (Test-Path -LiteralPath $Channel) { Remove-Item -LiteralPath $Channel -Recurse -Force }
}
if (Get-AppxPackage -Name $name) { throw 'CodexBar is still installed after Remove-AppxPackage.' }
Write-Information 'OK: uninstalled.' -InformationAction Continue
