<#
.SYNOPSIS
    Builds the file paths, counter metrics, and history context for analysis prompts.

.DESCRIPTION
    Collects source file paths, formats .NET counter metrics, and gathers optimization history
    into a structured object suitable for prompt construction. Source file contents are NOT
    included — the analysis agent reads them directly via its own tools.

.PARAMETER Config
    Imported harness configuration hashtable.

.PARAMETER RepoRoot
    Absolute path to the repository root.

.PARAMETER CounterMetrics
    PSCustomObject with .NET counter metrics (optional).

.PARAMETER PreviousRcaExplanation
    Explanation from the previous experiment's RCA (optional).

.PARAMETER DiagnosticReports
    Hashtable of analyzer name → @{ Report; Summary } from diagnostic profiling (optional).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable]$Config,

    [Parameter(Mandatory)]
    [string]$RepoRoot,

    [PSCustomObject]$CounterMetrics,

    [string]$PreviousRcaExplanation,

    [hashtable]$DiagnosticReports
)

# ── Source file paths (agent explores files itself) ──────────────────────────
$apiProjectPath = Join-Path $RepoRoot $Config.Api.ProjectPath
$sourceGlob = if ($Config.Api.SourceFileGlob) { $Config.Api.SourceFileGlob } else { '*.*' }
$sourcePaths = if ($Config.Api.SourceCodePaths) { $Config.Api.SourceCodePaths } else { @('.') }

$sourceFilePaths = foreach ($subPath in $sourcePaths) {
    $searchDir = Join-Path $apiProjectPath $subPath
    if (Test-Path $searchDir) {
        Get-ChildItem -Path $searchDir -Filter $sourceGlob -Recurse | ForEach-Object {
            $_.FullName.Substring($RepoRoot.Length + 1).Replace('\', '/')
        }
    }
}

# ── Counter metrics context ──────────────────────────────────────────────────
$counterContext = ''
if ($CounterMetrics) {
    $cpuAvg = if ($CounterMetrics.Runtime.CpuUsage) { "$($CounterMetrics.Runtime.CpuUsage.Avg)%" } else { 'N/A' }
    $heapMax = if ($CounterMetrics.Runtime.GcHeapSizeMB) { "$($CounterMetrics.Runtime.GcHeapSizeMB.Max)MB" } else { 'N/A' }
    $gen2 = if ($CounterMetrics.Runtime.Gen2Collections) { $CounterMetrics.Runtime.Gen2Collections.Last } else { 'N/A' }
    $threads = if ($CounterMetrics.Runtime.ThreadPoolThreads) { $CounterMetrics.Runtime.ThreadPoolThreads.Max } else { 'N/A' }
    $counterContext = @"

## Runtime Counters
- CPU avg: $cpuAvg
- GC heap max: $heapMax
- Gen2 collections: $gen2
- Thread pool max threads: $threads
"@
}

# ── Traffic distribution context ─────────────────────────────────────────────
$trafficContext = ''
$scenarioPath = if ($Config.ScaleTest.ScenarioPath) {
    Join-Path $RepoRoot $Config.ScaleTest.ScenarioPath
} else { $null }

if ($scenarioPath -and (Test-Path $scenarioPath)) {
    $scenarioContent = Get-Content $scenarioPath -Raw
    $trafficContext = @"

## Traffic Distribution (k6 Scenario)
The following k6 load test scenario defines the request patterns and relative weights of each
endpoint. Use this to estimate what percentage of total traffic each endpoint/code path receives.

``````javascript
$scenarioContent
``````
"@
}

# ── Optimization history context ─────────────────────────────────────────────
$historyContext = ''
$metadataDir = Join-Path $RepoRoot $Config.Api.MetadataPath
$logPath = Join-Path $metadataDir 'experiment-log.md'
$queueJsonPath = Join-Path $metadataDir 'experiment-queue.json'
$queueMdPath = Join-Path $metadataDir 'experiment-queue.md'

if (Test-Path $logPath) {
    $logContent = Get-Content $logPath -Raw
    $historyContext += "`n## Previously Tried Optimizations`n$logContent`n"
}

# Prefer structured JSON queue if available; fall back to markdown
if (Test-Path $queueJsonPath) {
    $queueJson = Get-Content $queueJsonPath -Raw | ConvertFrom-Json
    $pendingItems = @($queueJson.items | Where-Object { $_.status -eq 'pending' })
    $doneItems = @($queueJson.items | Where-Object { $_.status -eq 'done' })
    if ($pendingItems.Count -gt 0 -or $doneItems.Count -gt 0) {
        $queueLines = @()
        foreach ($item in $doneItems) {
            $queueLines += "- [TRIED] ``$($item.filePath)`` — $($item.explanation) *(experiment $($item.triedByExperiment) — $($item.outcome))*"
        }
        foreach ($item in $pendingItems) {
            $scopeTag = if ($item.scope -eq 'architecture') { '[ARCHITECTURE] ' } else { '' }
            $queueLines += "- [PENDING] ${scopeTag}``$($item.filePath)`` — $($item.explanation)"
        }
        $historyContext += "`n## Known Optimization Queue`n$($queueLines -join "`n")`n"
    }
} elseif (Test-Path $queueMdPath) {
    $queueContent = Get-Content $queueMdPath -Raw
    $historyContext += "`n## Known Optimization Queue`n$queueContent`n"
}

if ($PreviousRcaExplanation) {
    $historyContext += "`n## Last Experiment's Fix`n$PreviousRcaExplanation`n"
}

# ── Structured experiment history from run-metadata.json ─────────────────────
$runMetadataPath = Join-Path $RepoRoot $Config.Api.ResultsPath 'run-metadata.json'
if (Test-Path $runMetadataPath) {
    try {
        $runMeta = Get-Content $runMetadataPath -Raw | ConvertFrom-Json
        $experiments = @($runMeta.Experiments | Where-Object { $null -ne $_ })
        if ($experiments.Count -gt 0) {
            $historyLines = @(
                '| Exp | File | Outcome | p95 (ms) | RPS | Branch |'
                '|-----|------|---------|----------|-----|--------|'
            )
            foreach ($exp in $experiments) {
                $p95Display = if ($null -ne $exp.P95) { [math]::Round($exp.P95, 1) } else { 'N/A' }
                $rpsDisplay = if ($null -ne $exp.RPS) { [math]::Round($exp.RPS, 1) } else { 'N/A' }
                $branchDisplay = if ($exp.BranchName) { $exp.BranchName } else { '—' }
                $historyLines += "| $($exp.Experiment) | — | $($exp.Outcome) | $p95Display | $rpsDisplay | $branchDisplay |"
            }
            $historyContext += "`n## Experiment History (with metrics)`nDo NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.`n$($historyLines -join "`n")`n"
        }
    } catch {
        # Non-fatal: run-metadata is supplementary context
        Write-Verbose "Could not read run-metadata.json for history context: $_"
    }
}

# ── Diagnostic profiling context ─────────────────────────────────────────────
$profilingContext = ''
if ($DiagnosticReports -and $DiagnosticReports.Count -gt 0) {
    $profilingContext = "`n## Diagnostic Profiling Reports"
    $profilingContext += "`n(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)`n"

    foreach ($analyzerName in ($DiagnosticReports.Keys | Sort-Object)) {
        $entry = $DiagnosticReports[$analyzerName]
        $reportJson = if ($entry.Report) {
            $entry.Report | ConvertTo-Json -Depth 5 -Compress
        } else {
            $entry.Summary
        }
        $profilingContext += "`n### $analyzerName`n``````json`n$reportJson`n```````n"
    }
}

# ── Return structured result ─────────────────────────────────────────────────
[PSCustomObject]@{
    SourceFilePaths = @($sourceFilePaths)
    CounterContext = $counterContext
    TrafficContext = $trafficContext
    HistoryContext = $historyContext
    ProfilingContext = $profilingContext
}
