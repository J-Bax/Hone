# C# Migration Summary — PowerShell → .NET 10

> **Status:** Complete (Phase 10.3 — Documentation Updates)
> **Migration started:** 2026-04-04
> **Migration completed:** 2026-04-05 (Phase 10 — Target Migration & Cutover)
> **Test coverage:** 629+ tests across 15+1 projects

---

## Overview

The Hone harness has been fully migrated from PowerShell 7.2+ scripts to a C# / .NET 10 solution (`harness-csharp/Hone.slnx`). The migration maintained full behavioral fidelity with the original PowerShell implementation while adding strong typing, a structured observability pipeline, an iterative implementer loop, and a proper CLI host.

The PowerShell harness (`harness-legacy*`) remains in place as a reference implementation and is immediately runnable. The C# harness is now the primary implementation.

---

## Phase-by-Phase Summary

| Phase | Name | Deliverables |
|-------|------|-------------|
| **0** | Solution Scaffolding | .NET 10 solution, 15+1 projects, CI, editorconfig, BannedSymbols, central package management |
| **1** | Core Domain Models, Config & Observability | `HoneConfig` YAML hierarchy, `ConfigLoader`/`ConfigMerger`, domain records (`MetricSet`, `ComparisonResult`, `Opportunity`, `QueueItem`, etc.), `HoneEventBus`/`IHoneEventSink`, `StringUtils`/`JsonUtils` |
| **2** | Measurement & Comparison | `MetricComparer` (accept/reject decision engine), `ILoadTestRunner`/`K6LoadTestRunner`/`K6SummaryParser`, `IRuntimeMetricsCollector`/`DotnetCountersCollector`, `BaselineMeasurer`, `ScaleTestOrchestrator` |
| **3** | Lifecycle & Hooks | `ILifecycleHook`, `LifecycleHookDispatcher`, `HookResolver`, `ConfigValidator`, built-in hooks (`DotnetBuildHook`, `DotnetStartHook`, `DotnetStopHook`, `DotnetTestHook`, `HealthPollHook`, `K6RunHook`, `DatabaseResetHook`) |
| **4** | Source Control | `IVersionControl`/`GitVersionControl`, `ICodeHost`/`GitHubCodeHost`, `ExperimentBranchManager`, `PullRequestManager` |
| **5** | AI Agent Integration | `IAgentRunner`, `CopilotCliAgentRunner`, `AgentInvoker`, `AnalysisAgent`, `ClassificationAgent`, `ImplementerAgent`, `AnalysisContextBuilder` |
| **6** | Diagnostic Profiling | `ICollectorPlugin`/`IAnalyzerPlugin`, `DiagnosticCollectionOrchestrator`, `DiagnosticAnalysisOrchestrator`, `CpuHotspotsAnalyzer`, `MemoryGcAnalyzer`, `DiagnosticMeasurementOrchestrator` |
| **7** | Reporting & Export | `ResultsRenderer`, `DashboardExporter`, `RcaBuilder`, `PrBodyBuilder`, `ExperimentMetadataManager` |
| **8** | Orchestration | `HoneLoopRunner`, `OptimizationQueueManager`, `IterativeImplementerRunner`, `ExperimentFailureHandler`, `ArtifactStager` |
| **9** | CLI Host & Integration Tests | `Program.cs` with System.CommandLine (`hone run`, `hone validate`, `hone baseline`, `hone results`, `hone dashboard`), `ServiceRegistration`, `LoopPipelineAdapter`, `ImplementerPipelineAdapter`, 14 integration test scenarios |
| **10** | Target Migration & Cutover | `.hone/` configs converted to YAML, documentation rewritten for C# tech stack |

---

## Key Architectural Changes

### Configuration: `.psd1` → YAML

| Before | After |
|--------|-------|
| `harness/config.psd1` (PowerShell data file) | `harness-csharp/config.yaml` (engine defaults) |
| `.hone/config.psd1` (target config) | `.hone/config.yaml` (target config) |
| `Get-HoneConfig` / `Merge-HoneConfig` in `HoneHelpers.psm1` | `ConfigLoader.Load()` + `ConfigMerger.Merge()` in `Hone.Core` |
| Runtime overrides via `-MaxExperiments`, `-DryRun` PowerShell params | CLI flags: `--max-experiments`, `--dry-run`, `--model` |

YAML uses PascalCase keys matching C# record property names, deserialized via `YamlDotNet` with `PascalCaseNamingConvention`.

### Hook Types: PowerShell → C#

| Before (PowerShell) | After (C#) |
|---------------------|-----------|
| `Type = 'Script'; Path = '.hone\hooks\prepare.ps1'` | `Type: Command` (inline) or `Type: BuiltIn; Name: sqlserver-reset` |
| `Type = 'Shared'; Name = 'dotnet-start'` | `Type: BuiltIn; Name: dotnet-start` |
| `Type = 'Command'; Value = '...'` | `Type: Command; Value: '...'` (unchanged) |
| `Type = 'Http'; Method = 'POST'; Path = '/diag/gc'` | `Type: Http; Method: POST; Path: /diag/gc` (unchanged) |
| `Type = 'Skip'` | `Type: Skip` (unchanged) |

### Agent Naming: `Fixer` → `Implementer`

The "Fixer" agent and all associated concepts were renamed to "Implementer" to better reflect the agent's role (implementing optimization proposals, not just "fixing" bugs):

| Before | After |
|--------|-------|
| `hone-fixer` agent | `hone-implementer` agent |
| `Invoke-FixAgent.ps1` | `ImplementerAgent` in `Hone.Agents.Loop` |
| `Invoke-IterativeFix.ps1` | `IterativeImplementerRunner` in `Hone.Orchestration` |
| `Copilot.FixModel` | `Agents.ImplementerModel` |
| `config.psd1: Fixer = @{...}` | `config.yaml: Implementer: {...}` |

### Config Section Renames

| Before (`config.psd1`) | After (`config.yaml`) |
|------------------------|-----------------------|
| `Copilot = @{...}` | `Agents: {...}` |
| `Copilot.Model` | `Agents.DefaultModel` |
| `Copilot.AnalysisModel` | `Agents.AnalysisModel` |
| `Copilot.ClassificationModel` | `Agents.ClassificationModel` |
| `Copilot.FixModel` | `Agents.ImplementerModel` |
| `Copilot.AgentTimeoutSec` | `Agents.AgentTimeoutSec` |

### Observability: `Write-Status` / `Write-HoneLog.ps1` → `HoneEventBus`

The PowerShell harness used ad-hoc `Write-Status` calls and a `Write-HoneLog.ps1` script for logging. The C# harness uses a structured event pipeline:

- All components receive `IHoneEventEmitter` via dependency injection
- Events are typed records (`PhaseStarted`, `PhaseCompleted`, `AgentInvoked`, `ExperimentOutcomeEvent`, etc.)
- `HoneEventBus` broadcasts to registered `IHoneEventSink` implementations
- Built-in sinks: `ConsoleEventSink` (timestamped console) and `JsonLogEventSink` (JSONL rotation)
- Future sinks (TUI, webhook) can be added without modifying harness code

### CLI: PowerShell Scripts → `System.CommandLine`

| Before (PowerShell) | After (C# `hone` CLI) |
|---------------------|----------------------|
| `.\harness\Invoke-HoneLoop.ps1` | `hone run --target <path>` |
| `.\harness\Invoke-HoneLoop.ps1 -TargetPath <path>` | `hone run --target <path>` |
| `.\harness\Get-PerformanceBaseline.ps1` | `hone baseline --target <path>` |
| `.\harness\Show-Results.ps1` | `hone results --target <path>` |
| `.\harness\Export-Dashboard.ps1` | `hone dashboard --target <path>` |
| `.\harness\Test-HoneConfig.ps1` | `hone validate --target <path>` |
| `-MaxExperiments 10` | `--max-experiments 10` |
| `-DryRun` | `--dry-run` |

---

## Test Coverage Summary

| Phase | New Tests | Cumulative |
|-------|-----------|-----------|
| Phase 0 | 15 (placeholder per project) | 15 |
| Phase 1 | ~60 (Core domain, config, observability) | ~75 |
| Phase 2 | ~80 (MetricComparer, K6, counters) | ~155 |
| Phase 3 | ~65 (hooks, dispatcher, validator) | ~220 |
| Phase 4 | ~55 (VCS, git, PR management) | ~275 |
| Phase 5 | ~80 (agent runner, invoker, agents) | ~355 |
| Phase 6 | ~70 (diagnostics, collectors, analyzers) | ~425 |
| Phase 7 | ~55 (reporting, RCA, dashboard) | ~480 |
| Phase 8 | ~80 (loop runner, queue, implementer) | ~560 |
| Phase 9 | +14 (integration tests) −1 (placeholder) | **629+** |

Tests use xUnit, NSubstitute for mocking, and FluentAssertions. Integration tests use fixture targets in `harness-csharp/test-fixtures/`. The `MetricComparer` has snapshot tests using `Verify.Xunit` for golden-output validation.

---

## What Was Preserved

- **All five agents** and their prompt structures, output schemas, and role definitions
- **Decision logic** — `MetricComparer` produces bit-for-bit identical results to `Compare-Results.ps1`
- **Stacked diffs mode** and all exit conditions (max experiments, consecutive failures, build/test abort)
- **Optimization queue** design (JSON file, atomic writes, queue-driven analysis cycle)
- **Diagnostic plugin architecture** — same `collector.yaml` + `analyzer.yaml` metadata, same group-based pass scheduling
- **Mermaid diagrams** in all architecture documentation (labels updated to C# class names)
- **k6 scenarios** — no changes to JavaScript load test files
- **Sample API** (`.NET 6`) — no changes to the optimization target
- **`.hone/` directory contract** — structure preserved; only config format changed from `.psd1` to `.yaml`
- **Design principles** — all 9 architectural principles remain valid and documented

## What Changed

- **Language**: PowerShell 7.2+ → C# / .NET 10
- **Config format**: `.psd1` → YAML (PascalCase keys)
- **Hook types**: `Script`/`Shared` → `BuiltIn`; `Command`, `Http`, `Skip` unchanged
- **Agent naming**: `hone-fixer`/`Fixer` → `hone-implementer`/`Implementer`
- **Config sections**: `Copilot` → `Agents`
- **Entry point**: `Invoke-HoneLoop.ps1` → `hone run`
- **Observability**: ad-hoc `Write-Status` → typed `HoneEventBus` events
- **Timeout**: default agent timeout extended from 600s to 1800s
- **Iterative implementer**: `IterativeImplementerRunner` adds retry loop with diff-size guard and test-file guard (was single-shot in PowerShell)
- **DI container**: all components wired via `ManualServiceProvider` (lightweight `IServiceProvider`)

## Remaining Gaps / Future Work

- `hone baseline`, `hone results`, and `hone dashboard` commands are fully wired — `hone baseline` runs scale tests via `ScaleTestOrchestrator` and saves results; `hone results` reads the results directory and renders via `ResultsRenderer`; `hone dashboard` generates a self-contained HTML dashboard via `DashboardExporter`
- Non-.NET target support via `Command` hooks is functional but not tested end-to-end
- `CompatibilityAgent` (from `Hone.Agents.Preparation`) produces assessment reports but is not yet exposed as a dedicated CLI command
- TUI / web dashboard sinks for `HoneEventBus` are designed but not implemented
- The PowerShell harness in `harness/` is fully functional but no longer actively maintained — it will be archived in a future phase

---

## Full PowerShell → C# File Mapping

See [Appendix B of the phased plan](phased-plan.md#appendix-b-full-powershell--c-file-mapping) for the complete 40+ file mapping table.
