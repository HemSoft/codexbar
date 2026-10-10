<#
.SYNOPSIS
Builds CodexBar as a signed MSIX package and publishes it to a local App Installer channel (#93).

.DESCRIPTION
Each run builds the release executable, packs it with packaging\AppxManifest.xml, signs the package with a
self-signed certificate kept in your certificate store (created on first use, private key not exportable), and writes
the package plus CodexBar.appinstaller into the channel folder. Windows checks that file for updates when CodexBar
starts, so publishing a newer build updates every installation from the channel.

The package version is the workspace version from Cargo.toml plus the commit count, for example 0.1.0.312. Commit
before publishing again, or pass -Revision.

Windows installs a self-signed package only after its certificate is trusted on the PC: -Trust adds it to the local
machine's Trusted People store, which asks once for administrator approval. See docs\PACKAGING.md.

.PARAMETER Channel
The channel folder. Default: %LOCALAPPDATA%\CodexBar\channel.

.PARAMETER Revision
The fourth version part instead of the commit count.

.PARAMETER Trust
Trusts the signing certificate on this PC (administrator approval), so Windows installs the package.

.PARAMETER Install
Installs or updates CodexBar from the channel after publishing.

.PARAMETER Rollback
Points the channel at an earlier version already in the channel folder instead of building. Installations move back
to it the next time CodexBar starts (or right away with -Install).

.EXAMPLE
.\package.ps1 -Trust -Install

.EXAMPLE
.\package.ps1 -Rollback 0.1.0.311 -Install
#>
[CmdletBinding()]
param(
    [string]$Channel = (Join-Path $env:LOCALAPPDATA 'CodexBar\channel'),
    [ValidateRange(0, 65535)]
    [int]$Revision = -1,
    [switch]$Trust,
    [switch]$Install,
    [string]$Rollback
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The manifest's Publisher must equal the signing certificate's subject.
$publisher = 'CN=HemSoft CodexBar Self-Signed'
$packageName = 'HemSoft.CodexBar'

function Find-SdkTool([string]$Name) {
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools')
    )
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        # The newest SDK first: tools sit in ...\10.0.<build>.0\x64\.
        $tool = Get-ChildItem -LiteralPath $root -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } |
            Sort-Object { if ($_.FullName -match '\\(10\.\d+\.\d+\.\d+)\\x64\\') { [version]$Matches[1] } else { [version]'0.0' } } -Descending |
            Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    throw "$Name wasn't found. Install the Windows SDK (winget install Microsoft.WindowsSDK.10.0.26100)."
}

function Invoke-Tool([string]$Path, [string[]]$Arguments) {
    $output = & $Path @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $Path) failed with exit code ${LASTEXITCODE}:`n$output" }
    Write-Verbose $output
}

# Draws CodexBar's logo (the tray icon's three gold bars on black) at any size; non-square images center it.
function New-Logo([string]$Path, [int]$Width, [int]$Height) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $side = [Math]::Min($Width, $Height)
        $scale = $side / 32.0
        $left = ($Width - $side) / 2.0
        $top = ($Height - $side) / 2.0
        $radius = 6 * $scale
        $square = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $square.AddArc($left, $top, 2 * $radius, 2 * $radius, 180, 90)
        $square.AddArc($left + $side - 2 * $radius, $top, 2 * $radius, 2 * $radius, 270, 90)
        $square.AddArc($left + $side - 2 * $radius, $top + $side - 2 * $radius, 2 * $radius, 2 * $radius, 0, 90)
        $square.AddArc($left, $top + $side - 2 * $radius, 2 * $radius, 2 * $radius, 90, 90)
        $square.CloseFigure()
        $graphics.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(0x0A, 0x0A, 0x0A)), $square)
        $gold = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(0xD4, 0xAF, 0x37))
        $track = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(0x2A, 0x2A, 0x2A))
        foreach ($bar in @(@(6, 26), @(14, 20), @(22, 14))) {
            $y = $top + $bar[0] * $scale
            $graphics.FillRectangle($track, $left + 5 * $scale, $y, 22 * $scale, 4 * $scale)
            $graphics.FillRectangle($gold, $left + 5 * $scale, $y, ($bar[1] - 5) * $scale, 4 * $scale)
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-SigningCertificate {
    $existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($existing) { return $existing }
    Write-Information "Creating the self-signed signing certificate '$publisher' in your certificate store..." -InformationAction Continue
    New-SelfSignedCertificate -Type Custom -Subject $publisher -FriendlyName 'CodexBar package signing (self-signed)' `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

function Test-Elevated {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Windows trusts a self-signed package only through the local machine's Trusted People (or root) store.
function Grant-Trust($Certificate, [string]$CerPath) {
    if (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $Certificate.Thumbprint) {
        return
    }
    if (Test-Elevated) {
        Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        return
    }
    Write-Information 'Windows asks for administrator approval to trust the CodexBar certificate on this PC...' -InformationAction Continue
    $quoted = $CerPath -replace "'", "''"
    $command = "Import-Certificate -FilePath '$quoted' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $shell = (Get-Process -Id $PID).Path
    $process = Start-Process -FilePath $shell -Verb RunAs -Wait -PassThru -ArgumentList "-NoProfile -EncodedCommand $encoded"
    if ($process.ExitCode -ne 0 -or -not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $Certificate.Thumbprint)) {
        throw 'The certificate was not trusted.'
    }
}

function ConvertTo-FileUri([string]$Path) {
    [System.Security.SecurityElement]::Escape(([uri]$Path).AbsoluteUri)
}

# The App Installer file Windows checks at each start. Its own version is the publishing time, so a rollback (which
# points at a lower package version) still reads as the newer file; ForceUpdateFromAnyVersion lets it move back.
function Write-AppInstaller([string]$Version, [string]$PackageFile) {
    $now = Get-Date
    $fileVersion = '1.{0}.{1}.{2}' -f $now.Year, ($now.Month * 100 + $now.Day), ($now.Hour * 100 + $now.Minute)
    $appInstaller = Join-Path $Channel 'CodexBar.appinstaller'
    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018" Version="$fileVersion" Uri="$(ConvertTo-FileUri $appInstaller)">
  <MainPackage Name="$packageName" Publisher="$publisher" Version="$Version" ProcessorArchitecture="x64" Uri="$(ConvertTo-FileUri $PackageFile)" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="0" />
    <AutomaticBackgroundTask />
    <ForceUpdateFromAnyVersion>true</ForceUpdateFromAnyVersion>
  </UpdateSettings>
</AppInstaller>
"@
    [IO.File]::WriteAllText($appInstaller, $content, [Text.UTF8Encoding]::new($false))
    $appInstaller
}

New-Item -ItemType Directory -Path $Channel -Force | Out-Null
$certificate = Get-SigningCertificate
$cerPath = Join-Path $Channel 'CodexBar.cer'
Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null

if ($Rollback) {
    $version = $Rollback
    $packageFile = Join-Path $Channel "CodexBar_${version}_x64.msix"
    if (-not (Test-Path -LiteralPath $packageFile)) {
        throw "The channel has no CodexBar $version ($packageFile)."
    }
}
else {
    $cargo = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Cargo.toml') -Raw
    if ($cargo -notmatch '(?ms)^\[workspace\.package\].*?^version\s*=\s*"(\d+)\.(\d+)\.(\d+)"') {
        throw 'Cargo.toml has no [workspace.package] version.'
    }
    $base = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
    if ($Revision -lt 0) {
        $Revision = [int](git -C $PSScriptRoot rev-list --count HEAD)
        if ($LASTEXITCODE -ne 0) { throw 'git rev-list failed; pass -Revision.' }
    }
    $version = "$base.$Revision"
    $packageFile = Join-Path $Channel "CodexBar_${version}_x64.msix"
    if (Test-Path -LiteralPath $packageFile) {
        throw "The channel already has CodexBar $version. Commit your changes or pass -Revision with a higher number."
    }

    $commit = (git -C $PSScriptRoot rev-parse --short HEAD)
    if ($LASTEXITCODE -ne 0) { $commit = 'unknown' }
    if (git -C $PSScriptRoot status --porcelain --untracked-files=no) { $commit = "$commit-modified" }

    Write-Information "Building CodexBar $version ($commit)..." -InformationAction Continue
    $builtExe = $null
    $env:CODEXBAR_BUILD = $commit
    Push-Location $PSScriptRoot
    try {
        cargo build --release --locked -p codexbar-app --message-format=json-render-diagnostics | ForEach-Object {
            try { $message = $_ | ConvertFrom-Json } catch { return }
            if ($message.reason -eq 'compiler-artifact' -and $message.target.name -eq 'codexbar' -and $message.executable) {
                $builtExe = $message.executable
            }
        }
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally {
        Pop-Location
        Remove-Item Env:\CODEXBAR_BUILD -ErrorAction SilentlyContinue
    }
    if (-not $builtExe) { throw 'Cargo did not report the codexbar executable.' }

    $layout = Join-Path $PSScriptRoot 'target\msix\layout'
    if (Test-Path -LiteralPath $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null
    Copy-Item -LiteralPath $builtExe -Destination (Join-Path $layout 'codexbar.exe')
    $manifest = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging\AppxManifest.xml') -Raw).
        Replace('{{VERSION}}', $version).Replace('{{PUBLISHER}}', $publisher)
    [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
    New-Logo (Join-Path $layout 'Assets\Square44x44Logo.png') 44 44
    New-Logo (Join-Path $layout 'Assets\Square150x150Logo.png') 150 150
    New-Logo (Join-Path $layout 'Assets\Wide310x150Logo.png') 310 150
    New-Logo (Join-Path $layout 'Assets\StoreLogo.png') 50 50

    $unsigned = Join-Path $PSScriptRoot 'target\msix\CodexBar.msix'
    Invoke-Tool (Find-SdkTool 'makeappx.exe') @('pack', '/d', $layout, '/p', $unsigned, '/o')
    Invoke-Tool (Find-SdkTool 'signtool.exe') @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My', $unsigned)
    Move-Item -LiteralPath $unsigned -Destination $packageFile
}

$appInstaller = Write-AppInstaller $version $packageFile
Write-Information "Published CodexBar $version to $Channel." -InformationAction Continue

if ($Trust) {
    Grant-Trust $certificate $cerPath
}

if ($Install) {
    if ($Rollback) {
        Add-AppxPackage -Path $packageFile -ForceUpdateFromAnyVersion -ForceApplicationShutdown
    }
    else {
        Add-AppxPackage -AppInstallerFile $appInstaller -ForceApplicationShutdown
    }
    Write-Information "Installed CodexBar $version. Start it from the Start menu." -InformationAction Continue
}
