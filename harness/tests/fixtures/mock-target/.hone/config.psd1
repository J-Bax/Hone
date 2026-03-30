@{
    Name       = 'MockTarget'
    BaseBranch = 'main'

    Api = @{
        SolutionPath    = 'MockApi.sln'
        ProjectPath     = 'MockApi'
        TestProjectPath = 'MockApi.sln'
        MetadataPath    = 'results\metadata'
        BaseUrl         = 'http://localhost:0'
        HealthEndpoint  = '/health'
        StartupTimeout  = 10
        ResultsPath     = 'results'
    }

    Hooks = @{
        Prepare  = @{ Type = 'Script'; Path = '.hone\hooks\prepare.ps1' }
        Start    = @{ Type = 'Shared'; Name = 'dotnet-start' }
        Stop     = @{ Type = 'Shared'; Name = 'dotnet-stop' }
        Ready    = @{ Type = 'Skip' }
        Warmup   = @{ Type = 'Skip' }
        Active   = @{ Type = 'Skip' }
        Cooldown = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
        Cleanup  = @{ Type = 'Skip' }
    }

    ScaleTest = @{
        ScenarioPath         = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        MeasuredRuns         = 1
        WarmupEnabled        = $false
    }
}
