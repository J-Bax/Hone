BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $script:harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $script:failureScript = Join-Path -Path $script:harnessRoot -ChildPath 'Invoke-FailureHandler.ps1'
    $script:applyScript = Join-Path -Path $script:harnessRoot -ChildPath 'Apply-Suggestion.ps1'
    $script:queueScript = Join-Path -Path $script:harnessRoot -ChildPath 'Manage-OptimizationQueue.ps1'
    $script:configPath = Join-Path -Path $script:harnessRoot -ChildPath 'config.psd1'
    $script:originalLogPath = $env:HONE_LOG_PATH
}

AfterAll {
    $env:HONE_LOG_PATH = $script:originalLogPath
}

Describe 'Invoke-FailureHandler rejected experiment orchestration' {
    It 'preserves rejected artifacts and records queue and metadata outcomes' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'failure-handler-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $targetFile = 'MockApi\Controllers\ProductsController.cs'
        $targetFilePath = Join-Path -Path $targetDir -ChildPath $targetFile
        $originalContent = (Get-Content -Path $targetFilePath -Raw).Trim()

        $opportunities = @(
            [PSCustomObject]@{
                filePath = $targetFile
                title = 'Optimize controller query'
                explanation = 'Optimize controller query'
                scope = 'narrow'
            }
        )

        & $script:queueScript -Action 'Init' -Opportunities $opportunities -Experiment 1 -ConfigPath $script:configPath -TargetDir $targetDir | Out-Null
        $queueItem = & $script:queueScript -Action 'GetNext' -Experiment 2 -ConfigPath $script:configPath -TargetDir $targetDir

        $newContent = @'
namespace MockApi.Controllers;

public class ProductsController
{
    public string Get() => "optimized";
}
'@

        $applyResult = & $script:applyScript `
            -FilePath $targetFile `
            -NewContent $newContent `
            -Description 'Optimize controller query' `
            -Experiment 2 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir

        $applyResult.Success | Should -BeTrue

        $experimentDir = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-2'
        New-Item -ItemType Directory -Path $experimentDir -Force | Out-Null

        '# Analysis prompt' | Set-Content -Path (Join-Path -Path $experimentDir -ChildPath 'analysis-prompt.md') -Encoding utf8
        '{}' | Set-Content -Path (Join-Path -Path $experimentDir -ChildPath 'analysis-response.json') -Encoding utf8
        'build failed' | Set-Content -Path (Join-Path -Path $experimentDir -ChildPath 'build.log') -Encoding utf8
        '{}' | Set-Content -Path (Join-Path -Path $targetDir -ChildPath 'artifacts\run-metadata.json') -Encoding utf8

        $result = & $script:failureScript `
            -BranchName 'hone/experiment-2' `
            -FilePath $targetFile `
            -Experiment 2 `
            -Outcome 'stale' `
            -RevertDescription 'Optimize controller query' `
            -MetadataSummary 'Stale optimization attempt' `
            -MetadataFilePath $targetFile `
            -QueueItemId $queueItem.id `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir

        $result.Success | Should -BeTrue
        (Get-Content -Path $targetFilePath -Raw).Trim() | Should -Be $originalContent

        $queueJsonPath = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata\experiment-queue.json'
        $queue = Get-Content -Path $queueJsonPath -Raw | ConvertFrom-Json
        $updatedItem = $queue.items | Where-Object { $_.id -eq $queueItem.id }
        $updatedItem.status | Should -Be 'done'
        $updatedItem.outcome | Should -Be 'stale'
        $updatedItem.triedByExperiment | Should -Be 2

        $logPath = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata\experiment-log.md'
        $logContent = Get-Content -Path $logPath -Raw
        $logContent | Should -Match 'Stale optimization attempt'
        $logContent | Should -Match 'MockApi\\Controllers\\ProductsController.cs'
        $logContent | Should -Match 'stale'

        Push-Location $targetDir
        try {
            $currentBranch = (& git rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
            $stagedFiles = (& git show --name-only --pretty=format: HEAD 2>$null | Out-String)
        } finally {
            Pop-Location
        }

        $currentBranch | Should -Be 'hone/experiment-2'
        $stagedFiles | Should -Match 'artifacts/experiment-2/analysis-prompt.md'
        $stagedFiles | Should -Match 'artifacts/experiment-2/analysis-response.json'
        $stagedFiles | Should -Match 'artifacts/experiment-2/build.log'
        $stagedFiles | Should -Match 'artifacts/run-metadata.json'
    }
}
