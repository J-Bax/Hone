<#
.SYNOPSIS
    Starts CPU sampling stack collection via PerfView ETW kernel events.
.DESCRIPTION
    Launches PerfView in the background to collect CPU sampling stacks,
    thread-time data, and CLR events for the target .NET process.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ProcessId,

    [Parameter(Mandatory)]
    [string]$OutputDir,

    [Parameter(Mandatory)]
    [hashtable]$Settings
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    # Resolve PerfView executable path
    $perfViewExe = $Settings.PerfViewExePath
    if (-not $perfViewExe -or -not (Test-Path $perfViewExe)) {
        $msg = "PerfView executable not found at '$perfViewExe'. Set Settings.PerfViewExePath to a valid path."
        Write-Information $msg
        return [PSCustomObject][ordered]@{
            Success = $false
            Error   = $msg
        }
    }

    # Ensure output directory exists
    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    $outputPath = Join-Path $OutputDir 'perfview-cpu.etl.zip'

    $maxCollectSec = if ($Settings.ContainsKey('MaxCollectSec')) { $Settings.MaxCollectSec } else { 90 }
    $bufferSizeMB  = if ($Settings.ContainsKey('BufferSizeMB'))  { $Settings.BufferSizeMB }  else { 256 }

    # Build PerfView arguments
    $perfViewArgs = @(
        'collect'
        "/DataFile:$outputPath"
        '/NoGui'
        '/AcceptEULA'
        "/MaxCollectSec:$maxCollectSec"
        "/BufferSizeMB:$bufferSizeMB"
        '/Merge:true'
        '/Zip:true'
        '/ThreadTime'
        '/ClrEvents:Default'
        '/Providers:Microsoft-DotNet-SampleProfiler'
    )

    Write-Verbose "Starting PerfView: $perfViewExe $($perfViewArgs -join ' ')"

    $stderrLog = Join-Path $OutputDir 'perfview-cpu-stderr.log'
    $process = Start-Process -FilePath $perfViewExe `
        -ArgumentList $perfViewArgs `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardError $stderrLog

    # Allow time for ETW session attachment
    Start-Sleep -Seconds 3

    if ($process.HasExited) {
        $exitCode = $process.ExitCode
        $msg = "PerfView exited prematurely with exit code $exitCode. Check $stderrLog for details."
        Write-Information $msg
        return [PSCustomObject][ordered]@{
            Success = $false
            Error   = $msg
        }
    }

    Write-Information "PerfView CPU collector started (PID: $($process.Id), target PID: $ProcessId)."

    return [PSCustomObject][ordered]@{
        Success = $true
        Handle  = @{
            Process    = $process
            OutputPath = $outputPath
            ProcessId  = $ProcessId
            Settings   = $Settings
        }
    }
}
catch {
    $msg = "Failed to start PerfView CPU collector: $_"
    Write-Information $msg
    return [PSCustomObject][ordered]@{
        Success = $false
        Error   = $msg
    }
}
