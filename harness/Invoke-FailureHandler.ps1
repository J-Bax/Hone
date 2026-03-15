<#
.SYNOPSIS
    Unified handler for experiment failures in stacked-diffs mode.

.DESCRIPTION
    Performs the standard rejection sequence: revert code, optionally update
    the optimization metadata (tried log), and optionally mark the queue item
    done.  Returns the revert result for the caller to use when building the
    rejected PR.

    Metadata and queue updates are performed only when their respective
    parameters are supplied.  For regression/stale outcomes where the caller
    has already updated metadata and the queue before entering the stacked-diffs
    path, pass -SkipMetadataUpdate and/or -SkipQueueMarkDone.

.PARAMETER BranchName
    The current experiment branch name.

.PARAMETER FilePath
    The file that was modified by the fix (relative to repo root, normalised).

.PARAMETER Experiment
    Current experiment number.

.PARAMETER Outcome
    Why the experiment failed: 'regressed' or 'stale'.

.PARAMETER RevertDescription
    Brief description passed to Revert-ExperimentCode -Description.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER MetadataSummary
    Summary for Update-OptimizationMetadata -Summary.
    Required unless -SkipMetadataUpdate is set.

.PARAMETER MetadataFilePath
    FilePath for Update-OptimizationMetadata -FilePath (the original
    analysis file path, before normalisation).
    Required unless -SkipMetadataUpdate is set.

.PARAMETER QueueItemId
    Queue item ID for Manage-OptimizationQueue -ItemId.
    Required unless -SkipQueueMarkDone is set.

.PARAMETER SkipMetadataUpdate
    When set, the metadata update step is skipped (caller already did it).

.PARAMETER SkipQueueMarkDone
    When set, the queue mark-done step is skipped (caller already did it).

.OUTPUTS
    PSCustomObject with: Success, RevertResult
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BranchName,
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][int]$Experiment,
    [Parameter(Mandatory)][ValidateSet('regressed', 'stale')][string]$Outcome,
    [Parameter(Mandatory)][string]$RevertDescription,
    [string]$ConfigPath,

    [string]$MetadataSummary,
    [string]$MetadataFilePath,

    [int]$QueueItemId,

    [switch]$SkipMetadataUpdate,
    [switch]$SkipQueueMarkDone
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

# 1. Revert the code change
$revertResult = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
    -BranchName $BranchName `
    -FilePath $FilePath `
    -Experiment $Experiment `
    -Outcome $Outcome `
    -Description $RevertDescription `
    -ConfigPath $ConfigPath

if (-not $revertResult.Success) {
    Write-Warning "  Revert failed: $($revertResult.Outcome)"
}

# 2. Update optimization metadata (tried log)
if (-not $SkipMetadataUpdate) {
    & (Join-Path $PSScriptRoot 'Update-OptimizationMetadata.ps1') `
        -Action 'AddTried' `
        -Experiment $Experiment `
        -Summary $MetadataSummary `
        -FilePath $MetadataFilePath `
        -Outcome $Outcome `
        -ConfigPath $ConfigPath
}

# 3. Mark queue item done
if (-not $SkipQueueMarkDone) {
    & (Join-Path $PSScriptRoot 'Manage-OptimizationQueue.ps1') `
        -Action 'MarkDone' -ItemId $QueueItemId `
        -Experiment $Experiment -Outcome $Outcome `
        -ConfigPath $ConfigPath
}

return [PSCustomObject]@{
    Success = $revertResult.Success
    RevertResult = $revertResult
}
