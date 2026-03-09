<#
.SYNOPSIS
    Stops the PerfView CPU collection and waits for merge/zip.
.DESCRIPTION
    Signals PerfView to stop via its abort-file mechanism, waits for the
    process to exit (allowing time for ETL merge and zip), and verifies
    the output artifact exists.
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

        Write-Information "Signaled PerfView to stop. Waiting up to ${waitTimeoutSec}s for rundown/merge/zip..."

        $exited = $process.WaitForExit($waitTimeoutSec * 1000)
        if (-not $exited) {
            Write-Warning "PerfView did not exit within ${waitTimeoutSec}s — forcing stop."
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(10000) | Out-Null
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

