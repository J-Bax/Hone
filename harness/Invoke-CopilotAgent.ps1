<#
.SYNOPSIS
    Unified runner for Copilot CLI agent invocations.

.DESCRIPTION
    Handles the common boilerplate for calling copilot CLI agents:
    model resolution, UTF-8 encoding, spinner, timeout, response saving,
    JSON extraction, and retry logic.

.PARAMETER AgentName
    Name of the copilot agent (e.g., 'hone-analyst', 'hone-classifier', 'hone-fixer').

.PARAMETER Prompt
    The prompt text to send to the agent.

.PARAMETER ModelConfigKey
    Config key for model override (e.g., 'AnalysisModel'). Looked up under config.Copilot.

.PARAMETER DefaultModel
    Fallback model if not configured. Default: 'claude-opus-4.6'.

.PARAMETER SpinnerMessage
    Message to display in the spinner while the agent runs.

.PARAMETER CompletionMessage
    Message to display when the spinner completes.

.PARAMETER ResponsePath
    File path to save the raw response text.

.PARAMETER MaxRetries
    Number of retry attempts on failure. Default: 0 (no retries).

.PARAMETER RetryPromptSuffix
    Text appended to the prompt on retry attempts.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.OUTPUTS
    PSCustomObject with: Success, ExitCode, RawOutput, ResponseText, ParsedJson, TimedOut
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AgentName,

    [Parameter(Mandatory)]
    [string]$Prompt,

    [string]$ModelConfigKey,

    [string]$DefaultModel = 'claude-opus-4.6',

    [string]$SpinnerMessage,

    [string]$CompletionMessage,

    [string]$ResponsePath,

    [int]$MaxRetries = 0,

    [string]$RetryPromptSuffix,

    [string]$ConfigPath,

    [string]$MockResponsePath
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force
. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

$config = Get-HoneConfig -ConfigPath $ConfigPath

# DryRun mock support — return canned response instead of calling copilot
if ($MockResponsePath -and (Test-Path $MockResponsePath)) {
    $mockResponse = Get-Content $MockResponsePath -Raw
    $parsedJson = $null
    try { $parsedJson = $mockResponse | ConvertFrom-Json } catch {}
    if ($ResponsePath) {
        $responseDir = Split-Path -Parent $ResponsePath
        if (-not (Test-Path $responseDir)) { New-Item -ItemType Directory -Path $responseDir -Force | Out-Null }
        $mockResponse | Out-File -FilePath $ResponsePath -Encoding utf8
    }
    return [PSCustomObject]([ordered]@{
        Success      = $true
        ExitCode     = 0
        RawOutput    = $mockResponse
        ResponseText = $mockResponse
        ParsedJson   = $parsedJson
        TimedOut     = $false
    })
}

# ── Resolve model ───────────────────────────────────────────────────────────
$copilotModel = $DefaultModel
if ($ModelConfigKey -and $config.Copilot -and $config.Copilot.ContainsKey($ModelConfigKey)) {
    $copilotModel = $config.Copilot[$ModelConfigKey]
} elseif ($config.Copilot -and $config.Copilot.Model) {
    $copilotModel = $config.Copilot.Model
}

# ── Resolve timeout ─────────────────────────────────────────────────────────
$timeoutSec = 600
if ($config.Copilot -and $config.Copilot.AgentTimeoutSec) {
    $timeoutSec = $config.Copilot.AgentTimeoutSec
}

# ── Retry loop ──────────────────────────────────────────────────────────────
$lastError = $null
for ($attempt = 0; $attempt -le $MaxRetries; $attempt++) {
    $spinner = $null
    try {
        $effectivePrompt = if ($attempt -gt 0 -and $RetryPromptSuffix) {
            $Prompt + "`n`n$RetryPromptSuffix"
        } else {
            $Prompt
        }

        $retryLabel = if ($attempt -gt 0) { " (retry $attempt/$MaxRetries)" } else { '' }
        $spinMsg = if ($SpinnerMessage) { "$SpinnerMessage$retryLabel" } else { "Running $AgentName$retryLabel" }

        # UTF-8 encoding for copilot CLI output
        $prevEncoding = [Console]::OutputEncoding
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $spinner = Start-Spinner -Message $spinMsg

        $copilotResult = Invoke-CopilotWithTimeout `
            -ArgumentList @('--agent', $AgentName, '--model', $copilotModel, '-p', $effectivePrompt, '-s', '--no-auto-update', '--no-ask-user') `
            -TimeoutSec $timeoutSec

        [Console]::OutputEncoding = $prevEncoding

        $responseText = if ($copilotResult.Output) { $copilotResult.Output.Trim() } else { '' }

        $completionMsg = if ($CompletionMessage) { $CompletionMessage } else { "$AgentName complete" }
        Stop-Spinner -Spinner $spinner -CompletionMessage $completionMsg
        $spinner = $null

        # Save response if path provided
        if ($ResponsePath) {
            $responseDir = Split-Path -Parent $ResponsePath
            if (-not (Test-Path $responseDir)) {
                New-Item -ItemType Directory -Path $responseDir -Force | Out-Null
            }
            $responseText | Out-File -FilePath $ResponsePath -Encoding utf8
        }

        if ($copilotResult.TimedOut) {
            return [PSCustomObject]([ordered]@{
                Success      = $false
                ExitCode     = -1
                RawOutput    = $responseText
                ResponseText = $responseText
                ParsedJson   = $null
                TimedOut     = $true
            })
        }

        # Parse JSON — strip code fences, sanitize JS literals, extract first JSON object
        $jsonText = $responseText -replace '(?s)^```(?:json)?\s*', '' -replace '(?s)\s*```\s*$', ''
        $jsonText = $jsonText -replace '(?<=[\s,:[\{])\bNaN\b', 'null'
        $jsonText = $jsonText -replace '(?<=[\s,:[\{])-?Infinity\b', 'null'
        $parsedJson = $null
        if ($jsonText -match '(?s)(\{.+\})') {
            try {
                $parsedJson = $Matches[1] | ConvertFrom-Json
            } catch {
                # JSON parse failed — will be null
            }
        }

        return [PSCustomObject]([ordered]@{
            Success      = ($copilotResult.ExitCode -eq 0)
            ExitCode     = $copilotResult.ExitCode
            RawOutput    = $copilotResult.Output
            ResponseText = $responseText
            ParsedJson   = $parsedJson
            TimedOut     = $false
        })
    }
    catch {
        if ($spinner) { Stop-Spinner -Spinner $spinner -CompletionMessage $null }
        [Console]::OutputEncoding = $prevEncoding
        if ($_ -is [System.Management.Automation.PipelineStoppedException]) { throw }
        $lastError = $_
        if ($attempt -lt $MaxRetries) { continue }
    }
}

# All retries exhausted
return [PSCustomObject]([ordered]@{
    Success      = $false
    ExitCode     = -1
    RawOutput    = ''
    ResponseText = "Error after $($MaxRetries + 1) attempts: $lastError"
    ParsedJson   = $null
    TimedOut     = $false
})
