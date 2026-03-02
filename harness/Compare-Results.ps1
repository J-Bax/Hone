<#
.SYNOPSIS
    Compares current performance metrics against baseline and thresholds.

.DESCRIPTION
    Evaluates whether performance targets are met, whether a regression occurred
    compared to the previous iteration, and outputs a structured comparison report.

.PARAMETER CurrentMetrics
    PSCustomObject with current iteration metrics (from Invoke-ScaleTests.ps1).

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics (from Get-PerformanceBaseline.ps1).

.PARAMETER PreviousMetrics
    PSCustomObject with previous iteration metrics. Optional for first iteration.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Iteration
    Current iteration number for logging.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$BaselineMetrics,

    [PSCustomObject]$PreviousMetrics,

    [string]$ConfigPath,

    [int]$Iteration = 0
)

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$thresholds = $config.Thresholds

& (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
    -Phase 'compare' -Level 'info' -Message 'Comparing performance metrics' `
    -Iteration $Iteration

# ── Check thresholds ────────────────────────────────────────────────────────
$p95Met = $CurrentMetrics.HttpReqDuration.P95 -le $thresholds.P95LatencyMs
$rpsMet = $CurrentMetrics.HttpReqs.Rate -ge $thresholds.MinRequestsPerSec
$errorMet = $CurrentMetrics.HttpReqFailed.Rate -le $thresholds.MaxErrorRate
$allTargetsMet = $p95Met -and $rpsMet -and $errorMet

# ── Check for regression vs previous iteration ─────────────────────────────
$regression = $false
$regressionDetail = ''

if ($PreviousMetrics) {
    $previousP95 = $PreviousMetrics.HttpReqDuration.P95
    $currentP95 = $CurrentMetrics.HttpReqDuration.P95

    if ($previousP95 -gt 0) {
        $pctChange = ($currentP95 - $previousP95) / $previousP95
        if ($pctChange -gt $thresholds.MaxRegressionPct) {
            $regression = $true
            $regressionDetail = "p95 regressed by $([math]::Round($pctChange * 100, 1))% " +
                "(was $($previousP95)ms, now $($currentP95)ms)"
        }
    }
}

# ── Calculate improvement vs baseline ───────────────────────────────────────
$baselineP95 = $BaselineMetrics.HttpReqDuration.P95
$currentP95 = $CurrentMetrics.HttpReqDuration.P95
$improvementPct = if ($baselineP95 -gt 0) {
    [math]::Round((($baselineP95 - $currentP95) / $baselineP95) * 100, 1)
} else { 0 }

$result = [ordered]@{
    AllTargetsMet    = $allTargetsMet
    Regression       = $regression
    RegressionDetail = $regressionDetail
    ImprovementPct   = $improvementPct
    Checks           = [ordered]@{
        P95Latency = [ordered]@{
            Met      = $p95Met
            Current  = $CurrentMetrics.HttpReqDuration.P95
            Target   = $thresholds.P95LatencyMs
        }
        RPS        = [ordered]@{
            Met      = $rpsMet
            Current  = $CurrentMetrics.HttpReqs.Rate
            Target   = $thresholds.MinRequestsPerSec
        }
        ErrorRate  = [ordered]@{
            Met      = $errorMet
            Current  = $CurrentMetrics.HttpReqFailed.Rate
            Target   = $thresholds.MaxErrorRate
        }
    }
}

# Log the comparison
$logMessage = "Improvement vs baseline: ${improvementPct}% | " +
    "p95: $($CurrentMetrics.HttpReqDuration.P95)ms (target: $($thresholds.P95LatencyMs)ms) | " +
    "RPS: $([math]::Round($CurrentMetrics.HttpReqs.Rate, 1)) (target: $($thresholds.MinRequestsPerSec)) | " +
    "Errors: $([math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2))% (max: $([math]::Round($thresholds.MaxErrorRate * 100, 2))%)"

$level = if ($regression) { 'warning' } elseif ($allTargetsMet) { 'info' } else { 'info' }

& (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
    -Phase 'compare' -Level $level -Message $logMessage `
    -Iteration $Iteration `
    -Data @{ improvement = $improvementPct; targetsMet = $allTargetsMet; regression = $regression }

if ($regression) {
    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
        -Phase 'compare' -Level 'warning' -Message "REGRESSION DETECTED: $regressionDetail" `
        -Iteration $Iteration
}

if ($allTargetsMet) {
    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
        -Phase 'compare' -Level 'info' -Message 'All performance targets met!' `
        -Iteration $Iteration
}

return [PSCustomObject]$result
