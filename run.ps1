# Launch CodexBar in the system tray.
$ErrorActionPreference = 'Stop'

dotnet run --project "$PSScriptRoot\src\CodexBar.App"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
