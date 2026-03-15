<#
.SYNOPSIS
    Starts the sample API as a background process.

.DESCRIPTION
    Runs 'dotnet run' on the API project as a background job, then polls the
    health endpoint until it returns 200 or the startup timeout is exceeded.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.OUTPUTS
    PSCustomObject with properties: Success, Process, BaseUrl
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot

$config = Get-HoneConfig -ConfigPath $ConfigPath
$projectPath = Join-Path $repoRoot $config.Api.ProjectPath
$baseUrl = $config.Api.BaseUrl
$timeout = $config.Api.StartupTimeout

# ── Dynamic port: if configured port is 0, find a free ephemeral port ────────
$configuredPort = ([uri]$baseUrl).Port
$portAttempts = 0
$maxPortAttempts = if ($configuredPort -eq 0) { 3 } else { 1 }
do {
    $portAttempts++

    if ($configuredPort -eq 0) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $freePort = $listener.LocalEndpoint.Port
        $listener.Stop()
        $baseUrl = "http://localhost:$freePort"
        Write-Verbose "Dynamic port selected: $freePort"
    }

    $healthUrl = "$baseUrl$($config.Api.HealthEndpoint)"

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' -Message "Starting API from: $projectPath"

    # Start the API as a background process
    $apiProcess = Start-Process -FilePath 'dotnet' `
        -ArgumentList "run --project `"$projectPath`" --configuration Release --urls $baseUrl" `
        -PassThru -WindowStyle Hidden

    $healthy = Wait-ApiHealthy -HealthUrl $healthUrl -TimeoutSec $timeout -IntervalSec 1

    if (-not $healthy -and $apiProcess.HasExited -and $configuredPort -eq 0) {
        Write-Verbose "Port $freePort may have been claimed — retrying ($portAttempts/$maxPortAttempts)"
        continue
    }
    break
} while ($portAttempts -lt $maxPortAttempts)

$result = [ordered]@{
    Success = $healthy
    Process = $apiProcess
    BaseUrl = $baseUrl
}

if ($healthy) {
    # Elevate process priority to reduce OS scheduling jitter from background tasks
    try {
        $apiProcess.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High
        Write-Verbose "API process priority elevated to High"
    }
    catch {
        Write-Verbose "Could not elevate API process priority: $_"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' -Message "API is healthy at $baseUrl (took ${elapsed}s)"
}
else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'error' -Message "API failed to become healthy within ${timeout}s"

    # Kill the process if it didn't become healthy
    if (-not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

return [PSCustomObject]$result
