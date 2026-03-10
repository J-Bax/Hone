<#
.SYNOPSIS
    Stops the PerfView GC collection and waits for merge/zip.
.DESCRIPTION
    Signals PerfView to stop via its abort-file mechanism, then polls for
    completion by watching the log file for PerfView's "[DONE" marker.

    PerfView /NoGui has a known bug where the process lingers after completing
    all work (collection, rundown, merge, zip). Rather than blocking on
    WaitForExit for the full timeout, this script detects work completion
    early and terminates the hung process once data is safely written.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable]$Handle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$process    = $Handle.Process
$outputPath = $Handle.OutputPath
$waitTimeoutSec = if ($Handle.Settings -and $Handle.Settings.StopTimeoutSec) {
    [int]$Handle.Settings.StopTimeoutSec
} else { 300 }

if (-not $process) {
    Write-Warning 'No PerfView process in handle — collection may not have started.'
    return [PSCustomObject][ordered]@{
        Success       = $false
        Error         = 'No PerfView process in handle.'
        ArtifactPaths = @()
    }
}

try {
    if (-not $process.HasExited) {
        # PerfView's documented abort mechanism: create a <DataFile>.abort file
        $abortFilePath = "$outputPath.abort"
        Write-Verbose "Writing abort file: $abortFilePath"
        [System.IO.File]::WriteAllText($abortFilePath, 'stop')

        # PerfView writes its log to <DataFile without .zip>.log.txt
        $logPath = ($outputPath -replace '\.zip$', '') + '.log.txt'

        Write-Information "Signaled PerfView to stop. Waiting up to ${waitTimeoutSec}s for rundown/merge/zip..."

        # Poll for completion: PerfView writes "[DONE" to log when finished,
        # but has a known bug where the /NoGui process doesn't exit afterward.
        # Detect completion early so we can terminate the hung process quickly.
        $pollIntervalMs = 3000
        $deadline = [DateTime]::UtcNow.AddSeconds($waitTimeoutSec)
        $workComplete = $false

        while ([DateTime]::UtcNow -lt $deadline) {
            if ($process.HasExited) { break }

            if ((Test-Path $outputPath) -and (Test-Path $logPath)) {
                $logTail = Get-Content $logPath -Tail 5 -ErrorAction SilentlyContinue
                if ($logTail -and ($logTail -match '\[DONE')) {
                    $workComplete = $true
                    break
                }
            }

            Start-Sleep -Milliseconds $pollIntervalMs
        }

        if ($process.HasExited) {
            Write-Verbose "PerfView exited on its own."
        }
        elseif ($workComplete) {
            # PerfView completed its work but didn't exit (known /NoGui bug).
            # Give a short grace period, then terminate cleanly.
            $graceExited = $process.WaitForExit(5000)
            if (-not $graceExited) {
                Write-Information "PerfView work complete but process lingering — terminating (PID: $($process.Id))."
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                if (-not $process.WaitForExit(10000)) {
                    Write-Warning "PerfView process $($process.Id) did not terminate — may be orphaned."
                }
            }
        }
        else {
            # Deadline reached without completion — force stop
            Write-Warning "PerfView did not complete within ${waitTimeoutSec}s — forcing stop."
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            if (-not $process.WaitForExit(10000)) {
                Write-Warning "PerfView process $($process.Id) did not terminate — may be orphaned."
            }
            # Force-kill during active work leaves orphaned ETW sessions
            foreach ($session in @('NT Kernel Logger', 'PerfViewGCSession')) {
                logman stop $session -ets 2>&1 | Out-Null
            }
        }

        # Clean up abort file
        if (Test-Path $abortFilePath) {
            Remove-Item $abortFilePath -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Verbose "PerfView process already exited (exit code: $($process.ExitCode))."
    }

    # Allow file handles to flush
    Start-Sleep -Seconds 1

    if (-not (Test-Path $outputPath)) {
        $msg = "ETL artifact not found at '$outputPath' after PerfView exited."
        Write-Information $msg
        return [PSCustomObject][ordered]@{
            Success       = $false
            Error         = $msg
            ArtifactPaths = @()
        }
    }

    $sizeMB = [math]::Round((Get-Item $outputPath).Length / 1MB, 2)
    Write-Information "PerfView collection stopped. Artifact: $outputPath ($sizeMB MB)"

    return [PSCustomObject][ordered]@{
        Success       = $true
        ArtifactPaths = @($outputPath)
    }
}
catch {
    $msg = "Failed to stop PerfView collector: $_"
    Write-Information $msg
    return [PSCustomObject][ordered]@{
        Success       = $false
        Error         = $msg
        ArtifactPaths = @()
    }
}

