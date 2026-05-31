# ralph-process-issues.ps1 — Process GitHub Issues until none remain.
# Version: 1.5.2
# Repeatedly calls ralph -Issue until no open issues are left.
# Supports explicit issue ordering via -Issues or a config file.
# Pulls main between each run so the next issue sees the latest code.
# NOTE: PowerShell uses single-dash params: -Help, -Max 5 (not --help)
param(
    [int[]]$Issues,
    [string]$IssueConfig,
    [int]$Max = 0,
    [string]$IssueLabel,
    [string]$WorkUntil,
    [switch]$NoAudio,
    [switch]$SkipReview,
    [string]$Model,
    [string]$Provider,
    [string]$ReviewProduct,
    [string]$ReviewMode,
    [string[]]$Agents,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

function Resolve-RalphScriptPath {
    $candidatePaths = @(
        (Join-Path $PSScriptRoot '..' 'ralph.ps1'),
        (Join-Path $PSScriptRoot '..' 'ralph-loops' 'ralph.ps1')
    )

    $scriptRootPath = (Resolve-Path $PSScriptRoot -ErrorAction SilentlyContinue).Path
    $searchRoot = $scriptRootPath
    while ($searchRoot) {
        $candidatePaths += (Join-Path $searchRoot 'ai-tools\ralph-loops\ralph.ps1')

        $driveRoot = [System.IO.Path]::GetPathRoot($searchRoot)
        if ($searchRoot -ne $driveRoot) {
            $candidatePaths += @(Get-ChildItem -Path $searchRoot -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'ai-tools\ralph-loops\ralph.ps1' })
        }

        $parentRoot = Split-Path $searchRoot -Parent
        if (-not $parentRoot -or $parentRoot -eq $searchRoot) {
            break
        }

        $searchRoot = $parentRoot
    }

    foreach ($candidatePath in @($candidatePaths | Where-Object { $_ } | Select-Object -Unique)) {
        $resolvedCandidatePath = (Resolve-Path $candidatePath -ErrorAction SilentlyContinue).Path
        if ($resolvedCandidatePath -and (Test-Path $resolvedCandidatePath)) {
            return $resolvedCandidatePath
        }
    }

    foreach ($commandName in @('ralph.ps1', 'ralph')) {
        $ralphCmd = Get-Command $commandName -ErrorAction SilentlyContinue
        if (-not $ralphCmd) {
            continue
        }

        if ($ralphCmd.CommandType -eq 'Function' -and $ralphCmd.ScriptBlock -match "'([^']+\.ps1)'") {
            $resolvedCommandPath = (Resolve-Path $matches[1] -ErrorAction SilentlyContinue).Path
            if ($resolvedCommandPath -and (Test-Path $resolvedCommandPath)) {
                return $resolvedCommandPath
            }
        }

        $resolvedSourcePath = (Resolve-Path $ralphCmd.Source -ErrorAction SilentlyContinue).Path
        if ($resolvedSourcePath -and (Test-Path $resolvedSourcePath)) {
            return $resolvedSourcePath
        }
    }

    return $null
}

$ralphPath = Resolve-RalphScriptPath
if (-not $ralphPath) {
    throw "Cannot resolve ralph.ps1 from the script directory or from 'ralph.ps1'/'ralph' on PATH."
}

$configPath = (Resolve-Path (Join-Path (Split-Path $ralphPath -Parent) 'lib' 'config.ps1') -ErrorAction SilentlyContinue).Path
if (-not $configPath -or -not (Test-Path $configPath)) {
    throw "Cannot resolve config.ps1 relative to ralph.ps1 at '$ralphPath'."
}

. $configPath

if (-not $Provider) { $Provider = Get-RalphDefaultProvider }
try {
    $script:ProviderConfig = Resolve-RalphProvider -Name $Provider
}
catch {
    Write-Host "Invalid provider '$Provider'. Valid: $((Get-RalphProviderNames) -join ', ')" -ForegroundColor Red
    exit 1
}

if ($Help) {
    Write-Host ""
    Write-Host "ralph-process-issues.ps1 — Process Issues Until Done" -ForegroundColor Cyan
    Write-Host "Repeatedly picks open GitHub Issues, works on them, and creates PRs."
    Write-Host "Stops when no open issues remain, or -Max / -WorkUntil limits are reached."
    Write-Host ""
    Write-Host "PARAMETERS" -ForegroundColor Yellow
    Write-Host "  -Issues <int[]>        Explicit issue numbers to process in order (e.g. -Issues 294,280,281)"
    Write-Host "  -IssueConfig <path>    Path to JSON config file with ordered issue numbers"
    Write-Host "                         Format: { `"issues`": [294, 280, 281, 286] }"
    Write-Host "                         Default lookup: ./ralph-issue-queue.json (if it exists)"
    Write-Host "  -Max <int>             Maximum issues to process (default: unlimited)"
    Write-Host "  -IssueLabel <label>    Only process issues with this label (e.g. 'automated', 'tech-debt')"
    Write-Host "  -WorkUntil <HH:mm>     Stop after this local time (e.g. -WorkUntil 08:00)"
    Write-Host "  -Model <name>          Model to pass through to ralph.ps1"
    Write-Host "  -Provider <name>       CLI provider to pass through"
    Write-Host "  -ReviewProduct <name>  Automated PR review product to pass through to ralph.ps1"
    Write-Host "  -ReviewMode <name>     Review request mode to pass through when supported"
    Write-Host "  -Agents <specs>        Agent specs to pass through"
    Write-Host "  -NoAudio               Suppress audio feedback"
    Write-Host "  -SkipReview            Skip automated PR review requests"
    Write-Host "  -Help                  Show this help message"
    Write-Host ""
    Write-Host "ISSUE ORDER PRIORITY" -ForegroundColor Yellow
    Write-Host "  1. -Issues parameter (explicit list)"
    Write-Host "  2. -IssueConfig file (or auto-detected ralph-issue-queue.json)"
    Write-Host "  3. Oldest open issue (default behavior)"
    Write-Host ""
    Write-Host "EXAMPLES" -ForegroundColor Yellow
    Write-Host "  ralph-process-issues                                # oldest first (default)"
    Write-Host "  ralph-process-issues -Issues 294,280,281            # explicit order"
    Write-Host "  ralph-process-issues -IssueConfig ./my-queue.json   # from config file"
    Write-Host "  ralph-process-issues -IssueLabel automated          # only 'automated' issues"
    Write-Host "  ralph-process-issues -Max 5                         # process up to 5 issues"
    Write-Host "  ralph-process-issues -WorkUntil 08:00               # work until 8am"
    Write-Host "  ralph-process-issues -Max 3 -Model opus47           # 3 issues with opus 4.7"
    Write-Host ""
    exit 0
}

# --- Resolve ralph.ps1 path ---
if (-not $ralphPath -or -not (Test-Path $ralphPath)) {
    Write-Host "ERROR: Cannot find ralph.ps1" -ForegroundColor Red
    exit 1
}

# --- Parse deadline ---
$deadline = $null
if ($WorkUntil) {
    try {
        $parsed = [DateTime]::ParseExact($WorkUntil, 'HH:mm', $null)
        $deadline = Get-Date -Hour $parsed.Hour -Minute $parsed.Minute -Second 0
        if ($deadline -le (Get-Date)) { $deadline = $deadline.AddDays(1) }
    }
    catch {
        Write-Host "Invalid -WorkUntil format. Use HH:mm (e.g. 08:00)." -ForegroundColor Red
        exit 1
    }
}

# --- Resolve repo slug ---
$repoSlug = gh repo view --json nameWithOwner -q '.nameWithOwner' 2>$null
if (-not $repoSlug) {
    $remoteUrl = git --no-pager remote get-url origin 2>$null
    if ($remoteUrl -match '[:/]([^/]+/[^/]+?)(?:\.git)?$') {
        $repoSlug = $Matches[1]
    }
}
if (-not $repoSlug) {
    Write-Host "ERROR: Cannot determine repo slug." -ForegroundColor Red
    exit 1
}

# --- Main loop ---
# Resolve issue queue: -Issues > -IssueConfig > auto-detect > oldest
$issueQueue = @()
if ($Issues -and $Issues.Count -gt 0) {
    $issueQueue = $Issues
    Write-Host "  Order:     explicit ($($issueQueue -join ', '))"
}
elseif ($IssueConfig -and (Test-Path $IssueConfig)) {
    $config = Get-Content $IssueConfig -Raw | ConvertFrom-Json
    $issueQueue = @($config.issues)
    Write-Host "  Order:     config file ($IssueConfig) — $($issueQueue.Count) issues"
}
else {
    # Auto-detect ralph-issue-queue.json in repo root
    $autoConfig = Join-Path (git rev-parse --show-toplevel 2>$null) 'ralph-issue-queue.json'
    if (Test-Path $autoConfig) {
        $config = Get-Content $autoConfig -Raw | ConvertFrom-Json
        $issueQueue = @($config.issues)
        Write-Host "  Order:     auto-detected ($autoConfig) — $($issueQueue.Count) issues"
    }
    else {
        Write-Host "  Order:     oldest first (default)"
    }
}

$maxIssues = if ($Max -gt 0) { $Max } else { [int]::MaxValue }
# If we have an explicit queue, cap at queue length
if ($issueQueue.Count -gt 0 -and $maxIssues -gt $issueQueue.Count) {
    $maxIssues = $issueQueue.Count
}
$processed = 0
$runStart = Get-Date

# Accumulated stats from child ralph runs
$accTokensIn = [double]0
$accTokensOut = [double]0
$accTokensCached = [double]0
$accTokenCost = 0.0
$accModels = @{}
$accProviderModels = @{}

Write-Host ""
Write-Host "====================================" -ForegroundColor Cyan
Write-Host "ralph-process-issues — Processing open issues" -ForegroundColor Cyan
Write-Host "  Repo:      $repoSlug"
Write-Host "  Label:     $(if ($IssueLabel) { $IssueLabel } else { '(any)' })"
Write-Host "  Max:       $(if ($Max -gt 0) { $Max } else { 'unlimited' })"
Write-Host "  WorkUntil: $(if ($deadline) { $deadline.ToString('HH:mm') } else { '(none)' })"
Write-Host "====================================" -ForegroundColor Cyan

for ($i = 1; $i -le $maxIssues; $i++) {
    # Check deadline
    if ($deadline -and (Get-Date) -ge $deadline) {
        Write-Host ""
        Write-Host "Deadline reached ($($deadline.ToString('HH:mm'))). Stopping." -ForegroundColor Yellow
        break
    }

    Write-Host ""
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host "== Processing issue $i$(if ($Max -gt 0) { " of $Max" })" -ForegroundColor Cyan
    Write-Host "====================================" -ForegroundColor Cyan

    # Build ralph args — use explicit issue number from queue, or 'oldest'
    $runStatsPath = Join-Path $env:TEMP "ralph-run-stats-$([guid]::NewGuid().ToString('N').Substring(0,8)).json"
    $ralphArgs = @{
        Autopilot = $true
        StatsPath = $runStatsPath
    }
    if ($issueQueue.Count -gt 0) {
        $ralphArgs['Issue'] = $issueQueue[$i - 1]
    }
    else {
        $ralphArgs['Issue'] = 'oldest'
    }
    if ($IssueLabel) { $ralphArgs['IssueLabel'] = $IssueLabel }
    if ($Model) { $ralphArgs['Model'] = $Model }
    if ($Provider) { $ralphArgs['Provider'] = $Provider }
    if ($ReviewProduct) { $ralphArgs['ReviewProduct'] = $ReviewProduct }
    if ($ReviewMode) { $ralphArgs['ReviewMode'] = $ReviewMode }
    if ($Agents) { $ralphArgs['Agents'] = $Agents }
    if ($NoAudio) { $ralphArgs['NoAudio'] = $true }
    if ($SkipReview) { $ralphArgs['SkipReview'] = $true }

    & $ralphPath @ralphArgs
    $exitCode = $LASTEXITCODE

    # Collect stats from child run
    if (Test-Path $runStatsPath) {
        try {
            $childStats = Get-Content $runStatsPath -Raw | ConvertFrom-Json
            $accTokensIn += [double]$childStats.tokensIn
            $accTokensOut += [double]$childStats.tokensOut
            $accTokensCached += [double]$childStats.tokensCached
            $accTokenCost += [double]$childStats.tokenCost
            $modelKey = [string]$childStats.modelId
            if ($modelKey) {
                if (-not $accModels.ContainsKey($modelKey)) { $accModels[$modelKey] = 0 }
                $accModels[$modelKey]++
            }
            $providerKey = if ($childStats.provider) { [string]$childStats.provider } elseif ($Provider) { [string]$Provider } else { '(default)' }
            $providerModelKey = if ($modelKey) { "$providerKey/$modelKey" } else { "$providerKey/(provider-default)" }
            if (-not $accProviderModels.ContainsKey($providerModelKey)) { $accProviderModels[$providerModelKey] = 0 }
            $accProviderModels[$providerModelKey]++
        } catch { $null = $_ }
        Remove-Item $runStatsPath -Force -ErrorAction SilentlyContinue
    }

    if ($exitCode -eq 2) {
        Write-Host ""
        Write-Host "No more open issues. Done!" -ForegroundColor Green
        break
    }
    elseif ($exitCode -ne 0) {
        Write-Host "ralph.ps1 exited with code $exitCode. Stopping." -ForegroundColor Yellow
        break
    }

    $processed++

    # Pull main before next issue
    if ($i -lt $maxIssues) {
        Write-Host ""
        Write-Host "Pulling latest main before next issue..." -ForegroundColor DarkGray
        git checkout main 2>&1 | Out-Null
        git pull --ff-only 2>&1 | Out-Null
    }
}

# --- Summary ---
$totalDuration = (Get-Date) - $runStart
$totalDurStr = "{0:hh\:mm\:ss}" -f $totalDuration

# Format token counts
function ConvertTo-TokenDisplayString([double]$val) {
    if ($val -ge 1000000) { return "{0:F1}M" -f ($val / 1000000) }
    if ($val -ge 1000)    { return "{0:F1}k" -f ($val / 1000) }
    return [string][int]$val
}
$arrUp = [char]0x2191; $arrDn = [char]0x2193; $dot = [char]0x2022
$tokInStr = ConvertTo-TokenDisplayString $accTokensIn
$tokOutStr = ConvertTo-TokenDisplayString $accTokensOut
$tokCachedStr = ConvertTo-TokenDisplayString $accTokensCached
$modelList = if ($accModels.Count -gt 0) { ($accModels.Keys | Sort-Object) -join ', ' } else { $Model ?? '(default)' }
$providerModelList = if ($accProviderModels.Count -gt 0) {
    ($accProviderModels.Keys | Sort-Object) -join ', '
}
elseif ($Provider -and $Model) {
    "$Provider/$Model"
}
elseif ($Provider) {
    "$Provider/(provider-default)"
}
else {
    '(default)'
}

Write-Host ""
Write-Host "====================================" -ForegroundColor Green
Write-Host "ralph-process-issues — Complete" -ForegroundColor Green
Write-Host "  Issues processed: $processed"
Write-Host "  Duration:         $totalDurStr"
Write-Host ""
Write-Host "  Provider/model(s): $providerModelList" -ForegroundColor Cyan
Write-Host "  Model(s):         $modelList" -ForegroundColor Cyan
Write-Host "  Tokens:           $arrUp $tokInStr $dot $arrDn $tokOutStr $dot $tokCachedStr (cached)"
Write-Host "  Token cost:       `$$([math]::Round($accTokenCost, 4))  (provider/model-aware per-1M pricing)" -ForegroundColor Yellow
Write-Host "====================================" -ForegroundColor Green
