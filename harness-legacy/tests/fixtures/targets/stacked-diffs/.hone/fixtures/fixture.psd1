@{
    Scenario = @{
        Name = 'stacked-diffs'
        ExpectedOutcome = 'stacked-diffs'
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
            ByExperiment = @{
                '2' = @{
                    ExitCode = 1
                    Output = 'Fixture stacked-diffs build failed'
                }
            }
        }

        Tests = @{
            Default = @{
                ExitCode = 0
                Total = 4
                Passed = 4
                Failed = 0
                TrxContent = '<TestRun id="stacked-diffs" />'
            }
        }

        Scale = @{
            Primary = @{
                Default = @{
                    SummaryPath = '__HARNESS_ROOT__\test-fixtures\k6-results\improved-summary.json'
                    CounterMetricsPath = '__HARNESS_ROOT__\test-fixtures\diagnostics\runtime-counters.json'
                    Output = 'Fixture stacked-diffs scale run complete'
                }
                ByExperiment = @{
                    '0' = @{
                        SummaryPath = '__HARNESS_ROOT__\test-fixtures\k6-results\baseline-summary.json'
                        CounterMetricsPath = '__HARNESS_ROOT__\test-fixtures\diagnostics\runtime-counters.json'
                        Output = 'Fixture baseline scale run complete'
                    }
                    '3' = @{
                        SummaryPath = '__HARNESS_ROOT__\test-fixtures\k6-results\further-improved-summary.json'
                        CounterMetricsPath = '__HARNESS_ROOT__\test-fixtures\diagnostics\runtime-counters.json'
                        Output = 'Fixture stacked-diffs experiment 3 scale run complete'
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
                PrNumber = 5300
                PrUrl = 'https://example.invalid/hone/stacked-diffs/pull/5300'
            }
            ByExperiment = @{
                '1' = @{
                    SkipPRCreation = $true
                    PrNumber = 5301
                    PrUrl = 'https://example.invalid/hone/stacked-diffs/pull/5301'
                }
                '3' = @{
                    SkipPRCreation = $true
                    PrNumber = 5303
                    PrUrl = 'https://example.invalid/hone/stacked-diffs/pull/5303'
                }
            }
        }
    }
}
