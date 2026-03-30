<#
.SYNOPSIS
    Shared hook: builds a .NET solution.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$null = $BaseUrl

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$solutionPath = Join-Path $TargetPath $Config.Api.SolutionPath
$buildOutput = dotnet build $solutionPath --configuration Release 2>&1
$buildExitCode = $LASTEXITCODE

$stopwatch.Stop()

$buildOutputString = ($buildOutput | Out-String)

if ($Experiment) {
    $logDir = Join-Path -Path $TargetPath -ChildPath $Config.Api.ResultsPath "experiment-$Experiment"
    if (-not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    $buildOutputString | Out-File -FilePath (Join-Path $logDir 'build.log') -Encoding utf8
}

return [PSCustomObject]@{
    Success = ($buildExitCode -eq 0)
    Message = if ($buildExitCode -eq 0) { 'Build succeeded' } else { "Build failed (exit code $buildExitCode)" }
    Duration = $stopwatch.Elapsed
    Artifacts = @(if ($Experiment) { Join-Path $logDir 'build.log' })
    ExitCode = $buildExitCode
    Output = $buildOutputString
}
