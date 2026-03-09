<#
.SYNOPSIS
    Main entry point for the Hone agentic optimization loop.

.DESCRIPTION
    Orchestrates the full iterative optimization cycle. Each experiment is
    a self-contained cycle of 5 phases:
    1. Measure    — establish current performance metrics
    2. Analyze    — populate the optimization queue (only when empty)
    3. Experiment — pick from queue, apply fix, and build
    4. Verify     — run E2E tests, stress-test, and accept/reject
    5. Publish    — push branch + create PR (or revert on failure)

    Analysis is queue-driven: the analysis agent runs only when the
    optimization queue has no actionable items. It produces 3-5 ranked
    ideas that populate the queue. Subsequent experiments pick from the
    queue until it is drained, then analysis runs again with fresh metrics.

    Supports two modes:
    - Stacked diffs (default): experiments form a linear branch chain.
      Successful experiments get PRs comparing against the last success.
      Failed experiments are reverted in-place and preserved for the record.
    - Legacy: each experiment branches from master, PRs target master,
      loop blocks waiting for merge between experiments.

.PARAMETER MaxExperiments
    Override max experiments from config.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.EXAMPLE
    .\Invoke-HoneLoop.ps1

.PARAMETER DryRun
    Skip slow operations (k6 scale tests, API start/stop, DB reset) and use
    synthetic metrics. AI agents, build, and E2E tests still run normally.
    PRs are created with a [DRY RUN] prefix.

.EXAMPLE
    .\Invoke-HoneLoop.ps1 -MaxExperiments 10

.EXAMPLE
    .\Invoke-HoneLoop.ps1 -DryRun -MaxExperiments 3
#>
[CmdletBinding()]
param(
    [int]$MaxExperiments,
    [string]$ConfigPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Undo-ExperimentBranch {
    <#
    .SYNOPSIS
        Rolls back to a target branch and deletes the experiment branch.
        Used in legacy (non-stacked) mode only.
    #>
    param(
        [Parameter(Mandatory)][string]$BranchName,
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$RestoreBranch = 'master'
    )
    try {
        Push-Location (Join-Path $RepoRoot 'sample-api')
        git checkout $RestoreBranch 2>&1 | Out-Null
        git branch -D $BranchName 2>&1 | Out-Null
        Pop-Location
    }
    catch {
        Pop-Location -ErrorAction SilentlyContinue
        Write-Warning "Rollback failed for branch '$BranchName': $_"
    }
}

function Limit-String {
    <#
    .SYNOPSIS
        Truncates a string at the last word boundary before MaxLength,
        appending "…" when truncated.
    #>
    param(
        [string]$Text,
        [int]$MaxLength = 120
    )
    if (-not $Text -or $Text.Length -le $MaxLength) { return $Text }
    $truncated = $Text.Substring(0, $MaxLength)
    $lastSpace = $truncated.LastIndexOf(' ')
    if ($lastSpace -gt ($MaxLength * 0.5)) {
        return $truncated.Substring(0, $lastSpace) + '…'
    }
    return $truncated + '…'
}

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

# Apply parameter overrides
if ($MaxExperiments -gt 0) { $config.Loop.MaxExperiments = $MaxExperiments }

$maxExp = $config.Loop.MaxExperiments
$tolerances = $config.Tolerances
$stackedDiffs = if ($config.Loop.ContainsKey('StackedDiffs')) { $config.Loop.StackedDiffs } else { $false }
$waitForMerge = if ($config.Loop.ContainsKey('WaitForMerge')) { $config.Loop.WaitForMerge } else { $true }
$maxConsecutiveFailures = if ($tolerances.ContainsKey('MaxConsecutiveFailures')) {
    $tolerances.MaxConsecutiveFailures
} else {
    $tolerances.StaleExperimentsBeforeStop
}

# ── Banner ──────────────────────────────────────────────────────────────────
$bannerTitle = if ($DryRun) { 'HONE — Agentic Optimizer [DRY RUN]' } else { 'HONE — Agentic Optimizer' }
Write-Information '' -InformationAction Continue
Write-Information '╔══════════════════════════════════════════════════════════╗' -InformationAction Continue
$innerWidth = 58
$leftPad = [Math]::Max(0, [Math]::Floor(($innerWidth - $bannerTitle.Length) / 2))
$bannerLine = $bannerTitle.PadLeft($leftPad + $bannerTitle.Length).PadRight($innerWidth)
Write-Information "║$bannerLine║" -InformationAction Continue
Write-Information '╚══════════════════════════════════════════════════════════╝' -InformationAction Continue
Write-Information '' -InformationAction Continue
if ($DryRun) {
    Write-Information '  ⚡ DRY RUN: Skipping k6 scale tests, using synthetic metrics' -InformationAction Continue
    Write-Information '             AI agents, build, and E2E tests run normally' -InformationAction Continue
    Write-Information '' -InformationAction Continue
}
Write-Information "  Max experiments:       $maxExp" -InformationAction Continue
Write-Information "  Min improvement:      $([math]::Round($tolerances.MinImprovementPct * 100, 1))% (any metric)" -InformationAction Continue
Write-Information "  Max regression:       $([math]::Round($tolerances.MaxRegressionPct * 100, 1))% (per metric)" -InformationAction Continue
Write-Information "  Stale exp stop:       $($tolerances.StaleExperimentsBeforeStop) consecutive" -InformationAction Continue
$modeLabel = if ($stackedDiffs) { 'stacked diffs (linear chain)' } else { 'legacy (each off master)' }
$mergeLabel = if ($waitForMerge) { 'yes (blocks)' } else { 'no (fire-and-forget)' }
Write-Information "  Mode:                 $modeLabel" -InformationAction Continue
Write-Information "  Wait for PR merge:    $mergeLabel" -InformationAction Continue
if ($stackedDiffs) {
    Write-Information "  Max consec. failures: $maxConsecutiveFailures" -InformationAction Continue
}
$effCfg = $tolerances.Efficiency
if ($effCfg -and $effCfg.Enabled) {
    Write-Information "  Efficiency tiebreak:  CPU $([math]::Round($effCfg.MinCpuReductionPct * 100))% / WorkingSet $([math]::Round($effCfg.MinWorkingSetReductionPct * 100))% min reduction" -InformationAction Continue
}
Write-Information '' -InformationAction Continue

# ── Prerequisite check: k6 must be on PATH ──────────────────────────────────
if (-not (Get-Command 'k6' -ErrorAction SilentlyContinue)) {
    # Auto-add common install location before failing
    $k6Default = Join-Path $env:ProgramFiles 'k6'
    if (Test-Path (Join-Path $k6Default 'k6.exe')) {
        $env:PATH = "$k6Default;$env:PATH"
    } else {
        Write-Error 'k6 is not on PATH — install k6 or add its directory to PATH before running the optimization loop'
        return
    }
}

# ── Prerequisite check: gh must be on PATH ───────────────────────────────────
if (-not (Get-Command 'gh' -ErrorAction SilentlyContinue)) {
    # Auto-add common install location before failing
    $ghDefault = Join-Path $env:ProgramFiles 'GitHub CLI'
    if (Test-Path (Join-Path $ghDefault 'gh.exe')) {
        $env:PATH = "$ghDefault;$env:PATH"
    } else {
        Write-Error 'gh is not on PATH — install GitHub CLI or add its directory to PATH before running the optimization loop'
        return
    }
}

# ── Prerequisite check: standalone copilot CLI must be on PATH ───────────────
if (-not (Get-Command 'copilot' -ErrorAction SilentlyContinue)) {
    Write-Error 'copilot CLI is not on PATH — install the GitHub Copilot CLI (https://docs.github.com/copilot/how-tos/copilot-cli) before running the optimization loop'
    return
}

# ── Ensure GH_TOKEN is set for copilot CLI ───────────────────────────────────
if (-not $env:GH_TOKEN) {
    $ghToken = gh auth token 2>$null
    if ($ghToken) {
        $env:GH_TOKEN = $ghToken
    } else {
        Write-Error 'gh is not authenticated — run ''gh auth login'' before running the optimization loop'
        return
    }
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message "Hone loop starting (max $maxExp experiments)"

# ── Collect machine info ────────────────────────────────────────────────────
$machineInfo = & (Join-Path $PSScriptRoot 'Get-MachineInfo.ps1')
Write-Information "  CPU:     $($machineInfo.Cpu.Name) ($($machineInfo.Cpu.LogicalProcessors) logical cores)" -InformationAction Continue
Write-Information "  RAM:     $($machineInfo.Memory.TotalGB)GB" -InformationAction Continue
Write-Information "  OS:      $($machineInfo.OS.Description)" -InformationAction Continue
Write-Information '' -InformationAction Continue

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message 'Machine info collected' `
    -Data @{
        cpuName          = $machineInfo.Cpu.Name
        logicalCores     = $machineInfo.Cpu.LogicalProcessors
        physicalCores    = $machineInfo.Cpu.PhysicalCores
        totalMemoryGB    = $machineInfo.Memory.TotalGB
        os               = $machineInfo.OS.Description
    }

# ── Run metadata tracking ───────────────────────────────────────────────────
$runMetadataPath = Join-Path $repoRoot $config.Api.ResultsPath 'run-metadata.json'
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
        Experiments     = @()
    }
}

# ── Load baseline ───────────────────────────────────────────────────────────
$baselinePath = Join-Path $repoRoot $config.Api.ResultsPath 'baseline.json'

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

# ── Experiment Loop ──────────────────────────────────────────────────────────
$previousMetrics = $null
$previousCounterMetrics = $null
$previousRcaExplanation = ''
$exitReason = 'max_experiments'
$bestExperiment = 0
$bestP95 = $baselineMetrics.HttpReqDuration.P95
$staleCount = 0
$consecutiveFailures = 0

# Stacked-diffs state: track the branch chain and PR stack
$currentBranch = 'master'
$lastSuccessfulBranch = 'master'
$prChain = @()
$branchChain = @('master')
$failedExperiments = @()
$successCount = 0

# ── Resume from prior run ───────────────────────────────────────────────────
$startExperiment = 1
$priorExperiments = @()
if ($runMetadata.Experiments -and $runMetadata.Experiments.Count -gt 0) {
    $priorExperiments = @($runMetadata.Experiments)
    $lastEntry = $priorExperiments[-1]
    $startExperiment = $lastEntry.Experiment + 1

    # Restore running counters (backward-compatible: default 0 for old metadata)
    $staleCount = if ($null -ne $lastEntry.StaleCount) { $lastEntry.StaleCount } else { 0 }
    $consecutiveFailures = if ($null -ne $lastEntry.ConsecutiveFailures) { $lastEntry.ConsecutiveFailures } else { 0 }

    # Restore best metrics
    foreach ($exp in $priorExperiments) {
        if ($exp.Outcome -eq 'improved' -and $exp.P95 -lt $bestP95) {
            $bestP95 = $exp.P95
            $bestExperiment = $exp.Experiment
        }
    }
    $successCount = @($priorExperiments | Where-Object { $_.Outcome -eq 'improved' }).Count

    # Restore stacked-diffs branch state
    if ($stackedDiffs) {
        $currentBranch = $lastEntry.BranchName
        $lastImproved = $priorExperiments | Where-Object { $_.Outcome -eq 'improved' } | Select-Object -Last 1
        $lastSuccessfulBranch = if ($lastImproved) { $lastImproved.BranchName } else { 'master' }
        $branchChain = @('master') + @($priorExperiments | ForEach-Object { $_.BranchName })
        $prChain = @($priorExperiments | Where-Object { $_.PrNumber } | ForEach-Object {
            [PSCustomObject]@{ Number = $_.PrNumber; Experiment = $_.Experiment; Url = "$($_.PrUrl)" }
        })
        $failedExperiments = @($priorExperiments | Where-Object { $_.Outcome -ne 'improved' } | ForEach-Object {
            [PSCustomObject]@{ Experiment = $_.Experiment; Reason = $_.Outcome }
        })
    }

    Write-Information "  Resuming from:      experiment $startExperiment ($($priorExperiments.Count) previous)" -InformationAction Continue
    Write-Information "  Prior successes:    $successCount" -InformationAction Continue
    if ($stackedDiffs) {
        Write-Information "  Current branch:     $currentBranch" -InformationAction Continue
    }
    Write-Information '' -InformationAction Continue

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'loop' -Level 'info' `
        -Message "Resuming from experiment $startExperiment (restored state from $($priorExperiments.Count) prior experiments)" `
        -Data @{
            startExperiment     = $startExperiment
            priorCount          = $priorExperiments.Count
            successCount        = $successCount
            staleCount          = $staleCount
            consecutiveFailures = $consecutiveFailures
            currentBranch       = $currentBranch
        }
}

# MaxExperiments means "run N more" — compute absolute loop end
$loopEnd = $startExperiment + $maxExp - 1

# Ensure the submodule starts on the correct branch for experiment forking
Push-Location (Join-Path $repoRoot 'sample-api')
git checkout $currentBranch 2>&1 | Out-Null
Pop-Location

for ($experiment = $startExperiment; $experiment -le $loopEnd; $experiment++) {

    $experimentStartedAt = Get-Date -Format 'o'

    Write-Information '' -InformationAction Continue
    Write-Information "━━━ Experiment $experiment / $loopEnd ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -InformationAction Continue

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'loop' -Level 'info' -Message "Starting experiment $experiment" -Experiment $experiment

    # ── Phase 1: Measure ──────────────────────────────────────────────────
    Write-Information '[1/5] 📊 Measuring (current state)...' -InformationAction Continue

    # For experiment 1, use baseline as current (no prior fix to measure).
    # For experiment 2+, use the metrics from the previous experiment's Verify phase.
    $metricsForAnalysis = if ($previousMetrics) { $previousMetrics } else { $baselineMetrics }
    $countersForAnalysis = if ($previousCounterMetrics) { $previousCounterMetrics } else { $null }

    $refP95 = $metricsForAnalysis.HttpReqDuration.P95
    $refRps = [math]::Round($metricsForAnalysis.HttpReqs.Rate, 1)
    $refLabel = if ($previousMetrics) { "experiment $($experiment - 1)" } else { 'baseline' }
    Write-Information "  Reference: p95=${refP95}ms, RPS=${refRps} (from $refLabel)" -InformationAction Continue

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "Reference metrics from ${refLabel}: p95=${refP95}ms, RPS=${refRps}" `
        -Experiment $experiment

    # ── Phase 2: Analyze (conditional — only when queue is empty) ────────
    # Build a comparison object (needed for both analysis prompts and RCA).
    $comparisonForAnalysis = & (Join-Path $PSScriptRoot 'Compare-Results.ps1') `
        -CurrentMetrics $metricsForAnalysis `
        -BaselineMetrics $baselineMetrics `
        -PreviousMetrics $previousMetrics `
        -CurrentCounterMetrics $countersForAnalysis `
        -PreviousCounterMetrics $previousCounterMetrics `
        -ConfigPath $ConfigPath `
        -Experiment $experiment

    $hasQueue = & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
        -Action 'HasActionable' -ConfigPath $ConfigPath

    if (-not $hasQueue) {
        Write-Information '[2/5] 🧠 Analyzing (queue empty — running analysis)...' -InformationAction Continue

        # ── Diagnostic profiling (runs only when analysis is needed) ─────
        $diagnosticReports = $null
        if ($config.Diagnostics -and $config.Diagnostics.Enabled -and -not $DryRun) {
            Write-Information '  Running diagnostic measurement (PerfView profiling)...' -InformationAction Continue
            $diagResult = & (Join-Path $PSScriptRoot 'Invoke-DiagnosticMeasurement.ps1') `
                -Experiment $experiment `
                -ConfigPath $ConfigPath `
                -CurrentMetrics $metricsForAnalysis

            if ($diagResult.Success) {
                $diagnosticReports = $diagResult.AnalyzerReports
                Write-Information "  Diagnostic profiling complete: $($diagnosticReports.Count) report(s)" -InformationAction Continue
            }
            else {
                throw 'Diagnostic measurement failed. Fix PerfView/profiling setup or set Diagnostics.Enabled = $false.'
            }
        }
        elseif ($DryRun) {
            Write-Information '  Diagnostic profiling skipped (dry run)' -InformationAction Continue
        }

        $rawAnalysis = & (Join-Path $PSScriptRoot 'Invoke-AnalysisAgent.ps1') `
            -CurrentMetrics $metricsForAnalysis `
            -BaselineMetrics $baselineMetrics `
            -ComparisonResult $comparisonForAnalysis `
            -CounterMetrics $countersForAnalysis `
            -Experiment $experiment `
            -ConfigPath $ConfigPath `
            -PreviousRcaExplanation $previousRcaExplanation `
            -DiagnosticReports $diagnosticReports

        if (-not $rawAnalysis.Success) {
            Write-Warning '  Analysis agent failed — skipping experiment'
            $staleCount++
            if ($stackedDiffs) { $consecutiveFailures++ }
            if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop -or ($stackedDiffs -and $consecutiveFailures -ge $maxConsecutiveFailures)) {
                $exitReason = if ($stackedDiffs) { 'max_consecutive_failures' } else { 'no_improvement' }
                Write-Information '  Stopping: too many consecutive failures' -InformationAction Continue
                break
            }
            continue
        }

        Write-Information "  Analysis complete: $(@($rawAnalysis.Opportunities).Count) opportunities found" -InformationAction Continue
        if ($rawAnalysis.ResponsePath) {
            Write-Information "  Response saved to: $($rawAnalysis.ResponsePath)" -InformationAction Continue
        }

        # Populate the optimization queue from analysis results
        $initResult = & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'Init' `
            -Opportunities $rawAnalysis.Opportunities `
            -Experiment $experiment `
            -ConfigPath $ConfigPath

        if (-not $initResult.Success) {
            Write-Warning '  Failed to initialize optimization queue — skipping experiment'
            $staleCount++
            if ($stackedDiffs) { $consecutiveFailures++ }
            continue
        }

        Write-Information "  Queue initialized with $($initResult.Count) items" -InformationAction Continue
    }
    else {
        Write-Information '[2/5] 🧠 Analyzing... (queue has items — skipping analysis)' -InformationAction Continue
    }

    # Pick the next actionable item from the queue
    $currentItem = & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
        -Action 'GetNext' -Experiment $experiment -ConfigPath $ConfigPath

    if (-not $currentItem) {
        Write-Warning '  No actionable items in queue — skipping experiment'
        $staleCount++
        if ($stackedDiffs) { $consecutiveFailures++ }
        if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop -or ($stackedDiffs -and $consecutiveFailures -ge $maxConsecutiveFailures)) {
            $exitReason = if ($stackedDiffs) { 'max_consecutive_failures' } else { 'no_improvement' }
            Write-Information '  Stopping: too many consecutive failures' -InformationAction Continue
            break
        }
        continue
    }

    # Build analysisResult-compatible object from queue item so downstream code is unchanged
    $analysisResult = [PSCustomObject]@{
        Success      = $true
        FilePath     = $currentItem.filePath
        Explanation  = $currentItem.explanation
        ResponsePath = $null
    }
    $queueItemId = $currentItem.id

    $rcaResult = $null
    $applyResult = $null
    $branchName = "$($config.Loop.BranchPrefix)-$experiment"
    $applySuccess = $false
    $skipExperiment = $false

    Write-Information "  Queue item #$($currentItem.id): $($currentItem.filePath)" -InformationAction Continue

    # ── Classification ────────────────────────────────────────────────────
    $classificationResult = & (Join-Path $PSScriptRoot 'Invoke-ClassificationAgent.ps1') `
        -FilePath $analysisResult.FilePath `
        -Explanation $analysisResult.Explanation `
        -Experiment $experiment `
        -ConfigPath $ConfigPath

    $changeScope = $classificationResult.Scope
    Write-Information "  Classification: $changeScope — $($classificationResult.Reasoning)" -InformationAction Continue

    # Save root-cause analysis to experiment directory.
    # If the analyst provided a rich root-cause document, copy it (with metrics header).
    # Otherwise, fall back to the harness-generated RCA template.
    if ($currentItem -and $currentItem.rootCausePath -and (Test-Path $currentItem.rootCausePath)) {
        $iterDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$experiment"
        if (-not (Test-Path $iterDir)) {
            New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
        }

        # Copy agent RCA and prepend a metrics summary header
        $agentRca = Get-Content $currentItem.rootCausePath -Raw
        $metricsHeader = @"
# Root Cause Analysis — Experiment $experiment

> Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | Classification: $($classificationResult.Scope) — $($classificationResult.Reasoning)

| Metric | Current | Baseline |
|--------|---------|----------|
| p95 Latency | $($metricsForAnalysis.HttpReqDuration.P95)ms | $($baselineMetrics.HttpReqDuration.P95)ms |
| Requests/sec | $([math]::Round($metricsForAnalysis.HttpReqs.Rate, 1)) | $([math]::Round($baselineMetrics.HttpReqs.Rate, 1)) |
| Error Rate | $([math]::Round($metricsForAnalysis.HttpReqFailed.Rate * 100, 2))% | $([math]::Round($baselineMetrics.HttpReqFailed.Rate * 100, 2))% |

---

"@
        $rcaPath = Join-Path $iterDir 'root-cause.md'
        ($metricsHeader + $agentRca) | Out-File -FilePath $rcaPath -Encoding utf8
        $rcaResult = [PSCustomObject]@{ Success = $true; Path = $rcaPath }
        Write-Information "  Root cause analysis saved to: $rcaPath" -InformationAction Continue
    }
    else {
        $rcaResult = & (Join-Path $PSScriptRoot 'Export-ExperimentRCA.ps1') `
            -FilePath $analysisResult.FilePath `
            -Explanation $analysisResult.Explanation `
            -ChangeScope $changeScope `
            -ScopeReasoning $classificationResult.Reasoning `
            -CurrentMetrics $metricsForAnalysis `
            -BaselineMetrics $baselineMetrics `
            -ComparisonResult $comparisonForAnalysis `
            -Experiment $experiment `
            -ConfigPath $ConfigPath

        if ($rcaResult.Success) {
            Write-Information "  Root cause analysis saved to: $($rcaResult.Path)" -InformationAction Continue
        }
    }

    $isArchitecture = ($changeScope -eq 'architecture')

    if ($isArchitecture) {
        Write-Information '  ⚠ Architecture-level change detected — skipping' -InformationAction Continue
        Write-Information "    Scope: $changeScope | File: $($analysisResult.FilePath)" -InformationAction Continue

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'analyze' -Level 'info' `
            -Message "Architecture change skipped: $(Limit-String $analysisResult.Explanation 100)" `
            -Experiment $experiment

        # Mark queue item as skipped
        & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'MarkDone' -ItemId $queueItemId `
            -Experiment $experiment -Outcome 'skipped' `
            -ConfigPath $ConfigPath

        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Experiment $experiment `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'queued' `
            -ConfigPath $ConfigPath

        $skipExperiment = $true
    }

    if ($skipExperiment) {
        $staleCount++
        if ($stackedDiffs) { $consecutiveFailures++ }
        if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop -or ($stackedDiffs -and $consecutiveFailures -ge $maxConsecutiveFailures)) {
            $exitReason = if ($stackedDiffs) { 'max_consecutive_failures' } else { 'no_improvement' }
            Write-Information '  Stopping: too many consecutive failures' -InformationAction Continue
            break
        }
        continue
    }

    # ── Phase 3: Experiment (fix + build) ──────────────────────────────────
    Write-Information '[3/5] 🧪 Experimenting (fix + build)...' -InformationAction Continue

    # ── Sub-agent 3: Fix ───────────────────────────────────────────────────
    # Load root-cause document if available for this queue item
    $rcaDocument = $null
    if ($currentItem -and $currentItem.rootCausePath -and (Test-Path $currentItem.rootCausePath)) {
        $rcaDocument = Get-Content $currentItem.rootCausePath -Raw
    }

    $fixResult = & (Join-Path $PSScriptRoot 'Invoke-FixAgent.ps1') `
        -FilePath $analysisResult.FilePath `
        -Explanation $analysisResult.Explanation `
        -RootCauseDocument $rcaDocument `
        -Experiment $experiment `
        -ConfigPath $ConfigPath

    if (-not $fixResult.Success -or -not $fixResult.CodeBlock) {
        Write-Warning '  Fix agent failed to generate code — skipping experiment'
        $staleCount++
        if ($stackedDiffs) { $consecutiveFailures++ }
        continue
    }

    # Normalise file path: ensure it starts with 'sample-api/'
    $targetFile = $analysisResult.FilePath.Trim()
    if ($targetFile -notmatch '^sample-api[\\/]') {
        $targetFile = "sample-api/$targetFile"
    }

    $fullTargetPath = Join-Path $repoRoot $targetFile
    if (-not (Test-Path (Split-Path $fullTargetPath -Parent))) {
        Write-Warning "  Cannot apply fix — target directory does not exist: $targetFile"
        $staleCount++
        if ($stackedDiffs) { $consecutiveFailures++ }
        continue
    }

    Write-Information "  Applying fix to: $targetFile" -InformationAction Continue

    # Determine the base branch for this experiment
    $baseBranch = if ($stackedDiffs) { $currentBranch } else { 'master' }

    $applyResult = & (Join-Path $PSScriptRoot 'Apply-Suggestion.ps1') `
        -FilePath $targetFile `
        -NewContent $fixResult.CodeBlock `
        -Description (Limit-String $analysisResult.Explanation 120) `
        -Experiment $experiment `
        -BaseBranch $baseBranch `
        -ConfigPath $ConfigPath

    if (-not $applyResult.Success) {
        Write-Warning "  Fix application failed: $($applyResult.Description)"
        $staleCount++
        if ($stackedDiffs) { $consecutiveFailures++ }
        continue
    }

    Write-Information "  ✓ Fix committed locally on branch: $($applyResult.BranchName)" -InformationAction Continue

    # Build the project with the fix applied
    Write-Information '  Building...' -InformationAction Continue
    $buildResult = & (Join-Path $PSScriptRoot 'Build-SampleApi.ps1') -ConfigPath $ConfigPath

    if (-not $buildResult.Success) {
        if ($stackedDiffs) {
            Write-Warning "Build failed at experiment $experiment — reverting and continuing"

            $revertResult = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $experiment `
                -Outcome 'regressed' `
                -Description 'Build failure' `
                -ConfigPath $ConfigPath

            if (-not $revertResult.Success) {
                Write-Warning "  Revert failed: $($revertResult.Outcome)"
            }

            & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
                -Action 'AddTried' `
                -Experiment $experiment `
                -Summary "Build failure: $($analysisResult.Explanation)" `
                -FilePath $analysisResult.FilePath `
                -Outcome 'regressed' `
                -ConfigPath $ConfigPath

            & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
                -Action 'MarkDone' -ItemId $queueItemId `
                -Experiment $experiment -Outcome 'regressed' `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'build_failure' }
            $consecutiveFailures++
            $staleCount++

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Information "  Stopping: $consecutiveFailures consecutive failures reached limit" -InformationAction Continue
                break
            }
            continue
        }
        else {
            $exitReason = 'build_failure'
            Write-Error "Build failed at experiment $experiment — rolling back"
            Undo-ExperimentBranch -BranchName $branchName -RepoRoot $repoRoot -RestoreBranch $currentBranch
            break
        }
    }

    # ── Phase 4: Verify (tests + measure + compare) ──────────────────────
    Write-Information '[4/5] ✅ Verifying...' -InformationAction Continue
    $testResult = & (Join-Path $PSScriptRoot 'Invoke-E2ETests.ps1') `
        -ConfigPath $ConfigPath -Experiment $experiment

    if (-not $testResult.Success) {
        if ($stackedDiffs) {
            Write-Warning "E2E tests failed at experiment $experiment — reverting and continuing"

            $revertResult = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $experiment `
                -Outcome 'regressed' `
                -Description 'E2E test failure' `
                -ConfigPath $ConfigPath

            if (-not $revertResult.Success) {
                Write-Warning "  Revert failed: $($revertResult.Outcome)"
            }

            & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
                -Action 'AddTried' `
                -Experiment $experiment `
                -Summary "Test failure: $($analysisResult.Explanation)" `
                -FilePath $analysisResult.FilePath `
                -Outcome 'regressed' `
                -ConfigPath $ConfigPath

            & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
                -Action 'MarkDone' -ItemId $queueItemId `
                -Experiment $experiment -Outcome 'regressed' `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'test_failure' }
            $consecutiveFailures++
            $staleCount++

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Information "  Stopping: $consecutiveFailures consecutive failures reached limit" -InformationAction Continue
                break
            }
            continue
        }
        else {
            $exitReason = 'test_failure'
            Write-Warning "E2E tests failed at experiment $experiment — rolling back"
            Undo-ExperimentBranch -BranchName $branchName -RepoRoot $repoRoot -RestoreBranch $currentBranch
            break
        }
    }

    # Stress-test the optimized API (or generate synthetic metrics in dry-run)
    if ($DryRun) {
        Write-Information '  Measuring... [DRY RUN] Using synthetic metrics' -InformationAction Continue

        # Generate synthetic metrics showing 5% improvement over reference
        $syntheticMetrics = [PSCustomObject]@{
            Timestamp       = Get-Date -Format 'o'
            Experiment      = $experiment
            HttpReqDuration = [PSCustomObject]@{
                Avg = [math]::Round($metricsForAnalysis.HttpReqDuration.Avg * 0.95, 2)
                P50 = [math]::Round($metricsForAnalysis.HttpReqDuration.P50 * 0.95, 2)
                P90 = [math]::Round($metricsForAnalysis.HttpReqDuration.P90 * 0.95, 2)
                P95 = [math]::Round($metricsForAnalysis.HttpReqDuration.P95 * 0.95, 2)
                P99 = [math]::Round($metricsForAnalysis.HttpReqDuration.P99 * 0.95, 2)
                Max = [math]::Round($metricsForAnalysis.HttpReqDuration.Max * 0.95, 2)
            }
            HttpReqs = [PSCustomObject]@{
                Count = $metricsForAnalysis.HttpReqs.Count
                Rate  = [math]::Round($metricsForAnalysis.HttpReqs.Rate * 1.05, 1)
            }
            HttpReqFailed = [PSCustomObject]@{
                Count = 0
                Rate  = 0
            }
        }

        $scaleResult = [PSCustomObject]@{
            Success        = $true
            Metrics        = $syntheticMetrics
            CounterMetrics = $null
            RunMetrics     = $null
        }
        $scenarioResults = @()

        Write-Information "  Synthetic p95: $($syntheticMetrics.HttpReqDuration.P95)ms (5% improvement over reference)" -InformationAction Continue
    }
    else {
        Write-Information '  Measuring (k6 scale tests)...' -InformationAction Continue

        # Reset database so every experiment starts with identical seed data
        & (Join-Path $PSScriptRoot 'Reset-Database.ps1') -ConfigPath $ConfigPath -Experiment $experiment

        $apiResult = & (Join-Path $PSScriptRoot 'Start-SampleApi.ps1') -ConfigPath $ConfigPath

        if (-not $apiResult.Success) {
            if ($stackedDiffs) {
                Write-Warning "API failed to start at experiment $experiment — reverting and continuing"

                $revertResult = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                    -BranchName $branchName `
                    -FilePath $targetFile `
                    -Experiment $experiment `
                    -Outcome 'regressed' `
                    -Description 'API start failure' `
                    -ConfigPath $ConfigPath

                if (-not $revertResult.Success) {
                    Write-Warning "  Revert failed: $($revertResult.Outcome)"
                }

                & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
                    -Action 'AddTried' `
                    -Experiment $experiment `
                    -Summary "API start failure: $($analysisResult.Explanation)" `
                    -FilePath $analysisResult.FilePath `
                    -Outcome 'regressed' `
                    -ConfigPath $ConfigPath

                & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
                    -Action 'MarkDone' -ItemId $queueItemId `
                    -Experiment $experiment -Outcome 'regressed' `
                    -ConfigPath $ConfigPath

                $currentBranch = $branchName
                $branchChain += $branchName
                $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'api_start_failure' }
                $consecutiveFailures++
                $staleCount++

                if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                    $exitReason = 'max_consecutive_failures'
                    Write-Information "  Stopping: $consecutiveFailures consecutive failures reached limit" -InformationAction Continue
                    break
                }
                continue
            }
            else {
                $exitReason = 'api_start_failure'
                Write-Error "API failed to start at experiment $experiment. Aborting."
                break
            }
        }

        try {
            $scaleResult = & (Join-Path $PSScriptRoot 'Invoke-ScaleTests.ps1') `
                -ConfigPath $ConfigPath -Experiment $experiment -BaseUrl $apiResult.BaseUrl

            if (-not $scaleResult.Success) {
                if (-not $stackedDiffs) {
                    $exitReason = 'scale_test_failure'
                    Write-Error "Scale tests failed at experiment $experiment. Aborting."
                    break
                }
                # Stacked mode: flag for revert after API is stopped (below finally)
                Write-Warning "Scale tests failed at experiment $experiment — will revert after cleanup"
            }
            else {
                # Run additional (diagnostic) scenarios only on success
                Write-Information '      Running additional scenarios...' -InformationAction Continue
                $scenarioResults = & (Join-Path $PSScriptRoot 'Invoke-AllScaleTests.ps1') `
                    -ConfigPath $ConfigPath -Experiment $experiment -SkipPrimary -BaseUrl $apiResult.BaseUrl
            }
        }
        finally {
            & (Join-Path $PSScriptRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
        }

        # Handle scale test failure in stacked mode (after API is stopped)
        if ($stackedDiffs -and (-not $scaleResult.Success)) {
            $revertResult = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $experiment `
                -Outcome 'regressed' `
                -Description 'Scale test failure' `
                -ConfigPath $ConfigPath

            if (-not $revertResult.Success) {
                Write-Warning "  Revert failed: $($revertResult.Outcome)"
            }

            & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
                -Action 'AddTried' `
                -Experiment $experiment `
                -Summary "Scale test failure: $($analysisResult.Explanation)" `
                -FilePath $analysisResult.FilePath `
                -Outcome 'regressed' `
                -ConfigPath $ConfigPath

            & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
                -Action 'MarkDone' -ItemId $queueItemId `
                -Experiment $experiment -Outcome 'regressed' `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'scale_test_failure' }
            $consecutiveFailures++
            $staleCount++

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Information "  Stopping: $consecutiveFailures consecutive failures reached limit" -InformationAction Continue
                break
            }
            continue
        }
    }

    $currentMetrics = $scaleResult.Metrics
    $currentCounterMetrics = $scaleResult.CounterMetrics

    # Display runtime counter highlights if available
    if ($currentCounterMetrics) {
        $cpuInfo = if ($currentCounterMetrics.Runtime.CpuUsage) { "CPU avg: $($currentCounterMetrics.Runtime.CpuUsage.Avg)%" } else { 'CPU: N/A' }
        $heapInfo = if ($currentCounterMetrics.Runtime.GcHeapSizeMB) { "GC heap max: $($currentCounterMetrics.Runtime.GcHeapSizeMB.Max)MB" } else { 'Heap: N/A' }
        $gen2Info = if ($currentCounterMetrics.Runtime.Gen2Collections) { "Gen2: $($currentCounterMetrics.Runtime.Gen2Collections.Last)" } else { 'Gen2: N/A' }
        $threadInfo = if ($currentCounterMetrics.Runtime.ThreadPoolThreads) { "Threads max: $($currentCounterMetrics.Runtime.ThreadPoolThreads.Max)" } else { 'Threads: N/A' }
        Write-Information "  Runtime counters: $cpuInfo | $heapInfo | $gen2Info | $threadInfo" -InformationAction Continue
    }

    # Track best experiment
    if ($currentMetrics.HttpReqDuration.P95 -lt $bestP95) {
        $bestP95 = $currentMetrics.HttpReqDuration.P95
        $bestExperiment = $experiment
    }

    # Compare post-fix metrics against reference
    Write-Information '  Comparing results...' -InformationAction Continue

    $comparison = & (Join-Path $PSScriptRoot 'Compare-Results.ps1') `
        -CurrentMetrics $currentMetrics `
        -BaselineMetrics $baselineMetrics `
        -PreviousMetrics $previousMetrics `
        -CurrentCounterMetrics $currentCounterMetrics `
        -PreviousCounterMetrics $previousCounterMetrics `
        -RunMetrics $scaleResult.RunMetrics `
        -ConfigPath $ConfigPath `
        -Experiment $experiment

    # Display per-metric deltas
    $d = $comparison.Deltas
    Write-Information ("  p95: {0}ms ({1}%) | RPS: {2} ({3}%) | Errors: {4}% ({5}%) | vs baseline: {6}%" -f `
        $d.P95Latency.Current, $d.P95Latency.ChangePct, `
        $d.RPS.Current, $d.RPS.ChangePct, `
        [math]::Round($d.ErrorRate.Current * 100, 2), $d.ErrorRate.ChangePct, `
        $comparison.ImprovementPct) -InformationAction Continue

    # Display variance info if available
    if ($comparison.Variance) {
        $v = $comparison.Variance
        $cvLabel = if ($v.CV -gt 10) { '⚠' } else { '✓' }
        Write-Information ("  $cvLabel Variance: CV={0}% | range: {1}ms—{2}ms | stddev: {3}ms ({4} runs)" -f `
            $v.CV, $v.Min, $v.Max, $v.StdDev, $v.Runs) -InformationAction Continue
    }

    # Display efficiency deltas when counter data is available
    if ($comparison.EfficiencyDeltas) {
        $ed = $comparison.EfficiencyDeltas
        Write-Information ("  CPU: {0}% ({1}%) | WorkingSet: {2}MB ({3}%)" -f `
            $ed.CpuUsage.Current, $ed.CpuUsage.ChangePct, `
            $ed.WorkingSet.Current, $ed.WorkingSet.ChangePct) -InformationAction Continue
    }

    # ── Phase 5: Publish ──────────────────────────────────────────────────
    $experimentOutcome = 'stale'

    if ($comparison.Regression) {
        $experimentOutcome = 'regressed'
        Write-Warning "  Regression detected: $($comparison.RegressionDetail)"

        # Update metadata with regression outcome
        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Experiment $experiment `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'regressed' `
            -ConfigPath $ConfigPath

        # Mark queue item as done
        & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'MarkDone' -ItemId $queueItemId `
            -Experiment $experiment -Outcome 'regressed' `
            -ConfigPath $ConfigPath

        if ($stackedDiffs) {
            Write-Warning "  Reverting code change, preserving artifacts"

            $revertResult = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $experiment `
                -Outcome 'regressed' `
                -Description (Limit-String $analysisResult.Explanation 120) `
                -ConfigPath $ConfigPath

            if (-not $revertResult.Success) {
                Write-Warning "  Revert failed: $($revertResult.Outcome)"
            }

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'regressed' }
            $consecutiveFailures++
            $staleCount++

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Information "  Stopping: $consecutiveFailures consecutive failures reached limit" -InformationAction Continue
                break
            }
        }
        else {
            $exitReason = 'regression'
            Write-Warning "  Rolling back to previous state"
            Undo-ExperimentBranch -BranchName $branchName -RepoRoot $repoRoot -RestoreBranch $currentBranch
            break
        }
    }
    elseif ($comparison.Improved -or $comparison.TiebreakerUsed) {
        $experimentOutcome = 'improved'
        $staleCount = 0
        $consecutiveFailures = 0
        $successCount++

        if ($comparison.TiebreakerUsed) {
            Write-Information '  ↑ Efficiency improvement (tiebreaker) — publishing' -InformationAction Continue
        }
        else {
            Write-Information '  ↑ Improvement detected — publishing' -InformationAction Continue
        }

        Write-Information '[5/5] 📦 Publishing (push + PR)...' -InformationAction Continue

        # Update metadata BEFORE the amend so entries appear in the commit
        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Experiment $experiment `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'improved' `
            -ConfigPath $ConfigPath

        # Mark queue item as done
        & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'MarkDone' -ItemId $queueItemId `
            -Experiment $experiment -Outcome 'improved' `
            -ConfigPath $ConfigPath

        # Amend the commit to include post-fix artifacts (k6 summaries, comparison data)
        Push-Location (Join-Path $repoRoot 'sample-api')
        $experimentDir = Join-Path (Join-Path $repoRoot 'sample-api') 'results' "experiment-$experiment"
        if (Test-Path $experimentDir) {
            git add "results/experiment-$experiment/" 2>&1 | Out-Null
        }
        $runMetadataFile = Join-Path (Join-Path $repoRoot 'sample-api') 'results' 'run-metadata.json'
        if (Test-Path $runMetadataFile) {
            git add results/run-metadata.json 2>&1 | Out-Null
        }
        $metadataDir = Join-Path (Join-Path $repoRoot 'sample-api') 'results' 'metadata'
        if (Test-Path $metadataDir) {
            git add results/metadata/ 2>&1 | Out-Null
        }
        git commit --amend --no-edit 2>&1 | Out-Null

        # Push and create PR
        git push -u origin $branchName 2>&1 | Out-Null

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'info' `
            -Message "Branch pushed to origin: $branchName" `
            -Experiment $experiment

        # Determine PR base: stacked mode uses lastSuccessfulBranch; legacy uses master
        $prBaseBranch = if ($stackedDiffs) { $lastSuccessfulBranch } else { 'master' }

        # Build PR body with stack context
        $stackNote = ''
        if ($stackedDiffs -and $prChain.Count -gt 0) {
            $stackParts = @('`master`')
            foreach ($pr in $prChain) {
                $stackParts += "PR #$($pr.Number) (experiment-$($pr.Experiment))"
            }
            $stackParts += "**this PR** (experiment-$experiment)"
            $stackLine = $stackParts -join ' → '

            $failedBetween = @($failedExperiments | Where-Object {
                $_.Experiment -gt ($prChain[-1].Experiment) -and $_.Experiment -lt $experiment
            })
            $failedNote = ''
            if ($failedBetween.Count -gt 0) {
                $failedList = ($failedBetween | ForEach-Object { "$($_.Experiment) ($($_.Reason))" }) -join ', '
                $failedNote = "`n`n> **Note:** Experiments $failedList were attempted but their code changes were reverted. See their branches for details."
            }

            $stackNote = @"

**Stack:** $stackLine

**Base:** ``$prBaseBranch`` (review only the incremental change)$failedNote

"@
        }

        # Build per-scenario breakdown table from diagnostic scenario results
        $scenarioBreakdown = ''
        if ($scenarioResults -and $scenarioResults.Count -gt 0) {
            $scenarioRows = @()
            foreach ($sr in $scenarioResults) {
                if (-not $sr.Metrics) { continue }

                $scenarioBaselinePath = Join-Path $repoRoot $config.Api.ResultsPath "baseline-$($sr.ScenarioName).json"
                if (-not (Test-Path $scenarioBaselinePath)) { continue }

                $scenarioBaseline = Get-Content $scenarioBaselinePath -Raw | ConvertFrom-Json
                $sbP95 = $scenarioBaseline.HttpReqDuration.P95
                $scP95 = $sr.Metrics.HttpReqDuration.P95

                $scenarioDelta = if ($sbP95 -gt 0) {
                    [math]::Round((($scP95 - $sbP95) / $sbP95) * 100, 1)
                } else { 0 }
                $deltaSign = if ($scenarioDelta -gt 0) { '+' } else { '' }
                $indicator = if ($scenarioDelta -le -5) { '🟢' } elseif ($scenarioDelta -ge 10) { '🔴' } else { '⚪' }

                $scenarioRows += "| $indicator $($sr.ScenarioName) | $([math]::Round($sbP95, 1))ms | $([math]::Round($scP95, 1))ms | ${deltaSign}${scenarioDelta}% |"
            }

            if ($scenarioRows.Count -gt 0) {
                $scenarioTable = ($scenarioRows -join "`n")
                $scenarioBreakdown = @"

### Per-Scenario Breakdown
| Scenario | Baseline p95 | Current p95 | Delta |
|----------|-------------|-------------|-------|
$scenarioTable

"@
            }
        }

        $dryRunNotice = ''
        if ($DryRun) {
            $dryRunNotice = @"

> ⚡ **DRY RUN** — This PR was created in dry-run mode. Performance metrics are synthetic (not from real k6 tests). The code change is real but has not been stress-tested.

"@
        }

        # Include root-cause analysis in PR body if available
        $rcaSection = ''
        if ($rcaDocument) {
            $rcaSection = @"

### Root Cause Analysis

<details>
<summary>Click to expand full analysis</summary>

$rcaDocument

</details>

"@
        }

        $prBody = @"
## Hone Experiment $experiment
$dryRunNotice$stackNote
**Optimization:** $($analysisResult.Explanation)

**File changed:** ``$($analysisResult.FilePath)``
$rcaSection
### Performance Results
| Metric | Baseline | After Fix | Delta |
|--------|----------|-----------|-------|
| p95 Latency | $($baselineMetrics.HttpReqDuration.P95)ms | $($currentMetrics.HttpReqDuration.P95)ms | $($d.P95Latency.ChangePct)% |
| Requests/sec | $([math]::Round($baselineMetrics.HttpReqs.Rate, 1)) | $([math]::Round($currentMetrics.HttpReqs.Rate, 1)) | $($d.RPS.ChangePct)% |
| Error Rate | $([math]::Round($baselineMetrics.HttpReqFailed.Rate * 100, 2))% | $([math]::Round($currentMetrics.HttpReqFailed.Rate * 100, 2))% | $($d.ErrorRate.ChangePct)% |

**vs baseline improvement:** $($comparison.ImprovementPct)%
$scenarioBreakdown
---
*Auto-generated by the Hone agentic optimization harness.*
"@

        $prTitlePrefix = if ($DryRun) { '[DRY RUN] ' } else { '' }
        $prUrl = gh pr create `
            --base $prBaseBranch `
            --head $branchName `
            --title "${prTitlePrefix}hone(experiment-$experiment): $(Limit-String $analysisResult.Explanation 120)" `
            --body $prBody 2>&1

        $prNumber = $null
        if ($LASTEXITCODE -eq 0) {
            $prNumber = ($prUrl -split '/')[-1]

            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'publish' -Level 'info' `
                -Message "Pull request created: $prUrl" `
                -Experiment $experiment `
                -Data @{ prUrl = "$prUrl"; prNumber = $prNumber; baseBranch = $prBaseBranch }

            Write-Information "  ✓ Pull request created: $prUrl (base: $prBaseBranch)" -InformationAction Continue
        }
        else {
            Write-Warning "  Failed to create pull request: $prUrl"
        }

        # Update stacked-diffs state
        $lastSuccessfulBranch = $branchName
        $currentBranch = $branchName
        $branchChain += $branchName
        if ($prNumber) {
            $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl" }
        }

        # Wait for PR merge if configured (legacy mode or explicit opt-in)
        $shouldWait = $waitForMerge -and $prNumber -and ($experiment -lt $loopEnd)
        if ($shouldWait) {
            Write-Information '' -InformationAction Continue
            Write-Information '  ⏳ Waiting for PR to be reviewed and merged...' -InformationAction Continue
            Write-Information '     Merge the PR in GitHub to continue the optimization loop.' -InformationAction Continue

            $pollInterval = 30
            $lastLog = [datetime]::MinValue
            $logEvery = [timespan]::FromMinutes(5)

            while ($true) {
                Start-Sleep -Seconds $pollInterval
                $prState = gh pr view $prNumber --json state,mergedAt 2>$null | ConvertFrom-Json
                if ($prState.state -eq 'MERGED') {
                    Write-Information "  ✓ PR #$prNumber merged — continuing loop" -InformationAction Continue

                    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                        -Phase 'publish' -Level 'info' `
                        -Message "PR #$prNumber merged at $($prState.mergedAt)" `
                        -Experiment $experiment

                    if (-not $stackedDiffs) {
                        # Legacy mode: update local master
                        git fetch origin master 2>&1 | Out-Null
                        git checkout master 2>&1 | Out-Null
                        git merge origin/master --ff-only 2>&1 | Out-Null
                        $currentBranch = 'master'
                    }
                    break
                }
                elseif ($prState.state -eq 'CLOSED') {
                    Write-Warning "  PR #$prNumber was closed without merging — stopping loop"

                    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                        -Phase 'publish' -Level 'warning' `
                        -Message "PR #$prNumber closed without merge" `
                        -Experiment $experiment

                    if (-not $stackedDiffs) {
                        git checkout master 2>&1 | Out-Null
                        $currentBranch = 'master'
                    }
                    $exitReason = 'pr_rejected'
                    break
                }
                else {
                    if (([datetime]::Now - $lastLog) -ge $logEvery) {
                        Write-Information "  … Still waiting for PR #$prNumber to be merged ($(Get-Date -Format 'HH:mm'))" -InformationAction Continue
                        $lastLog = [datetime]::Now
                    }
                }
            }

            # If PR was rejected, break out of the main loop
            if ($exitReason -eq 'pr_rejected') {
                Pop-Location
                break
            }
        }

        Pop-Location
    }
    else {
        # No improvement and no regression — stale
        $staleCount++
        $consecutiveFailures++

        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Experiment $experiment `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'stale' `
            -ConfigPath $ConfigPath

        # Mark queue item as done
        & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'MarkDone' -ItemId $queueItemId `
            -Experiment $experiment -Outcome 'stale' `
            -ConfigPath $ConfigPath

        if ($stackedDiffs) {
            Write-Information "  ─ No improvement (stale — failure $consecutiveFailures / $maxConsecutiveFailures)" -InformationAction Continue

            $revertResult = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $experiment `
                -Outcome 'stale' `
                -Description (Limit-String $analysisResult.Explanation 120) `
                -ConfigPath $ConfigPath

            if (-not $revertResult.Success) {
                Write-Warning "  Revert failed: $($revertResult.Outcome)"
            }

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'stale' }

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Information "  Stopping: $consecutiveFailures consecutive failures reached limit" -InformationAction Continue
                break
            }
        }
        else {
            Write-Information "  ─ No improvement (stale $staleCount / $($tolerances.StaleExperimentsBeforeStop))" -InformationAction Continue

            Undo-ExperimentBranch -BranchName $branchName -RepoRoot $repoRoot -RestoreBranch $currentBranch

            if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop) {
                $exitReason = 'no_improvement'
                Write-Information '  Stopping: no improvement for consecutive experiments' -InformationAction Continue
                break
            }
        }
    }

    # Only update metrics reference on successful experiments.
    # After a regression/stale revert, the code is back to the previous state
    # so the reference metrics should remain from the last successful experiment.
    if ($experimentOutcome -eq 'improved') {
        $previousMetrics = $currentMetrics
        $previousCounterMetrics = $currentCounterMetrics
    }
    $previousRcaExplanation = if ($analysisResult) { $analysisResult.Explanation } else { '' }

    # ── Record experiment metadata ──────────────────────────────────────────────
    $experimentPrNumber = if ($experimentOutcome -eq 'improved' -and $prNumber) { $prNumber } else { $null }
    $experimentPrUrl = if ($experimentOutcome -eq 'improved' -and $prNumber -and $prUrl) { "$prUrl" } else { $null }

    $experimentMeta = [ordered]@{
        Experiment          = $experiment
        StartedAt           = $experimentStartedAt
        CompletedAt         = (Get-Date -Format 'o')
        Improved            = $comparison.Improved
        Regression          = $comparison.Regression
        P95                 = $currentMetrics.HttpReqDuration.P95
        RPS                 = [math]::Round($currentMetrics.HttpReqs.Rate, 1)
        Outcome             = $experimentOutcome
        BranchName          = $branchName
        BaseBranch          = if ($stackedDiffs) { $baseBranch } else { 'master' }
        PrNumber            = $experimentPrNumber
        PrUrl               = $experimentPrUrl
        StaleCount          = $staleCount
        ConsecutiveFailures = $consecutiveFailures
    }

    # Append to the in-memory experiments list
    if ($runMetadata.Experiments -is [System.Collections.IList]) {
        $runMetadata.Experiments += [PSCustomObject]$experimentMeta
    }
    else {
        $runMetadata | Add-Member -NotePropertyName Experiments -NotePropertyValue @([PSCustomObject]$experimentMeta) -Force
    }

    # Persist after each experiment so partial runs are captured
    $runMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $runMetadataPath -Encoding utf8
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Information '' -InformationAction Continue
Write-Information '╔══════════════════════════════════════════════════════════╗' -InformationAction Continue
Write-Information '║                    HONE COMPLETE                     ║' -InformationAction Continue
Write-Information '╚══════════════════════════════════════════════════════════╝' -InformationAction Continue
Write-Information '' -InformationAction Continue
Write-Information "  Exit reason:     $exitReason" -InformationAction Continue
Write-Information "  Experiments run:  $experiment" -InformationAction Continue
Write-Information "  Successful:      $successCount / $experiment" -InformationAction Continue
Write-Information "  Best p95:        ${bestP95}ms (experiment $bestExperiment)" -InformationAction Continue
Write-Information "  Baseline p95:    $($baselineMetrics.HttpReqDuration.P95)ms" -InformationAction Continue

$totalImprovement = if ($baselineMetrics.HttpReqDuration.P95 -gt 0) {
    [math]::Round((($baselineMetrics.HttpReqDuration.P95 - $bestP95) / $baselineMetrics.HttpReqDuration.P95) * 100, 1)
} else { 0 }
Write-Information "  Total improvement: ${totalImprovement}% (p95 vs baseline)" -InformationAction Continue

if ($stackedDiffs) {
    Write-Information '' -InformationAction Continue

    # Branch chain display
    $chainDisplay = ($branchChain | ForEach-Object {
        $branch = $_
        $failed = $failedExperiments | Where-Object {
            "$($config.Loop.BranchPrefix)-$($_.Experiment)" -eq $branch
        }
        if ($failed) { "$branch ✗" } else { "$branch ✓" }
    }) -join ' → '
    Write-Information "  Branch chain:" -InformationAction Continue
    Write-Information "    $chainDisplay" -InformationAction Continue

    # PR stack display
    if ($prChain.Count -gt 0) {
        $prDisplay = ($prChain | ForEach-Object { "PR #$($_.Number) (experiment-$($_.Experiment))" }) -join ' → '
        Write-Information '' -InformationAction Continue
        Write-Information "  PR stack (reviewable):" -InformationAction Continue
        Write-Information "    $prDisplay" -InformationAction Continue
    }

    # Failed experiments display
    if ($failedExperiments.Count -gt 0) {
        $failDisplay = ($failedExperiments | ForEach-Object { "$($_.Experiment) ($($_.Reason))" }) -join ', '
        Write-Information '' -InformationAction Continue
        Write-Information "  Failed experiments: $failDisplay" -InformationAction Continue
    }
}

Write-Information '' -InformationAction Continue

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' `
    -Message "Hone loop complete: $exitReason after $experiment experiments" `
    -Data @{
        exitReason    = $exitReason
        experiments    = $experiment
        bestP95       = $bestP95
        bestExperiment = $bestExperiment
        successCount  = $successCount
        prChain       = @($prChain | ForEach-Object { $_.Number })
    }

# ── Finalize run metadata ────────────────────────────────────────────────────
$runMetadata | Add-Member -NotePropertyName LoopCompletedAt -NotePropertyValue (Get-Date -Format 'o') -Force
if ($stackedDiffs) {
    $runMetadata | Add-Member -NotePropertyName PrChain -NotePropertyValue @($prChain | ForEach-Object { $_.Number }) -Force
    $runMetadata | Add-Member -NotePropertyName FullBranchChain -NotePropertyValue $branchChain -Force
}
$runMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $runMetadataPath -Encoding utf8

# Return summary object
[PSCustomObject][ordered]@{
    ExitReason        = $exitReason
    Experiments        = $experiment
    SuccessCount      = $successCount
    BestP95           = $bestP95
    BestExperiment     = $bestExperiment
    BaselineP95       = $baselineMetrics.HttpReqDuration.P95
    PrChain           = @($prChain | ForEach-Object { $_.Number })
    FullBranchChain   = $branchChain
    FailedExperiments  = @($failedExperiments | ForEach-Object { $_.Experiment })
}
