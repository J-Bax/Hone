BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $script:harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $script:applyScript = Join-Path -Path $script:harnessRoot -ChildPath 'Apply-Suggestion.ps1'
    $script:configPath = Join-Path -Path $script:harnessRoot -ChildPath 'config.psd1'
    $script:originalLogPath = $env:HONE_LOG_PATH
}

AfterAll {
    $env:HONE_LOG_PATH = $script:originalLogPath
}

Describe 'Apply-Suggestion stacked branch creation' {
    It 'resets dirty run metadata before forking from a different base branch' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'apply-suggestion-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $targetFile = 'MockApi\Controllers\ProductsController.cs'
        $targetFilePath = Join-Path -Path $targetDir -ChildPath $targetFile
        $runMetadataPath = Join-Path -Path $targetDir -ChildPath 'artifacts\run-metadata.json'

        '{"experiment":0}' | Set-Content -Path $runMetadataPath -Encoding utf8

        Push-Location $targetDir
        try {
            git add 'artifacts/run-metadata.json' 2>&1 | Out-Null
            git commit --no-gpg-sign -m 'track run metadata' 2>&1 | Out-Null
        } finally {
            Pop-Location
        }

        $firstApply = & $script:applyScript `
            -FilePath $targetFile `
            -NewContent @'
namespace MockApi.Controllers;

public class ProductsController
{
    public string Get() => "experiment-1";
}
'@ `
            -Description 'Create experiment 1 branch' `
            -Experiment 1 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir

        $firstApply.Success | Should -BeTrue

        Push-Location $targetDir
        try {
            '{"experiment":1}' | Set-Content -Path $runMetadataPath -Encoding utf8
            git add 'artifacts/run-metadata.json' 2>&1 | Out-Null
            git commit --no-gpg-sign -m 'experiment 1 metadata snapshot' 2>&1 | Out-Null

            '{"experiment":1,"dirty":true}' | Set-Content -Path $runMetadataPath -Encoding utf8
        } finally {
            Pop-Location
        }

        $secondApply = & $script:applyScript `
            -FilePath $targetFile `
            -NewContent @'
namespace MockApi.Controllers;

public class ProductsController
{
    public string Get() => "experiment-2";
}
'@ `
            -Description 'Create experiment 2 branch' `
            -Experiment 2 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir

        $secondApply.Success | Should -BeTrue
        $secondApply.BranchName | Should -Be 'hone/experiment-2'

        Push-Location $targetDir
        try {
            $currentBranch = (& git rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
            $metadataContent = Get-Content -Path $runMetadataPath -Raw | ConvertFrom-Json
            $fileContent = (Get-Content -Path $targetFilePath -Raw).Trim()
        } finally {
            Pop-Location
        }

        $currentBranch | Should -Be 'hone/experiment-2'
        $metadataContent.experiment | Should -Be 1
        $metadataContent.dirty | Should -BeTrue
        $fileContent | Should -Match 'experiment-2'
    }
}
