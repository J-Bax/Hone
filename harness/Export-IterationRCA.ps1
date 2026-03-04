<#
.SYNOPSIS
    Generates a root cause analysis document for a harness iteration.

.DESCRIPTION
    Parses the Copilot response and current performance metrics to produce
    a concise root-cause markdown file stored in the iteration subfolder.
    The document covers what performance issue was identified, the rationale
    behind the proposed fix, and the fix details.

.PARAMETER CopilotResponse
    Raw text of the Copilot CLI response.

.PARAMETER CurrentMetrics
    PSCustomObject with current iteration metrics (p95, RPS, error rate).

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
    [string]$CopilotResponse,

    [Parameter(Mandatory)]
    [PSCustomObject]$CurrentMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$BaselineMetrics,

    [Parameter(Mandatory)]
    [PSCustomObject]$ComparisonResult,

    [int]$Iteration = 0,

    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Generating root cause analysis for iteration $Iteration" `
    -Iteration $Iteration

# ── Parse the Copilot response into sections ────────────────────────────────
# The prompt asks Copilot to respond with numbered sections:
#   1. File path   2. Explanation   3. Code   4. Additional opportunities
# We attempt regex splitting; fall back to the full response on failure.

$filePath = ''
$explanation = ''
$codeBlock = ''

try {
    # Match "1." or "1)" style numbered sections
    $sections = [regex]::Split($CopilotResponse, '(?m)^\s*(?:\d+[\.\)]\s*)')
    # Filter out empty entries
    $sections = $sections | Where-Object { $_.Trim().Length -gt 0 }

    if ($sections.Count -ge 2) {
        # Section 1: file path — strip markdown formatting, backticks, bold markers
        $filePath    = ($sections[0]).Trim() -replace '[`*\r\n]', '' -replace '^\*\*', '' -replace '\*\*$', ''
        # If the path contains descriptive text after the actual path, take only the path
        if ($filePath -match '([\w/\\]+\.cs\b)') {
            $filePath = $Matches[1]
        }
        $explanation = ($sections[1]).Trim()
    }
    if ($sections.Count -ge 3) {
        $rawCode = ($sections[2]).Trim()
        # Extract content from fenced code block (```csharp ... ``` or ``` ... ```)
        if ($rawCode -match '(?ms)```(?:\w+)?\s*\r?\n(.+?)```') {
            $codeBlock = $Matches[1].TrimEnd()
        }
        else {
            # No fenced block — use the raw section as the code
            $codeBlock = $rawCode
        }
    }
}
catch {
    # Parsing failed — we'll use the full response as fallback
    $explanation = $CopilotResponse
}

# If parsing produced nothing useful, fall back
if ([string]::IsNullOrWhiteSpace($explanation)) {
    $explanation = $CopilotResponse
}

# ── Build metric context ────────────────────────────────────────────────────
$d = $ComparisonResult.Deltas
$p95Current  = $CurrentMetrics.HttpReqDuration.P95
$p95Baseline = $BaselineMetrics.HttpReqDuration.P95
$rpsCurrent  = [math]::Round($CurrentMetrics.HttpReqs.Rate, 1)
$rpsBaseline = [math]::Round($BaselineMetrics.HttpReqs.Rate, 1)
$errCurrent  = [math]::Round($CurrentMetrics.HttpReqFailed.Rate * 100, 2)
$errBaseline = [math]::Round($BaselineMetrics.HttpReqFailed.Rate * 100, 2)
$improvPct   = $ComparisonResult.ImprovementPct

# ── Build the RCA markdown ─────────────────────────────────────────────────
$rca = @"
# Root Cause Analysis — Iteration $Iteration

> Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

## Performance Issue

| Metric | Current | Baseline | Delta |
|--------|---------|----------|-------|
| p95 Latency | ${p95Current}ms | ${p95Baseline}ms | $($d.P95Latency.ChangePct)% |
| Requests/sec | $rpsCurrent | $rpsBaseline | $($d.RPS.ChangePct)% |
| Error Rate | ${errCurrent}% | ${errBaseline}% | $($d.ErrorRate.ChangePct)% |

Overall improvement vs baseline: **${improvPct}%** (p95 latency).

$(if ($ComparisonResult.EfficiencyDeltas) {
    $ed = $ComparisonResult.EfficiencyDeltas
    "**Efficiency:** CPU $($ed.CpuUsage.Current)% ($($ed.CpuUsage.ChangePct)% delta) | Working Set $($ed.WorkingSet.Current)MB ($($ed.WorkingSet.ChangePct)% delta)"
})

## Root Cause / Rationale

$(if ($filePath) { "**Target file:** ``$filePath```n" })
$explanation

## Proposed Fix

$(if ($codeBlock) {
    "``````csharp`n$codeBlock`n```````n"
} else {
    "_Complete replacement code included in the Copilot response — see ``copilot-response.md`` for full details._"
})
"@

# ── Write the RCA file ──────────────────────────────────────────────────────
$iterDir = Join-Path $repoRoot $config.Api.ResultsPath "iteration-$Iteration"
if (-not (Test-Path $iterDir)) {
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null
}

$rcaPath = Join-Path $iterDir 'root-cause.md'
$rca | Out-File -FilePath $rcaPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'analyze' -Level 'info' -Message "Root cause analysis saved to $rcaPath" `
    -Iteration $Iteration

# ── Return result ───────────────────────────────────────────────────────────
[PSCustomObject][ordered]@{
    Success     = $true
    Path        = $rcaPath
    FilePath    = $filePath
    Explanation = $explanation
    CodeBlock   = $codeBlock
}
