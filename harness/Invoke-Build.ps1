<#
.SYNOPSIS
    Builds a target solution and returns a structured result.

.DESCRIPTION
    Runs `dotnet build` against the configured solution path. When `-TargetDir`
    is provided, target config is merged from `.hone\config.psd1` and all paths
    are resolved relative to the target project root.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER TargetDir
    Root directory of the target project. Config paths are resolved relative to
    this directory when provided.

.PARAMETER Experiment
    Current experiment number for optional build-log output.

.PARAMETER Attempt
    Iteration number for iterative fixer flows.

.PARAMETER AdditionalLogPath
    Optional second log file path used for per-attempt artifacts.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$TargetDir,
    [int]$Experiment = 0,
    [int]$Attempt = -1,
    [string]$AdditionalLogPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath
if ($TargetDir) {
    $targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
    if (Test-Path $targetConfigPath) {
        $targetCfg = Import-PowerShellDataFile -Path $targetConfigPath
        $config = Merge-HoneConfig -Engine $config -Target $targetCfg
    }
}

$pathBase = if ($TargetDir) { $TargetDir } else { $repoRoot }
$solutionPath = Join-Path $pathBase $config.Api.SolutionPath
$fixture = Get-HarnessTestingFixture -Config $config -TargetDir $TargetDir
$fixtureBuild = if ($fixture) {
    Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Build') -Experiment $Experiment -Attempt $Attempt
} else {
    $null
}

. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'experiment' -Level 'info' `
    -Message "Building solution: $solutionPath" `
    -Experiment $Experiment

$spinner = Start-Spinner -Message 'Building solution'
try {
    if ($fixtureBuild) {
        $buildExitCode = if ($fixtureBuild.ContainsKey('ExitCode')) { [int]$fixtureBuild.ExitCode } else { 0 }
        $buildOutput = if ($fixtureBuild.ContainsKey('Output')) { $fixtureBuild.Output } else { 'Fixture build completed' }
    } else {
        $buildOutput = dotnet build $solutionPath --configuration Release 2>&1
        $buildExitCode = $LASTEXITCODE
    }
} finally {
    $buildMsg = if ($buildExitCode -eq 0) { 'Build succeeded' } else { "Build failed (exit code $buildExitCode)" }
    Stop-Spinner -Spinner $spinner -CompletionMessage $buildMsg
}

$buildOutputString = ($buildOutput | Out-String)

if ($Experiment -gt 0) {
    $logDir = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath "experiment-$Experiment"
    if (-not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    $primaryLogPath = Join-Path $logDir 'build.log'
    $buildOutputString | Out-File -FilePath $primaryLogPath -Encoding utf8
}

if ($AdditionalLogPath) {
    $additionalLogDir = Split-Path -Path $AdditionalLogPath -Parent
    if ($additionalLogDir -and -not (Test-Path -Path $additionalLogDir)) {
        New-Item -ItemType Directory -Path $additionalLogDir -Force | Out-Null
    }

    if (-not $primaryLogPath -or
        -not [string]::Equals($primaryLogPath, $AdditionalLogPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $buildOutputString | Out-File -FilePath $AdditionalLogPath -Encoding utf8
    }
}

$result = [ordered]@{
    Success = ($buildExitCode -eq 0)
    ExitCode = $buildExitCode
    Output = $buildOutputString
    SolutionPath = $solutionPath
}

if ($result.Success) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'info' `
        -Message 'Build succeeded' `
        -Experiment $Experiment
} else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'error' `
        -Message "Build failed with exit code $buildExitCode" `
        -Experiment $Experiment `
        -Data @{ output = $result.Output }
}

return [PSCustomObject]$result
