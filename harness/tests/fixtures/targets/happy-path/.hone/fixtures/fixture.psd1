@{
    Scenario = @{
        Name = 'happy-path'
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
        }

        Tests = @{
            Default = @{
                ExitCode = 0
                Total = 4
                Passed = 4
                Failed = 0
                TrxContent = '<TestRun id="happy-path" />'
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
                PrNumber = 5101
                PrUrl = 'https://example.invalid/hone/happy-path/pull/5101'
            }
        }
    }
}
