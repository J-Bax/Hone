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
    Why the experiment failed: 'regressed', 'stale', 'build_failure',
    'test_failure', 'retry_budget_exhausted', or 'retry'.

.PARAMETER Description
    Brief description of the original optimization that was attempted.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER TargetDir
    Root directory of the target project. Used for git operations.
#>
[CmdletBinding()]
# ConfigPath is part of the harness script interface; kept for consistency
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'ConfigPath')]
param(
    [Parameter(Mandatory)]
    [string]$BranchName,

    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [int]$Experiment,

    [Parameter(Mandatory)]
    [ValidateSet('regressed', 'stale', 'build_failure', 'test_failure', 'retry_budget_exhausted', 'retry')]
    [string]$Outcome,

    [string]$Description,

    [string]$ConfigPath,

    [switch]$SoftReset,

    [Parameter(Mandatory)]
    [string]$TargetDir
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$submoduleDir = $TargetDir
$targetLeaf = Split-Path $TargetDir -Leaf
$submoduleRelPath = $FilePath -replace "^$([regex]::Escape($targetLeaf))[\\/]", ''

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'publish' -Level 'info' `
    -Message "Reverting code change on branch: $BranchName ($Outcome)" `
    -Experiment $Experiment `
    -Data @{ file = $FilePath; branch = $BranchName; outcome = $Outcome }

try {
    Push-Location $submoduleDir

    if ($SoftReset) {
        git reset --soft HEAD~1 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "git reset --soft HEAD~1 failed with exit code $LASTEXITCODE"
        }

        git checkout HEAD -- $submoduleRelPath 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "git checkout HEAD -- $submoduleRelPath failed with exit code $LASTEXITCODE"
        }

        git reset HEAD -- $submoduleRelPath 2>&1 | Out-Null

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'info' `
            -Message "Soft reset completed for retry on branch: $BranchName" `
            -Experiment $Experiment

        $result = [ordered]@{
            Success = $true
            BranchName = $BranchName
            FilePath = $FilePath
            Outcome = $Outcome
            SoftReset = $true
        }

        return [PSCustomObject]$result
    }

    # Restore the modified file to its state before the fix commit
    git checkout HEAD~1 -- $submoduleRelPath 2>&1 | Out-Null

    # Stage the reverted file
    git add $submoduleRelPath 2>&1 | Out-Null

    # Stage experiment artifacts
    & (Join-Path $PSScriptRoot 'Stage-ExperimentArtifacts.ps1') `
        -Experiment $Experiment -SubmoduleDir $submoduleDir -ConfigPath $ConfigPath

    $shortDesc = if ($Description) {
        if ($Description.Length -le 120) { $Description }
        else {
            $trunc = $Description.Substring(0, 120)
            $lastSp = $trunc.LastIndexOf(' ')
            if ($lastSp -gt 60) { $trunc.Substring(0, $lastSp) + '…' } else { $trunc + '…' }
        }
    } else { $Outcome }

    git commit --no-gpg-sign -m "hone(experiment-$Experiment): revert — $Outcome`n`nReverted: $shortDesc" 2>&1 | Out-Null

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
        Success = $true
        BranchName = $BranchName
        FilePath = $FilePath
        Outcome = $Outcome
    }
} catch {
    $result = [ordered]@{
        Success = $false
        BranchName = $BranchName
        FilePath = $FilePath
        Outcome = "RevertError: $_"
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'publish' -Level 'error' `
        -Message "Failed to revert experiment code: $_" `
        -Experiment $Experiment
} finally {
    Pop-Location
}

return [PSCustomObject]$result
