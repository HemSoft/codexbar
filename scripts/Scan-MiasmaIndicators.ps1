[CmdletBinding()]
param(
    [Parameter()]
    [string]$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path,

    [Parameter()]
    [switch]$IncludeGitHistory,

    [Parameter()]
    [switch]$Json
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)

    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-Ripgrep {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter()][switch]$AllowNoMatches
    )

    $output = & rg @Arguments 2>$null
    $exitCode = $LASTEXITCODE
    if ($exitCode -gt 1) {
        throw "rg failed with exit code $exitCode"
    }

    if ($exitCode -eq 1 -and -not $AllowNoMatches) {
        throw 'rg returned no matches'
    }

    return @($output)
}

function Test-ExcludedPath {
    param([Parameter(Mandatory)][string]$Path)

    $normalized = $Path.Replace('/', '\')
    $excludedSegments = @(
        '\.git\',
        '\node_modules\',
        '\.next\',
        '\dist\',
        '\bin\',
        '\obj\',
        '\coverage\',
        '\CoverageReport\',
        '\TestResults\',
        '\StrykerOutput\',
        '\vendor\'
    )

    foreach ($segment in $excludedSegments) {
        if ($normalized.Contains($segment, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $normalized.EndsWith('\scripts\Scan-MiasmaIndicators.ps1', [StringComparison]::OrdinalIgnoreCase) -or
        $normalized.EndsWith('\nul', [StringComparison]::OrdinalIgnoreCase)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)][string]$BasePath,
        [Parameter(Mandatory)][string]$Path
    )

    [System.IO.Path]::GetRelativePath($BasePath, $Path).Replace('\', '/')
}

function Find-CandidateFile {
    param(
        [Parameter(Mandatory)][string]$SearchRoot,
        [Parameter(Mandatory)][string[]]$RelativePatterns
    )

    if (Test-CommandAvailable -Name 'rg') {
        $rgGlobs = foreach ($pattern in $RelativePatterns) {
            @('--glob', "**/$pattern")
        }

        return Invoke-Ripgrep -Arguments (@('--files', $SearchRoot) + $commonGlobs + $rgGlobs) -AllowNoMatches
    }

    $files = Get-ChildItem -LiteralPath $SearchRoot -Recurse -Force -File -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-ExcludedPath -Path $_.FullName) }

    foreach ($file in $files) {
        $relative = Get-RelativePath -BasePath $SearchRoot -Path $file.FullName
        foreach ($pattern in $RelativePatterns) {
            if ($relative -like $pattern) {
                $file.FullName
                break
            }
        }
    }
}

function Find-PatternMatch {
    param(
        [Parameter(Mandatory)][string]$SearchRoot,
        [Parameter(Mandatory)][string]$Pattern
    )

    if (Test-CommandAvailable -Name 'rg') {
        return Invoke-Ripgrep -Arguments (@('-n') + $commonGlobs + @($Pattern, $SearchRoot)) -AllowNoMatches
    }

    $files = Get-ChildItem -LiteralPath $SearchRoot -Recurse -Force -File -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-ExcludedPath -Path $_.FullName) }

    foreach ($file in $files) {
        try {
            Select-String -LiteralPath $file.FullName -Pattern $Pattern -ErrorAction Stop |
                ForEach-Object { "$($_.Path):$($_.LineNumber):$($_.Line)" }
        }
        catch {
            continue
        }
    }
}

function Add-Finding {
    param(
        [Parameter()][System.Collections.Generic.List[object]]$Findings,
        [Parameter(Mandatory)][string]$Severity,
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$Path,
        [Parameter()][string]$Detail = ''
    )

    $Findings.Add([pscustomobject]@{
        Severity = $Severity
        Type     = $Type
        Path     = $Path
        Detail   = $Detail
    }) | Out-Null
}

if (-not (Test-Path -LiteralPath $Root)) {
    throw "Root not found: $Root"
}

$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
$findings = [System.Collections.Generic.List[object]]::new()
$commonGlobs = @(
    '--hidden',
    '--glob', '!**/.git/**',
    '--glob', '!**/node_modules/**',
    '--glob', '!**/.next/**',
    '--glob', '!**/dist/**',
    '--glob', '!**/bin/**',
    '--glob', '!**/obj/**',
    '--glob', '!**/coverage/**',
    '--glob', '!**/CoverageReport/**',
    '--glob', '!**/TestResults/**',
    '--glob', '!**/StrykerOutput/**',
    '--glob', '!**/scripts/Scan-MiasmaIndicators.ps1',
    '--glob', '!**/vendor/**',
    '--glob', '!**/nul'
)

$exactFiles = Find-CandidateFile -SearchRoot $resolvedRoot -RelativePatterns @(
    '.github/setup.js',
    '.claude/settings.json',
    '.gemini/settings.json',
    '.cursor/rules/setup.mdc',
    '.vscode/tasks.json',
    'binding.gyp',
    'Gemfile'
)
foreach ($file in $exactFiles) {
    $normalized = $file.Replace('/', '\')
    if ($normalized -match '\\\.github\\setup\.js$') {
        Add-Finding -Findings $findings -Severity 'Critical' -Type 'Miasma setup dropper path' -Path $file
    }
}

$patternChecks = @(
    @{ Severity = 'Critical'; Type = 'Setup command'; Pattern = 'node\s+\.github/setup\.js' },
    @{ Severity = 'Critical'; Type = 'Miasma marker'; Pattern = 'Miasma[: -]+The Spreading Blight|Spreading Blight' },
    @{ Severity = 'Critical'; Type = 'binding.gyp node execution'; Pattern = '<!\(node\s+index\.js' },
    @{ Severity = 'Critical'; Type = 'package test persistence'; Pattern = '"test"\s*:\s*"node\s+\.github/setup\.js"' },
    @{ Severity = 'High'; Type = 'Red Hat wave workflow marker'; Pattern = 'OIDC_PACKAGES|bun\s+run\s+_index\.js' },
    @{ Severity = 'Medium'; Type = 'Suspicious commit phrase in file'; Pattern = 'chore: update dependencies \[skip ci\]' }
)

foreach ($check in $patternChecks) {
    $patternMatches = Find-PatternMatch -SearchRoot $resolvedRoot -Pattern $check.Pattern
    foreach ($match in $patternMatches) {
        Add-Finding -Findings $findings -Severity $check.Severity -Type $check.Type -Path $match
    }
}

$packageFiles = Find-CandidateFile -SearchRoot $resolvedRoot -RelativePatterns @('package.json')
foreach ($file in $packageFiles) {
    try {
        $packageJson = Get-Content -LiteralPath $file -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $scriptProperty = $packageJson.PSObject.Properties['scripts']
        if ($null -eq $scriptProperty -or $null -eq $scriptProperty.Value) {
            continue
        }

        foreach ($script in $scriptProperty.Value.PSObject.Properties) {
            $command = [string]$script.Value
            if ($command -match 'node\s+\.github/setup\.js') {
                Add-Finding -Findings $findings -Severity 'Critical' -Type 'package script setup command' -Path $file -Detail "$($script.Name): $command"
            }
            elseif ($script.Name -eq 'preinstall' -and $command -match '^\s*node\s+index\.js\s*$') {
                Add-Finding -Findings $findings -Severity 'High' -Type 'preinstall node index.js' -Path $file -Detail "$($script.Name): $command"
            }
        }
    }
    catch {
        Add-Finding -Findings $findings -Severity 'Low' -Type 'package.json parse error' -Path $file -Detail $_.Exception.Message
    }
}

if ($IncludeGitHistory) {
    if (-not (Test-CommandAvailable -Name 'git')) {
        throw 'git is required for -IncludeGitHistory.'
    }

    $excludedPath = '\\(node_modules|\.next|dist|bin|obj|coverage|CoverageReport|TestResults|StrykerOutput)($|\\)'
    $gitDirs = Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -Directory -Filter .git -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $excludedPath } |
        ForEach-Object { $_.Parent.FullName }
    $gitFiles = Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -File -Filter .git -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $excludedPath } |
        ForEach-Object { $_.Directory.FullName }
    $repos = @($gitDirs + $gitFiles) | Sort-Object -Unique

    $historyPaths = @(
        '.github/setup.js',
        '.claude/settings.json',
        '.gemini/settings.json',
        '.cursor/rules/setup.mdc',
        '.vscode/tasks.json',
        'binding.gyp'
    )

    foreach ($repo in $repos) {
        foreach ($path in $historyPaths) {
            $commits = @(git -C $repo rev-list --all -- $path 2>$null | Select-Object -First 1)
            if ($commits.Count -gt 0) {
                $severity = 'Medium'
                if ($path -eq '.github/setup.js' -or $path -eq '.claude/settings.json' -or $path -eq '.gemini/settings.json' -or $path -eq '.vscode/tasks.json') {
                    $severity = 'Critical'
                }
                elseif ($path -eq '.cursor/rules/setup.mdc' -or $path -eq 'binding.gyp') {
                    $severity = 'High'
                }

                Add-Finding -Findings $findings -Severity $severity -Type 'Git history path indicator' -Path (Join-Path $repo $path) -Detail $commits[0]
            }
        }
    }
}

$summary = [pscustomobject]@{
    Root              = $resolvedRoot
    IncludeGitHistory = [bool]$IncludeGitHistory
    FindingCount      = $findings.Count
    CriticalCount     = @($findings | Where-Object { $_.Severity -eq 'Critical' }).Count
    HighCount         = @($findings | Where-Object { $_.Severity -eq 'High' }).Count
    MediumCount       = @($findings | Where-Object { $_.Severity -eq 'Medium' }).Count
    LowCount          = @($findings | Where-Object { $_.Severity -eq 'Low' }).Count
    Findings          = @($findings | Sort-Object Severity, Type, Path)
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 6
}
else {
    $summary | Select-Object Root, IncludeGitHistory, FindingCount, CriticalCount, HighCount, MediumCount, LowCount | Format-List
    if ($findings.Count -gt 0) {
        $findings | Sort-Object Severity, Type, Path | Format-Table Severity, Type, Path, Detail -AutoSize
    }
}
