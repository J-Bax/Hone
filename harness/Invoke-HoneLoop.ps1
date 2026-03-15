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

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath

& (Join-Path $PSScriptRoot 'Test-HoneConfig.ps1') -ConfigPath $ConfigPath

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
Write-Status ''
Write-Status '══════════════════════════════════════════════════════════════'
Write-Status "  $bannerTitle"
Write-Status '══════════════════════════════════════════════════════════════'
Write-Status ''
if ($DryRun) {
    Write-Status '  ⚡ DRY RUN: Skipping k6 scale tests, using synthetic metrics'
    Write-Status '             AI agents, build, and E2E tests run normally'
    Write-Status ''
}
Write-Status "  Max experiments:       $maxExp"
Write-Status "  Min improvement:      $([math]::Round($tolerances.MinImprovementPct * 100, 1))% (any metric)"
Write-Status "  Max regression:       $([math]::Round($tolerances.MaxRegressionPct * 100, 1))% (per metric)"
Write-Status "  Stale exp stop:       $($tolerances.StaleExperimentsBeforeStop) consecutive"
$modeLabel = if ($stackedDiffs) { 'stacked diffs (linear chain)' } else { 'legacy (each off master)' }
$mergeLabel = if ($waitForMerge) { 'yes (blocks)' } else { 'no (fire-and-forget)' }
Write-Status "  Mode:                 $modeLabel"
Write-Status "  Wait for PR merge:    $mergeLabel"
if ($stackedDiffs) {
    Write-Status "  Max consec. failures: $maxConsecutiveFailures"
}
$effCfg = $tolerances.Efficiency
if ($effCfg -and $effCfg.Enabled) {
    Write-Status "  Efficiency tiebreak:  CPU $([math]::Round($effCfg.MinCpuReductionPct * 100))% / WorkingSet $([math]::Round($effCfg.MinWorkingSetReductionPct * 100))% min reduction"
}
Write-Status ''

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

# ── Ensure GH_TOKEN is set and valid ─────────────────────────────────────────
if (-not $env:GH_TOKEN) {
    # Try 'gh auth token' (gh >= 2.17) then fall back to parsing hosts.yml
    $ghToken = gh auth token 2>$null
    if (-not $ghToken -or $LASTEXITCODE -ne 0) {
        $hostsFile = Join-Path $env:APPDATA 'GitHub CLI\hosts.yml'
        if (Test-Path $hostsFile) {
            $hostsContent = Get-Content $hostsFile -Raw
            if ($hostsContent -match 'oauth_token:\s*(.+)') {
                $ghToken = $Matches[1].Trim()
            }
        }
    }
    if ($ghToken) {
        $env:GH_TOKEN = $ghToken
    } else {
        Write-Error 'gh is not authenticated — run ''gh auth login'' before running the optimization loop'
        return
    }
}
# Validate the token actually works (catches expired/revoked tokens)
$ghStatus = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "gh token is invalid or expired — run 'gh auth login' to refresh.`n$ghStatus"
    return
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message "Hone loop starting (max $maxExp experiments)"

# ── Collect machine info ────────────────────────────────────────────────────
$machineInfo = & (Join-Path $PSScriptRoot 'Get-MachineInfo.ps1')
Write-Status "  CPU:     $($machineInfo.Cpu.Name) ($($machineInfo.Cpu.LogicalProcessors) logical cores)"
Write-Status "  RAM:     $($machineInfo.Memory.TotalGB)GB"
Write-Status "  OS:      $($machineInfo.OS.Description)"
Write-Status ''

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
    Write-Status 'No baseline found. Running Get-PerformanceBaseline.ps1 first...'
    & (Join-Path $PSScriptRoot 'Get-PerformanceBaseline.ps1') -ConfigPath $ConfigPath

    if (-not (Test-Path $baselinePath)) {
        Write-Error 'Failed to establish baseline. Aborting.'
        return
    }
}

$baselineMetrics = Get-Content $baselinePath -Raw | ConvertFrom-Json
Write-Status "Baseline loaded: p95=$($baselineMetrics.HttpReqDuration.P95)ms"

# ── Experiment Loop ──────────────────────────────────────────────────────────
$previousMetrics = $null
$previousCounterMetrics = $null
$previousScenarioResults = $null
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
        if ($exp.Outcome -eq 'improved' -and $null -ne $exp.P95 -and $exp.P95 -lt $bestP95) {
            $bestP95 = $exp.P95
            $bestExperiment = $exp.Experiment
        }
    }
    $successCount = @($priorExperiments | Where-Object { $_.Outcome -eq 'improved' }).Count

    # Restore stacked-diffs branch state
    if ($stackedDiffs) {
        # For early-exit experiments (e.g., analysis_failed), BranchName may be null.
        # Walk backward to find the last experiment that had a branch.
        $lastWithBranch = $priorExperiments | Where-Object { $_.BranchName } | Select-Object -Last 1
        $currentBranch = if ($lastWithBranch) { $lastWithBranch.BranchName } else { 'master' }
        $lastImproved = $priorExperiments | Where-Object { $_.Outcome -eq 'improved' } | Select-Object -Last 1
        $lastSuccessfulBranch = if ($lastImproved) { $lastImproved.BranchName } else { 'master' }
        $branchChain = @('master') + @($priorExperiments | Where-Object { $_.BranchName } | ForEach-Object { $_.BranchName })
        $prChain = @($priorExperiments | Where-Object { $_.PrNumber } | ForEach-Object {
            [PSCustomObject]@{ Number = $_.PrNumber; Experiment = $_.Experiment; Url = "$($_.PrUrl)"; Outcome = $_.Outcome }
        })
        $failedExperiments = @($priorExperiments | Where-Object { $_.Outcome -ne 'improved' } | ForEach-Object {
            [PSCustomObject]@{ Experiment = $_.Experiment; Reason = $_.Outcome }
        })
    }

    Write-Status "  Resuming from:      experiment $startExperiment ($($priorExperiments.Count) previous)"
    Write-Status "  Prior successes:    $successCount"
    if ($stackedDiffs) {
        Write-Status "  Current branch:     $currentBranch"
    }
    Write-Status ''

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

    $experimentsRun = $experiment - $startExperiment + 1
    $experimentStartedAt = Get-Date -Format 'o'
    $prNumber = $null
    $prUrl = $null
    $branchName = $null  # Set properly after queue item is picked

    Write-Status ''
    Write-Status "━━━ Experiment $experiment / $loopEnd ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'loop' -Level 'info' -Message "Starting experiment $experiment" -Experiment $experiment

    # ── Phase 1: Measure ──────────────────────────────────────────────────
    Write-Status '[1/5] 📊 Measuring (current state)...'

    # For experiment 1, use baseline as current (no prior fix to measure).
    # For experiment 2+, use the metrics from the previous experiment's Verify phase.
    $metricsForAnalysis = if ($previousMetrics) { $previousMetrics } else { $baselineMetrics }
    $countersForAnalysis = if ($previousCounterMetrics) { $previousCounterMetrics } else { $null }

    $refP95 = $metricsForAnalysis.HttpReqDuration.P95
    $refRps = [math]::Round($metricsForAnalysis.HttpReqs.Rate, 1)
    $refLabel = if ($previousMetrics) { "experiment $($experiment - 1)" } else { 'baseline' }
    Write-Status "  Reference: p95=${refP95}ms, RPS=${refRps} (from $refLabel)"

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
        Write-Status '[2/5] 🧠 Analyzing (queue empty — running analysis)...'

        # ── Diagnostic profiling (runs only when analysis is needed) ─────
        $diagnosticReports = $null
        if ($config.Diagnostics -and $config.Diagnostics.Enabled -and -not $DryRun) {
            Write-Status '  Running diagnostic measurement (PerfView profiling)...'
            $diagResult = & (Join-Path $PSScriptRoot 'Invoke-DiagnosticMeasurement.ps1') `
                -Experiment $experiment `
                -ConfigPath $ConfigPath `
                -CurrentMetrics $metricsForAnalysis

            if ($diagResult.Success) {
                $diagnosticReports = $diagResult.AnalyzerReports
                Write-Status "  Diagnostic profiling complete: $($diagnosticReports.Count) report(s)"
            }
            else {
                throw 'Diagnostic measurement failed. Fix PerfView/profiling setup or set Diagnostics.Enabled = $false.'
            }
        }
        elseif ($DryRun) {
            Write-Status '  Diagnostic profiling skipped (dry run)'
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
            Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'analysis_failed' `
                -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { 'master' }) `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
            if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop -or ($stackedDiffs -and $consecutiveFailures -ge $maxConsecutiveFailures)) {
                $exitReason = if ($stackedDiffs) { 'max_consecutive_failures' } else { 'no_improvement' }
                Write-Status '  Stopping: too many consecutive failures'
                break
            }
            continue
        }

        Write-Status "  Analysis complete: $(@($rawAnalysis.Opportunities).Count) opportunities found"
        if ($rawAnalysis.ResponsePath) {
            Write-Status "  Response saved to: $($rawAnalysis.ResponsePath)"
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
            Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'queue_init_failed' `
                -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { 'master' }) `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
            continue
        }

        Write-Status "  Queue initialized with $($initResult.Count) items"
    }
    else {
        Write-Status '[2/5] 🧠 Analyzing... (queue has items — skipping analysis)'
    }

    # Pick the next actionable item from the queue.
    # If an item is classified as architecture, skip it and try the next one
    # rather than wasting the entire experiment iteration.
    $currentItem = $null
    $classificationResult = $null
    $changeScope = $null
    $queueItemId = $null
    $skipExperiment = $false

    while ($true) {
        $currentItem = & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'GetNext' -Experiment $experiment -ConfigPath $ConfigPath

        if (-not $currentItem) { break }

        $queueItemId = $currentItem.id
        Write-Status "  Queue item #$($currentItem.id): $($currentItem.filePath)"

        # ── Classification ────────────────────────────────────────────────
        $classificationResult = & (Join-Path $PSScriptRoot 'Invoke-ClassificationAgent.ps1') `
            -FilePath $currentItem.filePath `
            -Explanation $currentItem.explanation `
            -Experiment $experiment `
            -ConfigPath $ConfigPath

        $changeScope = $classificationResult.Scope
        Write-Status "  Classification: $changeScope — $($classificationResult.Reasoning)"

        if ($changeScope -ne 'architecture') { break }

        # Architecture-level change — mark skipped and try the next queue item
        Write-Status "  ⚠ Architecture-level change detected — skipping to next queue item"
        Write-Status "    Scope: $changeScope | File: $($currentItem.filePath)"

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'analyze' -Level 'info' `
            -Message "Architecture change skipped: $(Limit-String $currentItem.explanation 100)" `
            -Experiment $experiment

        & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'MarkDone' -ItemId $queueItemId `
            -Experiment $experiment -Outcome 'skipped' `
            -ConfigPath $ConfigPath

        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Experiment $experiment `
            -Summary $currentItem.explanation `
            -FilePath $currentItem.filePath `
            -Outcome 'queued' `
            -ConfigPath $ConfigPath

        $currentItem = $null
    }

    if (-not $currentItem) {
        Write-Warning '  No actionable items in queue — skipping experiment'
        $staleCount++
        if ($stackedDiffs) { $consecutiveFailures++ }
        Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
            -Experiment $experiment -StartedAt $experimentStartedAt `
            -Outcome 'no_queue_items' `
            -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { 'master' }) `
            -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
        if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop -or ($stackedDiffs -and $consecutiveFailures -ge $maxConsecutiveFailures)) {
            $exitReason = if ($stackedDiffs) { 'max_consecutive_failures' } else { 'no_improvement' }
            Write-Status '  Stopping: too many consecutive failures'
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

    $rcaResult = $null
    $applyResult = $null
    $branchName = "$($config.Loop.BranchPrefix)-$experiment"
    $applySuccess = $false

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
        Write-Status "  Root cause analysis saved to: $rcaPath"
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
            Write-Status "  Root cause analysis saved to: $($rcaResult.Path)"
        }
    }

    # ── Phase 3: Experiment (fix + build) ──────────────────────────────────
    Write-Status '[3/5] 🧪 Experimenting (fix + build)...'

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
        Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
            -Experiment $experiment -StartedAt $experimentStartedAt `
            -Outcome 'fix_failed' -BranchName $branchName `
            -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { 'master' }) `
            -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
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
        Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
            -Experiment $experiment -StartedAt $experimentStartedAt `
            -Outcome 'invalid_target' -BranchName $branchName `
            -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { 'master' }) `
            -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
        continue
    }

    Write-Status "  Applying fix to: $targetFile"

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
        Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
            -Experiment $experiment -StartedAt $experimentStartedAt `
            -Outcome 'apply_failed' -BranchName $branchName `
            -BaseBranch $baseBranch `
            -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
        continue
    }

    Write-Status "  ✓ Fix committed locally on branch: $($applyResult.BranchName)"

    # Build the project with the fix applied
    Write-Status '  Building...'
    $buildResult = & (Join-Path $PSScriptRoot 'Build-SampleApi.ps1') -ConfigPath $ConfigPath -Experiment $experiment

    if (-not $buildResult.Success) {
        if ($stackedDiffs) {
            Write-Warning "Build failed at experiment $experiment — reverting and continuing"

            & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                -BranchName $branchName -FilePath $targetFile `
                -Experiment $experiment -Outcome 'regressed' `
                -RevertDescription 'Build failure' `
                -MetadataSummary "Build failure: $($analysisResult.Explanation)" `
                -MetadataFilePath $analysisResult.FilePath `
                -QueueItemId $queueItemId `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'build_failure' }
            $consecutiveFailures++
            $staleCount++

            # Create rejected PR for the record
            $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
            $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode.`n" } else { '' }
            $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                -Type 'Rejected' -Experiment $experiment `
                -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                -OutcomeLabel '❌ **Build failure**' `
                -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice
            Push-Location (Join-Path $repoRoot 'sample-api')
            $rejPrResult = New-ExperimentPR `
                -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                -Outcome 'regressed' -Description $analysisResult.Explanation `
                -Body $rejBody -IsDryRun:$DryRun
            if ($rejPrResult.Success) {
                $prNumber = $rejPrResult.PrNumber
                $prUrl = $rejPrResult.PrUrl
                $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = 'build_failure' }
            }
            Pop-Location

            Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'build_failure' -BranchName $branchName `
                -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { 'master' }) `
                -PrNumber $prNumber -PrUrl $prUrl `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
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
    Write-Status '[4/5] ✅ Verifying...'
    $testResult = & (Join-Path $PSScriptRoot 'Invoke-E2ETests.ps1') `
        -ConfigPath $ConfigPath -Experiment $experiment

    if (-not $testResult.Success) {
        if ($stackedDiffs) {
            Write-Warning "E2E tests failed at experiment $experiment — reverting and continuing"

            & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                -BranchName $branchName -FilePath $targetFile `
                -Experiment $experiment -Outcome 'regressed' `
                -RevertDescription 'E2E test failure' `
                -MetadataSummary "Test failure: $($analysisResult.Explanation)" `
                -MetadataFilePath $analysisResult.FilePath `
                -QueueItemId $queueItemId `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'test_failure' }
            $consecutiveFailures++
            $staleCount++

            # Create rejected PR for the record
            $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
            $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode.`n" } else { '' }
            $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                -Type 'Rejected' -Experiment $experiment `
                -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                -OutcomeLabel '❌ **E2E test failure**' `
                -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice
            Push-Location (Join-Path $repoRoot 'sample-api')
            $rejPrResult = New-ExperimentPR `
                -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                -Outcome 'regressed' -Description $analysisResult.Explanation `
                -Body $rejBody -IsDryRun:$DryRun
            if ($rejPrResult.Success) {
                $prNumber = $rejPrResult.PrNumber
                $prUrl = $rejPrResult.PrUrl
                $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = 'test_failure' }
            }
            Pop-Location

            Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'test_failure' -BranchName $branchName `
                -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { 'master' }) `
                -PrNumber $prNumber -PrUrl $prUrl `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
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
        Write-Status '  Measuring... [DRY RUN] Using synthetic metrics'

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

        Write-Status "  Synthetic p95: $($syntheticMetrics.HttpReqDuration.P95)ms (5% improvement over reference)"
    }
    else {
        Write-Status '  Measuring (k6 scale tests)...'

        # Reset database so every experiment starts with identical seed data
        & (Join-Path $PSScriptRoot 'Reset-Database.ps1') -ConfigPath $ConfigPath -Experiment $experiment

        $apiResult = & (Join-Path $PSScriptRoot 'Start-SampleApi.ps1') -ConfigPath $ConfigPath

        if (-not $apiResult.Success) {
            if ($stackedDiffs) {
                Write-Warning "API failed to start at experiment $experiment — reverting and continuing"

                & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                    -BranchName $branchName -FilePath $targetFile `
                    -Experiment $experiment -Outcome 'regressed' `
                    -RevertDescription 'API start failure' `
                    -MetadataSummary "API start failure: $($analysisResult.Explanation)" `
                    -MetadataFilePath $analysisResult.FilePath `
                    -QueueItemId $queueItemId `
                    -ConfigPath $ConfigPath

                $currentBranch = $branchName
                $branchChain += $branchName
                $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'api_start_failure' }
                $consecutiveFailures++
                $staleCount++

                # Create rejected PR for the record
                $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                    -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
                $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode.`n" } else { '' }
                $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                    -Type 'Rejected' -Experiment $experiment `
                    -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                    -OutcomeLabel '❌ **API failed to start**' `
                    -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice
                Push-Location (Join-Path $repoRoot 'sample-api')
                $rejPrResult = New-ExperimentPR `
                    -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                    -Outcome 'regressed' -Description $analysisResult.Explanation `
                    -Body $rejBody -IsDryRun:$DryRun
                if ($rejPrResult.Success) {
                    $prNumber = $rejPrResult.PrNumber
                    $prUrl = $rejPrResult.PrUrl
                    $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = 'api_start_failure' }
                }
                Pop-Location

                Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                    -Experiment $experiment -StartedAt $experimentStartedAt `
                    -Outcome 'api_start_failure' -BranchName $branchName `
                    -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { 'master' }) `
                    -PrNumber $prNumber -PrUrl $prUrl `
                    -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures

                if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                    $exitReason = 'max_consecutive_failures'
                    Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
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
                -ConfigPath $ConfigPath -Experiment $experiment -BaseUrl $apiResult.BaseUrl -SkipHealthCheck

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
                Write-Status '      Running additional scenarios...'
                $scenarioResults = & (Join-Path $PSScriptRoot 'Invoke-AllScaleTests.ps1') `
                    -ConfigPath $ConfigPath -Experiment $experiment -SkipPrimary -SkipHealthCheck -BaseUrl $apiResult.BaseUrl
            }
        }
        finally {
            & (Join-Path $PSScriptRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
        }

        # Handle scale test failure in stacked mode (after API is stopped)
        if ($stackedDiffs -and (-not $scaleResult.Success)) {
            & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                -BranchName $branchName -FilePath $targetFile `
                -Experiment $experiment -Outcome 'regressed' `
                -RevertDescription 'Scale test failure' `
                -MetadataSummary "Scale test failure: $($analysisResult.Explanation)" `
                -MetadataFilePath $analysisResult.FilePath `
                -QueueItemId $queueItemId `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'scale_test_failure' }
            $consecutiveFailures++
            $staleCount++

            # Create rejected PR for the record
            $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
            $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode.`n" } else { '' }
            $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                -Type 'Rejected' -Experiment $experiment `
                -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                -OutcomeLabel '❌ **Scale test failure**' `
                -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice
            Push-Location (Join-Path $repoRoot 'sample-api')
            $rejPrResult = New-ExperimentPR `
                -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                -Outcome 'regressed' -Description $analysisResult.Explanation `
                -Body $rejBody -IsDryRun:$DryRun
            if ($rejPrResult.Success) {
                $prNumber = $rejPrResult.PrNumber
                $prUrl = $rejPrResult.PrUrl
                $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = 'scale_test_failure' }
            }
            Pop-Location

            Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'scale_test_failure' -BranchName $branchName `
                -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { 'master' }) `
                -PrNumber $prNumber -PrUrl $prUrl `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
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
        Write-Status "  Runtime counters: $cpuInfo | $heapInfo | $gen2Info | $threadInfo"
    }

    # Track best experiment
    if ($currentMetrics.HttpReqDuration.P95 -lt $bestP95) {
        $bestP95 = $currentMetrics.HttpReqDuration.P95
        $bestExperiment = $experiment
    }

    # Compare post-fix metrics against reference
    Write-Status '  Comparing results...'

    $comparison = & (Join-Path $PSScriptRoot 'Compare-Results.ps1') `
        -CurrentMetrics $currentMetrics `
        -BaselineMetrics $baselineMetrics `
        -PreviousMetrics $previousMetrics `
        -CurrentCounterMetrics $currentCounterMetrics `
        -PreviousCounterMetrics $previousCounterMetrics `
        -RunMetrics $scaleResult.RunMetrics `
        -ConfigPath $ConfigPath `
        -Experiment $experiment

    # Reference metrics used for delta computation (previous improved or baseline)
    $referenceMetrics = if ($previousMetrics) { $previousMetrics } else { $baselineMetrics }
    $referenceLabel = if ($previousMetrics) { "Experiment $($experiment - 1)" } else { 'Baseline' }

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

            & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                -BranchName $branchName -FilePath $targetFile `
                -Experiment $experiment -Outcome 'regressed' `
                -RevertDescription (Limit-String $analysisResult.Explanation 120) `
                -SkipMetadataUpdate -SkipQueueMarkDone `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'regressed' }
            $consecutiveFailures++
            $staleCount++

            # Create rejected PR for the record (with metrics)
            $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
            $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode. Metrics are synthetic.`n" } else { '' }
            $d = $comparison.Deltas
            $rejMetrics = @"

### Performance Results (vs $referenceLabel)
| Metric | $referenceLabel | After Fix | Delta |
|--------|----------|-----------|-------|
| p95 Latency | $($referenceMetrics.HttpReqDuration.P95)ms | $($currentMetrics.HttpReqDuration.P95)ms | $($d.P95Latency.ChangePct)% |
| Requests/sec | $([math]::Round($referenceMetrics.HttpReqs.Rate, 1)) | $([math]::Round($currentMetrics.HttpReqs.Rate, 1)) | $($d.RPS.ChangePct)% |
| Error Rate | $([math]::Round($referenceMetrics.HttpReqFailed.Rate * 100, 2))% | $([math]::Round($currentMetrics.HttpReqFailed.Rate * 100, 2))% | $($d.ErrorRate.ChangePct)% |

"@
            $rejRcaSection = ''
            if ($rcaDocument) {
                $rejRcaSection = @"

### Root Cause Analysis

<details>
<summary>Click to expand full analysis</summary>

$rcaDocument

</details>

"@
            }
            $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                -Type 'Rejected' -Experiment $experiment `
                -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                -OutcomeLabel "🔴 **Regression detected**" `
                -OutcomeDetail $comparison.RegressionDetail `
                -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice `
                -MetricsSection $rejMetrics -RcaSection $rejRcaSection
            Push-Location (Join-Path $repoRoot 'sample-api')
            $rejPrResult = New-ExperimentPR `
                -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                -Outcome 'regressed' -Description $analysisResult.Explanation `
                -Body $rejBody -IsDryRun:$DryRun
            if ($rejPrResult.Success) {
                $prNumber = $rejPrResult.PrNumber
                $prUrl = $rejPrResult.PrUrl
                $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = 'regressed' }
            }
            Pop-Location

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
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
            Write-Status '  ↑ Efficiency improvement (tiebreaker) — publishing'
        }
        else {
            Write-Status '  ↑ Improvement detected — publishing'
        }

        Write-Status '[5/5] 📦 Publishing (push + PR)...'

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

        # Amend the commit to include post-fix artifacts
        Push-Location (Join-Path $repoRoot 'sample-api')
        & (Join-Path $PSScriptRoot 'Stage-ExperimentArtifacts.ps1') `
            -Experiment $experiment -SubmoduleDir (Join-Path $repoRoot 'sample-api')
        git commit --amend --no-edit 2>&1 | Out-Null

        # Push and create PR
        git push -u origin $branchName 2>&1 | Out-Null

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'info' `
            -Message "Branch pushed to origin: $branchName" `
            -Experiment $experiment

        # Determine PR base: stacked mode uses baseBranch (previous experiment); legacy uses master
        $prBaseBranch = if ($stackedDiffs) { $baseBranch } else { 'master' }

        # Build PR body with stack context
        $stackNote = ''
        if ($stackedDiffs -and $prChain.Count -gt 0) {
            $stackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                -Experiment $experiment -OutcomeTag '[ACCEPTED]' -BaseBranch $prBaseBranch
        }

        # Build per-scenario breakdown table from diagnostic scenario results
        $scenarioBreakdown = ''
        if ($scenarioResults -and $scenarioResults.Count -gt 0) {
            $scenarioRows = @()
            foreach ($sr in $scenarioResults) {
                if (-not $sr.Metrics) { continue }

                # Use previous experiment's scenario results if available, otherwise fall back to baseline
                $scenarioRef = $null
                $scenarioRefLabel = 'Baseline'
                if ($previousScenarioResults -and $previousScenarioResults.ContainsKey($sr.ScenarioName)) {
                    $scenarioRef = $previousScenarioResults[$sr.ScenarioName]
                    $scenarioRefLabel = $referenceLabel
                }
                else {
                    $scenarioBaselinePath = Join-Path $repoRoot $config.Api.ResultsPath "baseline-$($sr.ScenarioName).json"
                    if (-not (Test-Path $scenarioBaselinePath)) { continue }
                    $scenarioRef = Get-Content $scenarioBaselinePath -Raw | ConvertFrom-Json
                }

                $sbP95 = $scenarioRef.HttpReqDuration.P95
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

### Per-Scenario Breakdown (vs $scenarioRefLabel)
| Scenario | $scenarioRefLabel p95 | Current p95 | Delta |
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

        $accMetrics = @"

### Performance Results (vs $referenceLabel)
| Metric | $referenceLabel | After Fix | Delta |
|--------|----------|-----------|-------|
| p95 Latency | $($referenceMetrics.HttpReqDuration.P95)ms | $($currentMetrics.HttpReqDuration.P95)ms | $($d.P95Latency.ChangePct)% |
| Requests/sec | $([math]::Round($referenceMetrics.HttpReqs.Rate, 1)) | $([math]::Round($currentMetrics.HttpReqs.Rate, 1)) | $($d.RPS.ChangePct)% |
| Error Rate | $([math]::Round($referenceMetrics.HttpReqFailed.Rate * 100, 2))% | $([math]::Round($currentMetrics.HttpReqFailed.Rate * 100, 2))% | $($d.ErrorRate.ChangePct)% |

"@
        $prBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
            -Type 'Accepted' -Experiment $experiment `
            -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
            -StackNote $stackNote -DryRunNotice $dryRunNotice `
            -MetricsSection $accMetrics -RcaSection $rcaSection `
            -ImprovementPct "$($comparison.ImprovementPct)" `
            -ScenarioBreakdown $scenarioBreakdown

        $prResult = New-ExperimentPR `
            -Experiment $experiment -BranchName $branchName -BaseBranch $prBaseBranch `
            -Outcome 'improved' -Description $analysisResult.Explanation `
            -Body $prBody -IsDryRun:$DryRun

        $prNumber = $null
        $prUrl = $null
        if ($prResult.Success) {
            $prNumber = $prResult.PrNumber
            $prUrl = $prResult.PrUrl
        }

        # Update stacked-diffs state
        $lastSuccessfulBranch = $branchName
        $currentBranch = $branchName
        $branchChain += $branchName
        if ($prNumber) {
            $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = 'improved' }
        }

        # Wait for PR merge if configured (legacy mode or explicit opt-in)
        $shouldWait = $waitForMerge -and $prNumber -and ($experiment -lt $loopEnd)
        if ($shouldWait) {
            Write-Status ''
            Write-Status '  ⏳ Waiting for PR to be reviewed and merged...'
            Write-Status '     Merge the PR in GitHub to continue the optimization loop.'

            $pollInterval = 30
            $lastLog = [datetime]::MinValue
            $logEvery = [timespan]::FromMinutes(5)

            while ($true) {
                Start-Sleep -Seconds $pollInterval
                $prState = gh pr view $prNumber --json state,mergedAt 2>$null | ConvertFrom-Json
                if ($prState.state -eq 'MERGED') {
                    Write-Status "  ✓ PR #$prNumber merged — continuing loop"

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
                        Write-Status "  … Still waiting for PR #$prNumber to be merged ($(Get-Date -Format 'HH:mm'))"
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
            Write-Status "  ─ No improvement (stale — failure $consecutiveFailures / $maxConsecutiveFailures)"

            & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                -BranchName $branchName -FilePath $targetFile `
                -Experiment $experiment -Outcome 'stale' `
                -RevertDescription (Limit-String $analysisResult.Explanation 120) `
                -SkipMetadataUpdate -SkipQueueMarkDone `
                -ConfigPath $ConfigPath

            $currentBranch = $branchName
            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = 'stale' }

            # Create rejected PR for the record (with metrics)
            $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
            $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode. Metrics are synthetic.`n" } else { '' }
            $d = $comparison.Deltas
            $rejMetrics = @"

### Performance Results (vs $referenceLabel)
| Metric | $referenceLabel | After Fix | Delta |
|--------|----------|-----------|-------|
| p95 Latency | $($referenceMetrics.HttpReqDuration.P95)ms | $($currentMetrics.HttpReqDuration.P95)ms | $($d.P95Latency.ChangePct)% |
| Requests/sec | $([math]::Round($referenceMetrics.HttpReqs.Rate, 1)) | $([math]::Round($currentMetrics.HttpReqs.Rate, 1)) | $($d.RPS.ChangePct)% |
| Error Rate | $([math]::Round($referenceMetrics.HttpReqFailed.Rate * 100, 2))% | $([math]::Round($currentMetrics.HttpReqFailed.Rate * 100, 2))% | $($d.ErrorRate.ChangePct)% |

"@
            $rejRcaSection = ''
            if ($rcaDocument) {
                $rejRcaSection = @"

### Root Cause Analysis

<details>
<summary>Click to expand full analysis</summary>

$rcaDocument

</details>

"@
            }
            $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                -Type 'Rejected' -Experiment $experiment `
                -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                -OutcomeLabel "⚪ **No improvement detected**" `
                -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice `
                -MetricsSection $rejMetrics -RcaSection $rejRcaSection
            Push-Location (Join-Path $repoRoot 'sample-api')
            $rejPrResult = New-ExperimentPR `
                -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                -Outcome 'stale' -Description $analysisResult.Explanation `
                -Body $rejBody -IsDryRun:$DryRun
            if ($rejPrResult.Success) {
                $prNumber = $rejPrResult.PrNumber
                $prUrl = $rejPrResult.PrUrl
                $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = 'stale' }
            }
            Pop-Location

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
                break
            }
        }
        else {
            Write-Status "  ─ No improvement (stale $staleCount / $($tolerances.StaleExperimentsBeforeStop))"

            Undo-ExperimentBranch -BranchName $branchName -RepoRoot $repoRoot -RestoreBranch $currentBranch

            if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop) {
                $exitReason = 'no_improvement'
                Write-Status '  Stopping: no improvement for consecutive experiments'
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
        if ($scenarioResults -and $scenarioResults.Count -gt 0) {
            $previousScenarioResults = @{}
            foreach ($sr in $scenarioResults) {
                if ($sr.Metrics) { $previousScenarioResults[$sr.ScenarioName] = $sr.Metrics }
            }
        }
    }
    $previousRcaExplanation = if ($analysisResult) { $analysisResult.Explanation } else { '' }

    # ── Record experiment metadata ──────────────────────────────────────────────
    $experimentPrNumber = if ($prNumber) { $prNumber } else { $null }
    $experimentPrUrl = if ($prUrl) { "$prUrl" } else { $null }

    Add-ExperimentMetadata -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
        -Experiment $experiment -StartedAt $experimentStartedAt `
        -Outcome $experimentOutcome -BranchName $branchName `
        -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { 'master' }) `
        -Metrics $currentMetrics `
        -Improved $comparison.Improved -Regression $comparison.Regression `
        -PrNumber $experimentPrNumber -PrUrl $experimentPrUrl `
        -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Status ''
Write-Status '══════════════════════════════════════════════════════════════'
Write-Status '  HONE COMPLETE'
Write-Status '══════════════════════════════════════════════════════════════'
Write-Status ''
Write-Status "  Exit reason:     $exitReason"
Write-Status "  Experiments run:  $experimentsRun"
Write-Status "  Successful:      $successCount / $experimentsRun"
Write-Status "  Best p95:        ${bestP95}ms (experiment $bestExperiment)"
Write-Status "  Baseline p95:    $($baselineMetrics.HttpReqDuration.P95)ms"

$totalImprovement = if ($baselineMetrics.HttpReqDuration.P95 -gt 0) {
    [math]::Round((($baselineMetrics.HttpReqDuration.P95 - $bestP95) / $baselineMetrics.HttpReqDuration.P95) * 100, 1)
} else { 0 }
Write-Status "  Total improvement: ${totalImprovement}% (p95 vs baseline)"

if ($stackedDiffs) {
    Write-Status ''

    # Branch chain display
    $chainDisplay = ($branchChain | ForEach-Object {
        $branch = $_
        $failed = $failedExperiments | Where-Object {
            "$($config.Loop.BranchPrefix)-$($_.Experiment)" -eq $branch
        }
        if ($failed) { "$branch ✗" } else { "$branch ✓" }
    }) -join ' → '
    Write-Status "  Branch chain:"
    Write-Status "    $chainDisplay"

    # PR stack display
    if ($prChain.Count -gt 0) {
        $prDisplay = ($prChain | ForEach-Object {
            $tag = if ($_.Outcome -eq 'improved') { '✓' } else { '✗' }
            "PR #$($_.Number) (experiment-$($_.Experiment)) $tag"
        }) -join ' → '
        Write-Status ''
        Write-Status "  PR stack (reviewable):"
        Write-Status "    $prDisplay"
    }

    # Failed experiments display
    if ($failedExperiments.Count -gt 0) {
        $failDisplay = ($failedExperiments | ForEach-Object { "$($_.Experiment) ($($_.Reason))" }) -join ', '
        Write-Status ''
        Write-Status "  Failed experiments: $failDisplay"
    }
}

Write-Status ''

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' `
    -Message "Hone loop complete: $exitReason after $experimentsRun experiments" `
    -Data @{
        exitReason    = $exitReason
        experiments    = $experimentsRun
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
    Experiments        = $experimentsRun
    SuccessCount      = $successCount
    BestP95           = $bestP95
    BestExperiment     = $bestExperiment
    BaselineP95       = $baselineMetrics.HttpReqDuration.P95
    PrChain           = @($prChain | ForEach-Object { $_.Number })
    FullBranchChain   = $branchChain
    FailedExperiments  = @($failedExperiments | ForEach-Object { $_.Experiment })
}
