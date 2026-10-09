# Build CodexBar (Rust) and launch it in the system tray.
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'CodexBar\bin'
$installedExe = Join-Path $installDir 'codexbar.exe'
$defaultGitHubConfigDir = Join-Path $env:USERPROFILE '.gh-work'

# Build first, so a failed build leaves the running instance alone. Cargo reports where it wrote the executable, which
# follows any configured target directory or target triple.
Write-Information 'Building CodexBar...' -InformationAction Continue
$builtExe = $null
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
}
if (-not $builtExe) {
    throw 'Cargo did not report the codexbar executable.'
}

# Stop the running app, and the retired WPF app, so only one tray icon remains and the copy isn't locked. Demo
# instances (--demo) are left running; they have their own single-instance lock.
$existingProcesses = @(
    Get-CimInstance Win32_Process -Filter "Name = 'codexbar.exe' OR Name = 'CodexBar.App.exe'" |
        Where-Object { $_.CommandLine -notmatch '(^|\s)--demo(\s|$)' } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
)
if ($existingProcesses.Count -gt 0) {
    $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $existingProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
}

# Run a copy, so the next build can replace the built executable while CodexBar is running.
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $builtExe -Destination $installedExe -Force

# The WPF app's "Start with Windows" wrote a CodexBar value to the Run key. Keep that choice, pointed at this app.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -LiteralPath $runKey -Name 'CodexBar' -ErrorAction SilentlyContinue) {
    Set-ItemProperty -LiteralPath $runKey -Name 'CodexBar' -Value "`"$installedExe`""
}

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
