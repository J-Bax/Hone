<#
.SYNOPSIS
    Displays performance results in the terminal.

.DESCRIPTION
    Reads baseline and experiment results from the results directory and presents
    them as a formatted comparison table. Shows key metrics and improvement deltas
    between experiments.

.PARAMETER ResultsPath
    Path to the results directory. Defaults to 'sample-api/results' at the repo root.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.EXAMPLE
    .\harness\Show-Results.ps1

.EXAMPLE
    .\harness\Show-Results.ps1 -ResultsPath .\sample-api\results
#>
[CmdletBinding()]
param(
    [string]$ResultsPath,
    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

# ── Console output helper (avoids PSAvoidUsingWriteHost) ────────────────────

function Write-Console {
    param(
        [string]$Text = '',
        [System.ConsoleColor]$Color,
        [switch]$NoNewline
    )
    if ($PSBoundParameters.ContainsKey('Color')) {
        if ($NoNewline) {
            $Host.UI.Write($Color, $Host.UI.RawUI.BackgroundColor, $Text)
        } else {
            $Host.UI.WriteLine($Color, $Host.UI.RawUI.BackgroundColor, $Text)
        }
    } elseif ($NoNewline) {
        $Host.UI.Write($Text)
    } else {
        $Host.UI.WriteLine($Text)
    }
}

$config = Get-HoneConfig -ConfigPath $ConfigPath

if (-not $ResultsPath) {
    $ResultsPath = Join-Path $repoRoot $config.Api.ResultsPath
}

# ── Load all result files ───────────────────────────────────────────────────

$baselinePath = Join-Path $ResultsPath 'baseline.json'
if (-not (Test-Path $baselinePath)) {
    Write-Console "`n  No baseline found. Run .\harness\Get-PerformanceBaseline.ps1 first.`n" -Color Yellow
    return
}

$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json

# Find experiment summaries from experiment-* subdirectories
$experimentDirs = Get-ChildItem -Path $ResultsPath -Directory -Filter 'experiment-*' -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Name -replace 'experiment-', '') }

# Parse each experiment's metrics
$experiments = @()
foreach ($dir in $experimentDirs) {
    $expNum = [int]($dir.Name -replace 'experiment-', '')
    $summaryFile = Join-Path $dir.FullName 'k6-summary.json'
    if (-not (Test-Path $summaryFile)) { continue }

    # Parse from the raw k6 summary
    $raw = Get-Content $summaryFile -Raw | ConvertFrom-Json
    $metrics = [PSCustomObject]@{
        Experiment = $expNum
        HttpReqDuration = [PSCustomObject]@{
            Avg = $raw.metrics.http_req_duration.avg
            P50 = $raw.metrics.http_req_duration.med
            P90 = $raw.metrics.http_req_duration.'p(90)'
            P95 = $raw.metrics.http_req_duration.'p(95)'
            P99 = $raw.metrics.http_req_duration.'p(99)'
            Max = $raw.metrics.http_req_duration.max
        }
        HttpReqs = [PSCustomObject]@{
            Count = $raw.metrics.http_reqs.count
            Rate = $raw.metrics.http_reqs.rate
        }
        HttpReqFailed = [PSCustomObject]@{
            Rate = $raw.metrics.http_req_failed.value ?? 0
        }
    }

    $experiments += $metrics
}

# Load counter data from dotnet-counters.json files
$counterDataMap = @{}
foreach ($dir in $experimentDirs) {
    $expNum = [int]($dir.Name -replace 'experiment-', '')
    $counterFile = Join-Path $dir.FullName 'dotnet-counters.json'
    if (Test-Path $counterFile) {
        $counterDataMap[$expNum] = Get-Content $counterFile -Raw | ConvertFrom-Json
    }
}

# Also try loading baseline counters
$baselineCounterPath = Join-Path $ResultsPath 'baseline-counters.json'
$baselineCounters = $null
if (Test-Path $baselineCounterPath) {
    $baselineCounters = Get-Content $baselineCounterPath -Raw | ConvertFrom-Json
}

# ── Thresholds ──────────────────────────────────────────────────────────────

$tolerances = $config.Tolerances

# ── Helper: status indicator ────────────────────────────────────────────────

function Get-StatusIcon {
    param([bool]$Met)
    if ($Met) { return '✓' } else { return '✗' }
}

function Get-StatusColor {
    param([bool]$Met)
    if ($Met) { return 'Green' } else { return 'Red' }
}

function Format-Pct {
    param([double]$Value)
    return "$([math]::Round($Value * 100, 2))%"
}

function Format-Delta {
    param([double]$Current, [double]$Baseline, [bool]$LowerIsBetter = $true)
    if ($Baseline -eq 0) { return 'N/A' }
    $pct = [math]::Round((($Current - $Baseline) / $Baseline) * 100, 1)
    $sign = if ($pct -gt 0) { '+' } else { '' }
    $improved = if ($LowerIsBetter) { $pct -lt 0 } else { $pct -gt 0 }
    $color = if ($improved) { 'Green' } elseif ($pct -eq 0) { 'Gray' } else { 'Red' }
    return @{ Text = "${sign}${pct}%"; Color = $color }
}

# ── Display ─────────────────────────────────────────────────────────────────

Write-Console ""
Write-Console "  ══════════════════════════════════════════════════════════════════════" -Color DarkCyan
Write-Console "                      HONE PERFORMANCE RESULTS" -Color DarkCyan
Write-Console "  ══════════════════════════════════════════════════════════════════════" -Color DarkCyan
Write-Console ""

# Machine info and run metadata
$runMetadataPath = Join-Path $ResultsPath 'run-metadata.json'
if (Test-Path $runMetadataPath) {
    $runMeta = Get-Content $runMetadataPath -Raw | ConvertFrom-Json
    if ($runMeta.Machine) {
        $m = $runMeta.Machine
        Write-Console "  CPU:     " -NoNewline -Color DarkGray
        Write-Console "$($m.Cpu.Name) ($($m.Cpu.LogicalProcessors) cores)" -NoNewline -Color White
        Write-Console " · " -NoNewline -Color DarkGray
        Write-Console "RAM: $($m.Memory.TotalGB)GB" -Color White
        Write-Console "  OS:      " -NoNewline -Color DarkGray
        Write-Console "$($m.OS.Description)" -Color White
        Write-Console "  Runtime: " -NoNewline -Color DarkGray
        Write-Console "PS $($m.Runtime.PowerShellVersion) · .NET SDK $($m.Runtime.DotnetSdkVersion) · $($m.Runtime.ClrVersion)" -Color White
    }
    if ($runMeta.BaselineRun) {
        Write-Console "  Baseline run: " -NoNewline -Color DarkGray
        Write-Console "$($runMeta.BaselineRun.CompletedAt)" -Color White
    }
    if ($runMeta.LoopCompletedAt) {
        Write-Console "  Loop completed: " -NoNewline -Color DarkGray
        Write-Console "$($runMeta.LoopCompletedAt)" -Color White
    }
    Write-Console ""
}

# Mode info row
Write-Console "  Mode: " -NoNewline -Color DarkGray
Write-Console "Relative improvement" -NoNewline -Color White
Write-Console " · " -NoNewline -Color DarkGray
Write-Console "Min improvement: $([math]::Round($tolerances.MinImprovementPct * 100, 1))%" -NoNewline -Color White
Write-Console " · " -NoNewline -Color DarkGray
Write-Console "Max regression: $([math]::Round($tolerances.MaxRegressionPct * 100, 1))%" -Color White
Write-Console ""

# ── Table header ────────────────────────────────────────────────────────────

$colWidths = @{ Label = 14; P95 = 12; Avg = 12; RPS = 12; Err = 12; Delta = 12; CPU = 12; Mem = 12 }
$totalWidth = 14 + 12 + 12 + 12 + 12 + 12 + 12 + 12 + 2
$separator = "  " + ("─" * $totalWidth)

Write-Console "  " -NoNewline
Write-Console ("{0,-$($colWidths.Label)}" -f "Experiment") -NoNewline -Color Cyan
Write-Console ("{0,$($colWidths.P95)}" -f "p95 (ms)") -NoNewline -Color Cyan
Write-Console ("{0,$($colWidths.Avg)}" -f "Avg (ms)") -NoNewline -Color Cyan
Write-Console ("{0,$($colWidths.RPS)}" -f "RPS") -NoNewline -Color Cyan
Write-Console ("{0,$($colWidths.Err)}" -f "Error %") -NoNewline -Color Cyan
Write-Console ("{0,$($colWidths.Delta)}" -f "p95 Δ") -NoNewline -Color Cyan
Write-Console ("{0,$($colWidths.CPU)}" -f "CPU avg%") -NoNewline -Color Cyan
Write-Console ("{0,$($colWidths.Mem)}" -f "Mem MB") -Color Cyan
Write-Console $separator -Color DarkGray

# ── Baseline row ────────────────────────────────────────────────────────────

$bP95 = [math]::Round($baseline.HttpReqDuration.P95, 2)
$bAvg = [math]::Round($baseline.HttpReqDuration.Avg, 2)
$bRPS = [math]::Round($baseline.HttpReqs.Rate, 1)
$bErr = Format-Pct ($baseline.HttpReqFailed.Rate)

Write-Console "  " -NoNewline
Write-Console ("{0,-$($colWidths.Label)}" -f "Baseline") -NoNewline -Color Yellow
Write-Console ("{0,$($colWidths.P95)}" -f $bP95) -NoNewline -Color White
Write-Console ("{0,$($colWidths.Avg)}" -f $bAvg) -NoNewline -Color White
Write-Console ("{0,$($colWidths.RPS)}" -f $bRPS) -NoNewline -Color White
Write-Console ("{0,$($colWidths.Err)}" -f $bErr) -NoNewline -Color White
Write-Console ("{0,$($colWidths.Delta)}" -f "—") -NoNewline -Color DarkGray
$bCpuAvg = if ($baselineCounters -and $baselineCounters.Runtime.CpuUsage) { [math]::Round($baselineCounters.Runtime.CpuUsage.Avg, 1) } else { '—' }
$bMemMB = if ($baselineCounters -and $baselineCounters.Runtime.WorkingSetMB) { [math]::Round($baselineCounters.Runtime.WorkingSetMB.Max, 1) } else { '—' }
Write-Console ("{0,$($colWidths.CPU)}" -f $bCpuAvg) -NoNewline -Color White
Write-Console ("{0,$($colWidths.Mem)}" -f $bMemMB) -Color White
Write-Console $separator -Color DarkGray

# ── Experiment rows ──────────────────────────────────────────────────────────

foreach ($exp in $experiments) {
    $expNum = $exp.Experiment
    if ($expNum -eq 0) { continue }  # Skip experiment 0 (same as baseline)

    $iP95 = [math]::Round($exp.HttpReqDuration.P95, 2)
    $iAvg = [math]::Round($exp.HttpReqDuration.Avg, 2)
    $iRPS = [math]::Round($exp.HttpReqs.Rate, 1)
    $iErr = Format-Pct ($exp.HttpReqFailed.Rate)

    $deltaP95 = Format-Delta -Current $iP95 -Baseline $bP95 -LowerIsBetter $true

    # Color by direction of change vs baseline (green = improved, red = worse)
    $p95Color = if ($iP95 -lt $bP95) { 'Green' } elseif ($iP95 -gt $bP95) { 'Red' } else { 'White' }
    $rpsColor = if ($iRPS -gt $bRPS) { 'Green' } elseif ($iRPS -lt $bRPS) { 'Red' } else { 'White' }
    $errColor = if ($exp.HttpReqFailed.Rate -lt $baseline.HttpReqFailed.Rate) { 'Green' } elseif ($exp.HttpReqFailed.Rate -gt $baseline.HttpReqFailed.Rate) { 'Red' } else { 'White' }

    $expCounters = if ($counterDataMap.ContainsKey($expNum)) { $counterDataMap[$expNum] } else { $null }
    $iCpuAvg = if ($expCounters -and $expCounters.Runtime.CpuUsage) { [math]::Round($expCounters.Runtime.CpuUsage.Avg, 1) } else { '—' }
    $iMemMB = if ($expCounters -and $expCounters.Runtime.WorkingSetMB) { [math]::Round($expCounters.Runtime.WorkingSetMB.Max, 1) } else { '—' }

    # Color coding for CPU (green = lower than baseline)
    $cpuColor = 'White'
    if ($iCpuAvg -ne '—' -and $bCpuAvg -ne '—') {
        $cpuColor = if ($iCpuAvg -lt $bCpuAvg) { 'Green' } elseif ($iCpuAvg -gt $bCpuAvg) { 'Red' } else { 'White' }
    }
    $memColor = 'White'
    if ($iMemMB -ne '—' -and $bMemMB -ne '—') {
        $memColor = if ($iMemMB -lt $bMemMB) { 'Green' } elseif ($iMemMB -gt $bMemMB) { 'Red' } else { 'White' }
    }

    Write-Console "  " -NoNewline
    Write-Console ("{0,-$($colWidths.Label)}" -f "Experiment $expNum") -NoNewline -Color White
    Write-Console ("{0,$($colWidths.P95)}" -f $iP95) -NoNewline -Color $p95Color
    Write-Console ("{0,$($colWidths.Avg)}" -f $iAvg) -NoNewline -Color White
    Write-Console ("{0,$($colWidths.RPS)}" -f $iRPS) -NoNewline -Color $rpsColor
    Write-Console ("{0,$($colWidths.Err)}" -f $iErr) -NoNewline -Color $errColor
    Write-Console ("{0,$($colWidths.Delta)}" -f $deltaP95.Text) -NoNewline -Color $deltaP95.Color
    Write-Console ("{0,$($colWidths.CPU)}" -f $iCpuAvg) -NoNewline -Color $cpuColor
    Write-Console ("{0,$($colWidths.Mem)}" -f $iMemMB) -Color $memColor
}

if ($experiments.Count -eq 0 -or ($experiments.Count -eq 1 -and $experiments[0].Experiment -eq 0)) {
    Write-Console "  " -NoNewline
    Write-Console "  No optimization experiments yet. Run .\harness\Invoke-HoneLoop.ps1" -Color DarkGray
}

# ── Per-scenario results ────────────────────────────────────────────────────

# Discover scenario baselines (baseline-{name}.json files)
$scenarioBaselineFiles = Get-ChildItem -Path $ResultsPath -Filter 'baseline-*.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'baseline.json' -and $_.Name -ne 'baseline-counters.json' }

if ($scenarioBaselineFiles.Count -gt 0) {
    Write-Console ''
    Write-Console '  ── Scenario Breakdown ──────────────────────────────────────────────────' -Color DarkCyan
    Write-Console ''

    # Header
    $sColWidths = @{ Name = 22; P95 = 12; RPS = 12; Err = 12; Delta = 12 }

    foreach ($sbFile in $scenarioBaselineFiles) {
        $scenarioName = $sbFile.BaseName -replace '^baseline-', ''
        $scenarioBaseline = Get-Content $sbFile.FullName -Raw | ConvertFrom-Json

        Write-Console "  " -NoNewline
        Write-Console $scenarioName -Color Yellow

        # Print sub-header
        Write-Console "  " -NoNewline
        Write-Console ("{0,-$($sColWidths.Name)}" -f '') -NoNewline
        Write-Console ("{0,$($sColWidths.P95)}" -f 'p95 (ms)') -NoNewline -Color DarkGray
        Write-Console ("{0,$($sColWidths.RPS)}" -f 'RPS') -NoNewline -Color DarkGray
        Write-Console ("{0,$($sColWidths.Err)}" -f 'Error %') -NoNewline -Color DarkGray
        Write-Console ("{0,$($sColWidths.Delta)}" -f 'p95 Δ') -Color DarkGray

        # Baseline row
        $sbP95 = [math]::Round($scenarioBaseline.HttpReqDuration.P95, 2)
        $sbRPS = [math]::Round($scenarioBaseline.HttpReqs.Rate, 1)
        $sbErr = Format-Pct ($scenarioBaseline.HttpReqFailed.Rate)

        Write-Console "  " -NoNewline
        Write-Console ("{0,-$($sColWidths.Name)}" -f '  Baseline') -NoNewline -Color DarkGray
        Write-Console ("{0,$($sColWidths.P95)}" -f $sbP95) -NoNewline -Color White
        Write-Console ("{0,$($sColWidths.RPS)}" -f $sbRPS) -NoNewline -Color White
        Write-Console ("{0,$($sColWidths.Err)}" -f $sbErr) -NoNewline -Color White
        Write-Console ("{0,$($sColWidths.Delta)}" -f '—') -Color DarkGray

        # Find experiment results for this scenario from experiment subdirectories
        $scenarioIterCount = 0
        foreach ($dir in $experimentDirs) {
            $sExpNum = [int]($dir.Name -replace 'experiment-', '')
            if ($sExpNum -eq 0) { continue }

            $sf = Join-Path $dir.FullName "k6-summary-$scenarioName.json"
            if (-not (Test-Path $sf)) { continue }

            $scenarioIterCount++
            $sRaw = Get-Content $sf -Raw | ConvertFrom-Json
            $sP95 = [math]::Round($sRaw.metrics.http_req_duration.'p(95)', 2)
            $sRPS = [math]::Round($sRaw.metrics.http_reqs.rate, 1)
            $sErr = Format-Pct ($sRaw.metrics.http_req_failed.value ?? 0)
            $sDelta = Format-Delta -Current $sP95 -Baseline $sbP95 -LowerIsBetter $true

            $sP95Color = if ($sP95 -lt $sbP95) { 'Green' } elseif ($sP95 -gt $sbP95) { 'Red' } else { 'White' }

            Write-Console "  " -NoNewline
            Write-Console ("{0,-$($sColWidths.Name)}" -f "  Experiment $sExpNum") -NoNewline -Color White
            Write-Console ("{0,$($sColWidths.P95)}" -f $sP95) -NoNewline -Color $sP95Color
            Write-Console ("{0,$($sColWidths.RPS)}" -f $sRPS) -NoNewline -Color White
            Write-Console ("{0,$($sColWidths.Err)}" -f $sErr) -NoNewline -Color White
            Write-Console ("{0,$($sColWidths.Delta)}" -f $sDelta.Text) -Color $sDelta.Color
        }

        if ($scenarioIterCount -eq 0) {
            Write-Console "  " -NoNewline
            Write-Console '    No experiment data yet' -Color DarkGray
        }

        Write-Console ''
    }
}

# ── Latency distribution (baseline) ────────────────────────────────────────

Write-Console ""
Write-Console "  Latency Distribution (Baseline):" -Color Cyan
Write-Console ""

$barMax = 50
$maxVal = @($baseline.HttpReqDuration.P50, $baseline.HttpReqDuration.P90,
    $baseline.HttpReqDuration.P95, $baseline.HttpReqDuration.Max) |
    Measure-Object -Maximum | Select-Object -ExpandProperty Maximum

$percentiles = @(
    @{ Label = 'p50'; Value = $baseline.HttpReqDuration.P50 }
    @{ Label = 'p90'; Value = $baseline.HttpReqDuration.P90 }
    @{ Label = 'p95'; Value = $baseline.HttpReqDuration.P95 }
    @{ Label = 'Max'; Value = $baseline.HttpReqDuration.Max }
)

foreach ($p in $percentiles) {
    $val = $p.Value
    if ($null -eq $val -or $val -eq 0) { continue }
    $barLen = [math]::Max(1, [math]::Round(($val / $maxVal) * $barMax))
    $bar = '█' * $barLen
    $color = 'Cyan'

    Write-Console ("  {0,4}  " -f $p.Label) -NoNewline -Color DarkGray
    Write-Console $bar -NoNewline -Color $color
    Write-Console " $([math]::Round($val, 1))ms" -Color White
}

# ── Latest experiment latency (if available) ─────────────────────────────────

$latestIter = $experiments | Where-Object { $_.Experiment -gt 0 } | Select-Object -Last 1
if ($latestIter) {
    Write-Console ""
    Write-Console "  Latency Distribution (Experiment $($latestIter.Experiment)):" -Color Cyan
    Write-Console ""

    $latestMaxVal = @($latestIter.HttpReqDuration.P50, $latestIter.HttpReqDuration.P90,
        $latestIter.HttpReqDuration.P95, $latestIter.HttpReqDuration.Max) |
        Measure-Object -Maximum | Select-Object -ExpandProperty Maximum

    # Use same scale as baseline for visual comparison
    $scaleMax = [math]::Max($maxVal, $latestMaxVal)

    $latestPercentiles = @(
        @{ Label = 'p50'; Value = $latestIter.HttpReqDuration.P50; BaseValue = $baseline.HttpReqDuration.P50 }
        @{ Label = 'p90'; Value = $latestIter.HttpReqDuration.P90; BaseValue = $baseline.HttpReqDuration.P90 }
        @{ Label = 'p95'; Value = $latestIter.HttpReqDuration.P95; BaseValue = $baseline.HttpReqDuration.P95 }
        @{ Label = 'Max'; Value = $latestIter.HttpReqDuration.Max; BaseValue = $baseline.HttpReqDuration.Max }
    )

    foreach ($p in $latestPercentiles) {
        $val = $p.Value
        $bVal = $p.BaseValue
        if ($null -eq $val -or $val -eq 0) { continue }
        $barLen = [math]::Max(1, [math]::Round(($val / $scaleMax) * $barMax))
        $bar = '█' * $barLen
        $color = if ($val -lt $bVal) { 'Green' } elseif ($val -gt $bVal) { 'Red' } else { 'Cyan' }

        Write-Console ("  {0,4}  " -f $p.Label) -NoNewline -Color DarkGray
        Write-Console $bar -NoNewline -Color $color
        Write-Console " $([math]::Round($val, 1))ms" -Color White
    }
}

# ── Overall status ──────────────────────────────────────────────────────────

Write-Console ""

$latestResult = if ($latestIter) { $latestIter } else { $baseline }
$p95Delta = Format-Delta -Current $latestResult.HttpReqDuration.P95 -Baseline $baseline.HttpReqDuration.P95 -LowerIsBetter $true
$rpsDelta = Format-Delta -Current $latestResult.HttpReqs.Rate -Baseline $baseline.HttpReqs.Rate -LowerIsBetter $false

Write-Console "  Status: " -NoNewline -Color DarkGray
if ($latestIter) {
    $improved = ($latestResult.HttpReqDuration.P95 -lt $baseline.HttpReqDuration.P95) -or
    ($latestResult.HttpReqs.Rate -gt $baseline.HttpReqs.Rate) -or
    ($latestResult.HttpReqFailed.Rate -lt $baseline.HttpReqFailed.Rate)
    if ($improved) {
        Write-Console "IMPROVED vs BASELINE" -NoNewline -Color Green
        Write-Console " | p95 $($p95Delta.Text)" -NoNewline -Color $p95Delta.Color
        Write-Console " | RPS $($rpsDelta.Text)" -Color $rpsDelta.Color
    } else {
        Write-Console "NO NET IMPROVEMENT vs BASELINE" -Color Yellow
    }
} else {
    Write-Console "Baseline only — run Invoke-HoneLoop.ps1 to optimize" -Color DarkGray
}

# Efficiency summary
if ($latestIter -and $counterDataMap.ContainsKey($latestIter.Experiment) -and $baselineCounters) {
    $latestCounters = $counterDataMap[$latestIter.Experiment]
    if ($latestCounters.Runtime.CpuUsage -and $baselineCounters.Runtime.CpuUsage) {
        $cpuDelta = Format-Delta -Current $latestCounters.Runtime.CpuUsage.Avg -Baseline $baselineCounters.Runtime.CpuUsage.Avg -LowerIsBetter $true
        $memDelta = Format-Delta -Current $latestCounters.Runtime.WorkingSetMB.Max -Baseline $baselineCounters.Runtime.WorkingSetMB.Max -LowerIsBetter $true
        Write-Console "  Efficiency: " -NoNewline -Color DarkGray
        Write-Console "CPU $($cpuDelta.Text)" -NoNewline -Color $cpuDelta.Color
        Write-Console " | " -NoNewline -Color DarkGray
        Write-Console "Memory $($memDelta.Text)" -Color $memDelta.Color
    }
}

Write-Console ""
