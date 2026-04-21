# Launch CodexBar in the system tray.
$ErrorActionPreference = 'Stop'

$project = "$PSScriptRoot\src\CodexBar.App"

# Kill any existing instance so the DLL isn't locked during build.
Get-Process -Name 'CodexBar.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# Build so errors are visible in the terminal.
Write-Host 'Building CodexBar...' -ForegroundColor Cyan
dotnet build $project --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Launch detached — the app lives in the system tray, not the terminal.
$exe = Join-Path $project 'bin\Debug\net9.0-windows\CodexBar.App.exe'
Start-Process -FilePath $exe
Write-Host 'CodexBar is running in the system tray.' -ForegroundColor Green
