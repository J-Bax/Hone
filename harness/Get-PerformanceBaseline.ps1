<#
.SYNOPSIS
    Establishes a performance baseline by running scale tests.

.DESCRIPTION
    Builds the API, starts it, runs the baseline k6 scenario, saves the results,
    and stops the API. The baseline results are stored under the target's results
    directory and used by Compare-Results.ps1 for comparison.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER TargetDir
    Root directory of the target project. Config paths are resolved relative
    to this directory and lifecycle hooks are used for Prepare/Start/Stop.

.PARAMETER TargetConfig
    The target project's .hone/config.psd1 hashtable. Required so lifecycle
    hooks can be resolved.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,

    [Parameter(Mandatory)]
    [string]$TargetDir,

    [Parameter(Mandatory)]
    [hashtable]$TargetConfig
)

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath

# Merge target config into engine config so hooks receive Api settings
$config = Merge-HoneConfig -Engine $config -Target $TargetConfig

$pathBase = $TargetDir

# ── Prerequisite check: k6 must be on PATH ──────────────────────────────────
if (-not (Get-Command 'k6' -ErrorAction SilentlyContinue)) {
    Write-Error 'k6 is not on PATH — install k6 or add its directory to PATH before running the baseline'
    return
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'baseline' -Level 'info' -Message 'Establishing performance baseline'

# ── Collect machine info ────────────────────────────────────────────────────
$machineInfo = & (Join-Path $PSScriptRoot 'Get-MachineInfo.ps1')
Write-Status "Machine: CPU: $($machineInfo.Cpu.Name) ($($machineInfo.Cpu.LogicalProcessors) logical cores) | RAM: $($machineInfo.Memory.TotalGB)GB"

# ── Step 1: Build ───────────────────────────────────────────────────────────
$buildResult = & (Join-Path $PSScriptRoot 'Invoke-Build.ps1') -ConfigPath $ConfigPath -TargetDir $TargetDir
if (-not $buildResult.Success) {
    Write-Error 'Build failed — cannot establish baseline'
    return
}

# ── Step 1.5: Reset Database ────────────────────────────────────────────────
$null = Assert-LifecycleHookSucceeded -Name 'Prepare' -Result (
    Invoke-LifecycleHook -Name 'Prepare' -TargetConfig $TargetConfig -TargetDir $TargetDir `
        -HarnessRoot $PSScriptRoot -Config $config -Experiment 0
)

# ── Step 1.6: Clear optimization metadata from prior runs ───────────────────
# Experiment numbering restarts from 1 on each baseline, so stale metadata
# (experiment log, optimization queue, root-cause docs, event log) must be
# cleared to avoid duplicate experiment numbers and stale queue items.
$metadataDir = Join-Path $pathBase $config.Api.MetadataPath
$resultsDir = Join-Path $pathBase $config.Api.ResultsPath

$clearedFiles = @()
foreach ($file in @('experiment-log.md', 'experiment-queue.json', 'experiment-queue.md')) {
    $path = Join-Path $metadataDir $file
    if (Test-Path $path) { Remove-Item $path -Force; $clearedFiles += $file }
}
$rcDir = Join-Path $metadataDir 'root-causes'
if (Test-Path $rcDir) { Remove-Item $rcDir -Recurse -Force; $clearedFiles += 'root-causes/' }

$honeLog = Join-Path $resultsDir 'hone.jsonl'
if (Test-Path $honeLog) { Remove-Item $honeLog -Force; $clearedFiles += 'hone.jsonl' }

if ($clearedFiles.Count -gt 0) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'baseline' -Level 'info' `
        -Message "Cleared optimization metadata from prior run: $($clearedFiles -join ', ')" `
        -Experiment 0
}

# ── Step 2: Start API ──────────────────────────────────────────────────────
$apiResult = Assert-LifecycleHookSucceeded -Name 'Start' -Result (
    Invoke-LifecycleHook -Name 'Start' -TargetConfig $TargetConfig -TargetDir $TargetDir `
        -HarnessRoot $PSScriptRoot -Config $config
)
# Normalise base URL property (shared hooks return ActualBaseUrl)
$baseUrl = if ($apiResult.PSObject.Properties['ActualBaseUrl']) { $apiResult.ActualBaseUrl } elseif ($apiResult.PSObject.Properties['BaseUrl']) { $apiResult.BaseUrl } else { $null }
if ($baseUrl -and -not $apiResult.PSObject.Properties['BaseUrl']) {
    $apiResult | Add-Member -NotePropertyName BaseUrl -NotePropertyValue $baseUrl -Force
}
$config['_Process'] = $apiResult.Process
$config['_BaseUrl'] = $apiResult.BaseUrl
$null = Assert-LifecycleHookSucceeded -Name 'Ready' -Result (
    Invoke-LifecycleHook -Name 'Ready' -TargetConfig $TargetConfig -TargetDir $TargetDir `
        -HarnessRoot $PSScriptRoot -Config $config -BaseUrl $apiResult.BaseUrl -Experiment 0
)

try {
    # ── Step 3: Run scale tests ─────────────────────────────────────────────
    $scaleResult = Assert-LifecycleHookSucceeded -Name 'Active' -Result (
        Invoke-LifecycleHook -Name 'Active' -TargetConfig $TargetConfig -TargetDir $TargetDir `
            -HarnessRoot $PSScriptRoot -Config $config -BaseUrl $apiResult.BaseUrl -Experiment 0
    )

    if ($scaleResult.PSObject.Properties['LastProcess'] -and $scaleResult.LastProcess) { $config['_Process'] = $scaleResult.LastProcess }
    if ($scaleResult.PSObject.Properties['LastBaseUrl'] -and $scaleResult.LastBaseUrl) { $config['_BaseUrl'] = $scaleResult.LastBaseUrl }

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
            -Experiment 0
    }

    # ── Step 4: Save baseline ───────────────────────────────────────────────
    $baselinePath = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath 'baseline.json'
    $scaleResult.Metrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $baselinePath -Encoding utf8

    # Save machine info and run metadata
    $runMetadataPath = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath 'run-metadata.json'
    $runMetadata = [ordered]@{
        Machine = $machineInfo
        BaselineRun = [ordered]@{
            StartedAt = $machineInfo.CollectedAt
            CompletedAt = (Get-Date -Format 'o')
        }
        Experiments = @()
    }
    $runMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $runMetadataPath -Encoding utf8

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'baseline' -Level 'info' `
        -Message "Run metadata saved to: $runMetadataPath" `
        -Experiment 0

    # Save counter metrics baseline if available
    if ($scaleResult.CounterMetrics) {
        $counterBaselinePath = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath 'baseline-counters.json'
        $scaleResult.CounterMetrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $counterBaselinePath -Encoding utf8

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'baseline' -Level 'info' `
            -Message "Counter baseline saved to: $counterBaselinePath" `
            -Experiment 0
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'baseline' -Level 'info' `
        -Message "Baseline saved to: $baselinePath" `
        -Data @{
        p95 = $scaleResult.Metrics.HttpReqDuration.P95
        rps = $scaleResult.Metrics.HttpReqs.Rate
    }

    Write-Status "Baseline established:"
    Write-Status "  p95 latency: $($scaleResult.Metrics.HttpReqDuration.P95)ms"
    Write-Status "  RPS:         $([math]::Round($scaleResult.Metrics.HttpReqs.Rate, 1))"
    Write-Status "  Error rate:  $([math]::Round(($scaleResult.Metrics.HttpReqFailed.Rate) * 100, 2))%"

    if ($scaleResult.CounterMetrics) {
        $cpuAvg = if ($scaleResult.CounterMetrics.Runtime.CpuUsage) { "$($scaleResult.CounterMetrics.Runtime.CpuUsage.Avg)%" } else { 'N/A' }
        $heapMax = if ($scaleResult.CounterMetrics.Runtime.GcHeapSizeMB) { "$($scaleResult.CounterMetrics.Runtime.GcHeapSizeMB.Max)MB" } else { 'N/A' }
        Write-Status "  CPU avg:     $cpuAvg"
        Write-Status "  GC heap max: $heapMax"
    }

    Write-Status "  Saved to:    $baselinePath"

    # ── Step 4b: Run all remaining scenarios for baseline capture ───────────
    Write-Status ''
    Write-Status 'Running additional scenarios for baseline capture...'

    # Use the latest base URL from ScaleTests (already propagated above).
    $currentBaseUrl = if ($config._BaseUrl) { $config._BaseUrl } else { $apiResult.BaseUrl }

    $allScenarioResults = & (Join-Path $PSScriptRoot 'Invoke-AllScaleTests.ps1') `
        -ConfigPath $ConfigPath -TargetDir $TargetDir -Experiment 0 -SkipPrimary -BaseUrl $currentBaseUrl

    foreach ($sr in $allScenarioResults) {
        if ($sr.Metrics) {
            $scenarioBaselinePath = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath "baseline-$($sr.ScenarioName).json"
            $sr.Metrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $scenarioBaselinePath -Encoding utf8

            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'baseline' -Level 'info' `
                -Message "Scenario baseline saved: $($sr.ScenarioName) — p95: $($sr.Metrics.HttpReqDuration.P95)ms" `
                -Experiment 0
        } else {
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'baseline' -Level 'warning' `
                -Message "Scenario '$($sr.ScenarioName)' produced no metrics" `
                -Experiment 0
        }
    }

    Write-Status ''
    Write-Status "All scenario baselines captured ($($allScenarioResults.Count) additional scenarios)"
} finally {
    if ($config._BaseUrl) {
        $null = Invoke-LifecycleHook -Name 'Cooldown' -TargetConfig $TargetConfig -TargetDir $TargetDir `
            -HarnessRoot $PSScriptRoot -Config $config -BaseUrl $config._BaseUrl -Experiment 0
    }

    # ── Step 5: Stop API ────────────────────────────────────────────────────
    # _Process was propagated from the Active phase after between-run restarts,
    # so this Stop targets the correct (latest) API process and its children.
    $null = Invoke-LifecycleHook -Name 'Stop' -TargetConfig $TargetConfig -TargetDir $TargetDir `
        -HarnessRoot $PSScriptRoot -Config $config -BaseUrl $config._BaseUrl -Experiment 0

    $null = Invoke-LifecycleHook -Name 'Cleanup' -TargetConfig $TargetConfig -TargetDir $TargetDir `
        -HarnessRoot $PSScriptRoot -Config $config -BaseUrl $config._BaseUrl -Experiment 0
}
