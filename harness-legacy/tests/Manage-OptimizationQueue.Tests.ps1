BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Manage-OptimizationQueue.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $originalLogPath = $env:HONE_LOG_PATH
    $null = $harnessRoot, $scriptPath, $configPath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Manage-OptimizationQueue target-aware behavior' {
    It 'initializes queue files under the target metadata path' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'queue-init') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'hone.jsonl'

        $opportunities = @(
            [PSCustomObject]@{
                filePath = 'MockApi\File.cs'
                title = 'Use better query'
                explanation = 'Use better query'
                scope = 'narrow'
                rootCause = 'Root cause details'
            },
            [PSCustomObject]@{
                filePath = 'MockApi\Arch.cs'
                title = 'Bigger refactor'
                explanation = 'Bigger refactor'
                scope = 'architecture'
            }
        )

        $result = & $scriptPath -Action 'Init' -Opportunities $opportunities -Experiment 3 -ConfigPath $configPath -TargetDir $targetDir

        $result.Success | Should -BeTrue
        $result.Count | Should -Be 2

        $queueJsonPath = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata' -AdditionalChildPath 'experiment-queue.json'
        $queueMarkdownPath = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata' -AdditionalChildPath 'experiment-queue.md'

        Test-Path $queueJsonPath | Should -BeTrue
        Test-Path $queueMarkdownPath | Should -BeTrue
        Test-Path "$queueJsonPath.tmp" | Should -BeFalse

        $queue = Get-Content -Path $queueJsonPath -Raw | ConvertFrom-Json
        $queue.generatedByExperiment | Should -Be 3
        @($queue.items).Count | Should -Be 2
        Test-Path $queue.items[0].rootCausePath | Should -BeTrue
    }

    It 'returns actionable items only and tracks completion in target metadata' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'queue-next') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'hone.jsonl'

        $opportunities = @(
            [PSCustomObject]@{
                filePath = 'MockApi\Architecture.cs'
                title = 'Architecture change'
                explanation = 'Architecture change'
                scope = 'architecture'
            },
            [PSCustomObject]@{
                filePath = 'MockApi\HotPath.cs'
                title = 'Hot path'
                explanation = 'Hot path'
                scope = 'narrow'
            }
        )

        & $scriptPath -Action 'Init' -Opportunities $opportunities -Experiment 4 -ConfigPath $configPath -TargetDir $targetDir | Out-Null

        (& $scriptPath -Action 'HasActionable' -ConfigPath $configPath -TargetDir $targetDir) | Should -BeTrue

        $next = & $scriptPath -Action 'GetNext' -Experiment 4 -ConfigPath $configPath -TargetDir $targetDir
        $next.filePath | Should -Be 'MockApi\HotPath.cs'
        $next.status | Should -Be 'in_progress'

        $queueJsonPath = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata' -AdditionalChildPath 'experiment-queue.json'
        $queue = Get-Content -Path $queueJsonPath -Raw | ConvertFrom-Json
        ($queue.items | Where-Object { $_.filePath -eq 'MockApi\HotPath.cs' }).status | Should -Be 'in_progress'

        & $scriptPath -Action 'MarkDone' -ItemId $next.id -Experiment 4 -Outcome 'improved' -ConfigPath $configPath -TargetDir $targetDir | Out-Null

        (& $scriptPath -Action 'HasActionable' -ConfigPath $configPath -TargetDir $targetDir) | Should -BeFalse

        $queue = Get-Content -Path $queueJsonPath -Raw | ConvertFrom-Json
        $doneItem = $queue.items | Where-Object { $_.id -eq $next.id }
        $doneItem.status | Should -Be 'done'
        $doneItem.outcome | Should -Be 'improved'
        $doneItem.triedByExperiment | Should -Be 4
    }

    It 'keeps the last committed queue intact when the atomic rename is interrupted' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'queue-atomic-failure') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'hone.jsonl'

        $opportunities = @(
            [PSCustomObject]@{
                filePath = 'MockApi\HotPath.cs'
                title = 'Hot path'
                explanation = 'Hot path'
                scope = 'narrow'
            }
        )

        & $scriptPath -Action 'Init' -Opportunities $opportunities -Experiment 5 -ConfigPath $configPath -TargetDir $targetDir | Out-Null

        $queueJsonPath = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata' -AdditionalChildPath 'experiment-queue.json'
        $originalContent = Get-Content -Path $queueJsonPath -Raw

        Mock Move-Item {
            throw 'Simulated rename failure'
        }

        {
            & $scriptPath -Action 'MarkDone' -ItemId 1 -Experiment 5 -Outcome 'stale' -ConfigPath $configPath -TargetDir $targetDir
        } | Should -Throw '*Simulated rename failure*'

        (Get-Content -Path $queueJsonPath -Raw) | Should -Be $originalContent
        Test-Path "$queueJsonPath.tmp" | Should -BeTrue
    }

    It 'clears an interrupted temp queue file on the next successful mutation' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'queue-temp-recovery') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'hone.jsonl'

        $opportunities = @(
            [PSCustomObject]@{
                filePath = 'MockApi\HotPath.cs'
                title = 'Hot path'
                explanation = 'Hot path'
                scope = 'narrow'
            }
        )

        & $scriptPath -Action 'Init' -Opportunities $opportunities -Experiment 6 -ConfigPath $configPath -TargetDir $targetDir | Out-Null

        $queueJsonPath = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata' -AdditionalChildPath 'experiment-queue.json'
        'partial queue write' | Set-Content -Path "$queueJsonPath.tmp" -Encoding ascii

        & $scriptPath -Action 'MarkDone' -ItemId 1 -Experiment 6 -Outcome 'stale' -ConfigPath $configPath -TargetDir $targetDir | Out-Null

        Test-Path "$queueJsonPath.tmp" | Should -BeFalse

        $queue = Get-Content -Path $queueJsonPath -Raw | ConvertFrom-Json
        $queue.items[0].status | Should -Be 'done'
        $queue.items[0].outcome | Should -Be 'stale'
        $queue.items[0].triedByExperiment | Should -Be 6
    }
}
