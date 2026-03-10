<#
.SYNOPSIS
    Analyzes GC statistics and allocation data using the hone-memory-profiler agent.

.DESCRIPTION
    Reads GC report data collected by the perfview collector, builds a prompt
    with performance context, and calls the hone-memory-profiler Copilot agent to
    identify memory pressure sources, GC overhead, and allocation hotspots.

.PARAMETER CollectorData
    Hashtable keyed by collector name. Each value has ExportedPaths and Summary.

.PARAMETER CurrentMetrics
    PSCustomObject with current performance metrics (p95, RPS, error rate).

.PARAMETER Experiment
    Current experiment number.

.PARAMETER Settings
    Hashtable of analyzer settings (e.g. Model).

.PARAMETER OutputDir
    Directory for saving prompts and responses.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable]$CollectorData,

    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [int]$Experiment = 0,

    [hashtable]$Settings = @{},

    [Parameter(Mandatory)]
    [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Extract GC report from collector data ───────────────────────────────────
$perfviewData = $CollectorData['perfview-gc']
if (-not $perfviewData) {
    Write-Warning "No perfview-gc collector data available — skipping memory-gc analysis."
    return [PSCustomObject][ordered]@{
        Success      = $false
        Report       = $null
        Summary      = 'No GC collector data available'
        PromptPath   = $null
        ResponsePath = $null
    }
}

$gcReportPath = if ($perfviewData -is [hashtable] -and $perfviewData.ContainsKey('GcReportPath') -and $perfviewData.GcReportPath) {
    $perfviewData.GcReportPath
} else {
    $perfviewData.ExportedPaths[0]
}
Write-Verbose "Reading GC report from: $gcReportPath"

if (-not (Test-Path -LiteralPath $gcReportPath)) {
    Write-Warning "GC report file not found: $gcReportPath"
    return [PSCustomObject][ordered]@{
        Success      = $false
        Report       = $null
        Summary      = "GC report file not found: $gcReportPath"
        PromptPath   = $null
        ResponsePath = $null
    }
}

$gcReportContent = Get-Content -LiteralPath $gcReportPath -Raw -Encoding utf8

# ── Extract allocation type data from perfview-cpu (optional) ───────────────
$allocTypesContent = $null
$cpuData = $CollectorData['perfview-cpu']
if ($cpuData) {
    $allocPath = if ($cpuData -is [hashtable] -and $cpuData.ContainsKey('AllocTypesPath') -and $cpuData.AllocTypesPath) {
                     $cpuData.AllocTypesPath
                 } elseif ($cpuData.ExportedPaths -and $cpuData.ExportedPaths.Count -ge 2) {
                     $cpuData.ExportedPaths[1]
                 } else { $null }
    if ($allocPath -and (Test-Path -LiteralPath $allocPath)) {
        $allocTypesContent = Get-Content -LiteralPath $allocPath -Raw -Encoding utf8
        Write-Verbose "Including allocation type data from: $allocPath"
    }
    else {
        Write-Information "No allocation type data available — GC analysis will proceed without it"
    }
}

# ── Extract current performance metrics ─────────────────────────────────────
$p95Latency = if ($CurrentMetrics.HttpReqDuration -and $CurrentMetrics.HttpReqDuration.P95) {
    $CurrentMetrics.HttpReqDuration.P95
} else { 'N/A' }

$reqsPerSec = if ($CurrentMetrics.HttpReqs -and $CurrentMetrics.HttpReqs.Rate) {
    [math]::Round($CurrentMetrics.HttpReqs.Rate, 1)
} else { 'N/A' }

$errorRate = if ($CurrentMetrics.HttpReqFailed -and $null -ne $CurrentMetrics.HttpReqFailed.Rate) {
    [math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2)
} else { 'N/A' }

$allocSection = if ($allocTypesContent) {
    @"

## Top Allocating Types (from sampled allocation ticks)

${allocTypesContent}
"@
} else { '' }

# ── Build the prompt ────────────────────────────────────────────────────────
$prompt = @"
Analyze the following GC and memory data from PerfView. The data includes GC statistics,
heap behavior, and allocation patterns captured during a load test.

## Current Performance
- p95 Latency: ${p95Latency}ms
- Requests/sec: ${reqsPerSec}
- Error rate: ${errorRate}%

## GC and Memory Report

${gcReportContent}
${allocSection}
Respond with JSON only.
"@

# ── Save prompt for audit ───────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$promptPath = Join-Path $OutputDir 'memory-gc-prompt.md'
$prompt | Out-File -FilePath $promptPath -Encoding utf8
Write-Information "Memory-GC analysis prompt saved to: $promptPath"

# ── Call the hone-memory-profiler agent ─────────────────────────────────────
$model = if ($Settings.Model) { $Settings.Model } else { 'claude-opus-4.6' }
Write-Information "Calling hone-memory-profiler agent (model: $model, experiment: $Experiment)"

try {
    $prevEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

    $copilotOutput = copilot --agent hone-memory-profiler --model $model -p $prompt -s `
        --no-auto-update --no-ask-user 2>&1
    $copilotExitCode = $LASTEXITCODE

    [Console]::OutputEncoding = $prevEncoding

    $responseText = ($copilotOutput | Out-String).Trim()

    # Save the raw response
    $responsePath = Join-Path $OutputDir 'memory-gc-response.json'
    $responseText | Out-File -FilePath $responsePath -Encoding utf8

    # Parse JSON — strip code fences, then extract the first complete JSON object
    $jsonText = $responseText -replace '(?s)^```(?:json)?\s*', '' -replace '(?s)\s*```$', ''
    if ($jsonText -match '(?s)(\{.+\})') {
        $jsonText = $Matches[1]
    }
    $parsed = $jsonText | ConvertFrom-Json

    $summary = if ($parsed.summary) { $parsed.summary } else { 'Analysis complete — no summary provided by agent.' }

    Write-Information "Memory-GC analysis complete: $summary"

    return [PSCustomObject][ordered]@{
        Success      = ($copilotExitCode -eq 0 -and $null -ne $parsed)
        Report       = $parsed
        Summary      = $summary
        PromptPath   = $promptPath
        ResponsePath = $responsePath
    }
}
catch {
    Write-Warning "Memory-GC analysis agent failed: $_"
    return [PSCustomObject][ordered]@{
        Success      = $false
        Report       = $null
        Summary      = "Analysis agent error: $_"
        PromptPath   = $promptPath
        ResponsePath = $null
    }
}
