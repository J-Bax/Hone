<#
.SYNOPSIS
    Exports CPU sampling stacks from a PerfView ETL to folded-stack format.
.DESCRIPTION
    Uses PerfView's UserCommand SaveCPUStacksAsCsv to extract CPU stacks,
    then converts to folded-stack format (semicolon-delimited frames + count).

    If process-filtered export produces no stacks, retries without a process
    filter and filters during CSV parsing as a fallback.
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
$exportTimeoutSec = if ($Settings.ContainsKey('ExportTimeoutSec')) { [int]$Settings.ExportTimeoutSec } else { 300 }
$cpuStacksPath = Join-Path $OutputDir 'cpu-stacks-folded.txt'
$cpuExportSuccess = $false

# ── Helper: run a PerfView command with a timeout ───────────────────────────
function Invoke-PerfViewWithTimeout {
    param(
        [string]$PerfViewExe,
        [string[]]$Arguments,
        [int]$TimeoutSec
    )

    $proc = Start-Process -FilePath $PerfViewExe -ArgumentList $Arguments `
        -PassThru -WindowStyle Hidden
    $exited = $proc.WaitForExit($TimeoutSec * 1000)
    if (-not $exited) {
        Write-Warning "PerfView export did not complete within ${TimeoutSec}s — killing process."
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        $proc.WaitForExit(10000) | Out-Null
    }
    return $proc
}

# ── Helper: run SaveCPUStacksAsCsv and return the CSV path if produced ──────
function Invoke-SaveCPUStacksAsCsv {
    param(
        [string]$PerfViewExe,
        [string]$EtlPath,
        [string]$LogPath,
        [string]$FilterProcessName,  # empty string = no process filter
        [int]$TimeoutSec
    )

    $args_ = @(
        "/LogFile:$LogPath"
        '/AcceptEULA'
        '/NoGui'
        'UserCommand'
        'SaveCPUStacksAsCsv'
        $EtlPath
    )
    if ($FilterProcessName) {
        $args_ += $FilterProcessName
    }

    Write-Verbose "Running PerfView CPU export: $PerfViewExe $($args_ -join ' ')"

    # Remove stale CSV so we can detect whether this invocation produces it
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($EtlPath)
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($baseName)
    $etlDir   = Split-Path $EtlPath -Parent
    $csvPath  = Join-Path $etlDir "$baseName.perfView.csv"
    if (Test-Path $csvPath) { Remove-Item $csvPath -Force }

    $null = Invoke-PerfViewWithTimeout -PerfViewExe $PerfViewExe -Arguments $args_ -TimeoutSec $TimeoutSec

    # SaveCPUStacksAsCsv creates <basename>.perfView.csv alongside the ETL
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($EtlPath)
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($baseName)
    $etlDir   = Split-Path $EtlPath -Parent
    $csvPath  = Join-Path $etlDir "$baseName.perfView.csv"

    if (Test-Path $csvPath) { return $csvPath }
    return $null
}

# ── Helper: parse CSV into folded-stack lines ───────────────────────────────
function ConvertTo-FoldedStacks {
    param(
        [string]$CsvPath,
        [string]$FilterProcessName  # non-empty = filter rows to this process's module
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $rows = Import-Csv -Path $CsvPath
    if (-not $rows -or @($rows).Count -eq 0) { return $lines }

    # SaveCPUStacksAsCsv outputs: Name,Exc,Exc%,Inc,Inc%,Fold,First,Last
    $nameCol = ($rows[0].PSObject.Properties.Name |
        Where-Object { $_ -eq 'Name' -or $_ -match 'stack|call' }) |
        Select-Object -First 1
    $countCol = ($rows[0].PSObject.Properties.Name |
        Where-Object { $_ -eq 'Exc' -or $_ -match 'count|sample|weight' }) |
        Select-Object -First 1

    if (-not $nameCol) { $nameCol = $rows[0].PSObject.Properties.Name[0] }
    if (-not $countCol) { $countCol = 'Inc' }

    # When exported without a process filter, the CSV is a flat roll-up of
    # methods from ALL processes. The Name column is "module!method". Filter
    # by excluding rows from clearly unrelated processes (e.g. k6, conhost)
    # while keeping the target process and shared framework modules.
    $excludeModules = @('k6', 'conhost', 'searchfilterhost', 'searchindexer',
        'teracopyservice', 'explorer', 'dwm', 'csrss', 'wininit', 'lsass')

    foreach ($row in $rows) {
        $name = $row.$nameCol
        if (-not $name -or $name -eq 'ROOT') { continue }

        if ($FilterProcessName) {
            $module = ($name -split '!')[0].ToLowerInvariant()
            if ($module -in $excludeModules) { continue }
        }

        $rawCount = $row.$countCol -replace '[,\s]', ''
        if ([double]::TryParse($rawCount, [ref]$null)) {
            $count = [double]$rawCount
            if ($count -gt 0) {
                $lines.Add("$name $([int]$count)")
            }
        }
    }

    return $lines
}

try {
    $cpuLogPath = Join-Path $OutputDir 'perfview-cpu-export.log'

    # ── Attempt 1: Export with process name filter ──────────────────────────
    $csvPath = Invoke-SaveCPUStacksAsCsv -PerfViewExe $perfViewExe `
        -EtlPath $etlPath -LogPath $cpuLogPath -FilterProcessName $ProcessName `
        -TimeoutSec $exportTimeoutSec

    $foldedLines = @()
    $usedFallback = $false

    if ($csvPath) {
        Write-Verbose "Parsing process-filtered CSV: $csvPath"
        $foldedLines = @(ConvertTo-FoldedStacks -CsvPath $csvPath -FilterProcessName '')
    }

    # ── Attempt 2: Retry without process filter ────────────────────────────
    # PerfView's process name lookup can occasionally fail; retry unfiltered.
    if ($foldedLines.Count -eq 0) {
        Write-Verbose "Process-filtered export produced no stacks — retrying without process filter"

        $fallbackLogPath = Join-Path $OutputDir 'perfview-cpu-export-fallback.log'
        $csvPath = Invoke-SaveCPUStacksAsCsv -PerfViewExe $perfViewExe `
            -EtlPath $etlPath -LogPath $fallbackLogPath -FilterProcessName '' `
            -TimeoutSec $exportTimeoutSec

        if ($csvPath) {
            Write-Verbose "Parsing unfiltered CSV with module-level filter: $csvPath"
            $foldedLines = @(ConvertTo-FoldedStacks -CsvPath $csvPath -FilterProcessName $ProcessName)
            $usedFallback = $true
        }
    }

    if ($foldedLines.Count -gt 0) {
        $cpuExportSuccess = $true
    }

    $sortedLines = @($foldedLines |
        Sort-Object { [int](($_ -split '\s+')[-1]) } -Descending |
        Select-Object -First $maxStacks)

    if ($sortedLines.Count -gt 0) {
        $sortedLines | Set-Content -Path $cpuStacksPath -Encoding utf8
    }
    else {
        "[PerfView CPU export did not produce stacks — raw ETL available at $etlPath] 1" |
            Set-Content -Path $cpuStacksPath -Encoding utf8
    }

    $fallbackNote = if ($usedFallback) { ' (fallback: unfiltered export)' } else { '' }
    $summaryText = "CPU: $(@($sortedLines).Count) stacks exported${fallbackNote}"
}
catch {
    $summaryText = "CPU export failed: $_"
    Write-Warning $summaryText
    "[CPU export error: $_] 1" | Set-Content -Path $cpuStacksPath -Encoding utf8
}

return @{
    Success       = $cpuExportSuccess
    ExportedPaths = @($cpuStacksPath)
    Summary       = $summaryText
    CpuStacksPath = $cpuStacksPath
}
