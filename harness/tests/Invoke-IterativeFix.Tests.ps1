BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $script:harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $script:scriptPath = Join-Path -Path $script:harnessRoot -ChildPath 'Invoke-IterativeFix.ps1'
    $script:configPath = Join-Path -Path $script:harnessRoot -ChildPath 'config.psd1'
    $script:originalLogPath = $env:HONE_LOG_PATH

    function Set-IterativeTargetConfigOverride {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)]
            [string]$TargetDir,

            [int]$MaxAttempts = 2,

            [double]$MaxDiffGrowthFactor = 3.0,

            [bool]$TestFileGuard = $true,

            [string]$TestProjectPath
        )

        if (-not $PSCmdlet.ShouldProcess($TargetDir, 'Set iterative fixer override configuration')) {
            return
        }

        $targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone\config.psd1'
        $configContent = Get-Content -Path $targetConfigPath -Raw

        if ($TestProjectPath) {
            $escapedProjectPath = [regex]::Escape($TestProjectPath)
            $configContent = [regex]::Replace(
                $configContent,
                "TestProjectPath\s*=\s*'[^']+'",
                "TestProjectPath  = '$escapedProjectPath'",
                1
            )
            $configContent = $configContent.Replace($escapedProjectPath, $TestProjectPath)
        }

        $guardLiteral = if ($TestFileGuard) { '$true' } else { '$false' }
        $growthFactor = $MaxDiffGrowthFactor.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $configContent = $configContent -replace "\r?\n\}\s*$", @"

    Fixer = @{
        MaxAttempts = $MaxAttempts
        MaxDiffGrowthFactor = $growthFactor
        TestFileGuard = $guardLiteral
    }
}
"@

        Set-Content -Path $targetConfigPath -Value $configContent -Encoding ascii
    }
}

AfterAll {
    $env:HONE_LOG_PATH = $script:originalLogPath
}

Describe 'Invoke-IterativeFix retry orchestration' {
    It 'succeeds on the first attempt and writes iteration artifacts' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'iterative-success-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'

        $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'iterative-success'
    }

    Runtime = @{
        Agents = @{
            Fix = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-fix-response.md'
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
                TrxContent = '<TestRun id="iterative-success" />'
            }
        }
    }
}
'@

        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $result = & $script:scriptPath `
            -FilePath 'MockApi\Controllers\ProductsController.cs' `
            -Explanation 'Optimize the hot path query' `
            -Experiment 1 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API'

        $iterationLogPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-1\iteration-log.json'
        $attemptDir = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-1\iterations\attempt-1'

        $result.Success | Should -BeTrue
        $result.Attempt | Should -Be 1
        $result.AttemptCount | Should -Be 1
        $result.TestResult.Success | Should -BeTrue
        Test-Path -Path $iterationLogPath | Should -BeTrue
        Test-Path -Path (Join-Path -Path $attemptDir -ChildPath 'build.log') | Should -BeTrue
        Test-Path -Path (Join-Path -Path $attemptDir -ChildPath 'e2e-tests.log') | Should -BeTrue

        $iterationLog = Get-Content -Path $iterationLogPath -Raw | ConvertFrom-Json
        $iterationLog.finalOutcome | Should -Be 'success'
        $iterationLog.totalAttempts | Should -Be 1
        $iterationLog.attempts[0].stage | Should -Be 'test'
        $iterationLog.attempts[0].outcome | Should -Be 'passed'

        Push-Location $targetDir
        try {
            $currentBranch = (& git rev-parse --abbrev-ref HEAD 2>$null | Out-String).Trim()
        } finally {
            Pop-Location
        }

        $currentBranch | Should -Be 'hone/experiment-1'
    }

    It 'retries after a build failure and succeeds on a later attempt' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'iterative-build-retry-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'

        $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'iterative-build-retry'
    }

    Runtime = @{
        Agents = @{
            Fix = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-fix-response.md'
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
                TrxContent = '<TestRun id="iterative-build-retry" />'
            }
        }
    }
}
'@

        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest
        Set-IterativeTargetConfigOverride -TargetDir $targetDir -MaxAttempts 2
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $result = & $script:scriptPath `
            -FilePath 'MockApi\Controllers\ProductsController.cs' `
            -Explanation 'Optimize the hot path query' `
            -Experiment 2 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API'

        $iterationLogPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-2\iteration-log.json'
        $retryPromptPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-2\iterations\attempt-2\fix-prompt.md'

        $result.Success | Should -BeTrue
        $result.Attempt | Should -Be 2
        $result.AttemptCount | Should -Be 2
        $result.BuildResult.Success | Should -BeTrue
        Test-Path -Path $iterationLogPath | Should -BeTrue
        (Get-Content -Path $retryPromptPath -Raw) | Should -Match 'CS1002'

        $iterationLog = Get-Content -Path $iterationLogPath -Raw | ConvertFrom-Json
        $iterationLog.finalOutcome | Should -Be 'success'
        $iterationLog.totalAttempts | Should -Be 2
        $iterationLog.attempts[0].stage | Should -Be 'build'
        $iterationLog.attempts[0].outcome | Should -Be 'failed'
        $iterationLog.attempts[1].outcome | Should -Be 'passed'
    }

    It 'retries after a test failure and succeeds on a later attempt' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'iterative-test-retry-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'

        $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'iterative-test-retry'
    }

    Runtime = @{
        Agents = @{
            Fix = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-fix-response.md'
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
                TrxContent = '<TestRun id="iterative-test-retry-success" />'
            }
            ByAttempt = @{
                '1' = @{
                    ExitCode = 1
                    Total = 4
                    Passed = 3
                    Failed = 1
                    TrxContent = '<TestRun id="iterative-test-retry-fail" />'
                }
            }
        }
    }
}
'@

        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest
        Set-IterativeTargetConfigOverride -TargetDir $targetDir -MaxAttempts 2
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $result = & $script:scriptPath `
            -FilePath 'MockApi\Controllers\ProductsController.cs' `
            -Explanation 'Optimize the hot path query' `
            -Experiment 3 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API'

        $iterationLogPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-3\iteration-log.json'
        $retryPromptPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-3\iterations\attempt-2\fix-prompt.md'

        $result.Success | Should -BeTrue
        $result.Attempt | Should -Be 2
        $result.AttemptCount | Should -Be 2
        $result.TestResult.Success | Should -BeTrue
        Test-Path -Path $iterationLogPath | Should -BeTrue
        (Get-Content -Path $retryPromptPath -Raw) | Should -Match 'Failed:\s*1'

        $iterationLog = Get-Content -Path $iterationLogPath -Raw | ConvertFrom-Json
        $iterationLog.attempts[0].stage | Should -Be 'test'
        $iterationLog.attempts[0].outcome | Should -Be 'failed'
        $iterationLog.attempts[1].outcome | Should -Be 'passed'
    }

    It 'returns retry budget exhausted after repeated failures' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'iterative-budget-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'

        $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'iterative-budget'
    }

    Runtime = @{
        Agents = @{
            Fix = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-fix-response.md'
                }
            }
        }

        Build = @{
            Default = @{
                ExitCode = 1
                Output = 'CS0103: The name db does not exist in the current context'
            }
        }

        Tests = @{
            Default = @{
                ExitCode = 0
                Total = 4
                Passed = 4
                Failed = 0
                TrxContent = '<TestRun id="iterative-budget" />'
            }
        }
    }
}
'@

        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest
        Set-IterativeTargetConfigOverride -TargetDir $targetDir -MaxAttempts 2
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $result = & $script:scriptPath `
            -FilePath 'MockApi\Controllers\ProductsController.cs' `
            -Explanation 'Optimize the hot path query' `
            -Experiment 4 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API'

        $iterationLogPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-4\iteration-log.json'

        $result.Success | Should -BeFalse
        $result.ExitReason | Should -Be 'retry_budget_exhausted'
        $result.LastFailureStage | Should -Be 'build'
        $result.AttemptCount | Should -Be 2
        Test-Path -Path $iterationLogPath | Should -BeTrue

        $iterationLog = Get-Content -Path $iterationLogPath -Raw | ConvertFrom-Json
        $iterationLog.finalOutcome | Should -Be 'retry_budget_exhausted'
        $iterationLog.totalAttempts | Should -Be 2
    }

    It 'rejects fixes that modify test files' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'iterative-test-guard-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $testDir = Join-Path -Path $targetDir -ChildPath 'MockApi.Tests'
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        @'
namespace MockApi.Tests;

public class ProductsControllerTests
{
}
'@ | Set-Content -Path (Join-Path -Path $testDir -ChildPath 'ProductsControllerTests.cs') -Encoding ascii

        $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'iterative-test-guard'
    }

    Runtime = @{
        Agents = @{
            Fix = @{
                Default = @{
                    MockResponsePath = '__HARNESS_ROOT__\test-fixtures\mock-fix-response.md'
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
                TrxContent = '<TestRun id="iterative-test-guard" />'
            }
        }
    }
}
'@

        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest
        Set-IterativeTargetConfigOverride -TargetDir $targetDir -MaxAttempts 2 -TestProjectPath 'MockApi.Tests'
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $result = & $script:scriptPath `
            -FilePath 'MockApi.Tests\ProductsControllerTests.cs' `
            -Explanation 'Optimize the test helper' `
            -Experiment 5 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API'

        $iterationLog = Get-Content -Path (Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-5\iteration-log.json') -Raw | ConvertFrom-Json

        $result.Success | Should -BeFalse
        $result.ExitReason | Should -Be 'retry_budget_exhausted'
        $result.LastFailureStage | Should -Be 'guard'
        $result.FailureDetail | Should -Match 'Fix modified test files'
        $iterationLog.attempts[0].changedFiles[0] | Should -Match 'MockApi.Tests'
    }

    It 'rejects retries when diff growth exceeds the configured guard' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'iterative-diff-guard-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $fixturesDir = Join-Path -Path $targetDir -ChildPath '.hone\fixtures'
        $smallFixPath = Join-Path -Path $fixturesDir -ChildPath 'small-fix.md'
        $largeFixPath = Join-Path -Path $fixturesDir -ChildPath 'large-fix.md'

        @'
```csharp
namespace MockApi.Controllers;

public class ProductsController
{
    public string Get() => "small";
}
```
'@ | Set-Content -Path $smallFixPath -Encoding ascii

        @'
```csharp
using System.Collections.Generic;
using System.Linq;

namespace MockApi.Controllers;

public class ProductsController
{
    public IEnumerable<string> Get()
    {
        var values = new List<string>
        {
            "one",
            "two",
            "three",
            "four",
            "five",
            "six",
            "seven",
            "eight"
        };

        return values.Select(static value => value.ToUpperInvariant()).ToArray();
    }
}
```
'@ | Set-Content -Path $largeFixPath -Encoding ascii

        $fixtureManifest = @'
@{
    Scenario = @{
        Name = 'iterative-diff-guard'
    }

    Runtime = @{
        Agents = @{
            Fix = @{
                Default = @{
                    MockResponsePath = 'small-fix.md'
                }
                ByAttempt = @{
                    '2' = @{
                        MockResponsePath = 'large-fix.md'
                    }
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
                    Output = 'CS1061: Missing extension method'
                }
            }
        }

        Tests = @{
            Default = @{
                ExitCode = 0
                Total = 4
                Passed = 4
                Failed = 0
                TrxContent = '<TestRun id="iterative-diff-guard" />'
            }
        }
    }
}
'@

        Enable-HarnessTestingFixture -TargetDir $targetDir -FixtureManifestContent $fixtureManifest
        Set-IterativeTargetConfigOverride -TargetDir $targetDir -MaxAttempts 2 -MaxDiffGrowthFactor 1.5
        Initialize-HoneTargetRepository -TargetDir $targetDir | Out-Null
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath 'artifacts\hone.jsonl'

        $result = & $script:scriptPath `
            -FilePath 'MockApi\Controllers\ProductsController.cs' `
            -Explanation 'Optimize the hot path query' `
            -Experiment 6 `
            -BaseBranch 'main' `
            -ConfigPath $script:configPath `
            -TargetDir $targetDir `
            -TargetName 'Mock API'

        $retryPromptPath = Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-6\iterations\attempt-2\fix-prompt.md'
        $iterationLog = Get-Content -Path (Join-Path -Path $targetDir -ChildPath 'artifacts\experiment-6\iteration-log.json') -Raw | ConvertFrom-Json

        $result.Success | Should -BeFalse
        $result.ExitReason | Should -Be 'retry_budget_exhausted'
        $result.LastFailureStage | Should -Be 'guard'
        $result.FailureDetail | Should -Match 'Diff grew to'
        (Get-Content -Path $retryPromptPath -Raw) | Should -Match 'CS1061'
        $iterationLog.attempts[1].stage | Should -Be 'guard'
        $iterationLog.attempts[1].outcome | Should -Be 'rejected'
    }
}
