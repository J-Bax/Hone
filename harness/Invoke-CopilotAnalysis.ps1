<#
.SYNOPSIS
    Invokes GitHub Copilot CLI to analyze performance and suggest optimizations.

.DESCRIPTION
    Constructs a detailed prompt with current performance metrics, baseline
    comparison, and source code context, then sends it to the standalone
    'copilot' CLI (with a configurable model) to get optimization recommendations.

.PARAMETER CurrentMetrics
    PSCustomObject with current iteration metrics.

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics.

.PARAMETER ComparisonResult
    PSCustomObject from Compare-Results.ps1.

.PARAMETER Iteration
    Current iteration number.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$BaselineMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$ComparisonResult,

    [PSCustomObject]$CounterMetrics,

    [int]$Iteration = 0,

    [string]$ConfigPath,

    [string]$PreviousRcaExplanation
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message 'Preparing Copilot analysis prompt' `
    -Iteration $Iteration

# ── Read source code context ────────────────────────────────────────────────
$apiProjectPath = Join-Path $repoRoot $config.Api.ProjectPath
$sourceGlob = if ($config.Api.SourceFileGlob) { $config.Api.SourceFileGlob } else { '*.*' }
$sourcePaths = if ($config.Api.SourceCodePaths) { $config.Api.SourceCodePaths } else { @('.') }

$sourceContext = foreach ($subPath in $sourcePaths) {
    $searchDir = Join-Path $apiProjectPath $subPath
    if (Test-Path $searchDir) {
        Get-ChildItem -Path $searchDir -Filter $sourceGlob -Recurse | ForEach-Object {
            "// === $($_.Name) ===`n$(Get-Content $_.FullName -Raw)"
        }
    }
}

# ── Build counter metrics context ───────────────────────────────────────────
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

# ── Build optimization history context ───────────────────────────────────────
$historyContext = ''
$metadataDir = Join-Path $repoRoot $config.Api.MetadataPath
$logPath   = Join-Path $metadataDir 'optimization-log.md'
$queuePath = Join-Path $metadataDir 'optimization-queue.md'

if (Test-Path $logPath) {
    $logContent = Get-Content $logPath -Raw
    $historyContext += "`n## Previously Tried Optimizations`n$logContent`n"
}
if (Test-Path $queuePath) {
    $queueContent = Get-Content $queuePath -Raw
    $historyContext += "`n## Known Optimization Queue`nItems marked [ARCHITECTURE] require manual [APPROVED] tag before implementation.`n$queueContent`n"
}
if ($PreviousRcaExplanation) {
    $historyContext += "`n## Last Iteration's Fix`n$PreviousRcaExplanation`n"
}

# ── Build the prompt ────────────────────────────────────────────────────────
$prompt = @"
I am optimizing a Web API for performance. Here are the current results:

## Current Performance (Iteration $Iteration)
- p95 Latency: $($CurrentMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($CurrentMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2))%
- Improvement vs baseline: $($ComparisonResult.ImprovementPct)%

## Baseline Performance
- p95 Latency: $($BaselineMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($BaselineMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($BaselineMetrics.HttpReqFailed.Rate * 100, 2))%
$counterContext
$historyContext

## Source Code
$($sourceContext -join "`n`n")

## RULES — READ CAREFULLY
1. Suggest EXACTLY ONE specific, scoped code change — a single file, a single concern.
   Do NOT bundle multiple optimizations into one response.
2. Do NOT repeat any optimization already listed in "Previously Tried Optimizations" above.
3. Pick the SINGLE highest-impact change that has NOT been tried yet.
4. The change must improve at least one metric (lower p95 latency, higher RPS, or
   lower error rate) WITHOUT regressing any other metric.
5. I need a measurable improvement from the current values — there are no fixed
   targets, just make it better.
6. You MUST preserve all existing functionality. Do not remove, rename, or alter
   the behaviour of any public API endpoint, response schema, or data contract.
   Performance optimizations must be invisible to API consumers.
7. Classify your suggestion as NARROW or ARCHITECTURE:
   - NARROW = single-file change that only modifies implementation internals
     (query tuning, caching, algorithm swap, in-memory optimization, index addition).
   - ARCHITECTURE = schema migration, new dependency/package, multi-file
     restructuring, endpoint contract change, or infrastructure change.
   If in doubt, classify as ARCHITECTURE.
   Items marked [ARCHITECTURE] in the optimization queue below require manual
   approval before implementation. Only suggest implementing an [ARCHITECTURE]
   item if it is also marked [APPROVED].

Respond with EXACTLY these numbered sections:
1. The file path to modify (relative to sample-api/, e.g. a relative path under the project directory)
2. A brief explanation of the single change and which metric it should improve
3. The complete new file content (the FULL file, not a diff)
4. Two to three additional optimization opportunities NOT YET TRIED, ranked by expected impact (one line each, prefixed with "- [NARROW] " or "- [ARCHITECTURE] ")
5. Scope: NARROW or ARCHITECTURE (one word only)
"@

# Save the prompt for audit
$iterDir = Join-Path $repoRoot $config.Api.ResultsPath "iteration-$Iteration"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}
$promptPath = Join-Path $iterDir 'copilot-prompt.md'
$prompt | Out-File -FilePath $promptPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Sending prompt to Copilot CLI (prompt saved to $promptPath)" `
    -Iteration $Iteration

# ── Call GitHub Copilot CLI ─────────────────────────────────────────────────
try {
    # Use the standalone copilot CLI with:
    #   -p       non-interactive prompt mode
    #   -s       silent/script-friendly output (response only, no stats)
    #   --model  pick the model configured in config.psd1
    #   --deny-tool  prevent the agent from running shell commands or writing files
    #              (we want pure text analysis, not code execution)
    #   --no-custom-instructions  ignore AGENTS.md etc. so only our prompt is used
    #   --no-ask-user  work autonomously without asking questions
    #   --no-auto-update  don't download updates mid-run

    $copilotModel = if ($config.Copilot -and $config.Copilot.Model) {
        $config.Copilot.Model
    } else {
        'claude-opus-4.6'
    }

    $copilotOutput = copilot --model $copilotModel -p $prompt -s `
        --deny-tool 'shell' --deny-tool 'write' --deny-tool 'read' `
        --no-auto-update --no-ask-user --no-custom-instructions 2>&1
    $copilotExitCode = $LASTEXITCODE

    # Save the response
    $responsePath = Join-Path $iterDir 'copilot-response.md'
    ($copilotOutput | Out-String) | Out-File -FilePath $responsePath -Encoding utf8

    # ── Parse additional opportunities from section 4 ───────────────────
    $responseText = ($copilotOutput | Out-String)
    $additionalOpportunities = @()
    try {
        # Look for section 4 content: lines starting with "- " after the section 4 heading.
        # The heading may use markdown bold (**4. ...**), heading (## 4. ...), or plain (4. ...).
        if ($responseText -match '(?ms)(?:^|\n)\s*(?:\*{0,2}|#{1,3}\s*)4[\.)\s]') {
            $section4Start = $responseText.IndexOf($Matches[0])
            $section4Text = $responseText.Substring($section4Start)
            $additionalOpportunities = @(
                [regex]::Matches($section4Text, '(?m)^\s*[-\*]\s+(.+)$') |
                    ForEach-Object { $_.Groups[1].Value.Trim() } |
                    Where-Object { $_.Length -gt 0 }
            )
        }
    }
    catch {
        Write-Verbose "Could not parse additional opportunities: $_"
    }

    $result = [ordered]@{
        Success                 = ($copilotExitCode -eq 0)
        ExitCode                = $copilotExitCode
        Prompt                  = $prompt
        Response                = $responseText
        PromptPath              = $promptPath
        ResponsePath            = $responsePath
        AdditionalOpportunities = $additionalOpportunities
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'info' -Message 'Copilot response received' `
        -Iteration $Iteration
}
catch {
    $result = [ordered]@{
        Success      = $false
        ExitCode     = -1
        Prompt       = $prompt
        Response     = "Error: $_"
        PromptPath   = $promptPath
        ResponsePath = $null
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'warning' -Message "Copilot CLI failed: $_" `
        -Iteration $Iteration
}

return [PSCustomObject]$result
