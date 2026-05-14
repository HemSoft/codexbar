# ralph-simplisticate.ps1 — Complexity and CRAP score reducer.
# Version: 1.3.1
param(
    [switch]$Autopilot,
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
    Write-Host "ralph-simplisticate.ps1 - Complexity Reducer" -ForegroundColor Cyan
    Write-Host "Runs ralph to reduce CRAP scores, complexity, and code smells on a dedicated feature branch."
    Write-Host ""
    Write-Host "PARAMETERS" -ForegroundColor Yellow
    Write-Host "  -Model <name>          Model to use (validated by ralph.ps1)"
    Write-Host "  -Provider <name>       CLI provider to pass through (validated downstream against config)"
    Write-Host "  -ReviewProduct <name>  Automated PR review product for ralph-pr handoff"
    Write-Host "  -ReviewMode <name>     Review request mode when the product supports multiple modes"
    Write-Host "  -Agents <specs>        Agent specs: role or role@model (validated by ralph.ps1)"
    Write-Host "                         Dev agents control the work loop; review agents run PR reviews"
    Write-Host "  -WorkUntil <HH:mm>     Stop after this local time"
    Write-Host "  -Autopilot             Enable autopilot mode (auto-merge PRs)"
    Write-Host "  -NoAudio               Suppress audio feedback"
    Write-Host "  -SkipReview            Skip automated PR review requests"
    Write-Host "  -Once                  Run only one work iteration"
    Write-Host "  -Help                  Show this help message"
    Write-Host ""
    Write-Host "EXAMPLES" -ForegroundColor Yellow
    Write-Host "  ralph-simplisticate -Autopilot"
    Write-Host "  ralph-simplisticate -Model sonnet -WorkUntil 08:00"
    Write-Host "  ralph-simplisticate -Agents auditor-crap-score"
    Write-Host "  ralph-simplisticate -Agents pr-review-quality@opus47,auditor-crap-score"
    Write-Host ""
    exit 0
}

# Resolve ralph.ps1 — direct call needed; splatting doesn't survive the alias's @args forwarding
$_ralphCmd = Get-Command ralph -ErrorAction SilentlyContinue
$_ralph = if ($_ralphCmd.CommandType -eq 'Function' -and $_ralphCmd.ScriptBlock -match "'([^']+\.ps1)'") {
    $matches[1]
} elseif ($_ralphCmd.Source) { $_ralphCmd.Source } else { $null }
if (-not $_ralph -or -not (Test-Path $_ralph)) { throw "Cannot resolve ralph.ps1 from 'ralph' command" }

$passThru = @{}
if ($Autopilot) { $passThru['Autopilot'] = $true }
if ($NoAudio) { $passThru['NoAudio'] = $true }
if ($SkipReview) { $passThru['SkipReview'] = $true }
if ($Model) { $passThru['Model'] = $Model }
if ($Provider) { $passThru['Provider'] = $Provider }
if ($ReviewProduct) { $passThru['ReviewProduct'] = $ReviewProduct }
if ($ReviewMode) { $passThru['ReviewMode'] = $ReviewMode }
if ($Agents) { $passThru['Agents'] = $Agents }
if ($WorkUntil) { $passThru['WorkUntil'] = $WorkUntil }
if ($Once) { $passThru['Once'] = $true }
$simplisticatePrompt = @'
Your goal is to reduce CRAP scores across the codebase. CRAP (Change Risk Anti-Patterns) combines cyclomatic complexity with test coverage:

  CRAP(m) = complexity(m)^2 × (1 - coverage(m)/100)^3 + complexity(m)

TARGET: CRAP < 6 for every function. Functions at or above 6 are high-risk — prioritize the worst scores first. Achieving CRAP < 6 requires keeping functions small AND well-tested.

As a secondary guide, prefer cyclomatic complexity ≤ 5 per function. But CRAP is the primary metric — a complexity-5 function at 95% coverage (CRAP ≈ 5) is acceptable, while a complexity-3 function at 0% coverage (CRAP = 12) is not.

WORKFLOW:
1. Run 'bun run test:coverage' to get per-function coverage data.
2. Run 'npx eslint . --rule "complexity: [warn, 5]"' to find complex functions.
3. For each function, estimate its CRAP score from complexity + coverage. Sort by CRAP descending and work from the top.
4. Two levers reduce CRAP — use both:
   a. REDUCE COMPLEXITY: extract helpers, simplify conditionals, reduce nesting.
   b. INCREASE COVERAGE: add or update tests for every function you touch.
5. After changes, re-run both commands to verify CRAP scores dropped below 6.

CRITICAL: Never split a function without adding tests for the new pieces. Every iteration must move CRAP scores downward. After addressing high-CRAP functions, also fix any other code smells, code repetition, or unnecessary complexity you find.
'@
& $_ralph -Prompt $simplisticatePrompt -Branch "feature/simplisticate" -CleanupWorktree -Max 10 @passThru
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
