<#
.SYNOPSIS
    Stops dotnet-counters collection and parses the captured metrics.

.DESCRIPTION
    Stops the dotnet-counters process started by Start-DotnetCounters.ps1,
    reads the CSV output file, and returns a structured object with aggregated
    runtime performance counter data including GC metrics, thread pool stats,
    exception counts, and working set.

.PARAMETER CounterHandle
    The PSCustomObject returned by Start-DotnetCounters.ps1 containing the
    Process and OutputPath.

.PARAMETER Experiment
    Current experiment number for logging.

.OUTPUTS
    PSCustomObject with structured .NET counter metrics, or $null on failure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [PSCustomObject]$CounterHandle,

    [int]$Experiment = 0
)

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' `
    -Message 'Stopping dotnet-counters collection' `
    -Experiment $Experiment

$process = $CounterHandle.Process
$csvPath = $CounterHandle.OutputPath

# Stop the dotnet-counters process gracefully
if ($process -and -not $process.HasExited) {
    try {
        # Send Ctrl+C equivalent by stopping the process
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        $process.WaitForExit(10000) | Out-Null

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' `
            -Message "dotnet-counters process stopped (PID: $($process.Id))" `
            -Experiment $Experiment
    } catch {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "Error stopping dotnet-counters: $_" `
            -Experiment $Experiment
    }
}

# Wait briefly for file to be flushed
Start-Sleep -Seconds 1

# Parse the CSV output
if (-not (Test-Path $csvPath)) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'warning' `
        -Message "Counter output file not found: $csvPath" `
        -Experiment $Experiment

    return $null
}

try {
    $csvContent = Get-Content $csvPath -Raw

    if ([string]::IsNullOrWhiteSpace($csvContent)) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message 'Counter output file is empty' `
            -Experiment $Experiment
        return $null
    }

    # dotnet-counters CSV format: Timestamp,Provider,Counter Name,Counter Type,Mean/Increment
    $rows = $csvContent | ConvertFrom-Csv -ErrorAction Stop

    if ($rows.Count -eq 0) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message 'No counter data rows found' `
            -Experiment $Experiment
        return $null
    }

    # Helper to extract a counter's stats
    function Get-CounterStat {
        param([string]$Provider, [string]$CounterName)

        $matching = $rows | Where-Object {
            $_.'Provider' -eq $Provider -and
            $_.'Counter Name' -like "*$CounterName*"
        }

        if (-not $matching -or $matching.Count -eq 0) { return $null }

        $values = $matching | ForEach-Object {
            $val = $_.'Mean/Increment'
            if ($null -ne $val -and $val -ne '') { [double]$val } else { 0 }
        }

        if ($values.Count -eq 0) { return $null }

        [ordered]@{
            Avg = [math]::Round(($values | Measure-Object -Average).Average, 2)
            Min = [math]::Round(($values | Measure-Object -Minimum).Minimum, 2)
            Max = [math]::Round(($values | Measure-Object -Maximum).Maximum, 2)
            Last = [math]::Round($values[-1], 2)
            Samples = $values.Count
        }
    }

    # ── Build structured metrics ────────────────────────────────────────────
    $metrics = [ordered]@{
        Timestamp = (Get-Date -Format 'o')
        Experiment = $Experiment
        CsvPath = $csvPath
        TotalSamples = $rows.Count

        # System.Runtime counters
        Runtime = [ordered]@{
            CpuUsage = Get-CounterStat 'System.Runtime' 'CPU Usage'
            WorkingSetMB = Get-CounterStat 'System.Runtime' 'Working Set'
            GcHeapSizeMB = Get-CounterStat 'System.Runtime' 'GC Heap Size'
            Gen0Collections = Get-CounterStat 'System.Runtime' 'Gen 0'
            Gen1Collections = Get-CounterStat 'System.Runtime' 'Gen 1'
            Gen2Collections = Get-CounterStat 'System.Runtime' 'Gen 2'
            Gen0SizeMB = Get-CounterStat 'System.Runtime' 'Gen 0 Size'
            Gen1SizeMB = Get-CounterStat 'System.Runtime' 'Gen 1 Size'
            Gen2SizeMB = Get-CounterStat 'System.Runtime' 'Gen 2 Size'
            LOHSizeMB = Get-CounterStat 'System.Runtime' 'LOH Size'
            POHSizeMB = Get-CounterStat 'System.Runtime' 'POH'
            GcPauseRatio = Get-CounterStat 'System.Runtime' 'time in GC'
            AllocRateMB = Get-CounterStat 'System.Runtime' 'Allocation Rate'
            ExceptionCount = Get-CounterStat 'System.Runtime' 'Exception'
            ThreadPoolThreads = Get-CounterStat 'System.Runtime' 'ThreadPool Thread'
            ThreadPoolQueue = Get-CounterStat 'System.Runtime' 'ThreadPool Queue'
            ThreadPoolCompleted = Get-CounterStat 'System.Runtime' 'ThreadPool Completed'
            MonitorContentions = Get-CounterStat 'System.Runtime' 'Monitor Lock'
            ActiveTimers = Get-CounterStat 'System.Runtime' 'Active Timer'
            Assemblies = Get-CounterStat 'System.Runtime' 'Assemblies'
        }

        # ASP.NET Core Hosting counters
        AspNetCore = [ordered]@{
            RequestRate = Get-CounterStat 'Microsoft.AspNetCore.Hosting' 'Request Rate'
            TotalRequests = Get-CounterStat 'Microsoft.AspNetCore.Hosting' 'Total Requests'
            CurrentRequests = Get-CounterStat 'Microsoft.AspNetCore.Hosting' 'Current Requests'
            FailedRequests = Get-CounterStat 'Microsoft.AspNetCore.Hosting' 'Failed Requests'
        }

        # HTTP Connection counters
        HttpConnections = [ordered]@{
            CurrentConnections = Get-CounterStat 'Microsoft.AspNetCore.Http.Connections' 'Current Connections'
            TotalConnections = Get-CounterStat 'Microsoft.AspNetCore.Http.Connections' 'Total Connections'
        }

        # Outbound HTTP counters
        HttpClient = [ordered]@{
            CurrentRequests = Get-CounterStat 'System.Net.Http' 'Current Requests'
            RequestsStarted = Get-CounterStat 'System.Net.Http' 'Requests Started'
            RequestsFailed = Get-CounterStat 'System.Net.Http' 'Requests Failed'
        }
    }

    # Log summary of key metrics
    $cpuAvg = if ($metrics.Runtime.CpuUsage) { "$($metrics.Runtime.CpuUsage.Avg)%" } else { 'N/A' }
    $heapMax = if ($metrics.Runtime.GcHeapSizeMB) { "$($metrics.Runtime.GcHeapSizeMB.Max)MB" } else { 'N/A' }
    $gen2 = if ($metrics.Runtime.Gen2Collections) { $metrics.Runtime.Gen2Collections.Last } else { 'N/A' }
    $gcPause = if ($metrics.Runtime.GcPauseRatio) { "$($metrics.Runtime.GcPauseRatio.Max)%" } else { 'N/A' }
    $threads = if ($metrics.Runtime.ThreadPoolThreads) { $metrics.Runtime.ThreadPoolThreads.Max } else { 'N/A' }
    $exceptions = if ($metrics.Runtime.ExceptionCount) { $metrics.Runtime.ExceptionCount.Last } else { 'N/A' }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "Counter summary — CPU avg: $cpuAvg, GC heap max: $heapMax, Gen2: $gen2, GC pause: $gcPause, Threads max: $threads, Exceptions: $exceptions" `
        -Experiment $Experiment `
        -Data @{
        cpuAvg = $metrics.Runtime.CpuUsage.Avg
        gcHeapMax = $metrics.Runtime.GcHeapSizeMB.Max
        gen2Collections = $metrics.Runtime.Gen2Collections.Last
        gcPauseMax = $metrics.Runtime.GcPauseRatio.Max
        threadPoolMax = $metrics.Runtime.ThreadPoolThreads.Max
    }

    # Save the parsed metrics as JSON alongside the CSV
    $jsonPath = [System.IO.Path]::ChangeExtension($csvPath, '.json')
    [PSCustomObject]$metrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $jsonPath -Encoding utf8

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "Counter metrics saved to: $jsonPath" `
        -Experiment $Experiment

    return [PSCustomObject]$metrics
} catch {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'warning' `
        -Message "Failed to parse counter data: $_" `
        -Experiment $Experiment

    return $null
}
