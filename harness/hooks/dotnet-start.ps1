<#
.SYNOPSIS
    Shared hook: starts a .NET API as a background process.

.DESCRIPTION
    Runs `dotnet run` on the configured project path. Supports dynamic port
    allocation when `Api.BaseUrl` uses port 0 and returns the started process
    together with the actual base URL.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$null = $Experiment

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$projectPath = Join-Path $TargetPath $Config.Api.ProjectPath
if (-not $BaseUrl) { $BaseUrl = $Config.Api.BaseUrl }
$timeout = if ($Config.Api.StartupTimeout) { $Config.Api.StartupTimeout } else { 90 }

$configuredPort = ([uri]$BaseUrl).Port
$maxPortAttempts = if ($configuredPort -eq 0) { 3 } else { 1 }
$attempt = 0
$healthy = $false
$apiProcess = $null

do {
    $attempt++

    if ($configuredPort -eq 0) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $freePort = $listener.LocalEndpoint.Port
        $listener.Stop()
        $BaseUrl = "http://localhost:$freePort"
    }

    $apiProcess = Start-Process -FilePath 'dotnet' `
        -ArgumentList "run --project `"$projectPath`" --configuration Release --urls $BaseUrl" `
        -PassThru -WindowStyle Hidden

    $healthUrl = "$BaseUrl$($Config.Api.HealthEndpoint)"
    $healthy = Wait-ApiHealthy -HealthUrl $healthUrl -TimeoutSec $timeout -IntervalSec 1

    if (-not $healthy -and $apiProcess -and $apiProcess.HasExited -and $configuredPort -eq 0) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
        continue
    }

    break
} while ($attempt -lt $maxPortAttempts)

$stopwatch.Stop()

if ($healthy) {
    try {
        $apiProcess.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High
    } catch {
        Write-Verbose "Could not elevate API process priority: $_"
    }

    return [PSCustomObject]@{
        Success = $true
        Message = "API is healthy at $BaseUrl (PID $($apiProcess.Id))"
        Duration = $stopwatch.Elapsed
        Artifacts = @()
        Process = $apiProcess
        ActualBaseUrl = $BaseUrl
    }
}

if ($apiProcess -and -not $apiProcess.HasExited) {
    Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
}

return [PSCustomObject]@{
    Success = $false
    Message = "API failed to become healthy within ${timeout}s"
    Duration = $stopwatch.Elapsed
    Artifacts = @()
    Process = $null
    ActualBaseUrl = $null
}
