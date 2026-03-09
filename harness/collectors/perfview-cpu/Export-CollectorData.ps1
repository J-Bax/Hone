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
    # Write placeholder so analyzer gets something
    "[CPU export error: $_] 1" | Set-Content -Path $cpuStacksPath -Encoding utf8
}

return @{
    Success       = (Test-Path $cpuStacksPath)
    ExportedPaths = @($cpuStacksPath)
    Summary       = $summaryText
    CpuStacksPath = $cpuStacksPath
}
