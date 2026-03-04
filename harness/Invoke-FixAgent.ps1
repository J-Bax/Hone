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

.PARAMETER Iteration
    Current iteration number for logging.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string]$Explanation,

    [string]$ConfigPath,

    [int]$Iteration = 0
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'fix' -Level 'info' -Message "Calling fix agent for: $FilePath" `
    -Iteration $Iteration

# ── Build the prompt ────────────────────────────────────────────────────────
$prompt = @"
Apply this specific optimization to the file and return the complete new file content.

## Target File
$FilePath

## Optimization to Apply
$Explanation

Read the file at the path above (relative to sample-api/), apply ONLY the
optimization described, and return the COMPLETE new file in a fenced code block.
No explanation, no commentary — just the code block.
"@

# ── Call the hone-fixer agent ───────────────────────────────────────────────
try {
    $copilotModel = if ($config.Copilot -and $config.Copilot.FixModel) {
        $config.Copilot.FixModel
    } elseif ($config.Copilot -and $config.Copilot.Model) {
        $config.Copilot.Model
    } else {
        'claude-opus-4.6'
    }

    $copilotOutput = copilot --agent hone-fixer --model $copilotModel -p $prompt -s `
        --no-auto-update --no-ask-user 2>&1
    $copilotExitCode = $LASTEXITCODE

    $responseText = ($copilotOutput | Out-String).Trim()

    # Save the response
    $iterDir = Join-Path $repoRoot $config.Api.ResultsPath "iteration-$Iteration"
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
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'fix' -Level 'info' -Message "Fix agent returned code ($($codeBlock.Length) chars)" `
            -Iteration $Iteration
    }
    else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'fix' -Level 'warning' -Message 'Fix agent response did not contain a code block' `
            -Iteration $Iteration
    }
}
catch {
    $result = [ordered]@{
        Success      = $false
        CodeBlock    = $null
        Response     = "Error: $_"
        ResponsePath = $null
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'fix' -Level 'warning' -Message "Fix agent failed: $_" `
        -Iteration $Iteration
}

return [PSCustomObject]$result
