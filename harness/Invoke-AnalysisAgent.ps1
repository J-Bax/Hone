<#
.SYNOPSIS
    Calls the hone-analyst CLI agent to identify the next optimization opportunity.

.DESCRIPTION
    Builds a performance context prompt with metrics, source code, and optimization
    history, then calls the hone-analyst custom agent via 'copilot --agent'.
    Parses the JSON response and returns a structured result object.

.PARAMETER CurrentMetrics
    PSCustomObject with current/baseline metrics.

.PARAMETER BaselineMetrics
    PSCustomObject with baseline metrics.

.PARAMETER ComparisonResult
    PSCustomObject from Compare-Results.ps1 (optional for first experiment).

.PARAMETER CounterMetrics
    PSCustomObject with .NET counter metrics (optional).

.PARAMETER Experiment
    Current experiment number.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER PreviousRcaExplanation
    Explanation from the previous experiment's RCA (optional).

.PARAMETER DiagnosticReports
    Hashtable of analyzer name → @{ Report; Summary } from diagnostic profiling (optional).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$BaselineMetrics,

    [PSCustomObject]$ComparisonResult,

    [PSCustomObject]$CounterMetrics,

    [int]$Experiment = 0,

    [string]$ConfigPath,

    [string]$PreviousRcaExplanation,

    [hashtable]$DiagnosticReports
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot

$config = Get-HoneConfig -ConfigPath $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message 'Preparing analysis agent prompt' `
    -Experiment $Experiment

# ── Build analysis context (source code, counters, history, profiling) ───────
$analysisContext = & (Join-Path $PSScriptRoot 'Build-AnalysisContext.ps1') `
    -Config $config -RepoRoot $repoRoot `
    -CounterMetrics $CounterMetrics -PreviousRcaExplanation $PreviousRcaExplanation `
    -DiagnosticReports $DiagnosticReports

$sourceFilePaths = $analysisContext.SourceFilePaths
$counterContext = $analysisContext.CounterContext
$trafficContext = $analysisContext.TrafficContext
$historyContext = $analysisContext.HistoryContext
$profilingContext = $analysisContext.ProfilingContext

# ── Build the prompt ────────────────────────────────────────────────────────
$improvementPct = if ($ComparisonResult -and $ComparisonResult.ImprovementPct) { $ComparisonResult.ImprovementPct } else { '0' }

$fileList = ($sourceFilePaths | ForEach-Object { "- $_" }) -join "`n"

$prompt = @"
Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment $Experiment)
- p95 Latency: $($CurrentMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($CurrentMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2))%
- Improvement vs baseline: ${improvementPct}%

## Baseline Performance
- p95 Latency: $($BaselineMetrics.HttpReqDuration.P95)ms
- Requests/sec: $([math]::Round($BaselineMetrics.HttpReqs.Rate, 1))
- Error rate: $([math]::Round($BaselineMetrics.HttpReqFailed.Rate * 100, 2))%
$counterContext
$trafficContext
$historyContext
$profilingContext

## Source Files
The following source files are available for analysis (paths relative to repo root).
Read the files that are relevant to identifying performance bottlenecks.

$fileList

Respond with JSON only. No markdown, no code blocks around the JSON.
"@

# ── Save the prompt for audit ───────────────────────────────────────────────
$iterDir = Join-Path -Path $repoRoot -ChildPath $config.Api.ResultsPath "experiment-$Experiment"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}
$promptPath = Join-Path $iterDir 'analysis-prompt.md'
$prompt | Out-File -FilePath $promptPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Calling hone-analyst agent (prompt saved to $promptPath)" `
    -Experiment $Experiment

# ── Call the hone-analyst agent ─────────────────────────────────────────────
$agentResult = & (Join-Path $PSScriptRoot 'Invoke-CopilotAgent.ps1') `
    -AgentName 'hone-analyst' `
    -Prompt $prompt `
    -ModelConfigKey 'AnalysisModel' `
    -DefaultModel 'claude-opus-4.6' `
    -SpinnerMessage 'Analyzing performance data' `
    -CompletionMessage 'Analysis complete' `
    -ResponsePath (Join-Path $iterDir 'analysis-response.json') `
    -ConfigPath $ConfigPath

$responsePath = Join-Path $iterDir 'analysis-response.json'
$parsed = $agentResult.ParsedJson

if ($parsed -and $parsed.opportunities) {
    # Normalize each opportunity to have all expected fields
    $opportunities = @($parsed.opportunities | ForEach-Object {
            [PSCustomObject]@{
                filePath = $_.filePath
                title = if ($_.title) { $_.title } else { $_.explanation }
                explanation = if ($_.explanation) { $_.explanation } elseif ($_.title) { $_.title } else { '' }
                scope = if ($_.scope) { $_.scope } else { 'narrow' }
                rootCause = if ($_.rootCause) { $_.rootCause } else { $null }
                impactEstimate = if ($_.impactEstimate) { $_.impactEstimate } else { $null }
            }
        })
    $primaryOpp = $opportunities[0]
    $result = [ordered]@{
        Success = ($agentResult.ExitCode -eq 0 -and $null -ne $primaryOpp.filePath)
        ExitCode = $agentResult.ExitCode
        FilePath = $primaryOpp.filePath
        Explanation = if ($primaryOpp.explanation) { $primaryOpp.explanation } else { $primaryOpp.title }
        Opportunities = $opportunities
        Prompt = $prompt
        Response = $agentResult.ResponseText
        PromptPath = $promptPath
        ResponsePath = $responsePath
    }

    $oppCount = @($result.Opportunities).Count
    $primaryFile = if ($result.FilePath) { $result.FilePath } else { '(unknown)' }
    $primaryTitle = if ($result.Opportunities.Count -gt 0 -and $result.Opportunities[0].title) {
        $t = $result.Opportunities[0].title
        if ($t.Length -gt 60) { $t.Substring(0, 60) + '…' } else { $t }
    } else { '' }
    Write-Status "    → $oppCount opportunities found (primary: $primaryFile)"
    if ($primaryTitle) {
        Write-Status "    → $primaryTitle"
    }
    $primaryOppForLog = if ($result.Opportunities.Count -gt 0) { $result.Opportunities[0] } else { $null }
    if ($primaryOppForLog -and $primaryOppForLog.impactEstimate -and $primaryOppForLog.impactEstimate.overallP95ImprovementPct) {
        $est = $primaryOppForLog.impactEstimate
        Write-Status "    → Impact estimate: ~$($est.overallP95ImprovementPct)% p95 improvement ($($est.confidence) confidence)"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'info' `
        -Message "Analysis agent returned $oppCount opportunities (primary: $($result.FilePath))" `
        -Experiment $Experiment
} else {
    $result = [ordered]@{
        Success = $false
        ExitCode = $agentResult.ExitCode
        FilePath = $null
        Explanation = $null
        Opportunities = @()
        Prompt = $prompt
        Response = $agentResult.ResponseText
        PromptPath = $promptPath
        ResponsePath = $responsePath
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'warning' -Message "Analysis agent failed: $($agentResult.ResponseText)" `
        -Experiment $Experiment
}

return [PSCustomObject]$result
