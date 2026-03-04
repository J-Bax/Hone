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

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$projectPath = Join-Path $repoRoot $config.Api.ProjectPath
$baseUrl = $config.Api.BaseUrl
$healthUrl = "$baseUrl$($config.Api.HealthEndpoint)"
$timeout = $config.Api.StartupTimeout

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' -Message "Starting API from: $projectPath"

# Start the API as a background process
$apiProcess = Start-Process -FilePath 'dotnet' `
    -ArgumentList "run --project `"$projectPath`" --configuration Release --urls $baseUrl" `
    -PassThru -WindowStyle Hidden

# Poll the health endpoint
$elapsed = 0
$healthy = $false

while ($elapsed -lt $timeout) {
    Start-Sleep -Seconds 1
    $elapsed++

    try {
        $response = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 2 -ErrorAction Stop
        if ($response.status -eq 'healthy') {
            $healthy = $true
            break
        }
    }
    catch {
        # API not ready yet, keep polling
        Write-Verbose "Health check attempt $elapsed/$timeout - not ready"
    }
}

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
