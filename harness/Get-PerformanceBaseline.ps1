<#
.SYNOPSIS
    Establishes a performance baseline by running scale tests.

.DESCRIPTION
    Builds the API, starts it, runs the baseline k6 scenario, saves the results,
    and stops the API. The baseline results are stored in results/baseline.json
    and used by Compare-Results.ps1 for comparison.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
    -Phase 'baseline' -Level 'info' -Message 'Establishing performance baseline'

# ── Step 1: Build ───────────────────────────────────────────────────────────
$buildResult = & (Join-Path $PSScriptRoot 'Build-SampleApi.ps1') -ConfigPath $ConfigPath
if (-not $buildResult.Success) {
    Write-Error 'Build failed — cannot establish baseline'
    return
}

# ── Step 2: Start API ──────────────────────────────────────────────────────
$apiResult = & (Join-Path $PSScriptRoot 'Start-SampleApi.ps1') -ConfigPath $ConfigPath
if (-not $apiResult.Success) {
    Write-Error 'API failed to start — cannot establish baseline'
    return
}

try {
    # ── Step 3: Run scale tests ─────────────────────────────────────────────
    $scaleResult = & (Join-Path $PSScriptRoot 'Invoke-ScaleTests.ps1') `
        -ConfigPath $ConfigPath -Iteration 0

    if (-not $scaleResult.Success) {
        Write-Error 'Scale tests failed — cannot establish baseline'
        return
    }

    # ── Step 4: Save baseline ───────────────────────────────────────────────
    $baselinePath = Join-Path $repoRoot $config.ScaleTest.OutputPath 'baseline.json'
    $scaleResult.Metrics | ConvertTo-Json -Depth 5 | Out-File -FilePath $baselinePath -Encoding utf8

    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
        -Phase 'baseline' -Level 'info' `
        -Message "Baseline saved to: $baselinePath" `
        -Data @{
            p95 = $scaleResult.Metrics.HttpReqDuration.P95
            rps = $scaleResult.Metrics.HttpReqs.Rate
        }

    Write-Information "Baseline established:" -InformationAction Continue
    Write-Information "  p95 latency: $($scaleResult.Metrics.HttpReqDuration.P95)ms" -InformationAction Continue
    Write-Information "  RPS:         $([math]::Round($scaleResult.Metrics.HttpReqs.Rate, 1))" -InformationAction Continue
    Write-Information "  Error rate:  $([math]::Round(($scaleResult.Metrics.HttpReqFailed.Rate) * 100, 2))%" -InformationAction Continue
    Write-Information "  Saved to:    $baselinePath" -InformationAction Continue
}
finally {
    # ── Step 5: Stop API ────────────────────────────────────────────────────
    & (Join-Path $PSScriptRoot 'Stop-SampleApi.ps1') -Process $apiResult.Process
}
