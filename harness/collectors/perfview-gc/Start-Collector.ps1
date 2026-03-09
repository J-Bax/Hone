<#
.SYNOPSIS
    Starts PerfView GC-focused ETW collection against a running .NET process.

.DESCRIPTION
    Launches PerfView in the background to collect GC events and optionally
    allocation tick data. The resulting ETL.zip file is used by Stop-Collector
    and Export-CollectorData for analysis.

.PARAMETER ProcessId
    The PID of the running .NET API process to trace.

.PARAMETER OutputDir
    Directory where the ETL.zip artifact will be written.

.PARAMETER Settings
    Merged settings hashtable (config defaults + collector defaults).
    Expected keys: PerfViewExePath, MaxCollectSec, BufferSizeMB, AllocationSampling.

.OUTPUTS
    Hashtable with Success, Handle (Process, OutputPath, ProcessId).
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

$harnessRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# ── Resolve PerfView path ───────────────────────────────────────────────────
$perfViewExe = $Settings.PerfViewExePath
if (-not $perfViewExe) {
    Write-Warning 'PerfViewExePath not specified in settings'
    return @{
        Success = $false
        Error   = 'PerfViewExePath not specified in settings'
    }
}

# Resolve relative paths from repo root
if (-not [System.IO.Path]::IsPathRooted($perfViewExe)) {
    $repoRoot = Split-Path -Parent $harnessRoot
    $perfViewExe = Join-Path $repoRoot $perfViewExe
}

if (-not (Test-Path $perfViewExe)) {
    Write-Warning "PerfView not found at: $perfViewExe"
    return @{
        Success = $false
        Error   = "PerfView not found at: $perfViewExe"
    }
}

# ── Ensure output directory exists ──────────────────────────────────────────
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$outputPath = Join-Path $OutputDir 'perfview-gc.etl.zip'

# ── Build PerfView arguments ────────────────────────────────────────────────
$maxCollectSec = if ($Settings.MaxCollectSec) { $Settings.MaxCollectSec } else { 90 }
$bufferSizeMB  = if ($Settings.BufferSizeMB)  { $Settings.BufferSizeMB }  else { 256 }

$perfViewArgs = @(
    'collect'
    "/DataFile:$outputPath"
    '/NoGui'
    '/AcceptEULA'
    "/MaxCollectSec:$maxCollectSec"
    "/BufferSizeMB:$bufferSizeMB"
    '/Merge:true'
    '/Zip:true'
    '/GCOnly'
)

if ($Settings.AllocationSampling) {
    $perfViewArgs += '/ClrEvents:GC+Exception'
    $perfViewArgs += '/ClrEventLevel:Informational'
}

Write-Information "Starting PerfView GC collection for PID $ProcessId → $outputPath"
Write-Verbose "PerfView args: $($perfViewArgs -join ' ')"

# ── Launch PerfView as background process ───────────────────────────────────
try {
    $stderrLog = Join-Path $OutputDir 'perfview-gc-stderr.log'
    $process = Start-Process -FilePath $perfViewExe `
        -ArgumentList $perfViewArgs `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardError $stderrLog

    # Allow time for PerfView to attach ETW session
    Start-Sleep -Seconds 3

    if ($process.HasExited) {
        $exitCode = $process.ExitCode
        $msg = "PerfView exited immediately with code $exitCode. Check $stderrLog for details."
        Write-Warning $msg
        return @{
            Success = $false
            Error   = $msg
        }
    }

    Write-Information "PerfView GC collection running (PID: $($process.Id))"

    return @{
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
    $msg = "Failed to start PerfView GC collection: $_"
    Write-Warning $msg
    return @{
        Success = $false
        Error   = $msg
    }
}
