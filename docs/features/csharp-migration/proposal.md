# Proposal: Migrate Hone Harness from PowerShell to C#

> **Status:** Approved  
> **Author:** Copilot  
> **Date:** 2026-04-04  
> **Companion Documents:** [phased-plan.md](phased-plan.md) — detailed phase-by-phase implementation plan; [agent-team.md](agent-team.md) — migration delivery agent model and MVP team

---

## 1. Executive Summary

Hone is an agentic performance optimization harness that orchestrates an iterative loop of measuring, analyzing, experimenting, verifying, and publishing performance improvements for .NET APIs. The harness is currently implemented as ~11,000 lines of PowerShell across 87 files (35 orchestration scripts, 7 lifecycle hooks, 11 collector/analyzer plugin scripts, 25 Pester test files, and 19 configuration data files).

This proposal recommends migrating the harness to a C# .NET 10 console application, organized as a modular solution of libraries and a CLI host. This is a clean break from the PowerShell codebase — no backward compatibility layer, no gradual migration, no PowerShell SDK dependencies. The migration delivers type safety, first-class async I/O, a mature unit testing ecosystem, a single-language stack, an extensible observability system, and generic interfaces that enable future tool/provider swaps without harness changes.

---

## 2. Motivation

### 2.1 Current Pain Points

| Pain Point | Detail |
|-----------|--------|
| **Type safety** | PowerShell's dynamic typing means contract violations (malformed metric objects, missing config keys, wrong hook return shapes) are caught at runtime — often deep into a multi-hour optimization run. |
| **Testability** | Pester tests (~2,200 LOC) rely heavily on mocking external commands (`git`, `dotnet`, `k6`, `copilot`) via function substitution and environment variables. C# dependency injection enables cleaner isolation without process-level mocking. |
| **Async I/O** | The harness manages concurrent subprocesses (API server, k6, PerfView, dotnet-counters). PowerShell's job system is less ergonomic than `async`/`await` + `System.Diagnostics.Process` in C#. Several scripts already drop into .NET APIs directly (e.g., `ProcessStartInfo.ArgumentList` for Copilot invocation). |
| **IDE support** | PowerShell tooling (IntelliSense, refactoring, navigation) is limited compared to C# in Visual Studio / Rider / VS Code with OmniSharp. |
| **Onboarding** | The team works primarily in C#. PowerShell proficiency is a bottleneck for contributors modifying the harness. |
| **Module boundaries** | `HoneHelpers.psm1` (920 lines, 20 functions) is a flat bag of utilities. C# namespaces and project references enforce clean module boundaries. |
| **Observability** | `Write-Status` / `Write-HoneLog.ps1` produce flat console text with no structured event pipeline. No path to a GUI or TUI dashboard — real-time monitoring requires watching log files. |
| **Vendor lock-in** | The harness is tightly coupled to specific tools: Copilot CLI for agents, k6 for load testing, dotnet-counters for metrics, git for VCS, GitHub for hosting. Swapping any tool requires touching many scripts. |

### 2.2 What Works Well Today (Preserve)

| Strength | Migration Strategy |
|----------|-------------------|
| **Deterministic orchestration** — scripted control flow, not agent-orchestrated agents | Translate directly to C# method calls with the same sequential flow |
| **Blackbox target design** — `.hone/` contract, lifecycle hooks, config merge | Model as strongly-typed interfaces (`ILifecycleHook`, `ICollectorPlugin`, `IAnalyzerPlugin`) |
| **Plugin architecture** — drop-in collectors and analyzers with metadata-driven discovery | All plugins become native C# implementations of `ICollectorPlugin` / `IAnalyzerPlugin` |
| **Test fixture system** — deterministic harness self-testing with fixture targets | Migrate fixtures to xUnit with `ITestOutputHelper`; rebuild fixture targets in YAML config format |
| **Structured data everywhere** — PSCustomObjects, JSON metrics, typed results | Replace with C# records/classes with JSON serialization |
| **Config merge** — engine defaults → target overrides → CLI flags | Typed merge function with YAML config files |

---

## 3. Proposed Architecture

### 3.1 Solution Structure

```
Hone.slnx                          # .NET 10 XML-based solution format (SDK default)
├── src/
│   ├── Hone.Core/                  # Domain models, contracts, config, observability, shared utilities
│   ├── Hone.Orchestration/         # Main loop, phase orchestration, queue management
│   ├── Hone.Agents.Core/           # IAgentRunner contract + shared agent infrastructure
│   ├── Hone.Agents.Loop/           # Optimization loop agents (analyst, classifier, implementer)
│   ├── Hone.Agents.Preparation/    # Target preparation agents (compatibility)
│   ├── Hone.Agents.CopilotCli/     # IAgentRunner implementation: GitHub Copilot CLI
│   ├── Hone.Measurement/           # Load test runner, metric parsing, comparison logic
│   ├── Hone.Measurement.K6/        # ILoadTestRunner implementation: k6 (Grafana)
│   ├── Hone.Measurement.DotnetCounters/ # IRuntimeMetricsCollector implementation: dotnet-counters
│   ├── Hone.Diagnostics/           # Diagnostic framework: collectors + analyzers
│   ├── Hone.Lifecycle/             # Hook resolution, dispatch, target management
│   ├── Hone.SourceControl/         # VCS + hosting abstractions (branch, PR, stacked diffs)
│   ├── Hone.SourceControl.Git/     # IVersionControl implementation: git + GitHub CLI
│   ├── Hone.Reporting/             # Dashboard, results display, RCA, PR body
│   └── Hone.Cli/                   # Console host, argument parsing, entry point
├── tests/
│   ├── Hone.Core.Tests/
│   ├── Hone.Orchestration.Tests/
│   ├── Hone.Agents.Core.Tests/
│   ├── Hone.Agents.Loop.Tests/
│   ├── Hone.Agents.Preparation.Tests/
│   ├── Hone.Agents.CopilotCli.Tests/
│   ├── Hone.Measurement.Tests/
│   ├── Hone.Measurement.K6.Tests/
│   ├── Hone.Measurement.DotnetCounters.Tests/
│   ├── Hone.Diagnostics.Tests/
│   ├── Hone.Lifecycle.Tests/
│   ├── Hone.SourceControl.Tests/
│   ├── Hone.SourceControl.Git.Tests/
│   ├── Hone.Reporting.Tests/
│   └── Hone.Integration.Tests/    # E2E scenarios using fixture targets
└── test-fixtures/                  # Shared fixture targets (rebuilt with YAML config)
```

### 3.2 Module Responsibilities

#### Hone.Core
The foundational library with zero external dependencies beyond the BCL. Every other project references this.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `Models/` | PSCustomObject shapes scattered across scripts | `MetricSet`, `ComparisonResult`, `ExperimentOutcome`, `Opportunity`, `QueueItem`, `MachineInfo`, `ExperimentMetadata` as C# records |
| `Config/` | `config.psd1` + `Get-HoneConfig` + `Merge-HoneConfig` | `HoneConfig` record hierarchy (`ApiConfig`, `TolerancesConfig`, `ScaleTestConfig`, `LoopConfig`, `AgentConfig`, `DiagnosticsConfig`, `LoggingConfig`, `ImplementerConfig`) with YAML loading and layered merge logic |
| `Contracts/` | Plugin contracts doc + hook return shapes | `ICollectorPlugin`, `IAnalyzerPlugin`, `ILifecycleHook`, `IProcessRunner`, `IVersionControl`, `ICodeHost`, `ILoadTestRunner`, `IRuntimeMetricsCollector`, `IAgentRunner` interfaces |
| `Observability/` | `Write-Status`, `Write-HoneLog.ps1` | `IHoneEventSink` — structured event pipeline (phase transitions, metric snapshots, agent invocations, experiment outcomes). Enables console, TUI, GUI, and remote dashboard consumers. Built-in `ConsoleEventSink` for CLI mode. |
| `Utilities/` | `Limit-String`, `Copy-HoneHashtable` | `StringUtils`, `JsonUtils` |

#### Hone.Orchestration
The brain of the system. Contains the main loop and phase coordination.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `HoneLoop` | `Invoke-HoneLoop.ps1` (1,499 lines) | `HoneLoopRunner` class with async phase execution |
| `IterativeImplementer` | `Invoke-IterativeFix.ps1` (557 lines) | `IterativeImplementerRunner` with retry logic, diff guards, test file guard |
| `QueueManager` | `Manage-OptimizationQueue.ps1` (214 lines) | `OptimizationQueueManager` with atomic JSON writes |
| `MetadataManager` | `Update-OptimizationMetadata.ps1` (152 lines) | `ExperimentMetadataManager` |
| `FailureHandler` | `Invoke-FailureHandler.ps1` (96 lines) | `ExperimentFailureHandler` |
| `ArtifactStager` | `Stage-ExperimentArtifacts.ps1` (100 lines) | `ArtifactStager` |

#### Hone.Agents.Core
Defines the generic agent runner contract. This lives in its own project so both loop agents and preparation agents can reference it, and so future agent runner implementations (Claude Code, API-based) only need to implement a single interface.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `IAgentRunner` | Implicit contract in `Invoke-CopilotWithTimeout` | Generic interface for invoking any AI agent backend |
| `AgentInvoker` | `Invoke-CopilotAgent.ps1` (191 lines) | Generic agent invocation: model resolution, JSON extraction, NaN sanitization, retry on parse failure. Delegates to `IAgentRunner` |
| `AgentResult<T>` | Per-agent return objects | Typed response wrapper with raw output, parsed result, and metadata |

```csharp
public interface IAgentRunner
{
    Task<AgentRunResult> InvokeAsync(AgentInvocation invocation, CancellationToken ct = default);
}

public record AgentInvocation(
    string AgentName, string Prompt, string? Model = null,
    TimeSpan? Timeout = null, string? WorkingDirectory = null);

public record AgentRunResult(
    bool Success, string Output, bool TimedOut, int ExitCode);
```

#### Hone.Agents.Loop
The optimization loop agents — used during the main experiment cycle. References `Hone.Agents.Core` for `IAgentRunner`.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `AnalysisAgent` | `Invoke-AnalysisAgent.ps1` + `Build-AnalysisContext.ps1` | `AnalysisAgent` + `AnalysisContextBuilder` |
| `ClassificationAgent` | `Invoke-ClassificationAgent.ps1` | `ClassificationAgent` with JSON retry |
| `ImplementerAgent` | `Invoke-FixAgent.ps1` | `ImplementerAgent` with code extraction (renamed from Fixer) |

#### Hone.Agents.Preparation
Agents used to prepare or validate a target before optimization begins. References `Hone.Agents.Core` for `IAgentRunner`.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `CompatibilityAgent` | `Invoke-CompatibilityAgent.ps1` | `CompatibilityAgent` — validates target suitability |

#### Hone.Agents.CopilotCli
The `IAgentRunner` implementation that shells out to the GitHub Copilot CLI. This is the only implementation for now; future implementations (Claude Code, direct API) would be separate projects implementing the same `IAgentRunner` interface.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `CopilotCliAgentRunner` | `Invoke-CopilotWithTimeout` (HoneHelpers.psm1) | `IAgentRunner` implementation using `ProcessStartInfo.ArgumentList`, timeout guard, UTF-8 encoding |

#### Hone.Measurement
Everything related to performance measurement and comparison. Defines generic interfaces for load testing and runtime metrics collection — the actual tool implementations live in separate projects.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `ILoadTestRunner` | Implicit k6 coupling | Generic interface: run a load test scenario, return structured metrics |
| `IRuntimeMetricsCollector` | Implicit dotnet-counters coupling | Generic interface: start/stop runtime metric collection, return parsed counters |
| `MetricComparer` | `Compare-Results.ps1` (367 lines) | `MetricComparer` — pure function, no I/O |
| `BaselineMeasurer` | `Get-PerformanceBaseline.ps1` (196 lines) | `BaselineMeasurer` — uses `ILoadTestRunner` and `IRuntimeMetricsCollector` |
| `CooldownManager` | `Invoke-Cooldown.ps1` | `CooldownManager` |

```csharp
public interface ILoadTestRunner
{
    Task<LoadTestResult> RunAsync(LoadTestOptions options, CancellationToken ct = default);
}

public interface IRuntimeMetricsCollector
{
    Task<MetricsCollectionHandle> StartAsync(int processId, RuntimeMetricsOptions options, CancellationToken ct = default);
    Task<RuntimeMetricsResult> StopAndParseAsync(MetricsCollectionHandle handle, CancellationToken ct = default);
}
```

#### Hone.Measurement.K6
The `ILoadTestRunner` implementation for k6 (Grafana). Handles k6-specific concerns: JSON summary parsing, warmup scenarios, multi-run median, `--out json`, `__ENV.BASE_URL`.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `K6LoadTestRunner` | `Invoke-ScaleTests.ps1` (480 lines) | `ILoadTestRunner` impl with k6 CLI invocation |
| `K6SummaryParser` | `Convert-HoneK6SummaryToMetricSet` | k6 JSON summary → `MetricSet` |

#### Hone.Measurement.DotnetCounters
The `IRuntimeMetricsCollector` implementation for `dotnet-counters`. Handles CSV collection, process attachment, and counter parsing.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `DotnetCountersCollector` | `Start-DotnetCounters.ps1` + `Stop-DotnetCounters.ps1` | `IRuntimeMetricsCollector` impl for `dotnet-counters` CSV parsing |

#### Hone.Diagnostics
The plugin framework for deep profiling. All plugins are native C# implementations.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `PluginDiscovery` | Directory scanning in `Invoke-DiagnosticCollection.ps1` | `PluginDiscoveryService` — discovers collectors and analyzers by convention |
| `CollectionOrchestrator` | `Invoke-DiagnosticCollection.ps1` (235 lines) | `DiagnosticCollectionOrchestrator` — manages multi-pass group scheduling |
| `MeasurementOrchestrator` | `Invoke-DiagnosticMeasurement.ps1` (260 lines) | `DiagnosticMeasurementOrchestrator` |
| `AnalysisOrchestrator` | `Invoke-DiagnosticAnalysis.ps1` (131 lines) | `DiagnosticAnalysisOrchestrator` |
| Built-in collectors | `collectors/perfview-cpu/`, `perfview-gc/`, `dotnet-counters/` | Native C# implementations of `ICollectorPlugin` |
| Built-in analyzers | `analyzers/cpu-hotspots/`, `memory-gc/` | Native C# implementations of `IAnalyzerPlugin` |

#### Hone.Lifecycle
Target management and lifecycle hook dispatch. All hooks are native C# implementations.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `HookResolver` | `Resolve-Hook` in HoneHelpers.psm1 | `HookResolver` — Command/Http/Skip dispatch (Script/Shared types replaced by native C# hooks) |
| `HookDispatcher` | `Invoke-LifecycleHook` + `hooks/Invoke-Hook.ps1` | `LifecycleHookDispatcher` |
| Built-in hooks | `hooks/dotnet-*.ps1`, `health-poll.ps1`, `k6-run.ps1` | Native C# hook implementations |
| `ConfigValidator` | `Test-HoneConfig.ps1` (280 lines) | `ConfigValidator` with typed validation rules |

#### Hone.SourceControl
Abstractions for version control and code hosting operations. Generic interfaces that enable future migration from git/GitHub to other providers (e.g., Azure DevOps, GitLab).

| Component | Replaces | Contents |
|-----------|----------|----------|
| `IVersionControl` | Implicit git coupling | Generic VCS interface: branch, commit, checkout, revert, diff |
| `ICodeHost` | Implicit GitHub coupling | Generic hosting interface: create PR, push branch, get PR status |
| `ExperimentBranchManager` | Branch logic in `Apply-Suggestion.ps1`, `Revert-ExperimentCode.ps1` | Orchestrates branch operations via `IVersionControl` |
| `PullRequestManager` | `New-ExperimentPR` + `Build-StackNote` | Orchestrates PR operations via `ICodeHost` |

```csharp
public interface IVersionControl
{
    Task<string> GetCurrentBranchAsync(string workingDir, CancellationToken ct = default);
    Task CheckoutAsync(string workingDir, string branch, bool create = false, CancellationToken ct = default);
    Task CommitAsync(string workingDir, string message, IEnumerable<string>? paths = null, CancellationToken ct = default);
    Task<string> GetDiffAsync(string workingDir, string? baseBranch = null, CancellationToken ct = default);
    Task RevertLastCommitAsync(string workingDir, CancellationToken ct = default);
}

public interface ICodeHost
{
    Task<PushResult> PushBranchAsync(string workingDir, string branch, CancellationToken ct = default);
    Task<PullRequestResult> CreatePullRequestAsync(CreatePrOptions options, CancellationToken ct = default);
    Task<PullRequestStatus> GetPullRequestStatusAsync(int prNumber, CancellationToken ct = default);
}
```

#### Hone.SourceControl.Git
The `IVersionControl` and `ICodeHost` implementations for git + GitHub CLI. This is the only implementation for now; future implementations (Azure DevOps, GitLab) would be separate projects.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `GitVersionControl` | Inline `git` calls throughout | `IVersionControl` impl using `git` CLI |
| `GitHubCodeHost` | `gh pr create` calls in HoneHelpers.psm1 | `ICodeHost` impl using `gh` CLI |

#### Hone.Reporting
Result visualization and export.

| Component | Replaces | Contents |
|-----------|----------|----------|
| `ResultsRenderer` | `Show-Results.ps1` (378 lines) | Console table renderer |
| `DashboardExporter` | `Export-Dashboard.ps1` (1,000 lines) | HTML dashboard generator |
| `PrBodyBuilder` | `Build-PRBody.ps1` (91 lines) | PR description generator |
| `RcaExporter` | `Export-ExperimentRCA.ps1` (164 lines) | RCA markdown generator |

#### Hone.Cli
Thin console host — argument parsing, DI container setup, and delegation to `Hone.Orchestration`.

| Component | Contents |
|-----------|----------|
| `Program.cs` | Entry point, `CommandLineParser` or `System.CommandLine` |
| `ServiceRegistration.cs` | DI container wiring for all modules |

### 3.3 Key Design Decisions

#### 3.3.1 Configuration Format: YAML

The current `.psd1` configuration is PowerShell-specific and cannot be loaded without a PowerShell runtime. The C# harness uses **YAML** as its configuration format.

**Options considered:**

| Format | Pros | Cons | Verdict |
|--------|------|------|---------|
| **YAML** | Human-readable, widely used in DevOps (k8s, GitHub Actions, Docker Compose), supports comments, mature C# libraries (YamlDotNet) | Whitespace-sensitive, no schema validation built-in | ✅ **Selected** |
| **TOML** | Simple, explicit, no ambiguity, supports comments | Less familiar in .NET ecosystem, fewer C# libraries | Strong alternative |
| **JSON** | Native .NET support via `System.Text.Json`, schema validation via JSON Schema | No comments, verbose for nested config, poor human editability | Rejected for primary format (used for data files/metrics only) |
| **JSON5** | Comments + trailing commas over JSON | Non-standard, limited library support | Rejected |
| **.psd1** | Backward compatible | Requires PowerShell SDK dependency, PowerShell-specific | Rejected — clean break |

Config hierarchy uses strongly-typed C# records:

```csharp
public record HoneConfig(
    ApiConfig Api,
    TolerancesConfig Tolerances,
    ScaleTestConfig ScaleTest,
    LoopConfig Loop,
    AgentConfig Agents,
    DiagnosticsConfig Diagnostics,
    LoggingConfig Logging,
    ImplementerConfig Implementer
);
```

The merge function becomes a typed method: `HoneConfig.Merge(engine, target, cliOverrides)`.

#### 3.3.2 Target `.hone/` Contract Updated

The `.hone/` directory contract migrates from `.psd1` to YAML:

```
<target-repo>/
└── .hone/
    ├── config.yaml              # REQUIRED — target configuration (was config.psd1)
    ├── hooks/                   # REQUIRED — lifecycle hook definitions (in config.yaml)
    ├── scenarios/               # REQUIRED — k6 load test scenarios
    └── context.md               # OPTIONAL — project-specific AI hints
```

Existing targets (sample-api, eShopOnWeb) will be migrated to the new YAML format as part of this effort. No backward compatibility with `.psd1` targets is maintained.

#### 3.3.3 Observability: Structured Event Pipeline

The current harness uses `Write-Status` (console text) and `Write-HoneLog.ps1` (JSONL file) as separate, disconnected systems. The C# harness introduces a unified **observability pipeline** via `IHoneEventSink`:

```csharp
public interface IHoneEventSink
{
    void Emit(HoneEvent @event);
}

public abstract record HoneEvent(DateTimeOffset Timestamp, int? Experiment);
public record PhaseStarted(string Phase, int Experiment, ...) : HoneEvent(...);
public record PhaseCompleted(string Phase, int Experiment, TimeSpan Duration, bool Success, ...) : HoneEvent(...);
public record MetricSnapshot(int Experiment, MetricSet Metrics, ...) : HoneEvent(...);
public record AgentInvoked(string AgentName, string Model, TimeSpan Duration, bool Success, ...) : HoneEvent(...);
public record ExperimentOutcomeEvent(int Experiment, string Outcome, ...) : HoneEvent(...);
public record DiagnosticProgress(string CollectorName, string Stage, ...) : HoneEvent(...);
```

Multiple sinks can be registered simultaneously:

| Sink | Purpose |
|------|---------|
| `ConsoleEventSink` | Timestamped console output (replaces `Write-Status`) |
| `JsonLogEventSink` | Structured JSONL file with rotation (replaces `Write-HoneLog.ps1`) |
| `TuiEventSink` (future) | Real-time terminal UI dashboard (e.g., via Spectre.Console or Terminal.Gui) |
| `GuiEventSink` (future) | WebSocket/gRPC feed for a desktop or web GUI |
| `WebhookEventSink` (future) | HTTP POST events to external monitoring systems |

This design makes the harness observable from any frontend without modifying the orchestration code.

#### 3.3.4 IAgentRunner: Backend-Agnostic Agent Invocation

The `IAgentRunner` interface in `Hone.Agents.Core` decouples agent invocation from any specific AI tool. The only implementation for now is `CopilotCliAgentRunner` in `Hone.Agents.CopilotCli`, but the interface enables future alternatives:

| Implementation | Backend | Status |
|---------------|---------|--------|
| `CopilotCliAgentRunner` | GitHub Copilot CLI (`copilot` process) | ✅ Now |
| `ClaudeCodeAgentRunner` | Claude Code CLI | 🔮 Future |
| `AnthropicApiAgentRunner` | Anthropic Messages API (direct HTTP) | 🔮 Future |
| `OpenAiApiAgentRunner` | OpenAI Chat Completions API | 🔮 Future |

#### 3.3.5 Generic Tool Interfaces

Several harness operations are currently coupled to specific tools. The C# harness defines generic interfaces with the current tool as the sole implementation:

| Interface | Current Implementation | Future Alternatives |
|-----------|----------------------|-------------------|
| `ILoadTestRunner` | `K6LoadTestRunner` | Locust, Artillery, NBomber, custom |
| `IRuntimeMetricsCollector` | `DotnetCountersCollector` | Prometheus, custom ETW, OpenTelemetry |
| `IVersionControl` | `GitVersionControl` | Mercurial, SVN (unlikely but possible) |
| `ICodeHost` | `GitHubCodeHost` | Azure DevOps, GitLab, Bitbucket |
| `IAgentRunner` | `CopilotCliAgentRunner` | Claude Code, direct API calls |

#### 3.3.6 Process Execution Abstraction

Many harness operations shell out to external tools (`dotnet`, `k6`, `git`, `gh`, `copilot`, `PerfView`). All process execution goes through an `IProcessRunner` interface:

```csharp
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
```

This is the primary seam for unit testing — tests inject a mock `IProcessRunner` that returns predetermined output, replacing the current pattern of environment variables and mock response files.

#### 3.3.7 Strict Code Quality: Warnings as Errors + Advanced Analyzers

The C# codebase enforces **zero-tolerance for warnings** via build-time enforcement, combining built-in .NET analyzers with a curated set of third-party Roslyn analyzers. This catches type safety issues, async bugs, security vulnerabilities, and project convention violations at compile time — not during multi-hour optimization runs.

##### Built-in Enforcement (`Directory.Build.props`)

```xml
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <AnalysisLevel>latest-all</AnalysisLevel>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <EnableNETAnalyzers>true</EnableNETAnalyzers>
  <NuGetAudit>true</NuGetAudit>
  <NuGetAuditLevel>low</NuGetAuditLevel>
  <NuGetAuditMode>all</NuGetAuditMode>
  <NoWarn />
</PropertyGroup>
```

| Setting | Effect |
|---------|--------|
| `TreatWarningsAsErrors` | Every C# compiler warning (CS*) becomes a build error — the build fails if any warnings exist |
| `AnalysisLevel=latest-all` | Enables all .NET analyzers at the latest rule set (including performance, reliability, security, naming) |
| `EnforceCodeStyleInBuild` | Code style rules (IDE*) are enforced at build time, not just in the editor |
| `EnableNETAnalyzers` | Enables the built-in Roslyn analyzers for API design, globalization, performance, etc. |
| `NuGetAudit` + `NuGetAuditLevel=low` + `NuGetAuditMode=all` | Fails the build if any direct or transitive NuGet dependency has a known CVE — supply-chain security at zero cost |
| `NoWarn` (empty) | Explicit: no globally suppressed warnings. Per-project `<NoWarn>` in `.csproj` files is prohibited |

##### Third-Party Roslyn Analyzers

Five third-party analyzer packages are included as `PrivateAssets="all"` (dev-only, not shipped). Selected for high signal, low overlap with the built-in `AnalysisLevel=latest-all` rule set, and active maintenance.

| Package | Rules | What It Catches | Why It's Included |
|---------|-------|----------------|-------------------|
| **Meziantou.Analyzer** | ~200 | String comparison culture, regex perf, async void, `IDisposable` misuse, equality gotchas | Best general-purpose community analyzer; fills gaps the built-in rules miss. Maintained by Gérald Barré (Microsoft). |
| **Microsoft.VisualStudio.Threading.Analyzers** | ~30 | Async deadlocks, fire-and-forget tasks, sync-over-async, missing `ConfigureAwait` | First-party Microsoft. Critical for a harness managing concurrent subprocesses (k6, PerfView, dotnet-counters). Catches real bugs nothing else detects. |
| **Microsoft.CodeAnalysis.BannedApiAnalyzers** | Custom | Project-specific banned API usage (see Banned API Enforcement below) | First-party Microsoft. Near-zero noise — you control exactly what's banned via `BannedSymbols.txt`. |
| **xunit.analyzers** | ~40 | xUnit anti-patterns: wrong assert methods, async void tests, missing `[Fact]` | First-party xUnit team. Lightweight. Using xUnit for all tests — no reason not to include. |
| **NSubstitute.Analyzers.CSharp** | ~15 | Non-virtual member substitution, ambiguous argument matchers | Catches mocking errors that would otherwise be runtime failures. Lightweight and focused. |

**Options evaluated and excluded:**

| Package | Why Excluded |
|---------|-------------|
| **Roslynator.Analyzers** | ~500 rules with heavy overlap against `AnalysisLevel=latest-all` + Meziantou. Tuning severity for 500 rules is a maintenance tax — the unique value is mostly cosmetic simplification. |
| **SonarAnalyzer.CSharp** | Designed for SonarQube/SonarCloud integration — used standalone it's ~400 rules with massive overlap. Cognitive complexity is the one useful unique feature, but not worth pulling the whole package. |
| **IDisposableAnalyzers** | Historical maintenance gaps. Meziantou (MA0055, MA0058) + built-in CA2000 already cover the important `IDisposable` patterns. |
| **StyleCop.Analyzers** | Significant overlap with `.editorconfig` + `AnalysisLevel=latest-all`. Adds maintenance burden without enough incremental value. |
| **ErrorProne.NET** | Less actively maintained. Overlaps with Meziantou. |

##### Banned API Enforcement (`BannedSymbols.txt`)

A `BannedSymbols.txt` file at the solution root is wired to all projects via `Directory.Build.targets` using `Microsoft.CodeAnalysis.BannedApiAnalyzers`. This enforces project-specific conventions at compile time:

| Banned API | Required Replacement | Rationale |
|-----------|---------------------|-----------|
| `System.DateTime` | `DateTimeOffset` | Timezone safety — the harness logs timestamps across multi-hour runs |
| `Thread.Sleep(int)` | `Task.Delay` | Async-first codebase |
| `File.ReadAllText` | `File.ReadAllTextAsync` | Async I/O |
| `File.WriteAllText` | `File.WriteAllTextAsync` | Async I/O |
| `ArrayList` | `List<T>` | Type safety |
| `Hashtable` | `Dictionary<TKey,TValue>` | Type safety |

The banned list is scoped to `src/` projects. Test projects may use `#pragma warning disable RS0030` with a documented justification where synchronous I/O is more readable in test setup.

##### Code Style (`.editorconfig`)

The `.editorconfig` at the solution root defines project-wide style rules with severity `warning` — which `TreatWarningsAsErrors` promotes to errors:

- **Naming**: PascalCase for public members, `_camelCase` for private fields, camelCase for locals/parameters, `I`-prefix for interfaces, `T`-prefix for type parameters
- **Code style**: `var` only when type is apparent, expression-body for single-line members, pattern matching preferred, explicit accessibility modifiers required
- **Formatting**: Allman brace style, spaces not tabs, sorted usings with `System` first
- **Analyzer severity tuning**: Specific Meziantou/Threading rules tuned per project needs — some demoted to `suggestion` where too noisy for this codebase

##### CI Enforcement

- `dotnet build Hone.slnx` — fails on any warning (via `TreatWarningsAsErrors` in `Directory.Build.props`, no CLI flags needed)
- `dotnet format --verify-no-changes` — catches formatting and style drift that local builds may miss
- `dotnet test` — all test projects with Coverlet code coverage reporting
- NuGet audit — automatically fails on vulnerable dependencies (via `NuGetAudit` in `Directory.Build.props`)

No suppressions are allowed without a documented justification in a `#pragma warning disable` with an accompanying comment explaining why.

---

## 4. Testing Strategy

### 4.1 Test Pyramid

```
                    ┌─────────────┐
                    │ Integration │  fixture-target E2E scenarios
                    │   Tests     │  (happy-path, regression, build-failure, etc.)
                    ├─────────────┤
                    │  Component  │  multi-class collaboration tests
                    │   Tests     │  (agent + parser, hook + dispatch)
                    ├─────────────┤
                    │    Unit     │  single-class logic tests
                    │   Tests     │  (MetricComparer, QueueManager, ConfigMerge)
                    └─────────────┘
```

### 4.2 Unit Test Coverage Targets

Every C# module has a corresponding `*.Tests` project. The following are the high-value unit test surfaces, mapped from existing Pester coverage:

| Module | Key Test Surfaces | Existing Pester Equivalent |
|--------|------------------|---------------------------|
| **Hone.Core** | Config merge (engine + target + CLI), config validation, metric set serialization, string utilities, observability event pipeline | `Config-Validation.Tests.ps1` |
| **Hone.Measurement** | `MetricComparer` — improvement/regression/stale/efficiency tiebreaker with absolute thresholds; load test result parsing; multi-run median selection | `Compare-Results.Tests.ps1`, `Get-PerformanceBaseline.Tests.ps1` |
| **Hone.Measurement.K6** | k6 JSON parsing, k6-specific scenario options, summary conversion | `Compare-Results.Tests.ps1` (metric parsing portions) |
| **Hone.Measurement.DotnetCounters** | dotnet-counters CSV parsing, process attachment, provider configuration | `Start-DotnetCounters.Tests.ps1`, `Stop-DotnetCounters.Tests.ps1` |
| **Hone.Orchestration** | Queue init/consume/mark/sync; metadata recording; failure handler revert + queue update; iterative implementer retry/diff-guard/test-guard | `Manage-OptimizationQueue.Tests.ps1`, `Invoke-IterativeFix.Tests.ps1`, `Invoke-FailureHandler.Tests.ps1`, `Update-OptimizationMetadata.Tests.ps1` |
| **Hone.Agents.Core** | Agent invocation with timeout, JSON extraction, NaN sanitization, retry on parse failure | `Invoke-CopilotAgent.Tests.ps1` |
| **Hone.Agents.Loop** | Prompt construction (analysis context builder); opportunity parsing; classification decision; code block extraction | `Build-AnalysisContext.Tests.ps1`, `Invoke-AnalysisAgent.Tests.ps1`, `Invoke-ClassificationAgent.Tests.ps1`, `Invoke-FixAgent.Tests.ps1` |
| **Hone.Agents.CopilotCli** | Copilot CLI process invocation, argument quoting, UTF-8 encoding | `Invoke-CopilotAgent.Tests.ps1` (process-level portions) |
| **Hone.Lifecycle** | Hook resolution (Command/Http/Skip); dispatch with fixture override; config validation of hooks | `Resolve-Hook.Tests.ps1`, `Invoke-LifecycleHook.Tests.ps1` |
| **Hone.SourceControl** | Branch creation/checkout/revert; PR body generation; stack note generation; experiment branch push | `Apply-Suggestion.Tests.ps1`, `Revert-ExperimentCode.Tests.ps1`, `Build-PRBody.Tests.ps1` |
| **Hone.Reporting** | RCA markdown generation; dashboard HTML generation; results table formatting | `Stage-ExperimentArtifacts.Tests.ps1` |

### 4.3 Integration Test Strategy

Integration tests use rebuilt fixture targets with YAML configuration. Each fixture target contains a `.hone/` directory with deterministic config, mock hooks, and pre-baked agent responses.

The fixture system translates directly:

| Pester Pattern | C# Equivalent |
|---------------|---------------|
| `New-HoneTestTarget` | `TestTargetBuilder` — fluent builder creating temp directories with `.hone/` structure |
| `Copy-HoneTargetFixture` | `FixtureTargetLoader` — copies pre-built fixture targets to temp directories |
| `Initialize-HoneTargetRepository` | `TestVcsRepository` — initializes VCS repos for branch testing |
| `$env:HONE_HARNESS_TEST_TARGET_DIR` | Constructor injection of `ITargetContext` |
| Mock copilot responses via `-MockResponsePath` | `FakeAgentRunner : IAgentRunner` returning file-based responses |
| Pester's `TestDrive` | xUnit's `IDisposable` pattern with temp directory cleanup |

### 4.4 Validation Strategy

Each phase is independently implementable and testable. Unit tests for a phase must all pass before moving to the next phase. There is **no requirement for the harness to work end-to-end until all phases are complete** — no hybrid PowerShell/C# mode exists. The PowerShell harness remains the working system until the full C# migration is done.

1. **Snapshot tests** — capture expected output for known inputs (metric comparisons, prompt generation, queue state transitions) as verified JSON files using `Verify.Xunit`
2. **C# tests assert against these snapshots** — any deviation from expected behavior fails the build

---

## 5. Risk Assessment

| Risk | Severity | Mitigation |
|------|----------|------------|
| **Behavioral regression** — subtle differences in metric comparison, config merge, or queue state | High | Comprehensive snapshot tests and fixture-based unit tests per phase |
| **Config format migration** — converting `.psd1` to YAML may miss edge cases | Medium | Validate converted YAML produces identical `HoneConfig` objects; manual review of each target's config |
| **Process execution differences** — stdout/stderr handling, exit code semantics, encoding | Medium | `IProcessRunner` abstraction with integration tests against real tools |
| **Migration duration** — large surface area could stall delivery | Medium | Phased approach; each phase is independently testable with full unit test coverage |
| **Windows-specific behavior** — PerfView, LocalDB, process management | Low | Platform abstractions where needed; initial scope remains Windows |

---

## 6. What's Out of Scope

- **Rewriting the target API or its tests** — the sample-api and eShopOnWeb remain .NET projects (though their `.hone/` configs will be converted to YAML)
- **Rewriting k6 scenarios** — JavaScript load tests are unchanged
- **Rewriting agent definitions** — `.agent.md` files and prompts are unchanged
- **Cross-platform support** — initial C# implementation targets Windows; cross-platform is a future goal
- **New features** — migration is behavior-preserving; future extensions (COE agent, knowledge base) are separate work
- **TUI/GUI implementation** — the observability pipeline enables these but only `ConsoleEventSink` ships initially
- **Alternative IAgentRunner implementations** — only `CopilotCliAgentRunner` ships; Claude Code / API runners are future work
- **Alternative ICodeHost implementations** — only `GitHubCodeHost` ships; Azure DevOps / GitLab are future work

---

## 7. Success Criteria

| Criterion | Measurement |
|-----------|-------------|
| **All existing Pester test scenarios pass** as equivalent xUnit tests | Green CI on all `*.Tests` projects |
| **Fixture target E2E tests pass** for happy-path, regression, build-failure, test-failure, and stacked-diffs scenarios | Integration test suite green (after all phases complete) |
| **Snapshot tests pass** for metric comparison, queue management, and config merge | Diff-free snapshot comparison via `Verify.Xunit` |
| **Target `.hone/` configs migrated** to YAML for sample-api and eShopOnWeb | Both targets produce valid `HoneConfig` from YAML |
| **Observability events emitted** for all phases, agent calls, and experiment outcomes | `ConsoleEventSink` and `JsonLogEventSink` produce structured output |
| **Documentation updated** to reflect C# codebase, YAML config, new interfaces | All docs in `docs/` reference C# where appropriate |

---

## 8. Relationship to Existing Roadmap

This migration is orthogonal to the features described in `future-extensions.md` (iterative implementer improvements, actor-critic gate, COE agent, knowledge base). However, the C# codebase makes those features easier to implement:

- **Iterative implementer** is already implemented in PowerShell; the C# version inherits the same logic with stronger typing
- **Actor-critic gate** is a new agent integration — easier with `IAgentRunner` interface and DI
- **COE agent** is a new post-experiment step — easier to compose into the typed phase pipeline
- **Knowledge base** benefits from C#'s JSON serialization and potential SQLite/LiteDB integration
- **TUI/GUI dashboard** — the observability pipeline (`IHoneEventSink`) provides the foundation; a Spectre.Console TUI or Blazor web dashboard can subscribe to the same events
- **Alternative agent backends** — `IAgentRunner` enables Claude Code, direct API calls, or local models without touching orchestration code
- **Alternative load test tools** — `ILoadTestRunner` enables NBomber, Locust, or custom tools
- **Alternative hosting providers** — `ICodeHost` enables Azure DevOps, GitLab, or Bitbucket

The migration should complete before these features are implemented in C# to avoid maintaining parallel implementations.

---

## 9. Next Steps

See [phased-plan.md](phased-plan.md) for the detailed phase-by-phase implementation plan including:

- Module-by-module migration sequence with dependencies
- Test case specifications per phase
- Validation checkpoints
- Documentation update schedule
