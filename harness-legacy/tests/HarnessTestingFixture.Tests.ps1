BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    Import-Module (Join-Path -Path $harnessRoot -ChildPath 'HoneHelpers.psm1') -Force
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'

    $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'happy-path'
    }

    Runtime = @{
        Agents = @{
            Analysis = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-analysis-response.json'
                }
            }
            Classification = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-classification-response.json'
                }
            }
            Fix = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-fix-response.md'
                }
                ByAttempt = @{
                    '2' = @{
                        MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-fix-response.md'
                    }
                }
            }
        }

        Build = @{
            Default = @{
                ExitCode = 0
                Output = 'Fixture build succeeded'
            }
            ByExperiment = @{
                '2' = @{
                    ExitCode = 1
                    Output = 'Fixture build failed'
                }
            }
        }

        Tests = @{
            Default = @{
                ExitCode = 0
                Total = 4
                Passed = 4
                Failed = 0
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

        Loop = @{
            Default = @{
                SkipExternalPrerequisites = $true
                SkipInitialBranchCheckout = $true
                MachineInfo = @{
                    Cpu = @{
                        Name = 'Fixture CPU'
                        LogicalProcessors = 8
                        PhysicalCores = 4
                    }
                    Memory = @{
                        TotalGB = 16
                    }
                    OS = @{
                        Description = 'Fixture OS'
                    }
                }
            }
        }

        Publish = @{
            Default = @{
                SkipPRCreation = $true
                PrNumber = 4242
                PrUrl = 'https://example.invalid/hone/fixture/pull/4242'
            }
        }
    }
}
'@

    $null = $configPath, $fixtureManifest
}

Describe 'Harness-testing fixture helpers' {
    It 'loads fixture manifests and resolves experiment-specific runtime definitions' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'fixture-target')
        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest

        $engineConfig = Import-PowerShellDataFile -Path $configPath
        $targetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $targetDir -ChildPath '.hone\config.psd1')
        $mergedConfig = Merge-HoneConfig -Engine $engineConfig -Target $targetConfig

        $fixture = Get-HarnessTestingFixture -Config $mergedConfig -TargetDir $targetDir
        $buildDefault = Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Build') -Experiment 1
        $buildFailure = Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Build') -Experiment 2
        $analysisMock = Get-HarnessTestingMockResponsePath -Config $mergedConfig -TargetDir $targetDir -Agent Analysis -Experiment 1
        $retryFixMock = Get-HarnessTestingMockResponsePath -Config $mergedConfig -TargetDir $targetDir -Agent Fix -Experiment 1 -Attempt 2

        $fixture.Scenario.Name | Should -Be 'happy-path'
        $buildDefault.ExitCode | Should -Be 0
        $buildFailure.ExitCode | Should -Be 1
        Test-Path -Path $analysisMock | Should -BeTrue
        Test-Path -Path $retryFixMock | Should -BeTrue
    }
}
