# Launch CodexBar in the system tray.
$ErrorActionPreference = 'Stop'

$projectDir = Join-Path $PSScriptRoot 'src\CodexBar.App'
$project = Join-Path $projectDir 'CodexBar.App.csproj'
$artifactsRoot = Join-Path $env:LOCALAPPDATA 'CodexBar\launcher-artifacts'
$artifactsDir = Join-Path $artifactsRoot 'current'
$defaultGitHubConfigDir = Join-Path $env:USERPROFILE '.gh-work'

# Kill any existing instance so the DLL isn't locked during build.
$existingProcesses = @(Get-Process -Name 'CodexBar.App' -ErrorAction SilentlyContinue)
if ($existingProcesses.Count -gt 0) {
    $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $existingProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
}

# Clear stale WPF temp projects so directory-based tooling stays unambiguous.
Get-ChildItem -LiteralPath $projectDir -Filter '*_wpftmp.csproj' -File |
    Remove-Item -Force

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

# Clean up older timestamped launcher builds without failing the launch if Windows still has a handle open.
Get-ChildItem -LiteralPath $artifactsRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $artifactsDir } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -Skip 2 |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

if (Test-Path -LiteralPath $artifactsDir) {
    Remove-Item -LiteralPath $artifactsDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Build so errors are visible in the terminal.
Write-Information 'Building CodexBar...' -InformationAction Continue
dotnet build $project --verbosity quiet --artifacts-path $artifactsDir /nr:false /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Launch detached; the app lives in the system tray, not the terminal.
$exe = Get-ChildItem -LiteralPath $artifactsDir -Filter 'CodexBar.App.exe' -Recurse -File |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) {
    throw "Could not find CodexBar.App.exe under $artifactsDir."
}

$processStartInfo = [System.Diagnostics.ProcessStartInfo]::new($exe)
$processStartInfo.UseShellExecute = $false
$processStartInfo.WorkingDirectory = Split-Path -Parent $exe

# Keep terminal-specific GitHub auth overrides from leaking into CodexBar refreshes.
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
