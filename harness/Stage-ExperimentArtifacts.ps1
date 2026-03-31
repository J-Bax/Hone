<#
.SYNOPSIS
    Stages experiment artifacts for git commit.

.DESCRIPTION
    Stages analysis prompts, responses, k6 summaries, dotnet-counters data,
    collector summaries, analyzer outputs, and metadata files. Must be called
    from within the target project directory (or will Push-Location to it).

.PARAMETER Experiment
    The experiment number.

.PARAMETER TargetDir
    Path to the target project root.

.PARAMETER SubmoduleDir
    Alias for TargetDir. Kept for backward compatibility.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$Experiment,

    [Parameter(Mandatory)]
    [Alias('SubmoduleDir')]
    [string]$TargetDir,

    [string]$ConfigPath
)

Push-Location $TargetDir
try {
    $resultsRoot = 'results'
    if ($ConfigPath) {
        Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force
        $config = Get-HoneConfig -ConfigPath $ConfigPath
        $targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
        if (Test-Path $targetConfigPath) {
            $targetCfg = Import-PowerShellDataFile -Path $targetConfigPath
            $config = Merge-HoneConfig -Engine $config -Target $targetCfg
        }
        if ($config.Api.ResultsPath) {
            $resultsRoot = $config.Api.ResultsPath
        }
    }

    $experimentDir = Join-Path -Path $TargetDir -ChildPath $resultsRoot -AdditionalChildPath "experiment-$Experiment"
    if (Test-Path $experimentDir) {
        # Analysis artifacts
        foreach ($pattern in @('analysis-prompt.md', 'analysis-response.json',
                'classification-response.json', 'fix-response.md', 'root-cause.md',
                'build.log', 'e2e-tests.log', 'e2e-results.trx', 'k6.log')) {
            $f = Join-Path $experimentDir $pattern
            if (Test-Path $f) {
                git add "$resultsRoot/experiment-$Experiment/$pattern" 2>&1 | Out-Null
            }
        }

        Get-ChildItem -Path $experimentDir -Filter 'k6-*.log' -ErrorAction SilentlyContinue |
            ForEach-Object { git add "$resultsRoot/experiment-$Experiment/$($_.Name)" 2>&1 | Out-Null }
        # k6 performance summaries
        Get-ChildItem $experimentDir -Filter 'k6-summary*.json' -ErrorAction SilentlyContinue |
            ForEach-Object { git add "$resultsRoot/experiment-$Experiment/$($_.Name)" 2>&1 | Out-Null }
        # Top-level dotnet-counters data
        foreach ($counterFile in @('dotnet-counters.json', 'dotnet-counters.csv')) {
            $f = Join-Path $experimentDir $counterFile
            if (Test-Path $f) {
                git add "$resultsRoot/experiment-$Experiment/$counterFile" 2>&1 | Out-Null
            }
        }
        # Parsed metric summaries from collectors
        foreach ($summary in @('diagnostics/dotnet-counters/dotnet-counters.json',
                'diagnostics/perfview-gc/gc-report.json')) {
            if (Test-Path (Join-Path $experimentDir $summary)) {
                git add "$resultsRoot/experiment-$Experiment/$summary" 2>&1 | Out-Null
            }
        }
        # Analyzer prompt/response files
        foreach ($analyzer in @('cpu-hotspots', 'memory-gc')) {
            $analyzerDir = Join-Path $experimentDir "diagnostics/$analyzer"
            if (Test-Path $analyzerDir) {
                Get-ChildItem $analyzerDir -Include '*-prompt.md', '*-response.json' -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        git add "$resultsRoot/experiment-$Experiment/diagnostics/$analyzer/$($_.Name)" 2>&1 | Out-Null
                    }
            }
        }
    }

    # Stage metadata files
    $metadataDir = Join-Path -Path $TargetDir -ChildPath $resultsRoot -AdditionalChildPath 'metadata'
    if (Test-Path $metadataDir) {
        git add "$resultsRoot/metadata/" 2>&1 | Out-Null
    }

    # Stage run-metadata.json
    $runMetadataFile = Join-Path -Path $TargetDir -ChildPath $resultsRoot -AdditionalChildPath 'run-metadata.json'
    if (Test-Path $runMetadataFile) {
        git add "$resultsRoot/run-metadata.json" 2>&1 | Out-Null
    }
} finally {
    Pop-Location
}
