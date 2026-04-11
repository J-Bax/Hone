# Post-Migration Fixups

Tracked TODO items from PR #20 review. These represent future improvements identified during the C# migration review.

## Hooks & Build System

### 1. Generic Build/Test Hooks
Build and Test hooks should not assume `dotnet build` and `dotnet test`. They should instead call a hook that the target blackbox API exposes. The target could be .NET Framework, for example.

**Current state:** `DotnetBuildHook` and `DotnetTestHook` are hardcoded to `dotnet` CLI commands.
**Future state:** Generic `BuildHook` and `TestHook` interfaces with pluggable implementations. The `dotnet` versions become one built-in default.

### 2. Hooks Design Review
How do hooks work now? Hooks are a critical part of the design so the target can expose interaction methods for Hone to invoke. Review the current hook architecture to ensure it supports the target API interaction model.

### 3. ~~Hook Documentation Generalization~~ ✅ Resolved
~~All documentation should be generic to "BuildHook" (interface-style, no implementation assumption) and "TestHook" (same). The implementation under the hood may end up being a built-in default hook for `dotnet build` and `dotnet test`, but high-level docs and code should not assume that.~~

**Resolved:** Created comprehensive `docs/hooks.md` documenting all 10 lifecycle hooks, 4 hook types (BuiltIn, Command, Http, Skip), 6 built-in hook implementations, and configuration examples for .NET, Java Spring Boot, and Python Flask targets. Updated `docs/configuration.md` with cross-references and diagnostic override examples. Updated `docs/getting-started.md` with non-.NET targeting guidance.

## Agent Design

### 4. Perf Analysis Agent Input Strategy
Should perf analysis agents get raw data as input prompt context? Or get a file pointer with all the raw data with detailed tool calling and agent mode enabled for them to do their own analysis? Needs investigation of token efficiency vs. analysis quality trade-offs.

### 5. Experiment History Context Bloat
Investigate whether including all experiment history bloats context. The history is there to ensure new experiments are picked, but as the number of experiments increases, this may flood the agent context window. Consider summarization, windowing, or relevance filtering.

### 6. Implementer Agent File Write Strategy
Why can't we just allow the implementer agent to make file writes itself? The current approach of parsing a response back and having something else apply the change seems overly complicated. Investigate letting the agent use tool calls to write files directly.

### 7. Agent Invocation Reliability
Can we make agent invocations stronger guarantees? Why allow them to fail at all? Investigate retry strategies, structured output enforcement, and deterministic prompting to reduce agent failure rates.

### 8. ~~AnalyzerResult Field Guarantees~~ ✅ Resolved
~~Can the analysis agent be guaranteed to give us all required fields in AnalyzerResult (except maybe error)? Currently these are weak nullable guarantees. Investigate structured output / schema enforcement to make these required.~~

**Resolved:** All 6 agent result records now have explicit nullable annotations and XML doc remarks documenting nullability semantics. `ClassificationResult.Scope` was changed from non-nullable `OpportunityScope` to `OpportunityScope?` to match deserialization reality. Internal DTOs already use `string?` for all JSON-sourced fields; `NormalizeOpportunities()` validates before constructing domain models. New tests cover missing/null scope and missing file path scenarios.

## Configuration & Compatibility

### 9. SourceCodePaths Completeness
Revisit SourceCodePaths to ensure it captures source code files for every scenario. Best option: the compatibility agent does a pre and post conversion evaluation, and part of that is ensuring SourceCodePaths config includes all actual source code folders.

### 10. ~~Diagnostic Measurement Overrideability~~ ✅ Resolved
~~Evaluate how to make diagnostic measurement configuration overrideable in the target API. Current diagnostics won't always be applicable to every target (e.g., PerfView is Windows-only, dotnet-counters requires .NET runtime).~~

**Resolved:** Added `DiagnosticsConfig? Diagnostics` to `TargetConfig` so targets can explicitly override/disable diagnostic collectors in `.hone/config.yaml`. Added YAML type mappings in `TargetConfigLoader` for deserialization. Added `ValidateDiagnosticCompatibility()` in `ConfigValidator` that warns when PerfView collectors are enabled on non-Windows platforms. Added commented example in `sample-api/.hone/config.yaml` showing the override pattern. Documented in `docs/hooks.md` and `docs/configuration.md`.

## Diagnostics

### 11. FoldedStackParser Module Filtering
Why can't we use included modules for stack filtering? Presumably we know the target API process name. Investigate using module inclusion lists rather than broad stack capture.

### 12. ~~FoldedStackParser Test Fixtures~~ ✅ Resolved
~~Does FoldedStackParser have a clear example output format to wire through unit tests from a real PerfView trace? Create representative test fixtures from actual PerfView output.~~

**Resolved:** `FoldedStackParserTests.cs` contains 7 test cases with representative PerfView CSV fixtures covering parsing, module filtering, sorting, truncation, and edge cases.

## Platform Support

### 13. Multi-Agent System Support
Add support for different agent systems beyond GitHub Copilot CLI — e.g., Claude Code, API-based agents. The `IAgentRunner` interface was designed for this; implement additional backends.

## Lifecycle Hooks (from audit)

### Undispatched Lifecycle Hooks — ✅ Partially Resolved
`ConfigValidator` requires all 10 lifecycle hooks but originally only 6 were dispatched. Warmup, Active, Cooldown, and Cleanup were validated but never fired.

**Resolved (3 of 4):** Added `WarmupAsync()`, `CooldownAsync()`, and `CleanupAsync()` to `ILoopPipeline` and `LoopPipelineAdapter`. Warmup is dispatched before the scale test measurement cycle. Cooldown is dispatched as the `afterRunCallback` after each k6 run (replacing the hardcoded GC endpoint callback). Cleanup is dispatched at the end of the experiment loop in `HoneLoopRunner`. Updated test mocks in both `HoneLoopRunnerTests` and `IntegrationTestBase`.

**Deferred — Active hook:** The Active hook (`BuiltIn: k6-run`) represents the load test itself. `K6RunHook.ExecuteAsync()` returns `HookResult` which lacks `LoadTestResult` metrics data (p95, RPS, error rate) needed by `ScaleTestOrchestrator` for median selection. Properly dispatching Active requires the hook system to support typed return values — a larger refactor that aligns with fixup #13 (Multi-Agent System Support / pluggable measurement backends).
