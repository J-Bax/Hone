<#
.SYNOPSIS
    Runs E2E tests as the regression gate.

.DESCRIPTION
    Executes 'dotnet test' on the E2E test project and parses the results.
    Returns a structured object indicating pass/fail status with test counts.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Experiment
    Current experiment number for logging.
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
$testProjectPath = Join-Path $repoRoot $config.Api.TestProjectPath
$resultsDir = Join-Path $repoRoot $config.Api.ResultsPath "experiment-$Experiment"
$trxPath = Join-Path $resultsDir "e2e-results.trx"

. (Join-Path $PSScriptRoot 'Show-Progress.ps1')

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'verify' -Level 'info' -Message "Running E2E tests: $testProjectPath" `
    -Experiment $Experiment

# Ensure results directory exists
if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

$spinner = Start-Spinner -Message 'Running E2E tests'
try {
    $testOutput = dotnet test $testProjectPath `
        --configuration Release `
        --logger "trx;LogFileName=$trxPath" `
        --verbosity normal 2>&1

    $testExitCode = $LASTEXITCODE
}
finally {
    # Parse the output for test counts
    $testOutputString = ($testOutput | Out-String)
    $totalMatch = $testOutputString -match 'Total tests:\s*(\d+)'
    $passedMatch = $testOutputString -match 'Passed:\s*(\d+)'
    $failedMatch = $testOutputString -match 'Failed:\s*(\d+)'

    $totalTests  = if ($totalMatch) { [int]$Matches[1] } else { 0 }
    $passedTests = if ($passedMatch) { [int]$Matches[1] } else { 0 }
    $failedTests = if ($failedMatch) { [int]$Matches[1] } else { 0 }

    $testMsg = if ($testExitCode -eq 0) { "$passedTests/$totalTests tests passed" } else { "$failedTests/$totalTests tests FAILED" }
    Stop-Spinner -Spinner $spinner -CompletionMessage $testMsg
}

$result = [ordered]@{
    Success     = ($testExitCode -eq 0)
    ExitCode    = $testExitCode
    TotalTests  = $totalTests
    PassedTests = $passedTests
    FailedTests = $failedTests
    TrxPath     = $trxPath
    Output      = $testOutputString
}

# Save test log to experiment directory
$testOutputString | Out-File -FilePath (Join-Path $resultsDir 'e2e-tests.log') -Encoding utf8

$logData = @{
    total  = $result.TotalTests
    passed = $result.PassedTests
    failed = $result.FailedTests
}

if ($result.Success) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'info' `
        -Message "E2E tests passed ($($result.PassedTests)/$($result.TotalTests))" `
        -Experiment $Experiment -Data $logData
}
else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'error' `
        -Message "E2E tests FAILED ($($result.FailedTests) failures out of $($result.TotalTests))" `
        -Experiment $Experiment -Data $logData
}

return [PSCustomObject]$result
