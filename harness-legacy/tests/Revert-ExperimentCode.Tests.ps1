BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $script:harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $script:revertScript = Join-Path -Path $script:harnessRoot -ChildPath 'Revert-ExperimentCode.ps1'
    $script:applyScript = Join-Path -Path $script:harnessRoot -ChildPath 'Apply-Suggestion.ps1'
    $script:configPath = Join-Path -Path $script:harnessRoot -ChildPath 'config.psd1'
    $script:originalLogPath = $env:HONE_LOG_PATH
}

AfterAll {
    $env:HONE_LOG_PATH = $script:originalLogPath
}

Describe 'Revert-ExperimentCode retry soft reset' {
    It 'removes the last fix commit without creating a revert commit' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'revert-retry-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $targetFile = 'MockApi\Controllers\ProductsController.cs'
        $targetFilePath = Join-Path -Path $targetDir -ChildPath $targetFile
        $originalContent = (Get-Content -Path $targetFilePath -Raw).Trim()

        $applyResult = & $script:applyScript `
            -FilePath $targetFile `
            -NewContent @'
namespace MockApi.Controllers;

public class ProductsController
{
    public string Get() => "optimized";
}
'@ `
            -Description 'Retryable optimization' `
            -Experiment 4 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir

        $applyResult.Success | Should -BeTrue

        Push-Location $targetDir
        try {
            $beforeCount = [int]((& git rev-list --count HEAD 2>$null | Out-String).Trim())
        } finally {
            Pop-Location
        }

        $result = & $script:revertScript `
            -BranchName 'hone/experiment-4' `
            -FilePath $targetFile `
            -Experiment 4 `
            -Outcome 'retry' `
            -Description 'Retryable optimization' `
            -ConfigPath $script:configPath `
            -SoftReset `
            -TargetDir $targetDir

        $result.Success | Should -BeTrue
        $result.SoftReset | Should -BeTrue
        (Get-Content -Path $targetFilePath -Raw).Trim() | Should -Be $originalContent

        Push-Location $targetDir
        try {
            $afterCount = [int]((& git rev-list --count HEAD 2>$null | Out-String).Trim())
            $headMessage = (& git log -1 --pretty=%s 2>$null | Out-String).Trim()
        } finally {
            Pop-Location
        }

        $afterCount | Should -Be ($beforeCount - 1)
        $headMessage | Should -Be 'fixture baseline'
    }
}
