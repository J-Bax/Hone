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

.PARAMETER TargetDir
    Root directory of the target project. Used for the security boundary
    (path-traversal check) and git operations.
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

    [string]$ConfigPath,

    [Parameter(Mandatory)]
    [string]$TargetDir
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath
if ($TargetDir) {
    $targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
    if (Test-Path $targetConfigPath) {
        $targetCfg = Import-PowerShellDataFile -Path $targetConfigPath
        $config = Merge-HoneConfig -Engine $config -Target $targetCfg
    }
}
$resultsRoot = if ($config.Api.ResultsPath) { $config.Api.ResultsPath } else { 'results' }
$branchName = "$($config.Loop.BranchPrefix)-$Experiment"
$fullPath = Join-Path $TargetDir $FilePath

# Validate target path is within target directory to prevent path traversal
$resolvedPath = [System.IO.Path]::GetFullPath($fullPath)
$allowedRoot = [System.IO.Path]::GetFullPath($TargetDir)
if (-not $resolvedPath.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Path traversal blocked: '$FilePath' resolves to '$resolvedPath' which is outside '$allowedRoot'"
}

# Determine the target root for git operations
$submoduleDir = $TargetDir
# Strip the target directory prefix so paths are relative to the target root
$targetLeaf = Split-Path $TargetDir -Leaf
$submoduleRelPath = $FilePath -replace "^$([regex]::Escape($targetLeaf))[\\/]", ''

function Get-PreservedRuntimeState {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$ResultsPath
    )

    $files = [System.Collections.Generic.List[object]]::new()
    $pathsToCapture = [System.Collections.Generic.List[string]]::new()

    $resultsDir = Join-Path -Path $RepositoryRoot -ChildPath $ResultsPath
    if (Test-Path -Path $resultsDir) {
        foreach ($file in Get-ChildItem -Path $resultsDir -Recurse -File -ErrorAction SilentlyContinue) {
            $pathsToCapture.Add($file.FullName)
        }
    }

    foreach ($path in ($pathsToCapture | Select-Object -Unique)) {
        $relativePath = $path.Substring($RepositoryRoot.Length).TrimStart('\')
        $files.Add([PSCustomObject]@{
                RelativePath = $relativePath
                Bytes = [System.IO.File]::ReadAllBytes($path)
            })
    }

    return @($files)
}

function Reset-RuntimeStateForBranchSwitch {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [object[]]$Snapshot
    )

    if (-not $PSCmdlet.ShouldProcess($RepositoryRoot, 'Reset preserved runtime state before branch switch')) {
        return
    }

    foreach ($entry in $Snapshot) {
        $relativePath = $entry.RelativePath.Replace('\', '/')
        & git ls-files --error-unmatch -- $relativePath 2>$null | Out-Null
        $isTracked = ($LASTEXITCODE -eq 0)

        if ($isTracked) {
            git restore --source=HEAD --staged --worktree -- $relativePath 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to reset tracked runtime state before branching: $relativePath"
            }
        }

        if (-not $isTracked) {
            $fullPath = Join-Path -Path $RepositoryRoot -ChildPath $entry.RelativePath
            if (Test-Path -Path $fullPath) {
                Remove-Item -Path $fullPath -Force -ErrorAction Stop
            }
        }
    }
}

function Restore-PreservedRuntimeState {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [object[]]$Snapshot
    )

    if (-not $PSCmdlet.ShouldProcess($RepositoryRoot, 'Restore preserved runtime state after branch switch')) {
        return
    }

    foreach ($entry in $Snapshot) {
        $destinationPath = Join-Path -Path $RepositoryRoot -ChildPath $entry.RelativePath
        $parentDir = Split-Path -Path $destinationPath -Parent
        if ($parentDir -and -not (Test-Path -Path $parentDir)) {
            New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
        }

        [System.IO.File]::WriteAllBytes($destinationPath, $entry.Bytes)
    }
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'experiment' -Level 'info' `
    -Message "Applying fix on branch: $branchName — $Description" `
    -Experiment $Experiment `
    -Data @{ file = $FilePath; branch = $branchName }

try {
    # Create and switch to a new branch inside the submodule
    Push-Location $submoduleDir

    $runtimeStateSnapshot = Get-PreservedRuntimeState -RepositoryRoot $submoduleDir -ResultsPath $resultsRoot
    if ($runtimeStateSnapshot.Count -gt 0) {
        Reset-RuntimeStateForBranchSwitch -RepositoryRoot $submoduleDir -Snapshot $runtimeStateSnapshot
    }

    # Branch from the specified base (master in legacy mode, previous experiment in stacked mode)
    $branchCreateOutput = @(git checkout -b $branchName $BaseBranch 2>&1)
    if ($LASTEXITCODE -ne 0) {
        # Branch might already exist, try switching to it
        $branchSwitchOutput = @(git checkout $branchName 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $createMessage = ($branchCreateOutput | Out-String).Trim()
            $switchMessage = ($branchSwitchOutput | Out-String).Trim()
            throw "Failed to switch to experiment branch '$branchName' from '$BaseBranch'. Create output: $createMessage`nExisting branch output: $switchMessage"
        }
    }

    $currentBranchName = (& git rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
    if (-not $currentBranchName) {
        throw "Unable to determine the current branch after switching to '$branchName'."
    }

    if ($currentBranchName -ne $branchName) {
        throw "Failed to switch to experiment branch '$branchName'. Current branch is '$currentBranchName'."
    }

    if ($runtimeStateSnapshot.Count -gt 0) {
        Restore-PreservedRuntimeState -RepositoryRoot $submoduleDir -Snapshot $runtimeStateSnapshot

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'experiment' -Level 'info' `
            -Message "Restored runtime state after branching ($($runtimeStateSnapshot.Count) files)" `
            -Experiment $Experiment
    }

    # Write the new content
    $NewContent | Out-File -FilePath $fullPath -Encoding utf8 -Force

    # Stage the code fix (path relative to submodule root)
    git add $submoduleRelPath 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to stage modified file '$submoduleRelPath' on branch '$branchName' (exit code $LASTEXITCODE)."
    }

    # Stage experiment artifacts
    & (Join-Path $PSScriptRoot 'Stage-ExperimentArtifacts.ps1') `
        -Experiment $Experiment -SubmoduleDir $submoduleDir -ConfigPath $ConfigPath

    $commitOutput = @(git commit --no-gpg-sign -m "hone(experiment-$Experiment): $Description" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to commit fix on branch '$branchName': $(($commitOutput | Out-String).Trim())"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'info' `
        -Message "Fix committed on branch: $branchName" `
        -Experiment $Experiment

    $result = [ordered]@{
        Success = $true
        BranchName = $branchName
        FilePath = $FilePath
        Description = $Description
        CommitSha = (& git rev-parse HEAD 2>$null | Out-String).Trim()
    }
} catch {
    $result = [ordered]@{
        Success = $false
        BranchName = $branchName
        FilePath = $FilePath
        Description = "Error: $_"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'error' `
        -Message "Failed to apply fix: $_" `
        -Experiment $Experiment
} finally {
    Pop-Location
}

return [PSCustomObject]$result
