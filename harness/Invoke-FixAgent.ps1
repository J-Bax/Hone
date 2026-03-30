<#
.SYNOPSIS
    Calls the hone-fixer CLI agent to generate optimized file content.

.DESCRIPTION
    Invokes the hone-fixer custom agent with the target file path and optimization
    description. The agent reads the current file and returns the complete optimized
    file content in a fenced code block. The harness handles all file I/O.

.PARAMETER FilePath
    Target file path (relative to the target project root).

.PARAMETER Explanation
    Description of the optimization to apply.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER TargetDir
    Root directory of the target project. Config paths are resolved relative to
    this directory and agent file exploration runs from this directory.

.PARAMETER TargetName
    Human-readable target name for prompt text.

.PARAMETER RootCauseDocument
    Optional markdown root-cause analysis document to include as context
    for the fix agent. When provided, this gives the fixer detailed evidence,
    theory, and proposed approaches.

.PARAMETER Experiment
    Current experiment number for logging.

.PARAMETER MockResponsePath
    Optional path to a canned agent response for deterministic testing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string]$Explanation,

    [string]$RootCauseDocument,

    [string]$ConfigPath,

    [string]$TargetDir,

    [string]$TargetName,

    [int]$Experiment = 0,

    [string]$MockResponsePath
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot

$config = Get-HoneConfig -ConfigPath $ConfigPath
if ($TargetDir) {
    $targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
    if (Test-Path $targetConfigPath) {
        $targetCfg = Import-PowerShellDataFile -Path $targetConfigPath
        $config = Merge-HoneConfig -Engine $config -Target $targetCfg
    }
}

$pathBase = if ($TargetDir) { $TargetDir } else { $repoRoot }
$targetLabel = if ($TargetName) { $TargetName } elseif ($TargetDir) { Split-Path -Path $TargetDir -Leaf } else { 'target project' }

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'experiment' -Level 'info' -Message "Calling fix agent for: $FilePath" `
    -Experiment $Experiment

# ── Build the prompt ────────────────────────────────────────────────────────
$rcaSection = ''
if ($RootCauseDocument) {
    $rcaSection = @"

## Root Cause Analysis

$RootCauseDocument

"@
}

$prompt = @"
Apply this specific optimization to the file and return the complete new file content.

## Target File
$FilePath

## Optimization to Apply
$Explanation
$rcaSection
Read the file at the path above (relative to sample-api/), apply ONLY the
optimization described, and return the COMPLETE new file in a fenced code block.
No explanation, no commentary — just the code block.
"@ -replace 'relative to sample-api/', "relative to the $targetLabel root"

# ── Call the hone-fixer agent ───────────────────────────────────────────────
$iterDir = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath "experiment-$Experiment"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}

$agentResult = & (Join-Path $PSScriptRoot 'Invoke-CopilotAgent.ps1') `
    -AgentName 'hone-fixer' `
    -Prompt $prompt `
    -ModelConfigKey 'FixModel' `
    -DefaultModel 'claude-opus-4.6' `
    -SpinnerMessage "Generating optimized code for $FilePath" `
    -CompletionMessage 'Code generation complete' `
    -ResponsePath (Join-Path $iterDir 'fix-response.md') `
    -ConfigPath $ConfigPath `
    -MockResponsePath $MockResponsePath `
    -WorkingDirectory $TargetDir `
    -Experiment $Experiment

$responsePath = Join-Path $iterDir 'fix-response.md'

# Extract code block content
$codeBlock = $null
if ($agentResult.ResponseText -match '(?ms)```(?:\w+)?\s*\r?\n(.+?)```') {
    $codeBlock = $Matches[1].TrimEnd()
}

$result = [ordered]@{
    Success = ($agentResult.ExitCode -eq 0 -and $null -ne $codeBlock)
    CodeBlock = $codeBlock
    Response = $agentResult.ResponseText
    ResponsePath = $responsePath
}

if ($codeBlock) {
    Write-Status "    → Generated $($codeBlock.Length) chars for $FilePath"
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'info' -Message "Fix agent returned code ($($codeBlock.Length) chars)" `
        -Experiment $Experiment
} else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'warning' -Message 'Fix agent response did not contain a code block' `
        -Experiment $Experiment
}

return [PSCustomObject]$result
