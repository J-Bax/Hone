BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Update-OptimizationMetadata.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $originalLogPath = $env:HONE_LOG_PATH
    $null = $harnessRoot, $scriptPath, $configPath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Update-OptimizationMetadata target-aware behavior' {
    It 'writes experiment log entries under the target metadata path' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'metadata-target') -MetadataPath 'artifacts\meta' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'hone.jsonl'

        & $scriptPath -Action 'AddTried' `
            -Experiment 2 `
            -Summary 'Optimize query path' `
            -FilePath 'MockApi\Products.cs' `
            -Outcome 'improved' `
            -ConfigPath $configPath `
            -TargetDir $targetDir

        $logPath = Join-Path -Path $targetDir -ChildPath 'artifacts\meta' -AdditionalChildPath 'experiment-log.md'
        Test-Path $logPath | Should -BeTrue
        (Get-Content -Path $logPath -Raw) | Should -Match 'Optimize query path'
        (Get-Content -Path $logPath -Raw) | Should -Match 'MockApi\\Products.cs'
    }
}
