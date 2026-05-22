#Requires -Version 7
<#
.SYNOPSIS
    Runs all 7 quality gates for the CodexBar repository and outputs a dashboard.
.DESCRIPTION
    Executes build, format check, tests with coverage (line + branch), CRAP score
    analysis, security audit, and markdown lint. Produces a consolidated report
    with pass/fail for each gate.
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
$totalGates = 7

# 1. Build
Write-Host "`n=== Build ===" -ForegroundColor Cyan
$buildOutput = dotnet build --verbosity minimal 2>&1
$buildExitCode = $LASTEXITCODE
$buildPass = $buildExitCode -eq 0
if ($buildPass) {
    $warnMatch = $buildOutput | Select-String '(\d+) Warning\(s\)' | Select-Object -Last 1
    if ($warnMatch) {
        $warnMatch.Matches.Value -match '(\d+) Warning' > $null
        $warnCount = [int]$matches[1]
        $buildPass = $warnCount -eq 0
    }
}
Write-Gate -Name 'Build' -Pass $buildPass -Detail $(if ($buildPass) { '0 warnings' } else { 'Warnings found or build failed' })
if ($buildPass) { $passCount++ }

# 2. Format
Write-Host "`n=== Format ===" -ForegroundColor Cyan
dotnet format "$repoRoot\CodexBar.slnx" --verify-no-changes --verbosity minimal 2>&1 | Out-Null
$formatPass = $LASTEXITCODE -eq 0
Write-Gate -Name 'Format' -Pass $formatPass -Detail $(if ($formatPass) { 'Clean' } else { 'Violations found' })
if ($formatPass) { $passCount++ }

# 3-4. Tests + Coverage (Line and Branch as separate gates)
Write-Host "`n=== Tests + Coverage ===" -ForegroundColor Cyan
$testProject = Join-Path $repoRoot 'src' 'CodexBar.Core.Tests' 'CodexBar.Core.Tests.csproj'
$runsettings = Join-Path $repoRoot 'src' 'CodexBar.Core.Tests' 'coverage.runsettings'
dotnet test $testProject --collect:"XPlat Code Coverage" --settings $runsettings --verbosity minimal 2>&1 | Out-Null
$testPass = $LASTEXITCODE -eq 0

$lineRate = 0.0
$branchRate = 0.0
$coverageFile = Get-ChildItem -Path (Join-Path $repoRoot 'src' 'CodexBar.Core.Tests' 'TestResults') -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($coverageFile) {
    [xml]$covDoc = Get-Content $coverageFile.FullName
    $lineRate = [double]$covDoc.coverage.'line-rate'
    $branchRate = [double]$covDoc.coverage.'branch-rate'
}

# Gate 3: Line coverage 100%
$lineCovPass = $testPass -and ($lineRate -ge 1.0)
Write-Gate -Name 'Line Coverage' -Pass $lineCovPass -Detail ('{0:P1}' -f $lineRate)
if ($lineCovPass) { $passCount++ }

# Gate 4: Branch coverage 100%
$branchCovPass = $testPass -and ($branchRate -ge 1.0)
Write-Gate -Name 'Branch Coverage' -Pass $branchCovPass -Detail ('{0:P1}' -f $branchRate)
if ($branchCovPass) { $passCount++ }

# 5. CRAP Score
Write-Host "`n=== CRAP Score ===" -ForegroundColor Cyan
$crapPass = $false
$crapDetail = 'Not evaluated'
if ($coverageFile) {
    [xml]$covXml = Get-Content $coverageFile.FullName
    $methods = $covXml.SelectNodes("//method[@complexity]")
    $totalCrap = 0.0
    $methodCount = 0
    $overThirty = 0
    foreach ($m in $methods) {
        $cc = [int]$m.complexity
        $lines = $m.SelectNodes(".//line")
        if ($lines.Count -gt 0) {
            $covered = ($lines | Where-Object { [int]$_.hits -gt 0 }).Count
            $total = $lines.Count
            $mCov = $covered / $total
        }
        else { $mCov = 1.0 }
        $crap = [math]::Pow($cc, 2) * [math]::Pow(1 - $mCov, 3) + $cc
        $totalCrap += $crap
        $methodCount++
        if ($crap -ge 30) { $overThirty++ }
    }
    $avgCrap = if ($methodCount -gt 0) { [math]::Round($totalCrap / $methodCount, 2) } else { 0 }
    $crapPass = ($overThirty -eq 0) -and ($avgCrap -le 4.0)
    $crapDetail = "$overThirty methods > 30, avg $avgCrap"
}
Write-Gate -Name 'CRAP Score' -Pass $crapPass -Detail $crapDetail
if ($crapPass) { $passCount++ }

# 6. Security Audit
Write-Host "`n=== Security Audit ===" -ForegroundColor Cyan
$secOutput = dotnet list package --vulnerable --include-transitive 2>&1
$hasVulnerablePackages = ($secOutput | Select-String 'has the following vulnerable packages').Count -gt 0
$secPass = -not $hasVulnerablePackages
Write-Gate -Name 'Security' -Pass $secPass -Detail $(if ($secPass) { '0 vulnerabilities' } else { 'Vulnerabilities found' })
if ($secPass) { $passCount++ }

# 7. Markdown Lint
Write-Host "`n=== Markdown Lint ===" -ForegroundColor Cyan
$mdLintResult = npx markdownlint-cli2 "**/*.md" "#node_modules" 2>&1
$mdPass = $LASTEXITCODE -eq 0
$mdDetail = if ($mdPass) { 'Clean' } else { 'Violations found' }
Write-Gate -Name 'Markdown' -Pass $mdPass -Detail $mdDetail
if ($mdPass) { $passCount++ }

# Summary
Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "Perfection Score: $passCount / $totalGates gates passing" -ForegroundColor $(if ($passCount -eq $totalGates) { 'Green' } else { 'Yellow' })
Write-Host "========================================" -ForegroundColor Yellow

exit $(if ($passCount -eq $totalGates) { 0 } else { 1 })
