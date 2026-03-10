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

function Write-Status ([string]$Message) {
    if ($Message -match '^\s*$' -or $Message -match '^[━═─╔╚╗╝║╠╣╦╩]') {
        Write-Information $Message -InformationAction Continue
    } else {
        Write-Information "[$(Get-Date -Format 'HH:mm:ss')] $Message" -InformationAction Continue
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message 'Preparing analysis agent prompt' `
    -Experiment $Experiment

# ── Build analysis context (source code, counters, history, profiling) ───────
$analysisContext = & (Join-Path $PSScriptRoot 'Build-AnalysisContext.ps1') `
    -Config $config -RepoRoot $repoRoot `
    -CounterMetrics $CounterMetrics -PreviousRcaExplanation $PreviousRcaExplanation `
    -DiagnosticReports $DiagnosticReports

$sourceFilePaths  = $analysisContext.SourceFilePaths
$counterContext   = $analysisContext.CounterContext
$historyContext   = $analysisContext.HistoryContext
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
$historyContext
$profilingContext

## Source Files
The following source files are available for analysis (paths relative to repo root).
Read the files that are relevant to identifying performance bottlenecks.

$fileList

Respond with JSON only. No markdown, no code blocks around the JSON.
"@

# ── Save the prompt for audit ───────────────────────────────────────────────
$iterDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}
$promptPath = Join-Path $iterDir 'analysis-prompt.md'
$prompt | Out-File -FilePath $promptPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Calling hone-analyst agent (prompt saved to $promptPath)" `
    -Experiment $Experiment

# ── Call the hone-analyst agent ─────────────────────────────────────────────
. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

try {
    $copilotModel = if ($config.Copilot -and $config.Copilot.AnalysisModel) {
        $config.Copilot.AnalysisModel
    } elseif ($config.Copilot -and $config.Copilot.Model) {
        $config.Copilot.Model
    } else {
        'claude-opus-4.6'
    }

    # Ensure UTF-8 decoding of copilot CLI output (prevents mojibake like ΓÇö for em-dash)
    $prevEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

    $spinner = Start-Spinner -Message "Analyzing performance data ($copilotModel)"

    $copilotOutput = copilot --agent hone-analyst --model $copilotModel -p $prompt -s `
        --no-auto-update --no-ask-user 2>&1
    $copilotExitCode = $LASTEXITCODE

    [Console]::OutputEncoding = $prevEncoding

    $responseText = ($copilotOutput | Out-String).Trim()

    # Show brief result from agent
    $oppPreview = if ($responseText.Length -gt 80) { $responseText.Substring(0, 80) + '…' } else { $responseText }
    Stop-Spinner -Spinner $spinner -CompletionMessage "Analysis complete"

    # Save the response
    $responsePath = Join-Path $iterDir 'analysis-response.json'
    $responseText | Out-File -FilePath $responsePath -Encoding utf8

    # Parse JSON — the agent may output exploration text before the JSON response.
    # Strip code fences, then extract the first complete JSON object ({...}).
    $jsonText = $responseText -replace '(?s)^```(?:json)?\s*', '' -replace '(?s)\s*```$', ''
    if ($jsonText -match '(?s)(\{.+\})') {
        $jsonText = $Matches[1]
    }
    $parsed = $jsonText | ConvertFrom-Json

    # Support both new format ({opportunities: [...]}) and legacy ({filePath, explanation, additionalOpportunities}).
    if ($parsed.opportunities) {
        # New multi-opportunity format — normalize each item to have all expected fields
        $opportunities = @($parsed.opportunities | ForEach-Object {
            [PSCustomObject]@{
                filePath    = $_.filePath
                title       = if ($_.title) { $_.title } else { $_.explanation }
                explanation = if ($_.explanation) { $_.explanation } elseif ($_.title) { $_.title } else { '' }
                scope       = if ($_.scope) { $_.scope } else { 'narrow' }
                rootCause   = if ($_.rootCause) { $_.rootCause } else { $null }
            }
        })
        $primaryOpp = $opportunities[0]
        $result = [ordered]@{
            Success       = ($copilotExitCode -eq 0 -and $null -ne $primaryOpp.filePath)
            ExitCode      = $copilotExitCode
            FilePath      = $primaryOpp.filePath
            Explanation   = if ($primaryOpp.explanation) { $primaryOpp.explanation } else { $primaryOpp.title }
            Opportunities = $opportunities
            Prompt        = $prompt
            Response      = $responseText
            PromptPath    = $promptPath
            ResponsePath  = $responsePath
        }
    }
    else {
        # Legacy single-item format — convert to opportunities array for consistency
        $legacyOpps = @([PSCustomObject]@{
            filePath    = $parsed.filePath
            title       = if ($parsed.title) { $parsed.title } else { $parsed.explanation }
            explanation = $parsed.explanation
            scope       = 'narrow'
            rootCause   = $null
        })
        if ($parsed.additionalOpportunities) {
            foreach ($addl in $parsed.additionalOpportunities) {
                $legacyOpps += [PSCustomObject]@{
                    filePath    = if ($addl.filePath) { $addl.filePath } else { $parsed.filePath }
                    title       = if ($addl.description) { $addl.description } else { $addl.explanation }
                    explanation = if ($addl.description) { $addl.description } else { $addl.explanation }
                    scope       = if ($addl.scope) { $addl.scope } else { 'narrow' }
                    rootCause   = $null
                }
            }
        }
        $result = [ordered]@{
            Success       = ($copilotExitCode -eq 0 -and $null -ne $parsed.filePath)
            ExitCode      = $copilotExitCode
            FilePath      = $parsed.filePath
            Explanation   = $parsed.explanation
            Opportunities = $legacyOpps
            Prompt        = $prompt
            Response      = $responseText
            PromptPath    = $promptPath
            ResponsePath  = $responsePath
        }
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

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'info' `
        -Message "Analysis agent returned $oppCount opportunities (primary: $($result.FilePath))" `
        -Experiment $Experiment
}
catch {
    Stop-Spinner -Spinner $spinner -CompletionMessage $null
    if ($_ -is [System.Management.Automation.PipelineStoppedException]) { throw }
    $result = [ordered]@{
        Success       = $false
        ExitCode      = -1
        FilePath      = $null
        Explanation   = $null
        Opportunities = @()
        Prompt        = $prompt
        Response      = "Error: $_"
        PromptPath    = $promptPath
        ResponsePath  = $null
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'analyze' -Level 'warning' -Message "Analysis agent failed: $_" `
        -Experiment $Experiment
}

return [PSCustomObject]$result
