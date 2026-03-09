<#
.SYNOPSIS
    Exports and summarizes dotnet-counters data.

.DESCRIPTION
    Processes artifacts produced by Stop-Collector.ps1. If the parsed JSON
    already exists among the artifact paths it is copied to OutputDir.
    Otherwise the CSV is parsed on the fly (reusing the same logic the
    harness Stop-DotnetCounters.ps1 uses). Returns a summary string
    highlighting key runtime metrics.

.PARAMETER ArtifactPaths
    CSV and/or JSON paths produced by Stop-Collector.ps1.

.PARAMETER OutputDir
    Directory where exported data should be written.

.PARAMETER ProcessName
    Not used for this collector; accepted for interface compatibility.

.PARAMETER Settings
    Merged collector settings hashtable.

.OUTPUTS
    Hashtable with Success [bool], ExportedPaths [string[]], and Summary [string].
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$ArtifactPaths,

    [Parameter(Mandatory)]
    [string]$OutputDir,

    [string]$ProcessName,

    [hashtable]$Settings = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$jsonSource = $ArtifactPaths | Where-Object { $_ -like '*.json' -and (Test-Path $_) } | Select-Object -First 1
$csvSource  = $ArtifactPaths | Where-Object { $_ -like '*.csv'  -and (Test-Path $_) } | Select-Object -First 1

$exportedPaths = @()
$metrics = $null

# ── Prefer the pre-parsed JSON ──────────────────────────────────────────
if ($jsonSource) {
    $destJson = Join-Path $OutputDir (Split-Path -Leaf $jsonSource)
    # Skip copy if source and destination resolve to the same file
    $resolvedSrc  = (Resolve-Path $jsonSource).Path
    $resolvedDest = if (Test-Path $destJson) { (Resolve-Path $destJson).Path } else { $destJson }
    if ($resolvedSrc -ne $resolvedDest) {
        Copy-Item -Path $jsonSource -Destination $destJson -Force
    }
    $exportedPaths += $destJson
    $metrics = Get-Content $jsonSource -Raw | ConvertFrom-Json
    Write-Verbose "Exported pre-parsed JSON from $jsonSource"
}
elseif ($csvSource) {
    # Fallback: parse CSV into JSON using the same approach as Stop-DotnetCounters.ps1
    Write-Verbose "No JSON artifact found; parsing CSV from $csvSource"
    $rows = Get-Content $csvSource -Raw | ConvertFrom-Csv

    if ($rows.Count -gt 0) {
        # Helper mirrors the one in Stop-DotnetCounters.ps1
        function Get-CounterStats {
            param([string]$Provider, [string]$CounterName, [object[]]$Rows)

            $matching = $Rows | Where-Object {
                $_.'Provider' -eq $Provider -and
                $_.'Counter Name' -like "*$CounterName*"
            }
            if (-not $matching -or $matching.Count -eq 0) { return $null }

            $values = $matching | ForEach-Object {
                $val = $_.'Mean/Increment'
                if ($null -ne $val -and $val -ne '') { [double]$val } else { 0 }
            }
            if ($values.Count -eq 0) { return $null }

            [ordered]@{
                Avg     = [math]::Round(($values | Measure-Object -Average).Average, 2)
                Min     = [math]::Round(($values | Measure-Object -Minimum).Minimum, 2)
                Max     = [math]::Round(($values | Measure-Object -Maximum).Maximum, 2)
                Last    = [math]::Round($values[-1], 2)
                Samples = $values.Count
            }
        }

        $metrics = [ordered]@{
            TotalSamples = $rows.Count
            Runtime      = [ordered]@{
                CpuUsage          = Get-CounterStats 'System.Runtime' 'CPU Usage' $rows
                WorkingSetMB      = Get-CounterStats 'System.Runtime' 'Working Set' $rows
                GcHeapSizeMB      = Get-CounterStats 'System.Runtime' 'GC Heap Size' $rows
                Gen2Collections   = Get-CounterStats 'System.Runtime' 'Gen 2' $rows
                GcPauseRatio      = Get-CounterStats 'System.Runtime' 'time in GC' $rows
                AllocRateMB       = Get-CounterStats 'System.Runtime' 'Allocation Rate' $rows
                ExceptionCount    = Get-CounterStats 'System.Runtime' 'Exception' $rows
                ThreadPoolThreads = Get-CounterStats 'System.Runtime' 'ThreadPool Thread' $rows
            }
            AspNetCore = [ordered]@{
                RequestRate    = Get-CounterStats 'Microsoft.AspNetCore.Hosting' 'Request Rate' $rows
                FailedRequests = Get-CounterStats 'Microsoft.AspNetCore.Hosting' 'Failed Requests' $rows
            }
        }

        $destJson = Join-Path $OutputDir 'dotnet-counters.json'
        [PSCustomObject]$metrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $destJson -Encoding utf8
        $exportedPaths += $destJson
    }
}

# ── Build human-readable summary ────────────────────────────────────────
$summaryParts = @()

if ($metrics) {
    $rt = if ($metrics -is [hashtable]) { $metrics.Runtime } else { $metrics.Runtime }

    $cpuAvg   = if ($rt.CpuUsage)          { "$($rt.CpuUsage.Avg)%" }          else { 'N/A' }
    $heapMax  = if ($rt.GcHeapSizeMB)      { "$($rt.GcHeapSizeMB.Max) MB" }    else { 'N/A' }
    $gen2     = if ($rt.Gen2Collections)    { "$($rt.Gen2Collections.Last)" }   else { 'N/A' }
    $gcPause  = if ($rt.GcPauseRatio)      { "$($rt.GcPauseRatio.Max)%" }      else { 'N/A' }
    $threads  = if ($rt.ThreadPoolThreads)  { "$($rt.ThreadPoolThreads.Max)" }  else { 'N/A' }
    $allocMB  = if ($rt.AllocRateMB)       { "$($rt.AllocRateMB.Avg) MB/s" }   else { 'N/A' }

    $summaryParts += "CPU avg: $cpuAvg"
    $summaryParts += "GC heap max: $heapMax"
    $summaryParts += "Gen2 collections: $gen2"
    $summaryParts += "GC pause max: $gcPause"
    $summaryParts += "Thread pool max: $threads"
    $summaryParts += "Alloc rate avg: $allocMB"
}
else {
    $summaryParts += 'No dotnet-counters metrics available'
}

$summaryText = $summaryParts -join ' | '

return @{
    Success       = $true
    ExportedPaths = $exportedPaths
    Summary       = $summaryText
}
