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
    [string]$ConfigPath,
    [int]$Experiment = 0
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$solutionPath = Join-Path $repoRoot $config.Api.SolutionPath

. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'experiment' -Level 'info' -Message "Building solution: $solutionPath"

$spinner = Start-Spinner -Message 'Building solution'
try {
    $buildOutput = dotnet build $solutionPath --configuration Release 2>&1
    $buildExitCode = $LASTEXITCODE
}
finally {
    $buildMsg = if ($buildExitCode -eq 0) { 'Build succeeded' } else { "Build failed (exit code $buildExitCode)" }
    Stop-Spinner -Spinner $spinner -CompletionMessage $buildMsg
}

$buildOutputString = ($buildOutput | Out-String)

# Save build log to experiment directory
if ($Experiment -gt 0) {
    $logDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
    if (-not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    $buildOutputString | Out-File -FilePath (Join-Path $logDir 'build.log') -Encoding utf8
}

$result = [ordered]@{
    Success    = ($buildExitCode -eq 0)
    ExitCode   = $buildExitCode
    Output     = $buildOutputString
    SolutionPath = $solutionPath
}

if ($result.Success) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'info' -Message 'Build succeeded'
}
else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'error' -Message "Build failed with exit code $buildExitCode" `
        -Data @{ output = $result.Output }
}

return [PSCustomObject]$result
