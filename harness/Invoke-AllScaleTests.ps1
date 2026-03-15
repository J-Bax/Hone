<#
.SYNOPSIS
    Runs every registered k6 scenario and returns per-scenario results.

.DESCRIPTION
    Reads the scenario registry (sample-api/scale-tests/thresholds.json), iterates through
    each scenario, and calls Invoke-ScaleTests.ps1 for each one.  The primary
    optimization scenario (use_for_optimization = true) is identified separately
    so callers can decide whether to skip it (it is usually run first by the
    main loop).

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Experiment
    Current experiment number — forwarded to Invoke-ScaleTests.ps1 for file
    naming and logging.

.PARAMETER SkipPrimary
    When set, scenarios with use_for_optimization = true are skipped.  Use this
    when the primary scenario was already executed by the caller.

.EXAMPLE
    # Run only the diagnostic (non-primary) scenarios for experiment 3
    .\Invoke-AllScaleTests.ps1 -ConfigPath .\harness\config.psd1 -Experiment 3 -SkipPrimary

.EXAMPLE
    # Run every scenario for baseline capture (experiment 0)
    .\Invoke-AllScaleTests.ps1 -ConfigPath .\harness\config.psd1 -Experiment 0
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [int]$Experiment = 0,
    [switch]$SkipPrimary,
    [switch]$SkipHealthCheck,
    [string]$BaseUrl
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot

$config = Get-HoneConfig -ConfigPath $ConfigPath

# ── Load scenario registry ──────────────────────────────────────────────────

$registryPath = Join-Path $repoRoot $config.ScaleTest.ScenarioRegistryPath
if (-not (Test-Path $registryPath)) {
    Write-Error "Scenario registry not found at $registryPath"
    return
}

$registry = Get-Content $registryPath -Raw | ConvertFrom-Json

# ── Iterate scenarios ───────────────────────────────────────────────────────

$results = [System.Collections.Generic.List[object]]::new()
$scenarioIndex = 0

foreach ($name in ($registry.scenarios.PSObject.Properties.Name)) {
    $scenario = $registry.scenarios.$name

    if ($SkipPrimary -and $scenario.use_for_optimization) {
        Write-Verbose "Skipping primary scenario '$name' (-SkipPrimary)"
        continue
    }

    # ── Cooldown + GC between scenarios ──────────────────────────────────
    if ($scenarioIndex -gt 0) {
        $cooldown = if ($config.ScaleTest.CooldownSeconds) { [int]$config.ScaleTest.CooldownSeconds } else { 3 }
        $cooldownBaseUrl = if ($BaseUrl) { $BaseUrl } else { $config.Api.BaseUrl }
        & (Join-Path $PSScriptRoot 'Invoke-Cooldown.ps1') `
            -BaseUrl $cooldownBaseUrl `
            -GcEndpoint $config.Api.GcEndpoint `
            -CooldownSeconds $cooldown `
            -Experiment $Experiment `
            -Reason 'between scenarios'
    }
    $scenarioIndex++

    $scenarioFile = Join-Path -Path $repoRoot -ChildPath (Split-Path $config.ScaleTest.ScenarioRegistryPath -Parent) $scenario.file

    # For the primary optimization scenario use no ScenarioName so the
    # existing k6-summary.json naming is preserved.
    $scenarioNameArg = if ($scenario.use_for_optimization) { $null } else { $name }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "Running scenario '$name': $($scenario.description)" `
        -Experiment $Experiment

    $params = @{
        ConfigPath = $ConfigPath
        Experiment = $Experiment
        ScenarioPath = $scenarioFile
    }
    if ($scenarioNameArg) { $params.ScenarioName = $scenarioNameArg }
    if ($BaseUrl) { $params.BaseUrl = $BaseUrl }
    if ($SkipHealthCheck) { $params.SkipHealthCheck = $true }

    $result = $null
    try {
        $result = & (Join-Path $PSScriptRoot 'Invoke-ScaleTests.ps1') @params
    } catch {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "Scenario '$name' threw an error: $_" `
            -Experiment $Experiment
    }

    $success = $result -and $result.Success
    $metrics = if ($result) { $result.Metrics } else { $null }

    $results.Add([PSCustomObject][ordered]@{
            ScenarioName = $name
            Description = $scenario.description
            UseForOptimization = [bool]$scenario.use_for_optimization
            Success = $success
            Metrics = $metrics
            CounterMetrics = if ($result) { $result.CounterMetrics } else { $null }
            SummaryPath = if ($result) { $result.SummaryPath } else { $null }
        })

    $status = if ($success) { 'OK' } else { 'FAIL' }
    $p95 = if ($metrics) { "$($metrics.HttpReqDuration.P95)ms" } else { 'N/A' }
    Write-Status "  [$status] $name — p95: $p95"
}

return , $results.ToArray()
