<#
.SYNOPSIS
    Calls the hone-classifier CLI agent to determine change scope.

.DESCRIPTION
    Invokes the hone-classifier custom agent to classify a proposed optimization
    as NARROW or ARCHITECTURE. Returns structured JSON with scope and reasoning.

.PARAMETER FilePath
    Target file path (relative to sample-api/).

.PARAMETER Explanation
    Description of the proposed optimization.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Experiment
    Current experiment number for logging.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string]$Explanation,

    [string]$ConfigPath,

    [int]$Experiment = 0
)

function Write-Status ([string]$Message) {
    if ($Message -match '^\s*$' -or $Message -match '^[━═─╔╚╗╝║╠╣╦╩]') {
        Write-Information $Message -InformationAction Continue
    } else {
        Write-Information "[$(Get-Date -Format 'HH:mm:ss')] $Message" -InformationAction Continue
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Classifying change scope for: $FilePath" `
    -Experiment $Experiment

# ── Build the prompt ────────────────────────────────────────────────────────
$prompt = @"
Classify the scope of this proposed optimization.

## Target File
$FilePath

## Proposed Optimization
$Explanation

Read the target file at the path above (relative to sample-api/) to verify the
change can be contained to a single file.

Respond with JSON only. No markdown, no code blocks around the JSON.
"@

# ── Call the hone-classifier agent (with retry on invalid JSON) ──────────────
. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

$maxRetries = 2
$result = $null

for ($attempt = 0; $attempt -le $maxRetries; $attempt++) {
    $spinner = $null
    try {
        $copilotModel = if ($config.Copilot -and $config.Copilot.ClassificationModel) {
            $config.Copilot.ClassificationModel
        } elseif ($config.Copilot -and $config.Copilot.Model) {
            $config.Copilot.Model
        } else {
            'claude-haiku-4.5'
        }

        # On retry, augment the prompt with a strict JSON reminder
        $effectivePrompt = if ($attempt -gt 0) {
            $prompt + "`n`nIMPORTANT: Respond with strict RFC 8259 JSON only. Do not use NaN, Infinity, undefined, or any JavaScript literals. Use null for missing numeric values."
        } else {
            $prompt
        }

        # Ensure UTF-8 decoding of copilot CLI output (prevents mojibake like ΓÇö for em-dash)
        $prevEncoding = [Console]::OutputEncoding
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $retryLabel = if ($attempt -gt 0) { " (retry $attempt/$maxRetries)" } else { '' }
        $spinner = Start-Spinner -Message "Classifying optimization scope$retryLabel"

        $copilotOutput = copilot --agent hone-classifier --model $copilotModel -p $effectivePrompt -s `
            --no-auto-update --no-ask-user 2>&1
        $copilotExitCode = $LASTEXITCODE

        [Console]::OutputEncoding = $prevEncoding

        $responseText = ($copilotOutput | Out-String).Trim()

        Stop-Spinner -Spinner $spinner -CompletionMessage 'Classification complete'
        $spinner = $null

        # Save the response
        $iterDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
        if (-not (Test-Path $iterDir)) {
            New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
        }
        $responsePath = Join-Path $iterDir 'classification-response.json'
        $responseText | Out-File -FilePath $responsePath -Encoding utf8

        # Parse JSON — strip any wrapping markdown code fences if present
        $jsonText = $responseText -replace '(?s)^```(?:json)?\s*', '' -replace '(?s)\s*```$', ''

        # Sanitize JavaScript-style literals that are invalid in JSON
        $jsonText = $jsonText -replace '(?<=[\s,:[\{])\bNaN\b', 'null'
        $jsonText = $jsonText -replace '(?<=[\s,:[\{])-?Infinity\b', 'null'

        $parsed = $jsonText | ConvertFrom-Json

        $scope = if ($parsed.scope -eq 'narrow') { 'narrow' } else { 'architecture' }

        Write-Status "    → Scope: $scope"

        $result = [ordered]@{
            Success   = ($copilotExitCode -eq 0 -and $null -ne $parsed.scope)
            Scope     = $scope
            Reasoning = $parsed.reasoning
            Response  = $responseText
        }

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'analyze' -Level 'info' -Message "Classification: $scope — $($parsed.reasoning)" `
            -Experiment $Experiment

        break  # Success — exit retry loop
    }
    catch {
        if ($spinner) { Stop-Spinner -Spinner $spinner -CompletionMessage $null }
        if ($_ -is [System.Management.Automation.PipelineStoppedException]) { throw }

        if ($attempt -lt $maxRetries) {
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'analyze' -Level 'warning' `
                -Message "Classification attempt $($attempt + 1) failed: $_ — retrying" `
                -Experiment $Experiment
            continue
        }

        # All retries exhausted — default to architecture (safe)
        $result = [ordered]@{
            Success   = $false
            Scope     = 'architecture'
            Reasoning = "Classification failed after $($maxRetries + 1) attempts: $_"
            Response  = "Error: $_"
        }

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'analyze' -Level 'warning' `
            -Message "Classification agent failed after $($maxRetries + 1) attempts: $_ — defaulting to architecture" `
            -Experiment $Experiment
    }
}

return [PSCustomObject]$result
