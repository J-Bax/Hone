<#
.SYNOPSIS
    Starts PerfView GC-only collection for GC statistics.
.DESCRIPTION
    Launches PerfView with /GCOnly for minimal-overhead GC event capture.
    Does NOT collect CPU sampling events — use perfview-cpu for that.
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

# ── Clean up stale ETW sessions from prior interrupted runs ─────────────────
# PerfView uses the singleton "NT Kernel Logger" plus a user-mode session.
# If a previous run was force-killed, these remain active and block new collection.
foreach ($session in @('NT Kernel Logger', 'PerfViewGCSession')) {
    $logmanOut = logman stop $session -ets 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Information "  Cleaned up stale ETW session: $session"
    }
}

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

    $outputPath = Join-Path $OutputDir 'perfview-gc.etl.zip'

    # Clean up stale intermediate files from interrupted prior runs
    $baseName = Join-Path $OutputDir 'perfview-gc'
    foreach ($staleFile in @("$baseName.etl", "$baseName.kernel.etl", "$baseName.clrRundown.etl",
                             "$baseName.etl.new", "$baseName.etl.zip",
                             "$baseName.etl.log.txt", "$baseName.etl.zip.abort")) {
        if (Test-Path $staleFile) {
            Remove-Item $staleFile -Force -ErrorAction SilentlyContinue
            Write-Verbose "Removed stale file: $staleFile"
        }
    }

    $maxCollectSec = if ($Settings.ContainsKey('MaxCollectSec')) { $Settings.MaxCollectSec } else { 90 }
    $bufferSizeMB  = if ($Settings.ContainsKey('BufferSizeMB'))  { $Settings.BufferSizeMB }  else { 256 }

    # /GCOnly: minimal overhead, captures only GC-related events
    # /ClrEvents:GC ensures GC events are enabled (redundant with /GCOnly but explicit)
    # /focusProcess scopes merge/analysis to the target process, reducing trace noise
    $perfViewArgs = @(
        'collect'
        "/DataFile:$outputPath"
        '/NoGui'
        '/AcceptEULA'
        "/MaxCollectSec:$maxCollectSec"
        "/BufferSizeMB:$bufferSizeMB"
        '/Merge:true'
        '/Zip:true'
        '/NoNGenPdbs'
        '/GCOnly'
        '/ClrEvents:GC'
        "/focusProcess:$ProcessId"
    )

    Write-Verbose "Starting PerfView GC: $perfViewExe $($perfViewArgs -join ' ')"

    $stderrLog = Join-Path $OutputDir 'perfview-gc-stderr.log'
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

    Write-Information "PerfView GC collector started (PID: $($process.Id), target PID: $ProcessId)"

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
    $msg = "Failed to start PerfView GC collector: $_"
    Write-Information $msg
    return [PSCustomObject][ordered]@{ Success = $false; Error = $msg }
}
