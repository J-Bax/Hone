<#
.SYNOPSIS
    Generates a root cause analysis document for a harness experiment.

.DESCRIPTION
    Takes structured analysis data (from the analysis and classification agents)
    and current performance metrics to produce a concise root-cause markdown file
    stored in the experiment subfolder.

.PARAMETER FilePath
    Target file identified by the analysis agent (relative to sample-api/).

.PARAMETER Explanation
    Description of the proposed optimization.

.PARAMETER ChangeScope
    Scope classification: 'narrow' or 'architecture'.

.PARAMETER ScopeReasoning
    Reasoning behind the scope classification.

.PARAMETER CodeBlock
    The optimized file content (from the fix agent). Optional — may be empty
    if the fix agent hasn't run yet (e.g., architecture changes).

.PARAMETER CurrentMetrics
    PSCustomObject with current experiment metrics (p95, RPS, error rate).

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics.

.PARAMETER ComparisonResult
    PSCustomObject from Compare-Results.ps1.

.PARAMETER Experiment
    Current experiment number.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [string]$FilePath,

    [string]$Explanation,

    [string]$ChangeScope = 'architecture',

    [string]$ScopeReasoning,

    [string]$CodeBlock,

    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$BaselineMetrics,

    [PSCustomObject]$ComparisonResult,

    [PSCustomObject]$ImpactEstimate,

    [PSCustomObject]$CounterMetrics,

    [PSCustomObject]$BaselineCounterMetrics,

    [int]$Experiment = 0,

    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Generating root cause analysis for experiment $Experiment" `
    -Experiment $Experiment

# ── Use structured data directly (no parsing needed) ─────────────────────────
$filePath = if ($FilePath) { $FilePath } else { '' }
$explanation = if ($Explanation) { $Explanation } else { '' }
$codeBlock = if ($CodeBlock) { $CodeBlock } else { '' }
$changeScope = if ($ChangeScope) { $ChangeScope } else { 'architecture' }
$scopeReasoning = if ($ScopeReasoning) { $ScopeReasoning } else { '' }

# ── Build metric context ────────────────────────────────────────────────────
$p95Current  = $CurrentMetrics.HttpReqDuration.P95
$p95Baseline = $BaselineMetrics.HttpReqDuration.P95
$rpsCurrent  = [math]::Round($CurrentMetrics.HttpReqs.Rate, 1)
$rpsBaseline = [math]::Round($BaselineMetrics.HttpReqs.Rate, 1)
$errCurrent  = [math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2)
$errBaseline = [math]::Round($BaselineMetrics.HttpReqFailed.Rate * 100, 2)

# Handle optional comparison result
$p95Delta = if ($ComparisonResult -and $ComparisonResult.Deltas) { "$($ComparisonResult.Deltas.P95Latency.ChangePct)%" } else { 'N/A' }
$rpsDelta = if ($ComparisonResult -and $ComparisonResult.Deltas) { "$($ComparisonResult.Deltas.RPS.ChangePct)%" } else { 'N/A' }
$errDelta = if ($ComparisonResult -and $ComparisonResult.Deltas) { "$($ComparisonResult.Deltas.ErrorRate.ChangePct)%" } else { 'N/A' }
$improvPct = if ($ComparisonResult -and $ComparisonResult.ImprovementPct) { $ComparisonResult.ImprovementPct } else { 0 }

# ── Build the RCA markdown ─────────────────────────────────────────────────
$rca = @"
# Root Cause Analysis — Experiment $Experiment

> Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

## Performance Issue

| Metric | Current | Baseline | Delta |
|--------|---------|----------|-------|
| p95 Latency | ${p95Current}ms | ${p95Baseline}ms | $p95Delta |
| Requests/sec | $rpsCurrent | $rpsBaseline | $rpsDelta |
| Error Rate | ${errCurrent}% | ${errBaseline}% | $errDelta |

Overall improvement vs baseline: **${improvPct}%** (p95 latency).

$(if ($ImpactEstimate) {
    $ie = $ImpactEstimate
    @"
## Impact Estimate

| Metric | Estimate |
|--------|----------|
| Traffic share | $($ie.trafficPct)% |
| Latency reduction | $($ie.latencyReductionMs)ms |
| Overall p95 improvement | $($ie.overallP95ImprovementPct)% |
| Confidence | $($ie.confidence) |

> $($ie.reasoning)
"@
})

$(if ($ComparisonResult -and $ComparisonResult.EfficiencyDeltas) {
    $ed = $ComparisonResult.EfficiencyDeltas
    "**Efficiency:** CPU $($ed.CpuUsage.Current)% ($($ed.CpuUsage.ChangePct)% delta) | Working Set $($ed.WorkingSet.Current)MB ($($ed.WorkingSet.ChangePct)% delta)"
})

$(if ($CounterMetrics) {
    $cpuAvg = if ($CounterMetrics.Runtime.CpuUsage) { "$([math]::Round($CounterMetrics.Runtime.CpuUsage.Avg, 1))%" } else { 'N/A' }
    $cpuMax = if ($CounterMetrics.Runtime.CpuUsage) { "$([math]::Round($CounterMetrics.Runtime.CpuUsage.Max, 1))%" } else { 'N/A' }
    $memAvg = if ($CounterMetrics.Runtime.WorkingSetMB) { "$([math]::Round($CounterMetrics.Runtime.WorkingSetMB.Avg, 1))MB" } else { 'N/A' }
    $memMax = if ($CounterMetrics.Runtime.WorkingSetMB) { "$([math]::Round($CounterMetrics.Runtime.WorkingSetMB.Max, 1))MB" } else { 'N/A' }
    
    $baselineCpuAvg = if ($BaselineCounterMetrics -and $BaselineCounterMetrics.Runtime.CpuUsage) { "$([math]::Round($BaselineCounterMetrics.Runtime.CpuUsage.Avg, 1))%" } else { 'N/A' }
    $baselineMemMax = if ($BaselineCounterMetrics -and $BaselineCounterMetrics.Runtime.WorkingSetMB) { "$([math]::Round($BaselineCounterMetrics.Runtime.WorkingSetMB.Max, 1))MB" } else { 'N/A' }
    
    @"

## Efficiency Metrics

| Metric | Current | Baseline |
|--------|---------|----------|
| CPU avg | $cpuAvg | $baselineCpuAvg |
| CPU peak | $cpuMax | — |
| Memory avg | $memAvg | — |
| Memory peak | $memMax | $baselineMemMax |
"@
})

## Root Cause / Rationale

$(if ($filePath) { "**Target file:** ``$filePath```n" })
**Scope:** ``$changeScope``$(if ($scopeReasoning) { " — $scopeReasoning" })
$explanation

## Proposed Fix

$(if ($codeBlock) {
    "```````n$codeBlock`n```````n"
} else {
    "_Code will be generated by the fix agent after scope classification._"
})
"@

# ── Write the RCA file ──────────────────────────────────────────────────────
$iterDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}

$rcaPath = Join-Path $iterDir 'root-cause.md'
$rca | Out-File -FilePath $rcaPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Root cause analysis saved to $rcaPath" `
    -Experiment $Experiment

# ── Return result ───────────────────────────────────────────────────────────
[PSCustomObject][ordered]@{
    Success     = $true
    Path        = $rcaPath
    FilePath    = $filePath
    Explanation = $explanation
    CodeBlock   = $codeBlock
    ChangeScope = $changeScope
}
