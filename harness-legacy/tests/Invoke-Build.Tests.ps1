BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Invoke-Build.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $originalLogPath = $env:HONE_LOG_PATH
    $null = $harnessRoot, $scriptPath, $configPath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Invoke-Build target-aware behavior' {
    It 'uses target-relative solution and results paths' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'build-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'hone.jsonl'

        Mock dotnet {
            $global:LASTEXITCODE = 0
            'Build succeeded'
        }

        $result = & $scriptPath -ConfigPath $configPath -TargetDir $targetDir -Experiment 7

        $result.Success | Should -BeTrue
        $result.SolutionPath | Should -Be (Join-Path -Path $targetDir -ChildPath 'MockApi.sln')
        Test-Path (Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'experiment-7\build.log') | Should -BeTrue
        Should -Invoke dotnet -Times 1
    }

    It 'writes an additional per-attempt build log when requested' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'build-attempt-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'
        $attemptLogPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-3\iterations\attempt-2\build.log'

        Mock dotnet {
            $global:LASTEXITCODE = 0
            'Build succeeded with attempt log'
        }

        $result = & $scriptPath -ConfigPath $configPath -TargetDir $targetDir -Experiment 3 -Attempt 2 -AdditionalLogPath $attemptLogPath

        $result.Success | Should -BeTrue
        Test-Path -Path $attemptLogPath | Should -BeTrue
    }
}
