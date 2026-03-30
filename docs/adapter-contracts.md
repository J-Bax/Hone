# Adapter Contracts — `.hone/` Target Interface Specification

> **Audience:** Anyone adding Hone support to a project.
> **Status:** Phase 1 implemented — covers .NET targets with lifecycle hooks, shared
> hook catalog, and config merge. Extensible to other stacks via custom `Script` hooks.

---

## 1. Overview

Hone is a **read-only execution engine**. It contains zero target-specific
configuration. Instead, each target project describes itself via a `.hone/`
directory — similar to how Docker reads a `Dockerfile` or GitHub Actions reads
`.github/workflows/`.

**Invocation:**

```powershell
Invoke-HoneLoop.ps1 -TargetPath C:\Projects\eShopOnWeb
Invoke-HoneLoop.ps1 -TargetPath .\sample-api            # relative paths work
```

**Config merge order** (lower layers override higher):

```
1. Hone engine defaults       (harness/config.psd1 — tolerances, models, loop)
        ↓ merged with
2. Target .hone/config.psd1   (paths, hooks, scenarios, optional overrides)
        ↓ merged with
3. CLI flags                   (-MaxExperiments, -DryRun, -Model, etc.)
```

A target can override Hone's default tolerance thresholds or loop settings.
CLI flags override everything — useful for quick experiments.

---

## 2. `.hone/` Directory Structure

```
<target-repo>/
└── .hone/
    ├── config.psd1              # REQUIRED — target configuration
    ├── hooks/                   # REQUIRED — lifecycle hook scripts
    │   ├── prepare.ps1          # reset DB, clear state before measurement
    │   └── ...                  # additional hook scripts as needed
    ├── scenarios/               # REQUIRED — k6 load test scenarios
    │   ├── baseline.js          # primary k6 scenario (used for optimization)
    │   ├── warmup.js            # warmup scenario (if WarmupEnabled = $true)
    │   └── thresholds.json      # scenario registry with pass/fail thresholds
    └── context.md               # OPTIONAL — project-specific AI hints
```

**All files are required except `context.md`.** Hooks declared as `Skip` in
config don't need corresponding script files. This ensures:

- Every target has been deliberately configured — no silent fallback to defaults
  that may not be appropriate
- Hone can validate the full `.hone/` contract at startup and fail fast with
  clear errors
- New targets must think through each lifecycle phase, even if the answer is
  "not needed"

The only optional file is `context.md` — project-specific hints that AI agents
use for better optimization suggestions. If absent, agents operate with Hone's
built-in prompts only.

---

## 3. `config.psd1` Schema

```powershell
@{
    # ── Target Identity ──────────────────────────────────────────
    Name       = 'eShopOnWeb'        # Used in branch names, logs, PR titles
    BaseBranch = 'main'              # Experiments branch from here

    # ── API Project Layout ───────────────────────────────────────
    Api = @{
        SolutionPath     = 'eShopOnWeb.sln'           # path to .sln (relative to target root)
        ProjectPath      = 'src\PublicApi'             # runnable project directory
        TestProjectPath  = 'tests\FunctionalTests'    # E2E / regression test project
        ResultsPath      = 'results'                  # harness output directory
        MetadataPath     = 'results\metadata'          # experiment metadata
        BaseUrl          = 'http://localhost:0'        # port 0 = ephemeral
        HealthEndpoint   = '/health'                   # readiness probe path
        GcEndpoint       = '/diag/gc'                  # GC trigger endpoint
        StartupTimeout   = 120                         # seconds to wait for health check
        SourceCodePaths  = @('src\PublicApi', 'src\ApplicationCore', 'src\Infrastructure')
        SourceFileGlob   = '*.cs'                      # file pattern for AI analysis scope
    }

    # ── Lifecycle Hooks ──────────────────────────────────────────
    # ALL 8 hooks must be declared. Use Type = 'Skip' for phases not needed.
    # Available types: Script, Shared, Command, Http, Skip
    Hooks = @{
        Prepare  = @{ Type = 'Script'; Path = '.hone\hooks\prepare.ps1' }
        Start    = @{ Type = 'Shared'; Name = 'dotnet-start' }
        Ready    = @{ Type = 'Shared'; Name = 'health-poll' }
        Warmup   = @{ Type = 'Skip' }
        Active   = @{ Type = 'Shared'; Name = 'k6-run' }
        Cooldown = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
        Stop     = @{ Type = 'Shared'; Name = 'dotnet-stop' }
        Cleanup  = @{ Type = 'Skip' }
    }

    # ── Scale Test Configuration ─────────────────────────────────
    ScaleTest = @{
        ScenarioPath         = '.hone\scenarios\baseline.js'      # primary k6 scenario
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'  # pass/fail thresholds
        WarmupEnabled        = $true                               # run warmup before active
        WarmupScenarioPath   = '.hone\scenarios\warmup.js'         # required if WarmupEnabled
        MeasuredRuns         = 5                                   # repetitions per measurement
        CooldownSeconds      = 5                                   # pause between runs
    }

    # ── Optional: Runtime Counter Config ─────────────────────────
    # DotnetCounters = @{
    #     Providers = @('System.Runtime', 'Microsoft.AspNetCore.Hosting')
    # }

    # ── Optional: Override Hone Engine Defaults ──────────────────
    # Tolerances = @{
    #     MaxRegressionPct  = 0.10      # reject if p95 regresses > 10%
    #     MinImprovementPct = 0.05      # accept if p95 improves > 5%
    # }
    # Loop = @{
    #     MaxExperiments = 6            # max optimization attempts
    # }
}
```

### Key Reference

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `Name` | string | ✅ | Target identity — used in branch names, logs, and PR titles |
| `BaseBranch` | string | ✅ | Git branch experiments branch from |
| `Api.SolutionPath` | string | ✅ | Path to solution/project file, relative to target root |
| `Api.ProjectPath` | string | ✅ | Runnable API project directory |
| `Api.TestProjectPath` | string | ✅ | E2E / regression test project directory |
| `Api.ResultsPath` | string | ✅ | Directory for harness output (created at runtime) |
| `Api.MetadataPath` | string | ✅ | Subdirectory for experiment metadata |
| `Api.BaseUrl` | string | ✅ | Base URL for the API (`http://localhost:0` for ephemeral port) |
| `Api.HealthEndpoint` | string | ✅ | Health check path polled by the `ready` hook |
| `Api.GcEndpoint` | string | ✅ | GC trigger endpoint used by `cooldown` |
| `Api.StartupTimeout` | int | ✅ | Seconds to wait for health check before failing |
| `Api.SourceCodePaths` | string[] | ✅ | Directories the AI agent analyzes for optimizations |
| `Api.SourceFileGlob` | string | ✅ | File pattern within source code paths (e.g., `*.cs`) |
| `Hooks.*` | hashtable | ✅ | All 8 lifecycle hooks — see [Hook Types](#4-hook-types) |
| `ScaleTest.ScenarioPath` | string | ✅ | Primary k6 scenario file |
| `ScaleTest.ScenarioRegistryPath` | string | ✅ | Threshold definitions for pass/fail |
| `ScaleTest.WarmupEnabled` | bool | ✅ | Whether to run warmup before active measurement |
| `ScaleTest.WarmupScenarioPath` | string | conditional | Required if `WarmupEnabled = $true` |
| `ScaleTest.MeasuredRuns` | int | ✅ | Number of measurement repetitions |
| `ScaleTest.CooldownSeconds` | int | ✅ | Seconds to pause between runs |
| `ScaleTest.ExtraArgs` | string[] | ❌ | Additional k6 CLI arguments (e.g., `@('--tag', 'env=staging')`) |
| `DotnetCounters` | hashtable | ❌ | Runtime counter providers (optional) |
| `DotnetCounters.Enabled` | bool | ❌ | Enable/disable counter collection (default: `$true`) |
| `DotnetCounters.Providers` | string[] | ❌ | Counter providers (e.g., `@('System.Runtime')`) |
| `DotnetCounters.RefreshIntervalSeconds` | int | ❌ | Sampling interval in seconds (default: 1) |
| `Tolerances` | hashtable | ❌ | Override Hone's default accept/reject thresholds |
| `Loop` | hashtable | ❌ | Override Hone's default loop settings |

All paths are **relative to the target root directory** (the parent of `.hone/`).

---

## 4. Hook Types

Every hook in `Hooks.*` must be one of five types. There is no `Default` type —
the target must explicitly choose for each lifecycle phase.

### Script

Target provides a PowerShell script in `.hone/hooks/`:

```powershell
@{ Type = 'Script'; Path = '.hone\hooks\prepare.ps1' }
```

`Path` is relative to the target root. The script must follow the
[Hook Script Contract](#5-hook-script-contract).

### Shared

References a reusable hook from Hone's `harness/hooks/` directory:

```powershell
@{ Type = 'Shared'; Name = 'dotnet-start' }
```

Resolves to `harness/hooks/dotnet-start.ps1` in the Hone installation. See
[Shared Hooks Catalog](#7-shared-hooks-catalog) for available hooks.

### Command

Inline shell command for simple operations:

```powershell
@{ Type = 'Command'; Value = 'Remove-Item app.db -ErrorAction SilentlyContinue' }
```

Executed directly by the harness. Best for one-liners that don't need the full
hook parameter contract.

### Http

Calls an endpoint on the running API:

```powershell
@{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
```

The harness combines `Path` with the API's `BaseUrl` to make the HTTP request.
Useful for cooldown, in-flight cache resets, or diagnostic triggers.

### Skip

Explicit no-op — the target does not need this lifecycle phase:

```powershell
@{ Type = 'Skip' }
```

No script file is required for skipped hooks.

---

## 5. Hook Script Contract

Every `Script` and `Shared` hook receives a standard parameter block and must
return a standard result object:

```powershell
# Parameters (passed by the harness)
param(
    [Parameter(Mandatory)] [string]$TargetPath,    # resolved target directory
    [Parameter(Mandatory)] [hashtable]$Config,      # full merged Hone config
    [string]$BaseUrl,                               # API base URL (available after start)
    [string]$Experiment                             # experiment identifier
)

# ── Hook implementation ──────────────────────────────────────
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# ... your logic here ...

$sw.Stop()

# ── Return contract ──────────────────────────────────────────
[PSCustomObject]@{
    Success   = [bool]$true                               # $true if hook succeeded
    Message   = 'Database dropped — will recreate on startup'  # human-readable status
    Duration  = [timespan]$sw.Elapsed                      # wall-clock time
    Artifacts = @()                                        # optional: file paths to collect
}
```

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `Success` | bool | ✅ | `$true` if the hook completed successfully |
| `Message` | string | ✅ | Human-readable status message for logs |
| `Duration` | timespan | ✅ | Wall-clock execution time |
| `Artifacts` | string[] | ❌ | File paths the harness should collect as experiment artifacts |

If `Success` is `$false`, the harness aborts the current measurement cycle and
logs the failure.

---

## 6. Measurement Lifecycle

Each measurement cycle (baseline + per-experiment) follows this 8-phase sequence:

```
┌─────────────────────────────────────────────────────┐
│                  Experiment N                        │
│                                                     │
│  1. BUILD          (compile target)                 │
│  2. VERIFY         (run E2E / regression tests)     │
│  3. MEASURE        (one or more measurement cycles) │
│     ┌─────────────────────────────────────────┐     │
│     │  a. prepare   ← reset DB, clear caches  │     │
│     │  b. start     ← launch API process       │     │
│     │  c. ready     ← wait for health check    │     │
│     │  d. warmup    ← optional warmup requests │     │
│     │  e. active    ← k6 stress test run       │     │
│     │  f. cooldown  ← GC collect, settle       │     │
│     │  g. stop      ← shut down API process    │     │
│     │  h. cleanup   ← collect artifacts, logs  │     │
│     └─────────────────────────────────────────┘     │
│     (repeat a–h for each measured run)              │
│  4. COMPARE        (evaluate metrics)               │
│  5. PUBLISH        (create PR if accepted)          │
└─────────────────────────────────────────────────────┘
```

### Phase Details

| Phase | Hook | When It Runs | Typical Implementation |
|-------|------|-------------|----------------------|
| **prepare** | `Hooks.Prepare` | Before each measurement run | Reset database, clear caches, restore known state |
| **start** | `Hooks.Start` | After prepare | Launch the API process (`dotnet run`, Docker, etc.) |
| **ready** | `Hooks.Ready` | After start | Poll health endpoint until the API is responsive |
| **warmup** | `Hooks.Warmup` | After ready | Optional custom pre-measurement warmup; most targets use `Skip` and rely on built-in k6 warmup |
| **active** | `Hooks.Active` | After warmup | Run k6 load test — this is the measured phase |
| **cooldown** | `Hooks.Cooldown` | After active | Trigger GC, let metrics settle before stopping |
| **stop** | `Hooks.Stop` | After cooldown | Shut down the API process |
| **cleanup** | `Hooks.Cleanup` | After stop | Collect logs, traces, or other artifacts |

The `MeasuredRuns` config key controls how many times the a–h cycle repeats per
experiment. Results are aggregated across runs.

---

## 7. Shared Hooks Catalog

Hone provides reusable hook implementations in `harness/hooks/`. Reference them
via `@{ Type = 'Shared'; Name = '<name>' }`.

| Shared Hook | Name | What It Does |
|-------------|------|-------------|
| **dotnet-build** | `dotnet-build` | `dotnet build <SolutionPath> --configuration Release` |
| **dotnet-start** | `dotnet-start` | `dotnet run --project <ProjectPath> --urls <BaseUrl>` |
| **dotnet-stop** | `dotnet-stop` | `Stop-Process` on the running API process |
| **dotnet-test** | `dotnet-test` | `dotnet test <TestProjectPath> --configuration Release` |
| **sqlserver-reset** | `sqlserver-reset` | Parse `appsettings.json`, `sqlcmd DROP DATABASE` |
| **health-poll** | `health-poll` | Poll `HealthEndpoint` until `status: healthy` or timeout |
| **k6-run** | `k6-run` | Run k6 scenario from `ScaleTest.ScenarioPath`, collect metrics |

> **Note on warmup:** Warmup is handled internally by `Invoke-ScaleTests.ps1`
> when `ScaleTest.WarmupEnabled = $true`, not as a separate lifecycle hook. The
> `Warmup` hook slot in `Hooks.*` can be used for custom pre-measurement warmup
> logic, but most targets should set `Warmup = @{ Type = 'Skip' }` and rely on
> the built-in warmup via `ScaleTest.WarmupScenarioPath`.

Shared hooks follow the same [Hook Script Contract](#5-hook-script-contract) —
they receive `$TargetPath`, `$Config`, `$BaseUrl`, and `$Experiment`, and return
the standard result object. They read target-specific values (paths, URLs,
timeouts) from `$Config`.

Targets with custom needs provide their own `Script` hooks instead of using
shared hooks.

---

## 8. `.gitignore` Template

The `.hone/` directory is fully tracked — everything in it is authored content.
The `results/` directory needs selective tracking: commit compact analysis
artifacts (for PR reviews), ignore raw profiling data (large, transient).

Add these rules to your target's `.gitignore`:

```gitignore
# ── Hone Results ─────────────────────────────────────────
# Commit analysis artifacts (compact, useful for PR reviews)
# Ignore raw profiling data (large, transient)

results/*
!results/.gitkeep
!results/baseline.json
!results/baseline-*.json
!results/run-metadata.json
!results/hone.jsonl
!results/metadata/

# Experiment directories: commit structure, ignore raw data
!results/experiment-*/
results/experiment-*/*.log
results/experiment-*/*.trx
results/experiment-*/diagnostics/perfview-cpu/
results/experiment-*/diagnostics/perfview/
results/experiment-*/diagnostics/perfview-gc/*
!results/experiment-*/diagnostics/perfview-gc/gc-report.json
results/experiment-*/diagnostics/dotnet-counters/*
!results/experiment-*/diagnostics/dotnet-counters/dotnet-counters.json
results/experiment-*/diagnostics/k6-*.json
```

### What's Committed vs Ignored

| Category | Files | Committed? | Why |
|----------|-------|-----------|-----|
| Baselines | `baseline.json`, `baseline-*.json` | ✅ | Reference metrics for comparison |
| k6 summaries | `k6-summary*.json` | ✅ | Compact performance data for PR evidence |
| Agent analysis | `*-prompt.md`, `*-response.json` | ✅ | Shows AI reasoning for reviewers |
| Classification | `classification-response.json` | ✅ | Scope assessment for each optimization |
| Root cause | `root-cause.md` | ✅ | Human-readable failure analysis |
| Parsed diagnostics | `gc-report.json`, `dotnet-counters.json` | ✅ | Compact summaries only |
| Metadata | `experiment-log.md`, `run-metadata.json` | ✅ | Optimization history and state |
| Event log | `hone.jsonl` | ✅ | Full harness event history |
| PerfView traces | `*.etl`, `*.etl.zip` | ❌ | Very large (100s of MB), raw profiling data |
| PerfView raw stacks | `perfview-cpu/`, `perfview-gc/*` | ❌ | Raw stacks — only parsed summary is useful |
| k6 raw logs | `k6-run*.log`, `k6-*.json` | ❌ | Per-run raw output — summaries suffice |
| Build/test logs | `build.log`, `e2e-results.trx` | ❌ | Transient, only relevant on failure |

---

## 9. Quick Start Example

Minimal steps to add Hone support to an existing .NET project:

### Step 1 — Create `.hone/` directory structure

```powershell
New-Item -ItemType Directory -Path .hone/hooks, .hone/scenarios -Force
```

### Step 2 — Write `config.psd1`

```powershell
# .hone/config.psd1
@{
    Name       = 'MyApi'
    BaseBranch = 'main'

    Api = @{
        SolutionPath     = 'MyApi.sln'
        ProjectPath      = 'src\MyApi'
        TestProjectPath  = 'tests\MyApi.Tests'
        ResultsPath      = 'results'
        MetadataPath     = 'results\metadata'
        BaseUrl          = 'http://localhost:0'
        HealthEndpoint   = '/health'
        GcEndpoint       = '/diag/gc'
        StartupTimeout   = 60
        SourceCodePaths  = @('src\MyApi')
        SourceFileGlob   = '*.cs'
    }

    Hooks = @{
        Prepare  = @{ Type = 'Shared'; Name = 'sqlserver-reset' }
        Start    = @{ Type = 'Shared'; Name = 'dotnet-start' }
        Ready    = @{ Type = 'Shared'; Name = 'health-poll' }
        Warmup   = @{ Type = 'Skip' }
        Active   = @{ Type = 'Shared'; Name = 'k6-run' }
        Cooldown = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
        Stop     = @{ Type = 'Shared'; Name = 'dotnet-stop' }
        Cleanup  = @{ Type = 'Skip' }
    }

    ScaleTest = @{
        ScenarioPath         = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        WarmupEnabled        = $false
        MeasuredRuns         = 5
        CooldownSeconds      = 5
    }
}
```

### Step 3 — Write a k6 scenario

```javascript
// .hone/scenarios/baseline.js
import http from 'k6/http';
import { check } from 'k6';

export const options = {
    vus: 10,
    duration: '30s',
};

export default function () {
    const base = __ENV.BASE_URL || 'http://localhost:5000';
    const res = http.get(`${base}/api/products`);
    check(res, { 'status 200': (r) => r.status === 200 });
}
```

### Step 4 — Write thresholds

```json
// .hone/scenarios/thresholds.json
{
    "scenarios": {
        "baseline": {
            "path": ".hone/scenarios/baseline.js",
            "thresholds": {
                "http_req_duration": ["p(95)<500"]
            }
        }
    }
}
```

### Step 5 — Add `.gitignore` rules

Copy the [`.gitignore` template](#8-gitignore-template) into your project's
`.gitignore`.

### Step 6 — Run Hone

```powershell
Invoke-HoneLoop.ps1 -TargetPath C:\Projects\MyApi
```

Hone validates the `.hone/` contract at startup and fails fast with clear
error messages if anything is missing or misconfigured.

---

## 10. Compatibility Assessment

Before manually creating a `.hone/` directory, you can run the **compatibility
assessment agent** to automatically evaluate a target project and generate an
onboarding plan.

### Running the Assessment

```powershell
.\harness\Invoke-CompatibilityAgent.ps1 -TargetPath C:\Projects\eShopOnWeb
```

The agent actively probes the target by:
- Running the build command and reporting success/failure
- Running the test suite and reporting pass/fail counts
- Reading configuration files to detect endpoints, health checks, and database config
- Scanning for project structure, dependencies, and API routes

### Assessment Output

The agent produces a JSON report written to `<target>/.hone-assessment.json`
with these sections:

| Section | Content |
|---------|---------|
| `target` | Detected stack, framework, and runtime |
| `compatibility` | Overall score (0–100), blockers, warnings, and ready items |
| `probeResults` | Detailed findings for git, build, tests, API, database, k6, and `.hone/` |
| `detectedConfig` | Auto-detected values that map to `.hone/config.psd1` keys |
| `onboardingPlan` | Phased plan with concrete steps to make the target Hone-compatible |
| `implementationPlan` | Hook recommendations, required code changes, k6 scenario guidance, and a draft `config.psd1` |

### Compatibility Scoring

| Area | Weight | Full marks when |
|------|--------|-----------------|
| Git + GitHub remote | 15 | Git repo with GitHub remote |
| CLI build | 20 | Build succeeds from command line |
| Test suite | 20 | Tests exist and pass from CLI |
| HTTP API | 15 | HTTP framework detected with discoverable endpoints |
| Database/state | 10 | Resettable state (DB or stateless) |
| Health endpoint | 10 | Health endpoint exists or trivial to add |
| k6 readiness | 10 | HTTP-based API with enumerable endpoints |

**Overall classification:**
- **compatible** (≥ 75): Target can be onboarded with minimal effort
- **partial** (40–74): Target needs significant work but is feasible
- **incompatible** (< 40): Target is not a good Hone candidate

### Non-.NET Targets

The assessment agent evaluates any stack (Node.js, Go, Python, Rust, Java, etc.)
but flags that Hone's current shared hooks are .NET-specific. Non-.NET targets
receive `Script` hook recommendations with guidance on what each custom hook
script should implement.

### Using the Assessment

After running the assessment:

1. Review blockers and address them (e.g., add a health endpoint, create tests)
2. Use `detectedConfig` values to seed your `.hone/config.psd1`
3. Use `implementationPlan.configTemplate` as a starting point for the config file
4. Follow the phased `onboardingPlan` to set up hooks and k6 scenarios
5. Run `Test-HoneConfig.ps1 -TargetPath <target>` to validate the final setup
