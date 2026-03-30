BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $buildScript = Join-Path -Path $harnessRoot -ChildPath 'Invoke-Build.ps1'
    $testsScript = Join-Path -Path $harnessRoot -ChildPath 'Invoke-E2ETests.ps1'
    $scaleScript = Join-Path -Path $harnessRoot -ChildPath 'Invoke-ScaleTests.ps1'
    $diagScript = Join-Path -Path $harnessRoot -ChildPath 'Invoke-DiagnosticMeasurement.ps1'
    $originalLogPath = $env:HONE_LOG_PATH

    $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'happy-path'
    }

    Runtime = @{
        Build = @{
            Default = @{
                ExitCode = 0
                Output = 'Fixture build succeeded'
            }
        }

        Tests = @{
            Default = @{
                ExitCode = 0
                Total = 4
                Passed = 4
                Failed = 0
                TrxContent = '<TestRun id="fixture" />'
            }
        }

        Scale = @{
            Primary = @{
                Default = @{
                    SummaryPath = '__HARNESS_ROOT__\test-fixtures\k6-results\improved-summary.json'
                    CounterMetricsPath = '__HARNESS_ROOT__\test-fixtures\diagnostics\runtime-counters.json'
                    Output = 'Fixture scale run complete'
                }
            }
            Scenarios = @{
                secondary = @{
                    Default = @{
                        SummaryPath = '__HARNESS_ROOT__\test-fixtures\k6-results\baseline-secondary-summary.json'
                        Output = 'Fixture secondary scenario'
                    }
                }
            }
        }

        Diagnostics = @{
            Reports = @{
                'cpu-hotspots' = @{
                    ReportPath = '__HARNESS_ROOT__\test-fixtures\diagnostics\cpu-hotspots-report.json'
                    Summary = 'Fixture CPU hotspot report'
                }
            }
        }
    }
}
'@
    $null = $configPath, $buildScript, $testsScript, $scaleScript, $diagScript, $originalLogPath, $fixtureManifest
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Harness-testing runtime fixtures' {
    It 'drives deterministic build, test, scale, and diagnostics through real entry points' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'runtime-target')
        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest
        Set-HoneFixtureBaseline -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'results\hone.jsonl'

        $buildResult = & $buildScript -ConfigPath $configPath -TargetDir $targetDir -Experiment 1
        $testResult = & $testsScript -ConfigPath $configPath -TargetDir $targetDir -Experiment 1
        $scaleResult = & $scaleScript -ConfigPath $configPath -TargetDir $targetDir -Experiment 1

        $targetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $targetDir -ChildPath '.hone\config.psd1')
        $diagnosticResult = & $diagScript -Experiment 1 -ConfigPath $configPath -CurrentMetrics $scaleResult.Metrics -TargetDir $targetDir -TargetConfig $targetConfig

        $buildResult.Success | Should -BeTrue
        $buildResult.Output | Should -Match 'Fixture build succeeded'

        $testResult.Success | Should -BeTrue
        $testResult.TotalTests | Should -Be 4
        $testResult.PassedTests | Should -Be 4

        $scaleResult.Success | Should -BeTrue
        $scaleResult.Metrics.HttpReqDuration.P95 | Should -Be 95.1
        $scaleResult.CounterMetrics.Runtime.CpuUsage.Avg | Should -Be 12.5

        $diagnosticResult.Success | Should -BeTrue
        $diagnosticResult.AnalyzerReports.Keys | Should -Contain 'cpu-hotspots'
        $diagnosticResult.AnalyzerReports['cpu-hotspots'].Summary | Should -Be 'Fixture CPU hotspot report'

        Assert-HoneArtifactCategory -TargetDir $targetDir -Experiment 1 -Categories @(
            'baseline_metrics',
            'build_output',
            'e2e_output',
            'e2e_trx',
            'k6_summary',
            'k6_log',
            'counter_metrics',
            'hone_log'
        )
    }
}
