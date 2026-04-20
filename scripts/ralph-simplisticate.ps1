# Runs ralph to reduce complexity and code smells on a dedicated feature branch.
param(
    [switch]$Autopilot,
    [switch]$NoAudio,
    [ValidateSet('sonnet', 'opus46', 'opus47', 'gpt')]
    [string]$Model,
    [string]$WorkUntil
)
$ErrorActionPreference = 'Stop'

# Resolve ralph.ps1 — direct call needed; splatting doesn't survive the alias's @args forwarding
$_ralphCmd = Get-Command ralph -ErrorAction SilentlyContinue
$_ralph = if ($_ralphCmd.CommandType -eq 'Function' -and $_ralphCmd.ScriptBlock -match "'([^']+)'") {
    $matches[1]
} elseif ($_ralphCmd.Source) { $_ralphCmd.Source } else { $null }
if (-not $_ralph -or -not (Test-Path $_ralph)) { throw "Cannot resolve ralph.ps1 from 'ralph' command" }

$passThru = @{}
if ($Autopilot) { $passThru['Autopilot'] = $true }
if ($NoAudio) { $passThru['NoAudio'] = $true }
if ($Model) { $passThru['Model'] = $Model }
if ($WorkUntil) { $passThru['WorkUntil'] = $WorkUntil }
& $_ralph -Prompt "Find patterns of complexity or code repetition and traditional code smells and fix them." -Branch "feature/simplisticate" -CleanupWorktree -Max 10 @passThru
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
