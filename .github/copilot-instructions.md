# Copilot Instructions for Hone

## Project Context

Hone is an **agentic performance optimization harness** that automatically improves API service performance through an iterative loop of testing, measuring, analyzing, and implementing fixes.

## Tech Stack

- **Harness**: C# / .NET 10 — solution at `harness-csharp/Hone.slnx` (15 source projects, 15+1 test projects)
- **CLI entry point**: `Hone.Cli/Program.cs` using `System.CommandLine`
- **Configuration**: YAML via `YamlDotNet` with PascalCase naming convention
- **Target API**: .NET 6 Web API with Entity Framework Core 6 and SQL Server LocalDB
- **Load Testing**: k6 (Grafana) with JavaScript scenario scripts
- **E2E Testing**: xUnit with `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory)
- **AI Agent**: GitHub Copilot CLI (standalone `copilot` command) for optimization analysis

## Key Directories

| Directory | Purpose |
|-----------|---------|
| `harness-csharp/` | C# harness — the core of the project |
| `harness-csharp/src/` | 15 source projects across Core, Orchestration, Agents, Measurement, Lifecycle, SourceControl, Diagnostics, Reporting, Cli |
| `harness-csharp/tests/` | 15+1 test projects (xUnit, NSubstitute, FluentAssertions) |
| `harness-csharp/config.yaml` | Engine-wide default configuration |
| `sample-api/SampleApi/` | .NET 6 Web API — the optimization target |
| `sample-api/SampleApi.Tests/` | xUnit E2E tests — the regression gate |
| `sample-api/scale-tests/` | k6 load test scenarios and thresholds |
| `sample-api/.hone/` | Target Hone contract (config.yaml, scenarios) |
| `sample-api/.hone/results/` | Generated output (gitignored) |
| `docs/` | Architecture and usage documentation |

## C# Coding Conventions

- **Target framework:** `net10.0` — use .NET 10 APIs and idioms
- **Nullable reference types:** enabled globally — annotate all reference types
- **Async/await:** prefer throughout; never use `.Result` or `.Wait()`
- **Records:** use `record` / `sealed record` for immutable data types (domain models, results, events)
- **Sealed classes:** prefer `sealed` for non-abstract concrete classes
- **Internal by default:** types are `internal` unless there is an explicit reason to make them `public`
- **Immutability:** prefer `init`-only properties on records; avoid mutable state
- **Pattern matching:** prefer `switch` expressions and `is` patterns over chains of `if`/`else`
- **Expression bodies:** use for single-expression members
- **`var`:** only when the type is apparent from the right-hand side
- **Accessibility:** always specify explicit access modifiers
- **Banned APIs:** `DateTime` (use `DateTimeOffset`), `Thread.Sleep` (use `Task.Delay`), `File.ReadAllText` (use async overload), `ArrayList`/`Hashtable` (use generics) — enforced by `BannedSymbols.txt`

### Naming Conventions

- PascalCase for public/internal members
- `_camelCase` for private fields
- camelCase for locals and parameters
- `I`-prefix for interfaces (`IAgentRunner`, `IVersionControl`)
- `T`-prefix for type parameters

### Build Quality

- `TreatWarningsAsErrors` — all warnings are errors; zero-warning policy enforced in CI
- Third-party analyzers: `Meziantou.Analyzer`, `Microsoft.VisualStudio.Threading.Analyzers`, `Microsoft.CodeAnalysis.BannedApiAnalyzers`
- `dotnet format --verify-no-changes` is enforced in CI

### k6 (JavaScript)
- Export `options` and default function per k6 conventions
- Use `check()` for response validation
- Output JSON summary via `--out json` or `handleSummary()`
- Read base URL from `__ENV.BASE_URL`

## Agentic Loop Phases

1. **Build** → `DotnetBuildHook` (`dotnet build`)
2. **Verify** → `DotnetTestHook` (`dotnet test`, must pass 100%)
3. **Measure** → `ScaleTestOrchestrator` + `K6LoadTestRunner` (capture p95 latency, RPS, error rate)
4. **Analyze** → `AnalysisAgent` + `CpuHotspotsAnalyzer` + `MemoryGcAnalyzer` via `CopilotCliAgentRunner`
5. **Classify** → `ClassificationAgent` (narrow vs. architecture scope gate)
6. **Implement** → `ImplementerAgent` via `IterativeImplementerRunner` on a new git branch
7. **Compare** → `MetricComparer` (accept/reject decision)
8. **Publish** → `ExperimentBranchManager` (PR creation or revert)
9. **Repeat** → `HoneLoopRunner` until limit or plateau

## CLI Commands

| Command | Description |
|---------|-------------|
| `hone run --target <path>` | Run the full agentic optimization loop |
| `hone validate --target <path>` | Validate `.hone/config.yaml` without running experiments |
| `hone baseline --target <path>` | Establish a performance baseline |
| `hone results --target <path>` | Display experiment results |
| `hone dashboard --target <path>` | Generate HTML dashboard |

Common flags: `--max-experiments N`, `--dry-run`, `--model <model>`

## Important Design Decisions

- The sample API is the optimization target — the agentic loop discovers performance issues through measurement, not hints
- E2E tests use `WebApplicationFactory` so they don't require a running server
- Performance results are stored as JSON in `sample-api/.hone/results/` for comparison across experiments
- Each optimization attempt is made on a separate git branch for easy rollback
- Observability is pipeline-based: all harness events are emitted via `HoneEventBus` to registered `IHoneEventSink` implementations (console, JSONL log)
- Configuration uses a three-layer merge: engine defaults (`harness-csharp/config.yaml`) → target overrides (`.hone/config.yaml`) → CLI flags

