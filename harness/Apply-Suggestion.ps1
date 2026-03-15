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
Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath
$branchName = "$($config.Loop.BranchPrefix)-$Experiment"
$fullPath = Join-Path $repoRoot $FilePath

# Validate target path is within sample-api to prevent path traversal
$resolvedPath = [System.IO.Path]::GetFullPath($fullPath)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'sample-api'))
if (-not $resolvedPath.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Path traversal blocked: '$FilePath' resolves to '$resolvedPath' which is outside '$allowedRoot'"
}

# Determine the submodule root for git operations
$submoduleDir = Join-Path $repoRoot 'sample-api'
# Strip the 'sample-api/' prefix so paths are relative to the submodule root
$submoduleRelPath = $FilePath -replace '^sample-api[\\/]', ''

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'experiment' -Level 'info' `
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

    # Stage experiment artifacts
    & (Join-Path $PSScriptRoot 'Stage-ExperimentArtifacts.ps1') `
        -Experiment $Experiment -SubmoduleDir $submoduleDir

    git commit -m "hone(experiment-$Experiment): $Description" 2>&1 | Out-Null

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'info' `
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
        -Phase 'experiment' -Level 'error' `
        -Message "Failed to apply fix: $_" `
        -Experiment $Experiment
}
finally {
    Pop-Location
}

return [PSCustomObject]$result
