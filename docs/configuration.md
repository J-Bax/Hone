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

        # Subdirectories to scan for source code context (relative to ProjectPath)
        SourceCodePaths = @('Controllers')

        # File pattern for source files to include in analysis prompts
        SourceFileGlob  = '*.cs'

        # Path to the E2E test project directory (relative to repo root)
        TestProjectPath = 'sample-api/SampleApi.Tests'

        # URL where the API listens when started
        BaseUrl         = 'http://localhost:5000'

        # Health check endpoint (GET, must return 200)
        HealthEndpoint  = '/health'

        # Optional endpoint (POST) to trigger server-side GC between runs
        GcEndpoint      = '/diag/gc'

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

        # Stop after this many consecutive unsuccessful iterations
        # (stale + regression combined).  Used by stacked-diffs mode.
        # Falls back to StaleIterationsBeforeStop when not set.
        MaxConsecutiveFailures = 10

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
        # ── Warmup ──────────────────────────────────────────
        # Run a short 1-VU warmup pass before the measured run to ensure
        # the application is fully warmed up before measured runs.
        WarmupEnabled      = $true
        WarmupScenarioPath = 'sample-api/scale-tests/scenarios/warmup.js'

        # ── Multi-run averaging ─────────────────────────────
        # Run the primary scenario this many times and take the median
        # of the results.  Reduces noise from run-to-run variance.
        MeasuredRuns = 5

        # Seconds to pause between consecutive measured runs.
        # Allows GC, thread pool, and TCP TIME_WAIT connections to settle.
        CooldownSeconds = 3
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

        # Stacked diffs: each iteration branches from the previous one,
        # forming a linear chain.  PRs compare N+1 against the last
        # successful iteration instead of master.
        # When $false (legacy mode): each iteration branches from master
        # and PRs target master directly.
        StackedDiffs  = $true

        # When $false the loop creates PRs and continues immediately
        # (fire-and-forget).  When $true the loop blocks until each PR
        # is merged before starting the next iteration.
        # In stacked mode $false is recommended; $true is the legacy
        # behaviour when StackedDiffs = $false.
        WaitForMerge  = $false
    }

    # ── Copilot CLI ─────────────────────────────────────────────
    Copilot = @{
        # Default AI model for all agents (see 'copilot --help' for choices)
        Model = 'claude-opus-4.6'

        # Per-agent model overrides (null = use default Model above)
        AnalysisModel       = $null
        ClassificationModel = 'claude-haiku-4.5'   # faster for binary scope decisions
        FixModel            = $null
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

### Api.GcEndpoint

Optional endpoint (POST) to trigger server-side garbage collection between runs. Called between measured runs to reduce GC noise in measurements. Defaults to `/diag/gc`.

### Api.SourceCodePaths

Array of subdirectories (relative to `ProjectPath`) to scan when gathering source code context for Copilot analysis prompts. Defaults to `@('Controllers')`.

### Api.SourceFileGlob

Glob pattern for source files to include in analysis prompts. Only files matching this pattern inside `SourceCodePaths` are sent as context. Defaults to `*.cs`.

### Api.StartupTimeout

Maximum number of seconds to wait for the API to become healthy after `dotnet run` is started. Default is 90 seconds. If the timeout is exceeded, the iteration is aborted.

### Tolerances.MinImprovementPct

Minimum relative improvement required in any single metric (p95 latency, RPS, or error rate) to accept an iteration. Expressed as a decimal (e.g., `0.03` = 3%). Must exceed the measurement noise floor to be meaningful. If no metric improves by at least this amount, the iteration is considered stale.

### Tolerances.MaxRegressionPct

Maximum allowed regression per metric before rejecting an iteration. Expressed as a decimal (e.g., `0.10` = 10%). The tolerance is set relatively high because the harness uses pure steady-state measurement with median-of-5 runs, GC settling, and cooldowns between runs. If any metric regresses beyond this threshold, the optimization branch is rolled back.

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

### ScaleTest.WarmupEnabled

Toggle the warmup pass before measured runs. When enabled, a short 1-VU warmup scenario is run to ensure the application (JIT, connection pools, caches) is fully warmed up before measurement begins.

### ScaleTest.WarmupScenarioPath

Path to the k6 scenario used for the warmup pass. Only used when `WarmupEnabled` is `$true`. Defaults to `sample-api/scale-tests/scenarios/warmup.js`.

### ScaleTest.MeasuredRuns

Number of times to run the primary scenario per iteration. The harness takes the median of all runs to reduce noise from run-to-run variance. Defaults to `5`.

### ScaleTest.CooldownSeconds

Seconds to pause between consecutive measured runs. Allows GC, the thread pool, and TCP TIME_WAIT connections to settle before the next run. Defaults to `3`.

### Copilot.Model

Default AI model used by all Copilot agents (analysis, classification, fix). See `copilot --help` for available model choices. Defaults to `claude-opus-4.6`.

### Copilot.AnalysisModel

Model override for the analysis agent. When `$null`, falls back to `Copilot.Model`.

### Copilot.ClassificationModel

Model override for the classification agent, which makes binary scope decisions (e.g., is this optimization in scope?). Defaults to `claude-haiku-4.5` for faster turnaround on simple decisions.

### Copilot.FixModel

Model override for the fix agent, which generates code changes. When `$null`, falls back to `Copilot.Model`.

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

### Loop.StackedDiffs

When `$true` (default), iterations form a **linear branch chain**. Each iteration branches from the previous one (whether it succeeded or failed). Successful iterations get PRs that compare against the last successful iteration branch. Failed iterations have their code reverted but their artifacts preserved, and the branch is pushed for the record.

When `$false` (legacy mode), each iteration branches from `master`, PRs target `master`, and the loop waits for merge between iterations.

### Loop.WaitForMerge

Controls whether the loop blocks waiting for each PR to be merged before starting the next iteration.

- `$false` (default) — Fire-and-forget: create the PR and continue immediately. Recommended with `StackedDiffs = $true`.
- `$true` — Block until the PR is merged or closed. This is the legacy behavior when `StackedDiffs = $false`.

### Tolerances.MaxConsecutiveFailures

Maximum consecutive unsuccessful iterations (stale + regression combined) before the loop stops. Used in stacked-diffs mode where regressions no longer immediately abort the loop. Falls back to `StaleIterationsBeforeStop` when not set. Default is `10`.

## Overriding at Runtime

The main loop script accepts parameter overrides:

```powershell
# Override max iterations
.\harness\Invoke-HoneLoop.ps1 -MaxIterations 10
```

Config file values serve as defaults; command-line parameters take precedence.
