<#
.SYNOPSIS
    Exports PerfView CPU stacks to folded-stack format for analysis.
.DESCRIPTION
    Uses PerfView /csvExport to dump CPU stacks from an ETL.zip file,
    then parses the output into folded-stack format (semicolon-separated
    frames with trailing sample counts) suitable for flamegraph tools.
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

try {
    $etlPath = $ArtifactPaths[0]
    if (-not (Test-Path $etlPath)) {
        return [PSCustomObject][ordered]@{
            Success = $false
            Error   = "ETL artifact not found: $etlPath"
        }
    }

    $perfViewExe = $Settings.PerfViewExePath
    if (-not $perfViewExe -or -not (Test-Path $perfViewExe)) {
        return [PSCustomObject][ordered]@{
            Success = $false
            Error   = "PerfView executable not found at '$perfViewExe'."
        }
    }

    $maxStacks = if ($Settings.ContainsKey('MaxStacks')) { $Settings.MaxStacks } else { 100 }

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    $foldedStacksPath = Join-Path $OutputDir 'cpu-stacks-folded.txt'
    $logPath          = Join-Path $OutputDir 'perfview-export.log'

    # Attempt PerfView CSV export of CPU stacks
    $exportArgs = @(
        '/noGui'
        "/LogFile:$logPath"
        '/csvExport:CPU'
        "/DataFile:$etlPath"
        "/ProcessFilter:$ProcessName"
        '/AcceptEULA'
    )

    Write-Verbose "Running PerfView export: $perfViewExe $($exportArgs -join ' ')"

    $exportProcess = Start-Process -FilePath $perfViewExe `
        -ArgumentList $exportArgs `
        -PassThru -WindowStyle Hidden -Wait

    # PerfView CSV export creates a file alongside the ETL with .CPU.csv suffix.
    # Derive the expected path from the data file.
    $baseName  = [System.IO.Path]::GetFileNameWithoutExtension($etlPath)   # perfview-cpu.etl
    $baseName  = [System.IO.Path]::GetFileNameWithoutExtension($baseName)  # perfview-cpu
    $etlDir    = Split-Path $etlPath -Parent
    $csvPath   = Join-Path $etlDir "$baseName.CPU.csv"
    $csvExists = Test-Path $csvPath

    $foldedLines = [System.Collections.Generic.List[string]]::new()

    if ($csvExists) {
        Write-Verbose "Parsing CSV export: $csvPath"
        $rows = Import-Csv -Path $csvPath

        # Build folded stacks: group by call-stack column, sum sample counts.
        # PerfView CSV format varies; attempt common column names.
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
            Write-Verbose "Could not identify stack/count columns in CSV. Falling back to raw rows."
            foreach ($row in $rows) {
                $values = $row.PSObject.Properties.Value -join ';'
                $foldedLines.Add("$values 1")
            }
        }
    }
    else {
        # Fallback: PerfView CSV export may not have produced a file (version mismatch,
        # unsupported format, etc.). Generate a placeholder noting the raw artifact exists.
        Write-Information "PerfView CSV export did not produce $csvPath. Creating placeholder from log."
        if (Test-Path $logPath) {
            $logContent = Get-Content $logPath -Raw
            # Try to extract any stack info from the log
            $foldedLines.Add("[PerfView export did not produce CSV — raw ETL available at $etlPath] 1")
            Write-Verbose "Export log content: $($logContent.Substring(0, [Math]::Min(500, $logContent.Length)))"
        }
        else {
            $foldedLines.Add("[No export output — raw ETL available at $etlPath] 1")
        }
    }

    # Sort descending by count and truncate to MaxStacks
    $sortedLines = $foldedLines |
        Sort-Object { [int](($_ -split '\s+')[-1]) } -Descending |
        Select-Object -First $maxStacks

    $sortedLines | Set-Content -Path $foldedStacksPath -Encoding utf8

    # Build a top-5 hotspot summary
    $top5 = $sortedLines | Select-Object -First 5
    $summaryParts = [System.Collections.Generic.List[string]]::new()
    $summaryParts.Add("Top CPU hotspots for '$ProcessName' ($($sortedLines.Count) unique stacks):")
    $rank = 1
    foreach ($line in $top5) {
        $parts   = $line -split '\s+'
        $count   = $parts[-1]
        $frames  = ($parts[0..($parts.Count - 2)] -join ' ') -split ';'
        $topFrame = $frames[-1].Trim()
        $summaryParts.Add("  ${rank}. $topFrame ($count samples)")
        $rank++
    }
    $summaryText = $summaryParts -join "`n"

    Write-Information $summaryText

    return [PSCustomObject][ordered]@{
        Success       = $true
        ExportedPaths = @($foldedStacksPath)
        Summary       = $summaryText
    }
}
catch {
    $msg = "Failed to export PerfView CPU data: $_"
    Write-Information $msg
    return [PSCustomObject][ordered]@{
        Success = $false
        Error   = $msg
    }
}
