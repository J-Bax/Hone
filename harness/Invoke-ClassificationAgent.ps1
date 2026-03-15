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

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot

$config = Get-HoneConfig -ConfigPath $ConfigPath

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

# ── Call the hone-classifier agent ───────────────────────────────────────────
$iterDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}

$agentResult = & (Join-Path $PSScriptRoot 'Invoke-CopilotAgent.ps1') `
    -AgentName 'hone-classifier' `
    -Prompt $prompt `
    -ModelConfigKey 'ClassificationModel' `
    -DefaultModel 'claude-haiku-4.5' `
    -SpinnerMessage 'Classifying optimization scope' `
    -CompletionMessage 'Classification complete' `
    -ResponsePath (Join-Path $iterDir 'classification-response.json') `
    -MaxRetries 2 `
    -RetryPromptSuffix 'IMPORTANT: Respond with strict RFC 8259 JSON only. Do not use NaN, Infinity, undefined, or any JavaScript literals. Use null for missing numeric values.' `
    -ConfigPath $ConfigPath

$parsed = $agentResult.ParsedJson

if ($parsed -and $parsed.scope) {
    $scope = if ($parsed.scope -eq 'narrow') { 'narrow' } else { 'architecture' }

    Write-Status "    → Scope: $scope"

    $result = [ordered]@{
        Success   = ($agentResult.ExitCode -eq 0)
        Scope     = $scope
        Reasoning = $parsed.reasoning
        Response  = $agentResult.ResponseText
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'info' -Message "Classification: $scope — $($parsed.reasoning)" `
        -Experiment $Experiment
}
else {
    # Classification failed — default to architecture (safe)
    $result = [ordered]@{
        Success   = $false
        Scope     = 'architecture'
        Reasoning = "Classification failed: $($agentResult.ResponseText)"
        Response  = $agentResult.ResponseText
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'warning' `
        -Message "Classification agent failed — defaulting to architecture" `
        -Experiment $Experiment
}

return [PSCustomObject]$result
