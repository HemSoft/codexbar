[CmdletBinding()]
param(
    [Parameter()]
    [string]$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path,

    [Parameter()]
    [string]$OutputDirectory = (Join-Path (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path 'artifacts/security'),

    [Parameter()]
    [string]$GitleaksVersion = '8.30.1',

    [Parameter()]
    [string]$GitleaksWindowsX64Sha256 = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e',

    [Parameter()]
    [string]$GitleaksPath,

    [Parameter()]
    [switch]$SkipGitHistory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)

    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Get-GitleaksExecutable {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ExpectedSha256,
        [Parameter(Mandatory)][string]$ToolRoot,
        [Parameter()][string]$ExistingPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExistingPath)) {
        if (-not (Test-Path -LiteralPath $ExistingPath)) {
            throw "GitleaksPath not found: $ExistingPath"
        }

        return (Resolve-Path -LiteralPath $ExistingPath).Path
    }

    $exePath = Join-Path $ToolRoot "gitleaks-$Version/gitleaks.exe"
    if (Test-Path -LiteralPath $exePath) {
        return $exePath
    }

    $zipPath = Join-Path $ToolRoot "gitleaks_$($Version)_windows_x64.zip"
    $url = "https://github.com/gitleaks/gitleaks/releases/download/v$Version/gitleaks_$($Version)_windows_x64.zip"
    New-Item -ItemType Directory -Force -Path $ToolRoot | Out-Null

    Write-Information "Downloading Gitleaks $Version..." -InformationAction Continue
    Invoke-WebRequest -Uri $url -OutFile $zipPath

    $actualSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "Gitleaks checksum mismatch. Expected $ExpectedSha256 but got $actualSha256."
    }

    $extractPath = Join-Path $ToolRoot "gitleaks-$Version"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractPath -Force
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Gitleaks executable not found after extraction: $exePath"
    }

    return $exePath
}

function Invoke-Gitleaks {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', 'Invoke-Gitleaks', Justification = 'Gitleaks is the product name.')]
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$ReportPath,
        [Parameter()][switch]$SkipHistory
    )

    $configPath = Join-Path $RepoRoot '.gitleaks.toml'
    $arguments = @('dir', $RepoRoot)
    if (-not $SkipHistory) {
        $arguments = @('git', $RepoRoot, '--log-opts=--all')
    }

    $arguments += @(
        '--config', $configPath,
        '--redact=100',
        '--report-format', 'sarif',
        '--report-path', $ReportPath,
        '--exit-code', '2',
        '--no-color',
        '--verbose'
    )

    & $ExePath @arguments
    return $LASTEXITCODE
}

function Invoke-MiasmaScan {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$ReportPath,
        [Parameter()][switch]$SkipHistory
    )

    $scriptPath = Join-Path $PSScriptRoot 'Scan-MiasmaIndicators.ps1'
    if ($SkipHistory) {
        $json = & $scriptPath -Root $RepoRoot -Json
    }
    else {
        $json = & $scriptPath -Root $RepoRoot -Json -IncludeGitHistory
    }
    $json | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    $result = $json | ConvertFrom-Json
    return [int]$result.FindingCount
}

$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path

if (-not (Test-CommandAvailable -Name 'git')) {
    throw 'git is required for the security scan.'
}

$gitleaksExe = Get-GitleaksExecutable `
    -Version $GitleaksVersion `
    -ExpectedSha256 $GitleaksWindowsX64Sha256 `
    -ToolRoot (Join-Path $resolvedOutput 'tools') `
    -ExistingPath $GitleaksPath

$gitleaksReport = Join-Path $resolvedOutput 'gitleaks.sarif'
$miasmaReport = Join-Path $resolvedOutput 'miasma.json'

Write-Information 'Running Gitleaks...' -InformationAction Continue
$gitleaksExitCode = Invoke-Gitleaks -ExePath $gitleaksExe -RepoRoot $resolvedRoot -ReportPath $gitleaksReport -SkipHistory:$SkipGitHistory

Write-Information 'Running Miasma indicator scan...' -InformationAction Continue
$miasmaFindingCount = Invoke-MiasmaScan -RepoRoot $resolvedRoot -ReportPath $miasmaReport -SkipHistory:$SkipGitHistory

$summary = [pscustomobject]@{
    Root                = $resolvedRoot
    OutputDirectory     = $resolvedOutput
    GitleaksVersion     = $GitleaksVersion
    GitleaksExitCode    = $gitleaksExitCode
    GitleaksReport      = $gitleaksReport
    MiasmaFindingCount  = $miasmaFindingCount
    MiasmaReport        = $miasmaReport
    GitHistoryIncluded  = -not [bool]$SkipGitHistory
}

$summaryPath = Join-Path $resolvedOutput 'security-scan-summary.json'
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$summary | Format-List

if ($gitleaksExitCode -eq 2) {
    Write-Error 'Gitleaks found one or more possible secrets.'
    exit 2
}

if ($gitleaksExitCode -ne 0) {
    Write-Error "Gitleaks failed with exit code $gitleaksExitCode."
    exit $gitleaksExitCode
}

if ($miasmaFindingCount -gt 0) {
    Write-Error "Miasma indicator scan found $miasmaFindingCount finding(s)."
    exit 3
}

Write-Information 'Security scan passed.' -InformationAction Continue
