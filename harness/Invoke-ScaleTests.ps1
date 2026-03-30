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
    [string]$TargetDir,
    [int]$Experiment = 0,
    [string]$ScenarioPath,
    [string]$ScenarioName,
    [string]$BaseUrl,
    [switch]$SkipHealthCheck,
    [System.Diagnostics.Process]$InitialProcess
)

$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath

# Merge target config when an external target directory is provided
if ($TargetDir) {
    $targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone' 'config.psd1'
    if (Test-Path $targetConfigPath) {
        $targetCfg = Import-PowerShellDataFile -Path $targetConfigPath
        $config = Merge-HoneConfig -Engine $config -Target $targetCfg
    }
}

$pathBase = if ($TargetDir) { $TargetDir } else { $repoRoot }
$fixture = Get-HarnessTestingFixture -Config $config -TargetDir $TargetDir
$fixtureScale = $null
if ($fixture) {
    if ($ScenarioName) {
        $fixtureScale = Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Scale', 'Scenarios', $ScenarioName) -Experiment $Experiment
    }

    if (-not $fixtureScale) {
        $fixtureScale = Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Scale', 'Primary') -Experiment $Experiment
    }
}

# Seed the local config with the caller's process reference so the first
# between-run Stop can terminate the initial API (which the caller started).
if ($InitialProcess) { $config._Process = $InitialProcess }

if (-not $ScenarioPath) {
    $ScenarioPath = Join-Path $pathBase $config.ScaleTest.ScenarioPath
}

$outputDir = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath "experiment-$Experiment"
if ($ScenarioName) {
    $jsonSummaryPath = Join-Path $outputDir "k6-summary-$ScenarioName.json"
} else {
    $jsonSummaryPath = Join-Path $outputDir "k6-summary.json"
}
$baseUrl = if ($BaseUrl) { $BaseUrl } else { $config.Api.BaseUrl }

# Ensure output directory exists
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' `
    -Message "Running k6 scenario: $ScenarioPath against $baseUrl" `
    -Experiment $Experiment

. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

# ── Pre-flight: verify k6 is available ──────────────────────────────────────
if (-not $fixtureScale -and -not (Get-Command 'k6' -ErrorAction SilentlyContinue)) {
    $msg = 'k6 is not on PATH — cannot run scale tests'
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'error' -Message $msg -Experiment $Experiment
    throw $msg
}

# ── Pre-flight: verify API is healthy ───────────────────────────────────────
if (-not $fixtureScale -and -not $SkipHealthCheck) {
    $healthEndpoint = $config.Api.HealthEndpoint
    if ($healthEndpoint) {
        $healthUrl = "$baseUrl$healthEndpoint"
        $healthOk = Wait-ApiHealthy -HealthUrl $healthUrl -TimeoutSec 10 -IntervalSec 2
        if (-not $healthOk) {
            $msg = "API is not healthy at $healthUrl — cannot run scale test '$ScenarioName'"
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'measure' -Level 'error' -Message $msg -Experiment $Experiment
            throw $msg
        }
    }
}

# ── Warmup pass (primary scenario only) ─────────────────────────────────────
$isPrimary = -not $ScenarioName
$warmupEnabled = $isPrimary -and $config.ScaleTest.WarmupEnabled -and $config.ScaleTest.WarmupScenarioPath

if (-not $fixtureScale -and $warmupEnabled) {
    $warmupPath = Join-Path $pathBase $config.ScaleTest.WarmupScenarioPath
    if (Test-Path $warmupPath) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' `
            -Message 'Running warmup pass' `
            -Experiment $Experiment

        $warmupArgs = @('run', '--env', "BASE_URL=$baseUrl", '--quiet', $warmupPath)
        $warmupSpinner = Start-Spinner -Message 'Warming up API'
        try {
            & k6 @warmupArgs 2>&1 | Out-Null
        } finally {
            Stop-Spinner -Spinner $warmupSpinner -CompletionMessage 'Warmup complete'
        }

        # Cooldown after warmup — let GC, thread pool, and TCP connections settle
        $cooldown = if ($config.ScaleTest.CooldownSeconds) { [int]$config.ScaleTest.CooldownSeconds } else { 3 }
        & (Join-Path $PSScriptRoot 'Invoke-Cooldown.ps1') `
            -BaseUrl $baseUrl `
            -GcEndpoint $config.Api.GcEndpoint `
            -CooldownSeconds $cooldown `
            -Experiment $Experiment `
            -Reason 'after warmup'
    } else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "Warmup scenario not found at $warmupPath — skipping" `
            -Experiment $Experiment
    }
}

# ── Start .NET counter collection if enabled (primary scenario only) ─────
$counterHandle = $null
$counterMetrics = $null
$countersEnabled = $config.ContainsKey('DotnetCounters') -and $config.DotnetCounters.Enabled -and (-not $ScenarioName)

if (-not $fixtureScale -and $countersEnabled) {
    # Discover the API process PID from the base URL port
    $apiPort = ([Uri]$baseUrl).Port
    $apiProcess = Get-NetTCPConnection -LocalPort $apiPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($apiProcess) {
        $counterHandle = & (Join-Path $PSScriptRoot 'Start-DotnetCounters.ps1') `
            -ProcessId $apiProcess.OwningProcess `
            -ConfigPath $ConfigPath `
            -OutputPath (Join-Path $outputDir 'dotnet-counters.csv') `
            -Experiment $Experiment

        if (-not $counterHandle.Success) {
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'measure' -Level 'warning' `
                -Message 'Counter collection failed to start — continuing without counters' `
                -Experiment $Experiment
            $counterHandle = $null
        }
    } else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "Could not find API process listening on port $apiPort — skipping counter collection" `
            -Experiment $Experiment
    }
}

try {
    if ($fixtureScale) {
        $runSummaryPaths = @()
        if ($fixtureScale.ContainsKey('RunSummaryPaths') -and $fixtureScale.RunSummaryPaths) {
            $runSummaryPaths = @($fixtureScale.RunSummaryPaths)
        } elseif ($fixtureScale.ContainsKey('SummaryPath') -and $fixtureScale.SummaryPath) {
            $runSummaryPaths = @($fixtureScale.SummaryPath)
        }

        $allRunMetrics = @()
        $k6ExitCode = if ($fixtureScale.ContainsKey('ExitCode')) { [int]$fixtureScale.ExitCode } else { 0 }
        $k6Output = if ($fixtureScale.ContainsKey('Output')) { $fixtureScale.Output } else { 'Fixture scale test completed' }

        for ($run = 0; $run -lt $runSummaryPaths.Count; $run++) {
            $sourceSummaryPath = Resolve-HarnessTestingFixturePath -Fixture $fixture -Path $runSummaryPaths[$run]
            if (-not (Test-Path -Path $sourceSummaryPath)) {
                throw "Fixture scale summary not found: $sourceSummaryPath"
            }

            $runNumber = $run + 1
            $runSummaryPath = if ($runSummaryPaths.Count -gt 1) {
                Join-Path -Path $outputDir -ChildPath "k6-summary-run$runNumber.json"
            } else {
                $jsonSummaryPath
            }

            Copy-Item -Path $sourceSummaryPath -Destination $runSummaryPath -Force
            $summary = Get-Content -Path $runSummaryPath -Raw | ConvertFrom-Json
            $allRunMetrics += Convert-HoneK6SummaryToMetricSet -Summary $summary -Experiment $Experiment -Run $runNumber -SummaryPath $runSummaryPath
        }

        if ($allRunMetrics.Count -gt 0) {
            if ($allRunMetrics.Count -eq 1) {
                $selectedRun = $allRunMetrics[0]
            } else {
                $sorted = $allRunMetrics | Sort-Object { $_.HttpReqDuration.P95 }
                $medianIndex = [math]::Floor($sorted.Count / 2)
                $selectedRun = $sorted[$medianIndex]
                if ($selectedRun.SummaryPath -ne $jsonSummaryPath) {
                    Copy-Item -Path $selectedRun.SummaryPath -Destination $jsonSummaryPath -Force
                }
            }

            $metrics = [ordered]@{
                Timestamp = $selectedRun.Timestamp
                Experiment = $selectedRun.Experiment
                HttpReqDuration = $selectedRun.HttpReqDuration
                HttpReqs = $selectedRun.HttpReqs
                HttpReqFailed = $selectedRun.HttpReqFailed
            }
        } else {
            $metrics = $null
        }

        if ($fixtureScale.ContainsKey('CounterMetricsPath') -and $fixtureScale.CounterMetricsPath) {
            $counterMetricsPath = Resolve-HarnessTestingFixturePath -Fixture $fixture -Path $fixtureScale.CounterMetricsPath
            if (-not (Test-Path -Path $counterMetricsPath)) {
                throw "Fixture counter metrics not found: $counterMetricsPath"
            }

            $counterMetrics = Get-Content -Path $counterMetricsPath -Raw | ConvertFrom-Json
            Copy-Item -Path $counterMetricsPath -Destination (Join-Path -Path $outputDir -ChildPath 'dotnet-counters.json') -Force
        } elseif ($fixtureScale.ContainsKey('CounterMetrics') -and $fixtureScale.CounterMetrics) {
            $counterMetrics = [PSCustomObject]$fixtureScale.CounterMetrics
            $counterMetrics | ConvertTo-Json -Depth 10 | Out-File -FilePath (Join-Path -Path $outputDir -ChildPath 'dotnet-counters.json') -Encoding utf8
        } else {
            $counterMetrics = $null
        }

        $fixtureLogName = if ($ScenarioName) { "k6-$ScenarioName.log" } else { 'k6.log' }
        ($k6Output | Out-String) | Out-File -FilePath (Join-Path -Path $outputDir -ChildPath $fixtureLogName) -Encoding utf8

        $result = [ordered]@{
            Success = ($null -ne $metrics -and $k6ExitCode -eq 0)
            ExitCode = $k6ExitCode
            Metrics = if ($metrics) { [PSCustomObject]$metrics } else { $null }
            CounterMetrics = $counterMetrics
            SummaryPath = $jsonSummaryPath
            Output = ($k6Output | Out-String)
            RunCount = $allRunMetrics.Count
            RunMetrics = $allRunMetrics
            LastProcess = $config._Process
            LastBaseUrl = $config._BaseUrl
        }

        return [PSCustomObject]$result
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
        # Reset state between runs (skip before the first run)
        if ($run -gt 1 -and $measuredRuns -gt 1) {
            if ($TargetDir -and $targetCfg) {
                # Stop → Prepare → Start cycle: the API must be stopped before
                # the database is dropped, otherwise EF Core's cached DbContext
                # pool holds stale connections to the deleted DB and all
                # subsequent VUs hit cascading errors.
                & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                    -Phase 'measure' -Level 'info' `
                    -Message "Restarting API before run $run (Stop → Prepare → Start)" `
                    -Experiment $Experiment

                Invoke-LifecycleHook -Name 'Stop' -TargetConfig $targetCfg `
                    -TargetDir $TargetDir -HarnessRoot $PSScriptRoot `
                    -Config $config -Experiment $Experiment | Out-Null

                Invoke-LifecycleHook -Name 'Prepare' -TargetConfig $targetCfg `
                    -TargetDir $TargetDir -HarnessRoot $PSScriptRoot `
                    -Config $config -Experiment $Experiment | Out-Null

                $restartResult = Invoke-LifecycleHook -Name 'Start' -TargetConfig $targetCfg `
                    -TargetDir $TargetDir -HarnessRoot $PSScriptRoot `
                    -Config $config -Experiment $Experiment

                if (-not $restartResult.Success) {
                    throw "API failed to restart before measured run ${run}: $($restartResult.Message)"
                }

                $baseUrl = if ($restartResult.ActualBaseUrl) { $restartResult.ActualBaseUrl } else { $baseUrl }
                $config._Process = $restartResult.Process
                $config._BaseUrl = $baseUrl

                # Update k6 args with the new base URL (port changes on restart)
                for ($i = 0; $i -lt $k6Args.Count; $i++) {
                    if ($k6Args[$i] -eq '--env' -and ($i + 1) -lt $k6Args.Count -and $k6Args[$i + 1] -like 'BASE_URL=*') {
                        $k6Args[$i + 1] = "BASE_URL=$baseUrl"
                        break
                    }
                }
            } else {
                # No target lifecycle hooks — fall back to cooldown only
                $cooldown = if ($config.ScaleTest.CooldownSeconds) { [int]$config.ScaleTest.CooldownSeconds } else { 3 }
                & (Join-Path $PSScriptRoot 'Invoke-Cooldown.ps1') `
                    -BaseUrl $baseUrl `
                    -GcEndpoint $config.Api.GcEndpoint `
                    -CooldownSeconds $cooldown `
                    -Experiment $Experiment `
                    -Reason 'between runs'
            }
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
        } else {
            $runArgs = $k6Args
            $runSummaryPath = $jsonSummaryPath
        }

        # Run k6
        $runLabel = if ($measuredRuns -gt 1) { "k6 run $run/$measuredRuns" } else { 'k6 scale test' }
        $runSpinner = Start-Spinner -Message $runLabel
        try {
            $k6Output = & k6 @runArgs 2>&1
            $k6ExitCode = $LASTEXITCODE
        } catch {
            Stop-Spinner -Spinner $runSpinner -CompletionMessage $null
            throw
        }

        # Save k6 console output to experiment directory
        $k6LogName = if ($measuredRuns -gt 1) { "k6-run$run.log" } elseif ($ScenarioName) { "k6-$ScenarioName.log" } else { 'k6.log' }
        ($k6Output | Out-String) | Out-File -FilePath (Join-Path $outputDir $k6LogName) -Encoding utf8

        # Parse the JSON summary for this run
        if (Test-Path $runSummaryPath) {
            $summary = Get-Content $runSummaryPath -Raw | ConvertFrom-Json

            $runMetrics = [ordered]@{
                Timestamp = (Get-Date -Format 'o')
                Experiment = $Experiment
                Run = $run
                HttpReqDuration = [ordered]@{
                    Avg = $summary.metrics.http_req_duration.avg
                    P50 = $summary.metrics.http_req_duration.med
                    P90 = $summary.metrics.http_req_duration.'p(90)'
                    P95 = $summary.metrics.http_req_duration.'p(95)'
                    P99 = $summary.metrics.http_req_duration.'p(99)'
                    Max = $summary.metrics.http_req_duration.max
                }
                HttpReqs = [ordered]@{
                    Count = $summary.metrics.http_reqs.count
                    Rate = $summary.metrics.http_reqs.rate
                }
                HttpReqFailed = [ordered]@{
                    Count = [int]($summary.metrics.http_req_failed.passes ?? 0)
                    Rate = $summary.metrics.http_req_failed.value ?? 0
                }
                SummaryPath = $runSummaryPath
            }

            $allRunMetrics += $runMetrics

            $runP95 = [math]::Round($runMetrics.HttpReqDuration.P95, 1)
            $runRps = [math]::Round($runMetrics.HttpReqs.Rate, 1)
            Stop-Spinner -Spinner $runSpinner -CompletionMessage "$runLabel — p95: ${runP95}ms, RPS: $runRps"

            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'measure' -Level 'info' `
                -Message "Run $run — p95: $($runMetrics.HttpReqDuration.P95)ms, RPS: $([math]::Round($runMetrics.HttpReqs.Rate, 1))" `
                -Experiment $Experiment `
                -Data @{
                run = $run
                p95 = $runMetrics.HttpReqDuration.P95
                rps = $runMetrics.HttpReqs.Rate
                errorRate = $runMetrics.HttpReqFailed.Rate
            }
        } else {
            Stop-Spinner -Spinner $runSpinner -CompletionMessage "$runLabel — no summary file"
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
        } else {
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
            Timestamp = $selectedRun.Timestamp
            Experiment = $selectedRun.Experiment
            HttpReqDuration = $selectedRun.HttpReqDuration
            HttpReqs = $selectedRun.HttpReqs
            HttpReqFailed = $selectedRun.HttpReqFailed
        }

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' `
            -Message "k6 completed — p95: $($metrics.HttpReqDuration.P95)ms, RPS: $([math]::Round($metrics.HttpReqs.Rate, 1))" `
            -Experiment $Experiment `
            -Data @{
            p95 = $metrics.HttpReqDuration.P95
            rps = $metrics.HttpReqs.Rate
            errorRate = $metrics.HttpReqFailed.Rate
            runs = $allRunMetrics.Count
        }
    } else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'error' `
            -Message 'No successful k6 runs produced metrics' `
            -Experiment $Experiment
    }

} finally {
    # ── Stop .NET counter collection ────────────────────────────────────────────
    if ($counterHandle) {
        $counterMetrics = & (Join-Path $PSScriptRoot 'Stop-DotnetCounters.ps1') `
            -CounterHandle $counterHandle `
            -Experiment $Experiment
    }
}

$result = [ordered]@{
    Success = ($null -ne $metrics)
    ExitCode = $k6ExitCode
    Metrics = if ($metrics) { [PSCustomObject]$metrics } else { $null }
    CounterMetrics = $counterMetrics
    SummaryPath = $jsonSummaryPath
    Output = ($k6Output | Out-String)
    RunCount = $allRunMetrics.Count
    RunMetrics = $allRunMetrics   # per-run p95/RPS for variance analysis
    LastProcess = $config._Process  # propagate the latest process after between-run restarts
    LastBaseUrl = $config._BaseUrl  # propagate the latest base URL after restarts
}

return [PSCustomObject]$result
