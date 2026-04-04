@{
    Name = 'HarnessRegressionFixture'
    BaseBranch = 'main'

    Api = @{
        SolutionPath = 'SampleApi.sln'
        ProjectPath = 'SampleApi'
        TestProjectPath = 'SampleApi.sln'
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
        Start = @{ Type = 'Shared'; Name = 'dotnet-start' }
        Stop = @{ Type = 'Shared'; Name = 'dotnet-stop' }
        Ready = @{ Type = 'Skip' }
        Warmup = @{ Type = 'Skip' }
        Active = @{ Type = 'Shared'; Name = 'k6-run' }
        Cooldown = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
        Cleanup = @{ Type = 'Skip' }
    }

    ScaleTest = @{
        ScenarioPath = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        MeasuredRuns = 1
        WarmupEnabled = $false
        CooldownSeconds = 0
    }

    HarnessTesting = @{
        Enabled = $true
        ManifestPath = '.hone\fixtures\fixture.psd1'
    }
}

