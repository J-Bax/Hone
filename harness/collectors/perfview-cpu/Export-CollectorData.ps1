<#
.SYNOPSIS
    Exports CPU sampling stacks from a PerfView ETL to folded-stack format.
.DESCRIPTION
    Uses PerfView's UserCommand SaveCPUStacksAsCsv to extract CPU stacks,
    then converts to folded-stack format (semicolon-delimited frames + count).
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

try {
    $cpuLogPath = Join-Path $OutputDir 'perfview-cpu-export.log'
    $exportArgs = @(
        "/LogFile:$cpuLogPath"
        '/AcceptEULA'
        '/NoGui'
        'UserCommand'
        'SaveCPUStacksAsCsv'
        $etlPath
        $ProcessName
    )

    Write-Verbose "Running PerfView CPU export: $perfViewExe $($exportArgs -join ' ')"
    $exportProcess = Start-Process -FilePath $perfViewExe `
        -ArgumentList $exportArgs `
        -PassThru -WindowStyle Hidden -Wait

    # SaveCPUStacksAsCsv creates <basename>.perfView.csv alongside the ETL
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($etlPath)
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($baseName)
    $etlDir   = Split-Path $etlPath -Parent
    $csvPath  = Join-Path $etlDir "$baseName.perfView.csv"

    $foldedLines = [System.Collections.Generic.List[string]]::new()

    if (Test-Path $csvPath) {
        Write-Verbose "Parsing CSV export: $csvPath"
        $rows = Import-Csv -Path $csvPath

        if ($rows -and @($rows).Count -gt 0) {
            # SaveCPUStacksAsCsv outputs: Name,Exc,Exc%,Inc,Inc%,Fold,First,Last
            $nameCol = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -eq 'Name' -or $_ -match 'stack|call' }) |
                        Select-Object -First 1
            $countCol = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -eq 'Exc' -or $_ -match 'count|sample|weight' }) |
                        Select-Object -First 1

            if (-not $nameCol) { $nameCol = $rows[0].PSObject.Properties.Name[0] }
            if (-not $countCol) { $countCol = 'Inc' }

            foreach ($row in $rows) {
                $name = $row.$nameCol
                if (-not $name -or $name -eq 'ROOT') { continue }
                $rawCount = $row.$countCol -replace '[,\s]', ''
                if ([double]::TryParse($rawCount, [ref]$null)) {
                    $count = [double]$rawCount
                    if ($count -gt 0) {
                        $foldedLines.Add("$name $([int]$count)")
                    }
                }
            }
        }
    }
    else {
        $foldedLines.Add("[PerfView CPU export did not produce CSV — raw ETL available at $etlPath] 1")
        Write-Verbose "No perfView.csv found at $csvPath"
    }

    $sortedLines = $foldedLines |
        Sort-Object { [int](($_ -split '\s+')[-1]) } -Descending |
        Select-Object -First $maxStacks

    $sortedLines | Set-Content -Path $cpuStacksPath -Encoding utf8

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
    $summaryText = "CPU: $(@($sortedLines).Count) stacks exported"
}
catch {
    $summaryText = "CPU export failed: $_"
    Write-Warning $summaryText
    "[CPU export error: $_] 1" | Set-Content -Path $cpuStacksPath -Encoding utf8
}

# ═══════════════════════════════════════════════════════════════════════════
# Part 2: Extract allocation tick types from same ETL via GCStats
# ═══════════════════════════════════════════════════════════════════════════
$allocTypesPath = Join-Path $OutputDir 'alloc-types.json'
$allocSummary = ''

try {
    $gcLogPath = Join-Path $OutputDir 'perfview-gcstats-alloc.log'
    $gcStatsArgs = @(
        "/LogFile:$gcLogPath"
        '/AcceptEULA'
        '/NoGui'
        'UserCommand'
        'GCStats'
        $etlPath
    )

    Write-Verbose "Running PerfView GCStats for allocation data: $perfViewExe $($gcStatsArgs -join ' ')"
    $gcProc = Start-Process -FilePath $perfViewExe `
        -ArgumentList $gcStatsArgs `
        -PassThru -WindowStyle Hidden -Wait

    # Find the GCStats HTML (same search logic as perfview-gc Export)
    $etlDir      = Split-Path -Parent $etlPath
    $etlBaseName = [System.IO.Path]::GetFileNameWithoutExtension(
        [System.IO.Path]::GetFileNameWithoutExtension($etlPath)
    )
    $pvTempDir = Join-Path $env:LOCALAPPDATA 'Temp\PerfView'
    $htmlCandidates = @(
        Join-Path $etlDir "$etlBaseName.gcStats.html"
        Join-Path $etlDir "$etlBaseName.GCStats.html"
    )
    if (Test-Path $pvTempDir) {
        $htmlCandidates += @(
            Get-ChildItem -Path $pvTempDir -Filter "$etlBaseName*.gcStats.html" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        ) | Where-Object { $_ } | ForEach-Object { $_.FullName }
    }
    $gcStatsHtml = $htmlCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    $topTypes = @()

    if ($gcStatsHtml) {
        $htmlContent = Get-Content $gcStatsHtml -Raw

        # Scope to the target process section (block with ProcessName AND GC Rollup)
        $hrBlocks = [regex]::Split($htmlContent, '<HR\s*/?\s*>')
        $processSection = $null
        foreach ($block in $hrBlocks) {
            if ($block -match [regex]::Escape($ProcessName) -and $block -match 'GC Rollup') {
                $processSection = $block
                break
            }
        }

        if (-not $processSection) { $processSection = $htmlContent }

        # Parse "Allocation Tick" table — PerfView renders this when /DotNetAllocSampled events exist
        # Look for a table following "Allocation Tick" text
        $allocMatch = [regex]::Match($processSection, '(?si)Allocation\s+Tick[^<]*<[^t]*<table[^>]*>(.*?)</table>')
        if ($allocMatch.Success) {
            $tableHtml = $allocMatch.Groups[1].Value
            $rows = [regex]::Matches($tableHtml, '(?si)<tr[^>]*>(.*?)</tr>')
            foreach ($row in $rows) {
                $cells = [regex]::Matches($row.Groups[1].Value, '(?si)<td[^>]*>(.*?)</td>')
                if ($cells.Count -ge 2) {
                    $typeName   = ($cells[0].Groups[1].Value -replace '<[^>]+>', '').Trim()
                    $allocMBRaw = ($cells[1].Groups[1].Value -replace '<[^>]+>|[,\s]', '').Trim()
                    if ($typeName -and $allocMBRaw -and [double]::TryParse($allocMBRaw, [ref]$null)) {
                        $topTypes += [ordered]@{
                            type    = $typeName
                            allocMB = [math]::Round([double]$allocMBRaw, 2)
                        }
                    }
                }
            }
            # Compute percentages
            $totalAlloc = ($topTypes | Measure-Object -Property allocMB -Sum).Sum
            if ($totalAlloc -gt 0) {
                foreach ($t in $topTypes) {
                    $t['pct'] = [math]::Round(($t.allocMB / $totalAlloc) * 100, 1)
                }
            }
            $topTypes = @($topTypes | Select-Object -First 20)
        }
    }

    @{ topAllocatingTypes = $topTypes } | ConvertTo-Json -Depth 5 |
        Out-File -FilePath $allocTypesPath -Encoding utf8

    if ($topTypes.Count -gt 0) {
        $allocSummary = "Alloc: $($topTypes.Count) types"
    }
    else {
        $allocSummary = 'Alloc: no allocation tick data'
    }
}
catch {
    $allocSummary = "Alloc export failed: $_"
    Write-Warning $allocSummary
    @{ topAllocatingTypes = @() } | ConvertTo-Json -Depth 5 |
        Out-File -FilePath $allocTypesPath -Encoding utf8
}

$fullSummary = @($summaryText, $allocSummary) -join ' | '

return @{
    Success        = (Test-Path $cpuStacksPath)
    ExportedPaths  = @($cpuStacksPath, $allocTypesPath)
    Summary        = $fullSummary
    CpuStacksPath  = $cpuStacksPath
    AllocTypesPath = $allocTypesPath
}
