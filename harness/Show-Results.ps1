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

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

if (-not $ResultsPath) {
    $ResultsPath = Join-Path $repoRoot $config.Api.ResultsPath
}

# ── Load all result files ───────────────────────────────────────────────────

$baselinePath = Join-Path $ResultsPath 'baseline.json'
if (-not (Test-Path $baselinePath)) {
    Write-Host "`n  No baseline found. Run .\harness\Get-PerformanceBaseline.ps1 first.`n" -ForegroundColor Yellow
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
        Experiment       = $expNum
        HttpReqDuration = [PSCustomObject]@{
            Avg = $raw.metrics.http_req_duration.avg
            P50 = $raw.metrics.http_req_duration.med
            P90 = $raw.metrics.http_req_duration.'p(90)'
            P95 = $raw.metrics.http_req_duration.'p(95)'
            P99 = $raw.metrics.http_req_duration.'p(99)'
            Max = $raw.metrics.http_req_duration.max
        }
        HttpReqs        = [PSCustomObject]@{
            Count = $raw.metrics.http_reqs.count
            Rate  = $raw.metrics.http_reqs.rate
        }
        HttpReqFailed   = [PSCustomObject]@{
            Rate = $raw.metrics.http_req_failed.value ?? 0
        }
    }

    $experiments += $metrics
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

Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════════════════════════════╗" -ForegroundColor DarkCyan
Write-Host "  ║                    HONE PERFORMANCE RESULTS                     ║" -ForegroundColor DarkCyan
Write-Host "  ╚══════════════════════════════════════════════════════════════════════╝" -ForegroundColor DarkCyan
Write-Host ""

# Machine info and run metadata
$runMetadataPath = Join-Path $ResultsPath 'run-metadata.json'
if (Test-Path $runMetadataPath) {
    $runMeta = Get-Content $runMetadataPath -Raw | ConvertFrom-Json
    if ($runMeta.Machine) {
        $m = $runMeta.Machine
        Write-Host "  CPU:     " -NoNewline -ForegroundColor DarkGray
        Write-Host "$($m.Cpu.Name) ($($m.Cpu.LogicalProcessors) cores)" -NoNewline -ForegroundColor White
        Write-Host " │ " -NoNewline -ForegroundColor DarkGray
        Write-Host "RAM: $($m.Memory.TotalGB)GB" -ForegroundColor White
        Write-Host "  OS:      " -NoNewline -ForegroundColor DarkGray
        Write-Host "$($m.OS.Description)" -ForegroundColor White
        Write-Host "  Runtime: " -NoNewline -ForegroundColor DarkGray
        Write-Host "PS $($m.Runtime.PowerShellVersion) │ .NET SDK $($m.Runtime.DotnetSdkVersion) │ $($m.Runtime.ClrVersion)" -ForegroundColor White
    }
    if ($runMeta.BaselineRun) {
        Write-Host "  Baseline run: " -NoNewline -ForegroundColor DarkGray
        Write-Host "$($runMeta.BaselineRun.CompletedAt)" -ForegroundColor White
    }
    if ($runMeta.LoopCompletedAt) {
        Write-Host "  Loop completed: " -NoNewline -ForegroundColor DarkGray
        Write-Host "$($runMeta.LoopCompletedAt)" -ForegroundColor White
    }
    Write-Host ""
}

# Mode info row
Write-Host "  Mode: " -NoNewline -ForegroundColor DarkGray
Write-Host "Relative improvement" -NoNewline -ForegroundColor White
Write-Host " │ " -NoNewline -ForegroundColor DarkGray
Write-Host "Min improvement: $([math]::Round($tolerances.MinImprovementPct * 100, 1))%" -NoNewline -ForegroundColor White
Write-Host " │ " -NoNewline -ForegroundColor DarkGray
Write-Host "Max regression: $([math]::Round($tolerances.MaxRegressionPct * 100, 1))%" -ForegroundColor White
Write-Host ""

# ── Table header ────────────────────────────────────────────────────────────

$colWidths = @{ Label = 14; P95 = 12; Avg = 12; RPS = 12; Err = 12; Delta = 12 }
$totalWidth = 14 + 12 + 12 + 12 + 12 + 12 + 2
$separator = "  " + ("─" * $totalWidth)

Write-Host "  " -NoNewline
Write-Host ("{0,-$($colWidths.Label)}" -f "Experiment") -NoNewline -ForegroundColor Cyan
Write-Host ("{0,$($colWidths.P95)}" -f "p95 (ms)") -NoNewline -ForegroundColor Cyan
Write-Host ("{0,$($colWidths.Avg)}" -f "Avg (ms)") -NoNewline -ForegroundColor Cyan
Write-Host ("{0,$($colWidths.RPS)}" -f "RPS") -NoNewline -ForegroundColor Cyan
Write-Host ("{0,$($colWidths.Err)}" -f "Error %") -NoNewline -ForegroundColor Cyan
Write-Host ("{0,$($colWidths.Delta)}" -f "p95 Δ") -ForegroundColor Cyan
Write-Host $separator -ForegroundColor DarkGray

# ── Baseline row ────────────────────────────────────────────────────────────

$bP95 = [math]::Round($baseline.HttpReqDuration.P95, 2)
$bAvg = [math]::Round($baseline.HttpReqDuration.Avg, 2)
$bRPS = [math]::Round($baseline.HttpReqs.Rate, 1)
$bErr = Format-Pct ($baseline.HttpReqFailed.Rate)

Write-Host "  " -NoNewline
Write-Host ("{0,-$($colWidths.Label)}" -f "Baseline") -NoNewline -ForegroundColor Yellow
Write-Host ("{0,$($colWidths.P95)}" -f $bP95) -NoNewline -ForegroundColor White
Write-Host ("{0,$($colWidths.Avg)}" -f $bAvg) -NoNewline -ForegroundColor White
Write-Host ("{0,$($colWidths.RPS)}" -f $bRPS) -NoNewline -ForegroundColor White
Write-Host ("{0,$($colWidths.Err)}" -f $bErr) -NoNewline -ForegroundColor White
Write-Host ("{0,$($colWidths.Delta)}" -f "—") -ForegroundColor DarkGray
Write-Host $separator -ForegroundColor DarkGray

# ── Experiment rows ──────────────────────────────────────────────────────────

foreach ($exp in $experiments) {
    $expNum = $exp.Experiment
    if ($expNum -eq 0) { continue }  # Skip experiment 0 (same as baseline)

    $iP95 = [math]::Round($exp.HttpReqDuration.P95, 2)
    $iAvg = [math]::Round($exp.HttpReqDuration.Avg, 2)
    $iRPS = [math]::Round($exp.HttpReqs.Rate, 1)
    $iErr = Format-Pct ($exp.HttpReqFailed.Rate)

    $deltaP95 = Format-Delta -Current $iP95 -Baseline $bP95 -LowerIsBetter $true
    $deltaRPS = Format-Delta -Current $iRPS -Baseline $bRPS -LowerIsBetter $false

    # Color by direction of change vs baseline (green = improved, red = worse)
    $p95Color = if ($iP95 -lt $bP95) { 'Green' } elseif ($iP95 -gt $bP95) { 'Red' } else { 'White' }
    $rpsColor = if ($iRPS -gt $bRPS) { 'Green' } elseif ($iRPS -lt $bRPS) { 'Red' } else { 'White' }
    $errColor = if ($exp.HttpReqFailed.Rate -lt $baseline.HttpReqFailed.Rate) { 'Green' } elseif ($exp.HttpReqFailed.Rate -gt $baseline.HttpReqFailed.Rate) { 'Red' } else { 'White' }

    Write-Host "  " -NoNewline
    Write-Host ("{0,-$($colWidths.Label)}" -f "Experiment $expNum") -NoNewline -ForegroundColor White
    Write-Host ("{0,$($colWidths.P95)}" -f $iP95) -NoNewline -ForegroundColor $p95Color
    Write-Host ("{0,$($colWidths.Avg)}" -f $iAvg) -NoNewline -ForegroundColor White
    Write-Host ("{0,$($colWidths.RPS)}" -f $iRPS) -NoNewline -ForegroundColor $rpsColor
    Write-Host ("{0,$($colWidths.Err)}" -f $iErr) -NoNewline -ForegroundColor $errColor
    Write-Host ("{0,$($colWidths.Delta)}" -f $deltaP95.Text) -ForegroundColor $deltaP95.Color
}

if ($experiments.Count -eq 0 -or ($experiments.Count -eq 1 -and $experiments[0].Experiment -eq 0)) {
    Write-Host "  " -NoNewline
    Write-Host "  No optimization experiments yet. Run .\harness\Invoke-HoneLoop.ps1" -ForegroundColor DarkGray
}

# ── Per-scenario results ────────────────────────────────────────────────────

# Discover scenario baselines (baseline-{name}.json files)
$scenarioBaselineFiles = Get-ChildItem -Path $ResultsPath -Filter 'baseline-*.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'baseline.json' -and $_.Name -ne 'baseline-counters.json' }

if ($scenarioBaselineFiles.Count -gt 0) {
    Write-Host ''
    Write-Host '  ── Scenario Breakdown ──────────────────────────────────────────────────' -ForegroundColor DarkCyan
    Write-Host ''

    # Header
    $sColWidths = @{ Name = 22; P95 = 12; RPS = 12; Err = 12; Delta = 12 }
    $sTotalWidth = 22 + 12 + 12 + 12 + 12 + 2

    foreach ($sbFile in $scenarioBaselineFiles) {
        $scenarioName = $sbFile.BaseName -replace '^baseline-', ''
        $scenarioBaseline = Get-Content $sbFile.FullName -Raw | ConvertFrom-Json

        Write-Host "  " -NoNewline
        Write-Host $scenarioName -ForegroundColor Yellow

        # Print sub-header
        Write-Host "  " -NoNewline
        Write-Host ("{0,-$($sColWidths.Name)}" -f '') -NoNewline
        Write-Host ("{0,$($sColWidths.P95)}" -f 'p95 (ms)') -NoNewline -ForegroundColor DarkGray
        Write-Host ("{0,$($sColWidths.RPS)}" -f 'RPS') -NoNewline -ForegroundColor DarkGray
        Write-Host ("{0,$($sColWidths.Err)}" -f 'Error %') -NoNewline -ForegroundColor DarkGray
        Write-Host ("{0,$($sColWidths.Delta)}" -f 'p95 Δ') -ForegroundColor DarkGray

        # Baseline row
        $sbP95 = [math]::Round($scenarioBaseline.HttpReqDuration.P95, 2)
        $sbRPS = [math]::Round($scenarioBaseline.HttpReqs.Rate, 1)
        $sbErr = Format-Pct ($scenarioBaseline.HttpReqFailed.Rate)

        Write-Host "  " -NoNewline
        Write-Host ("{0,-$($sColWidths.Name)}" -f '  Baseline') -NoNewline -ForegroundColor DarkGray
        Write-Host ("{0,$($sColWidths.P95)}" -f $sbP95) -NoNewline -ForegroundColor White
        Write-Host ("{0,$($sColWidths.RPS)}" -f $sbRPS) -NoNewline -ForegroundColor White
        Write-Host ("{0,$($sColWidths.Err)}" -f $sbErr) -NoNewline -ForegroundColor White
        Write-Host ("{0,$($sColWidths.Delta)}" -f '—') -ForegroundColor DarkGray

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

            Write-Host "  " -NoNewline
            Write-Host ("{0,-$($sColWidths.Name)}" -f "  Experiment $sExpNum") -NoNewline -ForegroundColor White
            Write-Host ("{0,$($sColWidths.P95)}" -f $sP95) -NoNewline -ForegroundColor $sP95Color
            Write-Host ("{0,$($sColWidths.RPS)}" -f $sRPS) -NoNewline -ForegroundColor White
            Write-Host ("{0,$($sColWidths.Err)}" -f $sErr) -NoNewline -ForegroundColor White
            Write-Host ("{0,$($sColWidths.Delta)}" -f $sDelta.Text) -ForegroundColor $sDelta.Color
        }

        if ($scenarioIterCount -eq 0) {
            Write-Host "  " -NoNewline
            Write-Host '    No experiment data yet' -ForegroundColor DarkGray
        }

        Write-Host ''
    }
}

# ── Latency distribution (baseline) ────────────────────────────────────────

Write-Host ""
Write-Host "  Latency Distribution (Baseline):" -ForegroundColor Cyan
Write-Host ""

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

    Write-Host ("  {0,4}  " -f $p.Label) -NoNewline -ForegroundColor DarkGray
    Write-Host $bar -NoNewline -ForegroundColor $color
    Write-Host " $([math]::Round($val, 1))ms" -ForegroundColor White
}

# ── Latest experiment latency (if available) ─────────────────────────────────

$latestIter = $experiments | Where-Object { $_.Experiment -gt 0 } | Select-Object -Last 1
if ($latestIter) {
    Write-Host ""
    Write-Host "  Latency Distribution (Experiment $($latestIter.Experiment)):" -ForegroundColor Cyan
    Write-Host ""

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

        Write-Host ("  {0,4}  " -f $p.Label) -NoNewline -ForegroundColor DarkGray
        Write-Host $bar -NoNewline -ForegroundColor $color
        Write-Host " $([math]::Round($val, 1))ms" -ForegroundColor White
    }
}

# ── Overall status ──────────────────────────────────────────────────────────

Write-Host ""

$latestResult = if ($latestIter) { $latestIter } else { $baseline }
$p95Delta = Format-Delta -Current $latestResult.HttpReqDuration.P95 -Baseline $baseline.HttpReqDuration.P95 -LowerIsBetter $true
$rpsDelta = Format-Delta -Current $latestResult.HttpReqs.Rate -Baseline $baseline.HttpReqs.Rate -LowerIsBetter $false

Write-Host "  Status: " -NoNewline -ForegroundColor DarkGray
if ($latestIter) {
    $improved = ($latestResult.HttpReqDuration.P95 -lt $baseline.HttpReqDuration.P95) -or
                ($latestResult.HttpReqs.Rate -gt $baseline.HttpReqs.Rate) -or
                ($latestResult.HttpReqFailed.Rate -lt $baseline.HttpReqFailed.Rate)
    if ($improved) {
        Write-Host "IMPROVED vs BASELINE" -NoNewline -ForegroundColor Green
        Write-Host " | p95 $($p95Delta.Text)" -NoNewline -ForegroundColor $p95Delta.Color
        Write-Host " | RPS $($rpsDelta.Text)" -ForegroundColor $rpsDelta.Color
    }
    else {
        Write-Host "NO NET IMPROVEMENT vs BASELINE" -ForegroundColor Yellow
    }
}
else {
    Write-Host "Baseline only — run Invoke-HoneLoop.ps1 to optimize" -ForegroundColor DarkGray
}

Write-Host ""
