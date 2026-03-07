<#
.SYNOPSIS
    Compares current performance metrics against previous iteration using
    relative improvement / regression logic.

.DESCRIPTION
    Evaluates whether at least one metric improved without any metric regressing
    beyond the configured tolerance.  There are no absolute targets — the loop
    keeps running as long as it can make things better.

.PARAMETER CurrentMetrics
    PSCustomObject with current iteration metrics (from Invoke-ScaleTests.ps1).

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics (from Get-PerformanceBaseline.ps1).

.PARAMETER PreviousMetrics
    PSCustomObject with previous iteration metrics. For the first iteration
    the baseline is used as the previous reference.

.PARAMETER CurrentCounterMetrics
    PSCustomObject with .NET counter metrics for this iteration.

.PARAMETER PreviousCounterMetrics
    PSCustomObject with .NET counter metrics from the previous iteration.
    For the first iteration the baseline counter data is not available,
    so the efficiency tiebreaker is skipped.

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

    [PSCustomObject]$CurrentCounterMetrics,

    [PSCustomObject]$PreviousCounterMetrics,

    # Per-run metrics array from Invoke-ScaleTests for variance analysis
    [array]$RunMetrics,

    [string]$ConfigPath,

    [int]$Iteration = 0
)

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$tolerances = $config.Tolerances

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'compare' -Level 'info' -Message 'Comparing performance metrics (relative mode)' `
    -Iteration $Iteration

# Use baseline as reference when there is no previous iteration
$reference = if ($PreviousMetrics) { $PreviousMetrics } else { $BaselineMetrics }

# ── Per-metric helpers ──────────────────────────────────────────────────────

function Get-PctChange([double]$Current, [double]$Previous) {
    if ($Previous -eq 0) { return 0 }
    return ($Current - $Previous) / $Previous
}

# ── Calculate per-metric deltas vs reference ────────────────────────────────

# p95 latency  – lower is better
$p95Current  = $CurrentMetrics.HttpReqDuration.P95
$p95Previous = $reference.HttpReqDuration.P95
$p95Change   = Get-PctChange $p95Current $p95Previous          # negative = improved
$p95Improved = $p95Change -le -$tolerances.MinImprovementPct   # any reduction = improved
# Regression requires BOTH percentage AND absolute delta thresholds.
# This prevents false positives on fast-baseline scenarios where ±2-3ms
# noise appears as a large percentage swing.
$p95AbsoluteDelta = $p95Current - $p95Previous
$minAbsDelta = if ($tolerances.MinAbsoluteP95DeltaMs) { $tolerances.MinAbsoluteP95DeltaMs } else { 0 }
$p95Regressed = ($p95Change -gt $tolerances.MaxRegressionPct) -and
                ($p95AbsoluteDelta -gt $minAbsDelta)

# RPS – higher is better
$rpsCurrent  = $CurrentMetrics.HttpReqs.Rate
$rpsPrevious = $reference.HttpReqs.Rate
$rpsChange   = Get-PctChange $rpsCurrent $rpsPrevious          # positive = improved
$rpsImproved = $rpsChange -ge $tolerances.MinImprovementPct
$rpsRegressed = $rpsChange -lt -$tolerances.MaxRegressionPct

# Error rate – lower is better
$errCurrent  = $CurrentMetrics.HttpReqFailed.Rate
$errPrevious = $reference.HttpReqFailed.Rate
$errChange   = Get-PctChange $errCurrent $errPrevious          # negative = improved
# For error rate, only flag improvement if the absolute change is meaningful
$errImproved  = ($errChange -le -$tolerances.MinImprovementPct) -and ($errPrevious -gt 0)
$errRegressed = ($errChange -gt $tolerances.MaxRegressionPct) -and ($errCurrent -gt 0)

# ── Aggregate performance decisions ─────────────────────────────────────────

$anyImproved  = $p95Improved -or $rpsImproved -or $errImproved
$anyRegressed = $p95Regressed -or $rpsRegressed -or $errRegressed

# ── Efficiency tiebreaker (CPU + Working Set) ───────────────────────────────
# When performance is flat (no improvement, no regression), check whether
# OS-level resource usage decreased.  Only CpuUsage.Avg and WorkingSetMB.Max
# are evaluated — these represent real shared-VM resource contention.

$efficiencyConfig   = $tolerances.Efficiency
$efficiencyEnabled  = $efficiencyConfig -and $efficiencyConfig.Enabled
$cpuImproved        = $false
$workingSetImproved = $false
$efficiencyImproved = $false
$tiebreakerUsed     = $false
$efficiencyDeltas   = $null

$counterReference = if ($PreviousCounterMetrics) { $PreviousCounterMetrics } else { $null }

if ($efficiencyEnabled -and $CurrentCounterMetrics -and $counterReference) {
    # CPU average — lower is better (negative change = improved)
    $cpuCurrent  = $CurrentCounterMetrics.Runtime.CpuUsage.Avg
    $cpuPrevious = $counterReference.Runtime.CpuUsage.Avg
    $cpuChange   = Get-PctChange $cpuCurrent $cpuPrevious
    $cpuImproved = $cpuChange -le -$efficiencyConfig.MinCpuReductionPct

    # Working set peak — lower is better (negative change = improved)
    $wsCurrent   = $CurrentCounterMetrics.Runtime.WorkingSetMB.Max
    $wsPrevious  = $counterReference.Runtime.WorkingSetMB.Max
    $wsChange    = Get-PctChange $wsCurrent $wsPrevious
    $workingSetImproved = $wsChange -le -$efficiencyConfig.MinWorkingSetReductionPct

    $efficiencyImproved = $cpuImproved -or $workingSetImproved

    $efficiencyDeltas = [ordered]@{
        CpuUsage = [ordered]@{
            Current   = [math]::Round($cpuCurrent, 2)
            Previous  = [math]::Round($cpuPrevious, 2)
            ChangePct = [math]::Round($cpuChange * 100, 1)
            Improved  = $cpuImproved
        }
        WorkingSet = [ordered]@{
            Current   = [math]::Round($wsCurrent, 2)
            Previous  = [math]::Round($wsPrevious, 2)
            ChangePct = [math]::Round($wsChange * 100, 1)
            Improved  = $workingSetImproved
        }
    }
}

# Tiebreaker: performance flat + efficiency improved → accept as improved
$tiebreakerUsed = (-not $anyImproved) -and (-not $anyRegressed) -and $efficiencyImproved

$regressionDetails = @()
if ($p95Regressed) {
    $regressionDetails += "p95 regressed by $([math]::Round($p95Change * 100, 1))% " +
        "(was $($p95Previous)ms, now $($p95Current)ms)"
}
if ($rpsRegressed) {
    $regressionDetails += "RPS regressed by $([math]::Round([math]::Abs($rpsChange) * 100, 1))% " +
        "(was $([math]::Round($rpsPrevious, 1)), now $([math]::Round($rpsCurrent, 1)))"
}
if ($errRegressed) {
    $regressionDetails += "Error rate regressed by $([math]::Round($errChange * 100, 1))% " +
        "(was $([math]::Round($errPrevious * 100, 2))%, now $([math]::Round($errCurrent * 100, 2))%)"
}

# ── Improvement vs baseline (informational) ─────────────────────────────────

$baselineP95 = $BaselineMetrics.HttpReqDuration.P95
$improvementPct = if ($baselineP95 -gt 0) {
    [math]::Round((($baselineP95 - $p95Current) / $baselineP95) * 100, 1)
} else { 0 }

# ── Variance analysis (across measured runs within this iteration) ──────────
$varianceInfo = $null
if ($RunMetrics -and $RunMetrics.Count -gt 1) {
    $p95Values = $RunMetrics | ForEach-Object { $_.HttpReqDuration.P95 }
    $mean = ($p95Values | Measure-Object -Average).Average
    $sumSquares = ($p95Values | ForEach-Object { ($_ - $mean) * ($_ - $mean) } | Measure-Object -Sum).Sum
    $stdDev = [math]::Sqrt($sumSquares / $p95Values.Count)
    $cv = if ($mean -gt 0) { $stdDev / $mean } else { 0 }
    $range = ($p95Values | Measure-Object -Maximum -Minimum)

    $varianceInfo = [ordered]@{
        Runs     = $p95Values.Count
        P95Values = $p95Values | ForEach-Object { [math]::Round($_, 2) }
        Mean     = [math]::Round($mean, 2)
        StdDev   = [math]::Round($stdDev, 2)
        CV       = [math]::Round($cv * 100, 1)   # coefficient of variation as percentage
        Min      = [math]::Round($range.Minimum, 2)
        Max      = [math]::Round($range.Maximum, 2)
        Range    = [math]::Round($range.Maximum - $range.Minimum, 2)
    }

    $cvLevel = if ($cv -gt 0.15) { 'error' } elseif ($cv -gt 0.10) { 'warning' } else { 'info' }
    $cvMessage = "Run variance: CV=$([math]::Round($cv * 100, 1))% | " +
        "p95 range: $([math]::Round($range.Minimum, 1))ms—$([math]::Round($range.Maximum, 1))ms | " +
        "stddev: $([math]::Round($stdDev, 1))ms ($($p95Values.Count) runs)"

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'compare' -Level $cvLevel -Message $cvMessage `
        -Iteration $Iteration `
        -Data @{ cv = [math]::Round($cv * 100, 1); stdDev = [math]::Round($stdDev, 2); runs = $p95Values.Count }

    if ($cv -gt 0.10) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'compare' -Level 'warning' `
            -Message "High measurement variance (CV > 10%) — comparison results may be unreliable" `
            -Iteration $Iteration
    }
}

$result = [ordered]@{
    Improved           = $anyImproved -or $tiebreakerUsed
    Regression         = $anyRegressed
    RegressionDetail   = ($regressionDetails -join '; ')
    ImprovementPct     = $improvementPct
    EfficiencyImproved = $efficiencyImproved
    TiebreakerUsed     = $tiebreakerUsed
    EfficiencyDeltas   = $efficiencyDeltas
    Variance           = $varianceInfo
    Deltas             = [ordered]@{
        P95Latency = [ordered]@{
            Current    = $p95Current
            Previous   = $p95Previous
            ChangePct  = [math]::Round($p95Change * 100, 1)
            Improved   = $p95Improved
            Regressed  = $p95Regressed
        }
        RPS = [ordered]@{
            Current    = [math]::Round($rpsCurrent, 1)
            Previous   = [math]::Round($rpsPrevious, 1)
            ChangePct  = [math]::Round($rpsChange * 100, 1)
            Improved   = $rpsImproved
            Regressed  = $rpsRegressed
        }
        ErrorRate = [ordered]@{
            Current    = $errCurrent
            Previous   = $errPrevious
            ChangePct  = [math]::Round($errChange * 100, 1)
            Improved   = $errImproved
            Regressed  = $errRegressed
        }
    }
    DotnetCounters     = $null
}

# ── Include .NET counter highlights in comparison ───────────────────────────
if ($CurrentCounterMetrics) {
    $result.DotnetCounters = [ordered]@{
        CpuUsage = [ordered]@{
            Avg = $CurrentCounterMetrics.Runtime.CpuUsage.Avg
            Max = $CurrentCounterMetrics.Runtime.CpuUsage.Max
        }
        GcHeapSizeMB = [ordered]@{
            Avg = $CurrentCounterMetrics.Runtime.GcHeapSizeMB.Avg
            Max = $CurrentCounterMetrics.Runtime.GcHeapSizeMB.Max
        }
        Gen0Collections = $CurrentCounterMetrics.Runtime.Gen0Collections.Last
        Gen1Collections = $CurrentCounterMetrics.Runtime.Gen1Collections.Last
        Gen2Collections = $CurrentCounterMetrics.Runtime.Gen2Collections.Last
        GcPauseRatio = [ordered]@{
            Avg = $CurrentCounterMetrics.Runtime.GcPauseRatio.Avg
            Max = $CurrentCounterMetrics.Runtime.GcPauseRatio.Max
        }
        ThreadPoolThreads = [ordered]@{
            Avg = $CurrentCounterMetrics.Runtime.ThreadPoolThreads.Avg
            Max = $CurrentCounterMetrics.Runtime.ThreadPoolThreads.Max
        }
        ThreadPoolQueueLength = [ordered]@{
            Avg = $CurrentCounterMetrics.Runtime.ThreadPoolQueue.Avg
            Max = $CurrentCounterMetrics.Runtime.ThreadPoolQueue.Max
        }
        ExceptionCount     = $CurrentCounterMetrics.Runtime.ExceptionCount.Last
        WorkingSetMB       = $CurrentCounterMetrics.Runtime.WorkingSetMB.Max
        AllocRateMB        = $CurrentCounterMetrics.Runtime.AllocRateMB.Avg
    }
}

# Log the comparison
$logMessage = "vs baseline: ${improvementPct}% | " +
    "p95: $($p95Current)ms ($([math]::Round($p95Change * 100, 1))% vs prev) | " +
    "RPS: $([math]::Round($rpsCurrent, 1)) ($([math]::Round($rpsChange * 100, 1))% vs prev) | " +
    "Errors: $([math]::Round($errCurrent * 100, 2))% ($([math]::Round($errChange * 100, 1))% vs prev)"

$level = if ($anyRegressed) { 'warning' } elseif ($anyImproved) { 'info' } else { 'info' }

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'compare' -Level $level -Message $logMessage `
    -Iteration $Iteration `
    -Data @{ improvement = $improvementPct; improved = ($anyImproved -or $tiebreakerUsed); regression = $anyRegressed; efficiencyImproved = $efficiencyImproved; tiebreakerUsed = $tiebreakerUsed }

if ($anyRegressed) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'compare' -Level 'warning' `
        -Message "REGRESSION DETECTED: $($regressionDetails -join '; ')" `
        -Iteration $Iteration
}

if ($anyImproved) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'compare' -Level 'info' -Message 'Improvement detected in at least one metric' `
        -Iteration $Iteration
}
elseif ($tiebreakerUsed) {
    $tbDetails = @()
    if ($cpuImproved) { $tbDetails += "CPU avg reduced by $([math]::Round([math]::Abs($cpuChange) * 100, 1))%" }
    if ($workingSetImproved) { $tbDetails += "Working set reduced by $([math]::Round([math]::Abs($wsChange) * 100, 1))%" }
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'compare' -Level 'info' `
        -Message "Efficiency tiebreaker: $($tbDetails -join '; ')" `
        -Iteration $Iteration
}
elseif (-not $anyRegressed) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'compare' -Level 'info' -Message 'No meaningful change in any metric (stale iteration)' `
        -Iteration $Iteration
}

return [PSCustomObject]$result
