<#
.SYNOPSIS
    Calls the hone-fixer CLI agent to generate optimized file content.

.DESCRIPTION
    Invokes the hone-fixer custom agent with the target file path and optimization
    description. The agent reads the current file and returns the complete optimized
    file content in a fenced code block. The harness handles all file I/O.

.PARAMETER FilePath
    Target file path (relative to sample-api/).

.PARAMETER Explanation
    Description of the optimization to apply.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER RootCauseDocument
    Optional markdown root-cause analysis document to include as context
    for the fix agent. When provided, this gives the fixer detailed evidence,
    theory, and proposed approaches.

.PARAMETER Experiment
    Current experiment number for logging.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string]$Explanation,

    [string]$RootCauseDocument,

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
"@

# ── Call the hone-fixer agent ───────────────────────────────────────────────
. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

try {
    $copilotModel = if ($config.Copilot -and $config.Copilot.FixModel) {
        $config.Copilot.FixModel
    } elseif ($config.Copilot -and $config.Copilot.Model) {
        $config.Copilot.Model
    } else {
        'claude-opus-4.6'
    }

    # Ensure UTF-8 decoding of copilot CLI output (prevents mojibake like ΓÇö for em-dash)
    $prevEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

    $spinner = Start-Spinner -Message "Generating optimized code for $FilePath"

    $copilotOutput = copilot --agent hone-fixer --model $copilotModel -p $prompt -s `
        --no-auto-update --no-ask-user 2>&1
    $copilotExitCode = $LASTEXITCODE

    [Console]::OutputEncoding = $prevEncoding

    $responseText = ($copilotOutput | Out-String).Trim()

    Stop-Spinner -Spinner $spinner -CompletionMessage "Code generation complete"

    # Save the response
    $iterDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
    if (-not (Test-Path $iterDir)) {
        New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
    }
    $responsePath = Join-Path $iterDir 'fix-response.md'
    $responseText | Out-File -FilePath $responsePath -Encoding utf8

    # Extract code block content
    $codeBlock = $null
    if ($responseText -match '(?ms)```(?:\w+)?\s*\r?\n(.+?)```') {
        $codeBlock = $Matches[1].TrimEnd()
    }

    $result = [ordered]@{
        Success      = ($copilotExitCode -eq 0 -and $null -ne $codeBlock)
        CodeBlock    = $codeBlock
        Response     = $responseText
        ResponsePath = $responsePath
    }

    if ($codeBlock) {
        Write-Status "    → Generated $($codeBlock.Length) chars for $FilePath"
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'experiment' -Level 'info' -Message "Fix agent returned code ($($codeBlock.Length) chars)" `
            -Experiment $Experiment
    }
    else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'experiment' -Level 'warning' -Message 'Fix agent response did not contain a code block' `
            -Experiment $Experiment
    }
}
catch {
    Stop-Spinner -Spinner $spinner -CompletionMessage $null
    if ($_ -is [System.Management.Automation.PipelineStoppedException]) { throw }
    $result = [ordered]@{
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'warning' -Message "Fix agent failed: $_" `
        -Experiment $Experiment
}

return [PSCustomObject]$result
