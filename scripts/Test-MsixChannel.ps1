<#
.SYNOPSIS
Verifies the MSIX release procedures end to end: clean install, update check, upgrade, rollback and uninstall (#93).

.DESCRIPTION
Publishes two builds to a throwaway channel with package.ps1, installs the first from the App Installer file, asks the
installed app (codexbar --package-status, inside its package) for its version and update check, upgrades, rolls
back and uninstalls. CI runs it on a clean runner. It refuses to run where CodexBar is already installed, because it
would replace and then remove that installation. It runs as administrator, trusts the signing certificate for the
test, and afterwards removes the trust and any signing certificate it created.
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

# The versions package.ps1 gives revisions 1 and 2: Cargo's version with the major plus one.
$cargo = Get-Content -LiteralPath (Join-Path $root 'Cargo.toml') -Raw
if ($cargo -notmatch '(?ms)^\[workspace\.package\].*?^version\s*=\s*"(\d+)\.(\d+)\.(\d+)"') { throw 'No workspace version.' }
$base = "$([int]$Matches[1] + 1).$($Matches[2]).$($Matches[3])"
$first = "$base.1"
$second = "$base.2"

if (Get-AppxPackage -Name $name) {
    throw "CodexBar is installed on this PC; this test would replace and remove it. Run it on a clean machine or in CI."
}
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this test as administrator: it trusts the signing certificate and removes that trust afterwards.'
}

# What the test adds to the certificate stores, so the cleanup removes only that.
$publisher = 'CN=HemSoft CodexBar Self-Signed'
$signingBefore = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $publisher | ForEach-Object Thumbprint)
$trustedBefore = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq $publisher | ForEach-Object Thumbprint)

function Assert-Installed([string]$Expected) {
    $installed = Get-AppxPackage -Name $name
    $actual = if ($installed) { $installed.Version } else { '(none)' }
    if ($actual -ne $Expected) { throw "Expected CodexBar $Expected installed, found $actual." }
    Write-Information "OK: CodexBar $Expected is installed." -InformationAction Continue
}

# Runs codexbar --package-status inside the installed package and returns what it wrote.
# Ends every CodexBar process from the package, so a stuck one can't hold up the uninstall.
function Stop-CodexBar {
    Get-CimInstance Win32_Process -Filter "Name = 'codexbar.exe'" |
        Where-Object { $_.ExecutablePath -match '\\WindowsApps\\' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# What Windows was doing when a step got stuck: the package, CodexBar's processes and recent deployment events.
function Write-Diagnostics([string]$File) {
    Write-Information '--- diagnostics ---' -InformationAction Continue
    Get-AppxPackage -Name $name | Format-List Name, Version, Status, InstallLocation | Out-Host
    Get-CimInstance Win32_Process -Filter "Name = 'codexbar.exe'" | Format-Table ProcessId, CommandLine -AutoSize -Wrap | Out-Host
    Get-WinEvent -LogName 'Microsoft-Windows-AppXDeploymentServer/Operational' -MaxEvents 25 -ErrorAction SilentlyContinue |
        Format-Table TimeCreated, Id, LevelDisplayName, Message -AutoSize -Wrap | Out-Host
    Get-WinEvent -LogName 'Microsoft-Windows-AppxPackaging/Operational' -MaxEvents 10 -ErrorAction SilentlyContinue |
        Format-Table TimeCreated, Id, Message -AutoSize -Wrap | Out-Host
}

function Get-PackageStatus {
    $installed = Get-AppxPackage -Name $name
    # Outside AppData, which Windows virtualizes for packaged processes.
    $folder = Join-Path $root 'target\msix'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $file = Join-Path $folder "status-$([guid]::NewGuid()).json"
    $exe = Join-Path $installed.InstallLocation 'codexbar.exe'
    # Invoke-CommandInDesktopPackage can wait for the process, so it runs in a job and the file decides.
    $job = Start-ThreadJob -ArgumentList $installed.PackageFamilyName, $exe, $file -ScriptBlock {
        param($family, $exe, $file)
        Invoke-CommandInDesktopPackage -PackageFamilyName $family -AppId 'CodexBar' -Command $exe -Args "--package-status `"$file`""
    }
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (Test-Path -LiteralPath $file)) {
        if ((Get-Date) -gt $deadline) {
            Write-Diagnostics $file
            $job | Receive-Job -ErrorAction SilentlyContinue | Out-Host
            Stop-CodexBar
            $started = Test-Path -LiteralPath ([IO.Path]::ChangeExtension($file, 'started'))
            throw "codexbar --package-status wrote nothing within 3 minutes (process started: $started)."
        }
        Start-Sleep -Milliseconds 500
    }
    $job | Stop-Job -PassThru | Remove-Job -Force
    Start-Sleep -Milliseconds 500
    $status = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath $file -Force
    $status
}

if (Test-Path -LiteralPath $Channel) { Remove-Item -LiteralPath $Channel -Recurse -Force }
try {
    # Clean install from the App Installer file.
    & $package -Channel $Channel -Revision 1 -Trust -Install
    Assert-Installed $first
    $status = Get-PackageStatus
    if (-not $status.packaged -or $status.version -ne $first -or -not $status.channel) {
        throw "The installed app reports $($status | ConvertTo-Json -Compress)."
    }
    if ($status.updateAvailable) { throw "No update was published yet, but the app reports: $($status.update)" }
    Write-Information "OK: the app runs from its package with the channel $($status.channel): $($status.update)" -InformationAction Continue

    # A newer build in the channel: the app's own update check sees it, and the App Installer file installs it.
    & $package -Channel $Channel -Revision 2
    $status = Get-PackageStatus
    if (-not $status.updateAvailable) { throw "The app didn't see the published update: $($status.update)" }
    Write-Information "OK: the app sees the update: $($status.update)" -InformationAction Continue
    Add-AppxPackage -AppInstallerFile (Join-Path $Channel 'CodexBar.appinstaller') -ForceTargetApplicationShutdown
    Assert-Installed $second

    # Rollback to the earlier build in the channel.
    & $package -Channel $Channel -Rollback $first -Install
    Assert-Installed $first
    $status = Get-PackageStatus
    if (-not $status.channel) { throw 'After the rollback the package lost its update channel.' }
    if ($status.updateAvailable) { throw "After the rollback the channel points at this version, but: $($status.update)" }
    Write-Information 'OK: rolled back through the channel, which no longer offers the newer build.' -InformationAction Continue
}
finally {
    Stop-CodexBar
    # Bounded too: a deployment Windows still runs for the package can hold the removal up.
    $removal = Start-ThreadJob -ArgumentList $name -ScriptBlock { param($name) Get-AppxPackage -Name $name | Remove-AppxPackage }
    if (-not (Wait-Job $removal -Timeout 300)) {
        Write-Diagnostics ''
        $removal | Stop-Job
    }
    $removal | Receive-Job -ErrorAction Continue
    $removal | Remove-Job -Force
    if (Test-Path -LiteralPath $Channel) { Remove-Item -LiteralPath $Channel -Recurse -Force }
    Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Subject -eq $publisher -and $_.Thumbprint -notin $trustedBefore } |
        Remove-Item
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher -and $_.Thumbprint -notin $signingBefore } |
        Remove-Item
}
if (Get-AppxPackage -Name $name) { throw 'CodexBar is still installed after Remove-AppxPackage.' }
Write-Information 'OK: uninstalled.' -InformationAction Continue
