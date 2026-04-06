BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $script:harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    Import-Module (Join-Path -Path $script:harnessRoot -ChildPath 'HoneHelpers.psm1') -Force
    $script:configPath = Join-Path -Path $script:harnessRoot -ChildPath 'config.psd1'
    $script:engineConfig = Import-PowerShellDataFile -Path $script:configPath
    $script:validatorPath = Join-Path -Path $script:harnessRoot -ChildPath 'Test-HoneConfig.ps1'
    $script:baselineScript = Join-Path -Path $script:harnessRoot -ChildPath 'Get-PerformanceBaseline.ps1'
    $script:buildScript = Join-Path -Path $script:harnessRoot -ChildPath 'Invoke-Build.ps1'
    $script:testsScript = Join-Path -Path $script:harnessRoot -ChildPath 'Invoke-E2ETests.ps1'
    $script:scaleScript = Join-Path -Path $script:harnessRoot -ChildPath 'Invoke-ScaleTests.ps1'
    $script:diagScript = Join-Path -Path $script:harnessRoot -ChildPath 'Invoke-DiagnosticMeasurement.ps1'
    $script:compareScript = Join-Path -Path $script:harnessRoot -ChildPath 'Compare-Results.ps1'
    $script:fixtureNames = @('happy-path', 'build-failure', 'test-failure', 'regression', 'stacked-diffs')
    $script:expectedOutcomes = @{
        'happy-path' = 'improved'
        'build-failure' = 'build_failure'
        'test-failure' = 'test_failure'
        'regression' = 'regressed'
        'stacked-diffs' = 'stacked-diffs'
    }
    $script:originalLogPath = $env:HONE_LOG_PATH
    $script:originalHarnessFixtureTarget = $env:HONE_HARNESS_TEST_TARGET_DIR
}

AfterAll {
    $env:HONE_LOG_PATH = $script:originalLogPath
    $env:HONE_HARNESS_TEST_TARGET_DIR = $script:originalHarnessFixtureTarget
}

Describe 'Harness-testing checked-in target fixtures' {
    It 'stages all checked-in Phase 2 target fixtures as valid hone targets' {
        foreach ($fixtureName in $script:fixtureNames) {
            $targetDir = Copy-HoneTargetFixture -Name $fixtureName -DestinationPath (Join-Path -Path $TestDrive -ChildPath $fixtureName)

            {
                & $script:validatorPath -ConfigPath $script:configPath -TargetPath $targetDir
            } | Should -Not -Throw

            $targetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $targetDir -ChildPath '.hone\config.psd1')
            $mergedConfig = Merge-HoneConfig -Engine $script:engineConfig -Target $targetConfig
            $fixture = Get-HarnessTestingFixture -Config $mergedConfig -TargetDir $targetDir
            $fixture.Scenario.Name | Should -Be $fixtureName
            $fixture.Scenario.ExpectedOutcome | Should -Be $script:expectedOutcomes[$fixtureName]
        }
    }

    It 'resets staged fixture run state without disturbing fixture assets' {
        $targetDir = Copy-HoneTargetFixture -Name 'happy-path' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'reset-target')
        $layout = Get-HoneTargetLayout -TargetDir $targetDir

        Set-HoneFixtureBaseline -TargetDir $targetDir | Out-Null
        New-Item -ItemType Directory -Path (Join-Path -Path $layout.ResultsDir -ChildPath 'experiment-1\diagnostics\cpu-hotspots') -Force | Out-Null
        'fixture log entry' | Set-Content -Path (Join-Path -Path $layout.ResultsDir -ChildPath 'hone.jsonl') -Encoding ascii
        '{}' | Set-Content -Path (Join-Path -Path $layout.ResultsDir -ChildPath 'experiment-1\diagnostics\cpu-hotspots\fixture-report.json') -Encoding ascii
        '[]' | Set-Content -Path (Join-Path -Path $layout.MetadataDir -ChildPath 'experiment-queue.json') -Encoding ascii

        $resetResult = Reset-HoneFixtureRun -TargetDir $targetDir

        $resetResult.RemovedPaths.Count | Should -BeGreaterThan 0
        Test-Path -Path (Join-Path -Path $layout.ResultsDir -ChildPath 'baseline.json') | Should -BeFalse
        Test-Path -Path (Join-Path -Path $layout.ResultsDir -ChildPath 'experiment-1') | Should -BeFalse
        Test-Path -Path (Join-Path -Path $layout.ResultsDir -ChildPath 'hone.jsonl') | Should -BeFalse
        Test-Path -Path (Join-Path -Path $layout.MetadataDir -ChildPath 'experiment-queue.json') | Should -BeFalse
        Test-Path -Path (Join-Path -Path $targetDir -ChildPath '.hone\fixtures\fixture.psd1') | Should -BeTrue
        Test-Path -Path (Join-Path -Path $targetDir -ChildPath '.hone\scenarios\thresholds.json') | Should -BeTrue
        Test-Path -Path $layout.ResultsDir | Should -BeTrue
        Test-Path -Path $layout.MetadataDir | Should -BeTrue
    }

    It 'runs the happy-path fixture target through the real harness entry points' {
        $targetDir = Copy-HoneTargetFixture -Name 'happy-path' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'happy-path-runtime')
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath '.hone\results\hone.jsonl'
        $targetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $targetDir -ChildPath '.hone\config.psd1')

        & $script:baselineScript -ConfigPath $script:configPath -TargetDir $targetDir -TargetConfig $targetConfig

        $baselineMetrics = Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\baseline.json') -Raw | ConvertFrom-Json
        $baselineCounters = Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\baseline-counters.json') -Raw | ConvertFrom-Json
        $secondaryBaselineMetrics = Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\baseline-secondary.json') -Raw | ConvertFrom-Json

        $buildResult = & $script:buildScript -ConfigPath $script:configPath -TargetDir $targetDir -Experiment 1
        $testResult = & $script:testsScript -ConfigPath $script:configPath -TargetDir $targetDir -Experiment 1
        $scaleResult = & $script:scaleScript -ConfigPath $script:configPath -TargetDir $targetDir -Experiment 1
        $diagnosticResult = & $script:diagScript -Experiment 1 -ConfigPath $script:configPath -CurrentMetrics $scaleResult.Metrics -TargetDir $targetDir -TargetConfig $targetConfig
        $comparisonResult = & $script:compareScript `
            -CurrentMetrics $scaleResult.Metrics `
            -BaselineMetrics $baselineMetrics `
            -PreviousMetrics $baselineMetrics `
            -CurrentCounterMetrics $scaleResult.CounterMetrics `
            -PreviousCounterMetrics $baselineCounters `
            -ConfigPath $script:configPath `
            -Experiment 1

        $buildResult.Success | Should -BeTrue
        $testResult.Success | Should -BeTrue
        $scaleResult.Success | Should -BeTrue
        $diagnosticResult.Success | Should -BeTrue
        $comparisonResult.Improved | Should -BeTrue
        $comparisonResult.Regression | Should -BeFalse
        $baselineMetrics.HttpReqDuration.P95 | Should -Be 105.4
        $secondaryBaselineMetrics.HttpReqDuration.P95 | Should -Be 112.6
        $scaleResult.Metrics.HttpReqDuration.P95 | Should -Be 95.1
        $diagnosticResult.AnalyzerReports['cpu-hotspots'].Report.hottestMethods | Should -Contain 'MockApi.Controllers.ProductsController.GetAll'

        Assert-HoneArtifactCategory -TargetDir $targetDir -Experiment 1 -Categories @(
            'baseline_metrics',
            'baseline_counter_metrics',
            'scenario_baselines',
            'build_output',
            'e2e_output',
            'e2e_trx',
            'k6_summary',
            'k6_log',
            'counter_metrics',
            'diagnostic_reports',
            'run_metadata',
            'hone_log'
        )
    }

    It 'replays failure and regression fixture targets through the real runtime seams' {
        $buildFailureDir = Copy-HoneTargetFixture -Name 'build-failure' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'build-failure-runtime')
        $env:HONE_LOG_PATH = Join-Path -Path $buildFailureDir -ChildPath '.hone\results\hone.jsonl'
        $buildFailureResult = & $script:buildScript -ConfigPath $script:configPath -TargetDir $buildFailureDir -Experiment 1

        $buildFailureResult.Success | Should -BeFalse
        $buildFailureResult.ExitCode | Should -Be 1
        $buildFailureResult.Output | Should -Match 'Fixture build failed'
        Assert-HoneArtifactCategory -TargetDir $buildFailureDir -Experiment 1 -Categories @('build_output', 'hone_log')

        $testFailureDir = Copy-HoneTargetFixture -Name 'test-failure' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'test-failure-runtime')
        $env:HONE_LOG_PATH = Join-Path -Path $testFailureDir -ChildPath '.hone\results\hone.jsonl'
        $testFailureBuild = & $script:buildScript -ConfigPath $script:configPath -TargetDir $testFailureDir -Experiment 1
        $testFailureResult = & $script:testsScript -ConfigPath $script:configPath -TargetDir $testFailureDir -Experiment 1

        $testFailureBuild.Success | Should -BeTrue
        $testFailureResult.Success | Should -BeFalse
        $testFailureResult.FailedTests | Should -Be 1
        Assert-HoneArtifactCategory -TargetDir $testFailureDir -Experiment 1 -Categories @('build_output', 'e2e_output', 'e2e_trx', 'hone_log')

        $regressionDir = Copy-HoneTargetFixture -Name 'regression' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'regression-runtime')
        $env:HONE_LOG_PATH = Join-Path -Path $regressionDir -ChildPath '.hone\results\hone.jsonl'
        $regressionTargetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $regressionDir -ChildPath '.hone\config.psd1')

        & $script:baselineScript -ConfigPath $script:configPath -TargetDir $regressionDir -TargetConfig $regressionTargetConfig

        $regressionBaselineMetrics = Get-Content -Path (Join-Path -Path $regressionDir -ChildPath '.hone\results\baseline.json') -Raw | ConvertFrom-Json
        $regressionBaselineCounters = Get-Content -Path (Join-Path -Path $regressionDir -ChildPath '.hone\results\baseline-counters.json') -Raw | ConvertFrom-Json
        $regressionBuild = & $script:buildScript -ConfigPath $script:configPath -TargetDir $regressionDir -Experiment 1
        $regressionTests = & $script:testsScript -ConfigPath $script:configPath -TargetDir $regressionDir -Experiment 1
        $regressionScale = & $script:scaleScript -ConfigPath $script:configPath -TargetDir $regressionDir -Experiment 1
        $regressionComparison = & $script:compareScript `
            -CurrentMetrics $regressionScale.Metrics `
            -BaselineMetrics $regressionBaselineMetrics `
            -PreviousMetrics $regressionBaselineMetrics `
            -CurrentCounterMetrics $regressionScale.CounterMetrics `
            -PreviousCounterMetrics $regressionBaselineCounters `
            -ConfigPath $script:configPath `
            -Experiment 1

        $regressionBuild.Success | Should -BeTrue
        $regressionTests.Success | Should -BeTrue
        $regressionScale.Success | Should -BeTrue
        $regressionScale.Metrics.HttpReqDuration.P95 | Should -Be 121.2
        $regressionComparison.Improved | Should -BeFalse
        $regressionComparison.Regression | Should -BeTrue
        $regressionComparison.RegressionDetail | Should -Match 'p95 regressed'
        Assert-HoneArtifactCategory -TargetDir $regressionDir -Experiment 1 -Categories @(
            'baseline_metrics',
            'baseline_counter_metrics',
            'scenario_baselines',
            'build_output',
            'e2e_output',
            'e2e_trx',
            'k6_summary',
            'counter_metrics',
            'hone_log'
        )
    }

    It 'surfaces stacked-diffs publish metadata from the checked-in fixture target' {
        $targetDir = Copy-HoneTargetFixture -Name 'stacked-diffs' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'stacked-diffs-runtime')
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath '.hone\results\hone.jsonl'
        $previousFixtureTarget = $env:HONE_HARNESS_TEST_TARGET_DIR

        try {
            $env:HONE_HARNESS_TEST_TARGET_DIR = $targetDir

            $failedBuild = & $script:buildScript -ConfigPath $script:configPath -TargetDir $targetDir -Experiment 2
            $firstPr = New-ExperimentPR -Experiment 1 -BranchName 'hone/experiment-1' -BaseBranch 'main' -Outcome 'improved' -Description 'Fixture PR 1' -Body 'Fixture body'
            $thirdPr = New-ExperimentPR -Experiment 3 -BranchName 'hone/experiment-3' -BaseBranch 'hone/experiment-1' -Outcome 'improved' -Description 'Fixture PR 3' -Body 'Fixture body'

            $failedBuild.Success | Should -BeFalse
            $failedBuild.Output | Should -Match 'Fixture stacked-diffs build failed'
            $firstPr.Success | Should -BeTrue
            $firstPr.PrNumber | Should -Be 5301
            $thirdPr.Success | Should -BeTrue
            $thirdPr.PrNumber | Should -Be 5303
        } finally {
            $env:HONE_HARNESS_TEST_TARGET_DIR = $previousFixtureTarget
        }
    }
}
