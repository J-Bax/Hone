<#
.SYNOPSIS
    Starts collecting .NET performance counters for a running API process.

.DESCRIPTION
    Launches 'dotnet-counters collect' as a background process against the target
    API. Captures counters from System.Runtime, Microsoft.AspNetCore.Hosting,
    Microsoft.AspNetCore.Http.Connections, and System.Net.Http providers.
    Returns a handle object used by Stop-DotnetCounters.ps1 to stop collection
    and parse results.

.PARAMETER ProcessId
    The PID of the running .NET API process to monitor.

.PARAMETER OutputPath
    Path where the CSV counter output will be written.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Iteration
    Current iteration number for logging and file naming.

.OUTPUTS
    PSCustomObject with properties: Success, Process, OutputPath
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ProcessId,

    [string]$OutputPath,

    [string]$ConfigPath,

    [int]$Iteration = 0
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

# Resolve output path
if (-not $OutputPath) {
    $outputDir = Join-Path $repoRoot $config.ScaleTest.OutputPath
    $OutputPath = Join-Path $outputDir "dotnet-counters-iteration-$Iteration.csv"
}

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Build the list of counter providers from config (or use defaults)
$defaultProviders = @(
    'System.Runtime'
    'Microsoft.AspNetCore.Hosting'
    'Microsoft.AspNetCore.Http.Connections'
    'System.Net.Http'
)

$providers = if ($config.DotnetCounters -and $config.DotnetCounters.Providers) {
    $config.DotnetCounters.Providers
}
else {
    $defaultProviders
}

$refreshInterval = if ($config.DotnetCounters -and $config.DotnetCounters.RefreshIntervalSeconds) {
    $config.DotnetCounters.RefreshIntervalSeconds
}
else {
    1
}

$providerList = $providers -join ','

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' `
    -Message "Starting dotnet-counters collection for PID $ProcessId (providers: $providerList)" `
    -Iteration $Iteration

# Build dotnet-counters arguments
$counterArgs = @(
    'collect'
    '--process-id', $ProcessId
    '--output', $OutputPath
    '--format', 'csv'
    '--refresh-interval', $refreshInterval
    '--counters', $providerList
)

# Start dotnet-counters as a background process
try {
    $counterProcess = Start-Process -FilePath 'dotnet-counters' `
        -ArgumentList $counterArgs `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $outputDir "dotnet-counters-stderr-$Iteration.log")

    # Give it a moment to attach
    Start-Sleep -Seconds 2

    if ($counterProcess.HasExited) {
        $exitCode = $counterProcess.ExitCode
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'error' `
            -Message "dotnet-counters exited immediately with code $exitCode. Is dotnet-counters installed? Run: dotnet tool install --global dotnet-counters" `
            -Iteration $Iteration

        return [PSCustomObject][ordered]@{
            Success    = $false
            Process    = $null
            OutputPath = $OutputPath
        }
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "dotnet-counters collecting (PID: $($counterProcess.Id)) → $OutputPath" `
        -Iteration $Iteration

    return [PSCustomObject][ordered]@{
        Success    = $true
        Process    = $counterProcess
        OutputPath = $OutputPath
    }
}
catch {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'warning' `
        -Message "Failed to start dotnet-counters: $_. Counter collection will be skipped." `
        -Iteration $Iteration

    return [PSCustomObject][ordered]@{
        Success    = $false
        Process    = $null
        OutputPath = $OutputPath
    }
}
