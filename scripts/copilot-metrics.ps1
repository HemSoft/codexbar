param(
    [string]$Enterprise = 'bertelsmann',
    [string]$Org = 'Relias-Engineering',
    [int]$Year = (Get-Date).Year,
    [int]$Month = (Get-Date).Month,
    [int]$StartDay = 1,
    [int]$EndDay = 0,
    [int]$Top = 25,
    [string[]]$User,
    [string]$ApiVersion = '2026-03-10',
    [int]$Delay = 5,
    [int]$DataRetentionDays = 3,
    [int]$RollingRefreshDays = 4,
    [string]$Token,
    [string]$GitHubUser,
    [string]$DataDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data'),
    [string]$RunStamp = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string]$InputPath,
    [string]$RepairInputFile,
    [switch]$Refresh,
    [switch]$IncludeOrganizationFilter,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

function Get-EasternTimeZone {
    foreach ($timeZoneId in @('Eastern Standard Time', 'America/New_York')) {
        try {
            return [TimeZoneInfo]::FindSystemTimeZoneById($timeZoneId)
        }
        catch [TimeZoneNotFoundException] {
            $null = $_
        }
        catch [InvalidTimeZoneException] {
            $null = $_
        }
    }

    throw 'Could not resolve Eastern Time zone.'
}

function Get-EasternTimestamp {
    $easternTimeZone = Get-EasternTimeZone
    $easternNow = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $easternTimeZone)
    $easternNow.ToString('dddd, MMMM d, yyyy h:mm tt')
}

function Write-MetricsSection {
    param([Parameter(Mandatory)][string]$Title)

    Write-Information ''
    Write-Information "== $Title =="
}

function Write-MetricsDetail {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    Write-Information ('  {0,-14} {1}' -f "$($Label):", $Value)
}

function Show-CopilotMetricsHeader {
    Write-Information ''
    Write-Information 'Copilot Metrics'
    Write-Information '---------------'
    Write-MetricsDetail -Label 'Displayed' -Value "$(Get-EasternTimestamp) Eastern Time"
    Write-MetricsDetail -Label 'Copyright' -Value '2024 Relias LLC'
}

Show-CopilotMetricsHeader

if ($Help) {
    $scriptName = Split-Path -Leaf $PSCommandPath
    @"
$scriptName - GitHub Copilot billing metrics

USAGE
  .\scripts\$scriptName
  .\scripts\$scriptName -Top 10
  .\scripts\$scriptName -InputPath .\data\copilot-metrics.json
  .\scripts\$scriptName -Refresh -Year 2026 -Month 6
  .\scripts\$scriptName -Refresh -Org Relias-Engineering -Top 25
  .\scripts\$scriptName -Refresh -GitHubUser fhemmerrelias -Top 25
  .\scripts\$scriptName -Refresh -User fhemmerrelias -StartDay 1 -EndDay 1
  .\scripts\$scriptName -RepairInputFile .\data\copilot-metrics.json

DEFAULT MODE
  With no arguments, reads the last saved run from:
    .\data\copilot-metrics.json

  If copilot-metrics.json is missing, falls back to legacy saved runs:
    .\data\top10.json
    .\data\top10-*.json

  Then prints the top 25 AI Credit consumers from that file. This default mode
  does not call GitHub and does not modify data files.

REFRESH MODE
  Use -Refresh to collect per-user GitHub Copilot premium request usage, store
  the raw responses, and print the top AI Credit consumers for the selected
  period.

SCOPE PARAMETERS
  -Enterprise <slug>       GitHub enterprise slug. Default: bertelsmann.
  -Org <name>              Organization used to enumerate Copilot seats.
                           Default: Relias-Engineering.
  -IncludeOrganizationFilter
                           Adds organization=<Org> to premium request usage
                           queries. By default, the script queries enterprise
                           user usage and scopes users by current org seats.
                           Only applies with -Refresh or -RepairInputFile.
  -GitHubUser <login>      GitHub CLI account used when token environment
                           variables are not set. Default: fhemmerrelias for
                           the default Relias-Engineering scope.

PERIOD PARAMETERS
  -Year <yyyy>             Billing year. Default: current local year.
  -Month <1-12>            Billing month. Default: current local month.
  -StartDay <n>            First day in the month. Default: 1.
  -EndDay <n>              Last day in the month. Default: 0, which means the
                           current UTC day for the current month, otherwise the
                           last day of the requested month.

USER PARAMETERS
  -Top <n>                 Number of ranked users to display. Default: 25.
  -User <login[]>          Optional explicit user list. Omit to enumerate all
                           current Copilot seat assignees in -Org. Only applies
                           with -Refresh.

DATA PARAMETERS
  -InputPath <file>        Read and rank a saved JSON run without calling GitHub.
                           Default: .\data\copilot-metrics.json, with
                           no-argument legacy fallback to .\data\top10.json
                           or newest .\data\top10-*.json.
  -Refresh                 Query GitHub and write a fresh
                           .\data\copilot-metrics.json.
  -RepairInputFile <file>  Repair a saved JSON run against the current org seat
                           roster, fetching missing or failed user/day responses.
  -Delay <seconds>         Seconds to wait between GitHub API calls.
                           Default: 5. Use 0 to disable.
  -DataRetentionDays <n>   Days of old generated data files to retain when
                           writing refresh or repair output. Default: 3.
  -RollingRefreshDays <n>  Maximum trailing UTC days to refresh because GitHub
                           billing data can be backfilled. Once the day before
                           yesterday is cached, refreshes only yesterday and
                           today. Default: 4.
  -DataDir <path>          Output folder. Default: .\data.
  -RunStamp <text>         Temporary save filename suffix.
                           Default: yyyyMMdd-HHmmss.

OUTPUT CONTRACT
  Default and -InputPath modes are read-only.

  -Refresh and -RepairInputFile write the latest raw run to:
    .\data\copilot-metrics.json

  -Refresh reuses compatible successful user/day responses already stored in
  copilot-metrics.json, fetches only missing or failed responses, and then
  atomically replaces the same JSON file.
  Failed user/day responses are saved as retryable cache entries instead of
  discarding successful work from the same run.
  Each fetched user/day response is checkpointed to copilot-metrics.json before
  the next API call, so interrupted runs resume from the last completed fetch.

AUTHENTICATION
  Not required for default or -InputPath mode.

  Token lookup order:
    1. -Token
    2. EnterpriseBillingToken
    3. GH_TOKEN
    4. GITHUB_TOKEN
    5. gh auth token --user <GitHubUser>
    6. gh auth token for the active account

EXTENSION NOTES
  Keep this -Help output current when new modes or output contracts are added.
  Expected future modes include org summaries, personal summaries, prediction
  inputs, and alternate ranking/grouping views.

NOTES
  For full-org refreshes, prefer top10-async.ps1 with -ThrottleLimit 20.
"@
    return
}

$enterpriseName = $Enterprise
$yearValue = $Year
$monthValue = $Month
$startDayValue = $StartDay
$endDayValue = $EndDay
$includeOrganizationFilterValue = $IncludeOrganizationFilter.IsPresent
$dataDirValue = $DataDir
$runStampValue = $RunStamp
$inputPathValue = $InputPath
$repairInputFileValue = $RepairInputFile
$refreshValue = $Refresh.IsPresent
$githubUserValue = $GitHubUser
$delaySecondsValue = $Delay
$dataRetentionDaysValue = $DataRetentionDays
$rollingRefreshDaysValue = $RollingRefreshDays
$usingDefaultInputPath = $false
$tokenSource = if ($Token) { '-Token' } else { $null }
$defaultMonthlyAICreditAllowance = 7000
$specialMonthlyAICreditAllowanceByUser = @{
    fhemmerrelias = 250000
}

if ($delaySecondsValue -lt 0) {
    throw '-Delay must be 0 or greater.'
}

if ($dataRetentionDaysValue -lt 0) {
    throw '-DataRetentionDays must be 0 or greater.'
}

if ($rollingRefreshDaysValue -lt 0) {
    throw '-RollingRefreshDays must be 0 or greater.'
}

if ($inputPathValue -and $repairInputFileValue) {
    throw 'Use either -InputPath or -RepairInputFile, not both.'
}

if ($refreshValue -and ($inputPathValue -or $repairInputFileValue)) {
    throw 'Use -Refresh without -InputPath or -RepairInputFile.'
}

if (-not $refreshValue -and -not $inputPathValue -and -not $repairInputFileValue) {
    $inputPathValue = Join-Path $dataDirValue 'copilot-metrics.json'
    $usingDefaultInputPath = $true
}

function Get-DefaultGitHubUser {
    if ($enterpriseName -eq 'bertelsmann' -and $Org -eq 'Relias-Engineering') {
        return 'fhemmerrelias'
    }

    $null
}

function Get-GitHubCliToken {
    param([string]$UserName)

    if ($UserName) {
        try {
            $userToken = gh auth token --user $UserName 2>$null
            if ($userToken) {
                return [pscustomobject]@{
                    Token  = [string]$userToken
                    Source = "gh auth token --user $UserName"
                }
            }
        }
        catch {
            throw "No GitHub CLI token found for -GitHubUser $UserName. Run 'gh auth login --user $UserName' or pass -Token."
        }

        throw "No GitHub CLI token found for -GitHubUser $UserName. Run 'gh auth login --user $UserName' or pass -Token."
    }

    try {
        $activeToken = gh auth token 2>$null
        if ($activeToken) {
            return [pscustomobject]@{
                Token  = [string]$activeToken
                Source = 'gh auth token for the active account'
            }
        }
    }
    catch {
        $null = $_
    }

    $null
}

if (-not $githubUserValue) {
    $githubUserValue = Get-DefaultGitHubUser
}

if (-not $inputPathValue -and -not $Token -and $env:EnterpriseBillingToken) {
    $Token = $env:EnterpriseBillingToken
    $tokenSource = 'EnterpriseBillingToken'
}

if (-not $inputPathValue -and -not $Token -and $env:GH_TOKEN) {
    $Token = $env:GH_TOKEN
    $tokenSource = 'GH_TOKEN'
}

if (-not $inputPathValue -and -not $Token -and $env:GITHUB_TOKEN) {
    $Token = $env:GITHUB_TOKEN
    $tokenSource = 'GITHUB_TOKEN'
}

if (-not $inputPathValue -and -not $Token) {
    $githubCliToken = Get-GitHubCliToken -UserName $githubUserValue
    if ($githubCliToken) {
        $Token = $githubCliToken.Token
        $tokenSource = $githubCliToken.Source
    }
}

if (-not $inputPathValue -and -not $Token) {
    throw 'No GitHub token found. Set EnterpriseBillingToken, GH_TOKEN, GITHUB_TOKEN, run gh auth login, or pass -Token.'
}

$headers = @{
    Accept                   = 'application/vnd.github+json'
    Authorization            = "Bearer $Token"
    'X-GitHub-Api-Version'   = $ApiVersion
}

$script:lastGitHubApiCallCompletedAt = $null

function Wait-GitHubApiDelay {
    if ($delaySecondsValue -le 0 -or $null -eq $script:lastGitHubApiCallCompletedAt) {
        return
    }

    $elapsed = [DateTimeOffset]::UtcNow - $script:lastGitHubApiCallCompletedAt
    $remainingDelay = $delaySecondsValue - $elapsed.TotalSeconds
    if ($remainingDelay -gt 0) {
        Start-Sleep -Seconds ([math]::Ceiling($remainingDelay))
    }
}

function Invoke-GitHubApi {
    param([Parameter(Mandatory)][string]$Path)

    $uri = "https://api.github.com$Path"
    Wait-GitHubApiDelay
    try {
        Invoke-RestMethod -Uri $uri -Headers $headers -Method Get
    }
    catch {
        $detail = $_.Exception.Message
        $response = $_.Exception.Response

        if ($response) {
            try {
                $stream = $response.GetResponseStream()
                if ($stream) {
                    $reader = [System.IO.StreamReader]::new($stream)
                    $body = $reader.ReadToEnd()
                    if ($body) { $detail = "$detail - $body" }
                }
            }
            catch {
                $null = $_
            }
        }

        if ($Path -like '*/copilot/billing*' -and $detail -match '404|Not Found') {
            $detail = "$detail. Copilot billing endpoints require an organization-owner token for '$Org'. Token source: $tokenSource"
        }

        throw [System.InvalidOperationException]::new("$detail [$Path]", $_.Exception)
    }
    finally {
        $script:lastGitHubApiCallCompletedAt = [DateTimeOffset]::UtcNow
    }
}

function Invoke-GitHubRawApi {
    param([Parameter(Mandatory)][string]$Path)

    $uri = "https://api.github.com$Path"
    Wait-GitHubApiDelay
    try {
        $response = Invoke-WebRequest -Uri $uri -Headers $headers -Method Get -UseBasicParsing
        $body = [string]$response.Content

        [pscustomobject]@{
            Path       = $Path
            StatusCode = [int]$response.StatusCode
            Body       = $body
            Json       = if ($body) { $body | ConvertFrom-Json } else { $null }
        }
    }
    catch {
        $detail = $_.Exception.Message
        $response = $_.Exception.Response
        $body = $null

        if ($response) {
            try {
                $stream = $response.GetResponseStream()
                if ($stream) {
                    $reader = [System.IO.StreamReader]::new($stream)
                    $body = $reader.ReadToEnd()
                    if ($body) { $detail = "$detail - $body" }
                }
            }
            catch {
                $null = $_
            }
        }

        throw [System.InvalidOperationException]::new("$detail [$Path]", $_.Exception)
    }
    finally {
        $script:lastGitHubApiCallCompletedAt = [DateTimeOffset]::UtcNow
    }
}

function Get-Encoded {
    param([Parameter(Mandatory)][string]$Value)

    [uri]::EscapeDataString($Value)
}

function Get-CopilotMetricsOutputPath {
    if (-not (Test-Path -Path $dataDirValue)) {
        New-Item -Path $dataDirValue -ItemType Directory | Out-Null
    }

    Join-Path $dataDirValue 'copilot-metrics.json'
}

function Save-CopilotMetricsRun {
    param(
        [Parameter(Mandatory)]$RunData,
        [Parameter(Mandatory)][string]$Path
    )

    $temporaryPath = "$Path.$runStampValue.tmp"
    $RunData | ConvertTo-Json -Depth 100 | Set-Content -Path $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Remove-ExpiredCopilotMetricsDataFile {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$CurrentPath)

    if (-not (Test-Path -LiteralPath $dataDirValue)) {
        return
    }

    $retentionCutoff = (Get-Date).AddDays(-$dataRetentionDaysValue)
    $currentFullPath = [System.IO.Path]::GetFullPath($CurrentPath)
    $cleanupPatterns = @(
        'copilot-metrics.backup-*.json',
        'copilot-metrics.bad-*.json',
        'copilot-metrics.json.*.tmp',
        'top10-*.json',
        'top10.json'
    )
    $removedCount = 0

    foreach ($pattern in $cleanupPatterns) {
        foreach ($file in @(Get-ChildItem -LiteralPath $dataDirValue -Filter $pattern -File -ErrorAction Stop)) {
            if ([System.IO.Path]::GetFullPath($file.FullName) -eq $currentFullPath) {
                continue
            }

            if ($file.LastWriteTime -ge $retentionCutoff) {
                continue
            }

            if ($PSCmdlet.ShouldProcess($file.FullName, 'Remove expired Copilot metrics data file')) {
                Remove-Item -LiteralPath $file.FullName -Force
                $removedCount++
            }
        }
    }

    if ($removedCount -gt 0) {
        Write-Information "Cleaned up $removedCount generated data file(s) older than $dataRetentionDaysValue day(s)."
    }
}

function Get-CopilotMetricsInputPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$AllowLegacyFallback
    )

    if (Test-Path -LiteralPath $Path) {
        return $Path
    }

    if (-not $AllowLegacyFallback) {
        return $Path
    }

    if (-not (Test-Path -LiteralPath $dataDirValue)) {
        return $Path
    }

    $legacyStableRun = Join-Path $dataDirValue 'top10.json'
    if (Test-Path -LiteralPath $legacyStableRun) {
        Write-Information "No copilot-metrics.json found; using legacy run $legacyStableRun"
        return $legacyStableRun
    }

    $legacyRun = Get-ChildItem -LiteralPath $dataDirValue -Filter 'top10-*.json' -File |
        Sort-Object -Property LastWriteTime, Name -Descending |
        Select-Object -First 1

    if ($legacyRun) {
        Write-Information "No copilot-metrics.json found; using latest legacy run $($legacyRun.FullName)"
        return $legacyRun.FullName
    }

    $Path
}

function Get-QueryDay {
    $daysInMonth = [DateTime]::DaysInMonth($yearValue, $monthValue)
    $lastDay = $endDayValue

    if ($lastDay -le 0) {
        $now = [DateTimeOffset]::UtcNow
        $lastDay = if ($now.Year -eq $yearValue -and $now.Month -eq $monthValue) {
            $now.Day
        }
        else {
            $daysInMonth
        }
    }

    if ($startDayValue -lt 1 -or $startDayValue -gt $daysInMonth) {
        throw "StartDay must be between 1 and $daysInMonth for $yearValue-$('{0:D2}' -f $monthValue)."
    }

    if ($lastDay -lt $startDayValue -or $lastDay -gt $daysInMonth) {
        throw "EndDay must be between StartDay and $daysInMonth for $yearValue-$('{0:D2}' -f $monthValue)."
    }

    $startDayValue..$lastDay
}

function Get-AlwaysRefreshDay {
    param(
        [Parameter(Mandatory)][int[]]$Day,
        $Responses
    )

    if ($rollingRefreshDaysValue -le 0) {
        return @()
    }

    $utcNow = [DateTimeOffset]::UtcNow
    if ($utcNow.Year -ne $yearValue -or $utcNow.Month -ne $monthValue) {
        return @()
    }

    $dayBeforeYesterday = $utcNow.Day - 2
    if ($dayBeforeYesterday -ge 1) {
        $responsesByDay = Get-ResponseDayLookup -Responses $Responses
        if (Test-ResponseNeedsRepair -Response $responsesByDay[$dayBeforeYesterday]) {
            return @()
        }
    }

    $refreshDayCount = [math]::Min(2, $rollingRefreshDaysValue)
    $firstRefreshDay = [math]::Max(1, $utcNow.Day - $refreshDayCount + 1)
    @($Day | Where-Object { $_ -ge $firstRefreshDay -and $_ -le $utcNow.Day })
}

function Get-UniqueLogin {
    param([string[]]$Login)

    $seen = @{}
    $uniqueLogins = [System.Collections.Generic.List[string]]::new()

    foreach ($login in @($Login)) {
        $loginValue = ([string]$login).Trim()
        $loginKey = Get-LoginLookupKey -Login $loginValue
        if (-not $loginKey) {
            continue
        }

        if (-not $seen.ContainsKey($loginKey)) {
            $seen[$loginKey] = $true
            $uniqueLogins.Add($loginValue)
        }
    }

    @($uniqueLogins | Sort-Object)
}

function Get-LoginLookupKey {
    param([AllowNull()][string]$Login)

    $loginText = ([string]$Login).Trim()
    if (-not $loginText) {
        return ''
    }

    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $loginText.ToCharArray()) {
        if ([System.Globalization.CharUnicodeInfo]::GetUnicodeCategory($character) -ne [System.Globalization.UnicodeCategory]::Format) {
            [void]$builder.Append($character)
        }
    }

    $builder.ToString().Normalize([System.Text.NormalizationForm]::FormC).ToUpperInvariant()
}

function Get-CopilotSeatLogin {
    $logins = [System.Collections.Generic.List[string]]::new()
    $page = 1
    $encodedOrg = Get-Encoded $Org

    do {
        $response = Invoke-GitHubApi "/orgs/$encodedOrg/copilot/billing/seats?per_page=100&page=$page"
        $seats = @($response.seats)

        foreach ($seat in $seats) {
            if ($seat.assignee.login) {
                $logins.Add([string]$seat.assignee.login)
            }
        }

        $page++
    } while ($seats.Count -eq 100)

    Get-UniqueLogin -Login $logins
}

function Get-UsageItem {
    param($Response)

    if ($null -eq $Response) { return @() }
    if ($Response -is [array]) { return @($Response) }

    foreach ($propertyName in @('items', 'usageItems', 'usage_items', 'data', 'results')) {
        $property = $Response.PSObject.Properties[$propertyName]
        if ($property -and $property.Value) {
            return @($property.Value)
        }
    }

    @($Response)
}

function Format-UsagePercent {
    param(
        [Parameter(Mandatory)][double]$Value,
        [Parameter(Mandatory)][double]$Total
    )

    if ($Total -le 0) {
        return '0.0%'
    }

    '{0:N1}%' -f (($Value / $Total) * 100)
}

function Get-MonthlyAICreditAllowance {
    param([Parameter(Mandatory)][string]$Login)

    if ($specialMonthlyAICreditAllowanceByUser.ContainsKey($Login)) {
        return [double]$specialMonthlyAICreditAllowanceByUser[$Login]
    }

    [double]$defaultMonthlyAICreditAllowance
}

function Test-CopilotPrReviewModel {
    param([string]$Model)

    $Model -and $Model.IndexOf('Code Review', [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-TopModelUsage {
    param([Parameter(Mandatory)][hashtable]$ModelUsage)

    $topModel = $ModelUsage.GetEnumerator() |
        Sort-Object -Property @{ Expression = { [double]$_.Value }; Descending = $true }, Name |
        Select-Object -First 1

    if (-not $topModel) {
        return [pscustomobject]@{
            Model    = ''
            Quantity = 0.0
        }
    }

    [pscustomobject]@{
        Model    = [string]$topModel.Name
        Quantity = [double]$topModel.Value
    }
}

function Convert-ResponseToUserUsage {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)]$Responses
    )

    $grossQuantity = 0.0
    $grossAmount = 0.0
    $netAmount = 0.0
    $daysWithUsage = 0
    $modelUsage = @{}
    $prReviewQuantity = 0.0

    foreach ($response in @($Responses)) {
        if (-not $response.Success) { continue }

        $items = @(Get-UsageItem $response.Response)
        $dayQuantity = 0.0

        foreach ($item in $items) {
            if ($null -ne $item.grossQuantity) {
                $quantity = [double]$item.grossQuantity
                $grossQuantity += $quantity
                $dayQuantity += $quantity

                if ($item.model) {
                    $model = [string]$item.model
                    if (-not $modelUsage.ContainsKey($model)) {
                        $modelUsage[$model] = 0.0
                    }

                    $modelUsage[$model] = [double]$modelUsage[$model] + $quantity

                    if (Test-CopilotPrReviewModel -Model $model) {
                        $prReviewQuantity += $quantity
                    }
                }
            }

            if ($null -ne $item.grossAmount) { $grossAmount += [double]$item.grossAmount }
            if ($null -ne $item.netAmount) { $netAmount += [double]$item.netAmount }
        }

        if ($dayQuantity -gt 0) { $daysWithUsage++ }
    }

    $topModel = Get-TopModelUsage -ModelUsage $modelUsage
    $monthlyAllowance = Get-MonthlyAICreditAllowance -Login $Login
    $remainingAllowance = [math]::Max(0.0, $monthlyAllowance - $grossQuantity)

    [pscustomobject]@{
        User             = $Login
        AICredits        = [math]::Round($grossQuantity, 3)
        MonthlyAllowance = [int][math]::Round($monthlyAllowance, 0)
        AllowanceUsedPct = Format-UsagePercent -Value $grossQuantity -Total $monthlyAllowance
        RemainingCredits = [int][math]::Round($remainingAllowance, 0)
        PRReviewPct      = Format-UsagePercent -Value $prReviewQuantity -Total $grossQuantity
        TopModel         = $topModel.Model
        TopModelPct      = Format-UsagePercent -Value $topModel.Quantity -Total $grossQuantity
        GrossCostUsd     = [math]::Round($grossAmount, 2)
        NetCostUsd       = [math]::Round($netAmount, 2)
        DaysWithUsage    = $daysWithUsage
    }
}

function Get-UserAICreditUsage {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)][int[]]$Days,
        [int[]]$DaysToFetch = @(),
        $ExistingResponses,
        [Parameter(Mandatory)][int]$UserIndex,
        [Parameter(Mandatory)][int]$UserCount,
        [Parameter(Mandatory)][string]$RefreshRunId,
        [scriptblock]$OnResponseFetched
    )

    $encodedEnterprise = Get-Encoded $enterpriseName
    $encodedLogin = Get-Encoded $Login
    $responsesByDay = Get-ResponseDayLookup -Responses $ExistingResponses
    $dayIndex = 0
    $failedFetchCount = 0

    foreach ($day in $DaysToFetch) {
        $dayIndex++
        $dayStatus = '{0} / {1}: {2}, missing day {3} / {4} ({5}-{6:D2}-{7:D2})' -f
            $UserIndex, $UserCount, $Login, $dayIndex, $DaysToFetch.Count, $yearValue, $monthValue, $day
        Write-Progress -Id 2 -ParentId 1 -Activity 'Fetching missing Copilot billing responses' `
            -Status $dayStatus `
            -PercentComplete (($dayIndex / $DaysToFetch.Count) * 100)

        $path = "/enterprises/$encodedEnterprise/settings/billing/premium_request/usage" +
            "?year=$yearValue&month=$monthValue&day=$day&user=$encodedLogin"

        if ($includeOrganizationFilterValue) {
            $encodedOrg = Get-Encoded $Org
            $path += "&organization=$encodedOrg"
        }

        $responseSucceeded = $false
        try {
            $rawResponse = Invoke-GitHubRawApi $path
            $refreshedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            $responsesByDay[[int]$day] = [pscustomobject]@{
                Day            = $day
                Path           = $path
                StatusCode     = $rawResponse.StatusCode
                Success        = $true
                RawJson        = $rawResponse.Body
                Response       = $rawResponse.Json
                RefreshedAtUtc = $refreshedAtUtc
                RefreshRunId   = $RefreshRunId
            }
            $responseSucceeded = $true
        }
        catch {
            $failedFetchCount++
            $refreshedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            Write-Warning "Caching failed response for $Login day $day`: $($_.Exception.Message)"
            $responsesByDay[[int]$day] = [pscustomobject]@{
                Day            = $day
                Path           = $path
                StatusCode     = $null
                Success        = $false
                RawJson        = $null
                Response       = $null
                Error          = $_.Exception.Message
                RefreshedAtUtc = $refreshedAtUtc
                RefreshRunId   = $RefreshRunId
            }
        }

        if ($OnResponseFetched) {
            $currentResponses = @($responsesByDay.GetEnumerator() |
                Sort-Object -Property Name |
                ForEach-Object { $_.Value })
            & $OnResponseFetched -Login $Login -Responses $currentResponses -Succeeded $responseSucceeded
        }
    }

    Write-Progress -Id 2 -Activity 'Fetching missing Copilot billing responses' -Completed
    $responses = @($responsesByDay.GetEnumerator() |
        Sort-Object -Property Name |
        ForEach-Object { $_.Value })

    [pscustomobject]@{
        Usage        = Convert-ResponseToUserUsage -Login $Login -Responses $responses
        Responses    = @($responses)
        FetchedCount = $DaysToFetch.Count - $failedFetchCount
        FailedCount  = $failedFetchCount
        ReusedCount  = $Days.Count - $DaysToFetch.Count
    }
}

function Get-BillingPath {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)][int]$Day
    )

    $encodedEnterprise = Get-Encoded $enterpriseName
    $encodedLogin = Get-Encoded $Login
    $path = "/enterprises/$encodedEnterprise/settings/billing/premium_request/usage" +
        "?year=$yearValue&month=$monthValue&day=$Day&user=$encodedLogin"

    if ($includeOrganizationFilterValue) {
        $encodedOrg = Get-Encoded $Org
        $path += "&organization=$encodedOrg"
    }

    $path
}

function Invoke-BillingPath {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)][int]$Day,
        [string]$Path
    )

    if (-not $Path) {
        $Path = Get-BillingPath -Login $Login -Day $Day
    }

    $rawResponse = Invoke-GitHubRawApi $Path
    [pscustomobject]@{
        Day        = $Day
        Path       = $Path
        StatusCode = $rawResponse.StatusCode
        Success    = $true
        RawJson    = $rawResponse.Body
        Response   = $rawResponse.Json
    }
}

function Get-RepairDay {
    param($Cache)

    if ($Cache.Days) {
        @($Cache.Days | ForEach-Object {
                foreach ($day in @($_)) { [int]$day }
            })
    }
    else {
        @(Get-QueryDay)
    }
}

function Test-ResponseNeedsRepair {
    param($Response)

    if (-not $Response) { return $true }
    if (-not $Response.Success) { return $true }
    if ($Response.StatusCode -and [int]$Response.StatusCode -ne 200) { return $true }
    if (-not $Response.Response -and -not $Response.RawJson) { return $true }

    $false
}

function Get-RunUserLookup {
    param($RunUser)

    $lookup = @{}
    foreach ($userEntry in @($RunUser)) {
        if ($userEntry.User) {
            $loginKey = Get-LoginLookupKey -Login $userEntry.User
            if (-not $loginKey) {
                continue
            }

            if ($lookup.ContainsKey($loginKey)) {
                $lookup[$loginKey] = Select-PreferredRunUserEntry -Existing $lookup[$loginKey] -Candidate $userEntry
            }
            else {
                $lookup[$loginKey] = $userEntry
            }
        }
    }

    $lookup
}

function Get-RunUserResponseCount {
    param($RunUser)

    @($RunUser.Responses).Count
}

function Select-PreferredRunUserEntry {
    param(
        $Existing,
        $Candidate
    )

    if (-not $Existing) {
        return $Candidate
    }

    if (-not $Candidate) {
        return $Existing
    }

    $existingSucceeded = [bool]$Existing.Success
    $candidateSucceeded = [bool]$Candidate.Success
    if ($candidateSucceeded -and -not $existingSucceeded) {
        return $Candidate
    }

    if ($candidateSucceeded -eq $existingSucceeded -and (Get-RunUserResponseCount -RunUser $Candidate) -gt (Get-RunUserResponseCount -RunUser $Existing)) {
        return $Candidate
    }

    $Existing
}

function Get-UniqueRunUser {
    param($RunUser)

    $lookup = Get-RunUserLookup -RunUser $RunUser
    @($lookup.GetEnumerator() |
        Sort-Object -Property Name |
        ForEach-Object { $_.Value })
}

function Test-CopilotMetricsCacheScope {
    param($Cache)

    if (-not $Cache) { return $false }
    if ([string]$Cache.Enterprise -ne $enterpriseName) { return $false }
    if ([string]$Cache.Organization -ne $Org) { return $false }
    if ([int]$Cache.Year -ne $yearValue) { return $false }
    if ([int]$Cache.Month -ne $monthValue) { return $false }
    if ($Cache.ApiVersion -and [string]$Cache.ApiVersion -ne $ApiVersion) { return $false }

    $cachedOrganizationFilter = $false
    if ($Cache.PSObject.Properties['IncludeOrganizationFilter']) {
        $cachedOrganizationFilter = [bool]$Cache.IncludeOrganizationFilter
    }

    $cachedOrganizationFilter -eq $includeOrganizationFilterValue
}

function Get-CompatibleCopilotMetricsCache {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $cache = Get-Content -Path $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if (Test-CopilotMetricsCacheScope -Cache $cache) {
        Write-MetricsSection -Title 'Cache'
        Write-MetricsDetail -Label 'Status' -Value 'Compatible'
        Write-MetricsDetail -Label 'Path' -Value $Path
        Write-MetricsDetail -Label 'Mode' -Value 'Reuse cached history; conditionally refresh recent UTC days'
        return $cache
    }

    Write-MetricsSection -Title 'Cache'
    Write-MetricsDetail -Label 'Status' -Value 'Different scope'
    Write-MetricsDetail -Label 'Path' -Value $Path
    Write-MetricsDetail -Label 'Mode' -Value 'Replace with this refresh'
    $null
}

function Get-ResponseDayLookup {
    param($Responses)

    $lookup = @{}
    foreach ($response in @($Responses)) {
        if ($response -and $response.PSObject.Properties['Day']) {
            $lookup[[int](@($response.Day)[0])] = $response
        }
    }

    $lookup
}

function Get-MissingResponseDay {
    param(
        [Parameter(Mandatory)][int[]]$Days,
        $Responses,
        [int[]]$AlwaysRefreshDay = @(),
        [string]$ResumeRefreshRunId
    )

    $responsesByDay = Get-ResponseDayLookup -Responses $Responses
    $alwaysRefreshDayLookup = @{}
    foreach ($day in $AlwaysRefreshDay) {
        $alwaysRefreshDayLookup[[int]$day] = $true
    }

    @($Days | Where-Object {
            $day = [int]$_
            $existingResponse = $responsesByDay[$day]
            if ($alwaysRefreshDayLookup.ContainsKey($day)) {
                if ($ResumeRefreshRunId -and $existingResponse -and $existingResponse.PSObject.Properties['RefreshRunId'] -and [string]$existingResponse.RefreshRunId -eq $ResumeRefreshRunId) {
                    $false
                }
                else {
                    $true
                }
            }
            else {
                Test-ResponseNeedsRepair -Response $existingResponse
            }
        })
}

function Get-RunResponseDay {
    param($RunUser)

    @($RunUser | ForEach-Object {
            foreach ($response in @($_.Responses)) {
                if ($response -and $response.PSObject.Properties['Day']) {
                    [int](@($response.Day)[0])
                }
            }
        }) | Sort-Object -Unique
}

function ConvertTo-RunUserEntry {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)]$Responses
    )

    $responseErrors = @($Responses |
        Where-Object { -not $_.Success } |
        ForEach-Object {
            if ($_.PSObject.Properties['Error']) { $_.Error } else { 'Unknown response error' }
        })

    [pscustomobject]@{
        User      = $Login
        Success   = $responseErrors.Count -eq 0
        Responses = @($Responses)
        Error     = $responseErrors -join '; '
    }
}

function Get-PersistedRunUser {
    param(
        [Parameter(Mandatory)][hashtable]$UserLookup,
        [Parameter(Mandatory)][string[]]$Login,
        [Parameter(Mandatory)][bool]$PreserveAllCachedUsers
    )

    if ($PreserveAllCachedUsers) {
        return @($UserLookup.GetEnumerator() |
            Sort-Object -Property Name |
            ForEach-Object { $_.Value })
    }

    @(Get-UniqueLogin -Login $Login | ForEach-Object {
            $loginKey = Get-LoginLookupKey -Login $_
            if ($UserLookup.ContainsKey($loginKey)) {
                $UserLookup[$loginKey]
            }
            else {
                Get-MissingRunUser -Login $_
            }
        })
}

function Get-CopilotMetricsRunData {
    param(
        [Parameter(Mandatory)]$RunUser,
        [Parameter(Mandatory)][int[]]$RefreshedDay,
        [Parameter(Mandatory)][int]$FetchedResponseCount,
        [Parameter(Mandatory)][int]$FailedResponseCount,
        [Parameter(Mandatory)][int]$ReusedResponseCount,
        [Parameter(Mandatory)][string]$RefreshRunId,
        [Parameter(Mandatory)][string]$RefreshStatus
    )

    $persistedDays = @(Get-RunResponseDay -RunUser $RunUser)
    if ($persistedDays.Count -eq 0) {
        $persistedDays = $RefreshedDay
    }

    [pscustomobject]@{
        GeneratedAtUtc            = [DateTimeOffset]::UtcNow.ToString('o')
        Enterprise                = $enterpriseName
        Organization              = $Org
        Year                      = $yearValue
        Month                     = $monthValue
        Days                      = $persistedDays
        RefreshedDays             = $RefreshedDay
        ApiVersion                = $ApiVersion
        IncludeOrganizationFilter = $includeOrganizationFilterValue
        RefreshRunId              = $RefreshRunId
        RefreshStatus             = $RefreshStatus
        FetchedResponseCount      = $FetchedResponseCount
        FailedResponseCount       = $FailedResponseCount
        ReusedResponseCount       = $ReusedResponseCount
        Users                     = @($RunUser)
    }
}

function Get-MissingRunUser {
    param([Parameter(Mandatory)][string]$Login)

    [pscustomobject]@{
        User      = $Login
        Success   = $false
        Responses = @()
        Error     = 'Missing from input cache.'
    }
}

function Merge-RepairedUser {
    param(
        [Parameter(Mandatory)]$UserEntry,
        [Parameter(Mandatory)][int[]]$RepairDays
    )

    $responses = @($UserEntry.Responses)
    $responsesByDay = @{}
    $repairCount = 0

    foreach ($response in $responses) {
        if ($response.PSObject.Properties['Day']) {
            $responsesByDay[[int](@($response.Day)[0])] = $response
        }
    }

    foreach ($dayValue in $RepairDays) {
        $day = [int]$dayValue
        $existing = $responsesByDay[$day]

        if (Test-ResponseNeedsRepair -Response $existing) {
            $path = if ($existing -and $existing.Path) {
                [string]$existing.Path
            }
            else {
                Get-BillingPath -Login $UserEntry.User -Day $day
            }

            try {
                $responsesByDay[$day] = Invoke-BillingPath -Login $UserEntry.User -Day $day -Path $path
            }
            catch {
                $responsesByDay[$day] = [pscustomobject]@{
                    Day        = $day
                    Path       = $path
                    StatusCode = $null
                    Success    = $false
                    RawJson    = $null
                    Response   = $null
                    Error      = $_.Exception.Message
                }
            }

            $repairCount++
        }
    }

    $mergedResponses = @($responsesByDay.GetEnumerator() |
        Sort-Object -Property Name |
        ForEach-Object { $_.Value })
    $errors = @($mergedResponses |
        Where-Object { -not $_.Success } |
        ForEach-Object {
            if ($_.PSObject.Properties['Error']) { $_.Error } else { 'Unknown response error' }
        })

    [pscustomobject]@{
        UserEntry   = [pscustomobject]@{
            User      = [string]$UserEntry.User
            Success   = $errors.Count -eq 0
            Responses = $mergedResponses
            Error     = $errors -join '; '
        }
        RepairCount = $repairCount
    }
}

function Show-TopUser {
    param(
        [Parameter(Mandatory)]$RunUser,
        [Parameter(Mandatory)][string]$Label
    )

    $topUsers = @(Get-UniqueRunUser -RunUser $RunUser | ForEach-Object {
            Convert-ResponseToUserUsage -Login $_.User -Responses $_.Responses
        }) |
        Sort-Object -Property AICredits -Descending |
        Select-Object -First $Top

    Write-MetricsSection -Title $Label

    $topUsers | Format-Table -AutoSize
}

if ($inputPathValue) {
    $inputPathValue = Get-CopilotMetricsInputPath -Path $inputPathValue -AllowLegacyFallback $usingDefaultInputPath

    if (-not (Test-Path -LiteralPath $inputPathValue)) {
        throw "No saved Copilot metrics run found at $inputPathValue. Run .\scripts\$(Split-Path -Leaf $PSCommandPath) -Refresh to create it."
    }

    $cache = Get-Content -Path $inputPathValue -Raw -Encoding UTF8 | ConvertFrom-Json
    Show-TopUser -RunUser $cache.Users -Label "Top $Top Copilot AI Credit consumers from $inputPathValue"
    return
}

if ($repairInputFileValue) {
    $cache = Get-Content -Path $repairInputFileValue -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($cache.Enterprise) { $enterpriseName = [string]$cache.Enterprise }
    if ($cache.Organization) { $Org = [string]$cache.Organization }
    if ($cache.Year) { $yearValue = [int]$cache.Year }
    if ($cache.Month) { $monthValue = [int]$cache.Month }

    $repairDays = @(Get-RepairDay -Cache $cache)
    Write-Information "Fetching Copilot seats for $Org..."
    $seatLogins = @(Get-CopilotSeatLogin)
    if ($seatLogins.Count -eq 0) {
        throw "No Copilot users found for $Org."
    }

    $inputUserCount = @($cache.Users).Count
    $userLookup = Get-RunUserLookup -RunUser $cache.Users
    $missingLogins = @($seatLogins | Where-Object { -not $userLookup.ContainsKey((Get-LoginLookupKey -Login $_)) })
    Write-Information "Repair mode found $($seatLogins.Count) current Copilot seat(s); input contains $inputUserCount user(s); $($missingLogins.Count) seat user(s) missing."

    $repairedUsers = [System.Collections.Generic.List[object]]::new()
    $repairCount = 0

    foreach ($login in $seatLogins) {
        $loginKey = Get-LoginLookupKey -Login $login
        $userEntry = if ($userLookup.ContainsKey($loginKey)) {
            $userLookup[$loginKey]
        }
        else {
            Get-MissingRunUser -Login ([string]$login)
        }

        $repair = Merge-RepairedUser -UserEntry $userEntry -RepairDays $repairDays
        $repairedUsers.Add($repair.UserEntry)
        $repairCount += $repair.RepairCount
    }

    if ($repairCount -eq 0) {
        Write-Information "No failed or missing responses found for current $Org seats in $repairInputFileValue."
        Show-TopUser -RunUser $repairedUsers -Label "Top $Top Copilot AI Credit consumers from $repairInputFileValue"
        return
    }

    $cachePath = Get-CopilotMetricsOutputPath
    $runData = [pscustomobject]@{
        GeneratedAtUtc       = [DateTimeOffset]::UtcNow.ToString('o')
        Enterprise           = $enterpriseName
        Organization         = $Org
        Year                 = $yearValue
        Month                = $monthValue
        Days                 = $repairDays
        ApiVersion           = $ApiVersion
        IncludeOrganizationFilter = $includeOrganizationFilterValue
        RepairedFrom              = $repairInputFileValue
        RepairedRequestCount      = $repairCount
        SeatCount                 = $seatLogins.Count
        InputUserCount            = $inputUserCount
        MissingUserCount          = $missingLogins.Count
        Users                     = @($repairedUsers)
    }

    Save-CopilotMetricsRun -RunData $runData -Path $cachePath
    Remove-ExpiredCopilotMetricsDataFile -CurrentPath $cachePath
    Write-Information "Saved repaired raw GitHub responses to $cachePath"
    Show-TopUser -RunUser $repairedUsers -Label 'Top 10 Copilot AI Credit consumers from repaired data'
    return
}

$usingExplicitUsers = $User -and $User.Count -gt 0
$cachePath = Get-CopilotMetricsOutputPath
$existingCache = Get-CompatibleCopilotMetricsCache -Path $cachePath
$existingUserLookup = if ($existingCache) { Get-RunUserLookup -RunUser $existingCache.Users } else { @{} }
$mergedUserLookup = if ($existingCache) { Get-RunUserLookup -RunUser $existingCache.Users } else { @{} }

Write-MetricsSection -Title 'Scope'
Write-MetricsDetail -Label 'Enterprise' -Value $enterpriseName
Write-MetricsDetail -Label 'Organization' -Value $Org
Write-MetricsDetail -Label 'Users' -Value $(if ($usingExplicitUsers) { 'Explicit list' } else { 'Current Copilot seats' })

$logins = if ($usingExplicitUsers) {
    Get-UniqueLogin -Login $User
}
else {
    Write-MetricsDetail -Label 'Seat lookup' -Value 'Fetching from GitHub'
    @(Get-CopilotSeatLogin)
}

$days = @(Get-QueryDay)
$resumeRefreshRunId = if ($existingCache -and $existingCache.RefreshStatus -eq 'InProgress' -and $existingCache.RefreshRunId) {
    [string]$existingCache.RefreshRunId
}
else {
    $null
}
$refreshRunId = if ($resumeRefreshRunId) {
    $resumeRefreshRunId
}
else {
    [guid]::NewGuid().ToString('N')
}

if ($logins.Count -eq 0) {
    throw "No Copilot users found for $Org."
}

Write-MetricsDetail -Label 'User count' -Value ([string]$logins.Count)
Write-MetricsDetail -Label 'Period' -Value "$yearValue-$('{0:D2}' -f $monthValue) days $($days[0])-$($days[-1])"
if ($resumeRefreshRunId) {
    Write-MetricsDetail -Label 'Resume' -Value "Continuing interrupted run $resumeRefreshRunId"
}
if (-not $includeOrganizationFilterValue -and -not $usingExplicitUsers) {
    Write-MetricsDetail -Label 'Billing scope' -Value "Enterprise user usage filtered to $Org seat assignees"
}

$refreshPlan = [System.Collections.Generic.List[object]]::new()
$alwaysRefreshDayLookup = @{}
$loginIndex = 0
foreach ($login in $logins) {
    $loginIndex++
    $loginKey = Get-LoginLookupKey -Login $login
    $existingUserEntry = if ($existingUserLookup.ContainsKey($loginKey)) {
        $existingUserLookup[$loginKey]
    }
    else {
        $null
    }
    $existingResponses = @($existingUserEntry.Responses)
    $alwaysRefreshDays = @(Get-AlwaysRefreshDay -Day $days -Responses $existingResponses)
    foreach ($alwaysRefreshDay in $alwaysRefreshDays) {
        $alwaysRefreshDayLookup[[int]$alwaysRefreshDay] = $true
    }

    $missingDays = @(Get-MissingResponseDay -Days $days -Responses $existingResponses -AlwaysRefreshDay $alwaysRefreshDays -ResumeRefreshRunId $resumeRefreshRunId)

    $refreshPlan.Add([pscustomobject]@{
            Login             = [string]$login
            SeatIndex         = $loginIndex
            ExistingResponses = $existingResponses
            AlwaysRefreshDays = $alwaysRefreshDays
            MissingDays       = $missingDays
        })
}

$alwaysRefreshDays = @($alwaysRefreshDayLookup.GetEnumerator() |
    Sort-Object -Property Name |
    ForEach-Object { [int]$_.Name })

$missingResponseCount = 0
$missingUserCount = 0
$resumeCheckpointedResponseCount = 0
foreach ($planItem in $refreshPlan) {
    $missingDayCount = @($planItem.MissingDays).Count
    $missingResponseCount += $missingDayCount
    if ($missingDayCount -gt 0) {
        $missingUserCount++
    }
}
$plannedResponseCount = $logins.Count * $days.Count
if ($resumeRefreshRunId) {
    foreach ($planItem in $refreshPlan) {
        foreach ($response in @($planItem.ExistingResponses)) {
            if ($response -and
                $response.PSObject.Properties['RefreshRunId'] -and
                [string]$response.RefreshRunId -eq $resumeRefreshRunId) {
                $resumeCheckpointedResponseCount++
            }
        }
    }
}
$plannedReuseCount = $plannedResponseCount - $missingResponseCount
Write-MetricsSection -Title 'Refresh Plan'
Write-MetricsDetail -Label 'Fetch' -Value "$missingResponseCount user/day response(s) to fetch or refresh"
Write-MetricsDetail -Label 'Reuse' -Value "$plannedReuseCount cached response(s)"
if ($resumeRefreshRunId) {
    Write-MetricsDetail -Label 'Already done' -Value "$resumeCheckpointedResponseCount checkpointed response(s) from this interrupted run"
}
if ($alwaysRefreshDays.Count -gt 0) {
    Write-MetricsDetail -Label 'UTC refresh' -Value "Conditional recent day(s): $($alwaysRefreshDays -join ',')"
}
Write-MetricsDetail -Label 'Checkpoint' -Value 'After every fetched response'

$results = [System.Collections.Generic.List[object]]::new()
$runUsers = [System.Collections.Generic.List[object]]::new()
$fetchedResponseCount = 0
$failedResponseCount = 0
$reusedResponseCount = $plannedReuseCount
$missingUserIndex = 0
$refreshStartedAt = [DateTimeOffset]::UtcNow
$checkpointResponseCount = 0
$checkpointSaver = {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)]$Responses,
        [Parameter(Mandatory)][bool]$Succeeded
    )

    if ($Succeeded) {
        $script:fetchedResponseCount++
    }
    else {
        $script:failedResponseCount++
    }

    $script:checkpointResponseCount++
    $script:mergedUserLookup[(Get-LoginLookupKey -Login $Login)] = ConvertTo-RunUserEntry -Login $Login -Responses $Responses
    $checkpointUsers = Get-PersistedRunUser -UserLookup $script:mergedUserLookup -Login $script:logins -PreserveAllCachedUsers $script:usingExplicitUsers
    $checkpointData = Get-CopilotMetricsRunData `
        -RunUser $checkpointUsers `
        -RefreshedDay $script:days `
        -FetchedResponseCount $script:fetchedResponseCount `
        -FailedResponseCount $script:failedResponseCount `
        -ReusedResponseCount $script:reusedResponseCount `
        -RefreshRunId $script:refreshRunId `
        -RefreshStatus 'InProgress'
    Save-CopilotMetricsRun -RunData $checkpointData -Path $script:cachePath
}

foreach ($planItem in $refreshPlan) {
    $login = [string]$planItem.Login
    $seatIndex = [int]$planItem.SeatIndex
    $missingDays = @($planItem.MissingDays)
    if ($missingDays.Count -gt 0) {
        $missingUserIndex++
        Write-Progress -Id 1 -Activity 'Fetching missing per-user Copilot billing responses' `
            -Status "remaining $missingUserIndex / $missingUserCount; seat $seatIndex / $($logins.Count): $login ($($missingDays.Count) missing day(s))" `
            -PercentComplete (($missingUserIndex / $missingUserCount) * 100)
    }

    try {
        $userData = Get-UserAICreditUsage -Login $login -Days $days -DaysToFetch $missingDays -ExistingResponses @($planItem.ExistingResponses) -UserIndex $missingUserIndex -UserCount $missingUserCount -RefreshRunId $refreshRunId -OnResponseFetched $checkpointSaver
        if ($userData) {
            $results.Add($userData.Usage)
            $runUserEntry = ConvertTo-RunUserEntry -Login $login -Responses $userData.Responses
            $runUsers.Add($runUserEntry)
            $mergedUserLookup[(Get-LoginLookupKey -Login $login)] = $runUserEntry
        }
    }
    catch {
        Write-Warning "Skipping $login`: $($_.Exception.Message)"
        $runUserEntry = [pscustomobject]@{
                User      = $login
                Success   = $false
                Responses = @()
                Error     = $_.Exception.Message
            }
        $runUsers.Add($runUserEntry)
        $loginKey = Get-LoginLookupKey -Login $login
        if ($mergedUserLookup.ContainsKey($loginKey)) {
            $mergedUserLookup[$loginKey] = Select-PreferredRunUserEntry -Existing $mergedUserLookup[$loginKey] -Candidate $runUserEntry
        }
        else {
            $mergedUserLookup[$loginKey] = $runUserEntry
        }
    }
}

Write-Progress -Id 1 -Activity 'Fetching missing per-user Copilot billing responses' -Completed

$failedUsers = @($runUsers | Where-Object { -not $_.Success })
if ($failedUsers.Count -gt 0) {
    $sampleErrors = $failedUsers |
        Select-Object -First 5 -ExpandProperty Error
    Write-Warning "Refresh has $failedResponseCount failed response(s) across $($failedUsers.Count) user(s); saving partial cache so failed days can be retried next run. Sample errors: $($sampleErrors -join '; ')"
}

$persistedUsers = Get-PersistedRunUser -UserLookup $mergedUserLookup -Login $logins -PreserveAllCachedUsers $usingExplicitUsers
$runData = Get-CopilotMetricsRunData -RunUser $persistedUsers -RefreshedDay $days -FetchedResponseCount $fetchedResponseCount -FailedResponseCount $failedResponseCount -ReusedResponseCount $reusedResponseCount -RefreshRunId $refreshRunId -RefreshStatus 'Complete'

Save-CopilotMetricsRun -RunData $runData -Path $cachePath
Remove-ExpiredCopilotMetricsDataFile -CurrentPath $cachePath
$elapsed = [DateTimeOffset]::UtcNow - $refreshStartedAt
Write-MetricsSection -Title 'Result'
Write-MetricsDetail -Label 'Elapsed' -Value ('{0:mm\:ss}' -f $elapsed)
Write-MetricsDetail -Label 'Fetched' -Value ([string]$fetchedResponseCount)
Write-MetricsDetail -Label 'Failed' -Value ([string]$failedResponseCount)
Write-MetricsDetail -Label 'Reused' -Value ([string]$reusedResponseCount)
Write-MetricsDetail -Label 'Checkpointed' -Value ([string]$checkpointResponseCount)
Write-MetricsDetail -Label 'Saved' -Value $cachePath

$displayUsers = Get-UniqueRunUser -RunUser $persistedUsers
$topUsers = $displayUsers |
    ForEach-Object {
        Convert-ResponseToUserUsage -Login $_.User -Responses $_.Responses
    } |
    Sort-Object -Property AICredits -Descending |
    Select-Object -First $Top
$displayDays = @(Get-RunResponseDay -RunUser $displayUsers)
if ($displayDays.Count -eq 0) {
    $displayDays = $days
}

Write-MetricsSection -Title "Top $Top Copilot AI Credit consumers for $Org ($yearValue-$('{0:D2}' -f $monthValue) days $($displayDays[0])-$($displayDays[-1]))"

$topUsers | Format-Table -AutoSize
