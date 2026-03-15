<#
.SYNOPSIS
    Compares current performance metrics against previous experiment using
    relative improvement / regression logic.

.DESCRIPTION
    Evaluates whether at least one metric improved without any metric regressing
    beyond the configured tolerance.  There are no absolute targets — the loop
    keeps running as long as it can make things better.

    The core comparison logic is in the Compare-Metrics pure function, which
    can be called independently for testing without any I/O side effects.

.PARAMETER CurrentMetrics
    PSCustomObject with current experiment metrics (from Invoke-ScaleTests.ps1).

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics (from Get-PerformanceBaseline.ps1).

.PARAMETER PreviousMetrics
    PSCustomObject with previous experiment metrics. For the first experiment
    the baseline is used as the previous reference.

.PARAMETER CurrentCounterMetrics
    PSCustomObject with .NET counter metrics for this experiment.

.PARAMETER PreviousCounterMetrics
    PSCustomObject with .NET counter metrics from the previous experiment.
    For the first experiment the baseline counter data is not available,
    so the efficiency tiebreaker is skipped.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Experiment
    Current experiment number for logging.
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

    [int]$Experiment = 0
)

# ── Pure comparison function (no I/O, no logging) ───────────────────────────

function Compare-Metrics {
    <#
    .SYNOPSIS
        Pure function that compares current vs previous performance metrics.
        Returns a structured comparison result with no logging side effects.

    .PARAMETER Current
        PSCustomObject with current experiment metrics.

    .PARAMETER Previous
        PSCustomObject with the reference metrics to compare against.

    .PARAMETER Tolerances
        Hashtable of tolerance thresholds from config (MinImprovementPct,
        MaxRegressionPct, MinAbsoluteP95DeltaMs, Efficiency, etc.).

    .PARAMETER Baseline
        PSCustomObject with baseline metrics (for improvement-vs-baseline calculation).
        If not provided, Previous is used as the baseline.

    .PARAMETER CurrentCounters
        PSCustomObject with .NET counter metrics for this experiment (optional).

    .PARAMETER PreviousCounters
        PSCustomObject with .NET counter metrics for the reference experiment (optional).

    .PARAMETER PerRunMetrics
        Array of per-run metric objects for variance analysis (optional).
    #>
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Current,

        [Parameter(Mandatory)]
        [PSCustomObject]$Previous,

        [Parameter(Mandatory)]
        [hashtable]$Tolerances,

        [PSCustomObject]$Baseline,

        [PSCustomObject]$CurrentCounters,

        [PSCustomObject]$PreviousCounters,

        [array]$PerRunMetrics
    )

    if (-not $Baseline) { $Baseline = $Previous }

    # ── Per-metric helper ───────────────────────────────────────────────────
    function Get-PctChange([double]$Cur, [double]$Prev) {
        if ($Prev -eq 0) { return 0 }
        $r = ($Cur - $Prev) / $Prev
        return [Math]::Max(-10.0, [Math]::Min(10.0, $r))
    }

    # ── Calculate per-metric deltas vs reference ────────────────────────────

    # p95 latency — lower is better
    $p95Current  = $Current.HttpReqDuration.P95
    $p95Previous = $Previous.HttpReqDuration.P95
    $p95Change   = Get-PctChange $p95Current $p95Previous
    $p95Improved = $p95Change -le -$Tolerances.MinImprovementPct
    $p95AbsoluteDelta = $p95Current - $p95Previous
    $minAbsDelta = if ($Tolerances.MinAbsoluteP95DeltaMs) { $Tolerances.MinAbsoluteP95DeltaMs } else { 0 }
    $p95Regressed = ($p95Change -gt $Tolerances.MaxRegressionPct) -and
                    ($p95AbsoluteDelta -gt $minAbsDelta)

    # RPS — higher is better
    $rpsCurrent  = $Current.HttpReqs.Rate
    $rpsPrevious = $Previous.HttpReqs.Rate
    $rpsChange   = Get-PctChange $rpsCurrent $rpsPrevious
    $rpsImproved = $rpsChange -ge $Tolerances.MinImprovementPct
    $rpsAbsoluteDelta = $rpsPrevious - $rpsCurrent
    $minAbsRpsDelta = if ($Tolerances.MinAbsoluteRPSDelta) { $Tolerances.MinAbsoluteRPSDelta } else { 0 }
    $rpsRegressed = ($rpsChange -lt -$Tolerances.MaxRegressionPct) -and
                    ($rpsAbsoluteDelta -gt $minAbsRpsDelta)

    # Error rate — lower is better
    $errCurrent  = $Current.HttpReqFailed.Rate
    $errPrevious = $Previous.HttpReqFailed.Rate
    $errChange   = Get-PctChange $errCurrent $errPrevious
    $errImproved  = ($errChange -le -$Tolerances.MinImprovementPct) -and ($errPrevious -gt 0)
    $errAbsoluteDelta = $errCurrent - $errPrevious
    $minAbsErrDelta = if ($Tolerances.MinAbsoluteErrorRateDelta) { $Tolerances.MinAbsoluteErrorRateDelta } else { 0 }
    $errRegressed = ($errChange -gt $Tolerances.MaxRegressionPct) -and ($errCurrent -gt 0) -and
                    ($errAbsoluteDelta -gt $minAbsErrDelta)

    # ── Aggregate performance decisions ─────────────────────────────────────
    $anyImproved  = $p95Improved -or $rpsImproved -or $errImproved
    $anyRegressed = $p95Regressed -or $rpsRegressed -or $errRegressed

    # ── Efficiency tiebreaker (CPU + Working Set) ───────────────────────────
    $efficiencyConfig   = $Tolerances.Efficiency
    $efficiencyEnabled  = $efficiencyConfig -and $efficiencyConfig.Enabled
    $cpuImproved        = $false
    $workingSetImproved = $false
    $efficiencyImproved = $false
    $tiebreakerUsed     = $false
    $efficiencyDeltas   = $null

    if ($efficiencyEnabled -and $CurrentCounters -and $PreviousCounters) {
        $cpuCurrent  = $CurrentCounters.Runtime.CpuUsage.Avg
        $cpuPrevious = $PreviousCounters.Runtime.CpuUsage.Avg
        $cpuChange   = Get-PctChange $cpuCurrent $cpuPrevious
        $cpuImproved = $cpuChange -le -$efficiencyConfig.MinCpuReductionPct

        $wsCurrent   = $CurrentCounters.Runtime.WorkingSetMB.Max
        $wsPrevious  = $PreviousCounters.Runtime.WorkingSetMB.Max
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

    $tiebreakerUsed = (-not $anyImproved) -and (-not $anyRegressed) -and $efficiencyImproved

    # ── Regression detail strings ───────────────────────────────────────────
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

    # ── Improvement vs baseline (informational) ─────────────────────────────
    $baselineP95 = $Baseline.HttpReqDuration.P95
    $improvementPct = if ($baselineP95 -gt 0) {
        [math]::Round((($baselineP95 - $p95Current) / $baselineP95) * 100, 1)
    } else { 0 }

    # ── Variance analysis ───────────────────────────────────────────────────
    $varianceInfo = $null
    if ($PerRunMetrics -and $PerRunMetrics.Count -gt 1) {
        $p95Values = $PerRunMetrics | ForEach-Object { $_.HttpReqDuration.P95 }
        $mean = ($p95Values | Measure-Object -Average).Average
        $sumSquares = ($p95Values | ForEach-Object { ($_ - $mean) * ($_ - $mean) } | Measure-Object -Sum).Sum
        $stdDev = [math]::Sqrt($sumSquares / $p95Values.Count)
        $cv = if ($mean -gt 0) { $stdDev / $mean } else { 0 }
        $range = ($p95Values | Measure-Object -Maximum -Minimum)

        $varianceInfo = [ordered]@{
            Runs      = $p95Values.Count
            P95Values = $p95Values | ForEach-Object { [math]::Round($_, 2) }
            Mean      = [math]::Round($mean, 2)
            StdDev    = [math]::Round($stdDev, 2)
            CV        = [math]::Round($cv * 100, 1)
            Min       = [math]::Round($range.Minimum, 2)
            Max       = [math]::Round($range.Maximum, 2)
            Range     = [math]::Round($range.Maximum - $range.Minimum, 2)
        }
    }

    # ── Build result object ─────────────────────────────────────────────────
    $result = [ordered]@{
        Improved            = $anyImproved -or $tiebreakerUsed
        Regression          = $anyRegressed
        RegressionDetail    = ($regressionDetails -join '; ')
        ImprovementPct      = $improvementPct
        ReferenceIsBaseline = ($Previous -eq $Baseline)
        EfficiencyImproved  = $efficiencyImproved
        TiebreakerUsed      = $tiebreakerUsed
        EfficiencyDeltas    = $efficiencyDeltas
        Variance            = $varianceInfo
        Deltas              = [ordered]@{
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
        DotnetCounters      = $null
    }

    # Include .NET counter highlights in comparison
    if ($CurrentCounters) {
        $result.DotnetCounters = [ordered]@{
            CpuUsage = [ordered]@{
                Avg = $CurrentCounters.Runtime.CpuUsage.Avg
                Max = $CurrentCounters.Runtime.CpuUsage.Max
            }
            GcHeapSizeMB = [ordered]@{
                Avg = $CurrentCounters.Runtime.GcHeapSizeMB.Avg
                Max = $CurrentCounters.Runtime.GcHeapSizeMB.Max
            }
            Gen0Collections = $CurrentCounters.Runtime.Gen0Collections.Last
            Gen1Collections = $CurrentCounters.Runtime.Gen1Collections.Last
            Gen2Collections = $CurrentCounters.Runtime.Gen2Collections.Last
            GcPauseRatio = [ordered]@{
                Avg = $CurrentCounters.Runtime.GcPauseRatio.Avg
                Max = $CurrentCounters.Runtime.GcPauseRatio.Max
            }
            ThreadPoolThreads = [ordered]@{
                Avg = $CurrentCounters.Runtime.ThreadPoolThreads.Avg
                Max = $CurrentCounters.Runtime.ThreadPoolThreads.Max
            }
            ThreadPoolQueueLength = [ordered]@{
                Avg = $CurrentCounters.Runtime.ThreadPoolQueue.Avg
                Max = $CurrentCounters.Runtime.ThreadPoolQueue.Max
            }
            ExceptionCount     = $CurrentCounters.Runtime.ExceptionCount.Last
            WorkingSetMB = [ordered]@{
                Avg = $CurrentCounters.Runtime.WorkingSetMB.Avg
                Max = $CurrentCounters.Runtime.WorkingSetMB.Max
            }
            AllocRateMB = [ordered]@{
                Avg = $CurrentCounters.Runtime.AllocRateMB.Avg
                Max = $CurrentCounters.Runtime.AllocRateMB.Max
            }
        }
    }

    return [PSCustomObject]$result
}

# ── Script wrapper: load config, call Compare-Metrics, log results ──────────

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath
$tolerances = $config.Tolerances

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'verify' -Level 'info' -Message 'Comparing performance metrics (relative mode)' `
    -Experiment $Experiment

$reference = if ($PreviousMetrics) { $PreviousMetrics } else { $BaselineMetrics }
$counterReference = if ($PreviousCounterMetrics) { $PreviousCounterMetrics } else { $null }

$result = Compare-Metrics `
    -Current $CurrentMetrics `
    -Previous $reference `
    -Tolerances $tolerances `
    -Baseline $BaselineMetrics `
    -CurrentCounters $CurrentCounterMetrics `
    -PreviousCounters $counterReference `
    -PerRunMetrics $RunMetrics

# ── Log variance ────────────────────────────────────────────────────────────
if ($result.Variance) {
    $v = $result.Variance
    $cv = $v.CV / 100.0
    $cvLevel = if ($cv -gt 0.15) { 'error' } elseif ($cv -gt 0.10) { 'warning' } else { 'info' }
    $cvMessage = "Run variance: CV=$($v.CV)% | " +
        "p95 range: $($v.Min)ms—$($v.Max)ms | " +
        "stddev: $($v.StdDev)ms ($($v.Runs) runs)"

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level $cvLevel -Message $cvMessage `
        -Experiment $Experiment `
        -Data @{ cv = $v.CV; stdDev = $v.StdDev; runs = $v.Runs }

    if ($cv -gt 0.10) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'verify' -Level 'warning' `
            -Message "High measurement variance (CV > 10%) — comparison results may be unreliable" `
            -Experiment $Experiment
    }
}

# ── Log the comparison ──────────────────────────────────────────────────────
$d = $result.Deltas
$logMessage = "vs baseline: $($result.ImprovementPct)% | " +
    "p95: $($d.P95Latency.Current)ms ($($d.P95Latency.ChangePct)% vs prev) | " +
    "RPS: $($d.RPS.Current) ($($d.RPS.ChangePct)% vs prev) | " +
    "Errors: $([math]::Round($d.ErrorRate.Current * 100, 2))% ($($d.ErrorRate.ChangePct)% vs prev)"

if ($result.EfficiencyDeltas) {
    $ed = $result.EfficiencyDeltas
    $logMessage += " | CPU: $($ed.CpuUsage.Current)% ($($ed.CpuUsage.ChangePct)% delta)" +
        " | WorkingSet: $($ed.WorkingSet.Current)MB ($($ed.WorkingSet.ChangePct)% delta)"
}

$level = if ($result.Regression) { 'warning' } else { 'info' }

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'verify' -Level $level -Message $logMessage `
    -Experiment $Experiment `
    -Data @{ improvement = $result.ImprovementPct; improved = $result.Improved; regression = $result.Regression; efficiencyImproved = $result.EfficiencyImproved; tiebreakerUsed = $result.TiebreakerUsed }

if ($result.Regression) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'warning' `
        -Message "REGRESSION DETECTED: $($result.RegressionDetail)" `
        -Experiment $Experiment
}

if ($result.Improved -and -not $result.TiebreakerUsed) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'info' -Message 'Improvement detected in at least one metric' `
        -Experiment $Experiment
}
elseif ($result.TiebreakerUsed) {
    $tbDetails = @()
    if ($result.EfficiencyDeltas.CpuUsage.Improved) {
        $tbDetails += "CPU avg reduced by $([math]::Round([math]::Abs($result.EfficiencyDeltas.CpuUsage.ChangePct), 1))%"
    }
    if ($result.EfficiencyDeltas.WorkingSet.Improved) {
        $tbDetails += "Working set reduced by $([math]::Round([math]::Abs($result.EfficiencyDeltas.WorkingSet.ChangePct), 1))%"
    }
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'info' `
        -Message "Efficiency tiebreaker: $($tbDetails -join '; ')" `
        -Experiment $Experiment
}
elseif (-not $result.Regression) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'info' -Message 'No meaningful change in any metric (stale experiment)' `
        -Experiment $Experiment
}

return $result
