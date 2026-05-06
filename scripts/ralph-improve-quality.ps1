# ralph-improve-quality.ps1 — Unified code quality improver (multi-stack).
# Version: 1.3.0
# Detects stacks in the repo and applies appropriate quality tooling:
#   .NET    → C# quality expert (analyzers, code style, strictness levels)
#   TS/React → react-doctor (component health, patterns, accessibility)
#   General → SOLID, DRY, complexity reduction, naming, testability
param(
    [ValidateSet('all', 'dotnet', 'typescript')]
    [string]$Stack = 'all',
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
    Write-Host "ralph-improve-quality.ps1 - Unified Code Quality Improver" -ForegroundColor Cyan
    Write-Host "Runs ralph to improve code quality across all stacks in the repo."
    Write-Host "Automatically detects .NET, TypeScript/React, and applies the right tooling."
    Write-Host ""
    Write-Host "PARAMETERS" -ForegroundColor Yellow
    Write-Host "  -Stack <name>          Focus on a specific stack: all, dotnet, typescript (default: all)"
    Write-Host "  -Model <name>          Model to use (validated by ralph.ps1)"
    Write-Host "  -Provider <name>       CLI provider: copilot, opencode (validated by ralph.ps1)"
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
    Write-Host "STACKS" -ForegroundColor Yellow
    Write-Host "  all          Auto-detect stacks and apply all applicable quality improvements"
    Write-Host "  dotnet       .NET/C# — analyzers, code style enforcement, strictness levels"
    Write-Host "  typescript   TypeScript/React — react-doctor score, component patterns, accessibility"
    Write-Host ""
    Write-Host "EXAMPLES" -ForegroundColor Yellow
    Write-Host "  ralph-improve-quality -Autopilot"
    Write-Host "  ralph-improve-quality -Stack dotnet -Model sonnet"
    Write-Host "  ralph-improve-quality -Stack typescript -Once"
    Write-Host "  ralph-improve-quality -Agents pr-review-quality,auditor-code-quality"
    Write-Host "  ralph-improve-quality -Agents pr-review-quality@opus47  # multi-model review"
    Write-Host ""
    exit 0
}

# --- Build stack-aware prompt ---
$promptParts = @()

$promptParts += "Improve code quality for this repo."

switch ($Stack) {
    'dotnet' {
        $promptParts += @(
            "Focus on .NET/C# quality improvements."
            "Apply C# quality expert practices: enable Roslyn analyzers, enforce code style via .editorconfig, increase build strictness (TreatWarningsAsErrors, nullable enable, implicit usings)."
            "Fix all analyzer warnings. Improve naming, reduce cyclomatic complexity, apply SOLID principles."
            "Use the csharp-quality-expert skill if available."
        )
    }
    'typescript' {
        $promptParts += @(
            "Focus on TypeScript/React quality improvements."
            "Run react-doctor and fix all issues to reach a score of 100."
            "Improve component patterns, accessibility, type safety, and reduce complexity."
            "Use the react-doctor skill if available."
        )
    }
    default {
        $promptParts += @(
            "Detect which stacks are present in this repo (look for .csproj/.sln for .NET, package.json/tsconfig.json for TypeScript/React)."
            "For .NET/C#: apply C# quality expert practices — enable Roslyn analyzers, enforce code style via .editorconfig, increase build strictness (TreatWarningsAsErrors, nullable enable). Fix all analyzer warnings."
            "For TypeScript/React: run react-doctor and fix all issues to reach a score of 100. Improve component patterns, accessibility, and type safety."
            "For all stacks: improve naming, reduce cyclomatic complexity, apply SOLID and DRY principles, improve testability."
            "Use the csharp-quality-expert and react-doctor skills if available."
        )
    }
}

$prompt = $promptParts -join ' '
$branchSuffix = if ($Stack -eq 'all') { 'quality' } else { "quality-$Stack" }

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
& $_ralph -Prompt $prompt -Branch "feature/improve-$branchSuffix" -CleanupWorktree -Max 10 @passThru
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
