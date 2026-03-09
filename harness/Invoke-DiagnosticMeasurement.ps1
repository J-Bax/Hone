<#
.SYNOPSIS
    Runs diagnostic measurement passes with all enabled profiling plugins.

.DESCRIPTION
    Orchestrates the diagnostic profiling pipeline with multi-pass support:

    Collectors are organized into groups (via the Group field in collector.psd1).
    Collectors in the same group run together in one pass; different groups get
    separate passes. 'default' group collectors (e.g., dotnet-counters) run in
    every pass since they're lightweight and non-interfering.

    Per group:
      1. Reset database (clean state)
      2. Start API
      3. Start collectors in this group
      4. Run k6 diagnostic scenario
      5. Stop collectors, export data
      6. Stop API

    After all passes:
      7. Run all analyzer plugins against merged collector data
      8. Return aggregated profiling reports

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

# ── Discover collector groups ───────────────────────────────────────────────
$groupResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticCollection.ps1') `
    -Action 'GetGroups' -Config $config
$groups = $groupResult.Groups

$groupNames = @($groups.Keys)
$totalPasses = $groupNames.Count
Write-Information "  │ Collection groups: $($groupNames -join ', ') ($totalPasses pass$(if ($totalPasses -ne 1) {'es'}))" -InformationAction Continue

# ── Merged collector data across all passes ─────────────────────────────────
$mergedCollectorData = @{}

$scenarioPath = $diagnostics.DiagnosticScenarioPath ?? $config.ScaleTest.ScenarioPath
$scenarioFullPath = Join-Path $repoRoot $scenarioPath
$diagnosticRuns = $diagnostics.DiagnosticRuns ?? 1
$passNumber = 0

foreach ($groupName in $groupNames) {
    $passNumber++
    $groupCollectors = $groups[$groupName]
    $collectorNames = @($groupCollectors | ForEach-Object { $_.Name })

    Write-Information "  │" -InformationAction Continue
    Write-Information "  │ ── Pass $passNumber/$totalPasses [$groupName]: $($collectorNames -join ', ')" -InformationAction Continue

    # ── Reset database ──────────────────────────────────────────────────────
    Write-Information '  │   Resetting database...' -InformationAction Continue
    & (Join-Path $harnessRoot 'Reset-Database.ps1') -ConfigPath $ConfigPath | Out-Null

    # ── Start API ───────────────────────────────────────────────────────────
    Write-Information '  │   Starting API...' -InformationAction Continue
    $apiResult = & (Join-Path $harnessRoot 'Start-SampleApi.ps1') -ConfigPath $ConfigPath

    if (-not $apiResult.Success) {
        Write-Warning "  │   API failed to start for pass $passNumber — skipping group '$groupName'"
        continue
    }

    try {
        # ── Discover API PID ────────────────────────────────────────────────
        $apiPort = ([uri]$apiResult.BaseUrl).Port
        $apiConn = Get-NetTCPConnection -LocalPort $apiPort -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $apiPid = if ($apiConn) { $apiConn.OwningProcess } else { $apiResult.Process.Id }
        $apiProcessName = (Get-Process -Id $apiPid -ErrorAction SilentlyContinue).ProcessName ?? 'dotnet'

        Write-Information "  │   API running (PID: $apiPid)" -InformationAction Continue

        # ── Start group collectors ──────────────────────────────────────────
        Write-Information '  │   Starting collectors...' -InformationAction Continue
        $startResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticCollection.ps1') `
            -Action 'Start' `
            -ProcessId $apiPid `
            -OutputDir $resultsDir `
            -Config $config `
            -CollectorSubset $collectorNames

        if ($startResult.Handles.Count -eq 0) {
            Write-Warning "  │   No collectors started for group '$groupName' — skipping"
            continue
        }

        $startedCollectors = ($startResult.Handles.Keys -join ', ')
        Write-Information "  │   Active: $startedCollectors" -InformationAction Continue

        Start-Sleep -Seconds 2

        # ── Run k6 diagnostic pass ──────────────────────────────────────────
        $baseUrl = $apiResult.BaseUrl
        Write-Information "  │   Running k6 ($diagnosticRuns run(s))..." -InformationAction Continue

        for ($run = 1; $run -le $diagnosticRuns; $run++) {
            if ($run -gt 1) {
                & (Join-Path $harnessRoot 'Invoke-Cooldown.ps1') -BaseUrl $baseUrl -GcEndpoint $config.Api.GcEndpoint -CooldownSeconds 3 | Out-Null
            }
            $k6SummaryPath = Join-Path $resultsDir "k6-diagnostic-$groupName-run$run.json"
            & k6 run --env "BASE_URL=$baseUrl" --summary-export $k6SummaryPath --quiet $scenarioFullPath 2>&1 | Out-Null
        }

        Write-Information '  │   k6 complete' -InformationAction Continue

        # ── Stop collectors ─────────────────────────────────────────────────
        Write-Information '  │   Stopping collectors...' -InformationAction Continue
        $stopResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticCollection.ps1') `
            -Action 'Stop' `
            -Config $config `
            -Handles $startResult.Handles

        if (-not $stopResult.Success) {
            Write-Warning "  │   Some collectors failed to stop — continuing with available data"
        }

        # ── Export collector data ───────────────────────────────────────────
        Write-Information '  │   Exporting...' -InformationAction Continue
        $exportResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticCollection.ps1') `
            -Action 'Export' `
            -Config $config `
            -ArtifactMap $stopResult.ArtifactMap `
            -OutputDir $resultsDir `
            -ProcessName $apiProcessName `
            -CollectorSubset $collectorNames

        if (-not $exportResult.Success) {
            Write-Warning "  │   Some exports failed — continuing with available data"
        }

        # Merge into unified collector data
        foreach ($key in $exportResult.CollectorData.Keys) {
            $mergedCollectorData[$key] = $exportResult.CollectorData[$key]
        }

        $exportedNames = ($exportResult.CollectorData.Keys -join ', ')
        Write-Information "  │   Exported: $exportedNames" -InformationAction Continue
    }
    finally {
        # ── Stop API (always) ───────────────────────────────────────────────
        Write-Information '  │   Stopping API...' -InformationAction Continue
        & (Join-Path $harnessRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process | Out-Null
    }
}

# ── Run analyzers with merged data from all passes ──────────────────────────
Write-Information '  │' -InformationAction Continue
Write-Information '  │ Running analyzers...' -InformationAction Continue
$analysisResult = & (Join-Path $harnessRoot 'Invoke-DiagnosticAnalysis.ps1') `
    -CollectorData $mergedCollectorData `
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
    CollectorData   = $mergedCollectorData
    AnalyzerReports = $analysisResult.Reports
}
