<#
.SYNOPSIS
    Exports GC statistics from a PerfView ETL to a structured JSON report.
.DESCRIPTION
    Uses PerfView's UserCommand GCStats to generate an HTML report, then parses
    the HTML to extract per-generation stats, pause times, and heap metrics into
    a machine-readable JSON format.
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
    return @{
        Success       = $false
        Summary       = "ETL artifact not found: $etlPath"
        ExportedPaths = @()
    }
}

$perfViewExe = $Settings.PerfViewExePath
if (-not $perfViewExe) {
    return @{
        Success       = $false
        Summary       = 'PerfViewExePath not specified in settings'
        ExportedPaths = @()
    }
}
if (-not [System.IO.Path]::IsPathRooted($perfViewExe)) {
    $repoRoot = Split-Path -Parent $harnessRoot
    $perfViewExe = Join-Path $repoRoot $perfViewExe
}
if (-not (Test-Path $perfViewExe)) {
    return @{
        Success       = $false
        Summary       = "PerfView executable not found: $perfViewExe"
        ExportedPaths = @()
    }
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$gcReportPath = Join-Path $OutputDir 'gc-report.json'

try {
    $gcLogPath = Join-Path $OutputDir 'perfview-gcstats.log'
    $gcStatsArgs = @(
        "/LogFile:$gcLogPath"
        '/AcceptEULA'
        '/NoGui'
        'UserCommand'
        'GCStats'
        $etlPath
    )

    Write-Verbose "Running PerfView GCStats: $perfViewExe $($gcStatsArgs -join ' ')"
    $gcStatsProc = Start-Process -FilePath $perfViewExe `
        -ArgumentList $gcStatsArgs `
        -PassThru -WindowStyle Hidden -Wait

    # PerfView UserCommand GCStats may exit non-zero due to NullReferenceException
    # even when it successfully writes the HTML — check for the output file instead
    if ($gcStatsProc.ExitCode -ne 0) {
        Write-Warning "PerfView GCStats exited with code $($gcStatsProc.ExitCode). See $gcLogPath"
    }

    # Locate the GCStats HTML — PerfView writes to temp dir alongside unzipped ETL
    $etlDir      = Split-Path -Parent $etlPath
    $etlBaseName = [System.IO.Path]::GetFileNameWithoutExtension(
        [System.IO.Path]::GetFileNameWithoutExtension($etlPath)
    )

    $pvTempDir = Join-Path $env:LOCALAPPDATA 'Temp\PerfView'
    $gcStatsHtmlCandidates = @(
        Join-Path $etlDir "$etlBaseName.gcStats.html"
        Join-Path $etlDir "$etlBaseName.GCStats.html"
    )
    if (Test-Path $pvTempDir) {
        $gcStatsHtmlCandidates += @(
            Get-ChildItem -Path $pvTempDir -Filter "$etlBaseName*.gcStats.html" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        ) | Where-Object { $_ } | ForEach-Object { $_.FullName }
    }
    $gcStatsHtmlCandidates += @(
        (Get-ChildItem -Path $etlDir -Filter '*GCStats*html' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1)?.FullName
    )
    $gcStatsHtmlCandidates = $gcStatsHtmlCandidates | Where-Object { $_ -and (Test-Path $_) }
    $gcStatsHtml = $gcStatsHtmlCandidates | Select-Object -First 1

    # Initialize report structure
    $report = [ordered]@{
        generationStats = [ordered]@{
            gen0 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
            gen1 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
            gen2 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
        }
        heapStats = [ordered]@{
            peakSizeMB       = 0.0
            totalAllocMB     = 0.0
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

    if ($gcStatsHtml) {
        Write-Verbose "Parsing GCStats HTML: $gcStatsHtml"
        $htmlContent = Get-Content $gcStatsHtml -Raw

        # Scope to the target process section (between HR tags)
        $processSection = $htmlContent
        $hrBlocks = [regex]::Split($htmlContent, '<HR\s*/?\s*>')
        foreach ($block in $hrBlocks) {
            if ($block -match [regex]::Escape($ProcessName) -and $block -match 'GC Rollup') {
                $processSection = $block
                break
            }
        }

        function Find-HtmlMetric {
            param([string]$Html, [string]$Pattern)
            if ($Html -match $Pattern) {
                $raw = $Matches[1] -replace '[,\s]', ''
                if ([double]::TryParse($raw, [ref]$null)) { return [double]$raw }
            }
            return $null
        }

        # Parse the GC Rollup table rows:
        # Gen, Count, MaxPause, MaxPeakMB, MaxAllocMBSec, TotalPause, TotalAllocMB, ..., MeanPause, Induced
        $tableRows = [regex]::Matches($processSection, '(?si)<TR[^>]*>(.*?)</TR>')
        foreach ($tr in $tableRows) {
            $cells = [regex]::Matches($tr.Groups[1].Value, '(?si)<TD[^>]*>(.*?)</TD>')
            if ($cells.Count -ge 10) {
                $genVal = ($cells[0].Groups[1].Value -replace '<[^>]+>', '').Trim()
                $countRaw    = ($cells[1].Groups[1].Value -replace '<[^>]+>|[,\s]', '').Trim()
                $maxPauseRaw = ($cells[2].Groups[1].Value -replace '<[^>]+>|[,\s]', '').Trim()
                $meanPauseRaw = ($cells[9].Groups[1].Value -replace '<[^>]+>|[,\s]', '').Trim()

                if ($genVal -match '^[012]$') {
                    $genKey = "gen$genVal"
                    if ([int]::TryParse($countRaw, [ref]$null)) {
                        $report.generationStats[$genKey].count = [int]$countRaw
                    }
                    if ([double]::TryParse($maxPauseRaw, [ref]$null) -and $maxPauseRaw -ne 'NaN') {
                        $report.generationStats[$genKey].maxPauseMs = [math]::Round([double]$maxPauseRaw, 3)
                    }
                    if ([double]::TryParse($meanPauseRaw, [ref]$null) -and $meanPauseRaw -ne 'NaN') {
                        $report.generationStats[$genKey].avgPauseMs = [math]::Round([double]$meanPauseRaw, 3)
                    }
                }
                elseif ($genVal -eq 'ALL') {
                    if ([double]::TryParse($maxPauseRaw, [ref]$null)) {
                        $report.pauseStats.maxPauseMs = [math]::Round([double]$maxPauseRaw, 3)
                    }
                }
            }
        }

        # Summary stats from <LI> items
        $totalPause = Find-HtmlMetric -Html $processSection -Pattern 'Total\s+GC\s+Pause:\s*([\d,.]+)\s*msec'
        if ($null -ne $totalPause) { $report.pauseStats.totalPauseMs = [math]::Round($totalPause, 3) }

        $gcRatio = Find-HtmlMetric -Html $processSection -Pattern '%\s*Time\s+paused\s+for\s+Garbage\s+Collection:\s*([\d,.]+)%'
        if ($null -ne $gcRatio) { $report.pauseStats.gcPauseRatio = [math]::Round($gcRatio, 2) }

        $peakHeap = Find-HtmlMetric -Html $processSection -Pattern 'Max\s+GC\s+Heap\s+Size:\s*([\d,.]+)\s*MB'
        if ($null -ne $peakHeap) { $report.heapStats.peakSizeMB = [math]::Round($peakHeap, 2) }

        $totalAllocs = Find-HtmlMetric -Html $processSection -Pattern 'Total\s+Allocs\s*:\s*([\d,.]+)\s*MB'
        if ($null -ne $totalAllocs) { $report.heapStats.totalAllocMB = [math]::Round($totalAllocs, 2) }

        $allocRate = Find-HtmlMetric -Html $processSection -Pattern 'Alloc.*?Rate.*?([\d,.]+)\s*MB/sec'
        if ($null -ne $allocRate) { $report.allocationStats.allocRateMBSec = [math]::Round($allocRate, 2) }
    }
    else {
        Write-Warning "GCStats HTML not found. GC report will contain default values."
    }

    $report | ConvertTo-Json -Depth 5 | Out-File -FilePath $gcReportPath -Encoding utf8

    $gen0Count    = $report.generationStats.gen0.count
    $gen1Count    = $report.generationStats.gen1.count
    $gen2Count    = $report.generationStats.gen2.count
    $gcPauseRatio = $report.pauseStats.gcPauseRatio
    $peakHeapMB   = $report.heapStats.peakSizeMB

    $summaryText = "GC: Gen0=$gen0Count Gen1=$gen1Count Gen2=$gen2Count | Pause ratio=${gcPauseRatio}% | Heap peak: ${peakHeapMB}MB"
}
catch {
    $summaryText = "GC export failed: $_"
    Write-Warning $summaryText
    # Write default report so analyzer gets something
    [ordered]@{
        generationStats = [ordered]@{
            gen0 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
            gen1 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
            gen2 = [ordered]@{ count = 0; avgPauseMs = 0.0; maxPauseMs = 0.0 }
        }
        heapStats = [ordered]@{ peakSizeMB = 0.0; totalAllocMB = 0.0; fragmentationPct = 0.0 }
        pauseStats = [ordered]@{ totalPauseMs = 0.0; maxPauseMs = 0.0; gcPauseRatio = 0.0 }
        allocationStats = [ordered]@{ allocRateMBSec = 0.0; topTypes = @() }
    } | ConvertTo-Json -Depth 5 | Out-File -FilePath $gcReportPath -Encoding utf8
}

return @{
    Success       = (Test-Path $gcReportPath)
    ExportedPaths = @($gcReportPath)
    Summary       = $summaryText
    GcReportPath  = $gcReportPath
}
