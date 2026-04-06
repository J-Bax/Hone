# Phase 6 Implementation Record — Diagnostic Profiling

> **Status:** Complete  
> **Phase:** 6 — Diagnostic Profiling (`Hone.Diagnostics`)
> **Baseline:** 373 tests → 466 tests (+93 new, 69 in Hone.Diagnostics.Tests)  
> **Build:** 0 warnings, 0 errors  

---

## Summary

Phase 6 migrates the diagnostic profiling plugin framework from PowerShell to C#. This includes plugin discovery, multi-pass collection orchestration, three built-in collector implementations (PerfView CPU, PerfView GC, dotnet-counters), two built-in analyzer implementations (CPU hotspots, Memory GC), and the measurement orchestrator that ties them together.

### PowerShell Files Replaced

| PowerShell File | Lines | C# Replacement |
|---|---|---|
| `Invoke-DiagnosticCollection.ps1` | 289 | `DiagnosticCollectionOrchestrator` |
| `Invoke-DiagnosticMeasurement.ps1` | 318 | `DiagnosticMeasurementOrchestrator` |
| `Invoke-DiagnosticAnalysis.ps1` | 166 | `DiagnosticMeasurementOrchestrator.RunAnalyzersAsync` |
| `collectors/perfview-cpu/` (4 files) | 492 | `PerfViewCpuCollector` + `PerfViewHelper` + `FoldedStackParser` |
| `collectors/perfview-gc/` (4 files) | ~490 | `PerfViewGcCollector` + `PerfViewHelper` + `GcReportParser` |
| `collectors/dotnet-counters/` (4 files) | 216 | `DotnetCountersCollectorPlugin` |
| `analyzers/cpu-hotspots/Invoke-Analyzer.ps1` | 179 | `CpuHotspotsAnalyzer` |
| `analyzers/memory-gc/Invoke-Analyzer.ps1` | 190 | `MemoryGcAnalyzer` |

---

## Slices Executed

### Slice 1: Plugin Contracts + Discovery Models + PluginDiscoveryService

**Worker:** `hone-migration-core`  
**Critics:** design-conformance ✅, correctness ✅, parity ✅, scope ✅  
**Gate:** APPROVED  

**Files created/modified:**
- `Hone.Core/Contracts/ICollectorPlugin.cs` — replaced placeholder with full interface (Name, StartAsync, StopAsync, ExportAsync)
- `Hone.Core/Contracts/IAnalyzerPlugin.cs` — replaced placeholder with full interface (Name, RequiredCollectors, AnalyzeAsync)
- `Hone.Core/Models/CollectorSettings.cs` — typed settings dictionary with convenience accessors
- `Hone.Core/Models/CollectorStartResult.cs` — start result with handle
- `Hone.Core/Models/CollectorExportResult.cs` — export result with extra properties passthrough
- `Hone.Core/Models/AnalyzerContext.cs` — context for analyzer invocation
- `Hone.Core/Models/AnalyzerResult.cs` — analyzer report result
- `Hone.Diagnostics/Discovery/CollectorMetadata.cs` — parsed collector.yaml model
- `Hone.Diagnostics/Discovery/AnalyzerMetadata.cs` — parsed analyzer.yaml model
- `Hone.Diagnostics/Discovery/DiscoveredCollector.cs` — discovered collector record
- `Hone.Diagnostics/Discovery/DiscoveredAnalyzer.cs` — discovered analyzer record
- `Hone.Diagnostics/Discovery/PluginDiscoveryService.cs` — YAML scanning, settings merge, PerfViewExePath injection
- `Hone.Diagnostics.Tests/Discovery/PluginDiscoveryServiceTests.cs` — 8 tests

**Post-review fix:** Added explicit `<PackageReference Include="YamlDotNet" />` to `Hone.Diagnostics.csproj` (critic F2).

### Slice 2: DiagnosticCollectionOrchestrator

**Worker:** `hone-migration-core`  
**Critics:** design-conformance ✅, correctness ✅, parity ✅, scope ✅, reliability ✅, performance ✅  
**Gate:** APPROVED  

**Files created:**
- `Hone.Diagnostics/Collection/DiagnosticCollectionOrchestrator.cs` — GetGroups, StartAsync, StopAsync, ExportAsync with failure isolation
- `Hone.Diagnostics/Collection/CollectionStartResult.cs`
- `Hone.Diagnostics/Collection/CollectionStopResult.cs`
- `Hone.Diagnostics/Collection/CollectionExportResult.cs`
- `Hone.Diagnostics.Tests/Collection/DiagnosticCollectionOrchestratorTests.cs` — 8 tests

### Slice 3: Built-in Collector Implementations

**Worker:** `hone-migration-core`  
**Critics:** design-conformance ✅, correctness ✅, parity (initially ⛔, fixed ✅), scope ✅, performance ✅, reliability (initially ⛔, fixed ✅)  
**Gate:** APPROVED after remediation  

**Files created:**
- `Hone.Diagnostics/Collectors/PerfViewCpuCollector.cs` — CPU sampling with ETW, folded stack export, alloc type export
- `Hone.Diagnostics/Collectors/PerfViewGcCollector.cs` — GC-only mode, HTML→JSON export
- `Hone.Diagnostics/Collectors/DotnetCountersCollectorPlugin.cs` — dotnet-counters collect, CSV/JSON export
- `Hone.Diagnostics/Collectors/PerfViewHelper.cs` — shared ETW cleanup, stale file cleanup, abort+poll stop, PerfView temp dir search
- `Hone.Diagnostics/Collectors/FoldedStackParser.cs` — CSV→folded-stack with module filtering
- `Hone.Diagnostics/Collectors/GcReportParser.cs` — HTML→GcReport with GeneratedRegex
- `Hone.Diagnostics/Collectors/GcReport.cs` — GC report model classes
- `Hone.Diagnostics/Collectors/PerfViewHandle.cs` — PerfView collection state
- `Hone.Diagnostics/Collectors/DotnetCountersHandle.cs` — dotnet-counters collection state
- `Hone.Diagnostics.Tests/Collectors/FoldedStackParserTests.cs` — 7 tests
- `Hone.Diagnostics.Tests/Collectors/GcReportParserTests.cs` — 7 tests
- `Hone.Diagnostics.Tests/Collectors/PerfViewCpuCollectorTests.cs` — 6 tests
- `Hone.Diagnostics.Tests/Collectors/PerfViewGcCollectorTests.cs` — 4 tests
- `Hone.Diagnostics.Tests/Collectors/DotnetCountersCollectorPluginTests.cs` — 6 tests

**Remediation applied:**
- **B1 (Parity):** Added PerfView temp dir (`%LOCALAPPDATA%\Temp\PerfView`) search for GCStats HTML and alloc CSV — matches PowerShell fallback behavior
- **B2 (Reliability):** Abort file write uses `CancellationToken.None`; `StopAsync` finally blocks force-cancel the CTS and wait for PerfView exit — prevents ETW session orphaning
- **N2 (Reliability):** `CleanStaleFiles` catches `UnauthorizedAccessException` alongside `IOException`

### Slice 4: Built-in Analyzer Implementations

**Worker:** `hone-migration-core`  
**Critics:** design-conformance ✅, correctness ✅, parity ✅, scope ✅  
**Gate:** APPROVED  

**Files created:**
- `Hone.Diagnostics/Analyzers/CpuHotspotsAnalyzer.cs` — reads folded stacks, truncates, calls hone-cpu-profiler agent
- `Hone.Diagnostics/Analyzers/MemoryGcAnalyzer.cs` — reads GC report + optional alloc types, calls hone-memory-profiler agent
- `Hone.Diagnostics/Analyzers/AnalyzerPromptHelper.cs` — shared prompt building, metrics formatting, JSON parsing
- `Hone.Diagnostics.Tests/Analyzers/CpuHotspotsAnalyzerTests.cs` — 8 tests
- `Hone.Diagnostics.Tests/Analyzers/MemoryGcAnalyzerTests.cs` — 8 tests

### Slice 5: DiagnosticMeasurementOrchestrator

**Worker:** `hone-migration-core`  
**Critics:** design-conformance ✅, correctness (initially ⛔, fixed ✅), parity (initially ⛔, fixed ✅), scope ✅, reliability ✅, performance ✅  
**Gate:** APPROVED after remediation  

**Files created:**
- `Hone.Diagnostics/Measurement/DiagnosticMeasurementOrchestrator.cs` — collection pass orchestration + analyzer execution
- `Hone.Diagnostics/Measurement/CollectionPassResult.cs`
- `Hone.Diagnostics/Measurement/DiagnosticAnalysisResult.cs`
- `Hone.Diagnostics/Measurement/DiagnosticMeasurementResult.cs`
- `Hone.Diagnostics.Tests/Measurement/DiagnosticMeasurementOrchestratorTests.cs` — 7 tests

**Remediation applied:**
- **B1 (Correctness/Parity):** Added `Func<Task>? workload` parameter to `RunCollectionPassAsync` — provides k6 execution slot between collector start/stop
- **NB-2:** Added `EmitWarning` helper using `LogLevel.Warning` for partial failure messages
- Export exception tolerance: wrapped `ExportAsync` in try/catch matching PowerShell behavior
- `DiagnosticMeasurementResult` documented as caller-assembled type

---

## Files Created (29 source + test files)

### Source (`harness-csharp/src/Hone.Diagnostics/`)

| Directory | File | Description |
|---|---|---|
| Discovery/ | `CollectorMetadata.cs` | Parsed collector.yaml model |
| Discovery/ | `AnalyzerMetadata.cs` | Parsed analyzer.yaml model |
| Discovery/ | `DiscoveredCollector.cs` | Discovered collector with merged settings |
| Discovery/ | `DiscoveredAnalyzer.cs` | Discovered analyzer with merged settings |
| Discovery/ | `PluginDiscoveryService.cs` | YAML scanning + settings merge |
| Collection/ | `DiagnosticCollectionOrchestrator.cs` | Multi-action collection orchestrator |
| Collection/ | `CollectionStartResult.cs` | Start result model |
| Collection/ | `CollectionStopResult.cs` | Stop result model |
| Collection/ | `CollectionExportResult.cs` | Export result model |
| Collectors/ | `PerfViewCpuCollector.cs` | PerfView CPU sampling collector |
| Collectors/ | `PerfViewGcCollector.cs` | PerfView GC-only collector |
| Collectors/ | `DotnetCountersCollectorPlugin.cs` | dotnet-counters collector |
| Collectors/ | `PerfViewHelper.cs` | Shared PerfView operations |
| Collectors/ | `FoldedStackParser.cs` | CSV→folded-stack parser |
| Collectors/ | `GcReportParser.cs` | HTML→GcReport parser |
| Collectors/ | `GcReport.cs` | GC report model classes |
| Collectors/ | `PerfViewHandle.cs` | PerfView collection handle |
| Collectors/ | `DotnetCountersHandle.cs` | dotnet-counters handle |
| Analyzers/ | `CpuHotspotsAnalyzer.cs` | CPU hotspots agent analyzer |
| Analyzers/ | `MemoryGcAnalyzer.cs` | Memory GC agent analyzer |
| Analyzers/ | `AnalyzerPromptHelper.cs` | Shared prompt helper |
| Measurement/ | `DiagnosticMeasurementOrchestrator.cs` | Full measurement pipeline |
| Measurement/ | `CollectionPassResult.cs` | Per-pass result |
| Measurement/ | `DiagnosticAnalysisResult.cs` | Analysis result |
| Measurement/ | `DiagnosticMeasurementResult.cs` | Complete pipeline result |

### Core Models Updated (`harness-csharp/src/Hone.Core/`)

| File | Change |
|---|---|
| `Contracts/ICollectorPlugin.cs` | Replaced placeholder with full interface |
| `Contracts/IAnalyzerPlugin.cs` | Replaced placeholder with full interface |
| `Models/CollectorSettings.cs` | New typed settings dictionary |
| `Models/CollectorStartResult.cs` | New start result record |
| `Models/CollectorExportResult.cs` | New export result record |
| `Models/AnalyzerContext.cs` | New analyzer context record |
| `Models/AnalyzerResult.cs` | New analyzer result record |

### Tests (`harness-csharp/tests/Hone.Diagnostics.Tests/`)

| Test File | Tests |
|---|---|
| `Discovery/PluginDiscoveryServiceTests.cs` | 8 |
| `Collection/DiagnosticCollectionOrchestratorTests.cs` | 8 |
| `Collectors/FoldedStackParserTests.cs` | 7 |
| `Collectors/GcReportParserTests.cs` | 7 |
| `Collectors/PerfViewCpuCollectorTests.cs` | 6 |
| `Collectors/PerfViewGcCollectorTests.cs` | 4 |
| `Collectors/DotnetCountersCollectorPluginTests.cs` | 6 |
| `Analyzers/CpuHotspotsAnalyzerTests.cs` | 8 |
| `Analyzers/MemoryGcAnalyzerTests.cs` | 8 |
| `Measurement/DiagnosticMeasurementOrchestratorTests.cs` | 7 |
| **Total** | **69** |

---

## Critic Review Summary

| Slice | Critics Run | Initial Gate | Final Gate | Iterations |
|---|---|---|---|---|
| 1 (Contracts + Discovery) | 4 always-on | APPROVE | APPROVE | 1 |
| 2 (Collection Orchestrator) | 4 + reliability + performance | APPROVE | APPROVE | 1 |
| 3 (Built-in Collectors) | 4 + reliability + performance | REJECT (B1, B2) | APPROVE | 2 |
| 4 (Built-in Analyzers) | 4 always-on | APPROVE | APPROVE | 1 |
| 5 (Measurement Orchestrator) | 4 + reliability + performance | REJECT (B1) | APPROVE | 2 |

---

## Approved Deviations

1. **YAML instead of PSD1:** Collector/analyzer metadata uses `collector.yaml`/`analyzer.yaml` instead of `.psd1`. This is an approved format change for the C# migration (PSD1 is PowerShell-specific).

2. **Config lookup by directory name:** PowerShell uses resolved `meta.Name ?? dir.Name` as config key; C# uses `dirName` directly. The config documents keys as directory names, and all existing collectors have `Name == dirName`.

3. **Analyzer failure returns non-fatal:** PowerShell throws on analyzer failure; C# returns `DiagnosticAnalysisResult.Success = false`. The loop runner (Phase 8) must throw to preserve end-to-end behavior. Documented as a contract requirement.

4. **Measurement orchestrator does not manage lifecycle hooks:** API start/stop and database reset are handled by the loop host (Phase 8), not the measurement orchestrator. The orchestrator focuses on collection passes and analysis.

5. **Fixture-diagnostics fast-path deferred:** The PowerShell fixture override path (Invoke-DiagnosticMeasurement.ps1 lines 79-134) is handled at the loop host level, not in the measurement orchestrator.

---

## Validation Results

- **Build:** 0 warnings, 0 errors (full solution)
- **Tests:** 466 total (373 baseline + 69 new diagnostic + 24 from prior phases' natural growth)
- **Plugin discovery:** Finds same plugins as PowerShell scanning (verified via tests)
- **PerfView collector args:** Match PowerShell command construction (verified via tests)
- **Folded stack parser:** Produces correct output from PerfView CSV format (verified via tests)
- **GC report parser:** Correctly parses PerfView GCStats HTML (verified via tests)
- **Failure isolation:** Collector/analyzer failures don't cascade (verified via tests)
- **Multi-pass scheduling:** Groups correctly separated, default collectors included in each (verified via tests)

---

## Risks

1. **PerfView process management is inherently fragile** — PerfView's `/NoGui` mode has a known bug where the process lingers after completion. The C# implementation mirrors the PowerShell workaround (abort file + log polling + force termination). Real-world testing with actual PerfView is needed to validate.

2. **ETW session contention** — PerfView uses system-wide ETW sessions ("NT Kernel Logger", "PerfViewSession"). The stale session cleanup logic is ported but not integration-tested. Multiple concurrent Hone runs could interfere.

3. **GCStats HTML parsing is regex-based** — PerfView's HTML output format is not a stable API. Format changes in future PerfView versions could break parsing. The parser should be tested against real PerfView output periodically.

---

## Recommended Next Phase

**Phase 7: Reporting & Export** — Migrate result visualization (console table, HTML dashboard, RCA markdown, PR body generation) into `Hone.Reporting`.
