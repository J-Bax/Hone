<#
.SYNOPSIS
    Builds the sample API project.

.DESCRIPTION
    Runs 'dotnet build' on the target solution. Returns a structured result
    object indicating success or failure.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$solutionPath = Join-Path $repoRoot $config.Api.SolutionPath

& (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
    -Phase 'build' -Level 'info' -Message "Building solution: $solutionPath"

$buildOutput = dotnet build $solutionPath --configuration Release 2>&1
$buildExitCode = $LASTEXITCODE

$result = [ordered]@{
    Success    = ($buildExitCode -eq 0)
    ExitCode   = $buildExitCode
    Output     = ($buildOutput | Out-String)
    SolutionPath = $solutionPath
}

if ($result.Success) {
    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
        -Phase 'build' -Level 'info' -Message 'Build succeeded'
}
else {
    & (Join-Path $PSScriptRoot 'Write-AutotuneLog.ps1') `
        -Phase 'build' -Level 'error' -Message "Build failed with exit code $buildExitCode" `
        -Data @{ output = $result.Output }
}

return [PSCustomObject]$result
