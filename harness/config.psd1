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

        # Directory for all performance results (relative to repo root)
        ResultsPath     = 'sample-api/results'

        # Directory for optimization metadata (log + queue) (relative to repo root)
        MetadataPath    = 'sample-api/results/metadata'
    }

    # ── Performance Tolerances ───────────────────────────────────
    # Instead of absolute targets, the loop accepts any improvement
    # and rejects regressions.  It stops when no metric can be improved.
    Tolerances = @{
        # Maximum regression allowed per metric before rejecting (0.10 = 10%)
        # Pure steady-state measurement (no ramp/setup contamination) with
        # median-of-5 runs, GC settling, and 3s cooldowns between runs.
        # Re-evaluate after baselining if CV drops below 8%.
        MaxRegressionPct  = 0.10

        # Minimum improvement (any single metric) to accept an iteration (0.03 = 3%)
        # Must exceed the noise floor to be meaningful.
        MinImprovementPct = 0.03

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

        # Additional k6 CLI arguments
        ExtraArgs    = @()
        # ── Warmup ──────────────────────────────────────────
        # Run a short 1-VU warmup pass before the measured run to ensure
        # the DB is seeded, EF Core model is compiled, and JIT is primed.
        WarmupEnabled      = $true
        WarmupScenarioPath = 'sample-api/scale-tests/scenarios/warmup.js'

        # ── Multi-run averaging ─────────────────────────────
        # Run the primary scenario this many times and take the median
        # of the results.  Reduces noise from run-to-run variance.
        MeasuredRuns = 5

        # Seconds to pause between consecutive measured runs.
        # Allows GC, thread pool, and TCP TIME_WAIT connections to settle.
        CooldownSeconds = 3    }

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
        # Log level: 'verbose', 'info', 'warning', 'error'
        Level      = 'info'
    }
}
