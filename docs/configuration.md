# Configuration Reference

All harness configuration lives in `harness/config.psd1`, a PowerShell data file.

## Full Reference

```powershell
@{
    # ── Target API ──────────────────────────────────────────────
    Api = @{
        # Path to the .NET solution file (relative to repo root)
        SolutionPath   = 'sample-api/SampleApi.sln'

        # Path to the API project directory (relative to repo root)
        ProjectPath    = 'sample-api/SampleApi'

        # Path to the E2E test project directory (relative to repo root)
        TestProjectPath = 'sample-api/SampleApi.Tests'

        # URL where the API listens when started
        BaseUrl        = 'http://localhost:5000'

        # Health check endpoint (GET, must return 200)
        HealthEndpoint = '/health'

        # Seconds to wait for API to become healthy after start
        StartupTimeout = 30
    }

    # ── Performance Thresholds ──────────────────────────────────
    Thresholds = @{
        # Target p95 latency in milliseconds
        P95LatencyMs     = 200

        # Minimum acceptable requests per second
        MinRequestsPerSec = 500

        # Maximum acceptable error rate (0.01 = 1%)
        MaxErrorRate     = 0.01

        # Maximum allowed p95 regression from previous iteration (0.10 = 10%)
        MaxRegressionPct = 0.10
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
```

## Settings Details

### Api.SolutionPath

Path to the `.sln` file, relative to the repository root. Used by `Build-SampleApi.ps1` for `dotnet build`.

### Api.ProjectPath

Path to the API project directory. Used by `Start-SampleApi.ps1` for `dotnet run --project`.

### Api.TestProjectPath

Path to the test project directory. Used by `Invoke-E2ETests.ps1` for `dotnet test`.

### Api.BaseUrl

The URL where the API listens. Passed to k6 as the `BASE_URL` environment variable and used by `Start-SampleApi.ps1` for health checking.

### Api.HealthEndpoint

Relative path appended to `BaseUrl` for startup health checks. The harness polls this endpoint until it returns HTTP 200 or the `StartupTimeout` is exceeded.

### Api.StartupTimeout

Maximum number of seconds to wait for the API to become healthy after `dotnet run` is started. If the timeout is exceeded, the iteration is aborted.

### Thresholds.P95LatencyMs

Target p95 (95th percentile) response time in milliseconds. When the measured p95 latency is at or below this value, the threshold is considered met.

### Thresholds.MinRequestsPerSec

Minimum sustained throughput target. Measured as the average requests per second during the k6 scenario.

### Thresholds.MaxErrorRate

Maximum acceptable HTTP error rate (non-2xx responses). Expressed as a decimal (e.g., `0.01` = 1%).

### Thresholds.MaxRegressionPct

Maximum allowed p95 latency regression from the previous iteration. If p95 increases by more than this percentage, the optimization is considered a regression and the branch is rolled back.

### ScaleTest.ScenarioPath

Path to the k6 JavaScript scenario file to execute on each measurement phase.

### ScaleTest.ExtraArgs

Array of additional command-line arguments passed to `k6 run`. For example: `@('--vus', '100', '--duration', '60s')`.

### Loop.MaxIterations

Maximum number of build-verify-measure-analyze-fix cycles. The loop stops after this many iterations even if performance targets haven't been fully met.

### Loop.BranchPrefix

Git branch naming prefix. Each iteration creates a branch named `{BranchPrefix}-{N}` (e.g., `autotune/iteration-1`).

## Overriding at Runtime

The main loop script accepts parameter overrides:

```powershell
# Override max iterations
.\harness\Invoke-AutotuneLoop.ps1 -MaxIterations 10

# Override the p95 target
.\harness\Invoke-AutotuneLoop.ps1 -P95TargetMs 150
```

Config file values serve as defaults; command-line parameters take precedence.
