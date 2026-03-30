BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $script:harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $script:loopScript = Join-Path -Path $script:harnessRoot -ChildPath 'Invoke-HoneLoop.ps1'
    $script:configPath = Join-Path -Path $script:harnessRoot -ChildPath 'config.psd1'
    $script:engineConfig = Import-PowerShellDataFile -Path $script:configPath
    $script:branchPrefix = $script:engineConfig.Loop.BranchPrefix
    $script:originalLogPath = $env:HONE_LOG_PATH
    $script:originalFixtureTarget = $env:HONE_HARNESS_TEST_TARGET_DIR

    function Set-LoopTestOverride {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)]
            [string]$TargetDir
        )

        if ($PSCmdlet.ShouldProcess($TargetDir, 'Set loop test override configuration')) {
            $targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone\config.psd1'
            $configContent = Get-Content -Path $targetConfigPath -Raw
            $configContent = $configContent -replace "\r?\n\}\s*$", @"

    Diagnostics = @{
        Enabled = `$false
    }

    Loop = @{
        StackedDiffs = `$true
        WaitForMerge = `$false
    }
}
"@

            Set-Content -Path $targetConfigPath -Value $configContent -Encoding ascii
        }
    }
}

AfterAll {
    $env:HONE_LOG_PATH = $script:originalLogPath
    $env:HONE_HARNESS_TEST_TARGET_DIR = $script:originalFixtureTarget
}

Describe 'Invoke-HoneLoop stacked-diffs branch ancestry' {
    It 'bases later successful experiments on the last successful branch and preserves rejected artifacts' {
        $targetDir = Copy-HoneTargetFixture -Name 'stacked-diffs' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'loop-stacked-diffs')
        Set-LoopTestOverride -TargetDir $targetDir
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'results\hone.jsonl'

        $result = & $script:loopScript -TargetPath $targetDir -MaxExperiments 3

        $branch1 = "$($script:branchPrefix)-1"
        $branch2 = "$($script:branchPrefix)-2"
        $branch3 = "$($script:branchPrefix)-3"

        $result.Experiments | Should -Be 3
        $result.SuccessCount | Should -Be 2
        $result.BestExperiment | Should -Be 3
        $result.FullBranchChain | Should -Be @('main', $branch1, $branch2, $branch3)
        $result.FailedExperiments | Should -Be @(2)
        $result.PrChain | Should -Contain 5301
        $result.PrChain | Should -Contain 5300
        $result.PrChain | Should -Contain 5303

        $runMetadataPath = Join-Path -Path $targetDir -ChildPath 'results\run-metadata.json'
        $runMetadata = Get-Content -Path $runMetadataPath -Raw | ConvertFrom-Json
        $experiment1 = $runMetadata.Experiments | Where-Object { $_.Experiment -eq 1 }
        $experiment2 = $runMetadata.Experiments | Where-Object { $_.Experiment -eq 2 }
        $experiment3 = $runMetadata.Experiments | Where-Object { $_.Experiment -eq 3 }

        $experiment1.BranchName | Should -Be $branch1
        $experiment1.BaseBranch | Should -Be 'main'
        $experiment1.PrNumber | Should -Be 5301
        $experiment2.Outcome | Should -Be 'build_failure'
        $experiment2.BranchName | Should -Be $branch2
        $experiment2.BaseBranch | Should -Be $branch1
        $experiment2.PrNumber | Should -Be 5300
        $experiment3.Outcome | Should -Be 'improved'
        $experiment3.BranchName | Should -Be $branch3
        $experiment3.BaseBranch | Should -Be $branch1
        $experiment3.PrNumber | Should -Be 5303
        $runMetadata.PrChain | Should -Be @(5301, 5300, 5303)
        $runMetadata.FullBranchChain | Should -Be @('main', $branch1, $branch2, $branch3)

        Test-Path (Join-Path -Path $targetDir -ChildPath 'results\experiment-2\build.log') | Should -BeTrue

    }
}
