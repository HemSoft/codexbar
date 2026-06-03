# Launch CodexBar in the system tray.
$ErrorActionPreference = 'Stop'

$projectDir = Join-Path $PSScriptRoot 'src\CodexBar.App'
$project = Join-Path $projectDir 'CodexBar.App.csproj'

# Kill any existing instance so the DLL isn't locked during build.
Get-Process -Name 'CodexBar.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# Clear stale WPF temp projects so directory-based tooling stays unambiguous.
Get-ChildItem -LiteralPath $projectDir -Filter '*_wpftmp.csproj' -File |
    Remove-Item -Force

# Build so errors are visible in the terminal.
Write-Information 'Building CodexBar...' -InformationAction Continue
dotnet build $project --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Launch detached; the app lives in the system tray, not the terminal.
$exe = Join-Path $projectDir 'bin\Debug\net9.0-windows\CodexBar.App.exe'
Start-Process -FilePath $exe
Write-Information 'CodexBar is running in the system tray.' -InformationAction Continue
