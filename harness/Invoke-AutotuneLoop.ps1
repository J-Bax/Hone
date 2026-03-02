<#
.SYNOPSIS
    Main entry point for the Autotune agentic optimization loop.

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

.PARAMETER P95TargetMs
    Override p95 latency target from config.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.EXAMPLE
    .\Invoke-AutotuneLoop.ps1

.EXAMPLE
    .\Invoke-AutotuneLoop.ps1 -MaxIterations 10 -P95TargetMs 150
#>
[CmdletBinding()]
param(
    [int]$MaxIterations,
    [double]$P95TargetMs,
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
if ($P95TargetMs -gt 0)   { $config.Thresholds.P95LatencyMs = $P95TargetMs }

$maxIter = $config.Loop.MaxIterations

# ── Banner ──────────────────────────────────────────────────────────────────
Write-Information '' -InformationAction Continue
Write-Information '╔══════════════════════════════════════════════════════════╗' -InformationAction Continue
Write-Information '║               AUTOTUNE — Agentic Optimizer              ║' -InformationAction Continue
Write-Information '╚══════════════════════════════════════════════════════════╝' -InformationAction Continue
Write-Information '' -InformationAction Continue
Write-Information "  Max iterations:  $maxIter" -InformationAction Continue
Write-Information "  p95 target:      $($config.Thresholds.P95LatencyMs)ms" -InformationAction Continue
Write-Information "  RPS target:      $($config.Thresholds.MinRequestsPerSec)" -InformationAction Continue
Write-Information "  Max error rate:  $([math]::Round($config.Thresholds.MaxErrorRate * 100, 2))%" -InformationAction Continue
Write-Information '' -InformationAction Continue

& (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
    -Phase 'loop' -Level 'info' -Message "Autotune loop starting (max $maxIter iterations)"

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
$exitReason = 'max_iterations'
$bestIteration = 0
$bestP95 = $baselineMetrics.HttpReqDuration.P95

for ($iteration = 1; $iteration -le $maxIter; $iteration++) {

    Write-Information '' -InformationAction Continue
    Write-Information "━━━ Iteration $iteration / $maxIter ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -InformationAction Continue

    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
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

        # Rollback to previous branch
        Push-Location $repoRoot
        git checkout main 2>&1 | Out-Null
        Pop-Location

        break
    }

    # ── Phase 3: Measure (Scale Tests) ─────────────────────────────────────
    Write-Information '[3/5] Measuring (k6 scale tests)...' -InformationAction Continue

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
    }
    finally {
        & (Join-Path $PSScriptRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
    }

    $currentMetrics = $scaleResult.Metrics

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
        -ConfigPath $ConfigPath `
        -Iteration $iteration

    Write-Information "  p95: $($currentMetrics.HttpReqDuration.P95)ms | RPS: $([math]::Round($currentMetrics.HttpReqs.Rate, 1)) | Improvement: $($comparison.ImprovementPct)%" -InformationAction Continue

    if ($comparison.AllTargetsMet) {
        $exitReason = 'targets_met'
        Write-Information '  ✓ All performance targets met!' -InformationAction Continue
        break
    }

    if ($comparison.Regression) {
        $exitReason = 'regression'
        Write-Warning "  Regression detected: $($comparison.RegressionDetail)"
        Write-Warning "  Rolling back to previous iteration"

        Push-Location $repoRoot
        git checkout main 2>&1 | Out-Null
        Pop-Location

        break
    }

    # ── Phase 5: Analyze + Fix ─────────────────────────────────────────────
    if ($iteration -lt $maxIter) {
        Write-Information '[5/5] Analyzing with Copilot and applying fix...' -InformationAction Continue

        $analysisResult = & (Join-Path $PSScriptRoot 'Invoke-CopilotAnalysis.ps1') `
            -CurrentMetrics $currentMetrics `
            -BaselineMetrics $baselineMetrics `
            -ComparisonResult $comparison `
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
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Information '' -InformationAction Continue
Write-Information '╔══════════════════════════════════════════════════════════╗' -InformationAction Continue
Write-Information '║                    AUTOTUNE COMPLETE                     ║' -InformationAction Continue
Write-Information '╚══════════════════════════════════════════════════════════╝' -InformationAction Continue
Write-Information '' -InformationAction Continue
Write-Information "  Exit reason:     $exitReason" -InformationAction Continue
Write-Information "  Iterations run:  $iteration" -InformationAction Continue
Write-Information "  Best p95:        ${bestP95}ms (iteration $bestIteration)" -InformationAction Continue
Write-Information "  Baseline p95:    $($baselineMetrics.HttpReqDuration.P95)ms" -InformationAction Continue
Write-Information '' -InformationAction Continue

& (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
    -Phase 'loop' -Level 'info' `
    -Message "Autotune loop complete: $exitReason after $iteration iterations" `
    -Data @{
        exitReason    = $exitReason
        iterations    = $iteration
        bestP95       = $bestP95
        bestIteration = $bestIteration
    }

# Return summary object
[PSCustomObject][ordered]@{
    ExitReason    = $exitReason
    Iterations    = $iteration
    BestP95       = $bestP95
    BestIteration = $bestIteration
    BaselineP95   = $baselineMetrics.HttpReqDuration.P95
}
