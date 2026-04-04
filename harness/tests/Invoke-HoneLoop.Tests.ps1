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
            [string]$TargetDir,

            [int]$MaxAttempts = 1
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

    Fixer = @{
        MaxAttempts = $MaxAttempts
        MaxDiffGrowthFactor = 3.0
        TestFileGuard = `$true
    }
}
"@

            Set-Content -Path $targetConfigPath -Value $configContent -Encoding ascii
        }
    }

    function Set-LoopFixtureManifest {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)]
            [string]$TargetDir,

            [Parameter(Mandatory)]
            [string]$ManifestContent
        )

        if ($PSCmdlet.ShouldProcess($TargetDir, 'Update loop fixture manifest')) {
            Set-Content -Path (Join-Path -Path $TargetDir -ChildPath '.hone\fixtures\fixture.psd1') -Value $ManifestContent -Encoding ascii
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
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath '.hone\results\hone.jsonl'

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

        $runMetadataPath = Join-Path -Path $targetDir -ChildPath '.hone\results\run-metadata.json'
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

        Test-Path (Join-Path -Path $targetDir -ChildPath '.hone\results\experiment-2\build.log') | Should -BeTrue

    }
}

Describe 'Invoke-HoneLoop iterative fixer integration' {
    It 'recovers from a first-attempt build failure and records retry metadata' {
        $targetDir = Copy-HoneTargetFixture -Name 'happy-path' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'loop-iterative-recovery')
        Set-LoopTestOverride -TargetDir $targetDir -MaxAttempts 2

        $manifestContent = @'
@{
    Scenario = @{
        Name = 'loop-iterative-recovery'
        ExpectedOutcome = 'improved'
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
            }
        }

        Hooks = @{
            Start = @{
                Default = @{
                    Success = $true
                    Message = 'Fixture API started'
                    BaseUrl = 'http://localhost:0'
                    ActualBaseUrl = 'http://localhost:0'
                    Process = $null
                }
            }
            Stop = @{
                Default = @{
                    Success = $true
                    Message = 'Fixture API stopped'
                }
            }
            Cooldown = @{
                Default = @{
                    Success = $true
                    Message = 'Fixture cooldown completed'
                }
            }
            Cleanup = @{
                Default = @{
                    Success = $true
                    Message = 'Fixture cleanup completed'
                }
            }
        }

        Build = @{
            Default = @{
                ExitCode = 0
                Output = 'Fixture build succeeded'
            }
            ByAttempt = @{
                '1' = @{
                    ExitCode = 1
                    Output = 'CS1002: ; expected'
                }
            }
        }

        Tests = @{
            Default = @{
                ExitCode = 0
                Total = 4
                Passed = 4
                Failed = 0
                TrxContent = '<TestRun id="loop-iterative-recovery" />'
            }
        }

        Scale = @{
            Primary = @{
                Default = @{
                    SummaryPath = '__HARNESS_ROOT__\test-fixtures\k6-results\improved-summary.json'
                    CounterMetricsPath = '__HARNESS_ROOT__\test-fixtures\diagnostics\runtime-counters.json'
                    Output = 'Fixture scale run complete'
                }
                ByExperiment = @{
                    '0' = @{
                        SummaryPath = '__HARNESS_ROOT__\test-fixtures\k6-results\baseline-summary.json'
                        CounterMetricsPath = '__HARNESS_ROOT__\test-fixtures\diagnostics\runtime-counters.json'
                        Output = 'Fixture baseline scale run complete'
                    }
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

        Baseline = @{
            Default = @{
                Enabled = $true
            }
        }

        Loop = @{
            Default = @{
                SkipExternalPrerequisites = $true
                SkipInitialBranchCheckout = $true
                MachineInfo = @{
                    Cpu = @{
                        Name = 'Harness Fixture CPU'
                        LogicalProcessors = 8
                        PhysicalCores = 4
                    }
                    Memory = @{
                        TotalGB = 16
                    }
                    OS = @{
                        Description = 'Harness Fixture OS'
                    }
                }
            }
        }

        Publish = @{
            Default = @{
                SkipPRCreation = $true
                PrNumber = 5401
                PrUrl = 'https://example.invalid/hone/loop-iterative-recovery/pull/5401'
            }
        }
    }
}
'@

        Set-LoopFixtureManifest -TargetDir $targetDir -ManifestContent $manifestContent
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath '.hone\results\hone.jsonl'

        $result = & $script:loopScript -TargetPath $targetDir -MaxExperiments 1
        $runMetadata = Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\run-metadata.json') -Raw | ConvertFrom-Json
        $experiment1 = $runMetadata.Experiments | Where-Object { $_.Experiment -eq 1 }

        $result.Experiments | Should -Be 1
        $result.SuccessCount | Should -Be 1
        $experiment1.Outcome | Should -Be 'improved'
        $experiment1.FixAttemptCount | Should -Be 2
        $experiment1.FixRetried | Should -BeTrue
        $experiment1.FixIterationLogPath | Should -Be '.hone/results/experiment-1/iteration-log.json'
        Test-Path -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\experiment-1\iteration-log.json') | Should -BeTrue
        (Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\experiment-1\root-cause.md') -Raw) | Should -Match 'Iterative Fix Summary'
    }

    It 'records retry budget exhaustion as a rejected stacked-diff experiment' {
        $targetDir = Copy-HoneTargetFixture -Name 'build-failure' -DestinationPath (Join-Path -Path $TestDrive -ChildPath 'loop-iterative-exhausted')
        Set-LoopTestOverride -TargetDir $targetDir -MaxAttempts 2
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath '.hone\results\hone.jsonl'

        $result = & $script:loopScript -TargetPath $targetDir -MaxExperiments 1
        $runMetadata = Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\run-metadata.json') -Raw | ConvertFrom-Json
        $experiment1 = $runMetadata.Experiments | Where-Object { $_.Experiment -eq 1 }
        $queue = Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\metadata\experiment-queue.json') -Raw | ConvertFrom-Json
        $iterationLog = Get-Content -Path (Join-Path -Path $targetDir -ChildPath '.hone\results\experiment-1\iteration-log.json') -Raw | ConvertFrom-Json

        $result.Experiments | Should -Be 1
        $result.SuccessCount | Should -Be 0
        $result.FailedExperiments | Should -Be @(1)
        $result.PrChain | Should -Contain 5102
        $experiment1.Outcome | Should -Be 'retry_budget_exhausted'
        $experiment1.FixAttemptCount | Should -Be 2
        $experiment1.FixRetried | Should -BeTrue
        $experiment1.PrNumber | Should -Be 5102
        $queue.items[0].outcome | Should -Be 'retry_budget_exhausted'
        $iterationLog.finalOutcome | Should -Be 'retry_budget_exhausted'
        $iterationLog.totalAttempts | Should -Be 2
    }
}

