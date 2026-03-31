<#
.SYNOPSIS
    Runs E2E tests as the regression gate.

.DESCRIPTION
    Executes 'dotnet test' on the E2E test project and parses the results.
    Returns a structured object indicating pass/fail status with test counts.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER TargetDir
    Root directory of the target project. Config paths are resolved relative
    to this directory when provided.

.PARAMETER Experiment
    Current experiment number for logging.

.PARAMETER Attempt
    Iteration number for iterative fixer flows.

.PARAMETER AdditionalLogPath
    Optional second log file path used for per-attempt artifacts.

.PARAMETER AdditionalTrxPath
    Optional second TRX file path used for per-attempt artifacts.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$TargetDir,
    [int]$Experiment = 0,
    [int]$Attempt = -1,
    [string]$AdditionalLogPath,
    [string]$AdditionalTrxPath
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
$testProjectPath = Join-Path $pathBase $config.Api.TestProjectPath
$resultsDir = Join-Path -Path $pathBase -ChildPath $config.Api.ResultsPath "experiment-$Experiment"
$trxPath = Join-Path $resultsDir "e2e-results.trx"
$fixture = Get-HarnessTestingFixture -Config $config -TargetDir $TargetDir
$fixtureTests = if ($fixture) {
    Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Tests') -Experiment $Experiment -Attempt $Attempt
} else {
    $null
}

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
    if ($fixtureTests) {
        $testExitCode = if ($fixtureTests.ContainsKey('ExitCode')) { [int]$fixtureTests.ExitCode } else { 0 }
        $testOutput = if ($fixtureTests.ContainsKey('Output')) {
            $fixtureTests.Output
        } elseif ($fixtureTests.ContainsKey('Passed') -and $fixtureTests.ContainsKey('Total')) {
            @"
Test run for fixture target
Total tests: $($fixtureTests.Total)
Passed: $($fixtureTests.Passed)
Failed: $(if ($fixtureTests.ContainsKey('Failed')) { $fixtureTests.Failed } else { 0 })
"@
        } else {
            'Test run for fixture target'
        }

        if ($fixtureTests.ContainsKey('TrxContent')) {
            $fixtureTests.TrxContent | Out-File -FilePath $trxPath -Encoding utf8
            if ($AdditionalTrxPath) {
                $additionalTrxDir = Split-Path -Path $AdditionalTrxPath -Parent
                if ($additionalTrxDir -and -not (Test-Path -Path $additionalTrxDir)) {
                    New-Item -ItemType Directory -Path $additionalTrxDir -Force | Out-Null
                }
                $fixtureTests.TrxContent | Out-File -FilePath $AdditionalTrxPath -Encoding utf8
            }
        }
    } else {
        $testOutput = dotnet test $testProjectPath `
            --configuration Release `
            --logger "trx;LogFileName=$trxPath" `
            --verbosity normal 2>&1

        $testExitCode = $LASTEXITCODE
    }
} finally {
    # Parse the output for test counts
    $testOutputString = ($testOutput | Out-String)
    $totalMatch = [regex]::Match($testOutputString, 'Total tests:\s*(\d+)')
    $passedMatch = [regex]::Match($testOutputString, 'Passed:\s*(\d+)')
    $failedMatch = [regex]::Match($testOutputString, 'Failed:\s*(\d+)')

    $totalTests = if ($totalMatch.Success) { [int]$totalMatch.Groups[1].Value } else { 0 }
    $passedTests = if ($passedMatch.Success) { [int]$passedMatch.Groups[1].Value } else { 0 }
    $failedTests = if ($failedMatch.Success) { [int]$failedMatch.Groups[1].Value } else { 0 }

    $testMsg = if ($testExitCode -eq 0) { "$passedTests/$totalTests tests passed" } else { "$failedTests/$totalTests tests FAILED" }
    Stop-Spinner -Spinner $spinner -CompletionMessage $testMsg
}

$result = [ordered]@{
    Success = ($testExitCode -eq 0)
    ExitCode = $testExitCode
    TotalTests = $totalTests
    PassedTests = $passedTests
    FailedTests = $failedTests
    TrxPath = $trxPath
    Output = $testOutputString
}

# Save test log to experiment directory
$primaryLogPath = Join-Path $resultsDir 'e2e-tests.log'
$testOutputString | Out-File -FilePath $primaryLogPath -Encoding utf8

if ($AdditionalLogPath) {
    $additionalLogDir = Split-Path -Path $AdditionalLogPath -Parent
    if ($additionalLogDir -and -not (Test-Path -Path $additionalLogDir)) {
        New-Item -ItemType Directory -Path $additionalLogDir -Force | Out-Null
    }

    if (-not [string]::Equals($primaryLogPath, $AdditionalLogPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $testOutputString | Out-File -FilePath $AdditionalLogPath -Encoding utf8
    }
}

if ($AdditionalTrxPath -and (Test-Path -Path $trxPath) -and
    -not [string]::Equals($trxPath, $AdditionalTrxPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    $additionalTrxDir = Split-Path -Path $AdditionalTrxPath -Parent
    if ($additionalTrxDir -and -not (Test-Path -Path $additionalTrxDir)) {
        New-Item -ItemType Directory -Path $additionalTrxDir -Force | Out-Null
    }
    Copy-Item -Path $trxPath -Destination $AdditionalTrxPath -Force
}

$logData = @{
    total = $result.TotalTests
    passed = $result.PassedTests
    failed = $result.FailedTests
}

if ($result.Success) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'info' `
        -Message "E2E tests passed ($($result.PassedTests)/$($result.TotalTests))" `
        -Experiment $Experiment -Data $logData
} else {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'verify' -Level 'error' `
        -Message "E2E tests FAILED ($($result.FailedTests) failures out of $($result.TotalTests))" `
        -Experiment $Experiment -Data $logData
}

return [PSCustomObject]$result
