Set-StrictMode -Version Latest

$script:CollectionStrategy = 'monthly-ai-credit-v1'

function Get-CopilotMetricsCollectionStrategy {
    $script:CollectionStrategy
}

function ConvertTo-CopilotLoginKey {
    param([AllowNull()][string]$Login)

    $loginText = ([string]$Login).Trim()
    if (-not $loginText) {
        return ''
    }

    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $loginText.ToCharArray()) {
        $category = [System.Globalization.CharUnicodeInfo]::GetUnicodeCategory($character)
        if ($category -ne [System.Globalization.UnicodeCategory]::Format) {
            [void]$builder.Append($character)
        }
    }

    $builder.ToString().Normalize([System.Text.NormalizationForm]::FormC).ToUpperInvariant()
}

function Get-CopilotUniqueLogin {
    param([string[]]$Login)

    $seen = @{}
    $uniqueLogins = [System.Collections.Generic.List[string]]::new()

    foreach ($candidate in @($Login)) {
        $loginValue = ([string]$candidate).Trim()
        $loginKey = ConvertTo-CopilotLoginKey -Login $loginValue
        if (-not $loginKey -or $seen.ContainsKey($loginKey)) {
            continue
        }

        $seen[$loginKey] = $true
        $uniqueLogins.Add($loginValue)
    }

    @($uniqueLogins | Sort-Object)
}

function ConvertTo-QueryParameter {
    param([Parameter(Mandatory)][hashtable]$Parameter)

    @($Parameter.GetEnumerator() |
        Sort-Object -Property Name |
        ForEach-Object {
            '{0}={1}' -f [uri]::EscapeDataString([string]$_.Name), [uri]::EscapeDataString([string]$_.Value)
        }) -join '&'
}

function Get-CopilotAICreditUsagePath {
    param(
        [Parameter(Mandatory)][string]$Enterprise,
        [Parameter(Mandatory)][int]$Year,
        [Parameter(Mandatory)][int]$Month,
        [Parameter(Mandatory)][string]$Login,
        [string]$Organization
    )

    $query = @{
        month   = $Month
        product = 'Copilot'
        user    = $Login
        year    = $Year
    }
    if ($Organization) {
        $query.organization = $Organization
    }

    $encodedEnterprise = [uri]::EscapeDataString($Enterprise)
    "/enterprises/$encodedEnterprise/settings/billing/ai_credit/usage?$(ConvertTo-QueryParameter -Parameter $query)"
}

function Test-CopilotMetricsCacheScope {
    param(
        $Cache,
        [Parameter(Mandatory)][string]$Enterprise,
        [Parameter(Mandatory)][string]$Organization,
        [Parameter(Mandatory)][int]$Year,
        [Parameter(Mandatory)][int]$Month,
        [Parameter(Mandatory)][string]$ApiVersion,
        [Parameter(Mandatory)][bool]$IncludeOrganizationFilter
    )

    if (-not $Cache) { return $false }
    if (-not $Cache.PSObject.Properties['CollectionStrategy']) { return $false }
    if ([string]$Cache.CollectionStrategy -ne $script:CollectionStrategy) { return $false }
    if ([string]$Cache.Enterprise -ne $Enterprise) { return $false }
    if ([string]$Cache.Organization -ne $Organization) { return $false }
    if ([int]$Cache.Year -ne $Year) { return $false }
    if ([int]$Cache.Month -ne $Month) { return $false }
    if ($Cache.PSObject.Properties['ApiVersion'] -and
        $Cache.ApiVersion -and [string]$Cache.ApiVersion -ne $ApiVersion) {
        return $false
    }

    $cachedOrganizationFilter = $false
    if ($Cache.PSObject.Properties['IncludeOrganizationFilter']) {
        $cachedOrganizationFilter = [bool]$Cache.IncludeOrganizationFilter
    }

    $cachedOrganizationFilter -eq $IncludeOrganizationFilter
}

function Test-CopilotUserEntryNeedsRefresh {
    param($UserEntry)

    if (-not $UserEntry) { return $true }
    if (-not [bool]$UserEntry.Success) { return $true }

    $responses = @($UserEntry.Responses)
    if ($responses.Count -ne 1) { return $true }

    $response = $responses[0]
    if (-not $response) { return $true }
    if (-not $response.PSObject.Properties['CollectionStrategy']) { return $true }
    if ([string]$response.CollectionStrategy -ne $script:CollectionStrategy) { return $true }
    if (-not [bool]$response.Success) { return $true }
    if ($response.StatusCode -and [int]$response.StatusCode -ne 200) { return $true }
    if (-not $response.Response -and -not $response.RawJson) { return $true }

    $false
}

function Get-CopilotRefreshPlan {
    param(
        [Parameter(Mandatory)][string[]]$Login,
        [Parameter(Mandatory)][hashtable]$UserLookup,
        [string]$ResumeRefreshRunId,
        [switch]$RepairOnly
    )

    $plan = [System.Collections.Generic.List[object]]::new()
    foreach ($loginValue in @(Get-CopilotUniqueLogin -Login $Login)) {
        $loginKey = ConvertTo-CopilotLoginKey -Login $loginValue
        $existingEntry = if ($UserLookup.ContainsKey($loginKey)) { $UserLookup[$loginKey] } else { $null }
        $needsRefresh = Test-CopilotUserEntryNeedsRefresh -UserEntry $existingEntry
        $completedInResume = $false

        if (-not $needsRefresh -and $ResumeRefreshRunId -and
            $existingEntry.PSObject.Properties['RefreshRunId'] -and
            [string]$existingEntry.RefreshRunId -eq $ResumeRefreshRunId) {
            $completedInResume = $true
        }

        $shouldFetch = if ($RepairOnly) { $needsRefresh } else { -not $completedInResume }
        $plan.Add([pscustomobject]@{
                Login                    = $loginValue
                ExistingEntry            = $existingEntry
                ShouldFetch              = $shouldFetch
                ReusedFromCurrentRun     = $completedInResume
                NeedsRepair              = $needsRefresh
            })
    }

    @($plan)
}

function ConvertTo-CopilotMonthlyResponse {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$StatusCode,
        [Parameter(Mandatory)][string]$RawJson,
        [Parameter(Mandatory)]$Response,
        [Parameter(Mandatory)][string]$RefreshRunId,
        [string]$RefreshedAtUtc = ([DateTimeOffset]::UtcNow.ToString('o'))
    )

    [pscustomobject]@{
        CollectionStrategy = $script:CollectionStrategy
        Path               = $Path
        StatusCode         = $StatusCode
        Success            = $true
        RawJson            = $RawJson
        Response           = $Response
        RefreshedAtUtc     = $RefreshedAtUtc
        RefreshRunId       = $RefreshRunId
    }
}

function ConvertTo-CopilotUserEntry {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)][AllowEmptyString()][string]$RefreshRunId,
        $Response,
        $ExistingEntry,
        [string]$ErrorMessage,
        [string]$RefreshedAtUtc = ([DateTimeOffset]::UtcNow.ToString('o'))
    )

    if ($Response) {
        return [pscustomobject]@{
            User            = $Login
            Success         = $true
            Responses       = @($Response)
            Error           = ''
            RefreshedAtUtc  = $RefreshedAtUtc
            RefreshRunId    = $RefreshRunId
        }
    }

    $existingResponses = if ($ExistingEntry) { @($ExistingEntry.Responses) } else { @() }
    [pscustomobject]@{
        User            = $Login
        Success         = $false
        Responses       = $existingResponses
        Error           = $ErrorMessage
        RefreshedAtUtc  = $RefreshedAtUtc
        RefreshRunId    = $RefreshRunId
    }
}

function Get-CopilotUsageItem {
    param($Response)

    if ($null -eq $Response) { return @() }
    if ($Response -is [array]) { return @($Response) }

    foreach ($propertyName in @('items', 'usageItems', 'usage_items', 'data', 'results')) {
        $property = $Response.PSObject.Properties[$propertyName]
        if ($property -and $null -ne $property.Value) {
            return @($property.Value)
        }
    }

    @($Response)
}

function Format-CopilotUsagePercent {
    param(
        [Parameter(Mandatory)][double]$Value,
        [Parameter(Mandatory)][double]$Total
    )

    if ($Total -le 0) {
        return '0.0%'
    }

    '{0:N1}%' -f (($Value / $Total) * 100)
}

function ConvertTo-CopilotUserUsage {
    param(
        [Parameter(Mandatory)][string]$Login,
        [Parameter(Mandatory)]$Responses,
        [double]$MonthlyAllowance = 7000
    )

    $grossQuantity = 0.0
    $grossAmount = 0.0
    $netAmount = 0.0
    $modelUsage = @{}
    $prReviewQuantity = 0.0

    foreach ($response in @($Responses)) {
        foreach ($item in @(Get-CopilotUsageItem -Response $response.Response)) {
            $quantity = if ($null -ne $item.grossQuantity) { [double]$item.grossQuantity } else { 0.0 }
            $grossQuantity += $quantity
            if ($null -ne $item.grossAmount) { $grossAmount += [double]$item.grossAmount }
            if ($null -ne $item.netAmount) { $netAmount += [double]$item.netAmount }

            if ($item.model -and $quantity -gt 0) {
                $model = [string]$item.model
                if (-not $modelUsage.ContainsKey($model)) {
                    $modelUsage[$model] = 0.0
                }
                $modelUsage[$model] = [double]$modelUsage[$model] + $quantity
                if ($model.IndexOf('Code Review', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $prReviewQuantity += $quantity
                }
            }
        }
    }

    $topModel = $modelUsage.GetEnumerator() |
        Sort-Object -Property @{ Expression = { [double]$_.Value }; Descending = $true }, Name |
        Select-Object -First 1
    $topModelName = if ($topModel) { [string]$topModel.Name } else { '' }
    $topModelQuantity = if ($topModel) { [double]$topModel.Value } else { 0.0 }
    $remainingAllowance = [math]::Max(0.0, $MonthlyAllowance - $grossQuantity)

    [pscustomobject]@{
        User             = $Login
        AICredits        = [math]::Round($grossQuantity, 3)
        MonthlyAllowance = [int][math]::Round($MonthlyAllowance, 0)
        AllowanceUsedPct = Format-CopilotUsagePercent -Value $grossQuantity -Total $MonthlyAllowance
        RemainingCredits = [int][math]::Round($remainingAllowance, 0)
        PRReviewPct      = Format-CopilotUsagePercent -Value $prReviewQuantity -Total $grossQuantity
        TopModel         = $topModelName
        TopModelPct      = Format-CopilotUsagePercent -Value $topModelQuantity -Total $grossQuantity
        GrossCostUsd     = [math]::Round($grossAmount, 2)
        NetCostUsd       = [math]::Round($netAmount, 2)
    }
}

Export-ModuleMember -Function @(
    'ConvertTo-CopilotLoginKey',
    'ConvertTo-CopilotUserUsage',
    'Get-CopilotMetricsCollectionStrategy',
    'Get-CopilotRefreshPlan',
    'Get-CopilotUniqueLogin',
    'Get-CopilotAICreditUsagePath',
    'ConvertTo-CopilotMonthlyResponse',
    'ConvertTo-CopilotUserEntry',
    'Test-CopilotMetricsCacheScope',
    'Test-CopilotUserEntryNeedsRefresh'
)
