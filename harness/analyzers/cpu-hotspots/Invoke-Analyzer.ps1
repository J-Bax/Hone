<#
.SYNOPSIS
    Analyzes CPU sampling stacks using the hone-cpu-profiler agent.

.DESCRIPTION
    Reads folded CPU stacks collected by the perfview collector, truncates to
    the top N stacks by sample count, builds a prompt with performance context,
    and calls the hone-cpu-profiler Copilot agent to identify CPU hotspots and
    performance-critical call paths.

.PARAMETER CollectorData
    Hashtable keyed by collector name. Each value has ExportedPaths and Summary.

.PARAMETER CurrentMetrics
    PSCustomObject with current performance metrics (p95, RPS, error rate).

.PARAMETER Experiment
    Current experiment number.

.PARAMETER Settings
    Hashtable of analyzer settings (e.g. Model, MaxStacks).

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

# ── Extract folded stacks from collector data ───────────────────────────────
$perfviewData = $CollectorData['perfview-cpu']
if (-not $perfviewData) {
    Write-Warning "No perfview-cpu collector data available — skipping cpu-hotspots analysis."
    return [PSCustomObject][ordered]@{
        Success      = $false
        Report       = $null
        Summary      = 'No CPU collector data available'
        PromptPath   = $null
        ResponsePath = $null
    }
}

$stacksPath = if ($perfviewData -is [hashtable] -and $perfviewData.ContainsKey('CpuStacksPath') -and $perfviewData.CpuStacksPath) {
    $perfviewData.CpuStacksPath
} else {
    $perfviewData.ExportedPaths[0]
}
Write-Verbose "Reading folded stacks from: $stacksPath"

if (-not (Test-Path -LiteralPath $stacksPath)) {
    Write-Warning "Folded stacks file not found: $stacksPath"
    return [PSCustomObject][ordered]@{
        Success      = $false
        Report       = $null
        Summary      = "Folded stacks file not found: $stacksPath"
        PromptPath   = $null
        ResponsePath = $null
    }
}

$stacksContent = Get-Content -LiteralPath $stacksPath -Encoding utf8

# ── Truncate to top N stacks by sample count ────────────────────────────────
$maxStacks = if ($Settings.MaxStacks) { $Settings.MaxStacks } else { 100 }

# Folded stack format: "frame1;frame2;method count" — sort by trailing count descending
$topStacks = $stacksContent |
    Where-Object { $_ -match '\s+(\d+)\s*$' } |
    Sort-Object { if ($_ -match '\s+(\d+)\s*$') { [long]$Matches[1] } else { 0 } } -Descending |
    Select-Object -First $maxStacks

$stackCount = @($topStacks).Count
$truncatedContent = ($topStacks -join "`n")

Write-Verbose "Selected top $stackCount stacks (MaxStacks=$maxStacks) for analysis"

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

# ── Build the prompt ────────────────────────────────────────────────────────
$prompt = @"
Analyze the following CPU sampling data from PerfView. The data is in folded stack format
(call chain separated by semicolons, followed by sample count).

## Current Performance
- p95 Latency: ${p95Latency}ms
- Requests/sec: ${reqsPerSec}
- Error rate: ${errorRate}%

## CPU Sampling Data (top $stackCount stacks by sample count)

${truncatedContent}

Respond with JSON only.
"@

# ── Save prompt for audit ───────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$promptPath = Join-Path $OutputDir 'cpu-hotspots-prompt.md'
$prompt | Out-File -FilePath $promptPath -Encoding utf8
Write-Information "CPU hotspots analysis prompt saved to: $promptPath"

# ── Call the hone-cpu-profiler agent ────────────────────────────────────────
$model = if ($Settings.Model) { $Settings.Model } else { 'claude-opus-4.6' }
Write-Information "Calling hone-cpu-profiler agent (model: $model, experiment: $Experiment)"

try {
    $prevEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

    $copilotOutput = copilot --agent hone-cpu-profiler --model $model -p $prompt -s `
        --no-auto-update --no-ask-user 2>&1
    $copilotExitCode = $LASTEXITCODE

    [Console]::OutputEncoding = $prevEncoding

    $responseText = ($copilotOutput | Out-String).Trim()

    # Save the raw response
    $responsePath = Join-Path $OutputDir 'cpu-hotspots-response.json'
    $responseText | Out-File -FilePath $responsePath -Encoding utf8

    # Parse JSON — strip code fences, then extract the first complete JSON object
    $jsonText = $responseText -replace '(?s)^```(?:json)?\s*', '' -replace '(?s)\s*```$', ''
    if ($jsonText -match '(?s)(\{.+\})') {
        $jsonText = $Matches[1]
    }
    $parsed = $jsonText | ConvertFrom-Json

    $summary = if ($parsed.summary) { $parsed.summary } else { 'Analysis complete — no summary provided by agent.' }

    Write-Information "CPU hotspots analysis complete: $summary"

    return [PSCustomObject][ordered]@{
        Success      = ($copilotExitCode -eq 0 -and $null -ne $parsed)
        Report       = $parsed
        Summary      = $summary
        PromptPath   = $promptPath
        ResponsePath = $responsePath
    }
}
catch {
    Write-Warning "CPU hotspots analysis agent failed: $_"
    return [PSCustomObject][ordered]@{
        Success      = $false
        Report       = $null
        Summary      = "Analysis agent error: $_"
        PromptPath   = $promptPath
        ResponsePath = $null
    }
}
