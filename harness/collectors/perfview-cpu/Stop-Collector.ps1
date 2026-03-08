<#
.SYNOPSIS
    Stops PerfView CPU sampling collection and waits for merge/zip.
.DESCRIPTION
    Signals PerfView to abort collection by writing its documented abort file,
    then waits for the process to exit and verifies the ETL.zip artifact exists.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable]$Handle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$waitTimeoutSec = 60

try {
    $process    = $Handle.Process
    $outputPath = $Handle.OutputPath

    if (-not $process) {
        return [PSCustomObject][ordered]@{
            Success = $false
            Error   = 'No PerfView process in handle.'
        }
    }

    if (-not $process.HasExited) {
        # PerfView's documented abort mechanism: create a <DataFile>.abort file
        $abortFilePath = "$outputPath.abort"
        Write-Verbose "Writing abort file: $abortFilePath"
        [System.IO.File]::WriteAllText($abortFilePath, 'stop')

        Write-Information "Signaled PerfView to stop. Waiting up to ${waitTimeoutSec}s for merge/zip..."

        $exited = $process.WaitForExit($waitTimeoutSec * 1000)
        if (-not $exited) {
            Write-Information "PerfView did not exit within ${waitTimeoutSec}s — forcing termination."
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
            Success = $false
            Error   = $msg
        }
    }

    $sizeMB = [math]::Round((Get-Item $outputPath).Length / 1MB, 2)
    Write-Information "PerfView CPU collection stopped. Artifact: $outputPath ($sizeMB MB)"

    return [PSCustomObject][ordered]@{
        Success       = $true
        ArtifactPaths = @($outputPath)
    }
}
catch {
    $msg = "Failed to stop PerfView CPU collector: $_"
    Write-Information $msg
    return [PSCustomObject][ordered]@{
        Success = $false
        Error   = $msg
    }
}
