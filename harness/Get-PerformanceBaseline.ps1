<#
.SYNOPSIS
    Establishes a performance baseline by running scale tests.

.DESCRIPTION
    Builds the API, starts it, runs the baseline k6 scenario, saves the results,
    and stops the API. The baseline results are stored in sample-api/results/baseline.json
    and used by Compare-Results.ps1 for comparison.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

# ── Prerequisite check: k6 must be on PATH ──────────────────────────────────
if (-not (Get-Command 'k6' -ErrorAction SilentlyContinue)) {
    Write-Error 'k6 is not on PATH — install k6 or add its directory to PATH before running the baseline'
    return
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'baseline' -Level 'info' -Message 'Establishing performance baseline'

# ── Collect machine info ────────────────────────────────────────────────────
$machineInfo = & (Join-Path $PSScriptRoot 'Get-MachineInfo.ps1')
Write-Information "Machine: $($machineInfo.MachineName) | CPU: $($machineInfo.Cpu.Name) ($($machineInfo.Cpu.LogicalProcessors) logical cores) | RAM: $($machineInfo.Memory.TotalGB)GB" -InformationAction Continue

# ── Step 1: Build ───────────────────────────────────────────────────────────
$buildResult = & (Join-Path $PSScriptRoot 'Build-SampleApi.ps1') -ConfigPath $ConfigPath
if (-not $buildResult.Success) {
    Write-Error 'Build failed — cannot establish baseline'
    return
}

# ── Step 1.5: Reset Database ────────────────────────────────────────────────
& (Join-Path $PSScriptRoot 'Reset-Database.ps1') -ConfigPath $ConfigPath -Iteration 0

# ── Step 2: Start API ──────────────────────────────────────────────────────
$apiResult = & (Join-Path $PSScriptRoot 'Start-SampleApi.ps1') -ConfigPath $ConfigPath
if (-not $apiResult.Success) {
    Write-Error 'API failed to start — cannot establish baseline'
    return
}

try {
    # ── Step 3: Run scale tests ─────────────────────────────────────────────
    $scaleResult = & (Join-Path $PSScriptRoot 'Invoke-ScaleTests.ps1') `
        -ConfigPath $ConfigPath -Iteration 0

    # For baselines, we only need metrics — k6 threshold failures are expected
    # since the API has not been optimized yet.
    if (-not $scaleResult.Metrics) {
        Write-Error 'Scale tests produced no metrics — cannot establish baseline'
        return
    }

    if (-not $scaleResult.Success) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'baseline' -Level 'warning' `
            -Message 'k6 thresholds not met (expected for unoptimized baseline)' `
            -Iteration 0
    }

    # ── Step 4: Save baseline ───────────────────────────────────────────────
    $baselinePath = Join-Path $repoRoot $config.Api.ResultsPath 'baseline.json'
    $scaleResult.Metrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $baselinePath -Encoding utf8

    # Save machine info and run metadata
    $runMetadataPath = Join-Path $repoRoot $config.Api.ResultsPath 'run-metadata.json'
    $runMetadata = [ordered]@{
        Machine     = $machineInfo
        BaselineRun = [ordered]@{
            StartedAt  = $machineInfo.CollectedAt
            CompletedAt = (Get-Date -Format 'o')
        }
        Iterations  = @()
    }
    $runMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $runMetadataPath -Encoding utf8

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'baseline' -Level 'info' `
        -Message "Run metadata saved to: $runMetadataPath" `
        -Iteration 0

    # Save counter metrics baseline if available
    if ($scaleResult.CounterMetrics) {
        $counterBaselinePath = Join-Path $repoRoot $config.Api.ResultsPath 'baseline-counters.json'
        $scaleResult.CounterMetrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $counterBaselinePath -Encoding utf8

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'baseline' -Level 'info' `
            -Message "Counter baseline saved to: $counterBaselinePath" `
            -Iteration 0
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'baseline' -Level 'info' `
        -Message "Baseline saved to: $baselinePath" `
        -Data @{
            p95 = $scaleResult.Metrics.HttpReqDuration.P95
            rps = $scaleResult.Metrics.HttpReqs.Rate
        }

    Write-Information "Baseline established:" -InformationAction Continue
    Write-Information "  p95 latency: $($scaleResult.Metrics.HttpReqDuration.P95)ms" -InformationAction Continue
    Write-Information "  RPS:         $([math]::Round($scaleResult.Metrics.HttpReqs.Rate, 1))" -InformationAction Continue
    Write-Information "  Error rate:  $([math]::Round(($scaleResult.Metrics.HttpReqFailed.Rate) * 100, 2))%" -InformationAction Continue

    if ($scaleResult.CounterMetrics) {
        $cpuAvg = if ($scaleResult.CounterMetrics.Runtime.CpuUsage) { "$($scaleResult.CounterMetrics.Runtime.CpuUsage.Avg)%" } else { 'N/A' }
        $heapMax = if ($scaleResult.CounterMetrics.Runtime.GcHeapSizeMB) { "$($scaleResult.CounterMetrics.Runtime.GcHeapSizeMB.Max)MB" } else { 'N/A' }
        Write-Information "  CPU avg:     $cpuAvg" -InformationAction Continue
        Write-Information "  GC heap max: $heapMax" -InformationAction Continue
    }

    Write-Information "  Saved to:    $baselinePath" -InformationAction Continue

    # ── Step 4b: Run all remaining scenarios for baseline capture ───────────
    Write-Information '' -InformationAction Continue
    Write-Information 'Running additional scenarios for baseline capture...' -InformationAction Continue

    $allScenarioResults = & (Join-Path $PSScriptRoot 'Invoke-AllScaleTests.ps1') `
        -ConfigPath $ConfigPath -Iteration 0 -SkipPrimary

    foreach ($sr in $allScenarioResults) {
        if ($sr.Metrics) {
            $scenarioBaselinePath = Join-Path $repoRoot $config.Api.ResultsPath "baseline-$($sr.ScenarioName).json"
            $sr.Metrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $scenarioBaselinePath -Encoding utf8

            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'baseline' -Level 'info' `
                -Message "Scenario baseline saved: $($sr.ScenarioName) — p95: $($sr.Metrics.HttpReqDuration.P95)ms" `
                -Iteration 0
        }
        else {
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'baseline' -Level 'warning' `
                -Message "Scenario '$($sr.ScenarioName)' produced no metrics" `
                -Iteration 0
        }
    }

    Write-Information '' -InformationAction Continue
    Write-Information "All scenario baselines captured ($($allScenarioResults.Count) additional scenarios)" -InformationAction Continue
}
finally {
    # ── Step 5: Stop API ────────────────────────────────────────────────────
    & (Join-Path $PSScriptRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
}
