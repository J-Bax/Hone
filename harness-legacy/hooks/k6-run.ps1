<#
.SYNOPSIS
    Shared hook: runs k6 scale tests against the running API.

.DESCRIPTION
    Delegates to Invoke-ScaleTests.ps1 for the Active lifecycle phase.
    Wraps the scale-test result in the standard hook return contract.

.PARAMETER TargetPath
    Root path of the target project.

.PARAMETER Config
    Merged harness configuration hashtable.

.PARAMETER BaseUrl
    Base URL of the running API.

.PARAMETER Experiment
    Current experiment identifier.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$harnessRoot = Split-Path $PSScriptRoot -Parent

# Invoke-ScaleTests.ps1 accepts a ConfigPath (engine config file).
$configPath = Join-Path $harnessRoot 'config.psd1'

try {
    $scaleResult = & (Join-Path $harnessRoot 'Invoke-ScaleTests.ps1') `
        -ConfigPath $configPath `
        -TargetDir $TargetPath `
        -Experiment $Experiment `
        -BaseUrl $BaseUrl `
        -SkipHealthCheck `
        -InitialProcess $Config._Process

    $stopwatch.Stop()

    return [PSCustomObject]@{
        Success = $scaleResult.Success
        Message = if ($scaleResult.Success) { 'k6 scale tests completed' } else { "k6 scale tests failed (exit code $($scaleResult.ExitCode))" }
        Duration = $stopwatch.Elapsed
        Artifacts = @(if ($scaleResult.SummaryPath) { $scaleResult.SummaryPath })
        Metrics = $scaleResult.Metrics
        CounterMetrics = $scaleResult.CounterMetrics
        RunMetrics = $scaleResult.RunMetrics
        LastProcess = $scaleResult.LastProcess
        LastBaseUrl = $scaleResult.LastBaseUrl
    }
} catch {
    $stopwatch.Stop()

    return [PSCustomObject]@{
        Success = $false
        Message = "k6 scale tests error: $_"
        Duration = $stopwatch.Elapsed
        Artifacts = @()
    }
}
