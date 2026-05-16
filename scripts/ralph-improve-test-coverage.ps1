# ralph-improve-test-coverage.ps1 — Test coverage and quality improver.
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
    Write-Host "ralph-improve-test-coverage.ps1 - Test Coverage Improver" -ForegroundColor Cyan
    Write-Host "Runs ralph to increase test coverage and test quality on a dedicated feature branch."
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
    Write-Host "  ralph-improve-test-coverage -Autopilot"
    Write-Host "  ralph-improve-test-coverage -Model sonnet -WorkUntil 08:00"
    Write-Host "  ralph-improve-test-coverage -Agents pr-review-quality"
    Write-Host "  ralph-improve-test-coverage -Agents pr-review-quality@opus47,pr-review-security"
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
$coveragePrompt = @'
Your goal is to improve test coverage AND test quality across the codebase.

PHASE 1 — COVERAGE GAPS
1. Run 'bun run test:coverage' to identify uncovered lines, branches, and functions.
2. Prioritize untested code paths by risk: public API surfaces first, then complex internal logic.
3. Write focused tests that cover the gaps. Aim for 100% line and branch coverage.

PHASE 2 — TEST QUALITY (even at 100% coverage)
Coverage alone does not guarantee quality. Evaluate and improve:

  RESILIENCE: Do tests actually fail when the code breaks?
  - Verify assertions are meaningful, not just "does not throw".
  - Check for tests that always pass regardless of implementation (tautological tests).
  - Where practical, temporarily mutate logic (flip a conditional, change a return value) and confirm at least one test fails. Revert the mutation afterward.

  CORRECTNESS: Are tests testing the right things?
  - Each test should have a single clear reason to fail.
  - Avoid brittle tests coupled to implementation details (e.g., exact call counts, internal state).
  - Prefer testing behavior and outcomes over internal mechanics.

  NAMING: Do test names describe the scenario and expected outcome?
  - Follow the pattern: "should <expected behavior> when <condition>".
  - A reader should understand what failed from the test name alone, without reading the test body.

  STRUCTURE: Are tests well-organized and maintainable?
  - Use Arrange-Act-Assert (or Given-When-Then) structure consistently.
  - Extract shared setup into helpers or beforeEach blocks when it reduces duplication without hurting readability.
  - Keep each test focused — one logical assertion per test.

PHASE 3 — TEST ARCHITECTURE
Evaluate whether the existing test types are sufficient. Do NOT add frameworks or patterns just for the sake of it — only adopt what demonstrably adds value for this codebase.

  Consider whether the codebase would benefit from:
  - Integration tests: if components interact with external services, databases, or APIs.
  - End-to-end / acceptance tests: if there are critical user-facing workflows.
  - BDD / Gherkin tests: if requirements are expressed as business rules that non-developers should be able to read and validate.
  - Contract tests: if there are producer/consumer API boundaries.
  - Snapshot tests: if there is UI or serialized output where regressions matter.
  - Performance / benchmark tests: if there are hot paths, large data processing, or latency-sensitive operations where regressions would impact users.
  - Property-based tests: if functions have wide input domains where edge cases are hard to enumerate.

  For each type you consider:
  1. State the concrete gap it fills that existing tests do not.
  2. Only implement it if the gap is real and the benefit outweighs the maintenance cost.
  3. If the codebase already has good coverage of that concern, move on.

METRICS TRACKING
Before making any changes, capture a baseline snapshot and document improvements as you go. Create or update a file named TEST-METRICS.md in the project root with a table like:

  | Metric                  | Before | After | Delta |
  |-------------------------|--------|-------|-------|
  | Line coverage %         |        |       |       |
  | Branch coverage %       |        |       |       |
  | Function coverage %     |        |       |       |
  | Total test count        |        |       |       |
  | Test types present      |        |       |       |
  | Avg assertions per test |        |       |       |

At the START of your session, fill in the "Before" column by running 'bun run test:coverage' and inspecting the test suite. At the END, fill in "After" and "Delta". Include a brief summary of what was added or improved and why.

This file serves as a living record so improvements are visible across runs and reviewable in PRs.

CRITICAL: Every change must leave tests green. Run 'bun run test:coverage' after each round of changes to verify coverage did not regress and all tests pass.
'@
& $_ralph -Prompt $coveragePrompt -Branch "feature/increase-coverage" -CleanupWorktree -Max 20 @passThru
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
