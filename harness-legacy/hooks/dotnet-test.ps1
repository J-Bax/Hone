<#
.SYNOPSIS
    Shared hook: runs .NET E2E tests as the regression gate.
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

$testProjectPath = Join-Path $TargetPath $Config.Api.TestProjectPath
$resultsDir = Join-Path -Path $TargetPath -ChildPath $Config.Api.ResultsPath "experiment-$Experiment"
$trxPath = Join-Path $resultsDir 'e2e-results.trx'

if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

$testOutput = dotnet test $testProjectPath `
    --configuration Release `
    --logger "trx;LogFileName=$trxPath" `
    --verbosity normal 2>&1

$testExitCode = $LASTEXITCODE
$stopwatch.Stop()

$testOutputString = ($testOutput | Out-String)
$totalMatch = [regex]::Match($testOutputString, 'Total tests:\s*(\d+)')
$passedMatch = [regex]::Match($testOutputString, 'Passed:\s*(\d+)')
$failedMatch = [regex]::Match($testOutputString, 'Failed:\s*(\d+)')

$totalTests = if ($totalMatch.Success) { [int]$totalMatch.Groups[1].Value } else { 0 }
$passedTests = if ($passedMatch.Success) { [int]$passedMatch.Groups[1].Value } else { 0 }
$failedTests = if ($failedMatch.Success) { [int]$failedMatch.Groups[1].Value } else { 0 }

$testLogPath = Join-Path $resultsDir 'e2e-tests.log'
$testOutputString | Out-File -FilePath $testLogPath -Encoding utf8

return [PSCustomObject]@{
    Success = ($testExitCode -eq 0)
    Message = if ($testExitCode -eq 0) { "$passedTests/$totalTests tests passed" } else { "$failedTests/$totalTests tests FAILED" }
    Duration = $stopwatch.Elapsed
    Artifacts = @($trxPath, $testLogPath)
    ExitCode = $testExitCode
    TotalTests = $totalTests
    PassedTests = $passedTests
    FailedTests = $failedTests
    Output = $testOutputString
}
