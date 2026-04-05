<#
.SYNOPSIS
    Shared hook: waits for the target API to become healthy.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$null = $TargetPath, $Experiment

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

if (-not $BaseUrl) {
    $stopwatch.Stop()
    return [PSCustomObject]@{
        Success = $false
        Message = 'No BaseUrl provided'
        Duration = $stopwatch.Elapsed
        Artifacts = @()
    }
}

$healthUrl = "$BaseUrl$($Config.Api.HealthEndpoint)"
$timeout = if ($Config.Api.StartupTimeout) { $Config.Api.StartupTimeout } else { 90 }
$healthy = Wait-ApiHealthy -HealthUrl $healthUrl -TimeoutSec $timeout -IntervalSec 1

$stopwatch.Stop()

return [PSCustomObject]@{
    Success = $healthy
    Message = if ($healthy) { "Health endpoint healthy after $($stopwatch.Elapsed.TotalSeconds.ToString('F1'))s" } else { "Health endpoint not healthy after ${timeout}s" }
    Duration = $stopwatch.Elapsed
    Artifacts = @()
}
