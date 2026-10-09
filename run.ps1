# Build CodexBar (Rust) and launch it in the system tray.
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'CodexBar\bin'
$installedExe = Join-Path $installDir 'codexbar.exe'
$builtExe = Join-Path $PSScriptRoot 'target\release\codexbar.exe'
$defaultGitHubConfigDir = Join-Path $env:USERPROFILE '.gh-work'

# Build first, so a failed build leaves the running instance alone.
Write-Information 'Building CodexBar...' -InformationAction Continue
Push-Location $PSScriptRoot
try {
    cargo build --release --locked -p codexbar-app
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

# Stop any running instance, including the retired WPF app, so only one tray icon remains and the copy isn't locked.
$existingProcesses = @(Get-Process -Name 'codexbar', 'CodexBar.App' -ErrorAction SilentlyContinue)
if ($existingProcesses.Count -gt 0) {
    $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $existingProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
}

# Run a copy, so the next build can replace target\release\codexbar.exe while CodexBar is running.
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $builtExe -Destination $installedExe -Force

$processStartInfo = [System.Diagnostics.ProcessStartInfo]::new($installedExe)
$processStartInfo.UseShellExecute = $false
$processStartInfo.WorkingDirectory = $installDir

# Keep terminal-specific GitHub auth overrides from leaking into CodexBar's gh calls.
@(
    'GH_TOKEN',
    'GITHUB_TOKEN',
    'GH_ENTERPRISE_TOKEN',
    'GITHUB_ENTERPRISE_TOKEN'
) | ForEach-Object {
    $null = $processStartInfo.Environment.Remove($_)
}

if (Test-Path -LiteralPath $defaultGitHubConfigDir) {
    $processStartInfo.Environment['GH_CONFIG_DIR'] = $defaultGitHubConfigDir
}

$null = [System.Diagnostics.Process]::Start($processStartInfo)
Write-Information 'CodexBar is running in the system tray.' -InformationAction Continue
