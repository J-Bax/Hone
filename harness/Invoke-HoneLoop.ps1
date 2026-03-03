<#
.SYNOPSIS
    Main entry point for the Hone agentic optimization loop.

.DESCRIPTION
    Orchestrates the full iterative optimization cycle:
    1. Build the target API
    2. Verify correctness with E2E tests
    3. Start the API and measure performance with k6
    4. Compare results against baseline and thresholds
    5. If targets not met: analyze with Copilot, apply fix, repeat
    6. Exit when targets met, regression detected, or max iterations reached

.PARAMETER MaxIterations
    Override max iterations from config.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.EXAMPLE
    .\Invoke-HoneLoop.ps1

.EXAMPLE
    .\Invoke-HoneLoop.ps1 -MaxIterations 10
#>
[CmdletBinding()]
param(
    [int]$MaxIterations,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

# Apply parameter overrides
if ($MaxIterations -gt 0) { $config.Loop.MaxIterations = $MaxIterations }

$maxIter = $config.Loop.MaxIterations
$tolerances = $config.Tolerances

# ── Banner ──────────────────────────────────────────────────────────────────
Write-Information '' -InformationAction Continue
Write-Information '╔══════════════════════════════════════════════════════════╗' -InformationAction Continue
Write-Information '║               HONE — Agentic Optimizer              ║' -InformationAction Continue
Write-Information '╚══════════════════════════════════════════════════════════╝' -InformationAction Continue
Write-Information '' -InformationAction Continue
Write-Information "  Max iterations:       $maxIter" -InformationAction Continue
Write-Information "  Min improvement:      $([math]::Round($tolerances.MinImprovementPct * 100, 1))% (any metric)" -InformationAction Continue
Write-Information "  Max regression:       $([math]::Round($tolerances.MaxRegressionPct * 100, 1))% (per metric)" -InformationAction Continue
Write-Information "  Stale iter stop:      $($tolerances.StaleIterationsBeforeStop) consecutive" -InformationAction Continue
$effCfg = $tolerances.Efficiency
if ($effCfg -and $effCfg.Enabled) {
    Write-Information "  Efficiency tiebreak:  CPU $([math]::Round($effCfg.MinCpuReductionPct * 100))% / WorkingSet $([math]::Round($effCfg.MinWorkingSetReductionPct * 100))% min reduction" -InformationAction Continue
}
Write-Information '' -InformationAction Continue

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message "Hone loop starting (max $maxIter iterations)"

# ── Collect machine info ────────────────────────────────────────────────────
$machineInfo = & (Join-Path $PSScriptRoot 'Get-MachineInfo.ps1')
Write-Information "  Machine: $($machineInfo.MachineName)" -InformationAction Continue
Write-Information "  CPU:     $($machineInfo.Cpu.Name) ($($machineInfo.Cpu.LogicalProcessors) logical cores)" -InformationAction Continue
Write-Information "  RAM:     $($machineInfo.Memory.TotalGB)GB" -InformationAction Continue
Write-Information "  OS:      $($machineInfo.OS.Description)" -InformationAction Continue
Write-Information '' -InformationAction Continue

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message 'Machine info collected' `
    -Data @{
        machineName      = $machineInfo.MachineName
        cpuName          = $machineInfo.Cpu.Name
        logicalCores     = $machineInfo.Cpu.LogicalProcessors
        physicalCores    = $machineInfo.Cpu.PhysicalCores
        totalMemoryGB    = $machineInfo.Memory.TotalGB
        os               = $machineInfo.OS.Description
    }

# ── Run metadata tracking ───────────────────────────────────────────────────
$runMetadataPath = Join-Path $repoRoot $config.ScaleTest.OutputPath 'run-metadata.json'
$loopStartedAt = Get-Date -Format 'o'

if (Test-Path $runMetadataPath) {
    $runMetadata = Get-Content $runMetadataPath -Raw | ConvertFrom-Json
    # Overwrite machine info with current (machine may differ between baseline & loop)
    $runMetadata | Add-Member -NotePropertyName Machine -NotePropertyValue $machineInfo -Force
    $runMetadata | Add-Member -NotePropertyName LoopStartedAt -NotePropertyValue $loopStartedAt -Force
}
else {
    $runMetadata = [PSCustomObject][ordered]@{
        Machine        = $machineInfo
        BaselineRun    = $null
        LoopStartedAt  = $loopStartedAt
        LoopCompletedAt = $null
        Iterations     = @()
    }
}

# ── Load baseline ───────────────────────────────────────────────────────────
$baselinePath = Join-Path $repoRoot $config.ScaleTest.OutputPath 'baseline.json'

if (-not (Test-Path $baselinePath)) {
    Write-Information 'No baseline found. Running Get-PerformanceBaseline.ps1 first...' -InformationAction Continue
    & (Join-Path $PSScriptRoot 'Get-PerformanceBaseline.ps1') -ConfigPath $ConfigPath

    if (-not (Test-Path $baselinePath)) {
        Write-Error 'Failed to establish baseline. Aborting.'
        return
    }
}

$baselineMetrics = Get-Content $baselinePath -Raw | ConvertFrom-Json
Write-Information "Baseline loaded: p95=$($baselineMetrics.HttpReqDuration.P95)ms" -InformationAction Continue

# ── Iteration Loop ──────────────────────────────────────────────────────────
$previousMetrics = $null
$previousCounterMetrics = $null
$exitReason = 'max_iterations'
$bestIteration = 0
$bestP95 = $baselineMetrics.HttpReqDuration.P95
$staleCount = 0

for ($iteration = 1; $iteration -le $maxIter; $iteration++) {

    $iterationStartedAt = Get-Date -Format 'o'

    Write-Information '' -InformationAction Continue
    Write-Information "━━━ Iteration $iteration / $maxIter ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -InformationAction Continue

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'loop' -Level 'info' -Message "Starting iteration $iteration" -Iteration $iteration

    # ── Phase 1: Build ──────────────────────────────────────────────────────
    Write-Information '[1/5] Building...' -InformationAction Continue
    $buildResult = & (Join-Path $PSScriptRoot 'Build-SampleApi.ps1') -ConfigPath $ConfigPath

    if (-not $buildResult.Success) {
        $exitReason = 'build_failure'
        Write-Error "Build failed at iteration $iteration. Aborting."
        break
    }

    # ── Phase 2: Verify (E2E Tests) ────────────────────────────────────────
    Write-Information '[2/5] Verifying (E2E tests)...' -InformationAction Continue
    $testResult = & (Join-Path $PSScriptRoot 'Invoke-E2ETests.ps1') `
        -ConfigPath $ConfigPath -Iteration $iteration

    if (-not $testResult.Success) {
        $exitReason = 'test_failure'
        Write-Warning "E2E tests failed at iteration $iteration — rolling back"

        # Rollback the submodule to main
        Push-Location (Join-Path $repoRoot 'sample-api')
        git checkout main 2>&1 | Out-Null
        Pop-Location

        break
    }

    # ── Phase 3: Measure (Scale Tests) ─────────────────────────────────────
    Write-Information '[3/5] Measuring (k6 scale tests)...' -InformationAction Continue

    # Reset database so every iteration starts with identical seed data
    & (Join-Path $PSScriptRoot 'Reset-Database.ps1') -ConfigPath $ConfigPath -Iteration $iteration

    $apiResult = & (Join-Path $PSScriptRoot 'Start-SampleApi.ps1') -ConfigPath $ConfigPath

    if (-not $apiResult.Success) {
        $exitReason = 'api_start_failure'
        Write-Error "API failed to start at iteration $iteration. Aborting."
        break
    }

    try {
        $scaleResult = & (Join-Path $PSScriptRoot 'Invoke-ScaleTests.ps1') `
            -ConfigPath $ConfigPath -Iteration $iteration

        if (-not $scaleResult.Success) {
            $exitReason = 'scale_test_failure'
            Write-Error "Scale tests failed at iteration $iteration. Aborting."
            break
        }

        # ── Run additional (diagnostic) scenarios ───────────────────────────
        Write-Information '      Running additional scenarios...' -InformationAction Continue
        $scenarioResults = & (Join-Path $PSScriptRoot 'Invoke-AllScaleTests.ps1') `
            -ConfigPath $ConfigPath -Iteration $iteration -SkipPrimary
    }
    finally {
        & (Join-Path $PSScriptRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
    }

    $currentMetrics = $scaleResult.Metrics
    $currentCounterMetrics = $scaleResult.CounterMetrics

    # Display .NET counter highlights if available
    if ($currentCounterMetrics) {
        $cpuInfo = if ($currentCounterMetrics.Runtime.CpuUsage) { "CPU avg: $($currentCounterMetrics.Runtime.CpuUsage.Avg)%" } else { 'CPU: N/A' }
        $heapInfo = if ($currentCounterMetrics.Runtime.GcHeapSizeMB) { "GC heap max: $($currentCounterMetrics.Runtime.GcHeapSizeMB.Max)MB" } else { 'Heap: N/A' }
        $gen2Info = if ($currentCounterMetrics.Runtime.Gen2Collections) { "Gen2: $($currentCounterMetrics.Runtime.Gen2Collections.Last)" } else { 'Gen2: N/A' }
        $threadInfo = if ($currentCounterMetrics.Runtime.ThreadPoolThreads) { "Threads max: $($currentCounterMetrics.Runtime.ThreadPoolThreads.Max)" } else { 'Threads: N/A' }
        Write-Information "  .NET counters: $cpuInfo | $heapInfo | $gen2Info | $threadInfo" -InformationAction Continue
    }

    # Track best iteration
    if ($currentMetrics.HttpReqDuration.P95 -lt $bestP95) {
        $bestP95 = $currentMetrics.HttpReqDuration.P95
        $bestIteration = $iteration
    }

    # ── Phase 4: Compare ───────────────────────────────────────────────────
    Write-Information '[4/5] Comparing results...' -InformationAction Continue

    $comparison = & (Join-Path $PSScriptRoot 'Compare-Results.ps1') `
        -CurrentMetrics $currentMetrics `
        -BaselineMetrics $baselineMetrics `
        -PreviousMetrics $previousMetrics `
        -CurrentCounterMetrics $currentCounterMetrics `
        -PreviousCounterMetrics $previousCounterMetrics `
        -ConfigPath $ConfigPath `
        -Iteration $iteration

    # Display per-metric deltas
    $d = $comparison.Deltas
    Write-Information ("  p95: {0}ms ({1}%) | RPS: {2} ({3}%) | Errors: {4}% ({5}%) | vs baseline: {6}%" -f `
        $d.P95Latency.Current, $d.P95Latency.ChangePct, `
        $d.RPS.Current, $d.RPS.ChangePct, `
        [math]::Round($d.ErrorRate.Current * 100, 2), $d.ErrorRate.ChangePct, `
        $comparison.ImprovementPct) -InformationAction Continue

    if ($comparison.Regression) {
        $exitReason = 'regression'
        Write-Warning "  Regression detected: $($comparison.RegressionDetail)"
        Write-Warning "  Rolling back to previous iteration"

        Push-Location (Join-Path $repoRoot 'sample-api')
        git checkout main 2>&1 | Out-Null
        Pop-Location

        break
    }

    # Display efficiency deltas when counter data is available
    if ($comparison.EfficiencyDeltas) {
        $ed = $comparison.EfficiencyDeltas
        Write-Information ("  CPU: {0}% ({1}%) | WorkingSet: {2}MB ({3}%)" -f `
            $ed.CpuUsage.Current, $ed.CpuUsage.ChangePct, `
            $ed.WorkingSet.Current, $ed.WorkingSet.ChangePct) -InformationAction Continue
    }

    if ($comparison.TiebreakerUsed) {
        $staleCount = 0
        Write-Information '  ↑ Efficiency improvement (tiebreaker) — continuing' -InformationAction Continue
    }
    elseif ($comparison.Improved) {
        $staleCount = 0
        Write-Information '  ↑ Improvement detected — continuing' -InformationAction Continue
    }
    else {
        $staleCount++
        Write-Information "  ─ No improvement (stale $staleCount / $($tolerances.StaleIterationsBeforeStop))" -InformationAction Continue
        if ($staleCount -ge $tolerances.StaleIterationsBeforeStop) {
            $exitReason = 'no_improvement'
            Write-Information '  Stopping: no improvement for consecutive iterations' -InformationAction Continue
            break
        }
    }

    # ── Phase 5: Analyze + Fix ─────────────────────────────────────────────
    if ($iteration -lt $maxIter) {
        Write-Information '[5/5] Analyzing with Copilot and applying fix...' -InformationAction Continue

        $analysisResult = & (Join-Path $PSScriptRoot 'Invoke-CopilotAnalysis.ps1') `
            -CurrentMetrics $currentMetrics `
            -BaselineMetrics $baselineMetrics `
            -ComparisonResult $comparison `
            -CounterMetrics $currentCounterMetrics `
            -Iteration $iteration `
            -ConfigPath $ConfigPath

        if ($analysisResult.Success) {
            Write-Information "  Copilot response saved to: $($analysisResult.ResponsePath)" -InformationAction Continue
            Write-Information '  → Review the suggestion and apply manually, or extend Apply-Suggestion.ps1 for auto-apply' -InformationAction Continue

            # NOTE: Auto-apply requires parsing Copilot's free-form response.
            # For the initial version, we log the suggestion and continue.
            # The Apply-Suggestion.ps1 script is ready for when parsing is implemented.
        }
        else {
            Write-Warning '  Copilot analysis failed — continuing to next iteration anyway'
        }
    }

    $previousMetrics = $currentMetrics
    $previousCounterMetrics = $currentCounterMetrics

    # ── Record iteration metadata ──────────────────────────────────────────────
    $iterationMeta = [ordered]@{
        Iteration   = $iteration
        StartedAt   = $iterationStartedAt
        CompletedAt = (Get-Date -Format 'o')
        Improved    = $comparison.Improved
        Regression  = $comparison.Regression
        P95         = $currentMetrics.HttpReqDuration.P95
        RPS         = [math]::Round($currentMetrics.HttpReqs.Rate, 1)
    }

    # Append to the in-memory iterations list
    if ($runMetadata.Iterations -is [System.Collections.IList]) {
        $runMetadata.Iterations += [PSCustomObject]$iterationMeta
    }
    else {
        $runMetadata | Add-Member -NotePropertyName Iterations -NotePropertyValue @([PSCustomObject]$iterationMeta) -Force
    }

    # Persist after each iteration so partial runs are captured
    $runMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $runMetadataPath -Encoding utf8
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Information '' -InformationAction Continue
Write-Information '╔══════════════════════════════════════════════════════════╗' -InformationAction Continue
Write-Information '║                    HONE COMPLETE                     ║' -InformationAction Continue
Write-Information '╚══════════════════════════════════════════════════════════╝' -InformationAction Continue
Write-Information '' -InformationAction Continue
Write-Information "  Exit reason:     $exitReason" -InformationAction Continue
Write-Information "  Iterations run:  $iteration" -InformationAction Continue
Write-Information "  Best p95:        ${bestP95}ms (iteration $bestIteration)" -InformationAction Continue
Write-Information "  Baseline p95:    $($baselineMetrics.HttpReqDuration.P95)ms" -InformationAction Continue

$totalImprovement = if ($baselineMetrics.HttpReqDuration.P95 -gt 0) {
    [math]::Round((($baselineMetrics.HttpReqDuration.P95 - $bestP95) / $baselineMetrics.HttpReqDuration.P95) * 100, 1)
} else { 0 }
Write-Information "  Total improvement: ${totalImprovement}% (p95 vs baseline)" -InformationAction Continue
Write-Information '' -InformationAction Continue

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' `
    -Message "Hone loop complete: $exitReason after $iteration iterations" `
    -Data @{
        exitReason    = $exitReason
        iterations    = $iteration
        bestP95       = $bestP95
        bestIteration = $bestIteration
    }

# ── Finalize run metadata ────────────────────────────────────────────────────
$runMetadata | Add-Member -NotePropertyName LoopCompletedAt -NotePropertyValue (Get-Date -Format 'o') -Force
$runMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $runMetadataPath -Encoding utf8

# Return summary object
[PSCustomObject][ordered]@{
    ExitReason    = $exitReason
    Iterations    = $iteration
    BestP95       = $bestP95
    BestIteration = $bestIteration
    BaselineP95   = $baselineMetrics.HttpReqDuration.P95
}
