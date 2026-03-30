BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Invoke-FixAgent.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $mockResponsePath = Join-Path -Path $harnessRoot -ChildPath 'test-fixtures\mock-fix-response.md'
    $originalLogPath = $env:HONE_LOG_PATH
    $null = $harnessRoot, $scriptPath, $configPath, $mockResponsePath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Invoke-FixAgent target-aware behavior' {
    It 'extracts code blocks and writes fix responses under the target results path' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'fix-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
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
            -RootCauseDocument "## Evidence`n- Controller issues repeated database calls" `
            -Experiment 5 `
            -ConfigPath $configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API' `
            -MockResponsePath $mockResponsePath

        $expectedResponsePath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-5\fix-response.md'

        $result.Success | Should -BeTrue
        $result.CodeBlock | Should -Match 'public class ProductsController'
        $result.CodeBlock | Should -Not -Match '^```'
        Test-Path $expectedResponsePath | Should -BeTrue
        Test-Path (Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl') | Should -BeTrue
    }
}
