<#
.SYNOPSIS
    Generates an interactive HTML dashboard for Autotune performance results.

.DESCRIPTION
    Reads baseline and iteration results from the results directory, generates
    a self-contained HTML file with Chart.js visualizations, and optionally
    opens it in the default browser.

.PARAMETER ResultsPath
    Path to the results directory. Defaults to 'results' at the repo root.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER OutputPath
    Where to write the HTML file. Defaults to results/dashboard.html.

.PARAMETER Open
    Open the dashboard in the default browser after generation.

.EXAMPLE
    .\harness\Export-Dashboard.ps1 -Open

.EXAMPLE
    .\harness\Export-Dashboard.ps1 -OutputPath .\my-report.html
#>
[CmdletBinding()]
param(
    [string]$ResultsPath,
    [string]$ConfigPath,
    [string]$OutputPath,
    [switch]$Open
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$tolerances = $config.Tolerances

if (-not $ResultsPath) {
    $ResultsPath = Join-Path $repoRoot $config.ScaleTest.OutputPath
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $ResultsPath 'dashboard.html'
}

# ── Load data ───────────────────────────────────────────────────────────────

$baselinePath = Join-Path $ResultsPath 'baseline.json'
if (-not (Test-Path $baselinePath)) {
    Write-Error "No baseline found at $baselinePath. Run .\harness\Get-PerformanceBaseline.ps1 first."
    return
}

$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json

# Collect all iterations
$iterationFiles = Get-ChildItem -Path $ResultsPath -Filter 'k6-summary-iteration-*.json' |
    Sort-Object { [int]($_.BaseName -replace '.*-(\d+)$', '$1') }

$allData = @()

# Add baseline as iteration 0
$allData += @{
    iteration = 0
    label     = 'Baseline'
    p50       = [math]::Round($baseline.HttpReqDuration.P50, 2)
    p90       = [math]::Round($baseline.HttpReqDuration.P90, 2)
    p95       = [math]::Round($baseline.HttpReqDuration.P95, 2)
    avg       = [math]::Round($baseline.HttpReqDuration.Avg, 2)
    max       = [math]::Round($baseline.HttpReqDuration.Max, 2)
    rps       = [math]::Round($baseline.HttpReqs.Rate, 1)
    reqCount  = $baseline.HttpReqs.Count
    errRate   = [math]::Round(($baseline.HttpReqFailed.Rate) * 100, 2)
}

foreach ($file in $iterationFiles) {
    $iterNum = [int]($file.BaseName -replace '.*-(\d+)$', '$1')
    if ($iterNum -eq 0) { continue }

    $raw = Get-Content $file.FullName -Raw | ConvertFrom-Json

    $allData += @{
        iteration = $iterNum
        label     = "Iteration $iterNum"
        p50       = [math]::Round($raw.metrics.http_req_duration.med, 2)
        p90       = [math]::Round($raw.metrics.http_req_duration.'p(90)', 2)
        p95       = [math]::Round($raw.metrics.http_req_duration.'p(95)', 2)
        avg       = [math]::Round($raw.metrics.http_req_duration.avg, 2)
        max       = [math]::Round($raw.metrics.http_req_duration.max, 2)
        rps       = [math]::Round($raw.metrics.http_reqs.rate, 1)
        reqCount  = $raw.metrics.http_reqs.count
        errRate   = [math]::Round(($raw.metrics.http_req_failed.value ?? 0) * 100, 2)
    }
}

# ── Counter data (summary) ──────────────────────────────────────────────────

$counterFiles = Get-ChildItem -Path $ResultsPath -Filter 'dotnet-counters-iteration-*.json' -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.BaseName -replace '.*-(\d+)$', '$1') }

$counterData = @()
foreach ($cf in $counterFiles) {
    $iterNum = [int]($cf.BaseName -replace '.*-(\d+)$', '$1')
    $raw = Get-Content $cf.FullName -Raw | ConvertFrom-Json

    $counterData += @{
        iteration        = $iterNum
        cpuAvg           = $raw.Runtime.CpuUsage.Avg
        cpuMax           = $raw.Runtime.CpuUsage.Max
        heapMBAvg        = $raw.Runtime.GcHeapSizeMB.Avg
        heapMBMax        = $raw.Runtime.GcHeapSizeMB.Max
        gen0             = $raw.Runtime.Gen0Collections.Last
        gen1             = $raw.Runtime.Gen1Collections.Last
        gen2             = $raw.Runtime.Gen2Collections.Last
        workingSetMB     = $raw.Runtime.WorkingSetMB.Max
        threadPoolMax    = $raw.Runtime.ThreadPoolThreads.Max
        exceptions       = $raw.Runtime.ExceptionCount.Last
    }
}

# ── Counter time-series data (from CSV) ────────────────────────────────────

$counterCsvFiles = Get-ChildItem -Path $ResultsPath -Filter 'dotnet-counters-iteration-*.csv' -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.BaseName -replace '.*-(\d+)$', '$1') }

$counterTimeSeries = @{}

foreach ($csvFile in $counterCsvFiles) {
    $iterNum = [int]($csvFile.BaseName -replace '.*-(\d+)$', '$1')
    $csvContent = Get-Content $csvFile.FullName -Raw

    if ([string]::IsNullOrWhiteSpace($csvContent)) { continue }

    $csvRows = $csvContent | ConvertFrom-Csv -ErrorAction SilentlyContinue
    if (-not $csvRows -or $csvRows.Count -eq 0) { continue }

    # Get sorted unique timestamps to use as x-axis labels
    $timestamps = $csvRows | Select-Object -ExpandProperty Timestamp -Unique | Sort-Object

    # Normalise timestamps to elapsed seconds from first sample
    $firstTime = [datetime]$timestamps[0]
    $elapsedLabels = @()
    foreach ($ts in $timestamps) {
        $elapsed = ([datetime]$ts - $firstTime).TotalSeconds
        $elapsedLabels += [math]::Round($elapsed, 0)
    }

    # Helper: extract a counter's time-series values aligned to timestamps
    $counterMap = @{
        cpu            = 'CPU Usage (%)'
        gcHeapMB       = 'GC Heap Size (MB)'
        gen0Rate       = 'Gen 0 GC Count (Count / 1 sec)'
        gen1Rate       = 'Gen 1 GC Count (Count / 1 sec)'
        gen2Rate       = 'Gen 2 GC Count (Count / 1 sec)'
        gen0SizeB      = 'Gen 0 Size (B)'
        gen1SizeB      = 'Gen 1 Size (B)'
        gen2SizeB      = 'Gen 2 Size (B)'
        lohSizeB       = 'LOH Size (B)'
        pohSizeB       = 'POH (Pinned Object Heap) Size (B)'
        allocRateB     = 'Allocation Rate (B / 1 sec)'
        gcPausePct     = '% Time in GC since last GC (%)'
        gcFragPct      = 'GC Fragmentation (%)'
        gcCommittedMB  = 'GC Committed Bytes (MB)'
        lockContention = 'Monitor Lock Contention Count (Count / 1 sec)'
        threadCount    = 'ThreadPool Thread Count'
        threadQueue    = 'ThreadPool Queue Length'
        threadCompleted = 'ThreadPool Completed Work Item Count (Count / 1 sec)'
        exceptions     = 'Exception Count (Count / 1 sec)'
        workingSetMB   = 'Working Set (MB)'
        activeTimers   = 'Number of Active Timers'
        requestRate    = 'Request Rate (Count / 1 sec)'
        totalRequests  = 'Total Requests'
        currentRequests = 'Current Requests'
    }

    $series = @{}
    foreach ($key in $counterMap.Keys) {
        $counterName = $counterMap[$key]
        $counterRows = $csvRows | Where-Object { $_.'Counter Name' -eq $counterName }

        # Build a timestamp → value lookup
        $tsLookup = @{}
        foreach ($row in $counterRows) {
            $tsLookup[$row.Timestamp] = [double]($row.'Mean/Increment')
        }

        # Align to timestamp array (null for missing)
        $values = @()
        foreach ($ts in $timestamps) {
            if ($tsLookup.ContainsKey($ts)) {
                $values += $tsLookup[$ts]
            } else {
                $values += $null
            }
        }
        $series[$key] = $values
    }

    $counterTimeSeries["iteration$iterNum"] = @{
        iteration = $iterNum
        labels    = $elapsedLabels
        series    = $series
    }
}

# ── Per-scenario data ───────────────────────────────────────────────────────

$scenarioBaselineFiles = Get-ChildItem -Path $ResultsPath -Filter 'baseline-*.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'baseline.json' -and $_.Name -ne 'baseline-counters.json' }

$scenarioData = @{}

foreach ($sbFile in $scenarioBaselineFiles) {
    $scenarioName = $sbFile.BaseName -replace '^baseline-', ''
    $sbRaw = Get-Content $sbFile.FullName -Raw | ConvertFrom-Json

    $scenarioEntries = @()
    $scenarioEntries += @{
        iteration = 0
        label     = 'Baseline'
        p50       = [math]::Round($sbRaw.HttpReqDuration.P50, 2)
        p90       = [math]::Round($sbRaw.HttpReqDuration.P90, 2)
        p95       = [math]::Round($sbRaw.HttpReqDuration.P95, 2)
        avg       = [math]::Round($sbRaw.HttpReqDuration.Avg, 2)
        max       = [math]::Round($sbRaw.HttpReqDuration.Max, 2)
        rps       = [math]::Round($sbRaw.HttpReqs.Rate, 1)
        reqCount  = $sbRaw.HttpReqs.Count
        errRate   = [math]::Round(($sbRaw.HttpReqFailed.Rate) * 100, 2)
    }

    # Find iteration results for this scenario
    $scenarioIterFiles = Get-ChildItem -Path $ResultsPath -Filter "k6-summary-$scenarioName-iteration-*.json" -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.BaseName -replace '.*-(\d+)$', '$1') }

    foreach ($sf in $scenarioIterFiles) {
        $sIterNum = [int]($sf.BaseName -replace '.*-(\d+)$', '$1')
        if ($sIterNum -eq 0) { continue }

        $sRaw = Get-Content $sf.FullName -Raw | ConvertFrom-Json
        $scenarioEntries += @{
            iteration = $sIterNum
            label     = "Iteration $sIterNum"
            p50       = [math]::Round($sRaw.metrics.http_req_duration.med, 2)
            p90       = [math]::Round($sRaw.metrics.http_req_duration.'p(90)', 2)
            p95       = [math]::Round($sRaw.metrics.http_req_duration.'p(95)', 2)
            avg       = [math]::Round($sRaw.metrics.http_req_duration.avg, 2)
            max       = [math]::Round($sRaw.metrics.http_req_duration.max, 2)
            rps       = [math]::Round($sRaw.metrics.http_reqs.rate, 1)
            reqCount  = $sRaw.metrics.http_reqs.count
            errRate   = [math]::Round(($sRaw.metrics.http_req_failed.value ?? 0) * 100, 2)
        }
    }

    $scenarioData[$scenarioName] = $scenarioEntries
}

# ── Serialize to JSON for embedding ─────────────────────────────────────────

# Force array serialization — PowerShell unwraps single-element arrays by default
$dataJson = ConvertTo-Json -InputObject @($allData) -Depth 5 -Compress
$counterJson = ConvertTo-Json -InputObject @($counterData) -Depth 5 -Compress
$scenarioJson = ConvertTo-Json -InputObject $scenarioData -Depth 5 -Compress
$timeSeriesJson = ConvertTo-Json -InputObject $counterTimeSeries -Depth 10 -Compress
$minImprovePct = [math]::Round($tolerances.MinImprovementPct * 100, 1)
$maxRegressPct = [math]::Round($tolerances.MaxRegressionPct * 100, 1)

# Load run metadata (machine info + timestamps)
$runMetadataPath = Join-Path $ResultsPath 'run-metadata.json'
$machineJson = 'null'
if (Test-Path $runMetadataPath) {
    $runMeta = Get-Content $runMetadataPath -Raw | ConvertFrom-Json
    $machineJson = $runMeta | ConvertTo-Json -Depth 10 -Compress
}

$generatedAt = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

# ── Generate HTML ───────────────────────────────────────────────────────────

# IMPORTANT: Use single-quoted here-string (@'...'@) so that PowerShell does NOT
# interpolate JavaScript's $ signs and backtick template literals.
# Dynamic values are injected via .Replace() calls after the here-string.

$html = @'
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Autotune Performance Dashboard</title>
    <script src="https://cdn.jsdelivr.net/npm/chart.js@4"></script>
    <style>
        :root {
            --bg: #0d1117;
            --surface: #161b22;
            --border: #30363d;
            --text: #e6edf3;
            --text-muted: #8b949e;
            --green: #3fb950;
            --red: #f85149;
            --yellow: #d29922;
            --blue: #58a6ff;
            --purple: #bc8cff;
            --cyan: #39d2c0;
        }
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
            background: var(--bg);
            color: var(--text);
            padding: 24px;
            line-height: 1.5;
        }
        h1 { font-size: 1.75rem; font-weight: 600; margin-bottom: 8px; }
        .subtitle { color: var(--text-muted); font-size: 0.875rem; margin-bottom: 24px; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 16px; margin-bottom: 24px; }
        .card { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 20px; }
        .card h3 { font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.5px; color: var(--text-muted); margin-bottom: 8px; }
        .metric { font-size: 2rem; font-weight: 700; font-variant-numeric: tabular-nums; }
        .metric-sub { font-size: 0.8rem; color: var(--text-muted); margin-top: 4px; }
        .met { color: var(--green); }
        .not-met { color: var(--red); }
        .neutral { color: var(--text); }
        .chart-container { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 20px; margin-bottom: 16px; }
        .chart-container h3 { font-size: 0.9rem; margin-bottom: 12px; color: var(--text-muted); }
        .chart-row { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-bottom: 16px; }
        @media (max-width: 768px) { .chart-row { grid-template-columns: 1fr; } }
        table { width: 100%; border-collapse: collapse; font-size: 0.875rem; font-variant-numeric: tabular-nums; }
        th, td { padding: 10px 12px; text-align: right; border-bottom: 1px solid var(--border); }
        th { color: var(--text-muted); font-weight: 600; text-transform: uppercase; font-size: 0.75rem; letter-spacing: 0.5px; }
        td:first-child, th:first-child { text-align: left; }
        tr:hover td { background: rgba(88, 166, 255, 0.05); }
        .tag { display: inline-block; font-size: 0.7rem; padding: 2px 8px; border-radius: 12px; font-weight: 600; }
        .tag-pass { background: rgba(63, 185, 80, 0.15); color: var(--green); }
        .tag-fail { background: rgba(248, 81, 73, 0.15); color: var(--red); }
        .tag-tiebreaker { background: rgba(56, 139, 253, 0.15); color: var(--blue, #58a6ff); }
        .improvement { display: inline-block; font-size: 0.8rem; font-weight: 600; }
        .improvement.positive { color: var(--green); }
        .improvement.negative { color: var(--red); }
        .improvement.zero { color: var(--text-muted); }
        canvas { max-height: 300px; }
        .section-header { font-size: 1.25rem; font-weight: 600; margin: 32px 0 16px; color: var(--text); }
        .section-subtitle { color: var(--text-muted); font-size: 0.85rem; margin: -12px 0 16px; }
        .iter-selector { display: flex; gap: 8px; margin-bottom: 16px; flex-wrap: wrap; }
        .iter-btn { background: var(--surface); border: 1px solid var(--border); color: var(--text-muted); padding: 6px 14px; border-radius: 6px; cursor: pointer; font-size: 0.8rem; font-weight: 600; transition: all 0.2s; }
        .iter-btn:hover { border-color: var(--blue); color: var(--blue); }
        .iter-btn.active { background: var(--blue); border-color: var(--blue); color: #fff; }
        .chart-grid-3 { display: grid; grid-template-columns: repeat(auto-fit, minmax(380px, 1fr)); gap: 16px; margin-bottom: 16px; }
        @media (max-width: 768px) { .chart-grid-3 { grid-template-columns: 1fr; } }
    </style>
</head>
<body>
    <h1>Autotune Performance Dashboard</h1>
    <p class="subtitle">Generated __GENERATED_AT__ &nbsp;│&nbsp; Mode: Relative Improvement &nbsp;│&nbsp; Min improve: __MIN_IMPROVE__%  &nbsp;│&nbsp; Max regress: __MAX_REGRESS__%</p>

    <div id="machineInfoBar" style="display:none; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 14px 20px; margin-bottom: 24px; font-size: 0.85rem; color: var(--text-muted);">
        <span id="machineInfoText"></span>
    </div>

    <div class="grid" id="summaryCards"></div>

    <div class="chart-row">
        <div class="chart-container">
            <h3>Latency Over Iterations (ms)</h3>
            <canvas id="latencyChart"></canvas>
        </div>
        <div class="chart-container">
            <h3>Throughput &amp; Errors</h3>
            <canvas id="throughputChart"></canvas>
        </div>
    </div>

    <div class="chart-row">
        <div class="chart-container">
            <h3>Latency Distribution (Latest)</h3>
            <canvas id="distributionChart"></canvas>
        </div>
        <div class="chart-container">
            <h3>Baseline vs Latest</h3>
            <canvas id="comparisonChart"></canvas>
        </div>
    </div>

    <div class="chart-container">
        <h3>All Iterations</h3>
        <table id="detailTable">
            <thead>
                <tr>
                    <th>Iteration</th><th>p50 (ms)</th><th>p90 (ms)</th><th>p95 (ms)</th>
                    <th>Avg (ms)</th><th>Max (ms)</th><th>RPS</th><th>Requests</th>
                    <th>Errors</th><th>p95 vs Baseline</th><th>Status</th>
                </tr>
            </thead>
            <tbody></tbody>
        </table>
    </div>

    <div id="scenarioSection"></div>

    <!-- .NET Runtime Diagnostics Section -->
    <div id="runtimeSection">
        <h2 class="section-header">Runtime Diagnostics (.NET Counters)</h2>
        <p class="section-subtitle">Time-series data captured via dotnet-counters during load tests</p>
        <div class="iter-selector" id="iterSelector"></div>

        <div class="chart-grid-3">
            <div class="chart-container">
                <h3>CPU Utilization (%)</h3>
                <canvas id="cpuChart"></canvas>
            </div>
            <div class="chart-container">
                <h3>GC Collection Rate (per sec)</h3>
                <canvas id="gcRateChart"></canvas>
            </div>
            <div class="chart-container">
                <h3>Allocation Rate (MB/s)</h3>
                <canvas id="allocChart"></canvas>
            </div>
        </div>

        <div class="chart-grid-3">
            <div class="chart-container">
                <h3>% Time in GC (Pause Pressure)</h3>
                <canvas id="gcPauseChart"></canvas>
            </div>
            <div class="chart-container">
                <h3>GC Heap &amp; Gen Sizes (MB)</h3>
                <canvas id="heapChart"></canvas>
            </div>
            <div class="chart-container">
                <h3>Lock Contention Rate (per sec)</h3>
                <canvas id="lockChart"></canvas>
            </div>
        </div>

        <div class="chart-grid-3">
            <div class="chart-container">
                <h3>ThreadPool Threads &amp; Queue</h3>
                <canvas id="threadChart"></canvas>
            </div>
            <div class="chart-container">
                <h3>Working Set (MB)</h3>
                <canvas id="memChart"></canvas>
            </div>
            <div class="chart-container">
                <h3>Exceptions &amp; Request Rate</h3>
                <canvas id="reqExChart"></canvas>
            </div>
        </div>
    </div>

    <script>
    var data = __DATA_JSON__;
    var counterData = __COUNTER_JSON__;
    var timeSeriesData = __TIMESERIES_JSON__;
    var runMetadata = __RUN_METADATA_JSON__;
    var config = { minImprovePct: __MIN_IMPROVE__, maxRegressPct: __MAX_REGRESS__ };

    // ── Machine info banner ───────────────────────────────────────────
    if (runMetadata && runMetadata.Machine) {
        var m = runMetadata.Machine;
        var parts = [];
        if (m.MachineName) parts.push('<strong>' + m.MachineName + '</strong>');
        if (m.Cpu && m.Cpu.Name) parts.push('CPU: ' + m.Cpu.Name + ' (' + m.Cpu.LogicalProcessors + ' logical cores)');
        if (m.Memory && m.Memory.TotalGB) parts.push('RAM: ' + m.Memory.TotalGB + ' GB');
        if (m.OS && m.OS.Description) parts.push('OS: ' + m.OS.Description);
        if (m.Runtime) {
            var rt = [];
            if (m.Runtime.DotnetSdkVersion) rt.push('.NET SDK ' + m.Runtime.DotnetSdkVersion);
            if (m.Runtime.PowerShellVersion) rt.push('PS ' + m.Runtime.PowerShellVersion);
            if (rt.length) parts.push(rt.join(' &bull; '));
        }
        if (runMetadata.LoopCompletedAt) parts.push('Loop completed: ' + new Date(runMetadata.LoopCompletedAt).toLocaleString());
        else if (runMetadata.BaselineRun && runMetadata.BaselineRun.CompletedAt) parts.push('Baseline: ' + new Date(runMetadata.BaselineRun.CompletedAt).toLocaleString());
        var bar = document.getElementById('machineInfoBar');
        document.getElementById('machineInfoText').innerHTML = parts.join(' &nbsp;\u2502&nbsp; ');
        bar.style.display = 'block';
    }

    var latest = data[data.length - 1];
    var baseline = data[0];

    function pctChange(current, base, lowerBetter) {
        if (!base || base === 0) return { pct: 0, improved: false };
        var pct = ((current - base) / base * 100);
        var improved = lowerBetter ? pct < 0 : pct > 0;
        return { pct: pct.toFixed(1), improved: improved };
    }

    function renderCards() {
        var container = document.getElementById('summaryCards');
        var p95Change = pctChange(latest.p95, baseline.p95, true);
        var rpsChange = pctChange(latest.rps, baseline.rps, false);
        var errChange = pctChange(latest.errRate, baseline.errRate, true);

        var cards = [
            {
                title: 'p95 Latency',
                value: latest.p95 + 'ms',
                met: data.length > 1 ? p95Change.improved : null,
                sub: data.length > 1
                    ? (p95Change.pct > 0 ? '+' : '') + p95Change.pct + '% from baseline'
                    : 'Baseline measurement'
            },
            {
                title: 'Requests / sec',
                value: latest.rps.toLocaleString(),
                met: data.length > 1 ? rpsChange.improved : null,
                sub: data.length > 1
                    ? (rpsChange.pct > 0 ? '+' : '') + rpsChange.pct + '% from baseline'
                    : 'Baseline measurement'
            },
            {
                title: 'Error Rate',
                value: latest.errRate + '%',
                met: data.length > 1 ? errChange.improved : null,
                sub: data.length > 1
                    ? (errChange.pct > 0 ? '+' : '') + errChange.pct + '% from baseline'
                    : 'Baseline measurement'
            },
            {
                title: 'Iterations',
                value: data.length - 1,
                met: null,
                sub: latest.label
            }
        ];

        var html = '';
        for (var i = 0; i < cards.length; i++) {
            var c = cards[i];
            var cls = c.met === true ? 'met' : c.met === false ? 'not-met' : 'neutral';
            html += '<div class="card"><h3>' + c.title + '</h3>'
                + '<div class="metric ' + cls + '">' + c.value + '</div>'
                + '<div class="metric-sub">' + c.sub + '</div></div>';
        }
        container.innerHTML = html;
    }

    var chartDefaults = {
        responsive: true,
        maintainAspectRatio: true,
        plugins: {
            legend: { labels: { color: '#8b949e', font: { size: 11 } } }
        },
        scales: {
            x: { ticks: { color: '#8b949e' }, grid: { color: '#21262d' } },
            y: { ticks: { color: '#8b949e' }, grid: { color: '#21262d' } }
        }
    };

    function renderLatencyChart() {
        var ctx = document.getElementById('latencyChart').getContext('2d');
        var labels = data.map(function(d) { return d.label; });

        new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'p95', data: data.map(function(d) { return d.p95; }),
                        borderColor: '#f85149', backgroundColor: 'rgba(248, 81, 73, 0.1)',
                        fill: false, tension: 0.3, pointRadius: 5, borderWidth: 2
                    },
                    {
                        label: 'p90', data: data.map(function(d) { return d.p90; }),
                        borderColor: '#d29922', fill: false, tension: 0.3, pointRadius: 4, borderWidth: 2
                    },
                    {
                        label: 'p50', data: data.map(function(d) { return d.p50; }),
                        borderColor: '#3fb950', fill: false, tension: 0.3, pointRadius: 4, borderWidth: 2
                    },
                    {
                        label: 'Target p95', data: data.map(function() { return baseline.p95; }),
                        borderColor: 'rgba(248, 81, 73, 0.4)', borderDash: [6, 4],
                        pointRadius: 0, borderWidth: 1, fill: false
                    }
                ]
            },
            options: chartDefaults
        });
    }

    function renderThroughputChart() {
        var ctx = document.getElementById('throughputChart').getContext('2d');
        var labels = data.map(function(d) { return d.label; });

        new Chart(ctx, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'RPS',
                        data: data.map(function(d) { return d.rps; }),
                        backgroundColor: data.map(function(d) {
                            return d.rps >= baseline.rps ? 'rgba(63, 185, 80, 0.6)' : 'rgba(248, 81, 73, 0.6)';
                        }),
                        borderRadius: 4, yAxisID: 'y'
                    },
                    {
                        label: 'Error %',
                        data: data.map(function(d) { return d.errRate; }),
                        type: 'line', borderColor: '#f85149', backgroundColor: 'rgba(248, 81, 73, 0.1)',
                        fill: false, tension: 0.3, pointRadius: 5, borderWidth: 2, yAxisID: 'y1'
                    }
                ]
            },
            options: {
                responsive: true, maintainAspectRatio: true,
                plugins: chartDefaults.plugins,
                scales: {
                    x: chartDefaults.scales.x,
                    y: { ticks: { color: '#8b949e' }, grid: { color: '#21262d' }, position: 'left', title: { display: true, text: 'RPS', color: '#8b949e' } },
                    y1: { ticks: { color: '#8b949e' }, grid: { drawOnChartArea: false, color: '#21262d' }, position: 'right', title: { display: true, text: 'Error %', color: '#8b949e' } }
                }
            }
        });
    }

    function renderDistributionChart() {
        var ctx = document.getElementById('distributionChart').getContext('2d');
        var d = data[data.length - 1];

        new Chart(ctx, {
            type: 'bar',
            data: {
                labels: ['p50', 'p90', 'p95', 'Avg', 'Max'],
                datasets: [{
                    label: d.label,
                    data: [d.p50, d.p90, d.p95, d.avg, d.max],
                    backgroundColor: [
                        'rgba(63, 185, 80, 0.6)', 'rgba(210, 153, 34, 0.6)',
                        d.p95 <= baseline.p95 ? 'rgba(63, 185, 80, 0.6)' : 'rgba(248, 81, 73, 0.6)',
                        'rgba(88, 166, 255, 0.6)', 'rgba(188, 140, 255, 0.6)'
                    ],
                    borderRadius: 4
                }]
            },
            options: {
                responsive: true, maintainAspectRatio: true,
                indexAxis: 'y',
                plugins: chartDefaults.plugins,
                scales: {
                    x: chartDefaults.scales.y,
                    y: chartDefaults.scales.x
                }
            }
        });
    }

    function renderComparisonChart() {
        var ctx = document.getElementById('comparisonChart').getContext('2d');
        var l = data[data.length - 1];
        var b = baseline;
        var labels = ['p50', 'p90', 'p95', 'Avg', 'Max'];

        new Chart(ctx, {
            type: 'radar',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'Baseline', data: [b.p50, b.p90, b.p95, b.avg, b.max],
                        borderColor: 'rgba(248, 81, 73, 0.7)', backgroundColor: 'rgba(248, 81, 73, 0.1)',
                        pointRadius: 4, borderWidth: 2
                    },
                    {
                        label: l.label, data: [l.p50, l.p90, l.p95, l.avg, l.max],
                        borderColor: 'rgba(63, 185, 80, 0.7)', backgroundColor: 'rgba(63, 185, 80, 0.1)',
                        pointRadius: 4, borderWidth: 2
                    }
                ]
            },
            options: {
                responsive: true, maintainAspectRatio: true,
                scales: {
                    r: {
                        ticks: { color: '#8b949e', backdropColor: 'transparent' },
                        grid: { color: '#21262d' },
                        pointLabels: { color: '#8b949e' }
                    }
                },
                plugins: { legend: { labels: { color: '#8b949e', font: { size: 11 } } } }
            }
        });
    }

    function renderTable() {
        var tbody = document.querySelector('#detailTable tbody');
        var baseP95 = baseline.p95;
        var rows = '';

        // Build a lookup of counter data by iteration for efficiency tiebreaker
        var counterByIter = {};
        if (counterData && counterData.length) {
            for (var c = 0; c < counterData.length; c++) {
                counterByIter[counterData[c].iteration] = counterData[c];
            }
        }

        for (var i = 0; i < data.length; i++) {
            var d = data[i];
            var p95Better = d.p95 <= baseline.p95;
            var rpsBetter = d.rps >= baseline.rps;
            var errBetter = d.errRate <= baseline.errRate;
            var perfImproved = d.iteration === 0 || (p95Better || rpsBetter || errBetter);

            // Check efficiency tiebreaker: performance flat but CPU or working set reduced
            var isTiebreaker = false;
            if (!perfImproved && d.iteration > 0) {
                var prevIter = d.iteration - 1;
                var cur = counterByIter[d.iteration];
                var prev = counterByIter[prevIter];
                if (cur && prev) {
                    var cpuReduced = prev.cpuAvg > 0 && ((cur.cpuAvg - prev.cpuAvg) / prev.cpuAvg) <= -0.05;
                    var wsReduced = prev.workingSetMB > 0 && ((cur.workingSetMB - prev.workingSetMB) / prev.workingSetMB) <= -0.05;
                    isTiebreaker = cpuReduced || wsReduced;
                }
            }

            var improved = perfImproved || isTiebreaker;

            var delta = '\u2014';
            if (d.iteration > 0 && baseP95 > 0) {
                var pct = ((d.p95 - baseP95) / baseP95 * 100).toFixed(1);
                var cls = pct < 0 ? 'positive' : pct > 0 ? 'negative' : 'zero';
                delta = '<span class="improvement ' + cls + '">' + (pct > 0 ? '+' : '') + pct + '%</span>';
            }

            var statusCls = d.iteration === 0 ? 'tag-pass' : isTiebreaker ? 'tag-tiebreaker' : improved ? 'tag-pass' : 'tag-fail';
            var statusLabel = d.iteration === 0 ? 'BASE' : isTiebreaker ? 'TIEBREAKER' : improved ? 'IMPROVED' : 'REGRESSED';

            rows += '<tr>'
                + '<td>' + d.label + '</td>'
                + '<td>' + d.p50 + '</td>'
                + '<td>' + d.p90 + '</td>'
                + '<td style="color:' + (p95Better ? 'var(--green)' : d.iteration === 0 ? 'var(--text)' : 'var(--red)') + '">' + d.p95 + '</td>'
                + '<td>' + d.avg + '</td>'
                + '<td>' + d.max + '</td>'
                + '<td style="color:' + (rpsBetter ? 'var(--green)' : d.iteration === 0 ? 'var(--text)' : 'var(--red)') + '">' + d.rps + '</td>'
                + '<td>' + (d.reqCount || '\u2014') + '</td>'
                + '<td style="color:' + (errBetter ? 'var(--green)' : d.iteration === 0 ? 'var(--text)' : 'var(--red)') + '">' + d.errRate + '%</td>'
                + '<td>' + delta + '</td>'
                + '<td><span class="tag ' + statusCls + '">' + statusLabel + '</span></td>'
                + '</tr>';
        }
        tbody.innerHTML = rows;
    }

    var scenarioData = __SCENARIO_JSON__;

    function renderScenarios() {
        var container = document.getElementById('scenarioSection');
        var names = Object.keys(scenarioData);
        if (names.length === 0) { container.innerHTML = ''; return; }

        var html = '<h2 style="margin:24px 0 16px;font-size:1.25rem;color:var(--text)">Scenario Breakdown</h2>';

        for (var n = 0; n < names.length; n++) {
            var name = names[n];
            var sData = scenarioData[name];
            if (!sData || sData.length === 0) continue;

            var sBase = sData[0];
            var sLatest = sData[sData.length - 1];
            var sp95 = pctChange(sLatest.p95, sBase.p95, true);
            var sRps = pctChange(sLatest.rps, sBase.rps, false);

            var headerTag = '';
            if (sData.length > 1) {
                var cls = sp95.improved ? 'tag-pass' : 'tag-fail';
                var label = sp95.improved ? 'IMPROVED' : 'REGRESSED';
                headerTag = ' <span class="tag ' + cls + '">' + label + '</span>';
            }

            html += '<div class="chart-container">'
                + '<h3>' + name + headerTag + '</h3>';

            // Summary cards for this scenario
            html += '<div style="display:flex;gap:24px;margin-bottom:12px;flex-wrap:wrap">';
            html += '<div><span style="color:var(--text-muted);font-size:0.75rem">p95</span><br><span style="font-size:1.25rem;font-weight:700;color:var(--text)">' + sLatest.p95 + 'ms</span></div>';
            html += '<div><span style="color:var(--text-muted);font-size:0.75rem">RPS</span><br><span style="font-size:1.25rem;font-weight:700;color:var(--text)">' + sLatest.rps + '</span></div>';
            html += '<div><span style="color:var(--text-muted);font-size:0.75rem">Errors</span><br><span style="font-size:1.25rem;font-weight:700;color:var(--text)">' + sLatest.errRate + '%</span></div>';
            if (sData.length > 1) {
                var deltaCls = sp95.improved ? 'positive' : sp95.pct == 0 ? 'zero' : 'negative';
                html += '<div><span style="color:var(--text-muted);font-size:0.75rem">p95 vs Baseline</span><br><span class="improvement ' + deltaCls + '" style="font-size:1.25rem">' + (sp95.pct > 0 ? '+' : '') + sp95.pct + '%</span></div>';
            }
            html += '</div>';

            // Table
            html += '<table><thead><tr>'
                + '<th>Iteration</th><th>p95 (ms)</th><th>Avg (ms)</th>'
                + '<th>RPS</th><th>Errors</th><th>p95 vs Baseline</th>'
                + '</tr></thead><tbody>';

            for (var i = 0; i < sData.length; i++) {
                var d = sData[i];
                var p95Better = d.p95 <= sBase.p95;
                var delta = '\u2014';
                if (d.iteration > 0 && sBase.p95 > 0) {
                    var pct = ((d.p95 - sBase.p95) / sBase.p95 * 100).toFixed(1);
                    var cls2 = pct < 0 ? 'positive' : pct > 0 ? 'negative' : 'zero';
                    delta = '<span class="improvement ' + cls2 + '">' + (pct > 0 ? '+' : '') + pct + '%</span>';
                }
                html += '<tr>'
                    + '<td>' + d.label + '</td>'
                    + '<td style="color:' + (p95Better ? 'var(--green)' : d.iteration === 0 ? 'var(--text)' : 'var(--red)') + '">' + d.p95 + '</td>'
                    + '<td>' + d.avg + '</td>'
                    + '<td>' + d.rps + '</td>'
                    + '<td>' + d.errRate + '%</td>'
                    + '<td>' + delta + '</td>'
                    + '</tr>';
            }

            html += '</tbody></table></div>';
        }

        container.innerHTML = html;
    }

    renderCards();
    renderLatencyChart();
    renderThroughputChart();
    renderDistributionChart();
    renderComparisonChart();
    renderTable();
    renderScenarios();

    // ── Runtime Diagnostics (.NET Counters) ────────────────────────────────

    var runtimeCharts = {};
    var currentIterKey = null;

    function getIterKeys() {
        return Object.keys(timeSeriesData).sort(function(a, b) {
            return (timeSeriesData[a].iteration || 0) - (timeSeriesData[b].iteration || 0);
        });
    }

    function renderIterSelector() {
        var container = document.getElementById('iterSelector');
        var keys = getIterKeys();
        if (keys.length === 0) {
            document.getElementById('runtimeSection').style.display = 'none';
            return;
        }
        var html = '';
        for (var i = 0; i < keys.length; i++) {
            var k = keys[i];
            var iterNum = timeSeriesData[k].iteration;
            var label = iterNum === 0 ? 'Baseline' : 'Iteration ' + iterNum;
            var active = i === keys.length - 1 ? ' active' : '';
            html += '<button class="iter-btn' + active + '" data-key="' + k + '" onclick="selectIteration(this)">' + label + '</button>';
        }
        container.innerHTML = html;
        currentIterKey = keys[keys.length - 1];
    }

    function selectIteration(btn) {
        var buttons = document.querySelectorAll('.iter-btn');
        for (var i = 0; i < buttons.length; i++) buttons[i].classList.remove('active');
        btn.classList.add('active');
        currentIterKey = btn.getAttribute('data-key');
        renderRuntimeCharts();
    }

    function tsChartOpts(yTitle, stacked) {
        return {
            responsive: true, maintainAspectRatio: true,
            animation: { duration: 400 },
            plugins: {
                legend: { labels: { color: '#8b949e', font: { size: 11 }, usePointStyle: true, pointStyle: 'circle' } },
                tooltip: { mode: 'index', intersect: false }
            },
            interaction: { mode: 'index', intersect: false },
            scales: {
                x: { title: { display: true, text: 'Elapsed (s)', color: '#8b949e' }, ticks: { color: '#8b949e', maxTicksLimit: 15 }, grid: { color: '#21262d' } },
                y: { title: { display: true, text: yTitle, color: '#8b949e' }, ticks: { color: '#8b949e' }, grid: { color: '#21262d' }, stacked: stacked || false, beginAtZero: true }
            }
        };
    }

    function makeSeries(labels, values, label, color, fill) {
        return {
            label: label,
            data: values,
            borderColor: color,
            backgroundColor: color.replace('1)', '0.15)').replace('rgb(', 'rgba('),
            fill: fill || false,
            tension: 0.3,
            pointRadius: 1.5,
            borderWidth: 2
        };
    }

    function destroyChart(id) {
        if (runtimeCharts[id]) { runtimeCharts[id].destroy(); delete runtimeCharts[id]; }
    }

    function createChart(canvasId, type, chartData, options) {
        destroyChart(canvasId);
        var ctx = document.getElementById(canvasId).getContext('2d');
        runtimeCharts[canvasId] = new Chart(ctx, { type: type, data: chartData, options: options });
    }

    function toMB(bytesArr) {
        return bytesArr.map(function(v) { return v !== null ? +(v / 1048576).toFixed(2) : null; });
    }

    function toMBRate(bytesPerSecArr) {
        return bytesPerSecArr.map(function(v) { return v !== null ? +(v / 1048576).toFixed(2) : null; });
    }

    function renderRuntimeCharts() {
        if (!currentIterKey || !timeSeriesData[currentIterKey]) return;
        var ts = timeSeriesData[currentIterKey];
        var labels = ts.labels.map(function(s) { return s + 's'; });
        var s = ts.series;

        // 1. CPU Utilization
        createChart('cpuChart', 'line', {
            labels: labels,
            datasets: [
                makeSeries(labels, s.cpu || [], 'CPU %', 'rgb(88, 166, 255)', true)
            ]
        }, tsChartOpts('CPU %'));

        // 2. GC Collection Rate
        createChart('gcRateChart', 'line', {
            labels: labels,
            datasets: [
                makeSeries(labels, s.gen0Rate || [], 'Gen 0', 'rgb(63, 185, 80)'),
                makeSeries(labels, s.gen1Rate || [], 'Gen 1', 'rgb(210, 153, 34)'),
                makeSeries(labels, s.gen2Rate || [], 'Gen 2', 'rgb(248, 81, 73)')
            ]
        }, tsChartOpts('Collections / sec'));

        // 3. Allocation Rate (convert B/s → MB/s)
        createChart('allocChart', 'line', {
            labels: labels,
            datasets: [
                makeSeries(labels, toMBRate(s.allocRateB || []), 'Alloc Rate', 'rgb(188, 140, 255)', true)
            ]
        }, tsChartOpts('MB / sec'));

        // 4. % Time in GC
        createChart('gcPauseChart', 'line', {
            labels: labels,
            datasets: [
                makeSeries(labels, s.gcPausePct || [], '% Time in GC', 'rgb(248, 81, 73)', true)
            ]
        }, tsChartOpts('% Time'));

        // 5. GC Heap & Gen Sizes (convert bytes → MB)
        createChart('heapChart', 'line', {
            labels: labels,
            datasets: [
                makeSeries(labels, s.gcHeapMB || [], 'GC Heap', 'rgb(88, 166, 255)'),
                makeSeries(labels, toMB(s.gen0SizeB || []), 'Gen 0', 'rgb(63, 185, 80)'),
                makeSeries(labels, toMB(s.gen1SizeB || []), 'Gen 1', 'rgb(210, 153, 34)'),
                makeSeries(labels, toMB(s.gen2SizeB || []), 'Gen 2', 'rgb(248, 81, 73)'),
                makeSeries(labels, toMB(s.lohSizeB || []), 'LOH', 'rgb(188, 140, 255)'),
                makeSeries(labels, toMB(s.pohSizeB || []), 'POH', 'rgb(57, 210, 192)')
            ]
        }, tsChartOpts('MB'));

        // 6. Lock Contention
        createChart('lockChart', 'line', {
            labels: labels,
            datasets: [
                makeSeries(labels, s.lockContention || [], 'Contentions / sec', 'rgb(248, 81, 73)', true)
            ]
        }, tsChartOpts('Contentions / sec'));

        // 7. ThreadPool
        var threadOpts = tsChartOpts('Count');
        threadOpts.scales.y1 = {
            title: { display: true, text: 'Queue Length', color: '#8b949e' },
            ticks: { color: '#8b949e' }, grid: { drawOnChartArea: false, color: '#21262d' },
            position: 'right', beginAtZero: true
        };
        createChart('threadChart', 'line', {
            labels: labels,
            datasets: [
                Object.assign(makeSeries(labels, s.threadCount || [], 'Threads', 'rgb(88, 166, 255)'), { yAxisID: 'y' }),
                Object.assign(makeSeries(labels, s.threadQueue || [], 'Queue Length', 'rgb(210, 153, 34)'), { yAxisID: 'y1' }),
                Object.assign(makeSeries(labels, s.threadCompleted || [], 'Completed / sec', 'rgb(63, 185, 80)'), { yAxisID: 'y' })
            ]
        }, threadOpts);

        // 8. Working Set
        createChart('memChart', 'line', {
            labels: labels,
            datasets: [
                makeSeries(labels, s.workingSetMB || [], 'Working Set', 'rgb(57, 210, 192)', true)
            ]
        }, tsChartOpts('MB'));

        // 9. Exceptions & Request Rate
        var reqOpts = tsChartOpts('Rate / sec');
        reqOpts.scales.y1 = {
            title: { display: true, text: 'Exceptions / sec', color: '#8b949e' },
            ticks: { color: '#8b949e' }, grid: { drawOnChartArea: false, color: '#21262d' },
            position: 'right', beginAtZero: true
        };
        createChart('reqExChart', 'line', {
            labels: labels,
            datasets: [
                Object.assign(makeSeries(labels, s.requestRate || [], 'Requests / sec', 'rgb(63, 185, 80)'), { yAxisID: 'y' }),
                Object.assign(makeSeries(labels, s.exceptions || [], 'Exceptions / sec', 'rgb(248, 81, 73)'), { yAxisID: 'y1' })
            ]
        }, reqOpts);
    }

    renderIterSelector();
    renderRuntimeCharts();
    </script>
</body>
</html>
'@

# Inject dynamic values into placeholders
$html = $html.Replace('__DATA_JSON__', $dataJson)
$html = $html.Replace('__COUNTER_JSON__', $counterJson)
$html = $html.Replace('__TIMESERIES_JSON__', $timeSeriesJson)
$html = $html.Replace('__RUN_METADATA_JSON__', $machineJson)
$html = $html.Replace('__SCENARIO_JSON__', $scenarioJson)
$html = $html.Replace('__GENERATED_AT__', $generatedAt)
$html = $html.Replace('__MIN_IMPROVE__', [string]$minImprovePct)
$html = $html.Replace('__MAX_REGRESS__', [string]$maxRegressPct)

# ── Write file ──────────────────────────────────────────────────────────────

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$html | Out-File -FilePath $OutputPath -Encoding utf8
Write-Information "Dashboard written to: $OutputPath" -InformationAction Continue

if ($Open) {
    Start-Process $OutputPath
}
