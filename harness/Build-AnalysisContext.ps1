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
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable]$Config,

    [Parameter(Mandatory)]
    [string]$RepoRoot,

    [PSCustomObject]$CounterMetrics,

    [string]$PreviousRcaExplanation
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

# ── Optimization history context ─────────────────────────────────────────────
$historyContext = ''
$metadataDir = Join-Path $RepoRoot $Config.Api.MetadataPath
$logPath   = Join-Path $metadataDir 'optimization-log.md'
$queuePath = Join-Path $metadataDir 'optimization-queue.md'

if (Test-Path $logPath) {
    $logContent = Get-Content $logPath -Raw
    $historyContext += "`n## Previously Tried Optimizations`n$logContent`n"
}
if (Test-Path $queuePath) {
    $queueContent = Get-Content $queuePath -Raw
    $historyContext += "`n## Known Optimization Queue`n$queueContent`n"
}
if ($PreviousRcaExplanation) {
    $historyContext += "`n## Last Experiment's Fix`n$PreviousRcaExplanation`n"
}

# ── Return structured result ─────────────────────────────────────────────────
[PSCustomObject]@{
    SourceFilePaths = @($sourceFilePaths)
    CounterContext  = $counterContext
    HistoryContext  = $historyContext
}
