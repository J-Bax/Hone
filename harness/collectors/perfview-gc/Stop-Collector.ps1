<#
.SYNOPSIS
    Stops PerfView GC collection and waits for merge/zip to complete.

.DESCRIPTION
    Signals PerfView to stop via its abort-file mechanism, waits for the
    process to exit (allowing time for ETL merge and zip), and verifies
    the output artifact exists.

.PARAMETER Handle
    The handle hashtable returned by Start-Collector (Process, OutputPath, ProcessId).

.OUTPUTS
    Hashtable with Success and ArtifactPaths.
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

if (-not $process) {
    Write-Warning 'No PerfView process in handle — collection may not have started.'
    return @{
        Success       = $false
        ArtifactPaths = @()
    }
}

# ── Signal PerfView to stop via abort file ──────────────────────────────────
$abortPath = "$outputPath.abort"

if (-not $process.HasExited) {
    Write-Information 'Signalling PerfView to stop collection...'
    New-Item -ItemType File -Path $abortPath -Force | Out-Null

    # Wait for PerfView to finish merge/zip (up to 60s)
    $exited = $process.WaitForExit(60000)

    if (-not $exited) {
        Write-Warning 'PerfView did not exit within 60s — forcing stop.'
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
        $process.WaitForExit(10000) | Out-Null
    }
}
else {
    Write-Verbose 'PerfView process had already exited.'
}

# ── Clean up abort file ─────────────────────────────────────────────────────
if (Test-Path $abortPath) {
    Remove-Item -Path $abortPath -Force -ErrorAction SilentlyContinue
}

# ── Verify artifact ─────────────────────────────────────────────────────────
if (Test-Path $outputPath) {
    $sizeMB = [math]::Round((Get-Item $outputPath).Length / 1MB, 2)
    Write-Information "PerfView GC trace collected: $outputPath ($sizeMB MB)"

    return @{
        Success       = $true
        ArtifactPaths = @($outputPath)
    }
}
else {
    Write-Warning "PerfView GC artifact not found at: $outputPath"
    return @{
        Success       = $false
        ArtifactPaths = @()
    }
}
