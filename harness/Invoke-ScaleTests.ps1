<#
.SYNOPSIS
    Runs k6 scale tests and parses the results.

.DESCRIPTION
    Executes a k6 scenario against the running API, captures the JSON summary
    output, and returns a structured performance metrics object.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Iteration
    Current iteration number for logging and file naming.

.PARAMETER ScenarioPath
    Override the scenario path from config. Optional.

.PARAMETER ScenarioName
    Logical name for the scenario (e.g. 'stress-products'). When provided the
    k6 summary file is written as k6-summary-{ScenarioName}-iteration-{N}.json
    and .NET counter collection is skipped (counters are only gathered for the
    primary optimization scenario).
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [int]$Iteration = 0,
    [string]$ScenarioPath,
    [string]$ScenarioName
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

if (-not $ScenarioPath) {
    $ScenarioPath = Join-Path $repoRoot $config.ScaleTest.ScenarioPath
}

$outputDir = Join-Path $repoRoot $config.ScaleTest.OutputPath
if ($ScenarioName) {
    $jsonSummaryPath = Join-Path $outputDir "k6-summary-$ScenarioName-iteration-$Iteration.json"
} else {
    $jsonSummaryPath = Join-Path $outputDir "k6-summary-iteration-$Iteration.json"
}
$baseUrl = $config.Api.BaseUrl

# Ensure output directory exists
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' `
    -Message "Running k6 scenario: $ScenarioPath against $baseUrl" `
    -Iteration $Iteration

# ── Start .NET counter collection if enabled (primary scenario only) ─────
$counterHandle = $null
$counterMetrics = $null
$countersEnabled = $config.DotnetCounters -and $config.DotnetCounters.Enabled -and (-not $ScenarioName)

if ($countersEnabled) {
    # Discover the API process PID from the base URL port
    $apiPort = ([Uri]$baseUrl).Port
    $apiProcess = Get-NetTCPConnection -LocalPort $apiPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($apiProcess) {
        $counterHandle = & (Join-Path $PSScriptRoot 'Start-DotnetCounters.ps1') `
            -ProcessId $apiProcess.OwningProcess `
            -ConfigPath $ConfigPath `
            -Iteration $Iteration

        if (-not $counterHandle.Success) {
            & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
                -Phase 'measure' -Level 'warning' `
                -Message 'Counter collection failed to start — continuing without counters' `
                -Iteration $Iteration
            $counterHandle = $null
        }
    }
    else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "Could not find API process listening on port $apiPort — skipping counter collection" `
            -Iteration $Iteration
    }
}

# Build k6 arguments
$k6Args = @(
    'run'
    '--env', "BASE_URL=$baseUrl"
    '--summary-export', $jsonSummaryPath
)

# Add any extra args from config
if ($config.ScaleTest.ExtraArgs) {
    $k6Args += $config.ScaleTest.ExtraArgs
}

$k6Args += $ScenarioPath

# Run k6
$k6Output = & k6 @k6Args 2>&1
$k6ExitCode = $LASTEXITCODE

# Parse the JSON summary
$metrics = $null
if (Test-Path $jsonSummaryPath) {
    $summary = Get-Content $jsonSummaryPath -Raw | ConvertFrom-Json

    $metrics = [ordered]@{
        Timestamp       = (Get-Date -Format 'o')
        Iteration       = $Iteration
        HttpReqDuration = [ordered]@{
            Avg = $summary.metrics.http_req_duration.avg
            P50 = $summary.metrics.http_req_duration.med
            P90 = $summary.metrics.http_req_duration.'p(90)'
            P95 = $summary.metrics.http_req_duration.'p(95)'
            P99 = $summary.metrics.http_req_duration.'p(99)'
            Max = $summary.metrics.http_req_duration.max
        }
        HttpReqs        = [ordered]@{
            Count = $summary.metrics.http_reqs.count
            Rate  = $summary.metrics.http_reqs.rate
        }
        HttpReqFailed   = [ordered]@{
            Count = [int]($summary.metrics.http_req_failed.passes ?? 0)
            Rate  = $summary.metrics.http_req_failed.value ?? 0
        }
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "k6 completed — p95: $($metrics.HttpReqDuration.P95)ms, RPS: $([math]::Round($metrics.HttpReqs.Rate, 1))" `
        -Iteration $Iteration `
        -Data @{
            p95       = $metrics.HttpReqDuration.P95
            rps       = $metrics.HttpReqs.Rate
            errorRate = $metrics.HttpReqFailed.Rate
        }
}
else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'error' `
        -Message "k6 summary file not found at: $jsonSummaryPath" `
        -Iteration $Iteration
}

# ── Stop .NET counter collection ────────────────────────────────────────────
if ($counterHandle) {
    $counterMetrics = & (Join-Path $PSScriptRoot 'Stop-DotnetCounters.ps1') `
        -CounterHandle $counterHandle `
        -Iteration $Iteration
}

$result = [ordered]@{
    Success        = ($k6ExitCode -eq 0 -and $null -ne $metrics)
    ExitCode       = $k6ExitCode
    Metrics        = if ($metrics) { [PSCustomObject]$metrics } else { $null }
    CounterMetrics = $counterMetrics
    SummaryPath    = $jsonSummaryPath
    Output         = ($k6Output | Out-String)
}

return [PSCustomObject]$result
