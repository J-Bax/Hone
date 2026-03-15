<#
.SYNOPSIS
    Stages experiment artifacts for git commit.

.DESCRIPTION
    Stages analysis prompts, responses, k6 summaries, dotnet-counters data,
    collector summaries, analyzer outputs, and metadata files. Must be called
    from within the sample-api submodule directory (or will Push-Location to it).

.PARAMETER Experiment
    The experiment number.

.PARAMETER SubmoduleDir
    Path to the sample-api submodule root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$Experiment,

    [Parameter(Mandatory)]
    [string]$SubmoduleDir
)

Push-Location $SubmoduleDir
try {
    $experimentDir = Join-Path -Path $SubmoduleDir -ChildPath 'results' "experiment-$Experiment"
    if (Test-Path $experimentDir) {
        # Analysis artifacts
        foreach ($pattern in @('analysis-prompt.md', 'analysis-response.json',
                'classification-response.json', 'root-cause.md')) {
            $f = Join-Path $experimentDir $pattern
            if (Test-Path $f) {
                git add "results/experiment-$Experiment/$pattern" 2>&1 | Out-Null
            }
        }
        # k6 performance summaries
        Get-ChildItem $experimentDir -Filter 'k6-summary*.json' -ErrorAction SilentlyContinue |
            ForEach-Object { git add "results/experiment-$Experiment/$($_.Name)" 2>&1 | Out-Null }
        # Top-level dotnet-counters data
        foreach ($counterFile in @('dotnet-counters.json', 'dotnet-counters.csv')) {
            $f = Join-Path $experimentDir $counterFile
            if (Test-Path $f) {
                git add "results/experiment-$Experiment/$counterFile" 2>&1 | Out-Null
            }
        }
        # Parsed metric summaries from collectors
        foreach ($summary in @('diagnostics/dotnet-counters/dotnet-counters.json',
                'diagnostics/perfview-gc/gc-report.json')) {
            if (Test-Path (Join-Path $experimentDir $summary)) {
                git add "results/experiment-$Experiment/$summary" 2>&1 | Out-Null
            }
        }
        # Analyzer prompt/response files
        foreach ($analyzer in @('cpu-hotspots', 'memory-gc')) {
            $analyzerDir = Join-Path $experimentDir "diagnostics/$analyzer"
            if (Test-Path $analyzerDir) {
                Get-ChildItem $analyzerDir -Include '*-prompt.md', '*-response.json' -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        git add "results/experiment-$Experiment/diagnostics/$analyzer/$($_.Name)" 2>&1 | Out-Null
                    }
            }
        }
    }

    # Stage metadata files
    $metadataDir = Join-Path -Path $SubmoduleDir -ChildPath 'results' 'metadata'
    if (Test-Path $metadataDir) {
        git add results/metadata/ 2>&1 | Out-Null
    }

    # Stage run-metadata.json
    $runMetadataFile = Join-Path -Path $SubmoduleDir -ChildPath 'results' 'run-metadata.json'
    if (Test-Path $runMetadataFile) {
        git add results/run-metadata.json 2>&1 | Out-Null
    }
} finally {
    Pop-Location
}
