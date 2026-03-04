<#
.SYNOPSIS
    Main entry point for the Hone agentic optimization loop.

.DESCRIPTION
    Orchestrates the full iterative optimization cycle. Each iteration is
    a self-contained optimization cycle:
    1. Analyze with Copilot to identify the next optimization
    2. Apply the suggested code change locally
    3. Build the target API with the fix applied
    4. Verify correctness with E2E tests
    5. Start the API and measure performance with k6
    6. Compare results against baseline — confirm improvement
    7. If validated: push branch + create PR; if not: rollback
    Exits when max iterations reached, regression detected, or no improvement.

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

function Undo-IterationBranch {
    <#
    .SYNOPSIS
        Rolls back to master and deletes the iteration branch.
    #>
    param(
        [Parameter(Mandatory)][string]$BranchName,
        [Parameter(Mandatory)][string]$RepoRoot
    )
    try {
        Push-Location (Join-Path $RepoRoot 'sample-api')
        git checkout master 2>&1 | Out-Null
        git branch -D $BranchName 2>&1 | Out-Null
        Pop-Location
    }
    catch {
        Pop-Location -ErrorAction SilentlyContinue
        Write-Warning "Rollback failed for branch '$BranchName': $_"
    }
}

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
        Iterations     = @()
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

# ── Iteration Loop ──────────────────────────────────────────────────────────
$previousMetrics = $null
$previousCounterMetrics = $null
$previousRcaExplanation = ''
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

    # ── Phase 1: Analyze ───────────────────────────────────────────────────
    Write-Information '[1/7] Analyzing with Copilot...' -InformationAction Continue

    # For iteration 1, use baseline as current (no prior fix to measure).
    # For iteration 2+, use the metrics from the previous iteration's post-fix measurement.
    $metricsForAnalysis = if ($previousMetrics) { $previousMetrics } else { $baselineMetrics }
    $countersForAnalysis = if ($previousCounterMetrics) { $previousCounterMetrics } else { $null }

    # Build a comparison object for the analysis prompt.
    $comparisonForAnalysis = & (Join-Path $PSScriptRoot 'Compare-Results.ps1') `
        -CurrentMetrics $metricsForAnalysis `
        -BaselineMetrics $baselineMetrics `
        -PreviousMetrics $previousMetrics `
        -CurrentCounterMetrics $countersForAnalysis `
        -PreviousCounterMetrics $previousCounterMetrics `
        -ConfigPath $ConfigPath `
        -Iteration $iteration

    # ── Sub-agent 1: Analysis ──────────────────────────────────────────────
    $analysisResult = & (Join-Path $PSScriptRoot 'Invoke-AnalysisAgent.ps1') `
        -CurrentMetrics $metricsForAnalysis `
        -BaselineMetrics $baselineMetrics `
        -ComparisonResult $comparisonForAnalysis `
        -CounterMetrics $countersForAnalysis `
        -Iteration $iteration `
        -ConfigPath $ConfigPath `
        -PreviousRcaExplanation $previousRcaExplanation

    $rcaResult = $null
    $applyResult = $null
    $branchName = "$($config.Loop.BranchPrefix)-$iteration"
    $applySuccess = $false
    $skipIteration = $false

    if (-not $analysisResult.Success) {
        Write-Warning '  Analysis agent failed — skipping iteration'
        $staleCount++
        if ($staleCount -ge $tolerances.StaleIterationsBeforeStop) {
            $exitReason = 'no_improvement'
            Write-Information '  Stopping: no improvement for consecutive iterations' -InformationAction Continue
            break
        }
        continue
    }

    Write-Information "  Analysis: $($analysisResult.FilePath)" -InformationAction Continue
    Write-Information "  Response saved to: $($analysisResult.ResponsePath)" -InformationAction Continue

    # ── Sub-agent 2: Classification ────────────────────────────────────────
    $classificationResult = & (Join-Path $PSScriptRoot 'Invoke-ClassificationAgent.ps1') `
        -FilePath $analysisResult.FilePath `
        -Explanation $analysisResult.Explanation `
        -Iteration $iteration `
        -ConfigPath $ConfigPath

    $changeScope = $classificationResult.Scope
    Write-Information "  Classification: $changeScope — $($classificationResult.Reasoning)" -InformationAction Continue

    # Generate root cause analysis document (structured data, no regex)
    $rcaResult = & (Join-Path $PSScriptRoot 'Export-IterationRCA.ps1') `
        -FilePath $analysisResult.FilePath `
        -Explanation $analysisResult.Explanation `
        -ChangeScope $changeScope `
        -ScopeReasoning $classificationResult.Reasoning `
        -CurrentMetrics $metricsForAnalysis `
        -BaselineMetrics $baselineMetrics `
        -ComparisonResult $comparisonForAnalysis `
        -Iteration $iteration `
        -ConfigPath $ConfigPath

    if ($rcaResult.Success) {
        Write-Information "  Root cause analysis saved to: $($rcaResult.Path)" -InformationAction Continue
    }

    # Always log the analysis to optimization-log.md
    $isArchitecture = ($changeScope -eq 'architecture')

    if ($isArchitecture) {
        Write-Information '  ⚠ Architecture-level change detected — queuing for manual review' -InformationAction Continue
        Write-Information "    Scope: $changeScope | File: $($analysisResult.FilePath)" -InformationAction Continue
        Write-Information '    Add [APPROVED] tag in optimization-queue.md to enable implementation.' -InformationAction Continue

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'fix' -Level 'info' `
            -Message "Architecture change queued (not applied): $($analysisResult.Explanation.Substring(0, [Math]::Min(100, $analysisResult.Explanation.Length)))" `
            -Iteration $iteration

        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddQueue' `
            -Iteration $iteration `
            -Opportunities @($analysisResult.Explanation) `
            -Scopes @('architecture') `
            -ConfigPath $ConfigPath

        # Log architecture changes to the optimization log too
        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Iteration $iteration `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'queued' `
            -ConfigPath $ConfigPath

        $skipIteration = $true
    }

    if ($skipIteration) {
        $staleCount++
        if ($staleCount -ge $tolerances.StaleIterationsBeforeStop) {
            $exitReason = 'no_improvement'
            Write-Information '  Stopping: no improvement for consecutive iterations' -InformationAction Continue
            break
        }
        continue
    }

    # ── Phase 2: Fix (generate + apply locally, defer PR) ──────────────────
    Write-Information '[2/7] Generating fix...' -InformationAction Continue

    # ── Sub-agent 3: Fix ───────────────────────────────────────────────────
    $fixResult = & (Join-Path $PSScriptRoot 'Invoke-FixAgent.ps1') `
        -FilePath $analysisResult.FilePath `
        -Explanation $analysisResult.Explanation `
        -Iteration $iteration `
        -ConfigPath $ConfigPath

    if (-not $fixResult.Success -or -not $fixResult.CodeBlock) {
        Write-Warning '  Fix agent failed to generate code — skipping iteration'
        $staleCount++
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
        continue
    }

    Write-Information "  Applying fix to: $targetFile" -InformationAction Continue

    $applyResult = & (Join-Path $PSScriptRoot 'Apply-Suggestion.ps1') `
        -FilePath $targetFile `
        -NewContent $fixResult.CodeBlock `
        -Description $analysisResult.Explanation.Substring(0, [Math]::Min(120, $analysisResult.Explanation.Length)) `
        -Iteration $iteration `
        -ConfigPath $ConfigPath

    if (-not $applyResult.Success) {
        Write-Warning "  Fix application failed: $($applyResult.Description)"
        $staleCount++
        continue
    }

    Write-Information "  ✓ Fix committed locally on branch: $($applyResult.BranchName)" -InformationAction Continue

    # ── Phase 3: Build ──────────────────────────────────────────────────────
    Write-Information '[3/7] Building...' -InformationAction Continue
    $buildResult = & (Join-Path $PSScriptRoot 'Build-SampleApi.ps1') -ConfigPath $ConfigPath

    if (-not $buildResult.Success) {
        $exitReason = 'build_failure'
        Write-Error "Build failed at iteration $iteration — rolling back"

        Undo-IterationBranch -BranchName $branchName -RepoRoot $repoRoot

        break
    }

    # ── Phase 4: Verify (E2E Tests) ────────────────────────────────────────
    Write-Information '[4/7] Verifying (E2E tests)...' -InformationAction Continue
    $testResult = & (Join-Path $PSScriptRoot 'Invoke-E2ETests.ps1') `
        -ConfigPath $ConfigPath -Iteration $iteration

    if (-not $testResult.Success) {
        $exitReason = 'test_failure'
        Write-Warning "E2E tests failed at iteration $iteration — rolling back"

        Undo-IterationBranch -BranchName $branchName -RepoRoot $repoRoot

        break
    }

    # ── Phase 5: Measure (Scale Tests) ─────────────────────────────────────
    Write-Information '[5/7] Measuring (k6 scale tests)...' -InformationAction Continue

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

        # Run additional (diagnostic) scenarios
        Write-Information '      Running additional scenarios...' -InformationAction Continue
        $scenarioResults = & (Join-Path $PSScriptRoot 'Invoke-AllScaleTests.ps1') `
            -ConfigPath $ConfigPath -Iteration $iteration -SkipPrimary
    }
    finally {
        & (Join-Path $PSScriptRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
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

    # Track best iteration
    if ($currentMetrics.HttpReqDuration.P95 -lt $bestP95) {
        $bestP95 = $currentMetrics.HttpReqDuration.P95
        $bestIteration = $iteration
    }

    # ── Phase 6: Compare ───────────────────────────────────────────────────
    Write-Information '[6/7] Comparing results...' -InformationAction Continue

    $comparison = & (Join-Path $PSScriptRoot 'Compare-Results.ps1') `
        -CurrentMetrics $currentMetrics `
        -BaselineMetrics $baselineMetrics `
        -PreviousMetrics $previousMetrics `
        -CurrentCounterMetrics $currentCounterMetrics `
        -PreviousCounterMetrics $previousCounterMetrics `
        -RunMetrics $scaleResult.RunMetrics `
        -ConfigPath $ConfigPath `
        -Iteration $iteration

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

    # ── Phase 7: Publish or Rollback ───────────────────────────────────────
    $iterationOutcome = 'stale'

    if ($comparison.Regression) {
        $iterationOutcome = 'regressed'
        $exitReason = 'regression'
        Write-Warning "  Regression detected: $($comparison.RegressionDetail)"
        Write-Warning "  Rolling back to previous state"

        Undo-IterationBranch -BranchName $branchName -RepoRoot $repoRoot

        # Update metadata with regression outcome
        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Iteration $iteration `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'regressed' `
            -ConfigPath $ConfigPath

        break
    }
    elseif ($comparison.Improved -or $comparison.TiebreakerUsed) {
        $iterationOutcome = 'improved'
        $staleCount = 0

        if ($comparison.TiebreakerUsed) {
            Write-Information '  ↑ Efficiency improvement (tiebreaker) — publishing' -InformationAction Continue
        }
        else {
            Write-Information '  ↑ Improvement detected — publishing' -InformationAction Continue
        }

        Write-Information '[7/7] Publishing (push + PR)...' -InformationAction Continue

        # Amend the commit to include post-fix artifacts (k6 summaries, comparison data)
        Push-Location (Join-Path $repoRoot 'sample-api')
        $iterationDir = Join-Path (Join-Path $repoRoot 'sample-api') 'results' "iteration-$iteration"
        if (Test-Path $iterationDir) {
            git add "results/iteration-$iteration/" 2>&1 | Out-Null
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
            -Phase 'fix' -Level 'info' `
            -Message "Branch pushed to origin: $branchName" `
            -Iteration $iteration

        $prBody = @"
## Hone Iteration $iteration

**Optimization:** $($analysisResult.Explanation.Substring(0, [Math]::Min(200, $analysisResult.Explanation.Length)))

**File changed:** ``$($analysisResult.FilePath)``

### Performance Results
| Metric | Baseline | After Fix | Delta |
|--------|----------|-----------|-------|
| p95 Latency | $($baselineMetrics.HttpReqDuration.P95)ms | $($currentMetrics.HttpReqDuration.P95)ms | $($d.P95Latency.ChangePct)% |
| Requests/sec | $([math]::Round($baselineMetrics.HttpReqs.Rate, 1)) | $([math]::Round($currentMetrics.HttpReqs.Rate, 1)) | $($d.RPS.ChangePct)% |
| Error Rate | $([math]::Round($baselineMetrics.HttpReqFailed.Rate * 100, 2))% | $([math]::Round($currentMetrics.HttpReqFailed.Rate * 100, 2))% | $($d.ErrorRate.ChangePct)% |

**vs baseline improvement:** $($comparison.ImprovementPct)%

---
*Auto-generated by the Hone agentic optimization harness.*
*Review the changes, then merge to continue the optimization loop.*
"@

        $prUrl = gh pr create `
            --base master `
            --head $branchName `
            --title "hone(iteration-$iteration): $($analysisResult.Explanation.Substring(0, [Math]::Min(80, $analysisResult.Explanation.Length)))" `
            --body $prBody 2>&1

        $prNumber = $null
        if ($LASTEXITCODE -eq 0) {
            $prNumber = ($prUrl -split '/')[-1]

            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'fix' -Level 'info' `
                -Message "Pull request created: $prUrl" `
                -Iteration $iteration `
                -Data @{ prUrl = "$prUrl"; prNumber = $prNumber }

            Write-Information "  ✓ Pull request created: $prUrl" -InformationAction Continue
        }
        else {
            Write-Warning "  Failed to create pull request: $prUrl"
        }

        # If more iterations remain, wait for PR to be merged before continuing
        if ($prNumber -and $iteration -lt $maxIter) {
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
                        -Phase 'fix' -Level 'info' `
                        -Message "PR #$prNumber merged at $($prState.mergedAt)" `
                        -Iteration $iteration

                    # Update local master to include the merged changes
                    git fetch origin master 2>&1 | Out-Null
                    git checkout master 2>&1 | Out-Null
                    git merge origin/master --ff-only 2>&1 | Out-Null
                    break
                }
                elseif ($prState.state -eq 'CLOSED') {
                    Write-Warning "  PR #$prNumber was closed without merging — stopping loop"

                    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                        -Phase 'fix' -Level 'warning' `
                        -Message "PR #$prNumber closed without merge" `
                        -Iteration $iteration

                    git checkout master 2>&1 | Out-Null
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

        # Update metadata with improvement outcome
        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Iteration $iteration `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'improved' `
            -ConfigPath $ConfigPath
    }
    else {
        # No improvement and no regression — stale
        $staleCount++
        Write-Information "  ─ No improvement (stale $staleCount / $($tolerances.StaleIterationsBeforeStop))" -InformationAction Continue

        # Rollback the fix since it didn't help
        Undo-IterationBranch -BranchName $branchName -RepoRoot $repoRoot

        # Update metadata with stale outcome
        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddTried' `
            -Iteration $iteration `
            -Summary $analysisResult.Explanation `
            -FilePath $analysisResult.FilePath `
            -Outcome 'stale' `
            -ConfigPath $ConfigPath

        if ($staleCount -ge $tolerances.StaleIterationsBeforeStop) {
            $exitReason = 'no_improvement'
            Write-Information '  Stopping: no improvement for consecutive iterations' -InformationAction Continue
            break
        }
    }

    # Queue additional optimization opportunities from the analysis
    if ($analysisResult.AdditionalOpportunities -and $analysisResult.AdditionalOpportunities.Count -gt 0) {
        # Analysis agent returns JSON objects with .description and .scope fields
        $oppDescriptions = @($analysisResult.AdditionalOpportunities | ForEach-Object { $_.description })
        $oppScopes = @($analysisResult.AdditionalOpportunities | ForEach-Object {
            if ($_.scope -eq 'architecture') { 'architecture' } else { 'narrow' }
        })

        & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
            -Action 'AddQueue' `
            -Iteration $iteration `
            -Opportunities $oppDescriptions `
            -Scopes $oppScopes `
            -ConfigPath $ConfigPath

        Write-Information "  Queued $($oppDescriptions.Count) additional optimization opportunities" -InformationAction Continue
    }

    $previousMetrics = $currentMetrics
    $previousCounterMetrics = $currentCounterMetrics
    $previousRcaExplanation = if ($analysisResult) { $analysisResult.Explanation } else { '' }

    # ── Record iteration metadata ──────────────────────────────────────────────
    $iterationMeta = [ordered]@{
        Iteration   = $iteration
        StartedAt   = $iterationStartedAt
        CompletedAt = (Get-Date -Format 'o')
        Improved    = $comparison.Improved
        Regression  = $comparison.Regression
        P95         = $currentMetrics.HttpReqDuration.P95
        RPS         = [math]::Round($currentMetrics.HttpReqs.Rate, 1)
        Outcome     = $iterationOutcome
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
