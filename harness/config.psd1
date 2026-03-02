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
        StartupTimeout  = 30
    }

    # ── Performance Thresholds ──────────────────────────────────
    Thresholds = @{
        # Target p95 latency in milliseconds
        P95LatencyMs      = 200

        # Minimum acceptable requests per second
        MinRequestsPerSec = 500

        # Maximum acceptable error rate (0.01 = 1%)
        MaxErrorRate      = 0.01

        # Maximum allowed p95 regression from previous iteration (0.10 = 10%)
        MaxRegressionPct  = 0.10
    }

    # ── Scale Testing ───────────────────────────────────────────
    ScaleTest = @{
        # Path to the k6 scenario to run on each iteration
        ScenarioPath = 'scale-tests/scenarios/baseline.js'

        # Path to store k6 JSON summary output
        OutputPath   = 'results'

        # Additional k6 CLI arguments
        ExtraArgs    = @()
    }

    # ── Agentic Loop ───────────────────────────────────────────
    Loop = @{
        # Maximum number of optimization iterations
        MaxIterations = 5

        # Git branch prefix for optimization branches
        BranchPrefix  = 'autotune/iteration'
    }

    # ── Logging ─────────────────────────────────────────────────
    Logging = @{
        # Directory for log files (relative to repo root)
        OutputPath = 'results'

        # Log level: 'verbose', 'info', 'warning', 'error'
        Level      = 'info'
    }
}
