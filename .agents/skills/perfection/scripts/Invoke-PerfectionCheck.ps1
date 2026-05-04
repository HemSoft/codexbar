#Requires -Version 7
<#
.SYNOPSIS
    Runs all quality gates for the CodexBar repository and outputs a dashboard.
.DESCRIPTION
    Executes build, format check, tests with coverage, security audit, and
    markdown lint. Produces a consolidated report with pass/fail for each gate.
#>

param(
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')
Set-Location $repoRoot

function Write-Gate {
    param([string]$Name, [bool]$Pass, [string]$Detail)
    $emoji = if ($Pass) { '✅' } else { '❌' }
    Write-Host "$emoji $Name : $Detail"
}

$passCount = 0
$totalGates = 5

# 1. Build
Write-Host "`n=== Build ===" -ForegroundColor Cyan
$buildOutput = dotnet build --verbosity minimal 2>&1
$buildPass = $LASTEXITCODE -eq 0 -and ($buildOutput | Select-String '\d+ Warning\(s\)' | ForEach-Object { $_.Matches.Value -match '(\d+) Warning' > $null; [int]$matches[1] -eq 0 } | Where-Object { $_ } | Measure-Object).Count -gt 0
if (-not $buildPass) {
    $warnMatch = $buildOutput | Select-String '(\d+) Warning\(s\)' | Select-Object -Last 1
    if ($warnMatch) {
        $warnMatch.Matches.Value -match '(\d+) Warning' > $null
        $warnCount = [int]$matches[1]
        $buildPass = $warnCount -eq 0
    } else {
        $buildPass = $true
    }
}
Write-Gate -Name 'Build' -Pass $buildPass -Detail $(if ($buildPass) { '0 warnings' } else { 'Warnings found' })
if ($buildPass) { $passCount++ }

# 2. Format
Write-Host "`n=== Format ===" -ForegroundColor Cyan
dotnet format "$repoRoot\CodexBar.slnx" --verify-no-changes --verbosity minimal | Out-Null
$formatPass = $?
Write-Gate -Name 'Format' -Pass $formatPass -Detail $(if ($formatPass) { 'Clean' } else { 'Violations found' })
if ($formatPass) { $passCount++ }

# 3. Tests + Coverage
Write-Host "`n=== Tests + Coverage ===" -ForegroundColor Cyan
dotnet test "$repoRoot\CodexBar.slnx" --collect:"XPlat Code Coverage" --verbosity minimal | Out-Null
$testPass = $?
# Try to extract coverage from most recent cobertura file
$coverageFile = Get-ChildItem -Path src/CodexBar.Core.Tests/TestResults -Recurse -Filter "*.xml" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$coverageDetail = 'Unknown'
if ($coverageFile) {
    [xml]$cov = Get-Content $coverageFile.FullName
    $lineRate = [double]$cov.coverage.'line-rate'
    $branchRate = [double]$cov.coverage.'branch-rate'
    $coverageDetail = "line {0:P0}, branch {1:P0}" -f $lineRate, $branchRate
}
Write-Gate -Name 'Tests' -Pass $testPass -Detail $coverageDetail
if ($testPass) { $passCount++ }

# 4. Security Audit
Write-Host "`n=== Security Audit ===" -ForegroundColor Cyan
$secOutput = dotnet list package --vulnerable --include-transitive 2>&1
$secPass = ($secOutput | Select-String 'has no vulnerable packages').Count -gt 0
Write-Gate -Name 'Security' -Pass $secPass -Detail $(if ($secPass) { '0 vulnerabilities' } else { 'Vulnerabilities found' })
if ($secPass) { $passCount++ }

# 5. Markdown Lint
Write-Host "`n=== Markdown Lint ===" -ForegroundColor Cyan
$mdFiles = Get-ChildItem -Path . -Filter "*.md" -Recurse | Where-Object { $_.FullName -notmatch 'node_modules|\.git' }
$mdPass = $true
$mdErrors = 0
foreach ($f in $mdFiles) {
    # Very basic check: no trailing whitespace on lines
    $lines = Get-Content $f.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ' +$') {
            $mdPass = $false
            $mdErrors++
        }
    }
}
Write-Gate -Name 'Markdown' -Pass $mdPass -Detail $(if ($mdPass) { 'Clean' } else { "$mdErrors trailing whitespace lines" })
if ($mdPass) { $passCount++ }

# Summary
Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "Perfection Score: $passCount / $totalGates gates passing" -ForegroundColor $(if ($passCount -eq $totalGates) { 'Green' } else { 'Yellow' })
Write-Host "========================================" -ForegroundColor Yellow

exit $(if ($passCount -eq $totalGates) { 0 } else { 1 })
