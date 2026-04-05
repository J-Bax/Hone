BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Invoke-AnalysisAgent.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $mockResponsePath = Join-Path -Path $harnessRoot -ChildPath 'test-fixtures\mock-analysis-response.json'
    $originalLogPath = $env:HONE_LOG_PATH
    $null = $harnessRoot, $scriptPath, $configPath, $mockResponsePath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Invoke-AnalysisAgent target-aware behavior' {
    It 'writes prompt and response artifacts under the target results path' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'analysis-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $controllersDir = Join-Path -Path $targetDir -ChildPath 'MockApi\Controllers'
        New-Item -ItemType Directory -Path $controllersDir -Force | Out-Null

        @'
namespace MockApi.Controllers;
public class ProductsController {}
'@ | Set-Content -Path (Join-Path -Path $controllersDir -ChildPath 'ProductsController.cs') -Encoding ascii

        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $currentMetrics = [PSCustomObject]@{
            HttpReqDuration = [PSCustomObject]@{ P95 = 105.2 }
            HttpReqs = [PSCustomObject]@{ Rate = 120.6 }
            HttpReqFailed = [PSCustomObject]@{ Rate = 0.01 }
        }
        $baselineMetrics = [PSCustomObject]@{
            HttpReqDuration = [PSCustomObject]@{ P95 = 121.4 }
            HttpReqs = [PSCustomObject]@{ Rate = 98.5 }
            HttpReqFailed = [PSCustomObject]@{ Rate = 0.02 }
        }

        $result = & $scriptPath `
            -CurrentMetrics $currentMetrics `
            -BaselineMetrics $baselineMetrics `
            -Experiment 3 `
            -ConfigPath $configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API' `
            -MockResponsePath $mockResponsePath

        $expectedDir = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-3'
        $expectedPromptPath = Join-Path -Path $expectedDir -ChildPath 'analysis-prompt.md'
        $expectedResponsePath = Join-Path -Path $expectedDir -ChildPath 'analysis-response.json'

        $result.Success | Should -BeTrue
        @($result.Opportunities).Count | Should -Be 1
        $result.FilePath | Should -Be 'SampleApi/Controllers/ProductsController.cs'
        $result.PromptPath | Should -Be $expectedPromptPath
        $result.ResponsePath | Should -Be $expectedResponsePath
        Test-Path $expectedPromptPath | Should -BeTrue
        Test-Path $expectedResponsePath | Should -BeTrue
        Test-Path (Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl') | Should -BeTrue

        $savedPrompt = Get-Content -Path $expectedPromptPath -Raw
        $savedPrompt | Should -Match 'relative to the Mock API root'
        $savedPrompt | Should -Match 'MockApi\\Controllers\\ProductsController.cs'
    }
}
