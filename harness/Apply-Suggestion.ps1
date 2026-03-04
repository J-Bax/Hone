<#
.SYNOPSIS
    Applies an optimization suggestion on a new git branch.

.DESCRIPTION
    Creates a new git branch for the current iteration, applies the suggested
    code change, and commits it along with iteration artifacts and metadata.

.PARAMETER FilePath
    The file to modify (relative to repo root).

.PARAMETER NewContent
    The new content for the file.

.PARAMETER Description
    Brief description of the change (used in commit message).

.PARAMETER Iteration
    Current iteration number.

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

    [int]$Iteration = 0,

    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$branchName = "$($config.Loop.BranchPrefix)-$Iteration"
$fullPath = Join-Path $repoRoot $FilePath

# Determine the submodule root for git operations
$submoduleDir = Join-Path $repoRoot 'sample-api'
# Strip the 'sample-api/' prefix so paths are relative to the submodule root
$submoduleRelPath = $FilePath -replace '^sample-api[\\/]', ''

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'fix' -Level 'info' `
    -Message "Applying fix on branch: $branchName — $Description" `
    -Iteration $Iteration `
    -Data @{ file = $FilePath; branch = $branchName }

try {
    # Create and switch to a new branch inside the submodule
    Push-Location $submoduleDir

    git checkout -b $branchName 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # Branch might already exist, try switching to it
        git checkout $branchName 2>&1 | Out-Null
    }

    # Write the new content
    $NewContent | Out-File -FilePath $fullPath -Encoding utf8 -Force

    # Stage the code fix (path relative to submodule root)
    git add $submoduleRelPath 2>&1 | Out-Null

    # Stage iteration artifacts (root-cause.md, k6-summary)
    $iterationDir = Join-Path $submoduleDir 'results' "iteration-$Iteration"
    if (Test-Path $iterationDir) {
        $rcaFile = Join-Path $iterationDir 'root-cause.md'
        $k6Summaries = Get-ChildItem -Path $iterationDir -Filter 'k6-summary*.json' -ErrorAction SilentlyContinue
        if (Test-Path $rcaFile) {
            git add "results/iteration-$Iteration/root-cause.md" 2>&1 | Out-Null
        }
        foreach ($summary in $k6Summaries) {
            git add "results/iteration-$Iteration/$($summary.Name)" 2>&1 | Out-Null
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

    git commit -m "hone(iteration-$Iteration): $Description" 2>&1 | Out-Null

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'fix' -Level 'info' `
        -Message "Fix committed on branch: $branchName" `
        -Iteration $Iteration

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
        -Iteration $Iteration
}
finally {
    Pop-Location
}

return [PSCustomObject]$result
