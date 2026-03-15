# Configuration

All harness configuration lives in [`harness/config.psd1`](../harness/config.psd1), a PowerShell data file with inline documentation for every setting.

The config file is the single source of truth — every option includes comments explaining its purpose, valid values, and defaults. Refer to the file directly rather than external docs.

## Key Configuration Areas

- **Api** — Solution path, project path, test project, base URL, health endpoint, results directory
- **Tolerances** — Regression threshold, improvement threshold, stale experiment limits, efficiency tiebreaker
- **ScaleTest** — Primary k6 scenario, scenario registry, warmup, measured runs, cooldown
- **Loop** — Max experiments, branch prefix, stacked diffs mode, wait-for-merge behavior
- **Copilot** — AI model selection and per-agent model overrides
- **DotnetCounters** — Runtime counter collection providers and sampling interval
- **Diagnostics** — Diagnostic profiling plugin framework (PerfView, analyzers)
- **Logging** — Log level

## Runtime Overrides

`Invoke-HoneLoop.ps1` exposes these command-line parameters that take precedence over config file values:

```powershell
# Override max experiments
.\harness\Invoke-HoneLoop.ps1 -MaxExperiments 10

# Use a different config file
.\harness\Invoke-HoneLoop.ps1 -ConfigPath .\my-config.psd1

# Dry-run mode: skip k6 scale tests, API start/stop, database reset,
# and diagnostic profiling.  AI agents, build, and E2E tests still run
# normally.  PRs are created with a [DRY RUN] prefix.
.\harness\Invoke-HoneLoop.ps1 -DryRun -MaxExperiments 3
```

### DryRun Mode

When `-DryRun` is specified:

| Component | Behavior |
|-----------|----------|
| Build (`dotnet build`) | Runs normally |
| E2E tests (`dotnet test`) | Runs normally |
| k6 scale tests | **Skipped** — synthetic metrics used (5% improvement) |
| API start/stop | **Skipped** |
| Database reset | **Skipped** |
| Diagnostic profiling (PerfView) | **Skipped** |
| AI agents (analyst, classifier, fixer) | Run normally |
| PRs | Created with `[DRY RUN]` prefix |

Dry-run mode is useful for testing the AI pipeline and branch management without waiting for full load tests.

To change any other setting (tolerances, scale-test options, model selection, etc.), edit `config.psd1` directly.

## Diagnostics Configuration

The `Diagnostics` section controls the diagnostic profiling plugin framework. This is separate from the evaluation measurement (ScaleTest + DotnetCounters) used for accept/reject decisions.

Collectors are organized into **groups** — collectors in the same group run together in one diagnostic pass, while different groups get separate passes (each with its own API instance + k6 run). This prevents interfering collectors (e.g., PerfView CPU sampling vs `/GCOnly` mode) from corrupting each other's data.

```powershell
Diagnostics = @{
    Enabled            = $true                    # Master switch
    CollectorsPath     = 'harness/collectors'     # Plugin directory for collectors
    AnalyzersPath      = 'harness/analyzers'      # Plugin directory for analyzers
    PerfViewExePath    = 'tools/PerfView/PerfView.exe'  # Downloaded by Setup-DevEnvironment.ps1
    DiagnosticScenarioPath = $null                # k6 scenario ($null = use ScaleTest.ScenarioPath)
    DiagnosticRuns     = 1                        # Runs per pass (accuracy less important)

    CollectorSettings = @{
        'perfview-cpu'    = @{ Enabled = $true; MaxCollectSec = 90; BufferSizeMB = 256; MaxStacks = 100 }
        'perfview-gc'     = @{ Enabled = $true; MaxCollectSec = 90; BufferSizeMB = 256 }
        'dotnet-counters' = @{ Enabled = $true }
    }

    AnalyzerSettings = @{
        'cpu-hotspots' = @{ Enabled = $true; Model = 'claude-opus-4.6'; MaxStacks = 100 }
        'memory-gc'    = @{ Enabled = $true; Model = 'claude-opus-4.6' }
    }
}
```

**Collection groups** are defined in each collector's `collector.psd1` via the `Group` field:
- `perfview-cpu` → group `etw-cpu` (CPU sampling + allocation ticks via `/DotNetAllocSampled`)
- `perfview-gc` → group `etw-gc` (GC-only mode, suppresses CPU sampling)
- `dotnet-counters` → group `default` (runs in every pass, lightweight)

**Important**: PerfView requires **Administrator privileges** for kernel-level CPU sampling. Run the harness in an elevated terminal. PerfView is downloaded automatically by `Setup-DevEnvironment.ps1`.

To disable diagnostic profiling entirely, set `Diagnostics.Enabled = $false`. Individual collectors and analyzers can be disabled independently via their `Enabled` flag.

## Configuration Interactions

Some configuration combinations interact in non-obvious ways:

| Setting A | Setting B | Interaction |
|-----------|-----------|-------------|
| `StackedDiffs = $true` | `WaitForMerge = $true` | Works but defeats the purpose of stacked diffs — each PR blocks the loop until merged |
| `Diagnostics.Enabled = $true` | `Diagnostics.DiagnosticRuns = 0` | Collectors start but no k6 load test data is collected during the diagnostic pass |
| `ScaleTest.MeasuredRuns = 1` | `Tolerances.MaxRegressionPct < 0.05` | A single run produces noisy metrics that may exceed tight tolerances, causing false regressions |
| `Tolerances.MaxConsecutiveFailures` | `MaxExperiments` | If MaxConsecutiveFailures ≥ MaxExperiments, the consecutive failure limit never triggers |
| `Diagnostics.Enabled = $true` | Running without admin | PerfView requires Administrator privileges for kernel-level CPU sampling — diagnostic collection will fail |

The `Test-HoneConfig.ps1` script detects some of these interactions and emits warnings at startup.
