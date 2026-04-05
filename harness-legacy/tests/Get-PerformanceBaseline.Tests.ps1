BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $script:harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $script:baselineScript = Join-Path -Path $script:harnessRoot -ChildPath 'Get-PerformanceBaseline.ps1'
    $script:configPath = Join-Path -Path $script:harnessRoot -ChildPath 'config.psd1'
    $script:originalLogPath = $env:HONE_LOG_PATH

    function Set-BaselineLifecycleTarget {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)]
            [string]$TargetDir,

            [Parameter(Mandatory)]
            [string]$TracePath,

            [string]$FailHook
        )

        if ($PSCmdlet.ShouldProcess($TargetDir, 'Set baseline lifecycle fixture target content')) {
            $honeDir = Join-Path -Path $TargetDir -ChildPath '.hone'
            $hooksDir = Join-Path -Path $honeDir -ChildPath 'hooks'
            $traceLiteral = $TracePath.Replace("'", "''")

            foreach ($hookName in @('prepare', 'start', 'ready', 'active', 'cooldown', 'stop', 'cleanup')) {
                $titleCaseHook = [char]::ToUpperInvariant($hookName[0]).ToString() + $hookName.Substring(1)
                $isFailureHook = $FailHook -eq $titleCaseHook
                $scriptContent = switch ($hookName) {
                    'start' {
                        @"
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]`$TargetPath,
    [Parameter(Mandatory)] [hashtable]`$Config,
    [string]`$BaseUrl,
    [string]`$Experiment
)

Add-Content -Path '$traceLiteral' -Value '$titleCaseHook'

[PSCustomObject]@{
    Success = `$true
    Message = 'Start complete'
    Duration = [timespan]::Zero
    Artifacts = @()
    BaseUrl = 'http://localhost:0'
    ActualBaseUrl = 'http://localhost:0'
    Process = `$null
}
"@
                    }
                    'active' {
                        @"
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]`$TargetPath,
    [Parameter(Mandatory)] [hashtable]`$Config,
    [string]`$BaseUrl,
    [string]`$Experiment
)

Add-Content -Path '$traceLiteral' -Value '$titleCaseHook'

[PSCustomObject]@{
    Success = `$true
    Message = 'Active complete'
    Duration = [timespan]::Zero
    Artifacts = @()
    Metrics = [PSCustomObject]@{
        HttpReqDuration = [PSCustomObject]@{
            Avg = 100
            P50 = 95
            P90 = 110
            P95 = 115
            P99 = 130
            Max = 140
        }
        HttpReqs = [PSCustomObject]@{
            Count = 1000
            Rate = 50
        }
        HttpReqFailed = [PSCustomObject]@{
            Count = 0
            Rate = 0
        }
    }
    CounterMetrics = [PSCustomObject]@{
        Runtime = [PSCustomObject]@{
            CpuUsage = [PSCustomObject]@{ Avg = 10; Max = 14 }
            GcHeapSizeMB = [PSCustomObject]@{ Avg = 64; Max = 80 }
            Gen0Collections = [PSCustomObject]@{ Last = 1 }
            Gen1Collections = [PSCustomObject]@{ Last = 0 }
            Gen2Collections = [PSCustomObject]@{ Last = 0 }
            GcPauseRatio = [PSCustomObject]@{ Avg = 0.01; Max = 0.02 }
            ThreadPoolThreads = [PSCustomObject]@{ Avg = 8; Max = 12 }
            ThreadPoolQueue = [PSCustomObject]@{ Avg = 0; Max = 1 }
            ExceptionCount = [PSCustomObject]@{ Last = 0 }
            WorkingSetMB = [PSCustomObject]@{ Avg = 80; Max = 100 }
            AllocRateMB = [PSCustomObject]@{ Avg = 12; Max = 15 }
        }
    }
    LastProcess = `$null
    LastBaseUrl = 'http://localhost:0'
}
"@
                    }
                    default {
                        $successLiteral = if ($isFailureHook) { '$false' } else { '$true' }
                        $messageLiteral = if ($isFailureHook) { "$titleCaseHook failed" } else { "$titleCaseHook complete" }
                        @"
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]`$TargetPath,
    [Parameter(Mandatory)] [hashtable]`$Config,
    [string]`$BaseUrl,
    [string]`$Experiment
)

Add-Content -Path '$traceLiteral' -Value '$titleCaseHook'

[PSCustomObject]@{
    Success = $successLiteral
    Message = '$messageLiteral'
    Duration = [timespan]::Zero
    Artifacts = @()
}
"@
                    }
                }

                Set-Content -Path (Join-Path -Path $hooksDir -ChildPath "$hookName.ps1") -Value $scriptContent -Encoding ascii
            }

            @'
{
  "scenarios": {
    "baseline": {
      "description": "Mock baseline scenario",
      "file": "baseline.js",
      "use_for_optimization": true
    }
  }
}
'@ | Set-Content -Path (Join-Path -Path $honeDir -ChildPath 'scenarios\thresholds.json') -Encoding ascii

            @"
@{
    Name = 'BaselineLifecycleTarget'
    BaseBranch = 'main'

    Api = @{
        SolutionPath = 'MockApi.sln'
        ProjectPath = 'MockApi'
        TestProjectPath = 'MockApi.sln'
        MetadataPath = '.hone\results\metadata'
        ResultsPath = '.hone\results'
        BaseUrl = 'http://localhost:0'
        HealthEndpoint = '/health'
        GcEndpoint = '/diag/gc'
        StartupTimeout = 10
        SourceCodePaths = @('Controllers')
        SourceFileGlob = '*.cs'
    }

    Hooks = @{
        Prepare = @{ Type = 'Script'; Path = '.hone\hooks\prepare.ps1' }
        Start = @{ Type = 'Script'; Path = '.hone\hooks\start.ps1' }
        Stop = @{ Type = 'Script'; Path = '.hone\hooks\stop.ps1' }
        Ready = @{ Type = 'Script'; Path = '.hone\hooks\ready.ps1' }
        Warmup = @{ Type = 'Skip' }
        Active = @{ Type = 'Script'; Path = '.hone\hooks\active.ps1' }
        Cooldown = @{ Type = 'Script'; Path = '.hone\hooks\cooldown.ps1' }
        Cleanup = @{ Type = 'Script'; Path = '.hone\hooks\cleanup.ps1' }
    }

    ScaleTest = @{
        ScenarioPath = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        MeasuredRuns = 1
        WarmupEnabled = `$false
        CooldownSeconds = 0
    }

    HarnessTesting = @{
        Enabled = `$true
        ManifestPath = '.hone\fixtures\fixture.psd1'
    }
}
"@ | Set-Content -Path (Join-Path -Path $honeDir -ChildPath 'config.psd1') -Encoding ascii

            @'
@{
    Scenario = @{
        Name = 'baseline-lifecycle'
        ExpectedOutcome = 'baseline'
    }

    Runtime = @{
        Build = @{
            Default = @{
                ExitCode = 0
                Output = 'Fixture build succeeded'
            }
        }
    }
}
'@ | Set-Content -Path (Join-Path -Path $honeDir -ChildPath 'fixtures\fixture.psd1') -Encoding ascii
        }
    }
}

AfterAll {
    $env:HONE_LOG_PATH = $script:originalLogPath
}

Describe 'Get-PerformanceBaseline lifecycle orchestration' {
    It 'executes lifecycle hooks in the expected baseline order' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'baseline-lifecycle-order')
        $tracePath = Join-Path -Path $TestDrive -ChildPath 'baseline-lifecycle-order.log'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath '.hone\results\hone.jsonl'

        Set-BaselineLifecycleTarget -TargetDir $targetDir -TracePath $tracePath

        $targetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $targetDir -ChildPath '.hone\config.psd1')

        & $script:baselineScript -ConfigPath $script:configPath -TargetDir $targetDir -TargetConfig $targetConfig

        (Get-Content -Path $tracePath) | Should -Be @(
            'Prepare'
            'Start'
            'Ready'
            'Active'
            'Cooldown'
            'Stop'
            'Cleanup'
        )

        Test-Path (Join-Path -Path $targetDir -ChildPath '.hone\results\baseline.json') | Should -BeTrue
        Test-Path (Join-Path -Path $targetDir -ChildPath '.hone\results\run-metadata.json') | Should -BeTrue
    }

    It 'surfaces hook failures clearly and stops at the failing hook' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'baseline-lifecycle-failure')
        $tracePath = Join-Path -Path $TestDrive -ChildPath 'baseline-lifecycle-failure.log'
        $env:HONE_LOG_PATH = Join-Path -Path $targetDir -ChildPath '.hone\results\hone.jsonl'

        Set-BaselineLifecycleTarget -TargetDir $targetDir -TracePath $tracePath -FailHook 'Ready'

        $targetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $targetDir -ChildPath '.hone\config.psd1')

        {
            & $script:baselineScript -ConfigPath $script:configPath -TargetDir $targetDir -TargetConfig $targetConfig
        } | Should -Throw '*Lifecycle hook ''Ready'' failed*'

        (Get-Content -Path $tracePath) | Should -Be @(
            'Prepare'
            'Start'
            'Ready'
        )
    }
}
