<#
.SYNOPSIS
    Runs E2E tests as the regression gate.

.DESCRIPTION
    Executes 'dotnet test' on the E2E test project and parses the results.
    Returns a structured object indicating pass/fail status with test counts.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Iteration
    Current iteration number for logging.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [int]$Iteration = 0
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath
$testProjectPath = Join-Path $repoRoot $config.Api.TestProjectPath
$resultsDir = Join-Path $repoRoot $config.Api.ResultsPath "iteration-$Iteration"
$trxPath = Join-Path $resultsDir "e2e-results.trx"

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'verify' -Level 'info' -Message "Running E2E tests: $testProjectPath" `
    -Iteration $Iteration

# Ensure results directory exists
if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

$testOutput = dotnet test $testProjectPath `
    --configuration Release `
    --logger "trx;LogFileName=$trxPath" `
    --verbosity normal 2>&1

$testExitCode = $LASTEXITCODE

# Parse the output for test counts
$totalMatch = ($testOutput | Out-String) -match 'Total tests:\s*(\d+)'
$passedMatch = ($testOutput | Out-String) -match 'Passed:\s*(\d+)'
$failedMatch = ($testOutput | Out-String) -match 'Failed:\s*(\d+)'

$result = [ordered]@{
    Success     = ($testExitCode -eq 0)
    ExitCode    = $testExitCode
    TotalTests  = if ($totalMatch) { [int]$Matches[1] } else { 0 }
    PassedTests = if ($passedMatch) { [int]$Matches[1] } else { 0 }
    FailedTests = if ($failedMatch) { [int]$Matches[1] } else { 0 }
    TrxPath     = $trxPath
    Output      = ($testOutput | Out-String)
}

$logData = @{
    total  = $result.TotalTests
    passed = $result.PassedTests
    failed = $result.FailedTests
}

if ($result.Success) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'info' `
        -Message "E2E tests passed ($($result.PassedTests)/$($result.TotalTests))" `
        -Iteration $Iteration -Data $logData
}
else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'error' `
        -Message "E2E tests FAILED ($($result.FailedTests) failures out of $($result.TotalTests))" `
        -Iteration $Iteration -Data $logData
}

return [PSCustomObject]$result
