<#
.SYNOPSIS
    Runs k6 scale tests and parses the results.

.DESCRIPTION
    Executes a k6 scenario against the running API, captures the JSON summary
    output, and returns a structured performance metrics object.

    For the primary scenario (no ScenarioName), supports:
    - Warmup: a short 1-VU pass that warms up the application before measured runs
    - Multi-run: runs the scenario N times and returns the median result

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Experiment
    Current experiment number for logging and file naming.

.PARAMETER ScenarioPath
    Override the scenario path from config. Optional.

.PARAMETER ScenarioName
    Logical name for the scenario (e.g. 'stress-products'). When provided the
    k6 summary file is written as k6-summary-{ScenarioName}-experiment-{N}.json
    and .NET counter collection is skipped (counters are only gathered for the
    primary optimization scenario). Warmup and multi-run are also skipped.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [int]$Experiment = 0,
    [string]$ScenarioPath,
    [string]$ScenarioName
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

if (-not $ScenarioPath) {
    $ScenarioPath = Join-Path $repoRoot $config.ScaleTest.ScenarioPath
}

$outputDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
if ($ScenarioName) {
    $jsonSummaryPath = Join-Path $outputDir "k6-summary-$ScenarioName.json"
} else {
    $jsonSummaryPath = Join-Path $outputDir "k6-summary.json"
}
$baseUrl = $config.Api.BaseUrl

# Ensure output directory exists
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' `
    -Message "Running k6 scenario: $ScenarioPath against $baseUrl" `
    -Experiment $Experiment

# ── Pre-flight: verify k6 is available ──────────────────────────────────────
if (-not (Get-Command 'k6' -ErrorAction SilentlyContinue)) {
    $msg = 'k6 is not on PATH — cannot run scale tests'
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'error' -Message $msg -Experiment $Experiment
    throw $msg
}

# ── Pre-flight: verify API is healthy ───────────────────────────────────────
$healthEndpoint = $config.Api.HealthEndpoint
if ($healthEndpoint) {
    $healthUrl = "$baseUrl$healthEndpoint"
    $healthOk = $false
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            $healthRes = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 3 -ErrorAction Stop
            if ($healthRes.status -eq 'healthy') {
                $healthOk = $true
                break
            }
        } catch {
            Write-Verbose "Health check attempt $attempt/5 — not ready"
        }
        Start-Sleep -Seconds 2
    }
    if (-not $healthOk) {
        $msg = "API is not healthy at $healthUrl — cannot run scale test '$ScenarioName'"
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'error' -Message $msg -Experiment $Experiment
        throw $msg
    }
}

# ── Warmup pass (primary scenario only) ─────────────────────────────────────
$isPrimary = -not $ScenarioName
$warmupEnabled = $isPrimary -and $config.ScaleTest.WarmupEnabled -and $config.ScaleTest.WarmupScenarioPath

if ($warmupEnabled) {
    $warmupPath = Join-Path $repoRoot $config.ScaleTest.WarmupScenarioPath
    if (Test-Path $warmupPath) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' `
            -Message 'Running warmup pass' `
            -Experiment $Experiment

        $warmupArgs = @('run', '--env', "BASE_URL=$baseUrl", '--quiet', $warmupPath)
        & k6 @warmupArgs 2>&1 | Out-Null

        # Cooldown after warmup — let GC, thread pool, and TCP connections settle
        $cooldown = if ($config.ScaleTest.CooldownSeconds) { [int]$config.ScaleTest.CooldownSeconds } else { 3 }
        & (Join-Path $PSScriptRoot 'Invoke-Cooldown.ps1') `
            -BaseUrl $baseUrl `
            -GcEndpoint $config.Api.GcEndpoint `
            -CooldownSeconds $cooldown `
            -Experiment $Experiment `
            -Reason 'after warmup'
    }
    else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "Warmup scenario not found at $warmupPath — skipping" `
            -Experiment $Experiment
    }
}

# ── Start .NET counter collection if enabled (primary scenario only) ─────
$counterHandle = $null
$counterMetrics = $null
$countersEnabled = $config.DotnetCounters -and $config.DotnetCounters.Enabled -and (-not $ScenarioName)

if ($countersEnabled) {
    # Discover the API process PID from the base URL port
    $apiPort = ([Uri]$baseUrl).Port
    $apiProcess = Get-NetTCPConnection -LocalPort $apiPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($apiProcess) {
        $counterHandle = & (Join-Path $PSScriptRoot 'Start-DotnetCounters.ps1') `
            -ProcessId $apiProcess.OwningProcess `
            -ConfigPath $ConfigPath `
            -Experiment $Experiment

        if (-not $counterHandle.Success) {
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'measure' -Level 'warning' `
                -Message 'Counter collection failed to start — continuing without counters' `
                -Experiment $Experiment
            $counterHandle = $null
        }
    }
    else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "Could not find API process listening on port $apiPort — skipping counter collection" `
            -Experiment $Experiment
    }
}

# Build k6 arguments
$k6Args = @(
    'run'
    '--env', "BASE_URL=$baseUrl"
    '--summary-export', $jsonSummaryPath
)

# Add any extra args from config
if ($config.ScaleTest.ExtraArgs) {
    $k6Args += $config.ScaleTest.ExtraArgs
}

$k6Args += $ScenarioPath

# ── Determine number of measured runs ───────────────────────────────────────
$measuredRuns = 1
if ($isPrimary -and $config.ScaleTest.MeasuredRuns -and $config.ScaleTest.MeasuredRuns -gt 1) {
    $measuredRuns = [int]$config.ScaleTest.MeasuredRuns
}

$allRunMetrics = @()
$k6ExitCode = 0
$k6Output = ''

for ($run = 1; $run -le $measuredRuns; $run++) {
    # Cooldown between runs (skip before the first run)
    if ($run -gt 1 -and $measuredRuns -gt 1) {
        $cooldown = if ($config.ScaleTest.CooldownSeconds) { [int]$config.ScaleTest.CooldownSeconds } else { 3 }
        & (Join-Path $PSScriptRoot 'Invoke-Cooldown.ps1') `
            -BaseUrl $baseUrl `
            -GcEndpoint $config.Api.GcEndpoint `
            -CooldownSeconds $cooldown `
            -Experiment $Experiment `
            -Reason 'between runs'
    }

    if ($measuredRuns -gt 1) {
        # Use per-run summary file, copy the winning run to the canonical path later
        $runSummaryPath = Join-Path $outputDir "k6-summary-run$run.json"
        $runArgs = $k6Args.Clone()
        # Replace the summary-export path for this run
        for ($i = 0; $i -lt $runArgs.Count; $i++) {
            if ($runArgs[$i] -eq '--summary-export' -and ($i + 1) -lt $runArgs.Count) {
                $runArgs[$i + 1] = $runSummaryPath
                break
            }
        }

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' `
            -Message "Measured run $run / $measuredRuns" `
            -Experiment $Experiment
    }
    else {
        $runArgs = $k6Args
        $runSummaryPath = $jsonSummaryPath
    }

    # Run k6
    $k6Output = & k6 @runArgs 2>&1
    $k6ExitCode = $LASTEXITCODE

    # Parse the JSON summary for this run
    if (Test-Path $runSummaryPath) {
        $summary = Get-Content $runSummaryPath -Raw | ConvertFrom-Json

        $runMetrics = [ordered]@{
            Timestamp       = (Get-Date -Format 'o')
            Experiment       = $Experiment
            Run             = $run
            HttpReqDuration = [ordered]@{
                Avg = $summary.metrics.http_req_duration.avg
                P50 = $summary.metrics.http_req_duration.med
                P90 = $summary.metrics.http_req_duration.'p(90)'
                P95 = $summary.metrics.http_req_duration.'p(95)'
                P99 = $summary.metrics.http_req_duration.'p(99)'
                Max = $summary.metrics.http_req_duration.max
            }
            HttpReqs        = [ordered]@{
                Count = $summary.metrics.http_reqs.count
                Rate  = $summary.metrics.http_reqs.rate
            }
            HttpReqFailed   = [ordered]@{
                Count = [int]($summary.metrics.http_req_failed.passes ?? 0)
                Rate  = $summary.metrics.http_req_failed.value ?? 0
            }
            SummaryPath     = $runSummaryPath
        }

        $allRunMetrics += $runMetrics

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' `
            -Message "Run $run — p95: $($runMetrics.HttpReqDuration.P95)ms, RPS: $([math]::Round($runMetrics.HttpReqs.Rate, 1))" `
            -Experiment $Experiment `
            -Data @{
                run       = $run
                p95       = $runMetrics.HttpReqDuration.P95
                rps       = $runMetrics.HttpReqs.Rate
                errorRate = $runMetrics.HttpReqFailed.Rate
            }
    }
    else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'error' `
            -Message "k6 summary file not found at: $runSummaryPath (run $run)" `
            -Experiment $Experiment
    }
}

# ── Select median result ────────────────────────────────────────────────────
$metrics = $null
if ($allRunMetrics.Count -gt 0) {
    if ($allRunMetrics.Count -eq 1) {
        $selectedRun = $allRunMetrics[0]
    }
    else {
        # Sort by p95 and pick the median (middle) run
        $sorted = $allRunMetrics | Sort-Object { $_.HttpReqDuration.P95 }
        $medianIndex = [math]::Floor($sorted.Count / 2)
        $selectedRun = $sorted[$medianIndex]

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' `
            -Message ("Median selected: run {0} — p95: {1}ms (from {2} runs: {3})" -f `
                $selectedRun.Run, `
                $selectedRun.HttpReqDuration.P95, `
                $allRunMetrics.Count, `
                (($sorted | ForEach-Object { '{0}ms' -f $_.HttpReqDuration.P95 }) -join ', ')) `
            -Experiment $Experiment
    }

    # Copy the winning run's summary to the canonical path
    if ($measuredRuns -gt 1 -and $selectedRun.SummaryPath -ne $jsonSummaryPath) {
        Copy-Item -Path $selectedRun.SummaryPath -Destination $jsonSummaryPath -Force
    }

    # Build final metrics (without the per-run helper fields)
    $metrics = [ordered]@{
        Timestamp       = $selectedRun.Timestamp
        Experiment       = $selectedRun.Experiment
        HttpReqDuration = $selectedRun.HttpReqDuration
        HttpReqs        = $selectedRun.HttpReqs
        HttpReqFailed   = $selectedRun.HttpReqFailed
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "k6 completed — p95: $($metrics.HttpReqDuration.P95)ms, RPS: $([math]::Round($metrics.HttpReqs.Rate, 1))" `
        -Experiment $Experiment `
        -Data @{
            p95       = $metrics.HttpReqDuration.P95
            rps       = $metrics.HttpReqs.Rate
            errorRate = $metrics.HttpReqFailed.Rate
            runs      = $allRunMetrics.Count
        }
}
else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'error' `
        -Message 'No successful k6 runs produced metrics' `
        -Experiment $Experiment
}

# ── Stop .NET counter collection ────────────────────────────────────────────
if ($counterHandle) {
    $counterMetrics = & (Join-Path $PSScriptRoot 'Stop-DotnetCounters.ps1') `
        -CounterHandle $counterHandle `
        -Experiment $Experiment
}

$result = [ordered]@{
    Success        = ($null -ne $metrics)
    ExitCode       = $k6ExitCode
    Metrics        = if ($metrics) { [PSCustomObject]$metrics } else { $null }
    CounterMetrics = $counterMetrics
    SummaryPath    = $jsonSummaryPath
    Output         = ($k6Output | Out-String)
    RunCount       = $allRunMetrics.Count
    RunMetrics     = $allRunMetrics   # per-run p95/RPS for variance analysis
}

return [PSCustomObject]$result
