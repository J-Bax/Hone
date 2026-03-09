<#
.SYNOPSIS
    Exports both CPU stacks and GC statistics from a unified PerfView ETL.
.DESCRIPTION
    Processes the single ETL.zip artifact from the merged PerfView collection:
      1. CPU stacks → folded-stack format (cpu-stacks-folded.txt)
      2. GC stats   → structured JSON report (gc-report.json)

    Returns both paths so downstream analyzers (cpu-hotspots, memory-gc) can
    consume their respective data.
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

$maxStacks = if ($Settings.ContainsKey('MaxStacks')) { $Settings.MaxStacks } else { 100 }

$cpuStacksPath = Join-Path $OutputDir 'cpu-stacks-folded.txt'
$gcReportPath  = Join-Path $OutputDir 'gc-report.json'
$exportedPaths = @()
$summaryParts  = @()

# ═══════════════════════════════════════════════════════════════════════════
# Part 1: Export CPU stacks to folded format
# ═══════════════════════════════════════════════════════════════════════════
try {
    $cpuLogPath = Join-Path $OutputDir 'perfview-cpu-export.log'
    $exportArgs = @(
        '/noGui'
        "/LogFile:$cpuLogPath"
        '/csvExport:CPU'
        "/DataFile:$etlPath"
        "/ProcessFilter:$ProcessName"
        '/AcceptEULA'
    )

    Write-Verbose "Running PerfView CPU export: $perfViewExe $($exportArgs -join ' ')"
    $exportProcess = Start-Process -FilePath $perfViewExe `
        -ArgumentList $exportArgs `
        -PassThru -WindowStyle Hidden -Wait

    # PerfView CSV export creates a file alongside the ETL with .CPU.csv suffix
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($etlPath)     # perfview.etl
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($baseName)    # perfview
    $etlDir   = Split-Path $etlPath -Parent
    $csvPath  = Join-Path $etlDir "$baseName.CPU.csv"

    $foldedLines = [System.Collections.Generic.List[string]]::new()

    if (Test-Path $csvPath) {
        Write-Verbose "Parsing CSV export: $csvPath"
        $rows = Import-Csv -Path $csvPath

        if ($rows -and @($rows).Count -gt 0) {
            $stackCol = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -match 'stack|call' }) |
                        Select-Object -First 1
            $countCol = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -match 'count|inc|sample|weight' }) |
                        Select-Object -First 1

            if ($stackCol -and $countCol) {
                $grouped = $rows | Group-Object -Property $stackCol
                foreach ($group in $grouped) {
                    $stack = $group.Name -replace '\s*>>\s*', ';' -replace '\s*->\s*', ';'
                    $count = ($group.Group | Measure-Object -Property $countCol -Sum).Sum
                    if ($count -gt 0) {
                        $foldedLines.Add("$stack $count")
                    }
                }
            }
            else {
                foreach ($row in $rows) {
                    $values = $row.PSObject.Properties.Value -join ';'
                    $foldedLines.Add("$values 1")
                }
            }
        }
    }
    else {
        $foldedLines.Add("[PerfView CPU export did not produce CSV — raw ETL available at $etlPath] 1")
        Write-Verbose "No CPU.csv found at $csvPath"
    }

    $sortedLines = $foldedLines |
        Sort-Object { [int](($_ -split '\s+')[-1]) } -Descending |
        Select-Object -First $maxStacks

    $sortedLines | Set-Content -Path $cpuStacksPath -Encoding utf8
    $exportedPaths += $cpuStacksPath

    # Build a top-5 hotspot summary
    $top5 = $sortedLines | Select-Object -First 5
    $cpuSummaryLines = @("Top CPU hotspots ($(@($sortedLines).Count) stacks):")
    $rank = 1
    foreach ($line in $top5) {
        $parts   = $line -split '\s+'
        $count   = $parts[-1]
        $frames  = ($parts[0..($parts.Count - 2)] -join ' ') -split ';'
        $topFrame = $frames[-1].Trim()
        $cpuSummaryLines += "  ${rank}. $topFrame ($count samples)"
        $rank++
    }
    $cpuSummary = $cpuSummaryLines -join "`n"
    $summaryParts += "CPU: $(@($sortedLines).Count) stacks exported"
}
catch {
    $cpuSummary = "CPU export failed: $_"
    $summaryParts += $cpuSummary
    Write-Warning $cpuSummary
}

# ═══════════════════════════════════════════════════════════════════════════
# Part 2: Export GC statistics to JSON report
# ═══════════════════════════════════════════════════════════════════════════
try {
    $gcLogPath = Join-Path $OutputDir 'perfview-gcstats.log'
    $gcStatsArgs = @(
        '/NoGui'
        "/LogFile:$gcLogPath"
        '/GCStats'
        "/DataFile:$etlPath"
        "/ProcessFilter:$ProcessName"
    )

    Write-Verbose "Running PerfView GCStats: $perfViewExe $($gcStatsArgs -join ' ')"
    $gcStatsProc = Start-Process -FilePath $perfViewExe `
        -ArgumentList $gcStatsArgs `
        -PassThru -WindowStyle Hidden -Wait

    if ($gcStatsProc.ExitCode -ne 0) {
        Write-Warning "PerfView GCStats exited with code $($gcStatsProc.ExitCode). See $gcLogPath"
    }

    # Locate the GCStats HTML output
    $etlDir      = Split-Path -Parent $etlPath
    $etlBaseName = [System.IO.Path]::GetFileNameWithoutExtension(
        [System.IO.Path]::GetFileNameWithoutExtension($etlPath)
    )

    $gcStatsHtmlCandidates = @(
        Join-Path $etlDir "$etlBaseName.GCStats.html"
        Join-Path $etlDir 'PerfViewGCStats.html'
        (Get-ChildItem -Path $etlDir -Filter '*GCStats*html' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1)?.FullName
    ) | Where-Object { $_ -and (Test-Path $_) }

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

    if ($gcStatsHtml) {
        Write-Verbose "Parsing GCStats HTML: $gcStatsHtml"
        $htmlContent = Get-Content $gcStatsHtml -Raw

        function Find-HtmlMetric {
            param([string]$Html, [string]$Pattern)
            if ($Html -match $Pattern) {
                $raw = $Matches[1] -replace '[,\s]', ''
                if ([double]::TryParse($raw, [ref]$null)) { return [double]$raw }
            }
            return $null
        }

        # Per-generation stats
        foreach ($gen in @(0, 1, 2)) {
            $genKey = "gen$gen"
            $count = Find-HtmlMetric -Html $htmlContent -Pattern "Gen\s*$gen[^<]*?(\d+[\d,]*)\s*</td>"
            if ($null -ne $count) { $report.generationStats[$genKey].count = [int]$count }

            $avgPause = Find-HtmlMetric -Html $htmlContent -Pattern "Gen\s*$gen.*?Mean.*?([\d,.]+)\s*ms"
            if ($null -ne $avgPause) { $report.generationStats[$genKey].avgPauseMs = [math]::Round($avgPause, 3) }

            $maxPause = Find-HtmlMetric -Html $htmlContent -Pattern "Gen\s*$gen.*?Max.*?([\d,.]+)\s*ms"
            if ($null -ne $maxPause) { $report.generationStats[$genKey].maxPauseMs = [math]::Round($maxPause, 3) }
        }

        # Overall pause stats
        $totalPause = Find-HtmlMetric -Html $htmlContent -Pattern 'Total\s+(?:GC\s+)?Pause.*?([\d,.]+)\s*ms'
        if ($null -ne $totalPause) { $report.pauseStats.totalPauseMs = [math]::Round($totalPause, 3) }

        $maxPauseOverall = Find-HtmlMetric -Html $htmlContent -Pattern 'Max\s+(?:GC\s+)?Pause.*?([\d,.]+)\s*ms'
        if ($null -ne $maxPauseOverall) { $report.pauseStats.maxPauseMs = [math]::Round($maxPauseOverall, 3) }

        $gcRatio = Find-HtmlMetric -Html $htmlContent -Pattern '(?:GC\s+)?(?:Pause\s+)?(?:Time|Ratio)[^<]*?([\d,.]+)\s*%'
        if ($null -ne $gcRatio) { $report.pauseStats.gcPauseRatio = [math]::Round($gcRatio, 2) }

        # Heap stats
        $peakHeap = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Peak|Max)\s+(?:Heap|GC\s+Heap)\s+Size.*?([\d,.]+)\s*MB'
        if ($null -ne $peakHeap) { $report.heapStats.peakSizeMB = [math]::Round($peakHeap, 2) }

        $avgHeap = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Avg|Average)\s+(?:Heap|GC\s+Heap).*?([\d,.]+)\s*MB'
        if ($null -ne $avgHeap) { $report.heapStats.avgSizeMB = [math]::Round($avgHeap, 2) }

        $fragmentation = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Fragmentation|Frag).*?([\d,.]+)\s*%'
        if ($null -ne $fragmentation) { $report.heapStats.fragmentationPct = [math]::Round($fragmentation, 2) }

        # Allocation stats
        $allocRate = Find-HtmlMetric -Html $htmlContent -Pattern '(?:Alloc|Allocation)\s+Rate.*?([\d,.]+)\s*MB/sec'
        if ($null -ne $allocRate) { $report.allocationStats.allocRateMBSec = [math]::Round($allocRate, 2) }

        # Allocation tick top types
        $topTypes = [System.Collections.Generic.List[object]]::new()
        $allocSection = [regex]::Match($htmlContent, '(?s)Allocation\s+Tick.*?<table.*?>(.*?)</table>')
        if ($allocSection.Success) {
            $rows = [regex]::Matches($allocSection.Groups[1].Value, '(?s)<tr>(.*?)</tr>')
            foreach ($row in $rows) {
                $cells = [regex]::Matches($row.Groups[1].Value, '<td[^>]*>(.*?)</td>')
                if ($cells.Count -ge 2) {
                    $typeName   = ($cells[0].Groups[1].Value -replace '<[^>]+>', '').Trim()
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
            $totalAlloc = ($topTypes | Measure-Object -Property allocMB -Sum).Sum
            if ($totalAlloc -gt 0) {
                foreach ($t in $topTypes) {
                    $t.pct = [math]::Round(($t.allocMB / $totalAlloc) * 100, 1)
                }
            }
            $report.allocationStats.topTypes = @($topTypes | Select-Object -First 20)
        }
    }
    else {
        Write-Warning "GCStats HTML not found in $etlDir. GC report will contain default values."
    }

    $report | ConvertTo-Json -Depth 5 | Out-File -FilePath $gcReportPath -Encoding utf8
    $exportedPaths += $gcReportPath

    $gen0Count    = $report.generationStats.gen0.count
    $gen1Count    = $report.generationStats.gen1.count
    $gen2Count    = $report.generationStats.gen2.count
    $gcPauseRatio = $report.pauseStats.gcPauseRatio
    $peakHeapMB   = $report.heapStats.peakSizeMB

    $gcSummary = "GC: Gen0=$gen0Count Gen1=$gen1Count Gen2=$gen2Count | Pause ratio=${gcPauseRatio}% | Heap peak: ${peakHeapMB}MB"
    $summaryParts += "GC: $gcSummary"
}
catch {
    $gcSummary = "GC export failed: $_"
    $summaryParts += $gcSummary
    Write-Warning $gcSummary
}

# ═══════════════════════════════════════════════════════════════════════════
# Return combined result with convenience accessors for analyzers
# ═══════════════════════════════════════════════════════════════════════════
$summaryText = $summaryParts -join ' | '
Write-Information $summaryText

return @{
    Success       = ($exportedPaths.Count -gt 0)
    ExportedPaths = $exportedPaths
    Summary       = $summaryText
    CpuStacksPath = $cpuStacksPath
    GcReportPath  = $gcReportPath
}
