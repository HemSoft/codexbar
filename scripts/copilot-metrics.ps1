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
    [string]$Token = $env:EnterpriseBillingToken,
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

function Show-CopilotMetricsHeader {
    Write-Information 'Co-pilot Metrics. Copyrights 2024 by Relias LLC.'
    Write-Information "Displayed at: $(Get-EasternTimestamp) Eastern Time"
    Write-Information ''
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
  -DataDir <path>          Output folder. Default: .\data.
  -RunStamp <text>         Backup filename suffix. Default: yyyyMMdd-HHmmss.

OUTPUT CONTRACT
  Default and -InputPath modes are read-only.

  -Refresh and -RepairInputFile write the latest raw run to:
    .\data\copilot-metrics.json

  If copilot-metrics.json already exists, renames it before writing the new file:
    .\data\copilot-metrics.backup-<RunStamp>.json

  If the backup name already exists, appends -1, -2, and so on.

AUTHENTICATION
  Not required for default or -InputPath mode.

  Token lookup order:
    1. EnterpriseBillingToken
    2. GH_TOKEN
    3. GITHUB_TOKEN
    4. gh auth token

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
$usingDefaultInputPath = $false

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

if (-not $inputPathValue -and -not $Token) { $Token = $env:GH_TOKEN }
if (-not $inputPathValue -and -not $Token) { $Token = $env:GITHUB_TOKEN }
if (-not $inputPathValue -and -not $Token) { $Token = (gh auth token 2>$null) }
if (-not $inputPathValue -and -not $Token) {
    throw 'No GitHub token found. Set EnterpriseBillingToken, GH_TOKEN, or run gh auth login.'
}

$headers = @{
    Accept                   = 'application/vnd.github+json'
    Authorization            = "Bearer $Token"
    'X-GitHub-Api-Version'   = $ApiVersion
}

function Invoke-GitHubApi {
    param([Parameter(Mandatory)][string]$Path)

    $uri = "https://api.github.com$Path"
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

        throw [System.InvalidOperationException]::new("$detail [$Path]", $_.Exception)
    }
}

function Invoke-GitHubRawApi {
    param([Parameter(Mandatory)][string]$Path)

    $uri = "https://api.github.com$Path"
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
}

function Get-Encoded {
    param([Parameter(Mandatory)][string]$Value)

    [uri]::EscapeDataString($Value)
}

function Get-CopilotMetricsOutputPath {
    if (-not (Test-Path -Path $dataDirValue)) {
        New-Item -Path $dataDirValue -ItemType Directory | Out-Null
    }

    $outputPath = Join-Path $dataDirValue 'copilot-metrics.json'
    if (Test-Path -LiteralPath $outputPath) {
        $backupPath = Get-UniqueBackupPath -Directory $dataDirValue -RunStamp $runStampValue
        Move-Item -LiteralPath $outputPath -Destination $backupPath
        Write-Information "Backed up previous copilot-metrics.json to $backupPath"
    }

    $outputPath
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

function Get-UniqueBackupPath {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$RunStamp
    )

    $backupPath = Join-Path $Directory "copilot-metrics.backup-$RunStamp.json"
    if (-not (Test-Path -LiteralPath $backupPath)) {
        return $backupPath
    }

    $index = 1
    do {
        $backupPath = Join-Path $Directory "copilot-metrics.backup-$RunStamp-$index.json"
        $index++
    } while (Test-Path -LiteralPath $backupPath)

    $backupPath
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

    $logins | Sort-Object -Unique
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

function Convert-ResponseToUserUsage {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)]$Responses
    )

    $grossQuantity = 0.0
    $grossAmount = 0.0
    $netAmount = 0.0
    $daysWithUsage = 0
    $models = [System.Collections.Generic.HashSet[string]]::new()

    foreach ($response in @($Responses)) {
        if (-not $response.Success) { continue }

        $items = @(Get-UsageItem $response.Response)
        $dayQuantity = 0.0

        foreach ($item in $items) {
            if ($null -ne $item.grossQuantity) {
                $quantity = [double]$item.grossQuantity
                $grossQuantity += $quantity
                $dayQuantity += $quantity
            }

            if ($null -ne $item.grossAmount) { $grossAmount += [double]$item.grossAmount }
            if ($null -ne $item.netAmount) { $netAmount += [double]$item.netAmount }
            if ($item.model) { $null = $models.Add([string]$item.model) }
        }

        if ($dayQuantity -gt 0) { $daysWithUsage++ }
    }

    [pscustomobject]@{
        User          = $Login
        AICredits     = [math]::Round($grossQuantity, 3)
        GrossCostUsd  = [math]::Round($grossAmount, 2)
        NetCostUsd    = [math]::Round($netAmount, 2)
        DaysWithUsage = $daysWithUsage
        Models        = ($models | Sort-Object) -join ', '
    }
}

function Get-UserAICreditUsage {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)][int[]]$Days
    )

    $encodedEnterprise = Get-Encoded $enterpriseName
    $encodedLogin = Get-Encoded $Login
    $responses = [System.Collections.Generic.List[object]]::new()

    foreach ($day in $Days) {
        $path = "/enterprises/$encodedEnterprise/settings/billing/premium_request/usage" +
            "?year=$yearValue&month=$monthValue&day=$day&user=$encodedLogin"

        if ($includeOrganizationFilterValue) {
            $encodedOrg = Get-Encoded $Org
            $path += "&organization=$encodedOrg"
        }

        $rawResponse = Invoke-GitHubRawApi $path
        $responses.Add([pscustomobject]@{
                Day        = $day
                Path       = $path
                StatusCode = $rawResponse.StatusCode
                Success    = $true
                RawJson    = $rawResponse.Body
                Response   = $rawResponse.Json
            })
    }

    [pscustomobject]@{
        Usage     = Convert-ResponseToUserUsage -Login $Login -Responses $responses
        Responses = @($responses)
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
            $lookup[[string]$userEntry.User] = $userEntry
        }
    }

    $lookup
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

    $topUsers = @($RunUser | ForEach-Object {
            Convert-ResponseToUserUsage -Login $_.User -Responses $_.Responses
        }) |
        Sort-Object -Property AICredits -Descending |
        Select-Object -First $Top

    Write-Information ''
    Write-Information $Label
    Write-Information ''

    $topUsers | Format-Table -AutoSize
}

if ($inputPathValue) {
    $inputPathValue = Get-CopilotMetricsInputPath -Path $inputPathValue -AllowLegacyFallback $usingDefaultInputPath

    if (-not (Test-Path -LiteralPath $inputPathValue)) {
        throw "No saved Copilot metrics run found at $inputPathValue. Run .\scripts\$(Split-Path -Leaf $PSCommandPath) -Refresh to create it."
    }

    $cache = Get-Content -Path $inputPathValue -Raw | ConvertFrom-Json
    Show-TopUser -RunUser $cache.Users -Label "Top $Top Copilot AI Credit consumers from $inputPathValue"
    return
}

if ($repairInputFileValue) {
    $cache = Get-Content -Path $repairInputFileValue -Raw | ConvertFrom-Json
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
    $missingLogins = @($seatLogins | Where-Object { -not $userLookup.ContainsKey([string]$_) })
    Write-Information "Repair mode found $($seatLogins.Count) current Copilot seat(s); input contains $inputUserCount user(s); $($missingLogins.Count) seat user(s) missing."

    $repairedUsers = [System.Collections.Generic.List[object]]::new()
    $repairCount = 0

    foreach ($login in $seatLogins) {
        $userEntry = if ($userLookup.ContainsKey([string]$login)) {
            $userLookup[[string]$login]
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
        RepairedFrom         = $repairInputFileValue
        RepairedRequestCount = $repairCount
        SeatCount            = $seatLogins.Count
        InputUserCount       = $inputUserCount
        MissingUserCount     = $missingLogins.Count
        Users                = @($repairedUsers)
    }

    $runData | ConvertTo-Json -Depth 100 | Set-Content -Path $cachePath -Encoding UTF8
    Write-Information "Saved repaired raw GitHub responses to $cachePath"
    Show-TopUser -RunUser $repairedUsers -Label 'Top 10 Copilot AI Credit consumers from repaired data'
    return
}

$usingExplicitUsers = $User -and $User.Count -gt 0

if ($usingExplicitUsers) {
    Write-Information 'Using explicit user list...'
}
else {
    Write-Information "Fetching Copilot seats for $Org..."
}

$logins = if ($usingExplicitUsers) {
    @($User | Sort-Object -Unique)
}
else {
    @(Get-CopilotSeatLogin)
}

$days = @(Get-QueryDay)

if ($logins.Count -eq 0) {
    throw "No Copilot users found for $Org."
}

Write-Information "Querying $($logins.Count) users for $yearValue-$('{0:D2}' -f $monthValue) days $($days[0])-$($days[-1]) AI Credit usage..."
if (-not $includeOrganizationFilterValue -and -not $usingExplicitUsers) {
    Write-Information "Using enterprise user scope; users are limited to current $Org seat assignees."
}

$results = [System.Collections.Generic.List[object]]::new()
$runUsers = [System.Collections.Generic.List[object]]::new()
$index = 0

foreach ($login in $logins) {
    $index++
    Write-Progress -Activity 'Querying per-user Copilot billing usage' `
        -Status "$index / $($logins.Count): $login" `
        -PercentComplete (($index / $logins.Count) * 100)

    try {
        $userData = Get-UserAICreditUsage -Login $login -Days $days
        if ($userData) {
            $results.Add($userData.Usage)
            $runUsers.Add([pscustomobject]@{
                    User      = $login
                    Success   = $true
                    Responses = @($userData.Responses)
                    Error     = $null
                })
        }
    }
    catch {
        Write-Warning "Skipping $login`: $($_.Exception.Message)"
        $runUsers.Add([pscustomobject]@{
                User      = $login
                Success   = $false
                Responses = @()
                Error     = $_.Exception.Message
            })
    }
}

Write-Progress -Activity 'Querying per-user Copilot billing usage' -Completed

$cachePath = Get-CopilotMetricsOutputPath
$runData = [pscustomobject]@{
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Enterprise     = $enterpriseName
    Organization   = $Org
    Year           = $yearValue
    Month          = $monthValue
    Days           = $days
    ApiVersion     = $ApiVersion
    Users          = @($runUsers)
}

$runData | ConvertTo-Json -Depth 100 | Set-Content -Path $cachePath -Encoding UTF8
Write-Information "Saved raw GitHub responses to $cachePath"

$topUsers = $results |
    Sort-Object -Property AICredits -Descending |
    Select-Object -First $Top

Write-Information ''
Write-Information "Top $Top Copilot AI Credit consumers for $Org ($yearValue-$('{0:D2}' -f $monthValue) days $($days[0])-$($days[-1]))"
Write-Information ''

$topUsers | Format-Table -AutoSize
