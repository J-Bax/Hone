@{
    # ── Target API ──────────────────────────────────────────────
    Api = @{
        # Path to the .NET solution file (relative to repo root)
        SolutionPath    = 'sample-api/SampleApi.sln'

        # Path to the API project directory (relative to repo root)
        ProjectPath     = 'sample-api/SampleApi'

        # Path to the E2E test project directory (relative to repo root)
        TestProjectPath = 'sample-api/SampleApi.Tests'

        # URL where the API listens when started
        BaseUrl         = 'http://localhost:5000'

        # Health check endpoint (GET, must return 200)
        HealthEndpoint  = '/health'

        # Seconds to wait for API to become healthy after start
        StartupTimeout  = 90
    }

    # ── Performance Tolerances ───────────────────────────────────
    # Instead of absolute targets, the loop accepts any improvement
    # and rejects regressions.  It stops when no metric can be improved.
    Tolerances = @{
        # Minimum improvement (any single metric) to accept an iteration (0.01 = 1%)
        MinImprovementPct = 0.01

        # Maximum regression allowed per metric before rejecting (0.02 = 2%)
        MaxRegressionPct  = 0.02

        # Stop after this many consecutive iterations with no improvement
        StaleIterationsBeforeStop = 2

        # ── Efficiency Tiebreaker ────────────────────────────────
        # When performance metrics are flat (no improvement, no regression),
        # accept the iteration if OS-level resource usage decreased.
        # Only CPU and working set are evaluated — these are the resources
        # that matter on a shared-VM architecture.
        Efficiency = @{
            # Enable/disable efficiency tiebreaker
            Enabled = $true

            # Minimum reduction in avg CPU usage to count as efficiency gain (0.05 = 5%)
            MinCpuReductionPct       = 0.05

            # Minimum reduction in peak working set to count as efficiency gain (0.05 = 5%)
            MinWorkingSetReductionPct = 0.05
        }
    }

    # ── Scale Testing ───────────────────────────────────────────
    ScaleTest = @{
        # Path to the k6 scenario to run on each iteration (primary / optimization)
        ScenarioPath = 'sample-api/scale-tests/scenarios/baseline.js'

        # JSON file listing all scenarios and their metadata
        ScenarioRegistryPath = 'sample-api/scale-tests/thresholds.json'

        # Path to store k6 JSON summary output
        OutputPath   = 'sample-api/results'

        # Additional k6 CLI arguments
        ExtraArgs    = @()
    }

    # ── .NET Performance Counters ───────────────────────────────
    DotnetCounters = @{
        # Enable counter collection during scale tests
        Enabled = $true

        # Counter providers to collect
        Providers = @(
            'System.Runtime'
            'Microsoft.AspNetCore.Hosting'
            'Microsoft.AspNetCore.Http.Connections'
            'System.Net.Http'
        )

        # Sampling interval in seconds
        RefreshIntervalSeconds = 1
    }

    # ── Agentic Loop ───────────────────────────────────────────
    Loop = @{
        # Maximum number of optimization iterations
        MaxIterations = 5

        # Git branch prefix for optimization branches
        BranchPrefix  = 'hone/iteration'
    }

    # ── Logging ─────────────────────────────────────────────────
    Logging = @{
        # Directory for log files (relative to repo root)
        OutputPath = 'sample-api/results'

        # Log level: 'verbose', 'info', 'warning', 'error'
        Level      = 'info'
    }
}
