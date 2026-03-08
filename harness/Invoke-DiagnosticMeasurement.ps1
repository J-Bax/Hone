<#
.SYNOPSIS
    Runs a full diagnostic measurement pass with all enabled profiling plugins.

.DESCRIPTION
    Orchestrates the complete diagnostic profiling pipeline:
      1. Reset database (clean state)
      2. Start API
      3. Start all enabled collector plugins
      4. Run a single k6 load-test pass (diagnostic scenario)
      5. Stop all collectors and export data
      6. Stop API
      7. Run all enabled analyzer plugins against collector data
      8. Return aggregated profiling reports

    This measurement is separate from the evaluation measurement
    (Invoke-ScaleTests.ps1).  Profiling tools such as PerfView add
    overhead that biases latency / throughput numbers, so the results
    are used ONLY as analysis input — never for accept/reject decisions.

.PARAMETER Experiment
    Current experiment number.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER CurrentMetrics
    Current performance metrics (for analysis context).

.OUTPUTS
    @{
        Success          = $true
        CollectorData    = @{ 'perfview-cpu' = @{…}; 'perfview-gc' = @{…}; … }
        AnalyzerReports  = @{ 'cpu-hotspots' = @{…}; 'memory-gc' = @{…}; … }
    }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$Experiment,

    [string]$ConfigPath,

    $CurrentMetrics
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$harnessRoot = $PSScriptRoot
$repoRoot    = Split-Path -Parent $harnessRoot

# ── Load config ─────────────────────────────────────────────────────────────
if (-not $ConfigPath) { $ConfigPath = Join-Path $harnessRoot 'config.psd1' }
$config = Import-PowerShellDataFile $ConfigPath

if (-not $config.Diagnostics -or -not $config.Diagnostics.Enabled) {
    throw 'Diagnostics are not enabled in config. Set Diagnostics.Enabled = $true.'
}

$diagnostics = $config.Diagnostics
$resultsDir  = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment" 'diagnostics'

if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

Write-Information '' -InformationAction Continue
Write-Information '  ┌── Diagnostic Measurement ──────────────────────────┐' -InformationAction Continue

# ── Step 1: Reset database ──────────────────────────────────────────────────
Write-Information '  │ Resetting database...' -InformationAction Continue
& (Join-Path $harnessRoot 'Reset-Database.ps1') -ConfigPath $ConfigPath

# ── Step 2: Start API ──────────────────────────────────────────────────────
Write-Information '  │ Starting API...' -InformationAction Continue
$apiResult = & (Join-Path $harnessRoot 'Start-SampleApi.ps1') -ConfigPath $ConfigPath

if (-not $apiResult.Success) {
    throw "Diagnostic measurement failed: API did not start. Check Start-SampleApi.ps1 output."
}

try {
    # ── Step 3: Discover API process ID ──────────────────────────────────────
    $apiPort = ([uri]$config.Api.BaseUrl).Port
    $apiConn = Get-NetTCPConnection -LocalPort $apiPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $apiPid = if ($apiConn) { $apiConn.OwningProcess } else { $apiResult.Process.Id }
    $apiProcessName = (Get-Process -Id $apiPid -ErrorAction SilentlyContinue).ProcessName ?? 'dotnet'

    Write-Information "  │ API running (PID: $apiPid, process: $apiProcessName)" -InformationAction Continue

    # ── Step 4: Start all collectors ─────────────────────────────────────────
    Write-Information '  │ Starting collectors...' -InformationAction Continue
    $startResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticCollection.ps1') `
        -Action 'Start' `
        -ProcessId $apiPid `
        -OutputDir $resultsDir `
        -Config $config

    if ($startResult.Handles.Count -eq 0) {
        throw "Diagnostic measurement failed: no collectors started successfully."
    }

    $startedCollectors = ($startResult.Handles.Keys -join ', ')
    if (-not $startResult.Success) {
        Write-Warning "  │ Some collectors failed to start — continuing with: $startedCollectors"
    }
    else {
        Write-Information "  │ Collectors active: $startedCollectors" -InformationAction Continue
    }

    # Allow collectors to stabilise
    Start-Sleep -Seconds 2

    # ── Step 5: Run k6 diagnostic pass ──────────────────────────────────────
    $scenarioPath = $diagnostics.DiagnosticScenarioPath ?? $config.ScaleTest.ScenarioPath
    $scenarioFullPath = Join-Path $repoRoot $scenarioPath
    $baseUrl = $config.Api.BaseUrl
    $diagnosticRuns = $diagnostics.DiagnosticRuns ?? 1

    Write-Information "  │ Running k6 ($diagnosticRuns run(s))..." -InformationAction Continue

    for ($run = 1; $run -le $diagnosticRuns; $run++) {
        if ($run -gt 1) {
            & (Join-Path $harnessRoot 'Invoke-Cooldown.ps1') -CooldownSeconds 3 -ConfigPath $ConfigPath
        }

        $k6SummaryPath = Join-Path $resultsDir "k6-diagnostic-run$run.json"
        & k6 run --env "BASE_URL=$baseUrl" --summary-export $k6SummaryPath --quiet $scenarioFullPath 2>&1 | Out-Null
    }

    Write-Information '  │ k6 diagnostic pass complete' -InformationAction Continue

    # ── Step 6: Stop all collectors ─────────────────────────────────────────
    Write-Information '  │ Stopping collectors...' -InformationAction Continue
    $stopResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticCollection.ps1') `
        -Action 'Stop' `
        -Config $config `
        -Handles $startResult.Handles

    if (-not $stopResult.Success) {
        Write-Warning "  │ Some collectors failed to stop cleanly — continuing with available data"
    }

    # ── Step 7: Export collector data ────────────────────────────────────────
    Write-Information '  │ Exporting collector data...' -InformationAction Continue
    $exportResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticCollection.ps1') `
        -Action 'Export' `
        -Config $config `
        -ArtifactMap $stopResult.ArtifactMap `
        -OutputDir $resultsDir `
        -ProcessName $apiProcessName

    if (-not $exportResult.Success) {
        Write-Warning "  │ Some collectors failed to export data — continuing with available data"
    }

    $collectorData = $exportResult.CollectorData
    $exportedCollectors = ($collectorData.Keys -join ', ')
    Write-Information "  │ Exported: $exportedCollectors" -InformationAction Continue
}
finally {
    # ── Step 8: Stop API (always) ───────────────────────────────────────────
    Write-Information '  │ Stopping API...' -InformationAction Continue
    & (Join-Path $harnessRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
}

# ── Step 9: Run analyzers ───────────────────────────────────────────────────
Write-Information '  │ Running analyzers...' -InformationAction Continue
$analysisResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticAnalysis.ps1') `
    -CollectorData $collectorData `
    -CurrentMetrics $CurrentMetrics `
    -Experiment $Experiment `
    -Config $config `
    -OutputDir $resultsDir

if (-not $analysisResult.Success) {
    throw "Diagnostic measurement failed: one or more analyzers failed."
}

$analyzerNames = ($analysisResult.Reports.Keys -join ', ')
Write-Information "  │ Analyzers complete: $analyzerNames" -InformationAction Continue
Write-Information '  └──────────────────────────────────────────────────────┘' -InformationAction Continue

return @{
    Success         = $true
    CollectorData   = $collectorData
    AnalyzerReports = $analysisResult.Reports
}
