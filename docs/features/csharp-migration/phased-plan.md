# Phased Migration Plan: PowerShell → C# Harness

> **Status:** Draft  
> **Date:** 2026-04-04  
> **Companion Documents:** [proposal.md](proposal.md) — high-level architecture and rationale; [agent-team.md](agent-team.md) — migration delivery agent model and MVP team

---

## How to Read This Plan

Each phase is independently implementable and testable — all unit tests for a phase must pass before moving to the next. However, there is **no requirement for the harness to function end-to-end until all phases are complete**. There is no hybrid PowerShell/C# mode; the PowerShell harness continues to be the working system until the C# migration is fully done. Phases are ordered by dependency: earlier phases produce the contracts and models that later phases consume. Within each phase, every C# component lists:

- **Replaces** — which PowerShell file(s) it supersedes
- **Surface area** — public types, methods, and interfaces
- **Tests** — specific test cases with expected behavior
- **Validation** — how to confirm the phase's unit tests and module contracts are correct

Execution of this plan should use the migration delivery model in [agent-team.md](agent-team.md). The approved starting point is the MVP custom team, with the broader worker and critic set added only after pilot slices show that the MVP is insufficient.

---

## Phase Overview

| Phase | Name | Scope | Depends On |
|-------|------|-------|-----------|
| **0** | Solution Scaffolding | .sln, projects, CI, test infrastructure | — |
| **1** | Core Domain Models, Config & Observability | `Hone.Core` — records, YAML config, contracts, observability, utilities | Phase 0 |
| **2** | Measurement & Comparison | `Hone.Measurement` + `Hone.Measurement.K6` + `Hone.Measurement.DotnetCounters` — generic interfaces, metric comparison, k6 impl, counters impl | Phase 1 |
| **3** | Lifecycle & Hooks | `Hone.Lifecycle` — hook resolution, dispatch, config validation | Phase 1 |
| **4** | Source Control | `Hone.SourceControl` + `Hone.SourceControl.Git` — VCS/hosting abstractions, git/GitHub impl | Phase 1 |
| **5** | AI Agent Integration | `Hone.Agents.Core` + `Hone.Agents.Loop` + `Hone.Agents.Preparation` + `Hone.Agents.CopilotCli` | Phases 1, 2 |
| **6** | Diagnostic Profiling | `Hone.Diagnostics` — plugin framework, collectors, analyzers | Phases 1, 2, 5 |
| **7** | Reporting & Export | `Hone.Reporting` — dashboard, results, RCA, PR body | Phases 1, 2 |
| **8** | Orchestration | `Hone.Orchestration` — main loop, queue, iterative implementer | Phases 1–7 |
| **9** | CLI Host & Integration Tests | `Hone.Cli` + `Hone.Integration.Tests` | Phases 1–8 |
| **10** | Target Migration & Cutover | Convert `.hone/` configs to YAML, documentation, archive PowerShell | Phase 9 |

---

## Phase 0: Solution Scaffolding

### Goal
Create the .NET solution structure, CI pipeline, shared test infrastructure, and coding conventions.

### Deliverables

#### Solution file and projects
```
harness-csharp/
├── Hone.sln
├── .editorconfig                  # Code style, naming conventions, analyzer severity tuning
├── Directory.Build.props          # Shared properties (TFM, nullable, analyzers, NuGet audit)
├── Directory.Build.targets        # Wires BannedSymbols.txt to all projects via AdditionalFiles
├── Directory.Packages.props       # Central package management (including analyzer packages)
├── BannedSymbols.txt              # Banned API list for Microsoft.CodeAnalysis.BannedApiAnalyzers
├── src/
│   ├── Hone.Core/
│   ├── Hone.Orchestration/
│   ├── Hone.Agents.Core/
│   ├── Hone.Agents.Loop/
│   ├── Hone.Agents.Preparation/
│   ├── Hone.Agents.CopilotCli/
│   ├── Hone.Measurement/
│   ├── Hone.Measurement.K6/
│   ├── Hone.Measurement.DotnetCounters/
│   ├── Hone.Diagnostics/
│   ├── Hone.Lifecycle/
│   ├── Hone.SourceControl/
│   ├── Hone.SourceControl.Git/
│   ├── Hone.Reporting/
│   └── Hone.Cli/
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
│   └── Hone.Integration.Tests/
└── test-fixtures/                 # Shared fixture targets (YAML config)
```

#### Shared build configuration
- **Target framework:** `net10.0`
- **Nullable reference types:** enabled globally
- **Implicit usings:** enabled
- **Central package management** for xUnit, NSubstitute, FluentAssertions, System.CommandLine, YamlDotNet, and all analyzer packages
- **Strict code quality — warnings as errors + third-party analyzers** (enforced from day one)

See [proposal.md §3.3.7](proposal.md) for the full rationale and evaluation of all analyzer options.

**`Directory.Build.props` — applied to ALL projects:**

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

<ItemGroup>
  <PackageReference Include="Meziantou.Analyzer" PrivateAssets="all" />
  <PackageReference Include="Microsoft.VisualStudio.Threading.Analyzers" PrivateAssets="all" />
  <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" PrivateAssets="all" />
</ItemGroup>
```

Test-only analyzer packages are added in a separate `Directory.Build.props` inside `tests/`:

```xml
<!-- tests/Directory.Build.props -->
<ItemGroup>
  <PackageReference Include="xunit.analyzers" PrivateAssets="all" />
  <PackageReference Include="NSubstitute.Analyzers.CSharp" PrivateAssets="all" />
</ItemGroup>
```

**`Directory.Packages.props` — central version management** (includes all analyzer versions):

```xml
<ItemGroup Label="Analyzers">
  <PackageVersion Include="Meziantou.Analyzer" Version="*" />
  <PackageVersion Include="Microsoft.VisualStudio.Threading.Analyzers" Version="*" />
  <PackageVersion Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="*" />
  <PackageVersion Include="xunit.analyzers" Version="*" />
  <PackageVersion Include="NSubstitute.Analyzers.CSharp" Version="*" />
</ItemGroup>
```

> Version `*` above is a placeholder — pin to the latest stable version at time of scaffolding.

**`Directory.Build.targets` — wires `BannedSymbols.txt` to all projects:**

```xml
<ItemGroup>
  <AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" Condition="Exists('$(MSBuildThisFileDirectory)BannedSymbols.txt')" />
</ItemGroup>
```

**`BannedSymbols.txt` — project convention enforcement:**

```
T:System.DateTime; Use DateTimeOffset instead for timezone safety
M:System.Threading.Thread.Sleep(System.Int32); Use Task.Delay for async code
M:System.IO.File.ReadAllText(System.String); Use File.ReadAllTextAsync for async I/O
M:System.IO.File.WriteAllText(System.String,System.String); Use File.WriteAllTextAsync for async I/O
T:System.Collections.ArrayList; Use generic List<T>
T:System.Collections.Hashtable; Use generic Dictionary<TKey,TValue>
```

The banned list is enforced in `src/` projects. Test projects may use `#pragma warning disable RS0030` with a documented justification where synchronous I/O is more readable in test setup.

**`.editorconfig` — code style enforcement:**

- **Naming**: PascalCase for public members, `_camelCase` for private fields, camelCase for locals/parameters, `I`-prefix for interfaces, `T`-prefix for type parameters
- **Code style**: `var` only when type is apparent, expression-body for single-line members, pattern matching preferred, explicit accessibility modifiers required
- **Formatting**: Allman brace style, spaces not tabs, sorted usings with `System` first
- **Analyzer severity tuning**: Specific Meziantou/Threading rules tuned per project needs — some demoted to `suggestion` where too noisy

All style rules use severity `warning`, which `TreatWarningsAsErrors` promotes to build errors. No warning suppressions without a documented `#pragma` justification.

#### CI pipeline
- `dotnet build Hone.sln` — fails on any warning (TreatWarningsAsErrors in Directory.Build.props), any vulnerable dependency (NuGetAudit), and any banned API usage (BannedApiAnalyzers)
- `dotnet format --verify-no-changes` — catches formatting and style drift that local builds may miss
- `dotnet test` all test projects
- Code coverage reporting (Coverlet)

#### Test infrastructure base class
```csharp
// tests/Hone.TestInfrastructure/
public abstract class HoneTestBase : IDisposable
{
    protected string TempDir { get; }           // Per-test temp directory (like Pester TestDrive)
    protected ITestOutputHelper Output { get; }  // xUnit output capture

    protected string CreateTargetDir(string name, Action<TargetBuilder>? configure = null);
    protected string CopyFixtureTarget(string fixtureName);
    protected GitTestRepo InitGitRepo(string path);

    public void Dispose() => CleanupTempDir();
}
```

### Tests
- `dotnet build Hone.sln` succeeds with **zero warnings** (TreatWarningsAsErrors enforced)
- `dotnet test` discovers and runs a placeholder test per project
- CI pipeline passes (warnings-as-errors enforced via Directory.Build.props, no CLI flags needed)
- `.editorconfig` style rules are enforced at build time
- `dotnet format --verify-no-changes` passes on a clean checkout
- Intentionally introduce a banned API usage (e.g., `DateTime.Now`) → verify build fails with RS0030
- Intentionally break a naming convention → verify build fails with IDE naming rule
- All five third-party analyzer packages load without conflicts (verify via build output)

### Validation
- All projects compile, all placeholder tests pass, CI green, zero warnings
- Analyzer enforcement verified: banned API, naming, threading, and xUnit rules all produce build errors when violated

---

## Phase 1: Core Domain Models, Configuration & Observability

### Goal
Establish the foundational types that every other module depends on: domain models, YAML configuration hierarchy, contracts (interfaces), observability pipeline, and shared utilities.

### Components

#### 1.1 Domain Models (`Hone.Core/Models/`)

**Replaces:** PSCustomObject shapes scattered across `Compare-Results.ps1`, `Invoke-ScaleTests.ps1`, `Manage-OptimizationQueue.ps1`, `Update-OptimizationMetadata.ps1`, `Get-MachineInfo.ps1`, `HoneHelpers.psm1`

| C# Type | Replaces | Properties |
|---------|----------|------------|
| `MetricSet` | k6 summary PSCustomObject | `Timestamp`, `Experiment`, `Run`, `HttpReqDuration` (Avg/P50/P90/P95/P99/Max), `HttpReqs` (Count/Rate), `HttpReqFailed` (Count/Rate), `SummaryPath` |
| `ComparisonResult` | Return of `Compare-Results.ps1` | `Accepted`, `Outcome` (enum: Improved/Regressed/Stale/EfficiencyWin), `ImprovementPct`, `RegressionPct`, `Details[]` |
| `MetricComparison` | Per-metric detail object | `MetricName`, `Current`, `Previous`, `Baseline`, `DeltaPct`, `AbsoluteDelta`, `Improved`, `Regressed` |
| `Opportunity` | Analyst JSON response item | `FilePath`, `Title`, `Explanation`, `Scope` (enum: Narrow/Architecture), `RootCause`, `ImpactEstimate` |
| `QueueItem` | Queue JSON item | `Id`, `FilePath`, `Explanation`, `Scope`, `Status` (enum: Pending/InProgress/Done/Skipped), `TriedByExperiment`, `Outcome` |
| `OptimizationQueue` | Queue JSON root | `GeneratedByExperiment`, `Items[]` |
| `ExperimentMetadata` | run-metadata.json entry | `Experiment`, `StartedAt`, `CompletedAt`, `Outcome`, `BranchName`, `BaseBranch`, `P95`, `RPS`, `PrNumber`, `PrUrl`, `StaleCount`, `ConsecutiveFailures` |
| `RunMetadata` | run-metadata.json root | `TargetName`, `StartedAt`, `MachineInfo`, `Experiments[]` |
| `MachineInfo` | `Get-MachineInfo.ps1` return | `CpuName`, `CpuCores`, `TotalRamGB`, `OsVersion`, `DotnetVersion` |
| `ProcessResult` | Copilot/git/dotnet call return | `Success`, `Output`, `ExitCode`, `TimedOut` |
| `HookResult` | Hook return shape | `Success`, `Message`, `Duration`, `Artifacts[]`, `BaseUrl`, `Process` |
| `CollectorHandle` | Start-Collector return | `Success`, `Handle` (opaque object) |
| `CollectorArtifacts` | Stop-Collector return | `Success`, `ArtifactPaths[]` |
| `CollectorExport` | Export-CollectorData return | `Success`, `ExportedPaths[]`, `Summary` |
| `AnalyzerReport` | Invoke-Analyzer return | `Success`, `Report`, `Summary`, `PromptPath`, `ResponsePath` |
| `IterationLog` | iteration-log.json | `Attempts[]` (Attempt, Stage, Outcome, DiffLines) |
| `IterativeFixResult` | Invoke-IterativeFix return | `Success`, `AttemptCount`, `ExitReason`, `FailureDetail`, `IterationLog`, `IterationLogRelativePath` |

> **Naming note:** Throughout this plan, the PowerShell "Fixer" concept is renamed to **Implementer**. The agent that generates code changes is the `ImplementerAgent`, the retry loop is the `IterativeImplementerRunner`, and the config section is `ImplementerConfig`. This better reflects the agent's role: it implements optimization proposals, not just "fixes."

**Tests (`Hone.Core.Tests/Models/`):**

| Test | Behavior |
|------|----------|
| `MetricSet_Serialization_RoundTrips` | Serialize to JSON and back; all fields preserved |
| `MetricSet_FromK6Summary_ParsesAllFields` | Parse real k6 JSON summary fixture into MetricSet |
| `ComparisonResult_OutcomeEnum_CoversAllCases` | Improved, Regressed, Stale, EfficiencyWin all representable |
| `QueueItem_StatusTransitions_Valid` | Pending→InProgress→Done; Pending→Skipped; no backward transitions |
| `Opportunity_ScopeEnum_NarrowAndArchitecture` | Both values serialize/deserialize correctly |
| `ExperimentMetadata_AdditionalProperties_Preserved` | Extra key-value pairs round-trip through serialization |

#### 1.2 Configuration (`Hone.Core/Config/`)

**Replaces:** `config.psd1` (270 lines), `Get-HoneConfig`, `Merge-HoneConfig` (HoneHelpers.psm1), config loading in every script

| C# Type | Config Section |
|---------|---------------|
| `HoneConfig` | Root — aggregates all sections |
| `ApiConfig` | `Api` — SolutionPath, ProjectPath, BaseUrl, HealthEndpoint, etc. |
| `TolerancesConfig` | `Tolerances` — MaxRegressionPct, MinAbsoluteP95DeltaMs, Efficiency sub-config |
| `EfficiencyConfig` | `Tolerances.Efficiency` — Enabled, MinCpuReductionPct, MinWorkingSetReductionPct |
| `ScaleTestConfig` | `ScaleTest` — ScenarioPath, WarmupEnabled, MeasuredRuns, CooldownSeconds |
| `LoopConfig` | `Loop` — MaxExperiments, BranchPrefix, StackedDiffs, WaitForMerge, SkipClassification |
| `AgentConfig` | `Agents` — DefaultModel, AnalysisModel, ClassificationModel, ImplementerModel, AgentTimeoutSec |
| `DiagnosticsConfig` | `Diagnostics` — Enabled, CollectorsPath, AnalyzersPath, PerfViewExePath, collector/analyzer settings |
| `LoggingConfig` | `Logging` — Level, MaxFileSizeMB |
| `ImplementerConfig` | `Implementer` — MaxAttempts, MaxDiffGrowthFactor, TestFileGuard |
| `DotnetCountersConfig` | `DotnetCounters` — Enabled, Providers, RefreshIntervalSeconds |

**Key classes:**

```csharp
public static class ConfigLoader
{
    // Loads YAML configuration using YamlDotNet and deserializes into HoneConfig
    public static HoneConfig Load(string yamlPath);
}

public static class ConfigMerger
{
    // Merge engine defaults + target overrides + CLI overrides
    // Mirrors Merge-HoneConfig semantics: section-level merge for objects,
    // scalar override for primitives
    public static HoneConfig Merge(HoneConfig engine, HoneConfig target, CliOverrides? cli = null);
}
```

**Tests (`Hone.Core.Tests/Config/`):**

| Test | Behavior |
|------|----------|
| `ConfigLoader_LoadsYaml_AllSectionsPresent` | Load engine `config.yaml` → all sections populated |
| `ConfigLoader_MissingOptionalSections_UsesDefaults` | Partial YAML → missing sections get default values |
| `ConfigMerger_TargetOverridesEngine_SectionLevel` | Target `Api.BaseUrl` overrides engine, engine `Tolerances.MaxRegressionPct` preserved |
| `ConfigMerger_CliOverridesEverything` | CLI `MaxExperiments=5` overrides both engine and target |
| `ConfigMerger_PartialTargetOverride_MergesCorrectly` | Target with only `Api.BaseUrl` doesn't erase other engine Api settings |
| `ConfigMerger_UnknownTargetKeys_Preserved` | Target-specific keys not in engine schema still appear in merged config |
| `HoneConfig_DefaultValues_MatchExpected` | Default `HoneConfig()` matches documented defaults |
| `ConfigLoader_MissingFile_ThrowsDescriptiveError` | Clear exception with file path |
| `ConfigLoader_MalformedYaml_ThrowsParseError` | Invalid YAML syntax produces parse error |

#### 1.3 Contracts (`Hone.Core/Contracts/`)

**Replaces:** Plugin contracts doc, hook return shapes, implicit contracts across scripts

```csharp
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string? workingDirectory = null, TimeSpan? timeout = null,
        CancellationToken ct = default);
}

public interface IAgentRunner
{
    Task<AgentRunResult> InvokeAsync(AgentInvocation invocation, CancellationToken ct = default);
}

public interface ILoadTestRunner
{
    Task<LoadTestResult> RunAsync(LoadTestOptions options, CancellationToken ct = default);
}

public interface IRuntimeMetricsCollector
{
    Task<MetricsCollectionHandle> StartAsync(int processId, RuntimeMetricsOptions options, CancellationToken ct = default);
    Task<RuntimeMetricsResult> StopAndParseAsync(MetricsCollectionHandle handle, CancellationToken ct = default);
}

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
}

public interface ICollectorPlugin { ... }  // See Phase 6
public interface IAnalyzerPlugin { ... }   // See Phase 6
public interface ILifecycleHook { ... }    // See Phase 3
```

> **Design note on `IAgentRunner` placement:** `IAgentRunner` lives in `Hone.Core/Contracts/` because it is used by both `Hone.Agents.Loop` (optimization agents) and `Hone.Agents.Preparation` (target setup agents). Placing it in Core ensures both agent modules — and any future agent module — can reference it without circular dependencies. The same applies to all other generic interfaces above.

**Tests (`Hone.Core.Tests/Contracts/`):**

| Test | Behavior |
|------|----------|
| `ICollectorPlugin_Interface_MatchesPluginContractsDoc` | Verify method signatures match documented contracts |
| `IAnalyzerPlugin_Interface_MatchesPluginContractsDoc` | Verify method signatures match documented contracts |
| `ILifecycleHook_ExecuteAsync_ReturnsHookResult` | Interface compiles with expected return type |
| `IAgentRunner_InvokeAsync_ReturnsAgentRunResult` | Interface compiles with expected return type |
| `ILoadTestRunner_RunAsync_ReturnsLoadTestResult` | Interface compiles with expected return type |
| `IVersionControl_AllOperations_Present` | All branch/commit/checkout/revert/diff methods present |
| `ICodeHost_PushAndPr_Present` | Push and PR creation methods present |

#### 1.4 Utilities (`Hone.Core/Utilities/`)

**Replaces:** `Write-Status`, `Limit-String`, `Copy-HoneHashtable`, `Convert-HoneK6SummaryToMetricSet`

| C# Type | Replaces | Methods |
|---------|----------|---------|
| `StringUtils` | `Limit-String` | `Truncate(string text, int maxLength)` — word-boundary truncation with "…" |
| `JsonUtils` | JSON parsing scattered across agents | `SanitizeNaN(string json)`, `ExtractJsonBlock(string text)`, `ExtractCodeBlock(string text)` |

**Tests (`Hone.Core.Tests/Utilities/`):**

| Test | Behavior |
|------|----------|
| `Truncate_ShortString_ReturnsUnchanged` | "hello" with max=10 → "hello" |
| `Truncate_AtWordBoundary_AppendsEllipsis` | "hello world foo" with max=12 → "hello world…" |
| `Truncate_NoSpaceInFirstHalf_TruncatesHard` | "abcdefghij" with max=5 → "abcde…" |
| `Truncate_NullOrEmpty_ReturnsInput` | null → null, "" → "" |
| `SanitizeNaN_ReplacesNaN` | `{"value": NaN}` → `{"value": null}` |
| `SanitizeNaN_ReplacesInfinity` | `{"value": Infinity}` → `{"value": null}` |
| `ExtractJsonBlock_FromMarkdownFences` | ````json\n{...}\n```` → `{...}` |
| `ExtractCodeBlock_FromCSharpFences` | ````csharp\n...\n```` → code content |

#### 1.5 Observability (`Hone.Core/Observability/`)

**Replaces:** `Write-Status` (HoneHelpers.psm1), `Write-HoneLog.ps1` (87 lines), `Show-Progress.ps1`

The observability system is a structured event pipeline. All harness activity is emitted as typed events to registered sinks. This decouples "what happened" from "how to display it" and enables future TUI/GUI frontends.

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
public record ExperimentOutcomeEvent(int Experiment, string Outcome, ComparisonResult Details, ...) : HoneEvent(...);
public record StatusMessage(string Message, LogLevel Level, ...) : HoneEvent(...);
public record DiagnosticProgress(string CollectorName, string Stage, ...) : HoneEvent(...);

public class HoneEventBus : IHoneEventSink
{
    private readonly List<IHoneEventSink> _sinks;
    public void Register(IHoneEventSink sink);
    public void Emit(HoneEvent @event); // Broadcasts to all registered sinks
}
```

**Built-in sinks (shipped in Phase 1):**

| Sink | Purpose |
|------|---------|
| `ConsoleEventSink` | Timestamped console output with box-drawing support (replaces `Write-Status`) |
| `JsonLogEventSink` | Structured JSONL file with rotation at configurable size (replaces `Write-HoneLog.ps1`) |

**Future sinks (out of scope but enabled by this design):**

| Sink | Purpose |
|------|---------|
| `TuiEventSink` | Real-time terminal UI via Spectre.Console or Terminal.Gui |
| `GuiEventSink` | WebSocket/gRPC feed for desktop or web GUI |
| `WebhookEventSink` | HTTP POST events to external monitoring systems |

**Tests (`Hone.Core.Tests/Observability/`):**

| Test | Behavior |
|------|----------|
| `EventBus_BroadcastsToAllSinks` | 3 sinks registered → all 3 receive event |
| `ConsoleEventSink_Timestamps_NonBoxDrawing` | Non-box-drawing messages get `[HH:mm:ss]` prefix |
| `ConsoleEventSink_PassesThrough_BoxDrawing` | Box-drawing characters output without timestamp |
| `JsonLogEventSink_WritesValidJsonl` | Each event = one valid JSON line |
| `JsonLogEventSink_RotatesAtMaxSize` | Exceeding MaxFileSizeMB → old file renamed, new started |
| `JsonLogEventSink_IncludesAllEventFields` | PhaseStarted event → JSON contains Phase, Experiment, Timestamp |

### Validation Checkpoint
- All `Hone.Core.Tests` pass
- Config loaded from a YAML config matches expected values
- Observability events emitted and captured by both ConsoleEventSink and JsonLogEventSink
- `MetricSet` serialization round-trips correctly through JSON

---

## Phase 2: Measurement & Comparison

### Goal
Migrate the performance measurement pipeline: metric comparison (the accept/reject decision engine), generic load test abstraction with k6 implementation, generic runtime metrics abstraction with dotnet-counters implementation, baseline measurement, and cooldown.

### Components

#### 2.1 MetricComparer (`Hone.Measurement/Comparison/`)

**Replaces:** `Compare-Results.ps1` (367 lines) — the core pure function

This is the highest-value migration target: the accept/reject decision logic that determines whether an experiment is accepted. It must produce identical results to the PowerShell implementation.

```csharp
public class MetricComparer
{
    public ComparisonResult Compare(
        MetricSet current,
        MetricSet previous,
        MetricSet? baseline,
        TolerancesConfig tolerances,
        CounterMetrics? currentCounters = null,
        CounterMetrics? previousCounters = null);
}
```

**Tests (`Hone.Measurement.Tests/Comparison/`):**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `FlatMetrics_ReturnsStale` | Same p95/RPS/errors → Stale | `Compare-Results.Tests.ps1` |
| `ImprovedP95_NoRegression_Accepted` | p95 drops 20%, others flat → Improved | ✓ |
| `ImprovedRPS_NoRegression_Accepted` | RPS increases 15%, others flat → Improved | ✓ |
| `RegressionP95_BeyondThreshold_Rejected` | p95 up 15% AND >5ms absolute → Regressed | ✓ |
| `RegressionP95_BelowAbsoluteThreshold_NotRegression` | p95 up 15% but only 3ms absolute → Stale | ✓ |
| `RegressionRPS_BelowAbsoluteThreshold_NotRegression` | RPS down 12% but only 3 req/s → Stale | ✓ |
| `MixedSignals_ImprovementAndRegression_Rejected` | p95 improved, RPS regressed → Rejected | ✓ |
| `EfficiencyTiebreaker_FlatPerf_CpuDown_Accepted` | Flat metrics + CPU down 10% → EfficiencyWin | ✓ |
| `EfficiencyTiebreaker_Disabled_StaysStale` | Efficiency.Enabled=false → Stale even with CPU down | ✓ |
| `EfficiencyTiebreaker_DoesNotOverrideRegression` | Regressed metrics + CPU down → still Rejected | ✓ |
| `ZeroBaseline_ErrorRateRise_MaxDelta` | Baseline errors=0, current=0.05 → regression detected | ✓ |
| `ErrorRateRegression_BelowAbsoluteThreshold_Ignored` | Error rate up 50% but only 0.002 absolute → not regression | ✓ |

**Snapshot validation:** Capture expected MetricComparer output for 10 fixture metric pairs as JSON snapshots. C# tests assert output matches these snapshots (using `Verify.Xunit`).

#### 2.2 ILoadTestRunner + K6 Implementation (`Hone.Measurement/` + `Hone.Measurement.K6/`)

**Replaces:** `Invoke-ScaleTests.ps1` (480 lines)

The generic interface lives in `Hone.Measurement/`:

```csharp
public interface ILoadTestRunner
{
    Task<LoadTestResult> RunAsync(LoadTestOptions options, CancellationToken ct = default);
}

public record LoadTestOptions(
    string ScenarioPath, string BaseUrl, string OutputDir,
    int Experiment, int Run, TimeSpan? Timeout,
    IReadOnlyDictionary<string, string>? EnvironmentVars = null);

public record LoadTestResult(
    bool Success, MetricSet? Metrics, string? SummaryPath, string? Output);

public class ScaleTestOrchestrator
{
    // Full multi-run orchestration: warmup + N measured runs + median selection
    // Uses ILoadTestRunner — doesn't know about k6 specifics
    public Task<ScaleTestResult> RunAsync(ScaleTestConfig config, ILoadTestRunner runner, ...);
}
```

The k6 implementation lives in `Hone.Measurement.K6/`:

```csharp
public class K6LoadTestRunner : ILoadTestRunner
{
    // k6-specific: JSON summary parsing, __ENV.BASE_URL, --out json, scenario options
}

public class K6SummaryParser
{
    // Converts k6 JSON summary to MetricSet
    public MetricSet Parse(string jsonPath, int experiment, int run);
}
```

**Tests (`Hone.Measurement.Tests/`):**

| Test | Behavior |
|------|----------|
| `ScaleTestOrchestrator_MultipleRuns_SelectsMedian` | 5 runs with known p95 values → median selected |
| `ScaleTestOrchestrator_WarmupEnabled_RunsWarmupFirst` | Verifies warmup scenario called before measured runs |
| `ScaleTestOrchestrator_CooldownBetweenRuns` | Verifies delay between consecutive runs |

**Tests (`Hone.Measurement.K6.Tests/`):**

| Test | Behavior |
|------|----------|
| `K6LoadTestRunner_ParsesSummaryJson_CorrectMetrics` | Fixture k6 JSON → correct MetricSet |
| `K6LoadTestRunner_Timeout_ReturnsFailure` | Process exceeds timeout → Success=false, TimedOut=true |
| `K6SummaryParser_RealFixture_AllFieldsMapped` | Parse fixture k6 JSON → MetricSet with all fields |
| `K6LoadTestRunner_DynamicPort_SubstitutesBaseUrl` | Port 0 → resolved actual port injected into k6 env |

#### 2.3 IRuntimeMetricsCollector + DotnetCounters Implementation (`Hone.Measurement/` + `Hone.Measurement.DotnetCounters/`)

**Replaces:** `Start-DotnetCounters.ps1` (121 lines), `Stop-DotnetCounters.ps1` (175 lines)

The generic interface lives in `Hone.Measurement/`:

```csharp
public interface IRuntimeMetricsCollector
{
    Task<MetricsCollectionHandle> StartAsync(int processId, RuntimeMetricsOptions options, CancellationToken ct = default);
    Task<RuntimeMetricsResult> StopAndParseAsync(MetricsCollectionHandle handle, CancellationToken ct = default);
}
```

The dotnet-counters implementation lives in `Hone.Measurement.DotnetCounters/`:

```csharp
public class DotnetCountersCollector : IRuntimeMetricsCollector
{
    // dotnet-counters specific: CSV parsing, provider configuration
}
```

**Tests:**

| Test | Behavior |
|------|----------|
| `ParseCounterCsv_ExtractsAllProviders` | Fixture CSV → CounterMetrics with CPU%, GC heap, threads |
| `ParseCounterCsv_EmptyFile_ReturnsEmptyMetrics` | No data rows → default zero metrics |
| `StopAndParse_ProcessNotRunning_HandlesGracefully` | Process already exited → returns partial data |

#### 2.4 BaselineMeasurer (`Hone.Measurement/Baseline/`)

**Replaces:** `Get-PerformanceBaseline.ps1` (196 lines)

Uses `ILoadTestRunner` and `IRuntimeMetricsCollector` — doesn't know about k6 or dotnet-counters specifics.

**Tests:**

| Test | Behavior |
|------|----------|
| `MeasureBaseline_CollectsMachineInfo` | Machine info populated in result |
| `MeasureBaseline_RunsFullLifecycle` | Prepare → Build → Start → Measure → Stop sequence verified |
| `MeasureBaseline_SavesBaselineJson` | `baseline.json` written to results path |

### Validation Checkpoint
- All `Hone.Measurement.Tests`, `Hone.Measurement.K6.Tests`, and `Hone.Measurement.DotnetCounters.Tests` pass
- Snapshot tests: MetricComparer produces expected JSON for all 10 fixture pairs
- k6 JSON parsing produces correct `MetricSet` from real summary fixture files
- `ScaleTestOrchestrator` works with a fake `ILoadTestRunner` confirming tool independence

---

## Phase 3: Lifecycle & Hooks

### Goal
Migrate hook resolution, dispatch, and config validation. This is the target-facing contract layer.

### Components

#### 3.1 HookResolver (`Hone.Lifecycle/Hooks/`)

**Replaces:** `Resolve-Hook` function in HoneHelpers.psm1

With the clean break from PowerShell, `Script` and `Shared` hook types are replaced by native C# hook implementations. The hook types supported are:

```csharp
public class HookResolver
{
    public ResolvedHook Resolve(string hookName, TargetConfig targetConfig,
        string targetDir, string harnessRoot);
}

public record ResolvedHook(HookType Type, string? Command, 
    string? Url, string? Method);

public enum HookType { BuiltIn, Command, Http, Skip }
```

**Tests (`Hone.Lifecycle.Tests/Hooks/`):**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `Resolve_BuiltInHook_ResolvesToNativeImplementation` | BuiltIn type → C# hook implementation | `Resolve-Hook.Tests.ps1` |
| `Resolve_CommandHook_PassesThrough` | Command type → returned as-is | ✓ |
| `Resolve_HttpHook_PassesThrough` | Http type → returned as-is | ✓ |
| `Resolve_SkipHook_PassesThrough` | Skip type → returned as-is | ✓ |
| `Resolve_UndeclaredHook_ThrowsContractError` | Hook name not in config → clear error message | ✓ |
| `Resolve_UnknownType_Throws` | Type "FooBar" → exception | ✓ |

#### 3.2 HookDispatcher (`Hone.Lifecycle/Hooks/`)

**Replaces:** `Invoke-LifecycleHook` + `hooks/Invoke-Hook.ps1` (89 lines)

```csharp
public class LifecycleHookDispatcher
{
    public Task<HookResult> DispatchAsync(string hookName, ResolvedHook hook,
        string targetPath, HoneConfig config, string? baseUrl, int experiment);
}
```

**Tests:**

| Test | Behavior |
|------|----------|
| `Dispatch_BuiltInHook_ExecutesNativeImplementation` | Calls C# hook implementation directly |
| `Dispatch_CommandHook_ExecutesCommand` | Runs command via IProcessRunner |
| `Dispatch_HttpHook_MakesRequest` | Makes HTTP request to configured URL |
| `Dispatch_SkipHook_ReturnsSuccessImmediately` | No execution, Success=true |
| `Dispatch_FailedHook_ReturnsFailure` | Script exits non-zero → Success=false with message |
| `Dispatch_FixtureOverride_UsesFixture` | When fixture is present, uses fixture result instead of real execution |

#### 3.3 Built-in Shared Hooks (`Hone.Lifecycle/SharedHooks/`)

**Replaces:** `hooks/dotnet-build.ps1`, `dotnet-start.ps1`, `dotnet-stop.ps1`, `dotnet-test.ps1`, `health-poll.ps1`, `k6-run.ps1`

| C# Class | Replaces | Key Behavior |
|-----------|----------|-------------|
| `DotnetBuildHook` | `dotnet-build.ps1` (33 lines) | `dotnet build --configuration Release` |
| `DotnetStartHook` | `dotnet-start.ps1` (72 lines) | Start API process, dynamic port, health poll |
| `DotnetStopHook` | `dotnet-stop.ps1` (136 lines) | Graceful shutdown + force kill fallback |
| `DotnetTestHook` | `dotnet-test.ps1` (45 lines) | `dotnet test --logger trx`, parse results |
| `HealthPollHook` | `health-poll.ps1` (32 lines) | HTTP health check with configurable retry |
| `K6RunHook` | `k6-run.ps1` (55 lines) | Delegates to K6Runner |

**Tests:**

| Test | Behavior |
|------|----------|
| `DotnetStartHook_DynamicPort_ResolvesActualUrl` | Port 0 → reads actual port from stdout |
| `DotnetStartHook_HealthCheck_WaitsForHealthy` | Polls until healthy or timeout |
| `DotnetStopHook_GracefulShutdown_SendsCtrlC` | Tries graceful before force kill |
| `DotnetStopHook_OrphanedProcess_FindsAndStops` | Discovers stale process by project path |
| `HealthPollHook_Timeout_ReturnsFalse` | Health check never returns 200 → false |

#### 3.4 ConfigValidator (`Hone.Lifecycle/Validation/`)

**Replaces:** `Test-HoneConfig.ps1` (280 lines)

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `Validate_ValidConfig_Passes` | Real config.yaml → no errors | `Config-Validation.Tests.ps1` |
| `Validate_MissingRequiredPaths_Fails` | No Api.SolutionPath → error | ✓ |
| `Validate_InvalidToleranceRange_Fails` | MaxRegressionPct=-0.5 → error | ✓ |
| `Validate_InvalidPort_Warns` | Port outside 0-65535 → warning | ✓ |
| `Validate_MissingRequiredHooks_Fails` | No Hooks.Build → error | ✓ |
| `Validate_InteractionWarnings_Detected` | StackedDiffs+WaitForMerge → warning | ✓ |

### Validation Checkpoint
- All `Hone.Lifecycle.Tests` pass
- Hook resolution for all types (BuiltIn, Command, Http, Skip) behaves correctly per unit tests
- Config validation catches expected errors and warnings for fixture configs

---

## Phase 4: Source Control

### Goal
Migrate all version control and code hosting interactions behind generic interfaces: `IVersionControl` and `ICodeHost`. The git + GitHub CLI implementations are the only providers for now, but the abstraction enables future Azure DevOps, GitLab, or other providers.

### Components

#### 4.1 IVersionControl + GitVersionControl (`Hone.SourceControl/` + `Hone.SourceControl.Git/`)

**Replaces:** Inline `git` calls throughout harness scripts

The generic interface lives in `Hone.SourceControl/`:

```csharp
public interface IVersionControl
{
    Task<string> GetCurrentBranchAsync(string workingDir, CancellationToken ct = default);
    Task CheckoutAsync(string workingDir, string branch, bool create = false, CancellationToken ct = default);
    Task CommitAsync(string workingDir, string message, IEnumerable<string>? paths = null, CancellationToken ct = default);
    Task<string> GetDiffAsync(string workingDir, string? baseBranch = null, CancellationToken ct = default);
    Task RevertLastCommitAsync(string workingDir, CancellationToken ct = default);
    Task<int> GetDiffLineCountAsync(string workingDir, CancellationToken ct = default);
}

public interface ICodeHost
{
    Task<PushResult> PushBranchAsync(string workingDir, string branch, CancellationToken ct = default);
    Task<PullRequestResult> CreatePullRequestAsync(CreatePrOptions options, CancellationToken ct = default);
    Task<PullRequestStatus> GetPullRequestStatusAsync(int prNumber, CancellationToken ct = default);
}
```

The git + GitHub implementation lives in `Hone.SourceControl.Git/`:

```csharp
public class GitVersionControl : IVersionControl { ... }
public class GitHubCodeHost : ICodeHost { ... }
```

#### 4.2 ExperimentBranchManager (`Hone.SourceControl/Experiments/`)

**Replaces:** `Apply-Suggestion.ps1` (210 lines), `Revert-ExperimentCode.ps1` (158 lines)

Uses `IVersionControl` — doesn't know about git specifics.

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `ApplySuggestion_CreatesExperimentBranch` | Branch named `hone/experiment-N` created | `Apply-Suggestion.Tests.ps1` |
| `ApplySuggestion_WritesFileContent` | Target file contains new content | ✓ |
| `ApplySuggestion_CommitsWithMessage` | Commit message = `hone(experiment-N): {description}` | ✓ |
| `ApplySuggestion_PreservesRuntimeState` | Results directory preserved across branch switch | ✓ |
| `ApplySuggestion_ResetsMetadataBeforeFork` | Dirty runtime artifacts cleaned before branch creation | ✓ |
| `RevertExperiment_CreatesRevertCommit` | Revert commit preserves artifacts | `Revert-ExperimentCode.Tests.ps1` |
| `RevertExperiment_PreservesResultsDir` | Results directory not lost during revert | ✓ |

#### 4.3 PullRequestManager (`Hone.SourceControl/PullRequests/`)

**Replaces:** `New-ExperimentPR` + `Invoke-ExperimentBranchPush` + `Build-StackNote` in HoneHelpers.psm1, `Build-PRBody.ps1` (91 lines)

Uses `ICodeHost` — doesn't know about GitHub CLI specifics.

**Tests (`Hone.SourceControl.Tests/`):**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `CreatePR_SuccessfulExperiment_TitleContainsACCEPTED` | PR title format correct | HoneHelpers tests |
| `CreatePR_RejectedExperiment_TitleContainsREJECTED` | Rejected tag in title | ✓ |
| `CreatePR_DryRun_PrefixAddedToTitle` | `[DRY RUN]` prefix | ✓ |
| `BuildStackNote_MultiPrChain_FormatsCorrectly` | Stack line with arrows between PRs | `Build-PRBody.Tests.ps1` |
| `BuildStackNote_FailedExperimentsBetween_AddedToNote` | Failed experiments noted | ✓ |
| `PushBranch_Failure_ReturnsError` | Push fails → Success=false | ✓ |

**Tests (`Hone.SourceControl.Git.Tests/`):**

| Test | Behavior |
|------|----------|
| `GitVersionControl_Checkout_CallsGitProcess` | `git checkout` invoked via IProcessRunner |
| `GitVersionControl_Commit_CallsGitProcess` | `git commit` invoked with correct message |
| `GitHubCodeHost_CreatePR_CallsGhCli` | `gh pr create` invoked with correct args |
| `GitHubCodeHost_Push_CallsGitPush` | `git push -u origin <branch>` invoked |

### Validation Checkpoint
- All `Hone.SourceControl.Tests` and `Hone.SourceControl.Git.Tests` pass
- Branch creation/revert sequence operates correctly against a test repo
- PR body markdown matches expected snapshot for fixture data
- `ExperimentBranchManager` works with a fake `IVersionControl` confirming tool independence

---

## Phase 5: AI Agent Integration

### Goal
Migrate all AI agent interactions behind the `IAgentRunner` abstraction. Split into: `Hone.Agents.Core` (shared interface + invoker), `Hone.Agents.Loop` (optimization loop agents), `Hone.Agents.Preparation` (target setup agents), and `Hone.Agents.CopilotCli` (Copilot CLI implementation of `IAgentRunner`).

### Components

#### 5.1 IAgentRunner + CopilotCliAgentRunner (`Hone.Agents.Core/` + `Hone.Agents.CopilotCli/`)

**Replaces:** `Invoke-CopilotWithTimeout` (HoneHelpers.psm1), `Invoke-CopilotAgent.ps1` (191 lines)

`IAgentRunner` is defined in `Hone.Core/Contracts/` (see Phase 1). The generic invoker lives in `Hone.Agents.Core/`:

```csharp
public class AgentInvoker
{
    private readonly IAgentRunner _runner;
    
    // Generic agent invocation: model resolution, JSON extraction, retry on parse failure
    public Task<AgentResult<T>> InvokeAgentAsync<T>(AgentInvocationOptions options, 
        CancellationToken ct);
}
```

The Copilot CLI implementation lives in `Hone.Agents.CopilotCli/`:

```csharp
public class CopilotCliAgentRunner : IAgentRunner
{
    // Uses ProcessStartInfo.ArgumentList for proper quoting (same pattern as PowerShell)
    // Sets Console.OutputEncoding = UTF8
    // Async stdout/stderr reading to prevent deadlocks
    public Task<AgentRunResult> InvokeAsync(AgentInvocation invocation, CancellationToken ct);
}
```

**Tests (`Hone.Agents.Core.Tests/`):**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `AgentInvoker_ModelResolution_PerAgentOverride` | Agent-specific model overrides default | `Invoke-CopilotAgent.Tests.ps1` |
| `AgentInvoker_JsonParseFailed_RetriesWithSanitization` | NaN in response → sanitized, retried | ✓ |
| `AgentInvoker_MarkdownFences_Stripped` | Response wrapped in ```json``` → extracted | ✓ |
| `AgentInvoker_Timeout_ReturnsFailure` | IAgentRunner returns TimedOut → failure propagated | ✓ |

**Tests (`Hone.Agents.CopilotCli.Tests/`):**

| Test | Behavior |
|------|----------|
| `CopilotCliRunner_Timeout_KillsProcess` | Process exceeds timeout → killed, TimedOut=true |
| `CopilotCliRunner_UTF8Output_DecodedCorrectly` | Non-ASCII in agent response → preserved |
| `CopilotCliRunner_ArgumentQuoting_Correct` | Prompt with special characters → properly quoted |

#### 5.2 Loop Agents (`Hone.Agents.Loop/`)

The optimization loop agents — used during the main experiment cycle. Each agent takes an `AgentInvoker` (which delegates to `IAgentRunner`).

##### AnalysisAgent

**Replaces:** `Invoke-AnalysisAgent.ps1` (182 lines), `Build-AnalysisContext.ps1` (157 lines)

```csharp
public class AnalysisContextBuilder
{
    public AnalysisContext Build(string targetDir, HoneConfig config,
        MetricSet currentMetrics, MetricSet? baselineMetrics,
        ComparisonResult? comparison, CounterMetrics? counters,
        IReadOnlyList<AnalyzerReport>? diagnosticReports);
}

public record AnalysisContext(
    IReadOnlyList<string> SourceFilePaths, string CounterContext,
    string TrafficContext, string HistoryContext, string ProfilingContext);

public class AnalysisAgent
{
    public Task<AnalysisResult> AnalyzeAsync(AnalysisContext context, ...);
}
```

**Tests (`Hone.Agents.Loop.Tests/Analysis/`):**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `ContextBuilder_CollectsSourcePaths` | Scans SourceCodePaths for SourceFileGlob | `Build-AnalysisContext.Tests.ps1` |
| `ContextBuilder_FormatsCounterMetrics` | Counter CSV → readable context string | ✓ |
| `ContextBuilder_IncludesHistory` | experiment-log.md content included | ✓ |
| `ContextBuilder_IncludesDiagnosticReports` | CPU/GC reports injected into profiling section | ✓ |
| `AnalysisAgent_ParsesOpportunities` | JSON response → Opportunity[] | `Invoke-AnalysisAgent.Tests.ps1` |
| `AnalysisAgent_MockResponse_ExtractsPrimary` | First opportunity selected as primary | ✓ |

##### ClassificationAgent

**Replaces:** `Invoke-ClassificationAgent.ps1` (106 lines)

**Tests (`Hone.Agents.Loop.Tests/Classification/`):**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `ClassificationAgent_NarrowScope_Detected` | Single-file change → Narrow | `Invoke-ClassificationAgent.Tests.ps1` |
| `ClassificationAgent_ArchitectureScope_Detected` | Multi-file change → Architecture | ✓ |
| `ClassificationAgent_JsonParseFailure_DefaultsToArchitecture` | Malformed response → Architecture (safe fallback) | ✓ |

##### ImplementerAgent

**Replaces:** `Invoke-FixAgent.ps1` (164 lines) — renamed from "Fixer" to "Implementer"

**Tests (`Hone.Agents.Loop.Tests/Implementer/`):**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `ImplementerAgent_ExtractsCodeBlock` | Response with ```csharp fences → extracted code | `Invoke-FixAgent.Tests.ps1` |
| `ImplementerAgent_IncludesRCA` | RCA document appended to prompt | ✓ |
| `ImplementerAgent_RetryIncludesPreviousErrors` | Attempt >1 → previous build/test errors in prompt | ✓ |
| `ImplementerAgent_NoCodeBlock_ReturnsFailure` | Response without fenced code → Success=false | ✓ |

#### 5.3 Preparation Agents (`Hone.Agents.Preparation/`)

Agents used to prepare or validate a target before optimization begins.

##### CompatibilityAgent

**Replaces:** `Invoke-CompatibilityAgent.ps1` (242 lines)

**Tests (`Hone.Agents.Preparation.Tests/`):**

| Test | Behavior |
|------|----------|
| `CompatibilityAgent_CompatibleTarget_ReturnsSuccess` | Compatible target → Success=true |
| `CompatibilityAgent_IncompatibleTarget_ReturnsFailure` | Incompatible target → Success=false with reasoning |

### Validation Checkpoint
- All `Hone.Agents.Core.Tests`, `Hone.Agents.Loop.Tests`, `Hone.Agents.Preparation.Tests`, and `Hone.Agents.CopilotCli.Tests` pass
- Analysis context builder produces expected context sections for fixture data
- Mock response parsing extracts correct structured data from fixture agent responses
- `AgentInvoker` works with a fake `IAgentRunner` confirming backend independence

---

## Phase 6: Diagnostic Profiling

### Goal
Migrate the plugin framework: plugin discovery, multi-pass collection orchestration, and built-in collector/analyzer implementations.

### Components

#### 6.1 PluginDiscoveryService (`Hone.Diagnostics/Discovery/`)

**Replaces:** Directory scanning in `Invoke-DiagnosticCollection.ps1`

```csharp
public class PluginDiscoveryService
{
    // Scans directories for collector.yaml / analyzer.yaml metadata files
    // Returns discovered plugins with their settings merged from config
    public IReadOnlyList<DiscoveredCollector> DiscoverCollectors(string collectorsPath, DiagnosticsConfig config);
    public IReadOnlyList<DiscoveredAnalyzer> DiscoverAnalyzers(string analyzersPath, DiagnosticsConfig config);
}
```

**Tests:**

| Test | Behavior |
|------|----------|
| `DiscoverCollectors_FindsAllEnabled` | Scans fixture directory → finds perfview-cpu, perfview-gc, dotnet-counters |
| `DiscoverCollectors_DisabledPlugin_Excluded` | Enabled=false → not returned |
| `DiscoverCollectors_GroupAssignment_Correct` | Groups match collector.yaml metadata |
| `DiscoverAnalyzers_ChecksRequiredCollectors` | Missing required collector → analyzer skipped with warning |

#### 6.2 DiagnosticCollectionOrchestrator (`Hone.Diagnostics/Collection/`)

**Replaces:** `Invoke-DiagnosticCollection.ps1` (235 lines)

**Tests:**

| Test | Behavior |
|------|----------|
| `MultiPassScheduling_DifferentGroupsSeparated` | etw-cpu and etw-gc get separate passes |
| `DefaultGroupCollectors_RunInEveryPass` | dotnet-counters runs with each group |
| `CollectorFailure_OtherCollectorsContinue` | One collector throws → others still run |

#### 6.3 DiagnosticMeasurementOrchestrator (`Hone.Diagnostics/Measurement/`)

**Replaces:** `Invoke-DiagnosticMeasurement.ps1` (260 lines), `Invoke-DiagnosticAnalysis.ps1` (131 lines)

**Tests:**

| Test | Behavior |
|------|----------|
| `FullCycle_PerPass_StartCollectK6StopExport` | Per-pass lifecycle sequence correct |
| `AnalyzersRunAfterAllPasses` | All collectors done before analyzers start |
| `AnalyzerSkippedWhenRequiredCollectorMissing` | Required collector disabled → analyzer skipped |

#### 6.4 Built-in Collectors (native C#)

**Replaces:** `collectors/perfview-cpu/`, `collectors/perfview-gc/`, `collectors/dotnet-counters/`

| C# Class | Replaces | Key Behavior |
|-----------|----------|-------------|
| `PerfViewCpuCollector` | `perfview-cpu/` (492 lines total) | PerfView start/stop/export with ETW session cleanup |
| `PerfViewGcCollector` | `perfview-gc/` (similar) | PerfView /GCCollectOnly mode |
| `DotnetCountersCollectorPlugin` | `dotnet-counters/` (216 lines) | CSV collection + parsing |

#### 6.5 Built-in Analyzers (native C#)

**Replaces:** `analyzers/cpu-hotspots/` (139 lines), `analyzers/memory-gc/` (151 lines)

| C# Class | Replaces | Key Behavior |
|-----------|----------|-------------|
| `CpuHotspotsAnalyzer` | `cpu-hotspots/Invoke-Analyzer.ps1` | Parse folded stacks, build prompt, call agent via `IAgentRunner` |
| `MemoryGcAnalyzer` | `memory-gc/Invoke-Analyzer.ps1` | Parse GC report, build prompt, call agent via `IAgentRunner` |

### Validation Checkpoint
- All `Hone.Diagnostics.Tests` pass
- Plugin discovery finds same plugins as PowerShell scanning
- PerfView collector start/stop produces equivalent artifacts

---

## Phase 7: Reporting & Export

### Goal
Migrate result visualization: console table, HTML dashboard, RCA markdown, PR body generation.

### Components

#### 7.1 ResultsRenderer (`Hone.Reporting/Console/`)

**Replaces:** `Show-Results.ps1` (378 lines)

**Tests:**

| Test | Behavior |
|------|----------|
| `RenderResults_FormatsTable` | Fixture metrics → formatted console table |
| `RenderResults_HighlightsImprovements` | Improved metrics visually distinct |
| `RenderResults_EmptyResults_ShowsMessage` | No experiments → "No results" message |

#### 7.2 DashboardExporter (`Hone.Reporting/Dashboard/`)

**Replaces:** `Export-Dashboard.ps1` (1,000 lines)

**Tests:**

| Test | Behavior |
|------|----------|
| `ExportDashboard_GeneratesValidHtml` | Output parses as valid HTML |
| `ExportDashboard_IncludesChartJs` | Chart.js library embedded |
| `ExportDashboard_LatencyTrend_AllExperiments` | All experiment p95 values present in chart data |
| `ExportDashboard_SelfContained_NoExternalRefs` | No external CDN references |

#### 7.3 RcaExporter (`Hone.Reporting/Rca/`)

**Replaces:** `Export-ExperimentRCA.ps1` (164 lines)

**Tests:**

| Test | Behavior |
|------|----------|
| `ExportRCA_ContainsAllSections` | Metrics, analysis, root cause, proposed fix, results |
| `ExportRCA_MarkdownValid` | Output is valid markdown |
| `ExportRCA_IterativeFixer_AppendsSummary` | Iteration summary appended when present |

#### 7.4 PrBodyBuilder (`Hone.Reporting/PullRequest/`)

**Replaces:** `Build-PRBody.ps1` (91 lines)

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `BuildPRBody_ContainsMetrics` | p95, RPS, error rate in body | `Build-PRBody.Tests.ps1` |
| `BuildPRBody_ContainsStackNote` | Stack note present in stacked-diffs mode | ✓ |
| `BuildPRBody_ContainsDecision` | Accept/reject with reasoning | ✓ |

### Validation Checkpoint
- All `Hone.Reporting.Tests` pass
- PR body markdown matches expected snapshot for fixture data
- Dashboard HTML generates and opens correctly

---

## Phase 8: Orchestration

### Goal
Migrate the main loop (`Invoke-HoneLoop.ps1`) and supporting orchestration: queue management, iterative implementer, failure handler, and artifact staging. This is the largest and most complex phase — it wires everything together.

### Components

#### 8.1 OptimizationQueueManager (`Hone.Orchestration/Queue/`)

**Replaces:** `Manage-OptimizationQueue.ps1` (214 lines)

```csharp
public class OptimizationQueueManager
{
    public OptimizationQueue Initialize(IReadOnlyList<Opportunity> opportunities, int experiment);
    public QueueItem? GetNext();
    public bool HasActionable();
    public void MarkDone(int itemId, string outcome, int experiment);
    public void SyncMarkdown();
}
```

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `Init_CreatesQueueFile` | JSON file created at configured path | `Manage-OptimizationQueue.Tests.ps1` |
| `GetNext_ReturnsPendingNarrow` | Skips architecture items, returns first pending narrow | ✓ |
| `GetNext_MarksInProgress` | Returned item status = InProgress | ✓ |
| `MarkDone_RecordsOutcomeAndExperiment` | Item updated with outcome + triedByExperiment | ✓ |
| `HasActionable_AllDone_ReturnsFalse` | Empty queue → false, triggers re-analysis | ✓ |
| `AtomicWrite_NoTmpLeftovers` | No `.tmp` files after write | ✓ |
| `ConcurrentRead_SafeDuringWrite` | Read during write returns consistent state | ✓ |

#### 8.2 IterativeImplementerRunner (`Hone.Orchestration/Implementer/`)

**Replaces:** `Invoke-IterativeFix.ps1` (557 lines) — renamed from "Fixer" to "Implementer"

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `SingleAttempt_BuildPasses_Success` | Implement → build OK → test OK → Success | `Invoke-IterativeFix.Tests.ps1` |
| `BuildFailure_RetriesWithErrors` | Build fails → errors fed back → retry | ✓ |
| `TestFailure_RetriesWithOutput` | Tests fail → output fed back → retry | ✓ |
| `MaxAttempts_Exhausted_ReturnsFailure` | 3 attempts all fail → ExitReason=RetryBudgetExhausted | ✓ |
| `DiffGrowth_Exceeded_RejectsIteration` | Diff 4x larger than first attempt → rejected | ✓ |
| `TestFileGuard_BlocksTestModification` | Diff touches test project → rejected | ✓ |
| `PerAttempt_ArtifactsPreserved` | Each attempt has its own artifact directory | ✓ |
| `IterationLog_RecordsAllAttempts` | iteration-log.json contains all attempt records | ✓ |

#### 8.3 ExperimentFailureHandler (`Hone.Orchestration/Failure/`)

**Replaces:** `Invoke-FailureHandler.ps1` (96 lines)

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `HandleFailure_RevertsCode` | Experiment code changes reverted | `Invoke-FailureHandler.Tests.ps1` |
| `HandleFailure_UpdatesQueueOutcome` | Queue item marked with failure reason | ✓ |
| `HandleFailure_RecordsMetadata` | Metadata entry added with failure outcome | ✓ |
| `HandleFailure_PreservesArtifacts` | Experiment artifacts (logs, metrics) preserved | ✓ |

#### 8.4 ArtifactStager (`Hone.Orchestration/Artifacts/`)

**Replaces:** `Stage-ExperimentArtifacts.ps1` (100 lines)

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `StageArtifacts_CopiesLogs` | Build/test logs staged to experiment dir | `Stage-ExperimentArtifacts.Tests.ps1` |
| `StageArtifacts_CopiesMetrics` | k6 summaries staged | ✓ |
| `StageArtifacts_CopiesTrx` | Test result files staged | ✓ |

#### 8.5 HoneLoopRunner (`Hone.Orchestration/Loop/`)

**Replaces:** `Invoke-HoneLoop.ps1` (1,499 lines) — the main entry point

This is the most complex component. It orchestrates all phases in sequence:

```csharp
public class HoneLoopRunner
{
    public async Task<LoopResult> RunAsync(LoopOptions options, CancellationToken ct)
    {
        // 1. Load and merge config
        // 2. Validate config
        // 3. Establish baseline (if needed)
        // 4. Main experiment loop:
        //    a. Check queue → if empty, run analysis pipeline
        //    b. Pick next item from queue
        //    c. Run classification (if enabled)
        //    d. Run iterative implementer
        //    e. Run load tests (multi-run, median) via ILoadTestRunner
        //    f. Compare results
        //    g. Accept/reject/stale decision
        //    h. Publish (push + PR) or revert
        //    i. Update metadata
        //    j. Check exit conditions
    }
}
```

**Tests:**

| Test | Behavior | Pester Equivalent |
|------|----------|-------------------|
| `HappyPath_SingleExperiment_Accepted` | Full cycle: baseline → analyze → fix → verify → publish | `Invoke-HoneLoop.Tests.ps1` |
| `StackedDiffs_BranchChain_Correct` | Experiments form linear chain | ✓ |
| `StackedDiffs_FailedExperiment_RevertedContinues` | Failed experiment reverted, loop continues | ✓ |
| `QueueDriven_ReanalyzesWhenEmpty` | Queue empty → analysis runs again | ✓ |
| `MaxExperiments_StopsLoop` | Configured limit reached → loop exits | ✓ |
| `MaxConsecutiveFailures_StopsLoop` | N consecutive failures → loop exits | ✓ |
| `DryRun_SkipsSlowOperations` | DryRun flag → no k6, no API start, synthetic metrics | ✓ |
| `ExperimentMetadata_Consistent` | run-metadata.json matches experiment outcomes | ✓ |
| `PrChain_CorrectBaseAndNumbers` | Each PR targets correct base branch | ✓ |

### Validation Checkpoint
- All `Hone.Orchestration.Tests` pass
- Fixture target scenarios produce expected experiment counts, outcomes, and branch structures
- Queue state transitions are correct for fixture inputs

---

## Phase 9: CLI Host & Integration Tests

### Goal
Wire everything together with a console host and run full integration tests against fixture targets.

### Components

#### 9.1 Hone.Cli (`src/Hone.Cli/`)

```csharp
// Program.cs
var app = new CommandLineApplication();
app.Command("run", cmd => {
    var targetPath = cmd.Argument<string>("target", "Path to target project with .hone/config.yaml");
    var maxExperiments = cmd.Option<int>("--max-experiments", "Override max experiments");
    var dryRun = cmd.Option("--dry-run", "Skip slow operations");
    cmd.OnExecuteAsync(async ct => {
        var services = BuildServiceProvider(targetPath, maxExperiments, dryRun);
        var runner = services.GetRequiredService<HoneLoopRunner>();
        await runner.RunAsync(options, ct);
    });
});
```

**Commands:**
| Command | Replaces |
|---------|----------|
| `hone run --target <path>` | `Invoke-HoneLoop.ps1 -TargetPath <path>` |
| `hone run --target <path> --dry-run` | `Invoke-HoneLoop.ps1 -TargetPath <path> -DryRun` |
| `hone run --target <path> --max-experiments 5` | `Invoke-HoneLoop.ps1 -TargetPath <path> -MaxExperiments 5` |
| `hone baseline --target <path>` | `Get-PerformanceBaseline.ps1 -TargetDir <path>` |
| `hone results --target <path>` | `Show-Results.ps1` |
| `hone dashboard --target <path>` | `Export-Dashboard.ps1` |
| `hone validate --target <path>` | `Test-HoneConfig.ps1` |

#### 9.2 ServiceRegistration (`src/Hone.Cli/`)

```csharp
public static class ServiceRegistration
{
    public static IServiceProvider Build(CliOptions options)
    {
        var services = new ServiceCollection();
        
        // Core + Observability
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<HoneEventBus>();
        services.AddSingleton<IHoneEventSink>(sp => sp.GetRequiredService<HoneEventBus>());
        services.AddSingleton<IHoneEventSink, ConsoleEventSink>();
        services.AddSingleton<IHoneEventSink, JsonLogEventSink>();
        
        // Agent runner (swap implementation here for future backends)
        services.AddSingleton<IAgentRunner, CopilotCliAgentRunner>();
        services.AddTransient<AgentInvoker>();
        
        // Measurement (swap ILoadTestRunner for future load test tools)
        services.AddTransient<MetricComparer>();
        services.AddTransient<ILoadTestRunner, K6LoadTestRunner>();
        services.AddTransient<IRuntimeMetricsCollector, DotnetCountersCollector>();
        services.AddTransient<ScaleTestOrchestrator>();
        
        // Lifecycle
        services.AddTransient<HookResolver>();
        services.AddTransient<LifecycleHookDispatcher>();
        services.AddTransient<ConfigValidator>();
        
        // Source Control (swap implementations for future VCS/hosting)
        services.AddTransient<IVersionControl, GitVersionControl>();
        services.AddTransient<ICodeHost, GitHubCodeHost>();
        services.AddTransient<ExperimentBranchManager>();
        services.AddTransient<PullRequestManager>();
        
        // Loop Agents
        services.AddTransient<AnalysisAgent>();
        services.AddTransient<ClassificationAgent>();
        services.AddTransient<ImplementerAgent>();
        
        // Preparation Agents
        services.AddTransient<CompatibilityAgent>();
        
        // Diagnostics
        services.AddTransient<PluginDiscoveryService>();
        services.AddTransient<DiagnosticCollectionOrchestrator>();
        services.AddTransient<DiagnosticMeasurementOrchestrator>();
        
        // Reporting
        services.AddTransient<DashboardExporter>();
        services.AddTransient<RcaExporter>();
        services.AddTransient<PrBodyBuilder>();
        
        // Orchestration
        services.AddTransient<OptimizationQueueManager>();
        services.AddTransient<IterativeImplementerRunner>();
        services.AddTransient<ExperimentFailureHandler>();
        services.AddTransient<HoneLoopRunner>();
        
        return services.BuildServiceProvider();
    }
}
```

#### 9.3 Integration Tests (`tests/Hone.Integration.Tests/`)

Full E2E tests using YAML-configured fixture targets, testing the complete pipeline end-to-end:

| Scenario | Fixture Target | Behavior | Pester Equivalent |
|----------|---------------|----------|-------------------|
| `HappyPath_SingleExperiment` | `happy-path/` | Baseline → analyze → implement → improved → PR created | `Invoke-HoneLoop.Tests.ps1` |
| `BuildFailure_ExperimentRejected` | `build-failure/` | Implementation causes build failure → reverted → metadata recorded | ✓ |
| `TestFailure_ExperimentRejected` | `test-failure/` | Implementation causes test regression → reverted → metadata recorded | ✓ |
| `PerfRegression_ExperimentRejected` | `regression/` | Implementation degrades performance → rejected → PR with REJECTED tag | ✓ |
| `StaleExperiment_CountedAndContinues` | Synthetic | No improvement → stale count incremented → loop continues | ✓ |
| `StackedDiffs_BranchChain` | `stacked-diffs/` | Multiple experiments form linear branch chain with correct PR bases | ✓ |
| `QueueRefill_AnalysisRerunsWhenEmpty` | Synthetic | Queue exhausted → analysis runs again with fresh metrics | ✓ |
| `MaxExperiments_LoopStops` | Synthetic | After N experiments → loop exits cleanly | ✓ |
| `MaxConsecutiveFailures_LoopStops` | Synthetic | After N failures → loop exits with correct reason | ✓ |
| `DryRun_SkipsSlowOps` | `happy-path/` + DryRun | Load tests not called, synthetic metrics used, agents still run | ✓ |
| `IterativeImplementer_RetryOnBuildFailure` | `build-failure/` | First attempt fails build, second succeeds | `Invoke-IterativeFix.Tests.ps1` |
| `IterativeImplementer_TestFileGuard` | Synthetic | Implementation touches test files → rejected | ✓ |
| `DiagnosticProfiling_CollectorFlow` | Synthetic | Collectors start/stop/export in correct order | ✓ |
| `ObservabilityEvents_EmittedForAllPhases` | `happy-path/` | All phases emit PhaseStarted/PhaseCompleted events | New |

### Validation Checkpoint
- All integration tests pass
- `hone run --target .\sample-api --dry-run --max-experiments 1` completes successfully
- CLI argument parsing works correctly for all supported flags
- Observability events captured by both ConsoleEventSink and JsonLogEventSink

---

## Phase 10: Target Migration & Cutover

### Goal
Convert existing target `.hone/` configurations from `.psd1` to YAML, update all documentation, and cut over from PowerShell to C#.

### 10.1 Target Config Migration

Convert all `.hone/config.psd1` files to `.hone/config.yaml`:

| Target | Action |
|--------|--------|
| `sample-api/.hone/config.psd1` | Convert to `config.yaml`, validate equivalent `HoneConfig` |
| `eShopOnWeb/.hone/config.psd1` | Convert to `config.yaml`, validate equivalent `HoneConfig` |
| `harness/config.psd1` (engine defaults) | Convert to `config.yaml` |
| All test fixture targets | Convert `.psd1` to `.yaml` |

### 10.2 End-to-End Validation

With all phases complete, validate the full C# harness works end-to-end:

| Validation | Method |
|-----------|--------|
| **Metric comparison** | Fixture inputs → expected ComparisonResult JSON (snapshot tests) |
| **Queue state** | Analysis input → correct queue JSON after init/consume/markDone sequence |
| **Config merge** | Engine + target YAML configs → correct merged HoneConfig |
| **Branch structure** | Fixture scenarios → correct branch names, base branches, commit messages |
| **PR body** | Fixture data → expected markdown output (snapshot tests) |
| **RCA** | Experiment data → expected RCA markdown (snapshot tests) |
| **Full loop** | `hone run --target sample-api --dry-run` completes with correct experiment lifecycle |

### 10.3 Documentation Updates

| Document | Changes |
|----------|---------|
| `docs/architecture.md` | Rewrite for C# harness: .NET 10, solution structure, module dependency graph, observability pipeline |
| `docs/getting-started.md` | Update prerequisites (.NET 10 SDK), remove PowerShell/Pester, update commands to `hone run` |
| `docs/configuration.md` | Rewrite for YAML format, document all config sections with YAML examples |
| `docs/plugin-contracts.md` | Rewrite with C# interfaces (`ICollectorPlugin`, `IAnalyzerPlugin`) |
| `docs/adapter-contracts.md` | Update `.hone/` contract for YAML config, document new hook types (BuiltIn/Command/Http/Skip) |
| `docs/agent-designs.md` | Add `IAgentRunner` architecture, document CopilotCli implementation |
| `.github/copilot-instructions.md` | Full rewrite: C# tech stack, new directories, coding conventions |
| `README.md` | Update to reference C# harness, YAML config, `hone` CLI |
| `docs/features/csharp-migration/` | Add migration completion summary |

### 10.4 Cutover Steps

1. **Archive PowerShell harness** — move to `harness-legacy/` or tag the final PowerShell commit
2. **Publish C# harness** as the primary entry point
3. **Update CI** — switch from Pester to xUnit test runs
4. **Update dev setup** — replace `Setup-DevEnvironment.ps1` with equivalent (or a `hone setup` command)
5. **Remove PowerShell dependencies** — no more `.PSScriptAnalyzerSettings.psd1`, `Invoke-Lint.ps1`, `.githooks/pre-commit` PowerShell lint

### 10.5 Rollback Plan

If issues are discovered post-cutover:
- PowerShell harness remains in `harness-legacy/` and is immediately runnable
- Git tag marks the last PowerShell-only commit for easy revert

---

## Appendix A: Module Dependency Graph

```
Hone.Cli
  └── Hone.Orchestration
        ├── Hone.Agents.Loop
        │     ├── Hone.Agents.Core
        │     │     └── Hone.Core
        │     └── Hone.Core
        ├── Hone.Agents.Preparation
        │     ├── Hone.Agents.Core
        │     └── Hone.Core
        ├── Hone.Agents.CopilotCli
        │     ├── Hone.Agents.Core
        │     └── Hone.Core
        ├── Hone.Measurement
        │     └── Hone.Core
        ├── Hone.Measurement.K6
        │     ├── Hone.Measurement
        │     └── Hone.Core
        ├── Hone.Measurement.DotnetCounters
        │     ├── Hone.Measurement
        │     └── Hone.Core
        ├── Hone.Diagnostics
        │     ├── Hone.Agents.Core
        │     ├── Hone.Measurement
        │     └── Hone.Core
        ├── Hone.Lifecycle
        │     └── Hone.Core
        ├── Hone.SourceControl
        │     └── Hone.Core
        ├── Hone.SourceControl.Git
        │     ├── Hone.SourceControl
        │     └── Hone.Core
        ├── Hone.Reporting
        │     ├── Hone.Measurement
        │     └── Hone.Core
        └── Hone.Core
```

---

## Appendix B: Full PowerShell → C# File Mapping

| PowerShell File | LOC | C# Module | C# Class(es) |
|----------------|-----|-----------|--------------|
| `Invoke-HoneLoop.ps1` | 1,499 | Hone.Orchestration | `HoneLoopRunner` |
| `HoneHelpers.psm1` | 920 | Hone.Core + Hone.Lifecycle + Hone.SourceControl | Split across: `StringUtils`, `ConfigLoader`, `ConfigMerger`, `HookResolver`, `LifecycleHookDispatcher`, `PullRequestManager`, `ExperimentBranchManager`, `HoneEventBus` |
| `Invoke-IterativeFix.ps1` | 557 | Hone.Orchestration | `IterativeImplementerRunner` |
| `Invoke-ScaleTests.ps1` | 480 | Hone.Measurement + Hone.Measurement.K6 | `ScaleTestOrchestrator`, `K6LoadTestRunner`, `K6SummaryParser` |
| `Show-Results.ps1` | 378 | Hone.Reporting | `ResultsRenderer` |
| `Compare-Results.ps1` | 367 | Hone.Measurement | `MetricComparer` |
| `Test-HoneConfig.ps1` | 280 | Hone.Lifecycle | `ConfigValidator` |
| `config.psd1` | 270 | Hone.Core | `HoneConfig` hierarchy + `ConfigLoader` (YAML) |
| `Invoke-DiagnosticMeasurement.ps1` | 260 | Hone.Diagnostics | `DiagnosticMeasurementOrchestrator` |
| `Invoke-CompatibilityAgent.ps1` | 242 | Hone.Agents.Preparation | `CompatibilityAgent` |
| `Invoke-DiagnosticCollection.ps1` | 235 | Hone.Diagnostics | `DiagnosticCollectionOrchestrator` |
| `Manage-OptimizationQueue.ps1` | 214 | Hone.Orchestration | `OptimizationQueueManager` |
| `Apply-Suggestion.ps1` | 210 | Hone.SourceControl | `ExperimentBranchManager` |
| `Get-PerformanceBaseline.ps1` | 196 | Hone.Measurement | `BaselineMeasurer` |
| `Invoke-CopilotAgent.ps1` | 191 | Hone.Agents.Core + Hone.Agents.CopilotCli | `AgentInvoker`, `CopilotCliAgentRunner` |
| `Invoke-AnalysisAgent.ps1` | 182 | Hone.Agents.Loop | `AnalysisAgent` |
| `Stop-DotnetCounters.ps1` | 175 | Hone.Measurement.DotnetCounters | `DotnetCountersCollector` |
| `Invoke-FixAgent.ps1` | 164 | Hone.Agents.Loop | `ImplementerAgent` |
| `Export-ExperimentRCA.ps1` | 164 | Hone.Reporting | `RcaExporter` |
| `Revert-ExperimentCode.ps1` | 158 | Hone.SourceControl | `ExperimentBranchManager` |
| `Build-AnalysisContext.ps1` | 157 | Hone.Agents.Loop | `AnalysisContextBuilder` |
| `Update-OptimizationMetadata.ps1` | 152 | Hone.Orchestration | `ExperimentMetadataManager` |
| `Invoke-E2ETests.ps1` | 148 | Hone.Lifecycle (built-in hook) | `DotnetTestHook` |
| `analyzers/cpu-hotspots/Invoke-Analyzer.ps1` | 139 | Hone.Diagnostics | `CpuHotspotsAnalyzer` |
| `analyzers/memory-gc/Invoke-Analyzer.ps1` | 151 | Hone.Diagnostics | `MemoryGcAnalyzer` |
| `Invoke-DiagnosticAnalysis.ps1` | 131 | Hone.Diagnostics | `DiagnosticAnalysisOrchestrator` |
| `Start-DotnetCounters.ps1` | 121 | Hone.Measurement.DotnetCounters | `DotnetCountersCollector` |
| `Invoke-AllScaleTests.ps1` | 118 | Hone.Measurement | `AllScenariosRunner` |
| `Get-MachineInfo.ps1` | 118 | Hone.Core | `MachineInfoCollector` |
| `Invoke-ClassificationAgent.ps1` | 106 | Hone.Agents.Loop | `ClassificationAgent` |
| `Invoke-Build.ps1` | 101 | Hone.Lifecycle (built-in hook) | `DotnetBuildHook` |
| `Stage-ExperimentArtifacts.ps1` | 100 | Hone.Orchestration | `ArtifactStager` |
| `Invoke-FailureHandler.ps1` | 96 | Hone.Orchestration | `ExperimentFailureHandler` |
| `Build-PRBody.ps1` | 91 | Hone.Reporting | `PrBodyBuilder` |
| `hooks/Invoke-Hook.ps1` | 89 | Hone.Lifecycle | `LifecycleHookDispatcher` |
| `Write-HoneLog.ps1` | 87 | Hone.Core | `HoneLogger` |
| `Reset-Database.ps1` | 80 | Hone.Lifecycle (shared hook) | `DatabaseResetHook` |
| `Start-SampleApi.ps1` | 74 | Hone.Lifecycle (shared hook) | `DotnetStartHook` |
| `hooks/dotnet-start.ps1` | 72 | Hone.Lifecycle | `DotnetStartHook` |
| `hooks/k6-run.ps1` | 55 | Hone.Lifecycle | `K6RunHook` |
| `Invoke-Cooldown.ps1` | 45 | Hone.Measurement | `CooldownManager` |
| `hooks/dotnet-test.ps1` | 45 | Hone.Lifecycle | `DotnetTestHook` |
| `hooks/dotnet-build.ps1` | 33 | Hone.Lifecycle | `DotnetBuildHook` |
| `hooks/health-poll.ps1` | 32 | Hone.Lifecycle | `HealthPollHook` |
| `hooks/dotnet-stop.ps1` | 136 | Hone.Lifecycle | `DotnetStopHook` |
| `Export-Dashboard.ps1` | 1,000 | Hone.Reporting | `DashboardExporter` |
| `Show-Progress.ps1` | — | Hone.Core | `ProgressReporter` |

---

## Appendix C: NuGet Packages

| Package | Module | Purpose |
|---------|--------|---------|
| `System.CommandLine` | Hone.Cli | CLI argument parsing |
| `System.Text.Json` | Hone.Core | JSON serialization (already in BCL) |
| `YamlDotNet` | Hone.Core | YAML config parsing |
| `Microsoft.Extensions.DependencyInjection` | Hone.Cli | DI container |
| `Microsoft.Extensions.Logging` | Hone.Core | Logging abstractions |
| `xunit` + `xunit.runner.visualstudio` | All test projects | Test framework |
| `FluentAssertions` | All test projects | Assertion library |
| `NSubstitute` or `Moq` | All test projects | Mocking library |
| `Coverlet.collector` | All test projects | Code coverage |
| `Verify.Xunit` | Integration tests | Snapshot/golden output testing |
