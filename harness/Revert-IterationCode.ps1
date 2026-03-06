<#
.SYNOPSIS
    Reverts the code change from a failed iteration while preserving artifacts.

.DESCRIPTION
    On a failed iteration (regression/stale), this script:
    1. Restores the modified file to its pre-fix state
    2. Stages iteration artifacts (results, metadata, logs)
    3. Commits the revert with a descriptive message
    4. Pushes the branch so the failed attempt is preserved for the record

    The branch remains checked-out after this script completes, with clean
    code state matching the last successful iteration.  The next iteration
    can branch directly from this tip.

.PARAMETER BranchName
    The current iteration branch name.

.PARAMETER FilePath
    The file that was modified by the fix (relative to repo root).

.PARAMETER Iteration
    Current iteration number.

.PARAMETER Outcome
    Why the iteration failed: 'regressed' or 'stale'.

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
    [int]$Iteration,

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
    -Phase 'fix' -Level 'info' `
    -Message "Reverting code change on branch: $BranchName ($Outcome)" `
    -Iteration $Iteration `
    -Data @{ file = $FilePath; branch = $BranchName; outcome = $Outcome }

try {
    Push-Location $submoduleDir

    # Restore the modified file to its state before the fix commit
    git checkout HEAD~1 -- $submoduleRelPath 2>&1 | Out-Null

    # Stage the reverted file
    git add $submoduleRelPath 2>&1 | Out-Null

    # Stage iteration artifacts (k6 results, RCA, counters)
    $iterationDir = Join-Path $submoduleDir 'results' "iteration-$Iteration"
    if (Test-Path $iterationDir) {
        git add "results/iteration-$Iteration/" 2>&1 | Out-Null
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

    $shortDesc = if ($Description) {
        $Description.Substring(0, [Math]::Min(120, $Description.Length))
    } else { $Outcome }

    git commit -m "hone(iteration-$Iteration): revert — $Outcome`n`nReverted: $shortDesc" 2>&1 | Out-Null

    # Push the branch so the failed attempt is preserved remotely
    git push -u origin $BranchName 2>&1 | Out-Null

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'fix' -Level 'info' `
        -Message "Revert committed and pushed on branch: $BranchName" `
        -Iteration $Iteration

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
        -Phase 'fix' -Level 'error' `
        -Message "Failed to revert iteration code: $_" `
        -Iteration $Iteration
}
finally {
    Pop-Location
}

return [PSCustomObject]$result
