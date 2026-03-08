<#
.SYNOPSIS
    Applies an optimization suggestion on a new git branch.

.DESCRIPTION
    Creates a new git branch for the current experiment, applies the suggested
    code change, and commits it along with experiment artifacts and metadata.

.PARAMETER FilePath
    The file to modify (relative to repo root).

.PARAMETER NewContent
    The new content for the file.

.PARAMETER Description
    Brief description of the change (used in commit message).

.PARAMETER Experiment
    Current experiment number.

.PARAMETER BaseBranch
    The branch to fork from.  Defaults to 'master' (legacy mode).
    In stacked-diffs mode, pass the previous experiment's branch name.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string]$NewContent,

    [Parameter(Mandatory)]
    [string]$Description,

    [int]$Experiment = 0,

    [string]$BaseBranch = 'master',

    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$branchName = "$($config.Loop.BranchPrefix)-$Experiment"
$fullPath = Join-Path $repoRoot $FilePath

# Determine the submodule root for git operations
$submoduleDir = Join-Path $repoRoot 'sample-api'
# Strip the 'sample-api/' prefix so paths are relative to the submodule root
$submoduleRelPath = $FilePath -replace '^sample-api[\\/]', ''

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'fix' -Level 'info' `
    -Message "Applying fix on branch: $branchName — $Description" `
    -Experiment $Experiment `
    -Data @{ file = $FilePath; branch = $branchName }

try {
    # Create and switch to a new branch inside the submodule
    Push-Location $submoduleDir

    # Branch from the specified base (master in legacy mode, previous experiment in stacked mode)
    git checkout -b $branchName $BaseBranch 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # Branch might already exist, try switching to it
        git checkout $branchName 2>&1 | Out-Null
    }

    # Write the new content
    $NewContent | Out-File -FilePath $fullPath -Encoding utf8 -Force

    # Stage the code fix (path relative to submodule root)
    git add $submoduleRelPath 2>&1 | Out-Null

    # Stage experiment artifacts (root-cause.md, k6-summary)
    $experimentDir = Join-Path $submoduleDir 'results' "experiment-$Experiment"
    if (Test-Path $experimentDir) {
        $rcaFile = Join-Path $experimentDir 'root-cause.md'
        $k6Summaries = Get-ChildItem -Path $experimentDir -Filter 'k6-summary*.json' -ErrorAction SilentlyContinue
        if (Test-Path $rcaFile) {
            git add "results/experiment-$Experiment/root-cause.md" 2>&1 | Out-Null
        }
        foreach ($summary in $k6Summaries) {
            git add "results/experiment-$Experiment/$($summary.Name)" 2>&1 | Out-Null
        }
    }

    # Stage metadata files (optimization-log.md, optimization-queue.md)
    $metadataDir = Join-Path $submoduleDir 'results' 'metadata'
    if (Test-Path $metadataDir) {
        git add results/metadata/ 2>&1 | Out-Null
    }

    # Stage run-metadata.json
    $runMetadataFile = Join-Path $submoduleDir 'results' 'run-metadata.json'
    if (Test-Path $runMetadataFile) {
        git add results/run-metadata.json 2>&1 | Out-Null
    }

    git commit -m "hone(experiment-$Experiment): $Description" 2>&1 | Out-Null

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'fix' -Level 'info' `
        -Message "Fix committed on branch: $branchName" `
        -Experiment $Experiment

    $result = [ordered]@{
        Success     = $true
        BranchName  = $branchName
        FilePath    = $FilePath
        Description = $Description
    }
}
catch {
    $result = [ordered]@{
        Success     = $false
        BranchName  = $branchName
        FilePath    = $FilePath
        Description = "Error: $_"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'fix' -Level 'error' `
        -Message "Failed to apply fix: $_" `
        -Experiment $Experiment
}
finally {
    Pop-Location
}

return [PSCustomObject]$result
