# Configuration

Hone uses a **three-layer YAML configuration** hierarchy. Engine defaults ship with the harness; each target project provides its own overrides; CLI flags override everything at runtime.

## Config Merge Order

```
1. Engine defaults       (harness-csharp/config.yaml — tolerances, models, loop)
        ↓ merged with
2. Target .hone/config.yaml   (paths, hooks, scenarios, optional overrides)
        ↓ merged with
3. CLI flags             (--max-experiments, --dry-run, --model, etc.)
```

Target-level overrides are section-level: a target that overrides `Api.BaseUrl` does not erase other engine `Api` settings. CLI flags override everything.

All YAML keys use **PascalCase** to match C# record property names, which `YamlDotNet` deserializes via its `PascalCaseNamingConvention`.

## Key Configuration Sections

- **Api** — Solution path, project path, test project, base URL, health endpoint, results directory, source code paths
- **Tolerances** — Regression threshold, improvement threshold, stale experiment limits, efficiency tiebreaker, absolute delta thresholds
- **ScaleTest** — Primary k6 scenario, scenario registry, warmup, measured runs, cooldown
- **Loop** — Max experiments, branch prefix, stacked diffs mode, wait-for-merge behavior, skip classification
- **Agents** — AI model selection, per-agent model overrides, agent timeout
- **Hooks** — Lifecycle hooks for build, test, start/stop, measurement phases. See [Lifecycle Hooks](hooks.md) for the full reference.
- **Implementer** — Max implementer attempts, diff growth guard, test file guard
- **DotnetCounters** — Runtime counter collection providers and sampling interval
- **Diagnostics** — Diagnostic profiling plugin framework (PerfView, analyzers). Targets can override to disable platform-specific collectors.
- **Logging** — Log level, log rotation

### Notable Settings

**Agents**

| Key | Default | Description |
|-----|---------|-------------|
| `Agents.AgentTimeoutSec` | `1800` | Maximum seconds for any Copilot CLI agent invocation. If exceeded, the process is killed and the experiment is rejected. |
| `Agents.AnalysisModel` | `claude-opus-4.6` | Model used for the top-level analysis agent |
| `Agents.ClassificationModel` | `claude-opus-4.6` | Model used for scope classification |
| `Agents.ImplementerModel` | `claude-sonnet-4.6` | Model used for code generation |

**Tolerances**

| Key | Default | Description |
|-----|---------|-------------|
| `Tolerances.MinAbsoluteRPSDelta` | `5` | Minimum absolute RPS change required to register as improvement or regression. Prevents false positives on low-traffic scenarios. |
| `Tolerances.MinAbsoluteErrorRateDelta` | `0.005` | Minimum absolute error rate change (0.005 = 0.5%). Prevents false positives on near-zero error baselines. |

**Logging**

| Key | Default | Description |
|-----|---------|-------------|
| `Logging.MaxFileSizeMB` | `50` | Maximum size of `hone.jsonl` before log rotation. When exceeded, the file is renamed to `hone.jsonl.1` and a new file is started. |

## Full Engine Default Schema

The engine defaults (`harness-csharp/config.yaml`) document every setting with inline comments. Here is the full structure:

```yaml
Api:
  SolutionPath: "sample-api/SampleApi.sln"
  ProjectPath: "sample-api/SampleApi"
  SourceCodePaths:
    - Controllers
    - Data
    - Models
    - Pages
  SourceFileGlob: "*.cs"
  TestProjectPath: "sample-api/SampleApi.Tests"
  BaseUrl: "http://localhost:0"
  HealthEndpoint: "/health"
  GcEndpoint: "/diag/gc"
  StartupTimeout: 90
  ResultsPath: "sample-api/.hone/results"
  MetadataPath: "sample-api/.hone/results/metadata"

Tolerances:
  MaxRegressionPct: 0.10
  MinAbsoluteP95DeltaMs: 5
  MinAbsoluteRPSDelta: 5
  MinAbsoluteErrorRateDelta: 0.005
  MinImprovementPct: 0
  StaleExperimentsBeforeStop: 2
  MaxConsecutiveFailures: 10
  Efficiency:
    Enabled: true
    MinCpuReductionPct: 0.05
    MinWorkingSetReductionPct: 0.05

ScaleTest:
  ScenarioPath: "sample-api/scale-tests/scenarios/baseline.js"
  ScenarioRegistryPath: "sample-api/scale-tests/thresholds.json"
  ExtraArgs: []
  WarmupEnabled: true
  WarmupScenarioPath: "sample-api/scale-tests/scenarios/warmup.js"
  MeasuredRuns: 5
  CooldownSeconds: 3

Loop:
  MaxExperiments: 999
  BranchPrefix: "hone/experiment"
  StackedDiffs: true
  WaitForMerge: false
  SkipClassification: false

Agents:
  DefaultModel: "claude-sonnet-4.5"
  AnalysisModel: "claude-opus-4.6"
  ClassificationModel: "claude-opus-4.6"
  ImplementerModel: "claude-sonnet-4.6"
  AgentTimeoutSec: 1800

Implementer:
  MaxAttempts: 3
  MaxDiffGrowthFactor: 3.0
  TestFileGuard: true

Logging:
  Level: "info"
  MaxFileSizeMB: 50

DotnetCounters:
  Enabled: true
  Providers:
    - System.Runtime
    - Microsoft.AspNetCore.Hosting
    - Microsoft.AspNetCore.Http.Connections
    - System.Net.Http
  RefreshIntervalSeconds: 1

Diagnostics:
  Enabled: true
  CollectorsPath: "harness-csharp/plugins/collectors"
  AnalyzersPath: "harness-csharp/plugins/analyzers"
  PerfViewExePath: "tools/PerfView/PerfView.exe"
  DiagnosticScenarioPath: null
  DiagnosticRuns: 1
  K6TimeoutSec: 300
  CollectorSettings:
    perfview-cpu:
      Enabled: true
      MaxCollectSec: 150
      StopTimeoutSec: 600
      ExportTimeoutSec: 600
      BufferSizeMB: 256
      MaxStacks: 100
    perfview-gc:
      Enabled: true
      MaxCollectSec: 150
      StopTimeoutSec: 600
      ExportTimeoutSec: 600
      BufferSizeMB: 256
    dotnet-counters:
      Enabled: true
  AnalyzerSettings:
    cpu-hotspots:
      Enabled: true
      Model: "claude-opus-4.6"
      MaxStacks: 100
    memory-gc:
      Enabled: true
      Model: "claude-opus-4.6"
```

## Target Config Example (`.hone/config.yaml`)

A target project only needs to override what differs from the engine defaults:

```yaml
# .hone/config.yaml — target-specific overrides
Name: "eShopOnWeb"
BaseBranch: "main"

Api:
  SolutionPath: "eShopOnWeb.sln"
  ProjectPath: "src/PublicApi"
  TestProjectPath: "tests/FunctionalTests"
  ResultsPath: ".hone/results"
  MetadataPath: ".hone/results/metadata"
  BaseUrl: "http://localhost:0"
  HealthEndpoint: "/health"
  GcEndpoint: "/diag/gc"
  StartupTimeout: 120
  SourceCodePaths:
    - src/PublicApi
    - src/ApplicationCore
    - src/Infrastructure
  SourceFileGlob: "*.cs"

Hooks:
  Prepare:
    Type: BuiltIn
    Name: sqlserver-reset
  Start:
    Type: BuiltIn
    Name: dotnet-start
  Ready:
    Type: BuiltIn
    Name: health-poll
  Warmup:
    Type: Skip
  Active:
    Type: BuiltIn
    Name: k6-run
  Cooldown:
    Type: Http
    Method: POST
    Path: /diag/gc
  Stop:
    Type: BuiltIn
    Name: dotnet-stop
  Cleanup:
    Type: Skip

ScaleTest:
  ScenarioPath: ".hone/scenarios/baseline.js"
  ScenarioRegistryPath: ".hone/scenarios/thresholds.json"
  WarmupEnabled: true
  WarmupScenarioPath: ".hone/scenarios/warmup.js"
  MeasuredRuns: 5
  CooldownSeconds: 5

# Optional: override engine loop defaults
Loop:
  MaxExperiments: 20

# Optional: override engine tolerance defaults
Tolerances:
  MaxRegressionPct: 0.10
  MinImprovementPct: 0

# Optional: disable inapplicable diagnostic collectors
# (PerfView is Windows-only, dotnet-counters requires .NET runtime)
# Diagnostics:
#   CollectorSettings:
#     perfview-cpu:
#       Enabled: false
#     perfview-gc:
#       Enabled: false
```

## Runtime Overrides (CLI Flags)

`hone run` exposes CLI flags that take precedence over all config file values:

```sh
# Override max experiments
hone run --target sample-api --max-experiments 10

# Dry-run mode: skip k6 scale tests, API start/stop, database reset,
# and diagnostic profiling. AI agents, build, and E2E tests still run
# normally. PRs are created with a [DRY RUN] prefix.
hone run --target sample-api --dry-run --max-experiments 3

# Override the AI model for all agents
hone run --target sample-api --model claude-sonnet-4.5
```

### DryRun Mode

When `--dry-run` is specified:

| Component | Behavior |
|-----------|----------|
| Build (`dotnet build`) | Runs normally |
| E2E tests (`dotnet test`) | Runs normally |
| k6 scale tests | **Skipped** — synthetic metrics used (5% improvement) |
| API start/stop | **Skipped** |
| Database reset | **Skipped** |
| Diagnostic profiling (PerfView) | **Skipped** |
| AI agents (analyst, classifier, implementer) | Run normally |
| PRs | Created with `[DRY RUN]` prefix |

Dry-run mode is useful for testing the AI pipeline and branch management without waiting for full load tests.

## Model Cost Optimization

The `Agents` section assigns different models to each agent based on task complexity. This tiered strategy balances analysis quality with cost:

```yaml
Agents:
  DefaultModel: "claude-sonnet-4.5"    # Global default
  AnalysisModel: "claude-opus-4.6"     # hone-analyst (deep reasoning)
  ClassificationModel: "claude-opus-4.6" # hone-classifier (scope decisions require strong reasoning)
  ImplementerModel: "claude-sonnet-4.6"  # hone-implementer (code generation)
```

**Why this matters:** In a full run, Opus accounts for ~94% of total cost because it handles analysis and profiling agents with large context windows. Using a cheaper model for classification and a mid-tier model for code generation reduces overall cost without sacrificing critical quality.

The Copilot CLI automatically caches input prompts, achieving ~88% cache hit rates in practice. This reduces effective costs significantly, especially for the analyst agent which receives large, slowly-changing context (source files, optimization history).

**Tuning tips:**
- If cost is a concern, switch `AnalysisModel` to `claude-sonnet-4.5` — analysis quality decreases but cost drops dramatically
- If quality is paramount, use `claude-opus-4.6` for `ImplementerModel` as well — but expect higher code generation costs
- The profiler agents (`cpu-hotspots`, `memory-gc`) have separate model overrides under `Diagnostics.AnalyzerSettings` — these can also be downgraded independently

See [Agent Designs — Observed Cost Breakdown](agent-designs.md#observed-cost-breakdown) for detailed per-model token usage from a real run.

## Diagnostics Configuration

The `Diagnostics` section controls the diagnostic profiling plugin framework. This is separate from the evaluation measurement (ScaleTest + DotnetCounters) used for accept/reject decisions.

Collectors are organized into **groups** — collectors in the same group run together in one diagnostic pass, while different groups get separate passes (each with its own API instance + k6 run). This prevents interfering collectors (e.g., PerfView CPU sampling vs `/GCOnly` mode) from corrupting each other's data.

```yaml
Diagnostics:
  Enabled: true
  CollectorsPath: "harness-csharp/plugins/collectors"
  AnalyzersPath: "harness-csharp/plugins/analyzers"
  PerfViewExePath: "tools/PerfView/PerfView.exe"
  DiagnosticScenarioPath: null        # null = reuse ScaleTest.ScenarioPath
  DiagnosticRuns: 1
  K6TimeoutSec: 300

  CollectorSettings:
    perfview-cpu:
      Enabled: true
      MaxCollectSec: 90
      BufferSizeMB: 256
      MaxStacks: 100
    perfview-gc:
      Enabled: true
      MaxCollectSec: 90
      BufferSizeMB: 256
    dotnet-counters:
      Enabled: true

  AnalyzerSettings:
    cpu-hotspots:
      Enabled: true
      Model: "claude-opus-4.6"
      MaxStacks: 100
    memory-gc:
      Enabled: true
      Model: "claude-opus-4.6"
```

**Collection groups** are defined in each collector's `collector.yaml` metadata via the `Group` field:
- `perfview-cpu` → group `etw-cpu` (CPU sampling + allocation ticks via `/DotNetAllocSampled`)
- `perfview-gc` → group `etw-gc` (GC-only mode, suppresses CPU sampling)
- `dotnet-counters` → group `default` (runs in every pass, lightweight)

**Important**: PerfView requires **Administrator privileges** for kernel-level CPU sampling. Run the harness in an elevated terminal.

**Notable settings:**

| Key | Default | Description |
|-----|---------|-------------|
| `Diagnostics.K6TimeoutSec` | `300` | Maximum seconds for k6 diagnostic runs. If exceeded, the process is killed. |

To disable diagnostic profiling entirely, set `Diagnostics.Enabled: false`. Individual collectors and analyzers can be disabled independently via their `Enabled` flag.

## Configuration Validation

The `ConfigValidator` class validates configuration at startup (also exposed via `hone validate --target <path>`). It checks:

- Tolerance percentages are in `[0, 1]` range
- File paths (solution, project, scenarios) exist
- Port numbers are valid (0 for dynamic, or 1–65535)
- Required tools are installed (`dotnet`, `k6`, `git`, `copilot`, `gh`)
- Numeric settings are within reasonable ranges
- Hook types reference valid built-in hooks or command strings

The main loop runs this automatically. To validate manually:

```sh
hone validate --target sample-api
```

Validation errors halt the loop before any experiments run. Warnings (e.g., non-obvious setting interactions) are displayed but don't block execution.

## Configuration Interactions

Some configuration combinations interact in non-obvious ways:

| Setting A | Setting B | Interaction |
|-----------|-----------|-------------|
| `Loop.StackedDiffs: true` | `Loop.WaitForMerge: true` | Works but defeats the purpose of stacked diffs — each PR blocks the loop until merged |
| `Diagnostics.Enabled: true` | `Diagnostics.DiagnosticRuns: 0` | Collectors start but no k6 load test data is collected during the diagnostic pass |
| `ScaleTest.MeasuredRuns: 1` | `Tolerances.MaxRegressionPct: 0.05` | A single run produces noisy metrics that may exceed tight tolerances, causing false regressions |
| `Tolerances.MaxConsecutiveFailures` | `Loop.MaxExperiments` | If MaxConsecutiveFailures ≥ MaxExperiments, the consecutive failure limit never triggers |
| `Diagnostics.Enabled: true` | Running without admin | PerfView requires Administrator privileges for kernel-level CPU sampling — diagnostic collection will fail |

The `ConfigValidator` detects some of these interactions and emits warnings at startup.

