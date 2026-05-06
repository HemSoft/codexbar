# ralph-create-issues.ps1 — Scan codebase and create GitHub Issues.
# Version: 1.2.0
# Wrapper that invokes ralph-issues.ps1 with a configurable scan focus.
# Detects stacks in the repo and applies appropriate scanning:
#   .NET    → analyzers, code quality, security, test gaps
#   TS/React → component issues, accessibility, type safety
#   General → TODO/FIXME, tech debt, documentation gaps, security
param(
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
    Write-Host "ralph-create-issues.ps1 - Codebase Scanner → GitHub Issues" -ForegroundColor Cyan
    Write-Host "Scans the repo for improvements and creates GitHub Issues (with duplicate check)."
    Write-Host ""
    Write-Host "PARAMETERS" -ForegroundColor Yellow
    Write-Host "  -Focus <name>          Scan focus: all, security, quality, tests, tech-debt, documentation"
    Write-Host "  -Prompt <string>       Custom scan prompt (overrides -Focus). File path or literal text."
    Write-Host "  -Labels <strings>      Labels to apply (comma-separated). Auto-adds focus label if not set."
    Write-Host "  -DryRun               Report findings without creating issues"
    Write-Host "  -Model <name>          Model to use (validated by ralph-issues.ps1)"
    Write-Host "  -Provider <name>       CLI provider: copilot, opencode (validated by ralph-issues.ps1)"
    Write-Host "  -ReviewProduct <name>  Accepted for run-all compatibility; ignored (no PR review phase)"
    Write-Host "  -ReviewMode <name>     Accepted for run-all compatibility; ignored (no PR review phase)"
    Write-Host "  -Agents <specs>        Agent specs (dev agents only)"
    Write-Host "  -WorkUntil <HH:mm>     Stop after this local time"
    Write-Host "  -NoAudio               Suppress audio feedback"
    Write-Host "  -Once                  Run only one scan iteration"
    Write-Host "  -Help                  Show this help message"
    Write-Host ""
    Write-Host "FOCUS AREAS" -ForegroundColor Yellow
    Write-Host "  all            Scan for all categories of issues"
    Write-Host "  security       Security vulnerabilities, dependency issues, secrets, auth problems"
    Write-Host "  quality        Code quality: complexity, duplication, SOLID violations, naming"
    Write-Host "  tests          Missing tests, low coverage areas, brittle tests, test quality"
    Write-Host "  tech-debt      TODO/FIXME, deprecated APIs, outdated patterns, dead code"
    Write-Host "  documentation  Missing docs, outdated README, undocumented public APIs"
    Write-Host ""
    Write-Host "EXAMPLES" -ForegroundColor Yellow
    Write-Host "  ralph-create-issues"
    Write-Host "  ralph-create-issues -Focus security -Labels 'security,automated'"
    Write-Host "  ralph-create-issues -Focus tech-debt -DryRun"
    Write-Host "  ralph-create-issues -Focus quality -Once -Model opus"
    Write-Host ""
    exit 0
}

# --- Build focus-aware prompt ---
$promptParts = @()

switch ($Focus) {
    'security' {
        $promptParts += @(
            "Scan this codebase for SECURITY issues and create GitHub Issues for each finding."
            "Look for: hardcoded secrets, SQL injection, XSS vulnerabilities, insecure deserialization, broken auth, missing input validation, dependency vulnerabilities (check package versions), insecure configurations, exposed endpoints without auth, CORS misconfigurations."
            "For each finding, include: severity (critical/high/medium/low), affected file(s) and line numbers, description of the vulnerability, and suggested remediation."
        )
        if (-not $Labels) { $Labels = @('security', 'automated') }
    }
    'quality' {
        $promptParts += @(
            "Scan this codebase for CODE QUALITY issues and create GitHub Issues for each finding."
            "Look for: high cyclomatic complexity (>10), code duplication, SOLID principle violations, poor naming conventions, god classes/methods (>200 lines), tight coupling, missing error handling, magic numbers/strings, inconsistent patterns."
            "For each finding, include: affected file(s) and line numbers, description of the issue, and suggested improvement."
        )
        if (-not $Labels) { $Labels = @('code-quality', 'automated') }
    }
    'tests' {
        $promptParts += @(
            "Scan this codebase for TESTING gaps and create GitHub Issues for each finding."
            "Look for: public methods without tests, untested error paths, missing edge case tests, brittle tests (testing implementation not behavior), missing integration tests for critical paths, test files with low assertion density, flaky test patterns."
            "For each finding, include: affected file(s), what should be tested, and suggested test approach."
        )
        if (-not $Labels) { $Labels = @('testing', 'automated') }
    }
    'tech-debt' {
        $promptParts += @(
            "Scan this codebase for TECHNICAL DEBT and create GitHub Issues for each finding."
            "Look for: TODO/FIXME/HACK comments, deprecated API usage, outdated patterns, dead code, unused dependencies, overly complex configurations, migration opportunities (e.g., old .NET to new .NET), hardcoded values that should be config."
            "For each finding, include: affected file(s) and line numbers, description of the debt, and suggested modernization approach."
        )
        if (-not $Labels) { $Labels = @('tech-debt', 'automated') }
    }
    'documentation' {
        $promptParts += @(
            "Scan this codebase for DOCUMENTATION gaps and create GitHub Issues for each finding."
            "Look for: missing or outdated README, undocumented public APIs, missing XML docs on public methods (for .NET), missing JSDoc on exported functions (for TS), missing architecture decision records, outdated diagrams, missing setup/onboarding instructions."
            "For each finding, include: what needs documentation, suggested content outline, and priority."
        )
        if (-not $Labels) { $Labels = @('documentation', 'automated') }
    }
    default {
        $promptParts += @(
            "Scan this codebase comprehensively and create GitHub Issues for all improvements found."
            "Categories to scan: security vulnerabilities, code quality issues, missing tests, technical debt (TODO/FIXME, deprecated APIs), and documentation gaps."
            "Prioritize findings by severity: critical security issues first, then high-impact quality issues, then moderate improvements."
            "For each finding, include: category, severity, affected file(s), description, and suggested fix."
        )
        if (-not $Labels) { $Labels = @('automated') }
    }
}

$prompt = $promptParts -join ' '

# If custom -Prompt provided, it overrides the -Focus built prompt
if ($Prompt) { $prompt = $Prompt }

# Resolve ralph-issues.ps1 path
$_ralphIssuesPath = Join-Path $PSScriptRoot '..' 'ralph-issues.ps1'
if (-not (Test-Path $_ralphIssuesPath)) {
    # Try resolving from the ralph command location
    $_ralphCmd = Get-Command ralph -ErrorAction SilentlyContinue
    $_ralphDir = if ($_ralphCmd.CommandType -eq 'Function' -and $_ralphCmd.ScriptBlock -match "'([^']+\.ps1)'") {
        Split-Path $matches[1] -Parent
    } elseif ($_ralphCmd.Source) { Split-Path $_ralphCmd.Source -Parent } else { $null }
    if ($_ralphDir) {
        $_ralphIssuesPath = Join-Path $_ralphDir 'ralph-issues.ps1'
    }
}
if (-not (Test-Path $_ralphIssuesPath)) { throw "Cannot find ralph-issues.ps1. Expected at: $_ralphIssuesPath" }

$passThru = @{}
if ($NoAudio) { $passThru['NoAudio'] = $true }
if ($DryRun) { $passThru['DryRun'] = $true }
if ($Model) { $passThru['Model'] = $Model }
if ($Provider) { $passThru['Provider'] = $Provider }
if ($Agents) { $passThru['Agents'] = $Agents }
if ($WorkUntil) { $passThru['WorkUntil'] = $WorkUntil }
if ($Once) { $passThru['Once'] = $true }
if ($Labels) { $passThru['Labels'] = $Labels }

& $_ralphIssuesPath -Prompt $prompt -Max 1 @passThru
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
