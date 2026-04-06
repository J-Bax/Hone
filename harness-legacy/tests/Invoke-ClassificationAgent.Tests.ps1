BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Invoke-ClassificationAgent.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $mockResponsePath = Join-Path -Path $harnessRoot -ChildPath 'test-fixtures\mock-classification-response.json'
    $originalLogPath = $env:HONE_LOG_PATH
    $null = $harnessRoot, $scriptPath, $configPath, $mockResponsePath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Invoke-ClassificationAgent target-aware behavior' {
    It 'writes classification responses under the target results path' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'classification-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $controllersDir = Join-Path -Path $targetDir -ChildPath 'MockApi\Controllers'
        New-Item -ItemType Directory -Path $controllersDir -Force | Out-Null

        @'
namespace MockApi.Controllers;
public class ProductsController {}
'@ | Set-Content -Path (Join-Path -Path $controllersDir -ChildPath 'ProductsController.cs') -Encoding ascii

        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $result = & $scriptPath `
            -FilePath 'MockApi\Controllers\ProductsController.cs' `
            -Explanation 'Use eager loading to avoid repeated queries' `
            -Experiment 4 `
            -ConfigPath $configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API' `
            -MockResponsePath $mockResponsePath

        $expectedResponsePath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-4\classification-response.json'

        $result.Success | Should -BeTrue
        $result.Scope | Should -Be 'narrow'
        $result.Reasoning | Should -Match 'single controller file'
        Test-Path $expectedResponsePath | Should -BeTrue
        Test-Path (Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl') | Should -BeTrue
    }
}
