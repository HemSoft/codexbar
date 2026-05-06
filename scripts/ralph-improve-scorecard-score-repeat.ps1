# ralph-improve-scorecard-score-repeat.ps1 — Repeated scorecard score improver.
# Version: 1.3.0
# Between each run, pulls latest main so the next run branches from fresh code.
# NOTE: PowerShell uses single-dash params: -Help, -Times 5 (not --help)
param(
    [int]$Times = 3,
    [switch]$NoAudio,
    [switch]$SkipReview,
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
    Write-Host "ralph-improve-scorecard-score-repeat.ps1 - Repeated Scorecard Improver" -ForegroundColor Cyan
    Write-Host "Runs ralph-improve-scorecard-score.ps1 N times in autopilot mode."
    Write-Host "Between each run, pulls latest main so the next run branches from fresh code."
    Write-Host ""
    Write-Host "PARAMETERS" -ForegroundColor Yellow
    Write-Host "  -Times <int>           Number of times to run (default: 3)"
    Write-Host "  -Model <name>          Model to pass through (validated by ralph.ps1)"
    Write-Host "  -Provider <name>       CLI provider: copilot, opencode (validated by ralph.ps1)"
    Write-Host "  -ReviewProduct <name>  Automated PR review product to pass through"
    Write-Host "  -ReviewMode <name>     Review request mode to pass through when supported"
    Write-Host "  -Agents <specs>        Agent specs: role or role@model (validated by ralph.ps1)"
    Write-Host "                         Dev agents control the work loop; review agents run PR reviews"
    Write-Host "  -WorkUntil <HH:mm>     Stop after this local time (passed to each run)"
    Write-Host "  -NoAudio               Suppress audio feedback"
    Write-Host "  -SkipReview            Skip automated PR review requests"
    Write-Host "  -Once                  Run only one work iteration (passed to each run)"
    Write-Host "  -Help                  Show this help message"
    Write-Host ""
    Write-Host "EXAMPLES" -ForegroundColor Yellow
    Write-Host "  ralph-improve-scorecard-score-repeat"
    Write-Host "  ralph-improve-scorecard-score-repeat -Times 5"
    Write-Host "  ralph-improve-scorecard-score-repeat -Times 10 -Model sonnet -WorkUntil 08:00"
    Write-Host "  ralph-improve-scorecard-score-repeat -Times 3 -Agents auditor-scorecard"
    Write-Host ""
    exit 0
}

$scriptDir = $PSScriptRoot
$targetScript = Join-Path $scriptDir "ralph-improve-scorecard-score.ps1"
if (-not (Test-Path $targetScript)) {
    Write-Host "ERROR: ralph-improve-scorecard-score.ps1 not found at: $targetScript" -ForegroundColor Red
    exit 1
}

if ($Once) { $Times = 1 }

for ($i = 1; $i -le $Times; $i++) {
    Write-Host ""
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host "== Scorecard improvement run $i of $Times" -ForegroundColor Cyan
    Write-Host "====================================" -ForegroundColor Cyan

    $passThru = @{ Autopilot = $true }
    if ($NoAudio) { $passThru['NoAudio'] = $true }
    if ($SkipReview) { $passThru['SkipReview'] = $true }
    if ($Model) { $passThru['Model'] = $Model }
    if ($Provider) { $passThru['Provider'] = $Provider }
    if ($ReviewProduct) { $passThru['ReviewProduct'] = $ReviewProduct }
    if ($ReviewMode) { $passThru['ReviewMode'] = $ReviewMode }
    if ($Agents) { $passThru['Agents'] = $Agents }
    if ($WorkUntil) { $passThru['WorkUntil'] = $WorkUntil }
    if ($Once) { $passThru['Once'] = $true }

    & $targetScript @passThru

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Run $i failed (exit code $LASTEXITCODE). Stopping." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    if ($i -lt $Times) {
        Write-Host "Pulling latest main before next run..." -ForegroundColor DarkGray
        git checkout main 2>&1 | Out-Null
        git pull --ff-only 2>&1 | Out-Null
    }
}

Write-Host ""
Write-Host "====================================" -ForegroundColor Green
Write-Host "== All $Times scorecard improvement runs complete!" -ForegroundColor Green
Write-Host "====================================" -ForegroundColor Green
