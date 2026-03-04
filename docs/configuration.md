# Configuration Reference

All harness configuration lives in `harness/config.psd1`, a PowerShell data file.

## Full Reference

```powershell
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
        # Minimum improvement (any single metric) to accept an iteration (0.01 = 1%)
        MinImprovementPct = 0.01

        # Maximum regression allowed per metric before rejecting (0.02 = 2%)
        MaxRegressionPct  = 0.02

        # Stop after this many consecutive iterations with no improvement
        StaleIterationsBeforeStop = 2

        # ── Efficiency Tiebreaker ────────────────────────────────
        # When performance metrics are flat (no improvement, no regression),
        # accept the iteration if OS-level resource usage decreased.
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

Maximum number of seconds to wait for the API to become healthy after `dotnet run` is started. Default is 90 seconds. If the timeout is exceeded, the iteration is aborted.

### Tolerances.MinImprovementPct

Minimum relative improvement required in any single metric (p95 latency, RPS, or error rate) to accept an iteration. Expressed as a decimal (e.g., `0.01` = 1%). If no metric improves by at least this amount, the iteration is considered stale.

### Tolerances.MaxRegressionPct

Maximum allowed regression per metric before rejecting an iteration. Expressed as a decimal (e.g., `0.02` = 2%). If any metric regresses beyond this threshold, the optimization branch is rolled back.

### Tolerances.StaleIterationsBeforeStop

Number of consecutive iterations with no meaningful improvement before the loop stops early. Prevents wasting cycles when the optimization surface is exhausted.

### Tolerances.Efficiency

Efficiency tiebreaker settings, used when performance metrics are flat (neither improving nor regressing). When enabled, an iteration can still be accepted if it reduces resource usage.

- **Enabled** — Toggle the efficiency tiebreaker on or off.
- **MinCpuReductionPct** — Minimum reduction in average CPU usage to count as an efficiency gain (e.g., `0.05` = 5%).
- **MinWorkingSetReductionPct** — Minimum reduction in peak working set to count as an efficiency gain (e.g., `0.05` = 5%).

### ScaleTest.ScenarioPath

Path to the primary k6 JavaScript scenario file used for optimization measurements on each iteration.

### ScaleTest.ScenarioRegistryPath

Path to the JSON file listing all available scenarios and their metadata (descriptions, file paths, thresholds, and `use_for_optimization` flags). Used by `Invoke-AllScaleTests.ps1` to run the full diagnostic suite during baseline collection.

### Api.ResultsPath

Directory where all performance results are stored (relative to repo root). Baselines and k6 summaries are committed; counters and operational files are gitignored. Each iteration's artifacts are stored in an `iteration-{N}/` subdirectory.

### Api.MetadataPath

Directory for optimization metadata (relative to repo root). Defaults to `sample-api/results/metadata`. Contains two auto-generated, git-tracked markdown files:

- **`optimization-log.md`** — append-only ledger recording each optimization proposal with its iteration, target file, summary, and outcome (improved / regressed / stale / pending).
- **`optimization-queue.md`** — ranked list of potential optimizations discovered by Copilot. Items start unchecked (`- [ ]`) and are marked `- [x]` when tried, with a back-reference to the iteration and outcome.

Each iteration also produces a `root-cause.md` in its subfolder (`iteration-{N}/root-cause.md`), containing a concise root cause analysis with the performance issue, rationale, and proposed fix details.

### ScaleTest.ExtraArgs

Array of additional command-line arguments passed to `k6 run`. For example: `@('--vus', '100', '--duration', '60s')`.

### DotnetCounters.Enabled

Toggle .NET performance counter collection during scale tests. When enabled, `dotnet-counters collect` runs alongside k6, capturing runtime metrics for analysis.

### DotnetCounters.Providers

Array of .NET event counter providers to collect. Default providers:

- `System.Runtime` — GC, thread pool, exception count
- `Microsoft.AspNetCore.Hosting` — Request rate, duration
- `Microsoft.AspNetCore.Http.Connections` — Connection metrics
- `System.Net.Http` — Outbound HTTP client metrics

### DotnetCounters.RefreshIntervalSeconds

Sampling interval in seconds for `dotnet-counters collect`. Lower values give finer granularity but more data.

### Loop.MaxIterations

Maximum number of build-verify-measure-analyze-fix cycles. The loop stops after this many iterations even if further improvements are possible.

### Loop.BranchPrefix

Git branch naming prefix. Each iteration creates a branch named `{BranchPrefix}-{N}` (e.g., `hone/iteration-1`).

## Overriding at Runtime

The main loop script accepts parameter overrides:

```powershell
# Override max iterations
.\harness\Invoke-HoneLoop.ps1 -MaxIterations 10
```

Config file values serve as defaults; command-line parameters take precedence.
