# Generalizing Hone — Phase 1 High-Level Plan

## 1. Problem Statement

Hone is currently hardcoded to optimize a single target (`sample-api`) using a
fixed tech stack (.NET 6 / EF Core / SQL Server LocalDB). The goal of Phase 1 is
to make the optimization target **configurable** so that Hone can optimize
additional .NET projects — starting with **eShopOnWeb** and **OrchardCore** — and
eventually support non-.NET targets.

This document assesses the readiness of the harness, identifies every coupling
point, proposes a target-adapter architecture, evaluates alternatives to
submodules for managing external targets, and outlines a regression-testing
strategy that keeps `sample-api` working as the reference target throughout.

---

## 2. Candidate Targets

### 2.1 eShopOnWeb (MicrosoftLearning/eShopOnWeb)

| Attribute | Value |
|-----------|-------|
| **What** | Microsoft reference eCommerce monolith (product catalog, cart, orders) |
| **Framework** | ASP.NET Core 8.0 (MVC + Razor + Blazor WASM admin) |
| **Database** | SQL Server LocalDB (EF Core, two DbContexts: Catalog + Identity) |
| **Build** | `dotnet build` — 7 projects, 15–30 s |
| **Tests** | xUnit + NSubstitute — Unit, Integration, Functional, PublicApiIntegration — 1–2 min |
| **API surface** | REST via `PublicApi` project (`/api/products`, `/api/categories`, `/health`) |
| **Docker** | `docker-compose up` (Web :5106, API :5200) |
| **Hone fit** | ⭐⭐⭐⭐⭐ Excellent — same stack (.NET/EF/SQL Server), real-world multi-tier app, existing test suites, manageable build time |

**Key differences from sample-api:**
- Two runnable projects (`Web` and `PublicApi`) — Hone must know which to start
- Multiple test projects — config must specify which project(s) to run
- Multiple DB contexts — `Reset-Database.ps1` may need to drop two databases
- Blazor WASM compilation adds build time

### 2.2 OrchardCore (OrchardCMS/OrchardCore)

| Attribute | Value |
|-----------|-------|
| **What** | Production CMS framework — modular, multi-tenant |
| **Framework** | ASP.NET Core (latest) — 100+ projects |
| **Database** | 4 backends (SQL Server, PostgreSQL, MySQL, SQLite) via YesSql |
| **Build** | `dotnet build` — 100+ projects, 2–5 min |
| **Tests** | xUnit.v3 + Cypress E2E — full matrix 10–30 min |
| **API surface** | REST + GraphQL (`/graphql`) + Admin UI |
| **Docker** | Official images available |
| **Hone fit** | ⭐⭐⭐ Medium — enormous build/test overhead, multi-DB matrix, npm dependencies for Cypress, architectural complexity far exceeds what Hone's "narrow fix" model targets well |

**Key differences from sample-api:**
- Massive solution size (100+ projects) increases build/test cycle cost
- Multi-tenant architecture means optimization suggestions could be architectural
- Cypress E2E tests require Node.js — harness currently has no npm support
- Multiple DB backends complicate `Reset-Database.ps1`
- Setup wizard required on first run (not headless-friendly out of the box)

### 2.3 Assessment Summary

| Dimension | sample-api | eShopOnWeb | OrchardCore |
|-----------|------------|------------|-------------|
| Stack compatibility | ✅ Native | ✅ Same stack | ⚠️ Same stack, much larger |
| Build time | ~5 s | ~20 s | ~3 min |
| Test time | ~30 s | ~90 s | ~15 min |
| DB reset complexity | 1 DB, sqlcmd | 2 DBs, sqlcmd | 4 backends |
| Experiment cost | Low | Medium | High |
| Recommended approach | Reference target | First generalization target | Stretch goal / Docker-only |

---

## 3. Current Coupling Audit

An exhaustive script-by-script audit of every place the harness assumes
`sample-api` or the .NET stack.

### 3.1 Hardcoded `sample-api` Path References

These are the **blocking** couplings — the harness literally cannot operate on a
different directory without changing these lines.

| Script | Lines | What's Hardcoded | Impact |
|--------|-------|------------------|--------|
| `Invoke-HoneLoop.ps1` | ~324 | `Push-Location … 'sample-api'` — branch state verification | Blocks any non-sample-api target |
| `Invoke-HoneLoop.ps1` | ~621–625 | File path normalization: `if ($targetFile -notmatch '^sample-api[\\/]')` | Forces all fix paths under `sample-api/` |
| `Invoke-HoneLoop.ps1` | ~713, 779 | `Push-Location … 'sample-api'` — PR creation in submodule | Assumes git submodule structure |
| `Apply-Suggestion.ps1` | ~55–57 | `$allowedRoot = … 'sample-api'` — path traversal guard | Security boundary locked to `sample-api/` |
| `Apply-Suggestion.ps1` | ~61–62 | `$submoduleDir = … 'sample-api'` — branch creation | Git ops in wrong directory for other targets |
| `Revert-ExperimentCode.ps1` | ~59 | `$submoduleDir = … 'sample-api'` | Revert operates on wrong directory |
| `Invoke-FixAgent.ps1` | prompt | `"relative to sample-api/"` | AI agent told to look in wrong path |
| `HoneHelpers.psm1` | various | `sample-api` references in PR/branch utilities | PR creation targets wrong repo |

### 3.2 Hardcoded .NET Commands

These scripts invoke `dotnet` directly — not through a configurable command.

| Script | Command | Configurable? |
|--------|---------|---------------|
| `Build-SampleApi.ps1` | `dotnet build … --configuration Release` | Path yes, command no |
| `Invoke-E2ETests.ps1` | `dotnet test … --configuration Release --logger trx` | Path yes, command/format no |
| `Start-SampleApi.ps1` | `dotnet run --project … --configuration Release --urls $baseUrl` | Path/URL yes, command no |
| `Reset-Database.ps1` | `sqlcmd -S $server -Q …` | Connection string yes, tool/dialect no |

### 3.3 Already Parameterized (No Change Needed)

These are ready for multi-target use today.

| Component | Config Key(s) | Notes |
|-----------|---------------|-------|
| Solution path | `Api.SolutionPath` | Just point to new `.sln` |
| Project path | `Api.ProjectPath` | Project to `dotnet run` |
| Test project path | `Api.TestProjectPath` | Project to `dotnet test` |
| Results directory | `Api.ResultsPath` | Where metrics land |
| Metadata directory | `Api.MetadataPath` | Run metadata storage |
| Source code paths | `Api.SourceCodePaths` | Directories for AI context |
| Source file glob | `Api.SourceFileGlob` | `*.cs`, `*.ts`, etc. |
| k6 scenario path | `ScaleTest.ScenarioPath` | Scenario JS file |
| k6 scenario registry | `ScaleTest.ScenarioRegistryPath` | `thresholds.json` |
| Base URL / port | `Api.BaseUrl` | Dynamic port support |
| Health endpoint | `Api.HealthEndpoint` | `/health` |
| Startup timeout | `Api.StartupTimeout` | Seconds |
| All tolerances | `Tolerances.*` | Regression/improvement thresholds |
| AI models | `Copilot.*` | Model selection per agent |
| Diagnostic plugins | `Diagnostics.*` | Collector/analyzer framework |

### 3.4 Coupling Heatmap

```
                        ┌──────────────────────────────────────────┐
  Fully Portable        │  AI Pipeline · k6 Load Tests · Git VCS  │
  (no changes needed)   │  Health Checks · Tolerances · Config     │
                        └──────────────────────────────────────────┘
                        ┌──────────────────────────────────────────┐
  Parameterized but     │  Build command · Test command · Start    │
  .NET-only commands    │  command · DB reset · dotnet-counters    │
                        └──────────────────────────────────────────┘
                        ┌──────────────────────────────────────────┐
  Hardcoded to          │  'sample-api' path in 8+ scripts         │
  sample-api            │  Submodule git workflow in 4+ scripts    │
                        └──────────────────────────────────────────┘
```

---

## 4. Target Management: Target-Centric Architecture

### 4.1 Design Principle: Hone as Read-Only Engine

Hone is a **read-only execution engine**. All target-specific configuration
lives inside the target's own repository in a `.hone/` directory. Hone is
invoked by pointing it at a target directory:

```powershell
Invoke-HoneLoop.ps1 -TargetPath C:\Projects\eShopOnWeb
Invoke-HoneLoop.ps1 -TargetPath .\sample-api          # relative path
```

This mirrors how Docker reads `Dockerfile`, Terraform reads `.tf` files, and
GitHub Actions reads `.github/workflows/` — the project declares how it should
be operated on, and the engine executes.

**Consequences:**
- Hone's repo contains zero target-specific configuration
- Each target is self-describing — clone it, see its `.hone/` directory, run Hone
- Third-party targets (eShopOnWeb, OrchardCore) are forked; `.hone/` is added to
  the fork
- `sample-api` submodule already has `.hone/` — it becomes the reference
  implementation

### 4.2 Responsibility Matrix

| Concern | Owner | Where It Lives |
|---------|-------|----------------|
| **Target metadata** (name, base branch) | Target | `.hone/config.psd1` |
| **API paths** (solution, project, test project) | Target | `.hone/config.psd1` |
| **Source code paths + file glob** | Target | `.hone/config.psd1` |
| **Endpoints** (health, GC/diag) | Target | `.hone/config.psd1` |
| **Startup timeout** | Target | `.hone/config.psd1` |
| **Lifecycle hooks** (prepare, start, stop, etc.) | Target | `.hone/hooks/*.ps1` |
| **k6 scenarios + thresholds** | Target | `.hone/scenarios/` |
| **Scale test tuning** (warmup, runs, cooldown) | Target | `.hone/config.psd1` |
| **Results directory** | Target | `.hone/config.psd1` |
| **Runtime counter providers** (e.g., dotnet-counters) | Target | `.hone/config.psd1` |
| **Project-specific AI hints** | Target | `.hone/context.md` (future) |
| | | |
| **Orchestration loop** | Hone | `harness/Invoke-HoneLoop.ps1` |
| **AI agent definitions** (`.agent.md`) | Hone | `.github/agents/` |
| **Copilot model selection** | Hone | `harness/config.psd1` |
| **Tolerance thresholds** (accept/reject policy) | Hone default, target override | Hone `config.psd1`, optional `.hone/config.psd1` override |
| **Loop settings** (max experiments, mode) | Hone default, target override | Hone `config.psd1`, optional `.hone/config.psd1` override |
| **Shared hook implementations** | Hone | `harness/hooks/` |
| **Diagnostic plugin framework** | Hone | `harness/collectors/`, `harness/analyzers/` |
| **Metric comparison** | Hone | `harness/Compare-Results.ps1` |
| **Git workflow** (branching, PRs) | Hone | `harness/Apply-Suggestion.ps1`, etc. |
| **Classification** (narrow vs architecture) | Hone | `harness/Invoke-ClassificationAgent.ps1` |
| **Hook dispatcher** | Hone | `harness/HoneHelpers.psm1` |

### 4.3 `.hone/` Directory Structure

```
<target-repo>/
└── .hone/
    ├── config.psd1              # Target configuration
    ├── hooks/
    │   ├── prepare.ps1          # Reset DB / clear state before measurement
    │   ├── start.ps1            # Start the API process (can delegate to shared hook)
    │   ├── stop.ps1             # Stop the API process (can be a no-op wrapper)
    │   └── cleanup.ps1          # Post-measurement artifact collection (can be a no-op)
    ├── scenarios/
    │   ├── baseline.js          # Primary k6 scenario (used for optimization)
    │   ├── warmup.js            # Warmup scenario (can be a minimal no-op script)
    │   ├── stress.js            # Additional scenarios
    │   └── thresholds.json      # Scenario registry
    └── context.md               # Project-specific AI hints (optional — only optional file)
```

**All files in `.hone/` are required except `context.md`.** Hooks and scenarios
must be explicitly declared by the target — if a phase isn't needed, the target
provides a no-op implementation (e.g., `@{ Type = 'Skip' }` in config, or a
hook script that returns `Success = $true` immediately). This ensures:

- Every target has been deliberately configured — no silent fallback to defaults
  that may not be appropriate
- Hone can validate the full `.hone/` contract at startup and fail fast with
  clear errors
- New targets must think through each lifecycle phase, even if the answer is
  "not needed"

The only optional file is `context.md` — project-specific AI hints that agents
can use for better optimization suggestions. If absent, agents operate with
Hone's built-in prompts only.

### 4.4 `.hone/config.psd1` Schema

```powershell
@{
    # ── Target Identity ──────────────────────────────────────────
    Name       = 'eShopOnWeb'        # Used in branch names, logs, PR titles
    BaseBranch = 'main'              # Experiments branch from here

    # ── API Project Layout ───────────────────────────────────────
    Api = @{
        SolutionPath     = 'eShopOnWeb.sln'
        ProjectPath      = 'src\PublicApi'
        TestProjectPath  = 'tests\FunctionalTests'
        ResultsPath      = 'results'
        MetadataPath     = 'results\metadata'
        BaseUrl          = 'http://localhost:0'    # port 0 = ephemeral
        HealthEndpoint   = '/health'
        GcEndpoint       = '/diag/gc'
        StartupTimeout   = 120
        SourceCodePaths  = @('src\PublicApi', 'src\ApplicationCore', 'src\Infrastructure')
        SourceFileGlob   = '*.cs'
    }

    # ── Lifecycle Hooks ──────────────────────────────────────────
    # ALL hooks must be declared. Use Type = 'Skip' for phases not needed.
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

    # ── Scale Test Configuration (required) ─────────────────────
    ScaleTest = @{
        ScenarioPath         = '.hone\scenarios\baseline.js'    # must exist
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json' # must exist
        WarmupEnabled        = $true
        WarmupScenarioPath   = '.hone\scenarios\warmup.js'       # must exist if WarmupEnabled
        MeasuredRuns         = 5
        CooldownSeconds      = 5
    }

    # ── Optional Overrides (override Hone engine defaults) ───────
    # Tolerances = @{
    #     MaxRegressionPct  = 0.10
    #     MinImprovementPct = 0.05
    # }
    # Loop = @{
    #     MaxExperiments = 6
    # }
    # DotnetCounters = @{
    #     Providers = @('System.Runtime', 'Microsoft.AspNetCore.Hosting')
    # }
}
```

### 4.5 Config Merge Order

When Hone runs, configuration is assembled in layers:

```
1. Hone engine defaults       (harness/config.psd1: tolerances, models, loop)
        ↓ merged with
2. Target .hone/config.psd1   (paths, hooks, scenarios, optional overrides)
        ↓ merged with
3. CLI flags                   (-MaxExperiments, -DryRun, -Model, etc.)
```

Lower layers override higher layers. This means:
- A target can override Hone's default tolerance thresholds
- CLI flags override everything (useful for quick experiments)
- Hone's engine defaults are always the fallback

### 4.6 Hook Resolution

With hooks being required in `.hone/config.psd1`, resolution is straightforward
— no fallback chain needed:

```powershell
function Resolve-Hook {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$HookName,
        [Parameter(Mandatory)] [hashtable]$TargetConfig,
        [Parameter(Mandatory)] [string]$TargetDir,
        [Parameter(Mandatory)] [string]$HarnessRoot
    )

    $hook = $TargetConfig.Hooks[$HookName]
    if (-not $hook) {
        throw ".hone/config.psd1 must declare Hooks.$HookName (use Type = 'Skip' if not needed)"
    }

    switch ($hook.Type) {
        'Script'  { return @{ Type = 'Script'; Path = Join-Path $TargetDir $hook.Path } }
        'Shared'  { return @{ Type = 'Script'; Path = Join-Path $HarnessRoot "hooks/$($hook.Name).ps1" } }
        'Command' { return $hook }
        'Http'    { return $hook }
        'Skip'    { return $hook }
        default   { throw "Unknown hook type '$($hook.Type)' for Hooks.$HookName" }
    }
}
```

Every hook must be explicitly declared. There is no `Default` type — the target
must choose `Script`, `Shared`, `Command`, `Http`, or `Skip`. This eliminates
ambiguity and ensures the target author has deliberately considered each
lifecycle phase.

### 4.7 Shared Hooks (Hone-Provided, Target-Referenced)

Hone provides reusable hook implementations in `harness/hooks/`. Targets
reference them via the `Shared` hook type:

```powershell
# In .hone/config.psd1
Hooks = @{
    Start = @{ Type = 'Shared'; Name = 'dotnet-start' }   # uses harness/hooks/dotnet-start.ps1
    Stop  = @{ Type = 'Shared'; Name = 'dotnet-stop' }
}
```

Available shared hooks (extracted from current scripts):

| Shared Hook | From | What It Does |
|-------------|------|-------------|
| `dotnet-build` | `Build-SampleApi.ps1` | `dotnet build <solution> --configuration Release` |
| `dotnet-start` | `Start-SampleApi.ps1` | `dotnet run --project <project> --urls <baseUrl>` |
| `dotnet-stop` | `Stop-SampleApi.ps1` | `Stop-Process` on the API process |
| `dotnet-test` | `Invoke-E2ETests.ps1` | `dotnet test <project> --configuration Release` |
| `health-poll` | `HoneHelpers.psm1` | Poll `HealthEndpoint` until `status: healthy` or timeout |
| `k6-run` | `Invoke-ScaleTests.ps1` | Run k6 scenario, collect metrics |
| built-in warmup | `Invoke-ScaleTests.ps1` | Run warmup scenario if `WarmupEnabled` |
| `sqlserver-reset` | `Reset-Database.ps1` | Parse appsettings.json, `sqlcmd DROP DATABASE` |
| `sqlite-reset` | (new) | Delete `.db` file |

Targets that use standard .NET conventions can reference these directly. Targets
with custom needs provide their own hook scripts.

### 4.8 Third-Party Target Workflow

For projects like eShopOnWeb that don't have `.hone/`:

1. Fork the repository
2. Add `.hone/` directory with config, hooks, and k6 scenarios
3. Point Hone at the fork: `Invoke-HoneLoop.ps1 -TargetPath C:\Projects\eShopOnWeb-fork`
4. Optimizations are committed to the fork's experiment branches
5. Upstream changes are synced via `git pull upstream main`

This is intentionally simple. No overlay files, no dual-source configuration,
no magic merging. One repo, one `.hone/`, one source of truth.

### 4.9 Keeping sample-api as Reference

The `sample-api` submodule remains in Hone's repo as:
- **Reference implementation** of `.hone/` — shows how to write config, hooks,
  and scenarios
- **Regression anchor** — CI smoke tests run against it after every harness
  change
- **Development target** — Hone developers use it locally for rapid iteration

Its `.hone/` directory becomes the canonical example other targets follow.

---

## 5. Proposed Harness Architecture

### 5.1 Core Change: `-TargetPath` Invocation Model

The harness is invoked with a `-TargetPath` parameter pointing to the target
repo. All target-specific configuration is loaded from `<TargetPath>/.hone/`:

```powershell
# Invoke-HoneLoop.ps1 — new entrypoint signature
param(
    [Parameter(Mandatory)]
    [string]$TargetPath,       # path to target repo (must contain .hone/)

    [int]$MaxExperiments,      # CLI override
    [switch]$DryRun,           # CLI override
    [string]$Model             # CLI override
)

# Resolve and validate
$targetDir = [System.IO.Path]::GetFullPath($TargetPath)
$honeDir = Join-Path $targetDir '.hone'
if (-not (Test-Path (Join-Path $honeDir 'config.psd1'))) {
    throw "Target directory '$targetDir' does not contain .hone/config.psd1"
}

# Load and merge config
$engineConfig = Get-HoneConfig   # harness/config.psd1 (engine defaults)
$targetConfig = Import-PowerShellDataFile (Join-Path $honeDir 'config.psd1')
$config = Merge-HoneConfig -Engine $engineConfig -Target $targetConfig -CliOverrides $PSBoundParameters
```

This replaces the current model where `Invoke-HoneLoop.ps1` takes `-ConfigPath`
pointing to Hone's own `config.psd1` and all target paths are resolved relative
to Hone's repo root.

### 5.2 Lifecycle Hook Architecture

Rather than simply renaming scripts (`Build-SampleApi.ps1` → `Build-Target.ps1`),
the harness adopts a **lifecycle hook system**. The harness defines well-known
phases that surround each measurement cycle. Each target provides
implementations for the hooks it needs. The harness calls them at the right
time.

#### 5.2.1 Measurement Lifecycle

Each measurement cycle (baseline + per-experiment) follows this sequence:

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

**How this maps to today's harness:**

| Hook | Current Implementation | Hardcoded? |
|------|----------------------|------------|
| `prepare` | `Reset-Database.ps1` (sqlcmd DROP) | Yes — SQL Server only |
| `start` | `Start-SampleApi.ps1` (dotnet run) | Yes — .NET only |
| `ready` | Health check polling in `HoneHelpers.psm1` | No — already generic HTTP |
| `warmup` | Optional k6 warmup scenario | No — already generic |
| `active` | `Invoke-ScaleTests.ps1` (k6 run) | No — already generic |
| `cooldown` | `Invoke-Cooldown.ps1` / POST `/diag/gc` | No — already generic HTTP |
| `stop` | `Stop-SampleApi.ps1` (Stop-Process) | Partially — process management is generic |
| `cleanup` | (doesn't exist yet — implicit) | N/A |

The hook system generalizes `prepare`, `start`, `stop`, and eventually `build`
and `verify` — the four phases with hardcoded .NET/SQL Server assumptions.

#### 5.2.2 Hook Types

Each hook definition is one of five types:

```powershell
# Script — target provides a .ps1 file in .hone/hooks/
@{ Type = 'Script'; Path = '.hone/hooks/prepare.ps1' }

# Shared — reference a reusable hook from Hone's harness/hooks/
@{ Type = 'Shared'; Name = 'dotnet-start' }

# Command — inline shell command for simple cases
@{ Type = 'Command'; Value = 'Remove-Item app.db -ErrorAction SilentlyContinue' }

# Http — call an endpoint on the running API (for cooldown, in-flight reset)
@{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }

# Skip — explicitly no-op (target does not need this phase)
@{ Type = 'Skip' }
```

There is no `Default` type. Every hook must be explicitly declared by the
target. See section 4.4 for the full `.hone/config.psd1` schema with hook
definitions, section 4.6 for hook resolution logic, and section 4.7 for
available shared hooks.

#### 5.2.3 Hook Script Contract

Every script-type hook receives a standard parameter block and returns a
standard result:

```powershell
# Parameters (passed by harness)
param(
    [Parameter(Mandatory)] [string]$TargetPath,   # resolved target directory
    [Parameter(Mandatory)] [hashtable]$Config,     # full Hone config
    [Parameter(Mandatory)] [string]$BaseUrl,       # API base URL (for Http hooks)
    [string]$Experiment                            # experiment identifier
)

# Return contract
[PSCustomObject]@{
    Success   = [bool]$true
    Message   = 'Database dropped — will recreate on startup'
    Duration  = [timespan]$elapsed
    Artifacts = @()   # optional: paths to files the harness should collect
}
```

### 5.3 Target-Relative Path Resolution

All paths in `config.Api.*` become **relative to the target directory** instead
of relative to the Hone repo root:

```
Before:  Join-Path $repoRoot $config.Api.SolutionPath
         → C:\Projects\Hone\sample-api\SampleApi.sln

After:   Join-Path $targetDir $config.Api.SolutionPath
         → C:\Projects\eShopOnWeb\eShopOnWeb.sln
```

### 5.4 Security Boundary Update

`Apply-Suggestion.ps1` currently restricts edits to `sample-api/`. This becomes:

```powershell
# Before (hardcoded)
$allowedRoot = Join-Path $repoRoot 'sample-api'

# After (from -TargetPath parameter)
$allowedRoot = $targetDir   # resolved from Invoke-HoneLoop.ps1 -TargetPath
```

The security boundary follows the target — edits are always restricted to the
target directory, regardless of which target is configured.

### 5.5 Git Workflow Changes

Currently the harness creates experiment branches **inside the sample-api
submodule**. For external-path targets, the harness instead:

1. Creates experiment branches inside the target's own repo
2. `Apply-Suggestion.ps1` runs `git checkout -b` in `$targetDir`
3. PR creation via `gh pr create` runs in `$targetDir`
4. Revert via `Revert-ExperimentCode.ps1` operates in `$targetDir`

The Hone repo itself doesn't need branches per experiment — it's the
orchestrator, not the optimization target.

### 5.6 Target State Reset — Generalized via `prepare` Hook

The current `Reset-Database.ps1` is the poster child for tight coupling: it
parses `appsettings.json`, extracts a SQL Server connection string with regex,
and executes `sqlcmd` to drop the database. This is called in three places:

1. `Get-PerformanceBaseline.ps1` — before baseline measurement
2. `Invoke-HoneLoop.ps1` — before each experiment's measurement
3. `Invoke-DiagnosticMeasurement.ps1` — before each diagnostic profiling pass

**Why reset exists:** Each k6 run creates test orders, reviews, and cart items.
Without reset, subsequent experiments would measure against different data
volumes, invalidating comparisons. The reset ensures every measurement starts
from identical seed data (1000 products, ~2000 reviews, deterministic
`Random(42)` seed).

**Current flow:** Reset happens *before* the API starts. `sqlcmd` drops the DB
externally, then `Program.cs` recreates + seeds it via `EnsureCreated()` +
`SeedData.Initialize()` on startup. This means the `prepare` hook runs before
`start` — not while the API is running.

**Under the hook system**, all three call sites become:

```powershell
# Before (hardcoded)
& (Join-Path $PSScriptRoot 'Reset-Database.ps1') -ConfigPath $ConfigPath -Experiment $experiment

# After (hook-based)
Invoke-LifecycleHook -Name 'Prepare' -Config $config -TargetDir $targetDir -Experiment $experiment
```

The hook resolver finds the right implementation:

- **sample-api** → `.hone/hooks/prepare.ps1` calls `sqlcmd` to drop DB
- **eShopOnWeb** → `.hone/hooks/prepare.ps1` drops two DBs (Catalog + Identity)
- **OrchardCore** → `.hone/hooks/prepare.ps1` deletes SQLite `.db` file
- **Target with no prepare hook** → Hone uses built-in default
- **Target with auto-migrate on startup** → sets `Prepare = @{ Type = 'Skip' }`
  (the app handles its own state reset)

This design means:
- **No changes to the orchestration loop** beyond replacing 3 direct calls with
  `Invoke-LifecycleHook`
- **No changes to existing targets** — they provide their own `.hone/` with hooks
- **Full flexibility** for new targets to implement reset however they need to

### 5.7 Changes-at-a-Glance

**Hone engine changes:**

| File | Change Type | Description |
|------|-------------|-------------|
| `harness/config.psd1` | **Reduce** to engine-only settings | Tolerances, Copilot models, loop settings, diagnostic defaults |
| `HoneHelpers.psm1` | **Add** `Resolve-Hook`, `Invoke-LifecycleHook`, `Merge-HoneConfig` | Hook dispatch + config merge |
| `Invoke-HoneLoop.ps1` | **Replace** `-ConfigPath` with `-TargetPath` | Load `.hone/config.psd1`, replace `'sample-api'` refs + direct script calls |
| `Get-PerformanceBaseline.ps1` | **Replace** direct script calls | Use `Invoke-LifecycleHook` |
| `Invoke-DiagnosticMeasurement.ps1` | **Replace** direct script calls | Use `Invoke-LifecycleHook` |
| `Apply-Suggestion.ps1` | **Replace** hardcoded path | `$allowedRoot = $targetDir` |
| `Revert-ExperimentCode.ps1` | **Replace** hardcoded path | Use `$targetDir` |
| `Invoke-FixAgent.ps1` | **Replace** prompt text | `"relative to $targetName/"` |
| `Build-AnalysisContext.ps1` | **Update** path resolution | Use `$targetDir` |
| `Test-HoneConfig.ps1` | **Add** `.hone/` validation | Validate `.hone/config.psd1` exists and has required keys |
| `Build-SampleApi.ps1` | **Move** → `harness/hooks/dotnet-build.ps1` | Becomes shared hook |
| `Start-SampleApi.ps1` | **Move** → `harness/hooks/dotnet-start.ps1` | Becomes shared hook |
| `Stop-SampleApi.ps1` | **Move** → `harness/hooks/dotnet-stop.ps1` | Becomes shared hook |
| `Reset-Database.ps1` | **Move** → `harness/hooks/sqlserver-reset.ps1` | Becomes shared hook |
| `harness/hooks/Invoke-Hook.ps1` | **New** | Generic hook dispatcher |
| `harness/hooks/sqlite-reset.ps1` | **New** | SQLite reset (delete file) |
| `docs/adapter-contracts.md` | **New** | Hook contract + `.hone/` specification |

**Target-side changes (sample-api):**

| File | Change Type | Description |
|------|-------------|-------------|
| `sample-api/.hone/config.psd1` | **New** | Target configuration (paths, hooks, scenarios) |
| `sample-api/.hone/hooks/prepare.ps1` | **New** | SQL Server reset (wraps sqlcmd logic) |
| `sample-api/.hone/scenarios/` | **Move** from `sample-api/scale-tests/` | k6 scenarios + thresholds.json |
| `sample-api/.hone/context.md` | **New** (optional) | Project-specific AI hints |

**Removed from Hone:**

| Item | Reason |
|------|--------|
| `harness/profiles/` | No longer needed — config lives in target `.hone/` |
| Target-specific keys in `harness/config.psd1` | Moved to `.hone/config.psd1` |

### 5.8 `.gitignore` Strategy for `.hone/` and Results

The current `sample-api/.gitignore` has carefully tuned rules that commit
analysis artifacts (for PR reviews and supporting evidence) while ignoring raw
profiling data. This strategy must transfer cleanly to the target-centric model.

#### What's Committed Today (sample-api)

| Category | Files | Why Committed |
|----------|-------|---------------|
| Baselines | `baseline.json`, `baseline-*.json` | Reference metrics for comparison |
| k6 summaries | `k6-summary*.json` | Compact performance data for PR evidence |
| Agent analysis | `*-prompt.md`, `*-response.json` | Shows AI reasoning for reviewers |
| Classification | `classification-response.json` | Scope assessment for each optimization |
| Root cause | `root-cause.md` | Human-readable failure analysis |
| Parsed diagnostics | `gc-report.json`, `dotnet-counters.json` | Compact summaries, not raw data |
| Metadata | `experiment-log.md`, queues, `run-metadata.json` | Optimization history and state |
| Event log | `hone.jsonl` | Full harness event history |

#### What's Ignored Today

| Category | Files | Why Ignored |
|----------|-------|-------------|
| PerfView ETL traces | `*.etl`, `*.etl.zip` | Very large (100s of MB), raw profiling data |
| PerfView CPU/GC raw | `perfview-cpu/`, `perfview-gc/*` (except `gc-report.json`) | Raw stacks, only parsed summary is useful |
| k6 raw logs | `k6-run*.log`, `k6-*.json` (raw run data) | Per-run raw output, summaries suffice |
| Build/test logs | `build.log`, `e2e-results.trx`, `e2e-tests.log` | Transient, only relevant on failure |
| Counter stderr | `dotnet-counters-stderr.log` | Debug output |

#### `.gitignore` in the Target-Centric Model

With `.hone/` and `results/` both living in the target repo, each target needs
its own `.gitignore` rules. There are two categories:

**1. `.hone/` directory — fully tracked (no ignores needed)**

Everything in `.hone/` is authored content: config, hook scripts, k6 scenarios,
thresholds. All of it should be committed. No `.gitignore` rules needed.

**2. `results/` directory — selective tracking**

The results directory is generated by the harness at runtime. The `.gitignore`
rules from `sample-api` serve as the template. Each target's `.gitignore` must
include equivalent rules.

#### Reference `.gitignore` Template for Targets

Hone should provide a reference `.gitignore` snippet that targets can include.
This could live in `docs/adapter-contracts.md` or as a template file:

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

#### Implementation Approach

Two options for getting `.gitignore` rules into each target:

**Option A — Target author adds rules manually:**
- Document the template in `docs/adapter-contracts.md`
- sample-api's `.gitignore` serves as the reference implementation
- eShopOnWeb fork author copies the relevant rules into their `.gitignore`

**Option B — `.hone/gitignore` template auto-merged (future):**
- Hone could provide a `.hone/gitignore` file in the target
- A setup script or harness init step appends these rules to the target's
  `.gitignore` if not already present
- More automated but adds complexity

**Recommendation:** Option A for Phase 1 (simple, explicit). Document the
template, let target authors include it. Consider Option B as a future
convenience if onboarding friction becomes an issue.

#### Changes Required

| File | Change |
|------|--------|
| `docs/adapter-contracts.md` | Include `.gitignore` template and explanation of what's committed vs ignored |
| `sample-api/.gitignore` | Already correct — serves as reference implementation |
| `Stage-ExperimentArtifacts.ps1` | Update paths to be `$targetDir`-relative instead of `sample-api`-relative |
| Hone root `.gitignore` | Remove `sample-api/results/*` rules (redundant — submodule has its own) |

---

## 6. eShopOnWeb `.hone/` Configuration

The eShopOnWeb fork would contain this `.hone/` directory:

```
eShopOnWeb/                     (forked from MicrosoftLearning/eShopOnWeb)
└── .hone/
    ├── config.psd1
    ├── hooks/
    │   └── prepare.ps1         # dual-DbContext reset (Catalog + Identity)
    └── scenarios/
        ├── baseline.js         # k6: steady-state against PublicApi
        ├── warmup.js           # k6: warmup requests
        └── thresholds.json     # scenario registry
```

**`.hone/config.psd1`:**

```powershell
@{
    Name       = 'eShopOnWeb'
    BaseBranch = 'main'

    Api = @{
        SolutionPath     = 'eShopOnWeb.sln'
        ProjectPath      = 'src\PublicApi'
        TestProjectPath  = 'tests\FunctionalTests'
        ResultsPath      = 'results'
        MetadataPath     = 'results\metadata'
        BaseUrl          = 'http://localhost:0'
        HealthEndpoint   = '/health'
        StartupTimeout   = 120                    # Blazor WASM takes longer
        SourceCodePaths  = @('src\PublicApi', 'src\ApplicationCore', 'src\Infrastructure', 'src\Web')
        SourceFileGlob   = '*.cs'
    }

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

    ScaleTest = @{
        ScenarioPath         = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        WarmupEnabled        = $true
        WarmupScenarioPath   = '.hone\scenarios\warmup.js'
        MeasuredRuns         = 5
        CooldownSeconds      = 5
    }
}
```

**eShopOnWeb-specific considerations:**
- The `PublicApi` project is the optimization target (not `Web`)
- Functional tests already hit API endpoints via `WebApplicationFactory`
- Two EF Core DB contexts (Catalog + Identity) — may need to reset both
- k6 scenarios must be written for eShopOnWeb's API surface (`/api/products`,
  `/api/categories`, etc.)

---

## 7. OrchardCore `.hone/` Configuration

```
OrchardCore/                    (forked from OrchardCMS/OrchardCore)
└── .hone/
    ├── config.psd1
    ├── hooks/
    │   └── prepare.ps1         # SQLite reset (delete .db file + recipe re-apply)
    └── scenarios/
        ├── baseline.js         # k6: steady-state against CMS REST API
        └── thresholds.json
```

**`.hone/config.psd1`:**

```powershell
@{
    Name       = 'OrchardCore'
    BaseBranch = 'main'

    Api = @{
        SolutionPath     = 'OrchardCore.sln'
        ProjectPath      = 'src\OrchardCore.Cms.Web'
        TestProjectPath  = 'test\OrchardCore.Tests'
        ResultsPath      = 'results'
        BaseUrl          = 'http://localhost:0'
        HealthEndpoint   = '/health'
        StartupTimeout   = 180                   # Large app, module discovery
        SourceCodePaths  = @('src\OrchardCore', 'src\OrchardCore.Modules')
        SourceFileGlob   = '*.cs'
    }

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

    # Override Hone defaults for expensive build/test cycles
    Loop = @{
        MaxExperiments = 3     # fewer experiments due to high cycle cost
    }
}
```

**OrchardCore-specific concerns:**
- **Build time (~3 min)** makes experiment cycles expensive — 6 experiments ×
  (build + test + measure) could take over an hour
- **Test time (~15 min)** for full suite — consider filtering to a subset
  relevant to the optimization (e.g., `--filter` by category)
- **Setup wizard** — OrchardCore requires initial setup via UI or recipe on first
  run; harness would need a setup recipe step or pre-configured SQLite DB
- **Multi-DB** — `Reset-Database.ps1` would need SQLite support (just delete the
  `.db` file) or PostgreSQL support depending on chosen backend
- **Recommendation:** Start with SQLite backend (simplest reset — delete file)
  and a narrowed test scope

---

## 8. Regression Testing & Test Coverage Strategy

A critical requirement: **generalizing the harness must not break `sample-api`
support**. Here's the strategy:

### 8.1 Harness Unit Tests (New)

Create a Pester test suite for the harness itself:

```
harness/
└── tests/
    ├── HoneHelpers.Tests.ps1
    ├── Resolve-Hook.Tests.ps1
    ├── Invoke-LifecycleHook.Tests.ps1
    ├── Merge-HoneConfig.Tests.ps1
    ├── Config-Validation.Tests.ps1
    └── fixtures/
        └── mock-target/
            ├── .hone/
            │   ├── config.psd1
            │   └── hooks/
            │       └── prepare.ps1
            └── MockApi.sln
```

**What to test:**
- `Resolve-Hook` prefers explicit config over convention-discovered hooks
- `Resolve-Hook` discovers `.hone/hooks/<name>.ps1` when config says `Default`
- `Resolve-Hook` resolves `Shared` type to `harness/hooks/<name>.ps1`
- `Invoke-LifecycleHook` executes Script-type hooks and validates return shape
- `Invoke-LifecycleHook` executes Command-type hooks and captures exit code
- `Invoke-LifecycleHook` handles Http-type hooks (calls endpoint, parses
  response)
- `Invoke-LifecycleHook` treats Skip-type hooks as successful no-ops
- `Merge-HoneConfig` correctly layers engine defaults → target config → CLI
  overrides
- Config validation rejects targets missing `.hone/config.psd1`
- Config validation rejects targets with missing `Hooks.*` declarations
- Config validation rejects unknown hook types
- Config validation rejects missing `ScaleTest.ScenarioPath` or missing scenario files
- Path security boundary is enforced relative to `$targetDir`

### 8.2 sample-api Smoke Test (CI Gate)

A lightweight CI workflow that runs the sample-api optimization loop in
"dry-run" or single-experiment mode:

```yaml
# .github/workflows/smoke-test-sample-api.yml
name: Smoke Test — sample-api
on: [push, pull_request]
steps:
  - uses: actions/checkout@v4
    with:
      submodules: true
  - name: Validate .hone/ config
    run: pwsh -Command "./harness/Test-HoneConfig.ps1 -TargetPath ./sample-api"
  - name: Smoke-test full lifecycle
    run: pwsh -Command "./harness/Invoke-HoneLoop.ps1 -TargetPath ./sample-api -DryRun -MaxExperiments 0"
```

This runs on every PR and ensures the harness still works end-to-end with the
reference target.

### 8.3 `.hone/` Discovery Tests

Validate that Hone correctly discovers and loads `.hone/config.psd1`:

```powershell
Describe '.hone/ discovery' {
    It 'sample-api .hone/config.psd1 loads and resolves all paths' {
        $targetDir = Join-Path $PSScriptRoot '../../sample-api'
        $config = Import-PowerShellDataFile (Join-Path $targetDir '.hone/config.psd1')
        Test-Path (Join-Path $targetDir $config.Api.SolutionPath) | Should -BeTrue
        Test-Path (Join-Path $targetDir $config.Api.ProjectPath)  | Should -BeTrue
    }

    It 'rejects target directory without .hone/' {
        { ./harness/Test-HoneConfig.ps1 -TargetPath '/tmp/empty' } | Should -Throw
    }

    It 'all declared hook scripts exist' {
        $targetDir = Join-Path $PSScriptRoot '../../sample-api'
        $config = Import-PowerShellDataFile (Join-Path $targetDir '.hone/config.psd1')
        foreach ($hook in $config.Hooks.GetEnumerator()) {
            if ($hook.Value.Type -eq 'Script') {
                Test-Path (Join-Path $targetDir $hook.Value.Path) | Should -BeTrue
            }
        }
    }
}
```

### 8.4 Hone Engine Config (Reduced)

With target config moved to `.hone/`, Hone's own `config.psd1` shrinks to
engine-only concerns:

```powershell
# harness/config.psd1 — engine defaults only
@{
    Tolerances = @{
        MaxRegressionPct  = 0.10
        MinImprovementPct = 0.05
        MaxStaleCount     = 3
    }

    Loop = @{
        MaxExperiments = 6
        Mode           = 'stacked'
        BranchPrefix   = 'hone/experiment'
    }

    Copilot = @{
        Model         = 'claude-opus-4.6'
        AnalysisModel = 'claude-opus-4.6'
        Timeout       = 120
    }

    Diagnostics = @{
        Enabled       = $true
        Collectors    = @('perfview-cpu', 'perfview-gc', 'dotnet-counters')
    }

    Classifier = @{
        SkipClassification = $false
    }
}
```

Targets can override any of these keys in their `.hone/config.psd1`.
`Merge-HoneConfig` applies target overrides on top of engine defaults.

### 8.5 Test Coverage Summary

| Layer | What's Tested | Runner | When |
|-------|---------------|--------|------|
| Harness unit tests | Hook resolution, config merge, security boundary | Pester | Every PR |
| Hook contract tests | Each shared hook returns correct shape, handles errors | Pester | Every PR |
| `.hone/` discovery tests | Config loads, paths resolve, hook scripts exist | Pester | Every PR |
| sample-api smoke test | Full build → test → measure via `-TargetPath ./sample-api` | CI workflow | Every PR |
| sample-api full loop | Complete optimization loop (1 experiment) | Manual / nightly | Pre-release |
| eShopOnWeb integration | Build → test → measure via `-TargetPath <eshop-fork>` | Manual initially → CI | Post-Phase 1 |

---

## 9. Implementation Phases

### 9.0 Current Completion Status

This section reflects the current repository state.

#### Phase 1a checklist

- [x] `sample-api` has a `.hone/config.psd1`
- [x] `sample-api` has `.hone/hooks/prepare.ps1`
- [x] `sample-api` scenarios live under `.hone/scenarios/`
- [x] `harness/config.psd1` has been reduced to engine-oriented defaults
- [x] `Resolve-Hook`, `Invoke-LifecycleHook`, and `Merge-HoneConfig` exist in `HoneHelpers.psm1`
- [x] Shared hooks exist for `dotnet-build`, `dotnet-start`, `dotnet-stop`, `dotnet-test`, `sqlserver-reset`, `health-poll`, `k6-run`, and `Invoke-Hook`
- [x] `Invoke-HoneLoop.ps1` is target-centric and uses `-TargetPath`
- [x] Hardcoded `sample-api` path handling has been broadly replaced with `$targetDir` in the main harness flow and supporting scripts
- [x] Measurement orchestration now uses lifecycle hooks for `Prepare`, `Start`, `Ready`, `Active`, `Cooldown`, `Stop`, and `Cleanup`
- [x] AI agent prompts use target name / target-relative paths
- [x] `docs/adapter-contracts.md` exists and includes the `.gitignore` template
- [x] `Stage-ExperimentArtifacts.ps1` now respects `Api.ResultsPath`
- [x] Hone root `.gitignore` no longer carries the old `sample-api/results/*` assumptions
- [x] Pester coverage exists for hook resolution, config merge, target validation, and results-path staging
- [ ] Full end-to-end loop verification against `sample-api` has not yet been re-run as part of this completion pass

#### Phase 1a partial / notable deviations

- [~] Warmup remains built into `Invoke-ScaleTests.ps1` via `ScaleTest.WarmupEnabled` and `WarmupScenarioPath`, rather than being a standalone shared `k6-warmup` hook everywhere. This matches the current `docs/adapter-contracts.md` contract.
- [ ] `sqlite-reset` is still not implemented in `harness/hooks/`; OrchardCore-specific reset work remains deferred with Phase 1d.
- [ ] Security-boundary test coverage exists in runtime code paths, but there is still room to expand dedicated Pester coverage around all path-traversal and git-staging surfaces.

#### Phase 1b checklist

- [x] An eShopOnWeb target exists under `OptimizationTargets\eShopOnWeb-Honed`
- [x] eShopOnWeb has `.hone/config.psd1`
- [x] eShopOnWeb has `.hone/hooks/prepare.ps1`
- [x] eShopOnWeb has `.hone/scenarios/`
- [ ] eShopOnWeb `.gitignore` has not been updated to the Hone results template
- [ ] Dry-run verification for the eShop target has not been re-run as part of this pass
- [ ] First real optimization experiment is still open
- [ ] Hook-contract refinement follow-up is still open

#### Phase 1d checklist

- [ ] OrchardCore target scaffolding has not been added
- [ ] SQLite reset / recipe re-apply flow is not implemented
- [ ] OrchardCore feasibility assessment is still open

### Phase 1a — Target-Centric Engine + Lifecycle Hooks

**Goal:** Make Hone a read-only engine invoked via `-TargetPath`. Replace every
hardcoded `sample-api` reference. Add lifecycle hooks. `sample-api` remains the
only target but gets its own `.hone/` directory.

1. [x] Add `.hone/config.psd1` to `sample-api` with all target-specific settings
2. [x] Add `.hone/hooks/prepare.ps1` to `sample-api` (wraps current sqlcmd reset)
3. [x] Move `sample-api/scale-tests/` → `sample-api/.hone/scenarios/`
4. [x] Reduce `harness/config.psd1` to engine-only settings (tolerances, models,
   loop, diagnostics)
5. [x] Add `Resolve-Hook`, `Invoke-LifecycleHook`, `Merge-HoneConfig` to
   `HoneHelpers.psm1`
6. [x] Create `harness/hooks/` with shared hooks extracted from current scripts
   (`dotnet-build.ps1`, `dotnet-start.ps1`, `dotnet-stop.ps1`,
   `sqlserver-reset.ps1`, `Invoke-Hook.ps1`)
7. [x] Replace `-ConfigPath` with `-TargetPath` on `Invoke-HoneLoop.ps1`
8. [x] Update all 8+ scripts that reference `'sample-api'` to use `$targetDir`
9. [x] Replace direct script calls in orchestration with `Invoke-LifecycleHook`
10. [x] Update AI agent prompts to use target name from `.hone/config.psd1`
11. [x] Write `docs/adapter-contracts.md` with `.hone/` specification including
    `.gitignore` template for results artifacts (see section 5.8)
12. [x] Update `Stage-ExperimentArtifacts.ps1` to use `$targetDir`-relative paths
13. [x] Clean up Hone root `.gitignore` (remove redundant `sample-api/results/*` rules)
14. [x] Add Pester tests for hook resolution, config merge, security boundary
15. [ ] Verify full loop still works: `Invoke-HoneLoop.ps1 -TargetPath .\sample-api`

### Phase 1b — eShopOnWeb Integration

**Goal:** Fork eShopOnWeb, add `.hone/`, and run Hone against it end-to-end.

1. [x] Fork `MicrosoftLearning/eShopOnWeb`
2. [x] Add `.hone/config.psd1` with eShop-specific paths and hooks
3. [x] Add `.hone/hooks/prepare.ps1` (dual-DbContext reset: Catalog + Identity)
4. [ ] Add `.gitignore` rules for `results/` using template from adapter contracts
5. [x] Write k6 scenarios in `.hone/scenarios/` for PublicApi surface
5. [ ] Verify: `Invoke-HoneLoop.ps1 -TargetPath C:\Projects\eShopOnWeb -DryRun`
6. [ ] Run first real optimization experiment
7. [ ] Document any hook contract refinements needed

### Phase 1d — OrchardCore Assessment

**Goal:** Fork OrchardCore, add `.hone/`, validate feasibility.

1. [ ] Fork `OrchardCMS/OrchardCore`
2. [ ] Add `.hone/config.psd1` with OrchardCore paths
3. [ ] Add `.hone/hooks/prepare.ps1` (SQLite reset + recipe re-apply)
4. [ ] Write minimal k6 scenario for REST API
5. [ ] Attempt build → test cycle (assess time cost)
6. [ ] Evaluate whether experiment cycle time is acceptable
7. [ ] Document findings and go/no-go recommendation

---

## 10. Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Refactoring breaks sample-api loop | Medium | High | Pester tests + CI smoke test before/after |
| eShopOnWeb dual-DB reset fails | Low | Medium | Test reset script against eShop manually first |
| OrchardCore build time makes experiments impractical | High | Medium | Can scope to PublicApi subset; defer to Phase 2 |
| Config profile merge has edge cases | Low | Low | Pester tests with multiple profile fixtures |
| AI agent prompts assume .NET patterns | Low | Low | Both new targets are also .NET; revisit for non-.NET in Phase 2 |
| External path model breaks CI reproducibility | Medium | Medium | Clone-on-demand fallback with pinned ref |

---

## 11. Out of Scope for Phase 1

These are explicitly deferred to keep Phase 1 focused:

- **Non-.NET targets** (Node.js, Go, Python) — the hook system *enables* this
  (provide `npm-start.ps1`, `go-build.ps1` hooks) but writing those hooks is
  deferred
- **Build and verify as hooks** — Phase 1 focuses on the measurement-cycle hooks
  (`prepare` through `cleanup`). Promoting `build` and `verify` to hooks is a
  natural Phase 2 extension
- **`.hone/context.md` agent integration** — targets can already provide
  `context.md` (the only optional file in `.hone/`), but the hook in the agent
  pipeline to automatically read and inject its content into prompts is deferred
- **In-flight reset via HTTP** — some targets may prefer resetting while the API
  is running (POST `/diag/reset`). The hook system supports this via `Http`-type
  hooks, but the orchestration loop currently calls `prepare` before `start`.
  Supporting a `prepare`-after-`start` flow is deferred
- **Cross-platform profiling** — PerfView/ETW collectors remain Windows/.NET-only
- **Docker-as-target** — targets must have source access for the fix loop
- **Removing sample-api submodule** — keep it as the reference; don't migrate to
  external path until Phase 1 is proven stable

---

## 12. Decision Log

| Decision | Rationale |
|----------|-----------|
| Hone as read-only engine, `.hone/` as single source of truth | Clean separation; target is self-describing; Hone stays generic |
| Invoke via `-TargetPath` | Simple CLI model; no config file pointing; mirrors Docker/Terraform pattern |
| Fork third-party targets, add `.hone/` to fork | Single source of truth per target; no overlay complexity; upstream sync via `git pull` |
| Config merge: engine defaults → `.hone/` → CLI flags | Targets can override defaults; CLI overrides everything; clean precedence |
| Lifecycle hooks over monolithic scripts | Targets have different reset/start/stop needs; hooks let each target declare its own |
| `Shared` hook type references Hone's `harness/hooks/` | Avoids duplicating common .NET hooks across every target |
| Hook resolution: explicit config → convention `.hone/hooks/` → default | Simple, predictable; explicit wins over magic |
| `prepare` runs before `start` | Current flow drops DB externally, then API recreates on startup; preserves this proven model |
| AI agents stay in Hone engine | Agents are Hone's intelligence; not target-specific. Future: `.hone/context.md` for target hints |
| Keep sample-api submodule | Reference implementation of `.hone/`; regression anchor; CI smoke tests |
| eShopOnWeb first, OrchardCore second | eShopOnWeb is same complexity tier; OrchardCore is a stretch goal |
| Pester for harness tests | Native PowerShell testing; industry standard for PS projects |
| Phases: 1a (engine), 1b (eShop), 1d (OrchardCore) | Collapsed profiles phase into 1a since `.hone/` eliminates need for separate profile work |
