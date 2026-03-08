<#
.SYNOPSIS
    Exports structured GC metrics from a PerfView GC trace.

.DESCRIPTION
    Runs PerfView's GCStats command against the collected ETL.zip, parses
    the resulting HTML report, and produces a structured JSON report with
    generation statistics, heap sizes, pause times, and allocation data.

.PARAMETER ArtifactPaths
    Array containing the path to the PerfView ETL.zip file.

.PARAMETER OutputDir
    Directory where the gc-report.json will be written.

.PARAMETER ProcessName
    Name of the API process to filter in PerfView output.

.PARAMETER Settings
    Settings hashtable including PerfViewExePath.

.OUTPUTS
    Hashtable with Success, ExportedPaths, and Summary.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$ArtifactPaths,

    [Parameter(Mandatory)]
    [string]$OutputDir,

    [Parameter(Mandatory)]
    [string]$ProcessName,

    [Parameter(Mandatory)]
    [hashtable]$Settings
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$harnessRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# ── Resolve paths ───────────────────────────────────────────────────────────
$etlPath = $ArtifactPaths | Select-Object -First 1
if (-not $etlPath -or -not (Test-Path $etlPath)) {
    Write-Warning "ETL artifact not found: $etlPath"
    return @{ Success = $false; ExportedPaths = @(); Summary = 'ETL artifact missing' }
}

$perfViewExe = $Settings.PerfViewExePath
if (-not $perfViewExe) {
    Write-Warning 'PerfViewExePath not specified in settings'
    return @{ Success = $false; ExportedPaths = @(); Summary = 'PerfView path not configured' }
}

if (-not [System.IO.Path]::IsPathRooted($perfViewExe)) {
    $repoRoot = Split-Path -Parent $harnessRoot
    $perfViewExe = Join-Path $repoRoot $perfViewExe
}

if (-not (Test-Path $perfViewExe)) {
    Write-Warning "PerfView not found at: $perfViewExe"
    return @{ Success = $false; ExportedPaths = @(); Summary = 'PerfView executable missing' }
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$gcReportPath = Join-Path $OutputDir 'gc-report.json'
$logPath = Join-Path $OutputDir 'perfview-gcstats.log'

# ── Run PerfView GCStats ────────────────────────────────────────────────────
Write-Information "Running PerfView GCStats on $etlPath (filter: $ProcessName)..."

try {
    $gcStatsArgs = @(
        '/NoGui'
        "/LogFile:$logPath"
        '/GCStats'
        "/DataFile:$etlPath"
        "/ProcessFilter:$ProcessName"
    )

    $gcStatsProc = Start-Process -FilePath $perfViewExe `
        -ArgumentList $gcStatsArgs `
        -PassThru -WindowStyle Hidden -Wait

    if ($gcStatsProc.ExitCode -ne 0) {
        Write-Warning "PerfView GCStats exited with code $($gcStatsProc.ExitCode). See $logPath"
    }
}
catch {
    Write-Warning "Failed to run PerfView GCStats: $_"
    return @{ Success = $false; ExportedPaths = @(); Summary = "GCStats failed: $_" }
}

# ── Locate the GCStats HTML output ─────────────────────────────────────────
# PerfView places GCStats HTML alongside or inside the ETL directory
$etlDir = Split-Path -Parent $etlPath
$etlBaseName = [System.IO.Path]::GetFileNameWithoutExtension(
    [System.IO.Path]::GetFileNameWithoutExtension($etlPath)  # strip .etl from .etl.zip
)

$gcStatsHtmlCandidates = @(
    Join-Path $etlDir "$etlBaseName.GCStats.html"
    Join-Path $etlDir "PerfViewGCStats.html"
    (Get-ChildItem -Path $etlDir -Filter '*GCStats*html' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1)?.FullName
) | Where-Object { $_ -and (Test-Path $_) }

$gcStatsHtml = $gcStatsHtmlCandidates | Select-Object -First 1

# ── Initialize report structure ─────────────────────────────────────────────
$report = [ordered]@{
    generationStats = [ordered]@{
        gen0 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
        gen1 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
        gen2 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
    }
    heapStats = [ordered]@{
        peakSizeMB       = 0.0
        avgSizeMB        = 0.0
        fragmentationPct = 0.0
    }
    pauseStats = [ordered]@{
        totalPauseMs = 0.0
        maxPauseMs   = 0.0
        gcPauseRatio = 0.0
    }
    allocationStats = [ordered]@{
        allocRateMBSec = 0.0
        topTypes       = @()
    }
}

# ── Parse GCStats HTML ──────────────────────────────────────────────────────
if ($gcStatsHtml) {
    Write-Verbose "Parsing GCStats HTML: $gcStatsHtml"
    $htmlContent = Get-Content $gcStatsHtml -Raw

    # Helper: extract a numeric value near a label from HTML content
    function Find-HtmlMetric {
        param([string]$Html, [string]$Pattern)
        if ($Html -match $Pattern) {
            $raw = $Matches[1] -replace '[,\s]', ''
            if ([double]::TryParse($raw, [ref]$null)) {
                return [double]$raw
            }
        }
        return $null
    }

    # ── Generation counts and pause times ───────────────────────────────────
    # PerfView GCStats typically has a summary table with per-generation rows.
    # Pattern: Gen N | Count | ... | Mean (ms) | Max (ms) | ...
    foreach ($gen in @(0, 1, 2)) {
        $genKey = "gen$gen"

        # Match generation row — flexible pattern for PerfView HTML table rows
        $countPattern = "Gen\s*$gen[^<]*?(\d+[\d,]*)\s*</td>"
        $count = Find-HtmlMetric -Html $htmlContent -Pattern $countPattern
        if ($null -ne $count) {
            $report.generationStats[$genKey].count = [int]$count
        }

        # Try to extract pause times from generation-specific data
        # PerfView formats vary; try common patterns
        $pausePattern = "Gen\s*$gen.*?Mean.*?([\d,.]+)\s*ms"
        $avgPause = Find-HtmlMetric -Html $htmlContent -Pattern $pausePattern
        if ($null -ne $avgPause) {
            $report.generationStats[$genKey].avgPauseMs = [math]::Round($avgPause, 3)
        }

        $maxPausePattern = "Gen\s*$gen.*?Max.*?([\d,.]+)\s*ms"
        $maxPause = Find-HtmlMetric -Html $htmlContent -Pattern $maxPausePattern
        if ($null -ne $maxPause) {
            $report.generationStats[$genKey].maxPauseMs = [math]::Round($maxPause, 3)
        }
    }

    # ── Overall pause statistics ────────────────────────────────────────────
    $totalPause = Find-HtmlMetric -Html $htmlContent -Pattern 'Total\s+(?:GC\s+)?Pause.*?([\d,.]+)\s*ms'
    if ($null -ne $totalPause) {
        $report.pauseStats.totalPauseMs = [math]::Round($totalPause, 3)
    }

    $maxPauseOverall = Find-HtmlMetric -Html $htmlContent -Pattern 'Max\s+(?:GC\s+)?Pause.*?([\d,.]+)\s*ms'
    if ($null -ne $maxPauseOverall) {
        $report.pauseStats.maxPauseMs = [math]::Round($maxPauseOverall, 3)
    }

    $gcRatio = Find-HtmlMetric -Html $htmlContent -Pattern '(?:GC\s+)?(?:Pause\s+)?(?:Time|Ratio)[^<]*?([\d,.]+)\s*%'
    if ($null -ne $gcRatio) {
        $report.pauseStats.gcPauseRatio = [math]::Round($gcRatio, 2)
    }

    # ── Heap statistics ─────────────────────────────────────────────────────
    $peakHeap = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Peak|Max)\s+(?:Heap|GC\s+Heap)\s+Size.*?([\d,.]+)\s*MB'
    if ($null -ne $peakHeap) {
        $report.heapStats.peakSizeMB = [math]::Round($peakHeap, 2)
    }

    $avgHeap = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Avg|Average)\s+(?:Heap|GC\s+Heap).*?([\d,.]+)\s*MB'
    if ($null -ne $avgHeap) {
        $report.heapStats.avgSizeMB = [math]::Round($avgHeap, 2)
    }

    $fragmentation = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Fragmentation|Frag).*?([\d,.]+)\s*%'
    if ($null -ne $fragmentation) {
        $report.heapStats.fragmentationPct = [math]::Round($fragmentation, 2)
    }

    # ── Allocation statistics ───────────────────────────────────────────────
    $allocRate = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Alloc|Allocation)\s+Rate.*?([\d,.]+)\s*MB/sec'
    if ($null -ne $allocRate) {
        $report.allocationStats.allocRateMBSec = [math]::Round($allocRate, 2)
    }

    # ── Allocation tick data (top allocated types) ──────────────────────────
    # PerfView may include allocation tick data if ClrEvents:GC was enabled.
    # This data appears in a separate section or may require TraceLog analysis.
    # Parse if present; common format is a table with Type | Size columns.
    $topTypes = [System.Collections.Generic.List[object]]::new()
    $allocSection = [regex]::Match($htmlContent, '(?s)Allocation\s+Tick.*?<table.*?>(.*?)</table>')
    if ($allocSection.Success) {
        $rows = [regex]::Matches($allocSection.Groups[1].Value, '(?s)<tr>(.*?)</tr>')
        foreach ($row in $rows) {
            $cells = [regex]::Matches($row.Groups[1].Value, '<td[^>]*>(.*?)</td>')
            if ($cells.Count -ge 2) {
                $typeName = ($cells[0].Groups[1].Value -replace '<[^>]+>', '').Trim()
                $allocMBRaw = ($cells[1].Groups[1].Value -replace '<[^>]+>|[,\s]', '').Trim()

                if ($typeName -and $allocMBRaw -and [double]::TryParse($allocMBRaw, [ref]$null)) {
                    $topTypes.Add([ordered]@{
                        type    = $typeName
                        allocMB = [math]::Round([double]$allocMBRaw, 2)
                        pct     = 0.0
                    })
                }
            }
        }

        # Calculate percentages from total
        $totalAlloc = ($topTypes | Measure-Object -Property allocMB -Sum).Sum
        if ($totalAlloc -gt 0) {
            foreach ($t in $topTypes) {
                $t.pct = [math]::Round(($t.allocMB / $totalAlloc) * 100, 1)
            }
        }

        $report.allocationStats.topTypes = @($topTypes | Select-Object -First 20)
    }
    else {
        Write-Verbose 'No allocation tick table found in GCStats HTML. Allocation tick data may require /ClrEvents:GC analysis via TraceLog.'
    }
}
else {
    Write-Warning "GCStats HTML not found in $etlDir. Report will contain default values. Check $logPath for PerfView output."
}

# ── Write JSON report ───────────────────────────────────────────────────────
$report | ConvertTo-Json -Depth 5 | Out-File -FilePath $gcReportPath -Encoding utf8

Write-Information "GC report written to: $gcReportPath"

# ── Build summary ───────────────────────────────────────────────────────────
$gen0Count = $report.generationStats.gen0.count
$gen1Count = $report.generationStats.gen1.count
$gen2Count = $report.generationStats.gen2.count
$totalPauseMs = $report.pauseStats.totalPauseMs
$maxPauseMs = $report.pauseStats.maxPauseMs
$gcPauseRatio = $report.pauseStats.gcPauseRatio
$peakHeapMB = $report.heapStats.peakSizeMB
$allocRate = $report.allocationStats.allocRateMBSec

$summaryText = "GC: Gen0=$gen0Count Gen1=$gen1Count Gen2=$gen2Count | " +
               "Pause: total=${totalPauseMs}ms max=${maxPauseMs}ms ratio=${gcPauseRatio}% | " +
               "Heap peak: ${peakHeapMB}MB | Alloc rate: ${allocRate}MB/s"

Write-Information $summaryText

return @{
    Success       = $true
    ExportedPaths = @($gcReportPath)
    Summary       = $summaryText
}
