<#
.SYNOPSIS
    Calls the hone-analyst CLI agent to identify the next optimization opportunity.

.DESCRIPTION
    Builds a performance context prompt with metrics, source code, and optimization
    history, then calls the hone-analyst custom agent via 'copilot --agent'.
    Parses the JSON response and returns a structured result object.

.PARAMETER CurrentMetrics
    PSCustomObject with current/baseline metrics.

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics.

.PARAMETER ComparisonResult
    PSCustomObject from Compare-Results.ps1 (optional for first iteration).

.PARAMETER CounterMetrics
    PSCustomObject with .NET counter metrics (optional).

.PARAMETER Iteration
    Current iteration number.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER PreviousRcaExplanation
    Explanation from the previous iteration's RCA (optional).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$BaselineMetrics,

    [PSCustomObject]$ComparisonResult,

    [PSCustomObject]$CounterMetrics,

    [int]$Iteration = 0,

    [string]$ConfigPath,

    [string]$PreviousRcaExplanation
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message 'Preparing analysis agent prompt' `
    -Iteration $Iteration

# ── Read source code context ────────────────────────────────────────────────
$apiProjectPath = Join-Path $repoRoot $config.Api.ProjectPath
$sourceGlob = if ($config.Api.SourceFileGlob) { $config.Api.SourceFileGlob } else { '*.*' }
$sourcePaths = if ($config.Api.SourceCodePaths) { $config.Api.SourceCodePaths } else { @('.') }

$sourceContext = foreach ($subPath in $sourcePaths) {
    $searchDir = Join-Path $apiProjectPath $subPath
    if (Test-Path $searchDir) {
        Get-ChildItem -Path $searchDir -Filter $sourceGlob -Recurse | ForEach-Object {
            "// === $($_.Name) ===`n$(Get-Content $_.FullName -Raw)"
        }
    }
}

# ── Build counter metrics context ───────────────────────────────────────────
$counterContext = ''
if ($CounterMetrics) {
    $cpuAvg = if ($CounterMetrics.Runtime.CpuUsage) { "$($CounterMetrics.Runtime.CpuUsage.Avg)%" } else { 'N/A' }
    $heapMax = if ($CounterMetrics.Runtime.GcHeapSizeMB) { "$($CounterMetrics.Runtime.GcHeapSizeMB.Max)MB" } else { 'N/A' }
    $gen2 = if ($CounterMetrics.Runtime.Gen2Collections) { $CounterMetrics.Runtime.Gen2Collections.Last } else { 'N/A' }
    $threads = if ($CounterMetrics.Runtime.ThreadPoolThreads) { $CounterMetrics.Runtime.ThreadPoolThreads.Max } else { 'N/A' }
    $counterContext = @"

## Runtime Counters
- CPU avg: $cpuAvg
- GC heap max: $heapMax
- Gen2 collections: $gen2
- Thread pool max threads: $threads
"@
}

# ── Build optimization history context ───────────────────────────────────────
$historyContext = ''
$metadataDir = Join-Path $repoRoot $config.Api.MetadataPath
$logPath   = Join-Path $metadataDir 'optimization-log.md'
$queuePath = Join-Path $metadataDir 'optimization-queue.md'

if (Test-Path $logPath) {
    $logContent = Get-Content $logPath -Raw
    $historyContext += "`n## Previously Tried Optimizations`n$logContent`n"
}
if (Test-Path $queuePath) {
    $queueContent = Get-Content $queuePath -Raw
    $historyContext += "`n## Known Optimization Queue`n$queueContent`n"
}
if ($PreviousRcaExplanation) {
    $historyContext += "`n## Last Iteration's Fix`n$PreviousRcaExplanation`n"
}

# ── Build the prompt ────────────────────────────────────────────────────────
$improvementPct = if ($ComparisonResult -and $ComparisonResult.ImprovementPct) { $ComparisonResult.ImprovementPct } else { '0' }

$prompt = @"
Analyze this Web API's performance and identify the single highest-impact optimization.

## Current Performance (Iteration $Iteration)
- p95 Latency: $($CurrentMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($CurrentMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2))%
- Improvement vs baseline: ${improvementPct}%

## Baseline Performance
- p95 Latency: $($BaselineMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($BaselineMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($BaselineMetrics.HttpReqFailed.Rate * 100, 2))%
$counterContext
$historyContext

## Source Code
$($sourceContext -join "`n`n")

Respond with JSON only. No markdown, no code blocks around the JSON.
"@

# ── Save the prompt for audit ───────────────────────────────────────────────
$iterDir = Join-Path $repoRoot $config.Api.ResultsPath "iteration-$Iteration"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}
$promptPath = Join-Path $iterDir 'analysis-prompt.md'
$prompt | Out-File -FilePath $promptPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Calling hone-analyst agent (prompt saved to $promptPath)" `
    -Iteration $Iteration

# ── Call the hone-analyst agent ─────────────────────────────────────────────
try {
    $copilotModel = if ($config.Copilot -and $config.Copilot.AnalysisModel) {
        $config.Copilot.AnalysisModel
    } elseif ($config.Copilot -and $config.Copilot.Model) {
        $config.Copilot.Model
    } else {
        'claude-opus-4.6'
    }

    $copilotOutput = copilot --agent hone-analyst --model $copilotModel -p $prompt -s `
        --no-auto-update --no-ask-user 2>&1
    $copilotExitCode = $LASTEXITCODE

    $responseText = ($copilotOutput | Out-String).Trim()

    # Save the response
    $responsePath = Join-Path $iterDir 'analysis-response.json'
    $responseText | Out-File -FilePath $responsePath -Encoding utf8

    # Parse JSON — strip any wrapping markdown code fences if present
    $jsonText = $responseText -replace '(?s)^```(?:json)?\s*', '' -replace '(?s)\s*```$', ''
    $parsed = $jsonText | ConvertFrom-Json

    $result = [ordered]@{
        Success                 = ($copilotExitCode -eq 0 -and $null -ne $parsed.filePath)
        ExitCode                = $copilotExitCode
        FilePath                = $parsed.filePath
        Explanation             = $parsed.explanation
        AdditionalOpportunities = $parsed.additionalOpportunities
        Prompt                  = $prompt
        Response                = $responseText
        PromptPath              = $promptPath
        ResponsePath            = $responsePath
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'info' -Message "Analysis agent response received: $($parsed.filePath)" `
        -Iteration $Iteration
}
catch {
    $result = [ordered]@{
        Success                 = $false
        ExitCode                = -1
        FilePath                = $null
        Explanation             = $null
        AdditionalOpportunities = @()
        Prompt                  = $prompt
        Response                = "Error: $_"
        PromptPath              = $promptPath
        ResponsePath            = $null
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'warning' -Message "Analysis agent failed: $_" `
        -Iteration $Iteration
}

return [PSCustomObject]$result
