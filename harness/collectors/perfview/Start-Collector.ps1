<#
.SYNOPSIS
    Starts a single PerfView session collecting both CPU sampling stacks and GC events.
.DESCRIPTION
    Launches one PerfView process with merged provider sets (/ThreadTime for CPU,
    /GCOnly for GC events, /ClrEvents:Default for CLR stacks). This replaces the
    former two-process approach (perfview-cpu + perfview-gc) which competed for ETW
    resources and caused timeouts.
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
    $perfViewExe = $Settings.PerfViewExePath
    if (-not $perfViewExe) {
        $msg = 'PerfViewExePath not specified in settings.'
        Write-Information $msg
        return [PSCustomObject][ordered]@{ Success = $false; Error = $msg }
    }

    # Resolve relative paths from repo root
    if (-not [System.IO.Path]::IsPathRooted($perfViewExe)) {
        $repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
        $perfViewExe = Join-Path $repoRoot $perfViewExe
    }

    if (-not (Test-Path $perfViewExe)) {
        $msg = "PerfView executable not found at '$perfViewExe'."
        Write-Information $msg
        return [PSCustomObject][ordered]@{ Success = $false; Error = $msg }
    }

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    $outputPath = Join-Path $OutputDir 'perfview.etl.zip'

    $maxCollectSec = if ($Settings.ContainsKey('MaxCollectSec')) { $Settings.MaxCollectSec } else { 90 }
    $bufferSizeMB  = if ($Settings.ContainsKey('BufferSizeMB'))  { $Settings.BufferSizeMB }  else { 256 }

    # Merged provider set: CPU sampling + thread time + GC events + CLR stacks
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
        '/GCOnly'
        '/ClrEvents:Default'
        '/Providers:Microsoft-DotNet-SampleProfiler'
    )

    Write-Verbose "Starting PerfView: $perfViewExe $($perfViewArgs -join ' ')"

    $stderrLog = Join-Path $OutputDir 'perfview-stderr.log'
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
        return [PSCustomObject][ordered]@{ Success = $false; Error = $msg }
    }

    Write-Information "PerfView collector started (PID: $($process.Id), target PID: $ProcessId)"

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
    $msg = "Failed to start PerfView collector: $_"
    Write-Information $msg
    return [PSCustomObject][ordered]@{ Success = $false; Error = $msg }
}
