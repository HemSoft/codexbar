# Copyright (c) HemSoft Developments. All rights reserved.

[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$SkipToken
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Test-CommandExists {
    param([string]$Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

Write-Step "Checking Claude Code CLI"
if (-not (Test-CommandExists "claude")) {
    Write-Host "Claude Code CLI was not found on PATH." -ForegroundColor Yellow
    Write-Host "Install or sign into Claude Code, then rerun this script."
    exit 1
}

$claudeVersion = (& claude --version) -join "`n"
Write-Host "Found Claude Code: $claudeVersion"

if (-not $SkipToken) {
    Write-Step "Setting CLAUDE_CODE_OAUTH_TOKEN"
    Write-Host "This token is separate from MCP credentials. Generate a fresh one with:"
    Write-Host "  claude setup-token" -ForegroundColor Green
    Write-Host ""

    $secureToken = Read-Host "Paste the new Claude token" -AsSecureString
    $tokenBstr = [IntPtr]::Zero

    try {
        $tokenBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
        $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenBstr)
        $token = $token.Trim()
    }
    finally {
        if ($tokenBstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenBstr)
        }

        $secureToken.Dispose()
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        Write-Host "No token entered. Nothing was changed." -ForegroundColor Yellow
        exit 1
    }

    [Environment]::SetEnvironmentVariable(
        "CLAUDE_CODE_OAUTH_TOKEN",
        $token,
        [EnvironmentVariableTarget]::User)

    $env:CLAUDE_CODE_OAUTH_TOKEN = $token
}

Write-Step "Verifying user environment"
$savedToken = [Environment]::GetEnvironmentVariable(
    "CLAUDE_CODE_OAUTH_TOKEN",
    [EnvironmentVariableTarget]::User)

if ([string]::IsNullOrWhiteSpace($savedToken)) {
    Write-Host "CLAUDE_CODE_OAUTH_TOKEN is not set for the current user." -ForegroundColor Red
    exit 1
}

Write-Host "CLAUDE_CODE_OAUTH_TOKEN is set for the current user."
Write-Host "Token length: $($savedToken.Length)"

$projectPath = Join-Path $PSScriptRoot "src/CodexBar.App/CodexBar.App.csproj"

Write-Step "Next steps"
Write-Host "Fully quit CodexBar and start it again so it reads the new user environment."
Write-Host "To test this branch build directly, run:"
Write-Host "  dotnet run --project `"$projectPath`"" -ForegroundColor Green

if ($Run) {
    Write-Step "Starting CodexBar from this checkout"
    dotnet run --project $projectPath
}
