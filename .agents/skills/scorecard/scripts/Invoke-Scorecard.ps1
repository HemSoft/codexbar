#Requires -Version 7
<#
.SYNOPSIS
    Evaluates CodexBar repository health scorecard.
.DESCRIPTION
    Runs all quality gates and calculates Bronze/Silver/Gold classification
    with point-based scoring. Returns JSON for programmatic consumption.
.PARAMETER RepoRoot
    Path to the repository root. Defaults to four levels up from script location.
.PARAMETER OutputFormat
    Output format: 'json' (default) or 'markdown'.
#>

param(
    [string]$RepoRoot,
    [ValidateSet('json', 'markdown')]
    [string]$OutputFormat = 'json'
)

$ErrorActionPreference = 'Continue'

if (-not $RepoRoot) {
    $RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')
}
Set-Location $RepoRoot

$rules = @()

function Add-Rule {
    param(
        [string]$Level, [string]$Title, [int]$Points,
        [bool]$Passed, [string]$Detail
    )
    $script:rules += [PSCustomObject]@{
        level   = $Level
        title   = $Title
        points  = $Points
        passed  = $Passed
        detail  = $Detail
    }
}

# --- Bronze Tier ---

# 1. Build with zero warnings
$slnPath = Join-Path $RepoRoot 'CodexBar.slnx'
$buildOutput = dotnet build $slnPath --verbosity minimal 2>&1 | Out-String
$buildExitCode = $LASTEXITCODE
$buildPassed = ($buildExitCode -eq 0) -and ($buildOutput -notmatch '[1-9]\d*\s+Warning\(s\)')
Add-Rule 'Bronze' 'Build with zero warnings' 5 $buildPassed $(
    if ($buildPassed) { '0 warnings' } else { 'Build warnings or errors detected' }
)

# 2. Code format clean
dotnet format $slnPath --verify-no-changes --verbosity minimal 2>&1 | Out-Null
$formatExitCode = $LASTEXITCODE
$formatPassed = $formatExitCode -eq 0
Add-Rule 'Bronze' 'Code format clean' 5 $formatPassed $(
    if ($formatPassed) { 'No formatting violations' } else { 'Formatting violations found' }
)

# 3. All tests pass
$testOutput = dotnet test $slnPath --verbosity minimal --no-build 2>&1 | Out-String
$testExitCode = $LASTEXITCODE
$testPassed = $testExitCode -eq 0
$testMatch = [regex]::Matches($testOutput, 'Passed:\s+(\d+)')
$totalTests = ($testMatch | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
Add-Rule 'Bronze' 'All tests pass' 5 $testPassed $(
    if ($testPassed) { "$totalTests tests passing" } else { 'Test failures detected' }
)

# 4. CI/CD workflow exists
$ciPath = Join-Path $RepoRoot '.github' 'workflows' 'ci.yml'
$ciExists = Test-Path $ciPath
$ciHasBuildTest = $false
if ($ciExists) {
    $ciContent = Get-Content $ciPath -Raw
    $ciHasBuildTest = ($ciContent -match 'dotnet build') -and ($ciContent -match 'dotnet test')
}
Add-Rule 'Bronze' 'CI/CD workflow exists' 5 ($ciExists -and $ciHasBuildTest) $(
    if ($ciExists -and $ciHasBuildTest) { 'ci.yml with build+test steps' }
    elseif ($ciExists) { 'ci.yml exists but missing build/test steps' }
    else { 'No CI workflow found' }
)

# 5. README exists and non-trivial
$readmePath = Join-Path $RepoRoot 'README.md'
$readmeExists = Test-Path $readmePath
$readmeLines = 0
if ($readmeExists) { $readmeLines = (Get-Content $readmePath).Count }
Add-Rule 'Bronze' 'README exists and non-trivial' 5 ($readmeExists -and $readmeLines -gt 50) $(
    if ($readmeExists) { "$readmeLines lines" } else { 'No README.md' }
)

# 6. License defined
$licensePath = Join-Path $RepoRoot 'LICENSE'
$licenseExists = Test-Path $licensePath
Add-Rule 'Bronze' 'License defined' 5 $licenseExists $(
    if ($licenseExists) { 'LICENSE file present' } else { 'No LICENSE file' }
)

# --- Silver Tier ---

# Coverage (run tests with coverage)
$testProjectPath = Join-Path $RepoRoot 'src' 'CodexBar.Core.Tests' 'CodexBar.Core.Tests.csproj'
$coverageOutput = dotnet test $testProjectPath `
    --collect:"XPlat Code Coverage" --verbosity minimal --no-build 2>&1 | Out-String
$coverageFile = Get-ChildItem -Path (Join-Path $RepoRoot 'src' 'CodexBar.Core.Tests' 'TestResults') -Recurse `
    -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$lineRate = 0.0
$branchRate = 0.0
if ($coverageFile) {
    [xml]$cov = Get-Content $coverageFile.FullName
    $lineRate = [double]$cov.coverage.'line-rate'
    $branchRate = [double]$cov.coverage.'branch-rate'
}

# 7. Line coverage >= 80%
Add-Rule 'Silver' 'Line coverage >= 80%' 10 ($lineRate -ge 0.80) $(
    '{0:P1} line coverage' -f $lineRate
)

# 8. Branch coverage >= 80%
Add-Rule 'Silver' 'Branch coverage >= 80%' 5 ($branchRate -ge 0.80) $(
    '{0:P1} branch coverage' -f $branchRate
)

# 9. Security audit clean
$secOutput = dotnet list $slnPath package --vulnerable --include-transitive 2>&1 | Out-String
$secExitCode = $LASTEXITCODE
$secPassed = ($secExitCode -eq 0) -and ($secOutput -notmatch 'vulnerable package|vulnerable packages|has the following vulnerable packages')
Add-Rule 'Silver' 'Security audit clean' 5 $secPassed $(
    if ($secPassed) { '0 vulnerabilities' }
    elseif ($secExitCode -ne 0) { 'Security audit command failed' }
    else { 'Vulnerable packages found' }
)

# 10. Markdown lint clean
$mdResult = npx markdownlint-cli2 "**/*.md" "#node_modules" 2>&1 | Out-String
$mdExitCode = $LASTEXITCODE
$mdPassed = $mdExitCode -eq 0
Add-Rule 'Silver' 'Markdown lint clean' 5 $mdPassed $(
    if ($mdPassed) { 'No lint errors' } else { 'Markdown lint errors found' }
)

# 11. CRAP score: 0 methods > 30
# Do not award points when the metric is not evaluated
$crapPassed = $false
$crapDetail = 'Not evaluated (requires ReportGenerator run)'
Add-Rule 'Silver' 'CRAP score: 0 methods > 30' 10 $crapPassed $crapDetail

# --- Gold Tier ---

# 12-13. Mutation score (check most recent Stryker report)
$strykerReport = Get-ChildItem -Path (Join-Path $RepoRoot 'src' 'CodexBar.Core.Tests' 'StrykerOutput') -Recurse `
    -Filter "mutation-report.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$mutationScore = 0.0
if ($strykerReport) {
    $strykerData = Get-Content $strykerReport.FullName -Raw | ConvertFrom-Json
    $totalMutants = 0
    $killedMutants = 0
    foreach ($file in $strykerData.files.PSObject.Properties) {
        foreach ($mutant in $file.Value.mutants) {
            $totalMutants++
            if ($mutant.status -eq 'Killed') { $killedMutants++ }
        }
    }
    if ($totalMutants -gt 0) { $mutationScore = $killedMutants / $totalMutants * 100 }
}

Add-Rule 'Gold' 'Mutation score >= 60%' 10 ($mutationScore -ge 60) $(
    if ($strykerReport) { '{0:F1}% mutation score' -f $mutationScore }
    else { 'No Stryker report found (run dotnet stryker)' }
)

Add-Rule 'Gold' 'Mutation score >= 80%' 10 ($mutationScore -ge 80) $(
    if ($strykerReport) { '{0:F1}% mutation score' -f $mutationScore }
    else { 'No Stryker report found (run dotnet stryker)' }
)

# 14. Line coverage 100%
Add-Rule 'Gold' 'Line coverage 100%' 5 ($lineRate -ge 1.0) $(
    '{0:P1} line coverage' -f $lineRate
)

# 15. Branch coverage 100%
Add-Rule 'Gold' 'Branch coverage 100%' 5 ($branchRate -ge 1.0) $(
    '{0:P1} branch coverage' -f $branchRate
)

# 16. Zero TODO/HACK comments
$todoHacks = Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Recurse -Include "*.cs" |
    Where-Object { $_.FullName -notmatch 'Tests' } |
    Select-String -Pattern 'TODO|HACK' -CaseSensitive |
    Measure-Object
$todoCount = $todoHacks.Count
Add-Rule 'Gold' 'Zero TODO/HACK comments in source' 5 ($todoCount -eq 0) $(
    if ($todoCount -eq 0) { 'No TODO/HACK comments' } else { "$todoCount TODO/HACK comments found" }
)

# --- Calculate Classification ---
$bronzeRules = $rules | Where-Object { $_.level -eq 'Bronze' }
$silverRules = $rules | Where-Object { $_.level -eq 'Silver' }
$goldRules = $rules | Where-Object { $_.level -eq 'Gold' }

$bronzePassed = ($bronzeRules | Where-Object { $_.passed }).Count
$silverPassed = ($silverRules | Where-Object { $_.passed }).Count
$goldPassed = ($goldRules | Where-Object { $_.passed }).Count

$bronzePoints = ($bronzeRules | Where-Object { $_.passed } | Measure-Object -Property points -Sum).Sum
$silverPoints = ($silverRules | Where-Object { $_.passed } | Measure-Object -Property points -Sum).Sum
$goldPoints = ($goldRules | Where-Object { $_.passed } | Measure-Object -Property points -Sum).Sum

$totalScore = $bronzePoints + $silverPoints + $goldPoints
$totalPassed = $bronzePassed + $silverPassed + $goldPassed
$totalRules = $rules.Count

$bronzeAchieved = $bronzePassed -eq $bronzeRules.Count
$silverAchieved = $bronzeAchieved -and ($silverPassed -eq $silverRules.Count)
$goldAchieved = $silverAchieved -and ($goldPassed -eq $goldRules.Count)

$classification = 'None'
if ($goldAchieved) { $classification = 'Gold' }
elseif ($silverAchieved) { $classification = 'Silver' }
elseif ($bronzeAchieved) { $classification = 'Bronze' }

# --- Output ---
$result = [PSCustomObject]@{
    classification = [PSCustomObject]@{
        level        = $classification
        numericScore = $totalScore
        maxPoints    = 100
        bronze       = [PSCustomObject]@{
            passed    = $bronzePassed
            total     = $bronzeRules.Count
            points    = [int]$bronzePoints
            maxPoints = 30
            achieved  = $bronzeAchieved
        }
        silver       = [PSCustomObject]@{
            passed    = $silverPassed
            total     = $silverRules.Count
            points    = [int]$silverPoints
            maxPoints = 35
            achieved  = $silverAchieved
        }
        gold         = [PSCustomObject]@{
            passed    = $goldPassed
            total     = $goldRules.Count
            points    = [int]$goldPoints
            maxPoints = 35
            achieved  = $goldAchieved
        }
        score        = [PSCustomObject]@{
            percent = [math]::Round($totalPassed / $totalRules * 100, 1)
            passed  = $totalPassed
            total   = $totalRules
        }
    }
    rules          = $rules
}

if ($OutputFormat -eq 'json') {
    $result | ConvertTo-Json -Depth 5
}
else {
    Write-Output "## Scorecard Status - CodexBar"
    Write-Output ""
    Write-Output "**Score**: $totalScore / 100 (Classification: $classification)"
    Write-Output ""
    Write-Output "### Tier Breakdown"
    Write-Output "| Tier   | Passed | Total | Points | Max |"
    Write-Output "|--------|--------|-------|--------|-----|"
    Write-Output "| Bronze | $bronzePassed      | $($bronzeRules.Count)     | $([int]$bronzePoints)      | 30  |"
    Write-Output "| Silver | $silverPassed      | $($silverRules.Count)     | $([int]$silverPoints)      | 35  |"
    Write-Output "| Gold   | $goldPassed      | $($goldRules.Count)     | $([int]$goldPoints)      | 35  |"
    Write-Output ""

    $failing = $rules | Where-Object { -not $_.passed }
    if ($failing) {
        Write-Output "### Failing Rules"
        Write-Output "| Tier | Rule | Points | Detail |"
        Write-Output "|------|------|--------|--------|"
        foreach ($r in $failing) {
            Write-Output "| $($r.level) | $($r.title) | $($r.points) | $($r.detail) |"
        }
        Write-Output ""
    }

    $passing = $rules | Where-Object { $_.passed }
    if ($passing) {
        Write-Output "### Passing Rules"
        Write-Output "| Tier | Rule | Points |"
        Write-Output "|------|------|--------|"
        foreach ($r in $passing) {
            Write-Output "| $($r.level) | $($r.title) | $($r.points) |"
        }
    }

    try { $tz = [System.TimeZoneInfo]::FindSystemTimeZoneById('America/New_York') }
    catch { $tz = [System.TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time') }
    $et = [System.TimeZoneInfo]::ConvertTime((Get-Date).ToUniversalTime(), $tz)
    Write-Output ""
    Write-Output "_Report generated $($et.ToString('yyyy-MM-dd HH:mm')) ET_"
}

# --- Score History ---
try { $histTz = [System.TimeZoneInfo]::FindSystemTimeZoneById('America/New_York') }
catch { $histTz = [System.TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time') }
$histEt = [System.TimeZoneInfo]::ConvertTime((Get-Date).ToUniversalTime(), $histTz)

$historyPath = Join-Path $PSScriptRoot '..' 'score-history.log'
$historyLine = "$($histEt.ToString('yyyy-MM-dd HH:mm')) ET | Score: $totalScore/100 | Classification: $classification | Passed: $totalPassed/$totalRules | Bronze: $bronzePassed/$($bronzeRules.Count) | Silver: $silverPassed/$($silverRules.Count) | Gold: $goldPassed/$($goldRules.Count)"

$shouldAppend = $true
if (Test-Path $historyPath) {
    $lastDataLine = Get-Content $historyPath | Where-Object { $_ -and $_ -notmatch '^#' } | Select-Object -Last 1
    if ($lastDataLine -match '\| Score: (?<score>\d+)/100 \| Classification: (?<class>\w+)') {
        $shouldAppend = -not (($matches['score'] -eq "$totalScore") -and ($matches['class'] -eq $classification))
    }
}
else {
    Set-Content $historyPath "# Score history for scorecard skill"
}

if ($shouldAppend) {
    Add-Content $historyPath $historyLine
}
