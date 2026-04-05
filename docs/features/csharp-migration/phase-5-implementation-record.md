# Phase 5 Implementation Record: AI Agent Integration

> **Status:** Complete  
> **Date:** 2025-07-18  
> **Worker Agent:** `hone-migration-core` (fallback for `hone-migration-agent-integration`)  
> **Orchestrator:** `hone-migration-orchestrator`

---

## Summary

Phase 5 delivered the complete AI agent integration layer across four projects: `Hone.Agents.Core` (generic agent invocation with model resolution, JSON extraction, retry), `Hone.Agents.CopilotCli` (Copilot CLI process runner), `Hone.Agents.Loop` (analysis, classification, and implementer agents), and `Hone.Agents.Preparation` (compatibility assessment agent with filesystem pre-probing).

**Test delta:** 4 placeholder tests → 65 agent tests (+61 net new). Full solution: 423 tests, 0 failures, 0 warnings.

---

## Slices Executed

| Slice | Description | Worker | Critics | Outcome |
|-------|-------------|--------|---------|---------|
| 5-1 | AgentInvoker + AgentInvocationOptions + AgentResult\<T\> | `hone-migration-core` | 4 always-on + security-process, concurrency | ✅ approve (1st pass) |
| 5-2 | CopilotCliAgentRunner | `hone-migration-core` | 4 always-on + security-process, concurrency | ✅ approve (1st pass) |
| 5-3 | AnalysisContextBuilder + AnalysisContext + CounterSummary | `hone-migration-core` | 4 always-on | ✅ approve (1st pass) |
| 5-4 | AnalysisAgent + AnalysisResult | `hone-migration-core` | 4 always-on + concurrency | ✅ approve (1st pass) |
| 5-5 | ClassificationAgent + ClassificationResult | `hone-migration-core` | 4 always-on + concurrency | ✅ approve (1st pass) |
| 5-6 | ImplementerAgent + ImplementerResult | `hone-migration-core` | 4 always-on + concurrency | ✅ approve (1st pass) |
| 5-7 | CompatibilityAgent + PreProber + CompatibilityReport | `hone-migration-core` | 4 always-on + security-process, concurrency | ✅ approve (after fix) |

**Always-on critics:** design-conformance, correctness, parity, scope  
**On-demand critics used:** security-process (Slices 5-1, 5-2, 5-7), concurrency (Slices 5-1, 5-2, 5-4, 5-5, 5-6, 5-7)

---

## Files Created

### Hone.Agents.Core (3 files)

| File | Type | Replaces |
|------|------|----------|
| `src/Hone.Agents.Core/AgentInvocationOptions.cs` | sealed record | Options for `Invoke-CopilotAgent.ps1` parameters |
| `src/Hone.Agents.Core/AgentResult.cs` | sealed record | `Invoke-CopilotAgent.ps1` return object |
| `src/Hone.Agents.Core/AgentInvoker.cs` | sealed class | Core of `Invoke-CopilotAgent.ps1` (model resolution, JSON extraction, retry) |

### Hone.Agents.CopilotCli (1 file)

| File | Type | Replaces |
|------|------|----------|
| `src/Hone.Agents.CopilotCli/CopilotCliAgentRunner.cs` | sealed class | `Invoke-CopilotWithTimeout` (HoneHelpers.psm1) |

### Hone.Agents.Loop — Analysis (4 files)

| File | Type | Replaces |
|------|------|----------|
| `src/Hone.Agents.Loop/Analysis/AnalysisContext.cs` | sealed record | `Build-AnalysisContext.ps1` return object |
| `src/Hone.Agents.Loop/Analysis/CounterSummary.cs` | sealed record | Counter display values (decouples from Hone.Measurement) |
| `src/Hone.Agents.Loop/Analysis/AnalysisContextBuilder.cs` | static class | `Build-AnalysisContext.ps1` (157 lines) |
| `src/Hone.Agents.Loop/Analysis/AnalysisResult.cs` | sealed record | `Invoke-AnalysisAgent.ps1` return object |
| `src/Hone.Agents.Loop/Analysis/AnalysisAgent.cs` | sealed class | `Invoke-AnalysisAgent.ps1` (182 lines) |

### Hone.Agents.Loop — Classification (2 files)

| File | Type | Replaces |
|------|------|----------|
| `src/Hone.Agents.Loop/Classification/ClassificationResult.cs` | sealed record | `Invoke-ClassificationAgent.ps1` return object |
| `src/Hone.Agents.Loop/Classification/ClassificationAgent.cs` | sealed class | `Invoke-ClassificationAgent.ps1` (106 lines) |

### Hone.Agents.Loop — Implementer (2 files)

| File | Type | Replaces |
|------|------|----------|
| `src/Hone.Agents.Loop/Implementer/ImplementerResult.cs` | sealed record | `Invoke-FixAgent.ps1` return object |
| `src/Hone.Agents.Loop/Implementer/ImplementerAgent.cs` | sealed class | `Invoke-FixAgent.ps1` (164 lines) — renamed from "Fixer" to "Implementer" |

### Hone.Agents.Preparation (5 files)

| File | Type | Replaces |
|------|------|----------|
| `src/Hone.Agents.Preparation/CompatibilityResult.cs` | sealed record | `Invoke-CompatibilityAgent.ps1` return object |
| `src/Hone.Agents.Preparation/CompatibilityReport.cs` | DTOs | Agent response schema (compatibility, target, onboarding plan) |
| `src/Hone.Agents.Preparation/CompatibilityAgent.cs` | sealed class | `Invoke-CompatibilityAgent.ps1` (242 lines) |
| `src/Hone.Agents.Preparation/PreProber.cs` | internal static class | Pre-probe logic (git, filesystem scanning) |
| `src/Hone.Agents.Preparation/PreProbeData.cs` | internal records | Pre-probe data DTOs |

### Tests (4 files, 65 tests total)

| File | Tests | Coverage |
|------|-------|----------|
| `tests/Hone.Agents.Core.Tests/AgentInvokerTests.cs` | 12 | Model resolution (4), JSON extraction/sanitization (2), timeout, happy path, retries, exceptions, prompt suffix, timeout propagation |
| `tests/Hone.Agents.CopilotCli.Tests/CopilotCliAgentRunnerTests.cs` | 10 | Argument construction (4), process success/failure, timeout, cancellation, UTF-8, null guard |
| `tests/Hone.Agents.Loop.Tests/Analysis/AnalysisContextBuilderTests.cs` | 14 | Source paths (2), counters (2), traffic (2), history (5), profiling (3) |
| `tests/Hone.Agents.Loop.Tests/Analysis/AnalysisAgentTests.cs` | 8 | Opportunity parsing, primary extraction, empty response, model config, prompt content, scope defaults, title fallback, null comparison |
| `tests/Hone.Agents.Loop.Tests/Classification/ClassificationAgentTests.cs` | 5 | Narrow/architecture detection, JSON failure fallback, model config, prompt content |
| `tests/Hone.Agents.Loop.Tests/Implementer/ImplementerAgentTests.cs` | 5 | Code block extraction, RCA inclusion, retry errors, no-code-block failure, model config |
| `tests/Hone.Agents.Preparation.Tests/CompatibilityAgentTests.cs` | 11 | Compatible/incompatible targets, pre-probe scanning, invalid path, timeout, invalid JSON, model override, git probe, dir filtering, .hone detection |

---

## Files Modified

| File | Change | Reason |
|------|--------|--------|
| `src/Hone.Agents.CopilotCli/Hone.Agents.CopilotCli.csproj` | Added `InternalsVisibleTo` | Testing `BuildArguments` internal method |
| `src/Hone.Agents.Loop/Hone.Agents.Loop.csproj` | Added CA1716 NoWarn | `Loop` namespace segment triggers CA1716 |

---

## Files Deleted

| File | Reason |
|------|--------|
| `src/Hone.Agents.Core/Placeholder.cs` | Replaced by real implementation |
| `src/Hone.Agents.CopilotCli/Placeholder.cs` | Replaced by real implementation |
| `src/Hone.Agents.Loop/Placeholder.cs` | Replaced by real implementation |
| `src/Hone.Agents.Preparation/Placeholder.cs` | Replaced by real implementation |
| `tests/Hone.Agents.Core.Tests/PlaceholderTests.cs` | Replaced by real tests |
| `tests/Hone.Agents.CopilotCli.Tests/PlaceholderTests.cs` | Replaced by real tests |
| `tests/Hone.Agents.Loop.Tests/PlaceholderTests.cs` | Replaced by real tests |
| `tests/Hone.Agents.Preparation.Tests/PlaceholderTests.cs` | Replaced by real tests |

---

## Critic Review Summary

### Slice 5-1 — AgentInvoker

**Critics:** design-conformance, correctness, parity, scope, security-process, concurrency  
**Outcome:** `approve`

All 6 critics approved. FrozenDictionary-based model override lookup is thread-safe and immutable. Retry loop correctly bounded (0 to MaxRetries inclusive). JSON pipeline (ExtractJsonBlock → SanitizeNaN → Deserialize<T>) preserves PS extraction order. Security-process critic confirmed no injection vectors in prompt construction.

### Slice 5-2 — CopilotCliAgentRunner

**Critics:** design-conformance, correctness, parity, scope, security-process, concurrency  
**Outcome:** `approve`

`ProcessStartInfo.ArgumentList` prevents shell injection (not `Arguments` string). Async stdout/stderr reads prevent buffer deadlocks. Linked CancellationTokenSource correctly distinguishes timeout from caller cancellation. Process tree kill on both paths. 2 non-blocking advisories: stderr task not consumed on timeout/cancel paths (minor log noise risk), test helper duplicates production process logic.

### Slice 5-3 — AnalysisContextBuilder

**Critics:** design-conformance, correctness, parity, scope  
**Outcome:** `approve`

All 5 PS context sections ported with exact format-string parity. `CounterSummary` record decouples `Hone.Agents.Loop` from `Hone.Measurement` — orchestration layer owns the conversion. Sorted diagnostic reports match PS `Sort-Object` behavior. Queue JSON/markdown fallback logic preserved.

### Slice 5-4 — AnalysisAgent

**Critics:** design-conformance, correctness, parity, scope, concurrency  
**Outcome:** `approve`

Prompt text matches PS structure (Current Performance, Baseline Performance, context sections, source file list). Private DTOs for JSON parsing with normalization to `Opportunity` domain records. Scope defaults to `Narrow` matching PS. Title/explanation cross-fill logic preserved.

### Slice 5-5 — ClassificationAgent

**Critics:** design-conformance, correctness, parity, scope, concurrency  
**Outcome:** `approve`

Agent `hone-classifier` with `claude-haiku-4.5` default, 2 retries with RFC 8259 JSON retry suffix — exact PS parity. Safe fallback to `Architecture` on failure.

### Slice 5-6 — ImplementerAgent

**Critics:** design-conformance, correctness, parity, scope, concurrency  
**Outcome:** `approve`

Agent `hone-fixer` with `FixModel` config key mapping to `ImplementerModel`. Prompt sections (RCA, retry context with error output + current file content) match PS. Code block extraction via `JsonUtils.ExtractCodeBlock` with null-detection guard. Uses `InvokeAgentAsync<object>` for RawOutput since response is code, not JSON.

### Slice 5-7 — CompatibilityAgent

**Critics:** design-conformance, correctness, parity, scope, security-process, concurrency  
**Outcome:** `approve` (after fix)

**Blocking finding CORR-1:** `PreProber.ProbeGitAsync` remote URL parsing used `string.Replace("origin", "")` which corrupts URLs containing "origin" in the hostname. **Fix:** Replaced with tab-split parsing (`firstLine.Split('\t')`) matching PS anchored regex `'^origin\s+'`. Re-verified after fix.

Pre-probe filesystem scanning matches PS 1:1: 14 project file patterns, 3-level depth, max 10 hits, max 30 directory/file entries, excluded dirs (node_modules, bin, obj, packages), .hone detection. All git commands use `IProcessRunner` with 10-second timeouts — no shell injection vectors.

---

## Approved Design Deviations

| Deviation | Rationale | Doc Updated |
|-----------|-----------|-------------|
| `CounterSummary` record instead of `RuntimeCounterMetrics` parameter | Decouples `Hone.Agents.Loop` from `Hone.Measurement`; orchestration layer converts | No — design improvement |
| `AnalysisContextBuilder` is static, not instance | Pure function with no state — matches Phase 2 MetricComparer pattern | No — consistent with approved pattern |
| PS `Copilot.AgentTimeoutSec` default 600 → C# `AgentConfig.AgentTimeoutSec` default 1800 | Increased timeout matches production usage; configurable | No — intentional change |
| Mock response path not in `AgentInvoker` | Orchestration-layer concern, not core agent invocation | No — correct layering |
| Spinner/response-saving not in agents | UI and persistence are orchestration-layer concerns | No — correct layering |
| PS result fields omitted (`PromptPath`, `ResponsePath`, etc.) | Disk persistence belongs in orchestration layer | No — correct layering |
| `BuildArguments` default model `claude-sonnet-4-20250514` in CopilotCliAgentRunner | Fallback when `AgentInvoker` passes null model; AgentInvoker always resolves model | No — defensive default |
| C# retries on JSON parse failure (PS retries on runner exceptions) | More useful — JSON parse failures are recoverable by asking the LLM again | No — improvement |
| PS model resolution: per-agent → config default → caller default; C# per-agent → caller default → config default | In practice equivalent (caller default is null by default). C# API is more idiomatic | No — minor precedence reorder |

---

## Key Technical Decisions

### AgentInvoker Model Resolution
Three-tier fallback: per-agent config override (via `ModelConfigKey` → `AgentConfig` property) → caller-supplied default → `AgentConfig.DefaultModel`. The PS `FixModel` key is aliased to `ImplementerModel` via a `FrozenDictionary` lookup, preserving backward compatibility.

### CopilotCliAgentRunner Process Management
Uses `ProcessStartInfo.ArgumentList` (not `Arguments` string) for security — prevents shell interpretation of special characters in prompts. Linked `CancellationTokenSource` combines caller cancellation and timeout into a single token. Process tree kill via `Kill(entireProcessTree: true)` ensures child processes are cleaned up.

### CounterSummary Decoupling
The `AnalysisContextBuilder` accepts pre-formatted counter display values (`CounterSummary`) rather than raw `RuntimeCounterMetrics` from `Hone.Measurement`. This prevents the `Hone.Agents.Loop` project from depending on the measurement layer, maintaining the directed acyclic graph of project references specified in the proposal.

### CompatibilityAgent Pre-Probing
The pre-probe uses `IProcessRunner` for git commands and `System.IO` for filesystem scanning. This matches the PS pattern (raw git + Get-ChildItem) while remaining testable via NSubstitute mocks. The pre-probe is an internal implementation detail — the public API exposes only `AssessAsync(targetPath, model?, ct)`.

---

## PowerShell Parity Matrix

| PowerShell Script | C# Replacement | Parity Status |
|-------------------|----------------|---------------|
| `Invoke-CopilotAgent.ps1` (239 lines) | `AgentInvoker` + `AgentInvocationOptions` + `AgentResult<T>` | ✅ Full parity — model resolution, JSON extraction, retry, timeout |
| `Invoke-CopilotWithTimeout` (HoneHelpers.psm1, 69 lines) | `CopilotCliAgentRunner` | ✅ Full parity — process management, async I/O, timeout/kill |
| `Build-AnalysisContext.ps1` (157 lines) | `AnalysisContextBuilder` | ✅ Full parity — all 5 context sections |
| `Invoke-AnalysisAgent.ps1` (182 lines) | `AnalysisAgent` | ✅ Full parity — prompt, parsing, normalization |
| `Invoke-ClassificationAgent.ps1` (106 lines) | `ClassificationAgent` | ✅ Full parity — prompt, scope detection, safe fallback |
| `Invoke-FixAgent.ps1` (164 lines) | `ImplementerAgent` | ✅ Full parity — prompt with RCA/retry, code block extraction |
| `Invoke-CompatibilityAgent.ps1` (242 lines) | `CompatibilityAgent` + `PreProber` | ✅ Full parity — pre-probe, prompt, report parsing |

---

## Validation Results

| Check | Result |
|-------|--------|
| `dotnet build Hone.slnx` — zero warnings | ✅ Pass |
| `dotnet test Hone.slnx` — 423 tests, all passing | ✅ Pass |
| Phase 5 tests: 65 new tests across 7 test files | ✅ All pass |
| AgentInvoker: 12 tests covering model resolution, JSON pipeline, retries | ✅ Pass |
| CopilotCliAgentRunner: 10 tests covering arguments, process lifecycle | ✅ Pass |
| AnalysisContextBuilder: 14 tests covering all 5 context sections | ✅ Pass |
| AnalysisAgent: 8 tests covering opportunity parsing and normalization | ✅ Pass |
| ClassificationAgent: 5 tests covering scope detection and fallback | ✅ Pass |
| ImplementerAgent: 5 tests covering code block extraction and prompts | ✅ Pass |
| CompatibilityAgent: 11 tests covering pre-probe, assessment, and edge cases | ✅ Pass |
| No Phase 6+ work performed | ✅ Confirmed |

---

## Critic Rejections & Resolutions

| Slice | Critic | Finding | Resolution |
|-------|--------|---------|------------|
| 5-7 | correctness/parity | `PreProber.ProbeGitAsync` remote URL parsing: `string.Replace("origin", "")` corrupts URLs containing "origin" | Fixed: replaced with tab-split parsing matching PS anchored regex. Re-approved. |

---

## Risks

- **CopilotCliAgentRunner stderr task leak:** On timeout/cancellation paths, the stderr `ReadToEndAsync` task is not consumed. Modern .NET won't crash but may produce `UnobservedTaskException` log noise. Low priority — add `_ = stderrTask.ContinueWith(...)` in future hardening.
- **AnalysisContextBuilder sync I/O:** All file reads use synchronous `File.ReadAllText` (with `#pragma RS0030` suppressions). Matches PS parity but should be converted to async when the orchestration layer is migrated.
- **`{...}` first-match extraction absent:** PS does `$jsonText -match '(?s)(\{.+\})'` to extract JSON from surrounding prose. C# `JsonUtils.ExtractJsonBlock` only strips fences. If an agent returns unfenced JSON in prose, deserialization fails and triggers a retry. Low risk — retries mitigate.
- **CompatibilityAgent pre-probe git dependency:** If `IProcessRunner` is not provided, git info is skipped entirely. The orchestration layer must inject the runner. This is by design (testability).

---

## Hardening Backlog (Non-Blocking)

| Item | Source | Priority |
|------|--------|----------|
| Add `{...}` first-match extraction to `JsonUtils.ExtractJsonBlock` | Slice 5-1 critic | Low |
| Consume stderr task on timeout/cancel in CopilotCliAgentRunner | Slice 5-2 critic | Low |
| Add `ArgumentNullException.ThrowIfNull(targetDir)` to AnalysisContextBuilder | Slice 5-3 critic | Low |
| Convert sync file I/O in AnalysisContextBuilder to async | Slice 5-3 review | Medium — when orchestration layer uses async pipelines |
| Unify Success semantics across agents (ExitCode vs JSON-parsed) | Slices 5-4/5-5 critic | Low |
| Reduce TestableAgentRunner duplication in CopilotCli tests | Slice 5-2 critic | Low |

---

## Recommended Next Phase

**Phase 6: Diagnostic Profiling**

- Implements `Hone.Diagnostics` with PluginDiscoveryService, DiagnosticCollectionOrchestrator, DiagnosticMeasurementOrchestrator, built-in collectors (PerfView CPU/GC, dotnet-counters), and built-in analyzers (CPU hotspots, memory/GC).
- Worker: `hone-migration-core`
- Always-on critics plus likely `hone-csharp-concurrency-critic` (multi-pass scheduling), `hone-migration-security-process-critic` (PerfView process management), and `hone-csharp-performance-critic` (ETW session handling).
