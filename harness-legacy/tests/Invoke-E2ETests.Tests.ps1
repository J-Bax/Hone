BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Invoke-E2ETests.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $originalLogPath = $env:HONE_LOG_PATH
    $null = $harnessRoot, $scriptPath, $configPath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Invoke-E2ETests target-aware behavior' {
    It 'uses target-relative test and results paths and parses counts' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'tests-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'hone.jsonl'

        Mock dotnet {
            $global:LASTEXITCODE = 0
            @'
Test run for MockApi
Total tests: 3
Passed: 3
Failed: 0
'@
        }

        $result = & $scriptPath -ConfigPath $configPath -TargetDir $targetDir -Experiment 5

        $result.Success | Should -BeTrue
        $result.TotalTests | Should -Be 3
        $result.PassedTests | Should -Be 3
        $result.FailedTests | Should -Be 0
        Test-Path (Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'experiment-5\e2e-tests.log') | Should -BeTrue
        Should -Invoke dotnet -Times 1
    }

    It 'writes additional per-attempt test artifacts when requested' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'tests-attempt-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'
        $attemptLogPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-6\iterations\attempt-2\e2e-tests.log'
        $attemptTrxPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-6\iterations\attempt-2\e2e-results.trx'

        Mock dotnet {
            $global:LASTEXITCODE = 0
            @'
Test run for MockApi
Total tests: 2
Passed: 2
Failed: 0
'@
        }

        $result = & $scriptPath -ConfigPath $configPath -TargetDir $targetDir -Experiment 6 -Attempt 2 -AdditionalLogPath $attemptLogPath -AdditionalTrxPath $attemptTrxPath

        $result.Success | Should -BeTrue
        Test-Path -Path $attemptLogPath | Should -BeTrue
    }
}
