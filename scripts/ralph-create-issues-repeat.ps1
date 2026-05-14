# ralph-create-issues-repeat.ps1 — Repeated codebase scanner → GitHub Issues.
# Version: 1.2.1
# Between each run, pulls latest main so the next scan sees fresh code.
# NOTE: PowerShell uses single-dash params: -Help, -Times 5 (not --help)
param(
    [int]$Times = 3,
    [ValidateSet('all', 'security', 'quality', 'tests', 'tech-debt', 'documentation')]
    [string]$Focus = 'all',
    [string]$Prompt,
    [string[]]$Labels,
    [switch]$DryRun,
    [switch]$NoAudio,
    [string]$Model,
    [string]$Provider,
    [string]$ReviewProduct,
    [string]$ReviewMode,
    [string[]]$Agents,
    [string]$WorkUntil,
    [switch]$Once,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

if ($Help) {
    Write-Host ""
    Write-Host "ralph-create-issues-repeat.ps1 - Repeated Issue Scanner" -ForegroundColor Cyan
    Write-Host "Runs ralph-create-issues.ps1 N times in autopilot mode."
    Write-Host "Between each run, pulls latest main so the next scan sees fresh code."
    Write-Host ""
    Write-Host "PARAMETERS" -ForegroundColor Yellow
    Write-Host "  -Times <int>           Number of times to run (default: 3)"
    Write-Host "  -Focus <name>          Scan focus: all, security, quality, tests, tech-debt, documentation"
    Write-Host "  -Prompt <string>       Custom scan prompt (overrides -Focus). File path or literal text."
    Write-Host "  -Labels <strings>      Labels to apply to created issues"
    Write-Host "  -DryRun               Report findings without creating issues"
    Write-Host "  -Model <name>          Model to pass through (validated by ralph-issues.ps1)"
    Write-Host "  -Provider <name>       CLI provider to pass through (validated downstream against config)"
    Write-Host "  -ReviewProduct <name>  Accepted for run-all compatibility; ignored downstream"
    Write-Host "  -ReviewMode <name>     Accepted for run-all compatibility; ignored downstream"
    Write-Host "  -Agents <specs>        Agent specs (dev agents only)"
    Write-Host "  -WorkUntil <HH:mm>     Stop after this local time (passed to each run)"
    Write-Host "  -NoAudio               Suppress audio feedback"
    Write-Host "  -Once                  Run only one scan iteration per run (passed to each run)"
    Write-Host "  -Help                  Show this help message"
    Write-Host ""
    Write-Host "EXAMPLES" -ForegroundColor Yellow
    Write-Host "  ralph-create-issues-repeat"
    Write-Host "  ralph-create-issues-repeat -Times 5 -Focus security"
    Write-Host "  ralph-create-issues-repeat -Times 10 -Focus tech-debt -WorkUntil 08:00"
    Write-Host "  ralph-create-issues-repeat -Focus quality -DryRun"
    Write-Host ""
    exit 0
}

$scriptDir = $PSScriptRoot
$targetScript = Join-Path $scriptDir "ralph-create-issues.ps1"
if (-not (Test-Path $targetScript)) {
    Write-Host "ERROR: ralph-create-issues.ps1 not found at: $targetScript" -ForegroundColor Red
    exit 1
}

if ($Once) { $Times = 1 }

for ($i = 1; $i -le $Times; $i++) {
    Write-Host ""
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host "== Issue scan run $i of $Times (focus: $Focus)" -ForegroundColor Cyan
    Write-Host "====================================" -ForegroundColor Cyan

    $passThru = @{ Focus = $Focus }
    if ($Prompt) { $passThru['Prompt'] = $Prompt }
    if ($NoAudio) { $passThru['NoAudio'] = $true }
    if ($DryRun) { $passThru['DryRun'] = $true }
    if ($Model) { $passThru['Model'] = $Model }
    if ($Provider) { $passThru['Provider'] = $Provider }
    if ($ReviewProduct) { $passThru['ReviewProduct'] = $ReviewProduct }
    if ($ReviewMode) { $passThru['ReviewMode'] = $ReviewMode }
    if ($Agents) { $passThru['Agents'] = $Agents }
    if ($WorkUntil) { $passThru['WorkUntil'] = $WorkUntil }
    if ($Once) { $passThru['Once'] = $true }
    if ($Labels) { $passThru['Labels'] = $Labels }

    & $targetScript @passThru

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Run $i failed (exit code $LASTEXITCODE). Stopping." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    if ($i -lt $Times) {
        Write-Host "Pulling latest main before next scan..." -ForegroundColor DarkGray
        git checkout main 2>&1 | Out-Null
        git pull --ff-only 2>&1 | Out-Null
    }
}

Write-Host ""
Write-Host "====================================" -ForegroundColor Green
Write-Host "== All $Times issue scan runs complete!" -ForegroundColor Green
Write-Host "====================================" -ForegroundColor Green
