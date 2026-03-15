<#
.SYNOPSIS
    Discovers and runs all enabled diagnostic analyzer plugins.

.DESCRIPTION
    Scans the analyzers directory for plugin directories, loads their
    metadata from analyzer.psd1, verifies required collector data is
    available, and runs each enabled analyzer.  Returns an aggregated
    hashtable of analyzer reports.

    Each analyzer must expose:
      - analyzer.psd1       (metadata: Name, RequiredCollectors, AgentName, DefaultSettings)
      - Invoke-Analyzer.ps1 (params: CollectorData, CurrentMetrics, Experiment, Settings, OutputDir → report)
      - agent.md            (Copilot agent definition, symlinked to .github/agents/)

.PARAMETER CollectorData
    Hashtable keyed by collector name → @{ ExportedPaths; Summary }.

.PARAMETER CurrentMetrics
    Current performance metrics object (HttpReqDuration, HttpReqs, etc.).

.PARAMETER Experiment
    Current experiment number.

.PARAMETER Config
    Loaded harness config hashtable.

.PARAMETER OutputDir
    Root directory for analyzer output (per-analyzer subdirs are created).

.OUTPUTS
    @{ Success; Reports = @{ 'cpu-hotspots' = @{ Report; Summary }; ... } }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable]$CollectorData,

    [Parameter(Mandatory)]
    $CurrentMetrics,

    [Parameter(Mandatory)]
    [int]$Experiment,

    [Parameter(Mandatory)]
    [hashtable]$Config,

    [Parameter(Mandatory)]
    [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot
$diagnostics = $Config.Diagnostics

$analyzersPath = Join-Path $repoRoot $diagnostics.AnalyzersPath
if (-not (Test-Path $analyzersPath)) {
    Write-Warning "Analyzers directory not found: $analyzersPath"
    return @{ Success = $false; Reports = @{} }
}

# ── Discover enabled analyzers ──────────────────────────────────────────────
$analyzers = [System.Collections.Generic.List[hashtable]]::new()

foreach ($dir in (Get-ChildItem -Path $analyzersPath -Directory)) {
    $metaPath = Join-Path $dir.FullName 'analyzer.psd1'
    if (-not (Test-Path $metaPath)) { continue }

    $meta = Import-PowerShellDataFile $metaPath
    $name = $meta.Name ?? $dir.Name

    $configSettings = if ($diagnostics.AnalyzerSettings.ContainsKey($name)) {
        $diagnostics.AnalyzerSettings[$name]
    } else { @{} }

    $enabled = if ($configSettings.ContainsKey('Enabled')) {
        $configSettings.Enabled
    } else { $true }

    if (-not $enabled) {
        Write-Verbose "Analyzer '$name' is disabled in config — skipping"
        continue
    }

    # Check required collectors have data
    $requiredCollectors = $meta.RequiredCollectors ?? @()
    $missingCollectors = @($requiredCollectors | Where-Object { -not $CollectorData.ContainsKey($_) })

    if ($missingCollectors.Count -gt 0) {
        Write-Warning "Analyzer '$name' requires collectors ($($missingCollectors -join ', ')) but data is missing — skipping"
        continue
    }

    # Merge settings: config overrides → defaults
    $merged = @{}
    if ($meta.DefaultSettings) {
        foreach ($k in $meta.DefaultSettings.Keys) { $merged[$k] = $meta.DefaultSettings[$k] }
    }
    foreach ($k in $configSettings.Keys) {
        if ($k -ne 'Enabled') { $merged[$k] = $configSettings[$k] }
    }

    $analyzers.Add(@{
        Name     = $name
        Dir      = $dir.FullName
        Meta     = $meta
        Settings = $merged
    })
}

if ($analyzers.Count -eq 0) {
    Write-Status '  No enabled analyzers with available collector data'
    return @{ Success = $true; Reports = @{} }
}

# ── Run each analyzer ───────────────────────────────────────────────────────
$reports = @{}
$allSuccess = $true

foreach ($a in $analyzers) {
    $invokeScript = Join-Path $a.Dir 'Invoke-Analyzer.ps1'
    if (-not (Test-Path $invokeScript)) {
        Write-Warning "Invoke-Analyzer.ps1 not found for '$($a.Name)'"
        $allSuccess = $false
        continue
    }

    $analyzerOutputDir = Join-Path $OutputDir $a.Name
    if (-not (Test-Path $analyzerOutputDir)) {
        New-Item -ItemType Directory -Path $analyzerOutputDir -Force | Out-Null
    }

    Write-Status "  Running analyzer: $($a.Name)"

    try {
        $result = & $invokeScript `
            -CollectorData $CollectorData `
            -CurrentMetrics $CurrentMetrics `
            -Experiment $Experiment `
            -Settings $a.Settings `
            -OutputDir $analyzerOutputDir

        if ($result.Success) {
            $reports[$a.Name] = @{
                Report      = $result.Report
                Summary     = $result.Summary
                PromptPath  = $result.PromptPath
                ResponsePath = $result.ResponsePath
            }
            Write-Status "    $($a.Name): $($result.Summary)"
        }
        else {
            Write-Warning "  Analyzer '$($a.Name)' failed: $($result.Error ?? 'unknown error')"
            $allSuccess = $false
        }
    }
    catch {
        Write-Warning "  Analyzer '$($a.Name)' threw an exception: $_"
        $allSuccess = $false
    }
}

return @{ Success = $allSuccess; Reports = $reports }
