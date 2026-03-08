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

# ── Call the hone-classifier agent ──────────────────────────────────────────
. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

try {
    $copilotModel = if ($config.Copilot -and $config.Copilot.ClassificationModel) {
        $config.Copilot.ClassificationModel
    } elseif ($config.Copilot -and $config.Copilot.Model) {
        $config.Copilot.Model
    } else {
        'claude-haiku-4.5'
    }

    # Ensure UTF-8 decoding of copilot CLI output (prevents mojibake like ΓÇö for em-dash)
    $prevEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

    $spinner = Start-Spinner -Message 'Classifying optimization scope'

    $copilotOutput = copilot --agent hone-classifier --model $copilotModel -p $prompt -s `
        --no-auto-update --no-ask-user 2>&1
    $copilotExitCode = $LASTEXITCODE

    [Console]::OutputEncoding = $prevEncoding

    $responseText = ($copilotOutput | Out-String).Trim()

    Stop-Spinner -Spinner $spinner -CompletionMessage 'Classification complete'

    # Save the response
    $iterDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
    if (-not (Test-Path $iterDir)) {
        New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
    }
    $responsePath = Join-Path $iterDir 'classification-response.json'
    $responseText | Out-File -FilePath $responsePath -Encoding utf8

    # Parse JSON — strip any wrapping markdown code fences if present
    $jsonText = $responseText -replace '(?s)^```(?:json)?\s*', '' -replace '(?s)\s*```$', ''
    $parsed = $jsonText | ConvertFrom-Json

    $scope = if ($parsed.scope -eq 'narrow') { 'narrow' } else { 'architecture' }

    Write-Information "    → Scope: $scope" -InformationAction Continue

    $result = [ordered]@{
        Success   = ($copilotExitCode -eq 0 -and $null -ne $parsed.scope)
        Scope     = $scope
        Reasoning = $parsed.reasoning
        Response  = $responseText
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'info' -Message "Classification: $scope — $($parsed.reasoning)" `
        -Experiment $Experiment
}
catch {
    Stop-Spinner -Spinner $spinner -CompletionMessage $null
    if ($_ -is [System.Management.Automation.PipelineStoppedException]) { throw }
    # Default to architecture on failure (safe)
    $result = [ordered]@{
        Success   = $false
        Scope     = 'architecture'
        Reasoning = "Classification failed: $_"
        Response  = "Error: $_"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'warning' -Message "Classification agent failed: $_ — defaulting to architecture" `
        -Experiment $Experiment
}

return [PSCustomObject]$result
