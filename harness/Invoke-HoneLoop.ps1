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
    - Legacy: each experiment branches from the default branch, PRs target the default branch,
      loop blocks waiting for merge between experiments.

.PARAMETER MaxExperiments
    Override max experiments from config.

.PARAMETER TargetPath
    Path to the target project directory containing .hone/config.psd1.

.EXAMPLE
    .\Invoke-HoneLoop.ps1 -TargetPath ..\my-api

.PARAMETER DryRun
    Skip slow operations (k6 scale tests, API start/stop, DB reset) and use
    synthetic metrics. AI agents, build, and E2E tests still run normally.
    PRs are created with a [DRY RUN] prefix.

.EXAMPLE
    .\Invoke-HoneLoop.ps1 -TargetPath ..\my-api -MaxExperiments 10

.EXAMPLE
    .\Invoke-HoneLoop.ps1 -TargetPath ..\my-api -DryRun -MaxExperiments 3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TargetPath,
    [int]$MaxExperiments,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

# Resolve target directory
$resolvedTargetPath = Resolve-Path -Path $TargetPath -ErrorAction SilentlyContinue
if ($resolvedTargetPath) {
    $targetDir = $resolvedTargetPath.Path
} else {
    $targetDir = [System.IO.Path]::GetFullPath($TargetPath, (Get-Location).Path)
}
$honeDir = Join-Path $targetDir '.hone'
if (-not (Test-Path (Join-Path $honeDir 'config.psd1'))) {
    throw "Target directory '$targetDir' does not contain .hone/config.psd1"
}

# Load and merge config
$harnessRoot = $PSScriptRoot
$engineConfig = Get-HoneConfig -ConfigPath (Join-Path $harnessRoot 'config.psd1')
$targetConfig = Import-PowerShellDataFile (Join-Path $honeDir 'config.psd1')
$config = Merge-HoneConfig -Engine $engineConfig -Target $targetConfig
$fixture = Get-HarnessTestingFixture -Config $config -TargetDir $targetDir
$fixtureLoop = if ($fixture) {
    Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Loop') -Experiment 0
} else {
    $null
}
$fixtureSkipExternalChecks = [bool]($fixtureLoop -and $fixtureLoop.ContainsKey('SkipExternalPrerequisites') -and $fixtureLoop.SkipExternalPrerequisites)
$fixtureSkipBranchCheckout = [bool]($fixtureLoop -and $fixtureLoop.ContainsKey('SkipInitialBranchCheckout') -and $fixtureLoop.SkipInitialBranchCheckout)
$fixtureMachineInfo = if ($fixtureLoop -and $fixtureLoop.ContainsKey('MachineInfo')) { [PSCustomObject]$fixtureLoop.MachineInfo } else { $null }

if ($fixture) {
    $env:HONE_HARNESS_TEST_TARGET_DIR = $targetDir
}

# Validate merged config
$null = & (Join-Path $PSScriptRoot 'Test-HoneConfig.ps1') -ConfigPath (Join-Path $harnessRoot 'config.psd1') -TargetPath $targetDir

# Apply CLI overrides
if ($PSBoundParameters.ContainsKey('MaxExperiments')) {
    if (-not $config.ContainsKey('Loop')) { $config.Loop = @{} }
    $config.Loop.MaxExperiments = $MaxExperiments
}

$targetName = $targetConfig.Name

# Set the log path for Write-HoneLog.ps1 (which runs as a separate script
# and cannot see the merged config). Uses an env var for cross-script comms.
$env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath $config.Api.ResultsPath 'hone.jsonl'

# Sub-scripts accept -ConfigPath (engine config file path) alongside
# -TargetDir/-TargetConfig for target-specific settings.
$configPath = Join-Path $harnessRoot 'config.psd1'

$maxExp = $config.Loop.MaxExperiments
$tolerances = $config.Tolerances
$stackedDiffs = if ($config.Loop.ContainsKey('StackedDiffs')) { $config.Loop.StackedDiffs } else { $false }
$waitForMerge = if ($config.Loop.ContainsKey('WaitForMerge')) { $config.Loop.WaitForMerge } else { $true }
$skipClassification = if ($config.Loop.ContainsKey('SkipClassification')) { $config.Loop.SkipClassification } else { $false }
$maxConsecutiveFailures = if ($tolerances.ContainsKey('MaxConsecutiveFailures')) {
    $tolerances.MaxConsecutiveFailures
} else {
    $tolerances.StaleExperimentsBeforeStop
}
$defaultBranch = if ($config.ContainsKey('BaseBranch') -and $config.BaseBranch) { $config.BaseBranch } else { 'master' }

function Get-FixPhaseIterationSummary {
    param([PSCustomObject]$IterativeResult)

    if (-not $IterativeResult -or -not $IterativeResult.IterationLog) {
        return ''
    }

    $attempts = @($IterativeResult.IterationLog.attempts)
    if ($attempts.Count -eq 0) {
        return ''
    }

    if ($IterativeResult.Success -and $attempts.Count -le 1) {
        return ''
    }

    $attemptWord = if ($IterativeResult.AttemptCount -eq 1) { 'attempt' } else { 'attempts' }
    $summaryLead = if ($IterativeResult.Success) {
        "Fix reached build + test success after $($IterativeResult.AttemptCount) $attemptWord."
    } else {
        "Fix stopped after $($IterativeResult.AttemptCount) $attemptWord with outcome ``$($IterativeResult.ExitReason)``."
    }

    $summaryExtras = @()
    if ($IterativeResult.IterationLogRelativePath) {
        $summaryExtras += "> Iteration log: ``$($IterativeResult.IterationLogRelativePath)``"
    }

    if (-not $IterativeResult.Success -and $IterativeResult.FailureDetail) {
        $summaryExtras += "> Final failure: $($IterativeResult.FailureDetail)"
    }

    $rows = foreach ($attempt in $attempts) {
        $diffLines = if ($null -ne $attempt.diffLines) { $attempt.diffLines } else { '-' }
        "| $($attempt.attempt) | $($attempt.stage) | $($attempt.outcome) | $diffLines |"
    }

    $extrasBlock = if ($summaryExtras.Count -gt 0) {
        ($summaryExtras -join "`n") + "`n"
    } else {
        ''
    }

    return @"

### Iterative Fix Summary
$summaryLead
$extrasBlock
| Attempt | Stage | Outcome | Diff lines |
|--------|-------|---------|------------|
$($rows -join "`n")

"@
}

function Add-FixPhaseSummaryToRca {
    param(
        [string]$RcaPath,
        [string]$IterationSummary
    )

    if (-not $RcaPath -or -not $IterationSummary -or -not (Test-Path $RcaPath)) {
        return
    }

    $existing = Get-Content -Path $RcaPath -Raw
    if ($existing -match '### Iterative Fix Summary') {
        return
    }

    ($existing.TrimEnd() + "`r`n`r`n" + $IterationSummary.Trim() + "`r`n") | Out-File -FilePath $RcaPath -Encoding utf8
}

function Get-FixPhaseMetadataPropertyMap {
    param([PSCustomObject]$IterativeResult)

    if (-not $IterativeResult) {
        return $null
    }

    $props = [ordered]@{
        FixAttemptCount = $IterativeResult.AttemptCount
        FixFinalAttempt = $IterativeResult.Attempt
        FixRetried = [bool]($IterativeResult.AttemptCount -gt 1)
    }

    if ($IterativeResult.IterationLogRelativePath) {
        $props.FixIterationLogPath = $IterativeResult.IterationLogRelativePath
    }

    if ($IterativeResult.CommitSha) {
        $props.FixCommitSha = $IterativeResult.CommitSha
    }

    if (-not $IterativeResult.Success -and $IterativeResult.LastFailureStage) {
        $props.FixFailureStage = $IterativeResult.LastFailureStage
    }

    if (-not $IterativeResult.Success -and $IterativeResult.ExitReason) {
        $props.FixExitReason = $IterativeResult.ExitReason
    }

    return $props
}

function Get-FixPhaseFailurePresentation {
    param([PSCustomObject]$IterativeResult)

    switch ($IterativeResult.ExitReason) {
        'build_failure' {
            return [PSCustomObject]@{
                Label = '❌ **Build failure**'
                RevertDescription = 'Build failure'
                MetadataPrefix = 'Build failure'
                Detail = if ($IterativeResult.FailureDetail) { $IterativeResult.FailureDetail } else { 'Build failed during the fix phase.' }
            }
        }

        'test_failure' {
            return [PSCustomObject]@{
                Label = '❌ **E2E test failure**'
                RevertDescription = 'E2E test failure'
                MetadataPrefix = 'Test failure'
                Detail = if ($IterativeResult.FailureDetail) { $IterativeResult.FailureDetail } else { 'E2E tests failed during the fix phase.' }
            }
        }

        'retry_budget_exhausted' {
            $detail = "The fixer exhausted its retry budget."
            if ($IterativeResult.AttemptCount -gt 0) {
                $attemptWord = if ($IterativeResult.AttemptCount -eq 1) { 'attempt' } else { 'attempts' }
                $detail = "The fixer exhausted its retry budget after $($IterativeResult.AttemptCount) $attemptWord."
            }

            if ($IterativeResult.LastFailureStage) {
                $detail += " Final failure stage: $($IterativeResult.LastFailureStage)."
            }

            if ($IterativeResult.FailureDetail) {
                $detail += "`n`n$($IterativeResult.FailureDetail)"
            }

            return [PSCustomObject]@{
                Label = '♻️ **Retry budget exhausted**'
                RevertDescription = 'Retry budget exhausted'
                MetadataPrefix = 'Retry budget exhausted'
                Detail = $detail
            }
        }

        default {
            return [PSCustomObject]@{
                Label = '❌ **Fix phase failure**'
                RevertDescription = 'Fix phase failure'
                MetadataPrefix = 'Fix phase failure'
                Detail = if ($IterativeResult.FailureDetail) { $IterativeResult.FailureDetail } else { 'The fix phase failed.' }
            }
        }
    }
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
$modeLabel = if ($stackedDiffs) { 'stacked diffs (linear chain)' } else { "legacy (each off $defaultBranch)" }
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
if (-not $fixtureSkipExternalChecks -and -not (Get-Command 'k6' -ErrorAction SilentlyContinue)) {
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
if (-not $fixtureSkipExternalChecks -and -not (Get-Command 'gh' -ErrorAction SilentlyContinue)) {
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
if (-not $fixtureSkipExternalChecks -and -not (Get-Command 'copilot' -ErrorAction SilentlyContinue)) {
    Write-Error 'copilot CLI is not on PATH — install the GitHub Copilot CLI (https://docs.github.com/copilot/how-tos/copilot-cli) before running the optimization loop'
    return
}

# ── Ensure GH_TOKEN is set and valid ─────────────────────────────────────────
if (-not $fixtureSkipExternalChecks -and -not $env:GH_TOKEN) {
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
if (-not $fixtureSkipExternalChecks) {
    $ghStatus = gh auth status 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "gh token is invalid or expired — run 'gh auth login' to refresh.`n$ghStatus"
        return
    }
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message "Hone loop starting (max $maxExp experiments)"

# ── Collect machine info ────────────────────────────────────────────────────
$machineInfo = if ($fixtureMachineInfo) {
    $fixtureMachineInfo
} else {
    & (Join-Path $PSScriptRoot 'Get-MachineInfo.ps1')
}
Write-Status "  CPU:     $($machineInfo.Cpu.Name) ($($machineInfo.Cpu.LogicalProcessors) logical cores)"
Write-Status "  RAM:     $($machineInfo.Memory.TotalGB)GB"
Write-Status "  OS:      $($machineInfo.OS.Description)"
Write-Status ''

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message 'Machine info collected' `
    -Data @{
    cpuName = $machineInfo.Cpu.Name
    logicalCores = $machineInfo.Cpu.LogicalProcessors
    physicalCores = $machineInfo.Cpu.PhysicalCores
    totalMemoryGB = $machineInfo.Memory.TotalGB
    os = $machineInfo.OS.Description
}

# ── Run metadata tracking ───────────────────────────────────────────────────
$runMetadataPath = Join-Path -Path $targetDir -ChildPath $config.Api.ResultsPath 'run-metadata.json'
$loopStartedAt = Get-Date -Format 'o'

if (Test-Path $runMetadataPath) {
    $runMetadata = Get-Content $runMetadataPath -Raw | ConvertFrom-Json
    # Overwrite machine info with current (machine may differ between baseline & loop)
    $runMetadata | Add-Member -NotePropertyName Machine -NotePropertyValue $machineInfo -Force
    $runMetadata | Add-Member -NotePropertyName LoopStartedAt -NotePropertyValue $loopStartedAt -Force
} else {
    $runMetadata = [PSCustomObject][ordered]@{
        Machine = $machineInfo
        BaselineRun = $null
        LoopStartedAt = $loopStartedAt
        LoopCompletedAt = $null
        Experiments = @()
    }
}

# ── Load baseline ───────────────────────────────────────────────────────────
$baselinePath = Join-Path -Path $targetDir -ChildPath $config.Api.ResultsPath 'baseline.json'

if (-not (Test-Path $baselinePath)) {
    Write-Status 'No baseline found. Running Get-PerformanceBaseline.ps1 first...'
    $null = & (Join-Path $PSScriptRoot 'Get-PerformanceBaseline.ps1') -ConfigPath (Join-Path $harnessRoot 'config.psd1') `
        -TargetDir $targetDir -TargetConfig $targetConfig

    if (-not (Test-Path $baselinePath)) {
        Write-Error 'Failed to establish baseline. Aborting.'
        return
    }
}

$baselineMetrics = Get-Content $baselinePath -Raw | ConvertFrom-Json
Write-Status "Baseline loaded: p95=$($baselineMetrics.HttpReqDuration.P95)ms"

# ── Experiment Loop ──────────────────────────────────────────────────────────
$previousMetrics = $null
$previousMetricsExperiment = 0
$previousCounterMetrics = $null
$previousScenarioResults = $null
$previousRcaExplanation = ''
$exitReason = 'max_experiments'
$bestExperiment = 0
$bestP95 = $baselineMetrics.HttpReqDuration.P95
$staleCount = 0
$consecutiveFailures = 0

# Stacked-diffs state: track the branch chain and PR stack
$currentBranch = $defaultBranch
$prChain = @()
$branchChain = @($defaultBranch)
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

    # Restore reference metrics from the last accepted experiment so the first
    # post-resume comparison is against the correct reference (not baseline).
    $lastImproved = $priorExperiments | Where-Object { $_.Outcome -eq 'improved' } | Select-Object -Last 1
    if ($lastImproved) {
        $resultsBase = Join-Path -Path $targetDir -ChildPath $config.Api.ResultsPath
        $lastExpDir = Join-Path $resultsBase "experiment-$($lastImproved.Experiment)"
        $lastSummaryPath = Join-Path $lastExpDir 'k6-summary.json'
        if (Test-Path $lastSummaryPath) {
            $previousMetrics = Get-Content $lastSummaryPath -Raw | ConvertFrom-Json
            $previousMetricsExperiment = $lastImproved.Experiment

            # Restore per-scenario results if available
            $scenarioFiles = Get-ChildItem $lastExpDir -Filter 'k6-summary-*.json' -ErrorAction SilentlyContinue
            if ($scenarioFiles) {
                $previousScenarioResults = @{}
                foreach ($sf in $scenarioFiles) {
                    $scenarioName = $sf.BaseName -replace '^k6-summary-', ''
                    $previousScenarioResults[$scenarioName] = Get-Content $sf.FullName -Raw | ConvertFrom-Json
                }
            }

            Write-Status "  Reference metrics:  experiment $($lastImproved.Experiment) (p95=$($previousMetrics.HttpReqDuration.P95)ms)"
        } else {
            Write-Status "  Reference metrics:  baseline (experiment $($lastImproved.Experiment) summary not found)"
        }
    }

    # Restore stacked-diffs branch state
    if ($stackedDiffs) {
        # Failed experiments preserve their own branches for review, but the next
        # successful experiment must still branch from the last successful tip.
        $lastSuccessfulBranch = $priorExperiments |
            Where-Object { $_.BranchName -and $_.Outcome -eq 'improved' } |
            Select-Object -Last 1
        $currentBranch = if ($lastSuccessfulBranch) { $lastSuccessfulBranch.BranchName } else { $defaultBranch }
        $branchChain = @($defaultBranch) + @($priorExperiments | Where-Object { $_.BranchName } | ForEach-Object { $_.BranchName })
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
        startExperiment = $startExperiment
        priorCount = $priorExperiments.Count
        successCount = $successCount
        staleCount = $staleCount
        consecutiveFailures = $consecutiveFailures
        currentBranch = $currentBranch
        referenceExperiment = $previousMetricsExperiment
    }
}

# MaxExperiments means "run N more" — compute absolute loop end
$loopEnd = $startExperiment + $maxExp - 1

# Ensure the target starts on the correct branch for experiment forking
if (-not $fixtureSkipBranchCheckout) {
    Push-Location $targetDir
    git checkout $currentBranch 2>&1 | Out-Null
    Pop-Location
}

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
    $refLabel = if ($previousMetrics) { "experiment $previousMetricsExperiment" } else { 'baseline' }
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
        -ConfigPath $configPath `
        -Experiment $experiment

    $hasQueue = & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
        -Action 'HasActionable' -ConfigPath $configPath -TargetDir $targetDir

    if (-not $hasQueue) {
        Write-Status '[2/5] 🧠 Analyzing (queue empty — running analysis)...'

        # ── Diagnostic profiling (runs only when analysis is needed) ─────
        $diagnosticReports = $null
        if ($config.ContainsKey('Diagnostics') -and $config.Diagnostics.Enabled -and -not $DryRun) {
            Write-Status '  Running diagnostic measurement (PerfView profiling)...'
            $diagResult = & (Join-Path $PSScriptRoot 'Invoke-DiagnosticMeasurement.ps1') `
                -Experiment $experiment `
                -ConfigPath $configPath `
                -CurrentMetrics $metricsForAnalysis `
                -TargetDir $targetDir -TargetConfig $targetConfig

            if ($diagResult.Success) {
                $diagnosticReports = $diagResult.AnalyzerReports
                Write-Status "  Diagnostic profiling complete: $($diagnosticReports.Count) report(s)"
            } else {
                throw 'Diagnostic measurement failed. Fix PerfView/profiling setup or set Diagnostics.Enabled = $false.'
            }
        } elseif ($DryRun) {
            Write-Status '  Diagnostic profiling skipped (dry run)'
        }

        $rawAnalysis = & (Join-Path $PSScriptRoot 'Invoke-AnalysisAgent.ps1') `
            -CurrentMetrics $metricsForAnalysis `
            -BaselineMetrics $baselineMetrics `
            -ComparisonResult $comparisonForAnalysis `
            -CounterMetrics $countersForAnalysis `
            -Experiment $experiment `
            -ConfigPath $configPath `
            -PreviousRcaExplanation $previousRcaExplanation `
            -DiagnosticReports $diagnosticReports `
            -TargetDir $targetDir

        if (-not $rawAnalysis.Success) {
            Write-Warning '  Analysis agent failed — skipping experiment'
            $staleCount++
            if ($stackedDiffs) { $consecutiveFailures++ }
            Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'analysis_failed' `
                -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { $defaultBranch }) `
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
            -ConfigPath $configPath -TargetDir $targetDir

        if (-not $initResult.Success) {
            Write-Warning '  Failed to initialize optimization queue — skipping experiment'
            $staleCount++
            if ($stackedDiffs) { $consecutiveFailures++ }
            Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'queue_init_failed' `
                -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { $defaultBranch }) `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures
            continue
        }

        Write-Status "  Queue initialized with $($initResult.Count) items"
    } else {
        Write-Status '[2/5] 🧠 Analyzing... (queue has items — skipping analysis)'
    }

    # Pick the next actionable item from the queue.
    # If an item is classified as architecture, skip it and try the next one
    # rather than wasting the entire experiment iteration.
    $currentItem = $null
    $classificationResult = $null
    $changeScope = $null
    $queueItemId = $null

    while ($true) {
        $currentItem = & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'GetNext' -Experiment $experiment -ConfigPath $configPath -TargetDir $targetDir

        if (-not $currentItem) { break }

        $queueItemId = $currentItem.id
        Write-Status "  Queue item #$($currentItem.id): $($currentItem.filePath)"

        # ── Classification ────────────────────────────────────────────────
        if ($skipClassification) {
            $classificationResult = [PSCustomObject]@{
                Scope = 'narrow'
                Reasoning = 'Classification skipped (SkipClassification = $true)'
            }
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'analyze' -Level 'info' `
                -Message "Classification skipped — treating as narrow: $($currentItem.filePath)" `
                -Experiment $experiment
        } else {
            $classificationResult = & (Join-Path $PSScriptRoot 'Invoke-ClassificationAgent.ps1') `
                -FilePath $currentItem.filePath `
                -Explanation $currentItem.explanation `
                -Experiment $experiment `
                -ConfigPath $configPath `
                -TargetName $targetName `
                -TargetDir $targetDir
        }

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
            -ConfigPath $configPath -TargetDir $targetDir

        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Experiment $experiment `
            -Summary $currentItem.explanation `
            -FilePath $currentItem.filePath `
            -Outcome 'queued' `
            -ConfigPath $configPath -TargetDir $targetDir

        $currentItem = $null
    }

    if (-not $currentItem) {
        Write-Warning '  No actionable items in queue — skipping experiment'
        $staleCount++
        if ($stackedDiffs) { $consecutiveFailures++ }
        Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
            -Experiment $experiment -StartedAt $experimentStartedAt `
            -Outcome 'no_queue_items' `
            -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { $defaultBranch }) `
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
        Success = $true
        FilePath = $currentItem.filePath
        Explanation = $currentItem.explanation
        ResponsePath = $null
    }

    $rcaResult = $null
    $branchName = "$($config.Loop.BranchPrefix)-$experiment"
    $rcaDocument = $null

    # Save root-cause analysis to experiment directory.
    # If the analyst provided a rich root-cause document, copy it (with metrics header).
    # Otherwise, fall back to the harness-generated RCA template.
    if ($currentItem -and $currentItem.rootCausePath -and (Test-Path $currentItem.rootCausePath)) {
        $iterDir = Join-Path -Path $targetDir -ChildPath $config.Api.ResultsPath "experiment-$experiment"
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
        $rcaDocument = Get-Content $rcaPath -Raw
        Write-Status "  Root cause analysis saved to: $rcaPath"
    } else {
        $rcaResult = & (Join-Path $PSScriptRoot 'Export-ExperimentRCA.ps1') `
            -FilePath $analysisResult.FilePath `
            -Explanation $analysisResult.Explanation `
            -ChangeScope $changeScope `
            -ScopeReasoning $classificationResult.Reasoning `
            -CurrentMetrics $metricsForAnalysis `
            -BaselineMetrics $baselineMetrics `
            -ComparisonResult $comparisonForAnalysis `
            -Experiment $experiment `
            -ConfigPath $configPath `
            -TargetDir $targetDir

        if ($rcaResult.Success) {
            $rcaDocument = Get-Content $rcaResult.Path -Raw
            Write-Status "  Root cause analysis saved to: $($rcaResult.Path)"
        }
    }

    # ── Phase 3: Experiment (fix + build) ──────────────────────────────────
    Write-Status '[3/5] 🧪 Experimenting (fix + build)...'

    # Determine the base branch for this experiment
    $baseBranch = if ($stackedDiffs) { $currentBranch } else { $defaultBranch }
    $iterativeResult = & (Join-Path $PSScriptRoot 'Invoke-IterativeFix.ps1') `
        -FilePath $analysisResult.FilePath `
        -Explanation $analysisResult.Explanation `
        -RootCauseDocument $rcaDocument `
        -Experiment $experiment `
        -BaseBranch $baseBranch `
        -ConfigPath $configPath `
        -TargetName $targetName `
        -TargetDir $targetDir

    $targetFile = $iterativeResult.TargetFile
    $branchName = if ($iterativeResult.BranchName) { $iterativeResult.BranchName } else { $branchName }
    $iterationSummary = Get-FixPhaseIterationSummary -IterativeResult $iterativeResult
    $iterativeMetadataProperties = Get-FixPhaseMetadataPropertyMap -IterativeResult $iterativeResult

    if ($iterationSummary -and $rcaResult -and $rcaResult.Success) {
        Add-FixPhaseSummaryToRca -RcaPath $rcaResult.Path -IterationSummary $iterationSummary
    }

    if (-not $iterativeResult.Success) {
        $iterativeExitReason = $iterativeResult.ExitReason

        if ($iterativeExitReason -eq 'fix_failed') {
            Write-Warning '  Fix agent failed to generate code — skipping experiment'
            $staleCount++
            if ($stackedDiffs) { $consecutiveFailures++ }
            Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'fix_failed' -BranchName $branchName `
                -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { $defaultBranch }) `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures `
                -AdditionalProperties $iterativeMetadataProperties
            continue
        }

        if ($iterativeExitReason -eq 'invalid_target') {
            Write-Warning "  Cannot apply fix — target directory does not exist: $targetFile"
            $staleCount++
            if ($stackedDiffs) { $consecutiveFailures++ }
            Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'invalid_target' -BranchName $branchName `
                -BaseBranch $(if ($stackedDiffs) { $currentBranch } else { $defaultBranch }) `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures `
                -AdditionalProperties $iterativeMetadataProperties
            continue
        }

        if ($iterativeExitReason -eq 'apply_failed') {
            Write-Warning "  Fix application failed: $($iterativeResult.FailureDetail)"
            $staleCount++
            if ($stackedDiffs) { $consecutiveFailures++ }
            Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome 'apply_failed' -BranchName $branchName `
                -BaseBranch $baseBranch `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures `
                -AdditionalProperties $iterativeMetadataProperties
            continue
        }

        $fixPhaseFailure = Get-FixPhaseFailurePresentation -IterativeResult $iterativeResult

        if ($stackedDiffs) {
            Write-Warning "$($fixPhaseFailure.RevertDescription) at experiment $experiment — reverting and continuing"

            $null = & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                -BranchName $branchName -FilePath $targetFile `
                -Experiment $experiment -Outcome $iterativeExitReason `
                -RevertDescription $fixPhaseFailure.RevertDescription `
                -MetadataSummary "$($fixPhaseFailure.MetadataPrefix): $($analysisResult.Explanation)" `
                -MetadataFilePath $analysisResult.FilePath `
                -QueueItemId $queueItemId `
                -ConfigPath $configPath `
                -TargetDir $targetDir

            $branchChain += $branchName
            $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = $iterativeExitReason }
            $consecutiveFailures++
            $staleCount++

            $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
            $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode.`n" } else { '' }
            $rejRcaSection = ''
            if ($rcaDocument) {
                $rejRcaSection = @(
                    ''
                    '### Root Cause Analysis'
                    ''
                    '<details>'
                    '<summary>Click to expand full analysis</summary>'
                    ''
                    $rcaDocument
                    ''
                    '</details>'
                    ''
                ) -join "`n"
            }

            $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                -Type 'Rejected' -Experiment $experiment `
                -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                -OutcomeLabel $fixPhaseFailure.Label `
                -OutcomeDetail $fixPhaseFailure.Detail `
                -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice `
                -RcaSection $rejRcaSection -IterationSummary $iterationSummary

            Push-Location $targetDir
            $rejPrResult = New-ExperimentPR `
                -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                -Outcome $iterativeExitReason -Description $analysisResult.Explanation `
                -Body $rejBody -IsDryRun:$DryRun
            if ($rejPrResult.Success) {
                $prNumber = $rejPrResult.PrNumber
                $prUrl = $rejPrResult.PrUrl
                $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = $iterativeExitReason }
            }
            Pop-Location

            Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                -Experiment $experiment -StartedAt $experimentStartedAt `
                -Outcome $iterativeExitReason -BranchName $branchName `
                -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { $defaultBranch }) `
                -PrNumber $prNumber -PrUrl $prUrl `
                -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures `
                -AdditionalProperties $iterativeMetadataProperties

            if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                $exitReason = 'max_consecutive_failures'
                Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
                break
            }

            continue
        }

        $exitReason = $iterativeExitReason
        Write-Warning "$($fixPhaseFailure.RevertDescription) at experiment $experiment — rolling back"
        Undo-ExperimentBranch -BranchName $branchName -RestoreBranch $currentBranch -TargetDir $targetDir
        break
    }

    if ($iterativeResult.AttemptCount -gt 1) {
        Write-Status "  ✓ Fix passed build + tests after $($iterativeResult.AttemptCount) attempts"
    }

    # ── Phase 4: Verify (tests + measure + compare) ──────────────────────
    Write-Status '[4/5] ✅ Verifying...'

    # Stress-test the optimized API (or generate synthetic metrics in dry-run)
    if ($DryRun) {
        Write-Status '  Measuring... [DRY RUN] Using synthetic metrics'

        # Generate synthetic metrics showing 5% improvement over reference
        $syntheticMetrics = [PSCustomObject]@{
            Timestamp = Get-Date -Format 'o'
            Experiment = $experiment
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
                Rate = [math]::Round($metricsForAnalysis.HttpReqs.Rate * 1.05, 1)
            }
            HttpReqFailed = [PSCustomObject]@{
                Count = 0
                Rate = 0
            }
        }

        $scaleResult = [PSCustomObject]@{
            Success = $true
            Metrics = $syntheticMetrics
            CounterMetrics = $null
            RunMetrics = $null
        }
        $scenarioResults = @()

        Write-Status "  Synthetic p95: $($syntheticMetrics.HttpReqDuration.P95)ms (5% improvement over reference)"
    } else {
        Write-Status '  Measuring (k6 scale tests)...'
        $measurementFailure = $null
        $measurementFailureReason = $null
        $measurementFailureLabel = $null
        $scenarioResults = @()

        try {
            $prepareResult = Invoke-LifecycleHook -Name 'Prepare' -TargetConfig $targetConfig `
                -TargetDir $targetDir -HarnessRoot $harnessRoot -Config $config -Experiment $experiment
            if (-not $prepareResult.Success) {
                $measurementFailureReason = 'prepare_failure'
                $measurementFailureLabel = 'Prepare hook failed'
                throw $prepareResult.Message
            }

            $startResult = Invoke-LifecycleHook -Name 'Start' -TargetConfig $targetConfig `
                -TargetDir $targetDir -HarnessRoot $harnessRoot -Config $config -Experiment $experiment
            if (-not $startResult.Success) {
                $measurementFailureReason = 'api_start_failure'
                $measurementFailureLabel = 'API failed to start'
                throw $startResult.Message
            }

            $apiProcess = $startResult.Process
            $baseUrl = if ($startResult.PSObject.Properties['ActualBaseUrl']) { $startResult.ActualBaseUrl } elseif ($startResult.PSObject.Properties['BaseUrl']) { $startResult.BaseUrl } else { $null }
            $config._Process = $apiProcess
            $config._BaseUrl = $baseUrl

            $readyResult = Invoke-LifecycleHook -Name 'Ready' -TargetConfig $targetConfig `
                -TargetDir $targetDir -HarnessRoot $harnessRoot -Config $config -BaseUrl $baseUrl -Experiment $experiment
            if (-not $readyResult.Success) {
                $measurementFailureReason = 'api_start_failure'
                $measurementFailureLabel = 'API failed readiness check'
                throw $readyResult.Message
            }

            $scaleResult = Invoke-LifecycleHook -Name 'Active' -TargetConfig $targetConfig `
                -TargetDir $targetDir -HarnessRoot $harnessRoot -Config $config -BaseUrl $baseUrl -Experiment $experiment
            if (-not $scaleResult.Success) {
                $measurementFailureReason = 'scale_test_failure'
                $measurementFailureLabel = 'Scale test failure'
                throw $scaleResult.Message
            }

            if ($scaleResult.PSObject.Properties['LastProcess'] -and $scaleResult.LastProcess) { $config._Process = $scaleResult.LastProcess }
            if ($scaleResult.PSObject.Properties['LastBaseUrl'] -and $scaleResult.LastBaseUrl) { $baseUrl = $scaleResult.LastBaseUrl; $config._BaseUrl = $scaleResult.LastBaseUrl }

            Write-Status '      Running additional scenarios...'
            $scenarioResults = & (Join-Path $PSScriptRoot 'Invoke-AllScaleTests.ps1') `
                -ConfigPath $configPath -TargetDir $targetDir -Experiment $experiment -SkipPrimary -SkipHealthCheck -BaseUrl $baseUrl
        } catch {
            $measurementFailure = $_
        } finally {
            if ($config._BaseUrl) {
                $null = Invoke-LifecycleHook -Name 'Cooldown' -TargetConfig $targetConfig `
                    -TargetDir $targetDir -HarnessRoot $harnessRoot -Config $config -BaseUrl $config._BaseUrl -Experiment $experiment
            }

            $null = Invoke-LifecycleHook -Name 'Stop' -TargetConfig $targetConfig `
                -TargetDir $targetDir -HarnessRoot $harnessRoot -Config $config -BaseUrl $config._BaseUrl -Experiment $experiment

            $null = Invoke-LifecycleHook -Name 'Cleanup' -TargetConfig $targetConfig `
                -TargetDir $targetDir -HarnessRoot $harnessRoot -Config $config -BaseUrl $config._BaseUrl -Experiment $experiment
        }

        if ($measurementFailure) {
            $failureDetail = if ($measurementFailure.Exception) { $measurementFailure.Exception.Message } else { "$measurementFailure" }

            if ($stackedDiffs) {
                Write-Warning "$measurementFailureLabel at experiment $experiment — reverting and continuing"

                $null = & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                    -BranchName $branchName -FilePath $targetFile `
                    -Experiment $experiment -Outcome 'regressed' `
                    -RevertDescription $measurementFailureLabel `
                    -MetadataSummary "${measurementFailureLabel}: $($analysisResult.Explanation)" `
                    -MetadataFilePath $analysisResult.FilePath `
                    -QueueItemId $queueItemId `
                    -ConfigPath $configPath `
                    -TargetDir $targetDir

                $branchChain += $branchName
                $failedExperiments += [PSCustomObject]@{ Experiment = $experiment; Reason = $measurementFailureReason }
                $consecutiveFailures++
                $staleCount++

                $rejStackNote = Build-StackNote -PrChain $prChain -FailedExperiments $failedExperiments `
                    -Experiment $experiment -OutcomeTag '[REJECTED]' -BaseBranch $baseBranch
                $rejDryRunNotice = if ($DryRun) { "`n> ⚡ **DRY RUN** — Created in dry-run mode.`n" } else { '' }
                $rejBody = & (Join-Path $PSScriptRoot 'Build-PRBody.ps1') `
                    -Type 'Rejected' -Experiment $experiment `
                    -Description $analysisResult.Explanation -FilePath $analysisResult.FilePath `
                    -OutcomeLabel "❌ **$measurementFailureLabel**" `
                    -OutcomeDetail $failureDetail `
                    -StackNote $rejStackNote -DryRunNotice $rejDryRunNotice `
                    -IterationSummary $iterationSummary
                Push-Location $targetDir
                $rejPrResult = New-ExperimentPR `
                    -Experiment $experiment -BranchName $branchName -BaseBranch $baseBranch `
                    -Outcome 'regressed' -Description $analysisResult.Explanation `
                    -Body $rejBody -IsDryRun:$DryRun
                if ($rejPrResult.Success) {
                    $prNumber = $rejPrResult.PrNumber
                    $prUrl = $rejPrResult.PrUrl
                    $prChain += [PSCustomObject]@{ Number = $prNumber; Experiment = $experiment; Url = "$prUrl"; Outcome = $measurementFailureReason }
                }
                Pop-Location

                Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
                    -Experiment $experiment -StartedAt $experimentStartedAt `
                    -Outcome $measurementFailureReason -BranchName $branchName `
                    -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { $defaultBranch }) `
                    -PrNumber $prNumber -PrUrl $prUrl `
                    -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures `
                    -AdditionalProperties $iterativeMetadataProperties

                if ($consecutiveFailures -ge $maxConsecutiveFailures) {
                    $exitReason = 'max_consecutive_failures'
                    Write-Status "  Stopping: $consecutiveFailures consecutive failures reached limit"
                    break
                }
                continue
            } else {
                $exitReason = $measurementFailureReason
                Write-Error "$measurementFailureLabel at experiment $experiment. Aborting. $failureDetail"
                break
            }
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
        -ConfigPath $configPath `
        -Experiment $experiment

    # Reference metrics used for delta computation (previous improved or baseline)
    $referenceMetrics = if ($previousMetrics) { $previousMetrics } else { $baselineMetrics }
    $referenceLabel = if ($previousMetrics) { "Experiment $previousMetricsExperiment" } else { 'Baseline' }

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
            -ConfigPath $configPath -TargetDir $targetDir

        # Mark queue item as done
        & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
            -Action 'MarkDone' -ItemId $queueItemId `
            -Experiment $experiment -Outcome 'regressed' `
            -ConfigPath $configPath -TargetDir $targetDir

        if ($stackedDiffs) {
            Write-Warning "  Reverting code change, preserving artifacts"

            $null = & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                -BranchName $branchName -FilePath $targetFile `
                -Experiment $experiment -Outcome 'regressed' `
                -RevertDescription (Limit-String $analysisResult.Explanation 120) `
                -SkipMetadataUpdate -SkipQueueMarkDone `
                -ConfigPath $configPath `
                -TargetDir $targetDir

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
                -MetricsSection $rejMetrics -RcaSection $rejRcaSection `
                -IterationSummary $iterationSummary
            Push-Location $targetDir
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
        } else {
            $exitReason = 'regression'
            Write-Warning "  Rolling back to previous state"
            Undo-ExperimentBranch -BranchName $branchName -RestoreBranch $currentBranch -TargetDir $targetDir
            break
        }
    } else {
        if ($comparison.Improved -or $comparison.TiebreakerUsed) {
            $experimentOutcome = 'improved'
            $staleCount = 0
            $consecutiveFailures = 0
            $successCount++

            if ($comparison.TiebreakerUsed) {
                Write-Status '  ↑ Efficiency improvement (tiebreaker) — publishing'
            } else {
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
                -ConfigPath $configPath -TargetDir $targetDir

            # Mark queue item as done
            & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
                -Action 'MarkDone' -ItemId $queueItemId `
                -Experiment $experiment -Outcome 'improved' `
                -ConfigPath $configPath -TargetDir $targetDir

            # Amend the commit to include post-fix artifacts
            Push-Location $targetDir
            & (Join-Path $PSScriptRoot 'Stage-ExperimentArtifacts.ps1') `
                -Experiment $experiment -SubmoduleDir $targetDir
            git commit --amend --no-gpg-sign --no-edit 2>&1 | Out-Null

            # Push and create PR
            git push -u origin $branchName 2>&1 | Out-Null

            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'publish' -Level 'info' `
                -Message "Branch pushed to origin: $branchName" `
                -Experiment $experiment

            # Determine PR base: stacked mode uses baseBranch (previous experiment); legacy uses default branch
            $prBaseBranch = if ($stackedDiffs) { $baseBranch } else { $defaultBranch }

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
                    } else {
                        $scenarioBaselinePath = Join-Path -Path $targetDir -ChildPath $config.Api.ResultsPath "baseline-$($sr.ScenarioName).json"
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
                -ScenarioBreakdown $scenarioBreakdown `
                -IterationSummary $iterationSummary

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
                    $prState = gh pr view $prNumber --json state, mergedAt 2>$null | ConvertFrom-Json
                    if ($prState.state -eq 'MERGED') {
                        Write-Status "  ✓ PR #$prNumber merged — continuing loop"

                        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                            -Phase 'publish' -Level 'info' `
                            -Message "PR #$prNumber merged at $($prState.mergedAt)" `
                            -Experiment $experiment

                        if (-not $stackedDiffs) {
                            # Legacy mode: update local default branch
                            git fetch origin $defaultBranch 2>&1 | Out-Null
                            git checkout $defaultBranch 2>&1 | Out-Null
                            git merge "origin/$defaultBranch" --ff-only 2>&1 | Out-Null
                            $currentBranch = $defaultBranch
                        }
                        break
                    } elseif ($prState.state -eq 'CLOSED') {
                        Write-Warning "  PR #$prNumber was closed without merging — stopping loop"

                        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                            -Phase 'publish' -Level 'warning' `
                            -Message "PR #$prNumber closed without merge" `
                            -Experiment $experiment

                        if (-not $stackedDiffs) {
                            git checkout $defaultBranch 2>&1 | Out-Null
                            $currentBranch = $defaultBranch
                        }
                        $exitReason = 'pr_rejected'
                        break
                    } else {
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
        } else {
            # No improvement and no regression — stale
            $staleCount++
            $consecutiveFailures++

            & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
                -Action 'AddTried' `
                -Experiment $experiment `
                -Summary $analysisResult.Explanation `
                -FilePath $analysisResult.FilePath `
                -Outcome 'stale' `
                -ConfigPath $configPath -TargetDir $targetDir

            # Mark queue item as done
            & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
                -Action 'MarkDone' -ItemId $queueItemId `
                -Experiment $experiment -Outcome 'stale' `
                -ConfigPath $configPath -TargetDir $targetDir

            if ($stackedDiffs) {
                Write-Status "  ─ No improvement (stale — failure $consecutiveFailures / $maxConsecutiveFailures)"

                $null = & (Join-Path $PSScriptRoot 'Invoke-FailureHandler.ps1') `
                    -BranchName $branchName -FilePath $targetFile `
                    -Experiment $experiment -Outcome 'stale' `
                    -RevertDescription (Limit-String $analysisResult.Explanation 120) `
                    -SkipMetadataUpdate -SkipQueueMarkDone `
                    -ConfigPath $configPath `
                    -TargetDir $targetDir

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
                    -MetricsSection $rejMetrics -RcaSection $rejRcaSection `
                    -IterationSummary $iterationSummary
                Push-Location $targetDir
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
            } else {
                Write-Status "  ─ No improvement (stale $staleCount / $($tolerances.StaleExperimentsBeforeStop))"

                Undo-ExperimentBranch -BranchName $branchName -RestoreBranch $currentBranch -TargetDir $targetDir

                if ($staleCount -ge $tolerances.StaleExperimentsBeforeStop) {
                    $exitReason = 'no_improvement'
                    Write-Status '  Stopping: no improvement for consecutive experiments'
                    break
                }
            }
        }
    }

    # Only update metrics reference on successful experiments.
    # After a regression/stale revert, the code is back to the previous state
    # so the reference metrics should remain from the last successful experiment.
    if ($experimentOutcome -eq 'improved') {
        $previousMetrics = $currentMetrics
        $previousMetricsExperiment = $experiment
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

    Add-ExperimentMetadatum -RunMetadata $runMetadata -MetadataPath $runMetadataPath `
        -Experiment $experiment -StartedAt $experimentStartedAt `
        -Outcome $experimentOutcome -BranchName $branchName `
        -BaseBranch $(if ($stackedDiffs) { $baseBranch } else { $defaultBranch }) `
        -Metrics $currentMetrics `
        -Improved $comparison.Improved -Regression $comparison.Regression `
        -PrNumber $experimentPrNumber -PrUrl $experimentPrUrl `
        -StaleCount $staleCount -ConsecutiveFailures $consecutiveFailures `
        -AdditionalProperties $iterativeMetadataProperties
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
    exitReason = $exitReason
    experiments = $experimentsRun
    bestP95 = $bestP95
    bestExperiment = $bestExperiment
    successCount = $successCount
    prChain = @($prChain | ForEach-Object { $_.Number })
}

# ── Finalize run metadata ────────────────────────────────────────────────────
$runMetadata | Add-Member -NotePropertyName LoopCompletedAt -NotePropertyValue (Get-Date -Format 'o') -Force
if ($stackedDiffs) {
    $runMetadata | Add-Member -NotePropertyName PrChain -NotePropertyValue @($prChain | ForEach-Object { $_.Number }) -Force
    $runMetadata | Add-Member -NotePropertyName FullBranchChain -NotePropertyValue $branchChain -Force
}
$runMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $runMetadataPath -Encoding utf8

# Return summary object
$global:LASTEXITCODE = 0
[PSCustomObject][ordered]@{
    ExitReason = $exitReason
    Experiments = $experimentsRun
    SuccessCount = $successCount
    BestP95 = $bestP95
    BestExperiment = $bestExperiment
    BaselineP95 = $baselineMetrics.HttpReqDuration.P95
    PrChain = @($prChain | ForEach-Object { $_.Number })
    FullBranchChain = $branchChain
    FailedExperiments = @($failedExperiments | ForEach-Object { $_.Experiment })
}
