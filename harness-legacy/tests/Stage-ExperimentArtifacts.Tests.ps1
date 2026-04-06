BeforeAll {
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Stage-ExperimentArtifacts.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $null = $harnessRoot, $scriptPath, $configPath
}

Describe 'Stage-ExperimentArtifacts' {
    It 'stages files under Api.ResultsPath instead of assuming results/' {
        $targetDir = Join-Path -Path $TestDrive -ChildPath 'custom-results-target'
        New-Item -ItemType Directory -Path $targetDir | Out-Null
        New-Item -ItemType Directory -Path (Join-Path -Path $targetDir -ChildPath '.hone') | Out-Null

        @'
@{
    Name       = 'CustomResultsTarget'
    BaseBranch = 'main'

    Api = @{
        SolutionPath    = 'MockApi.sln'
        ProjectPath     = 'MockApi'
        TestProjectPath = 'MockApi.sln'
        MetadataPath    = 'artifacts\metadata'
        ResultsPath     = 'artifacts'
        BaseUrl         = 'http://localhost:0'
        HealthEndpoint  = '/health'
        StartupTimeout  = 10
    }

    Hooks = @{
        Prepare  = @{ Type = 'Skip' }
        Start    = @{ Type = 'Skip' }
        Stop     = @{ Type = 'Skip' }
        Ready    = @{ Type = 'Skip' }
        Warmup   = @{ Type = 'Skip' }
        Active   = @{ Type = 'Skip' }
        Cooldown = @{ Type = 'Skip' }
        Cleanup  = @{ Type = 'Skip' }
    }

    ScaleTest = @{
        ScenarioPath         = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        MeasuredRuns         = 1
        WarmupEnabled        = $false
    }
}
'@ | Out-File -FilePath (Join-Path -Path $targetDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1') -Encoding ascii

        $artifactsDir = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'experiment-2'
        $metadataDir = Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'metadata'
        $iterationsDir = Join-Path -Path $artifactsDir -ChildPath 'iterations\attempt-1'
        New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
        New-Item -ItemType Directory -Path $metadataDir -Force | Out-Null
        New-Item -ItemType Directory -Path $iterationsDir -Force | Out-Null
        '{}' | Set-Content -Path (Join-Path -Path $artifactsDir -ChildPath 'analysis-response.json') -Encoding utf8
        '{}' | Set-Content -Path (Join-Path -Path $artifactsDir -ChildPath 'iteration-log.json') -Encoding utf8
        'retry prompt' | Set-Content -Path (Join-Path -Path $iterationsDir -ChildPath 'fix-prompt.md') -Encoding utf8
        '{}' | Set-Content -Path (Join-Path -Path $targetDir -ChildPath 'artifacts' -AdditionalChildPath 'run-metadata.json') -Encoding utf8

        Mock git {}

        & $scriptPath -Experiment 2 -TargetDir $targetDir -ConfigPath $configPath

        Should -Invoke git -Times 1 -ParameterFilter { $args[0] -eq 'add' -and $args[1] -eq 'artifacts/experiment-2/analysis-response.json' }
        Should -Invoke git -Times 1 -ParameterFilter { $args[0] -eq 'add' -and $args[1] -eq 'artifacts/experiment-2/iteration-log.json' }
        Should -Invoke git -Times 1 -ParameterFilter { $args[0] -eq 'add' -and $args[1] -eq 'artifacts/experiment-2/iterations/' }
        Should -Invoke git -Times 1 -ParameterFilter { $args[0] -eq 'add' -and $args[1] -eq 'artifacts/metadata/' }
        Should -Invoke git -Times 1 -ParameterFilter { $args[0] -eq 'add' -and $args[1] -eq 'artifacts/run-metadata.json' }
    }
}
