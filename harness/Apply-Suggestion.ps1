<#
.SYNOPSIS
    Applies an optimization suggestion on a new git branch.

.DESCRIPTION
    Creates a new git branch for the current iteration, applies the suggested
    code change, and commits it. This provides easy rollback via git.

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

& (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
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

    # Stage and commit (paths relative to submodule root)
    git add $submoduleRelPath 2>&1 | Out-Null
    git commit -m "autotune(iteration-$Iteration): $Description" 2>&1 | Out-Null

    $result = [ordered]@{
        Success     = $true
        BranchName  = $branchName
        FilePath    = $FilePath
        Description = $Description
    }

    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
        -Phase 'fix' -Level 'info' `
        -Message "Fix committed on branch: $branchName" `
        -Iteration $Iteration
}
catch {
    $result = [ordered]@{
        Success     = $false
        BranchName  = $branchName
        FilePath    = $FilePath
        Description = "Error: $_"
    }

    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
        -Phase 'fix' -Level 'error' `
        -Message "Failed to apply fix: $_" `
        -Iteration $Iteration
}
finally {
    Pop-Location
}

return [PSCustomObject]$result
