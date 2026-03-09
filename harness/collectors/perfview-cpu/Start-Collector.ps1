<#
.SYNOPSIS
    Starts PerfView CPU sampling collection (ThreadTime + CLR events).
.DESCRIPTION
    Launches PerfView with CPU sampling enabled. Does NOT use /GCOnly, ensuring
    kernel Profile events (CPU sampling) are captured.
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

    $outputPath = Join-Path $OutputDir 'perfview-cpu.etl.zip'

    $maxCollectSec = if ($Settings.ContainsKey('MaxCollectSec')) { $Settings.MaxCollectSec } else { 90 }
    $bufferSizeMB  = if ($Settings.ContainsKey('BufferSizeMB'))  { $Settings.BufferSizeMB }  else { 256 }

    # CPU sampling: /ThreadTime enables context-switch + CPU profiling
    # /ClrEvents:Default includes GC, JIT, Exception, etc. for managed stack resolution
    # NO /GCOnly — that would suppress kernel CPU sampling events
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
    )

    Write-Verbose "Starting PerfView CPU: $perfViewExe $($perfViewArgs -join ' ')"

    $stderrLog = Join-Path $OutputDir 'perfview-cpu-stderr.log'
    $process = Start-Process -FilePath $perfViewExe `
        -ArgumentList $perfViewArgs `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardError $stderrLog

    Start-Sleep -Seconds 3

    if ($process.HasExited) {
        $exitCode = $process.ExitCode
        $msg = "PerfView exited prematurely with exit code $exitCode."
        Write-Information $msg
        return [PSCustomObject][ordered]@{ Success = $false; Error = $msg }
    }

    Write-Information "PerfView CPU collector started (PID: $($process.Id), target PID: $ProcessId)"

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
    return [PSCustomObject][ordered]@{ Success = $false; Error = $msg }
}
