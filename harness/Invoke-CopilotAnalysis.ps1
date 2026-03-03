<#
.SYNOPSIS
    Invokes GitHub Copilot CLI to analyze performance and suggest optimizations.

.DESCRIPTION
    Constructs a detailed prompt with current performance metrics, baseline
    comparison, and source code context, then sends it to 'gh copilot suggest'
    to get optimization recommendations.

.PARAMETER CurrentMetrics
    PSCustomObject with current iteration metrics.

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics.

.PARAMETER ComparisonResult
    PSCustomObject from Compare-Results.ps1.

.PARAMETER Iteration
    Current iteration number.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$BaselineMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$ComparisonResult,

    [int]$Iteration = 0,

    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message 'Preparing Copilot analysis prompt' `
    -Iteration $Iteration

# ── Read source code context ────────────────────────────────────────────────
$apiProjectPath = Join-Path $repoRoot $config.Api.ProjectPath
$controllerFiles = Get-ChildItem -Path (Join-Path $apiProjectPath 'Controllers') -Filter '*.cs' -Recurse
$sourceContext = foreach ($file in $controllerFiles) {
    "// === $($file.Name) ===`n$(Get-Content $file.FullName -Raw)"
}

# ── Build the prompt ────────────────────────────────────────────────────────
$prompt = @"
I am optimizing a .NET 6 Web API for performance. Here are the current results:

## Current Performance (Iteration $Iteration)
- p95 Latency: $($CurrentMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($CurrentMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2))%
- Improvement vs baseline: $($ComparisonResult.ImprovementPct)%

## Baseline Performance
- p95 Latency: $($BaselineMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($BaselineMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($BaselineMetrics.HttpReqFailed.Rate * 100, 2))%

## Source Code (Controllers)
$($sourceContext -join "`n`n")

## Task
Suggest ONE specific, targeted code change that will improve at least one of
these metrics (lower p95 latency, higher RPS, or lower error rate) WITHOUT
regressing any other metric. I need a measurable improvement from the current
values — there are no fixed targets, just make it better.

Focus on the most impactful optimization. Provide the exact file to modify and the
complete replacement code. Common optimization patterns to consider:
- Adding database indexes
- Replacing N+1 queries with eager loading (.Include())
- Adding response caching
- Implementing pagination
- Moving filtering to the database layer (IQueryable)

Respond with:
1. The file path to modify
2. A brief explanation of the change and which metric it should improve
3. The complete new file content
"@

# Save the prompt for audit
$promptPath = Join-Path $repoRoot $config.Logging.OutputPath "copilot-prompt-iteration-$Iteration.md"
$prompt | Out-File -FilePath $promptPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Sending prompt to Copilot CLI (prompt saved to $promptPath)" `
    -Iteration $Iteration

# ── Call GitHub Copilot CLI ─────────────────────────────────────────────────
try {
    $copilotOutput = echo $prompt | gh copilot suggest -t shell 2>&1
    $copilotExitCode = $LASTEXITCODE

    # Save the response
    $responsePath = Join-Path $repoRoot $config.Logging.OutputPath "copilot-response-iteration-$Iteration.md"
    ($copilotOutput | Out-String) | Out-File -FilePath $responsePath -Encoding utf8

    $result = [ordered]@{
        Success      = ($copilotExitCode -eq 0)
        ExitCode     = $copilotExitCode
        Prompt       = $prompt
        Response     = ($copilotOutput | Out-String)
        PromptPath   = $promptPath
        ResponsePath = $responsePath
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'info' -Message 'Copilot response received' `
        -Iteration $Iteration
}
catch {
    $result = [ordered]@{
        Success      = $false
        ExitCode     = -1
        Prompt       = $prompt
        Response     = "Error: $_"
        PromptPath   = $promptPath
        ResponsePath = $null
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'error' -Message "Copilot CLI failed: $_" `
        -Iteration $Iteration
}

return [PSCustomObject]$result
