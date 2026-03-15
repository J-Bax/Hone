<#
.SYNOPSIS
    Reverts the code change from a failed experiment while preserving artifacts.

.DESCRIPTION
    On a failed experiment (regression/stale), this script:
    1. Restores the modified file to its pre-fix state
    2. Stages experiment artifacts (results, metadata, logs)
    3. Commits the revert with a descriptive message
    4. Pushes the branch so the failed attempt is preserved for the record

    The branch remains checked-out after this script completes, with clean
    code state matching the last successful experiment.  The next experiment
    can branch directly from this tip.

.PARAMETER BranchName
    The current experiment branch name.

.PARAMETER FilePath
    The file that was modified by the fix (relative to repo root).

.PARAMETER Experiment
    Current experiment number.

.PARAMETER Outcome
    Why the experiment failed: 'regressed' or 'stale'.

.PARAMETER Description
    Brief description of the original optimization that was attempted.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BranchName,

    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [int]$Experiment,

    [Parameter(Mandatory)]
    [ValidateSet('regressed', 'stale')]
    [string]$Outcome,

    [string]$Description,

    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$submoduleDir = Join-Path $repoRoot 'sample-api'
$submoduleRelPath = $FilePath -replace '^sample-api[\\/]', ''

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'publish' -Level 'info' `
    -Message "Reverting code change on branch: $BranchName ($Outcome)" `
    -Experiment $Experiment `
    -Data @{ file = $FilePath; branch = $BranchName; outcome = $Outcome }

try {
    Push-Location $submoduleDir

    # Restore the modified file to its state before the fix commit
    git checkout HEAD~1 -- $submoduleRelPath 2>&1 | Out-Null

    # Stage the reverted file
    git add $submoduleRelPath 2>&1 | Out-Null

    # Stage experiment artifacts — agent analysis, metrics, and RCA (raw data is gitignored)
    $experimentDir = Join-Path $submoduleDir 'results' "experiment-$Experiment"
    if (Test-Path $experimentDir) {
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
        # Top-level dotnet-counters data (produced by Invoke-ScaleTests.ps1)
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

    # Stage metadata files (experiment-log.md, experiment-queue.md)
    $metadataDir = Join-Path $submoduleDir 'results' 'metadata'
    if (Test-Path $metadataDir) {
        git add results/metadata/ 2>&1 | Out-Null
    }

    # Stage run-metadata.json
    $runMetadataFile = Join-Path $submoduleDir 'results' 'run-metadata.json'
    if (Test-Path $runMetadataFile) {
        git add results/run-metadata.json 2>&1 | Out-Null
    }

    $shortDesc = if ($Description) {
        if ($Description.Length -le 120) { $Description }
        else {
            $trunc = $Description.Substring(0, 120)
            $lastSp = $trunc.LastIndexOf(' ')
            if ($lastSp -gt 60) { $trunc.Substring(0, $lastSp) + '…' } else { $trunc + '…' }
        }
    } else { $Outcome }

    git commit -m "hone(experiment-$Experiment): revert — $Outcome`n`nReverted: $shortDesc" 2>&1 | Out-Null

    # Push the branch so the failed attempt is preserved remotely
    git push -u origin $BranchName 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to push branch '$BranchName' to origin (exit code $LASTEXITCODE) — revert is local only"

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'warning' `
            -Message "git push failed for revert branch: $BranchName (exit code $LASTEXITCODE)" `
            -Experiment $Experiment
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'publish' -Level 'info' `
        -Message "Revert committed and pushed on branch: $BranchName" `
        -Experiment $Experiment

    $result = [ordered]@{
        Success    = $true
        BranchName = $BranchName
        FilePath   = $FilePath
        Outcome    = $Outcome
    }
}
catch {
    $result = [ordered]@{
        Success    = $false
        BranchName = $BranchName
        FilePath   = $FilePath
        Outcome    = "RevertError: $_"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'publish' -Level 'error' `
        -Message "Failed to revert experiment code: $_" `
        -Experiment $Experiment
}
finally {
    Pop-Location
}

return [PSCustomObject]$result
