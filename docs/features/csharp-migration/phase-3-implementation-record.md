# Phase 3 Implementation Record: Lifecycle & Hooks

> **Status:** Complete  
> **Date:** 2026-04-06  
> **Worker Agent:** `hone-migration-core`  
> **Orchestrator:** `hone-migration-orchestrator`

---

## Summary

Phase 3 delivered the full Hone.Lifecycle subsystem: hook resolution, lifecycle hook dispatch, all 6 built-in shared hooks (dotnet-build, dotnet-start, dotnet-stop, dotnet-test, health-poll, k6-run), and configuration validation. This replaces `Resolve-Hook`, `Invoke-LifecycleHook`, `Invoke-Hook.ps1`, all 6 PowerShell hook scripts in `harness/hooks/`, and `Test-HoneConfig.ps1`.

**Test delta:** 15 placeholder tests → 87 lifecycle tests (+72 net new). Full solution: 320 tests, 0 failures.

---

## Slices Executed

| Slice | Description | Worker | Critics | Outcome |
|-------|-------------|--------|---------|---------|
| 3-1 | Foundation contracts (ILifecycleHook, HookType, ResolvedHook, HookContext) | `hone-migration-core` | 4 always-on | ✅ approve-with-doc-update |
| 3-2 | HookResolver + TargetConfig + TargetHookConfig | `hone-migration-core` | 4 always-on | ✅ approve |
| 3-3 | LifecycleHookDispatcher + IBuiltInHookRegistry | `hone-migration-core` | 4 always-on | ✅ approve |
| 3-4 | DotnetBuildHook + DotnetTestHook | `hone-migration-core` | 4 always-on | ✅ approve |
| 3-5 | HealthPollHook | `hone-migration-core` | 4 always-on + concurrency | ✅ approve |
| 3-6 | DotnetStartHook + DotnetStopHook | `hone-migration-core` | 4 always-on + concurrency + reliability | ✅ approve (after fix) |
| 3-7 | K6RunHook | `hone-migration-core` | 4 always-on | ✅ approve |
| 3-8 | ConfigValidator | `hone-migration-core` | 4 always-on + test-strategy | ✅ approve |
| 3-9 | Final validation + implementation record | Orchestrator direct | — | ✅ all checks pass |

---

## Files Created

### Hone.Core Contracts (1 file)
| File | Purpose |
|------|---------|
| `src/Hone.Core/Contracts/HookContext.cs` | Hook execution context record (TargetPath, Config, BaseUrl, Experiment) |

### Hone.Lifecycle — Hooks (6 files)
| File | Purpose |
|------|---------|
| `src/Hone.Lifecycle/Hooks/HookType.cs` | Enum: BuiltIn, Command, Http, Skip |
| `src/Hone.Lifecycle/Hooks/ResolvedHook.cs` | Record with 4 factory methods (BuiltIn, Command, Http, Skip) |
| `src/Hone.Lifecycle/Hooks/TargetConfig.cs` | Target project config record with hooks dictionary |
| `src/Hone.Lifecycle/Hooks/TargetHookConfig.cs` | Single hook config from YAML (Type, Value, Path, Method) |
| `src/Hone.Lifecycle/Hooks/HookResolver.cs` | Static resolver: hook name + TargetConfig → ResolvedHook |
| `src/Hone.Lifecycle/Hooks/IBuiltInHookRegistry.cs` | Registry interface for built-in hook lookup |
| `src/Hone.Lifecycle/Hooks/LifecycleHookDispatcher.cs` | Dispatches resolved hooks (BuiltIn/Command/Http/Skip) |

### Hone.Lifecycle — SharedHooks (6 files)
| File | Purpose |
|------|---------|
| `src/Hone.Lifecycle/SharedHooks/DotnetBuildHook.cs` | Replaces `dotnet-build.ps1` |
| `src/Hone.Lifecycle/SharedHooks/DotnetTestHook.cs` | Replaces `dotnet-test.ps1` |
| `src/Hone.Lifecycle/SharedHooks/HealthPollHook.cs` | Replaces `health-poll.ps1` |
| `src/Hone.Lifecycle/SharedHooks/DotnetStartHook.cs` | Replaces `dotnet-start.ps1` |
| `src/Hone.Lifecycle/SharedHooks/DotnetStopHook.cs` | Replaces `dotnet-stop.ps1` |
| `src/Hone.Lifecycle/SharedHooks/K6RunHook.cs` | Replaces `k6-run.ps1` |

### Hone.Lifecycle — Validation (2 files)
| File | Purpose |
|------|---------|
| `src/Hone.Lifecycle/Validation/ValidationResult.cs` | Sealed record: IsValid, Errors, Warnings |
| `src/Hone.Lifecycle/Validation/ConfigValidator.cs` | Static validator replacing `Test-HoneConfig.ps1` |

### Tests (8 files)
| File | Tests |
|------|-------|
| `tests/Hone.Lifecycle.Tests/Hooks/HookTypeTests.cs` | 12 |
| `tests/Hone.Lifecycle.Tests/Hooks/HookResolverTests.cs` | 10 |
| `tests/Hone.Lifecycle.Tests/Hooks/LifecycleHookDispatcherTests.cs` | 9 |
| `tests/Hone.Lifecycle.Tests/SharedHooks/DotnetBuildHookTests.cs` | 4 |
| `tests/Hone.Lifecycle.Tests/SharedHooks/DotnetTestHookTests.cs` | 5 |
| `tests/Hone.Lifecycle.Tests/SharedHooks/HealthPollHookTests.cs` | 5 |
| `tests/Hone.Lifecycle.Tests/SharedHooks/DotnetStartHookTests.cs` | 3 |
| `tests/Hone.Lifecycle.Tests/SharedHooks/DotnetStopHookTests.cs` | 6 |
| `tests/Hone.Lifecycle.Tests/SharedHooks/K6RunHookTests.cs` | 6 |
| `tests/Hone.Lifecycle.Tests/Validation/ConfigValidatorTests.cs` | 27 |

**Total new test methods: 87** (replaces 1 placeholder test from Phase 0)

---

## Files Modified

| File | Change | Reason |
|------|--------|--------|
| `src/Hone.Core/Contracts/ILifecycleHook.cs` | Added `ExecuteAsync(HookContext, CancellationToken)` | Was empty placeholder from Phase 0 |
| `src/Hone.Lifecycle/Hone.Lifecycle.csproj` | Added `InternalsVisibleTo` for test project | Required for testing internal `FindTargetProcesses` in DotnetStopHook |
| `docs/features/csharp-migration/phased-plan.md` | Updated ResolvedHook signature (`Uri? Url`, `string? HttpMethod`) and DispatchAsync signature | Type-safety improvements approved in Slice 3-1 review |

---

## Files Deleted

| File | Reason |
|------|--------|
| `src/Hone.Lifecycle/Placeholder.cs` | Replaced by real implementation |
| `tests/Hone.Lifecycle.Tests/PlaceholderTests.cs` | Replaced by real tests |

---

## Critic Review Summary

### Slice 3-1 — Foundation Contracts
**Critics:** design-conformance, correctness, parity, scope  
**Outcome:** `approve-with-doc-update`

All contracts conform to spec. Two type-safety improvements approved: `ResolvedHook.Url` as `Uri?` (not `string?`), `ResolvedHook.HttpMethod` (renamed from `Method`). Doc update applied to phased-plan.md.

### Slice 3-2 — HookResolver
**Critics:** design-conformance, correctness, parity, scope  
**Outcome:** `approve`

Static class pattern matches Phase 2 MetricComparer. Dropped `targetDir`/`harnessRoot` params (only needed for PS Script/Shared path resolution, unnecessary in C# BuiltIn model). 1 advisory (null Type defense-in-depth) noted, not blocking.

### Slice 3-3 — LifecycleHookDispatcher
**Critics:** design-conformance, correctness, parity, scope  
**Outcome:** `approve`

Sealed class with primary constructor DI. All 4 dispatch paths tested (BuiltIn, Command, Http, Skip). Fixture override handling deferred to orchestration layer (clean separation from PS's in-dispatcher approach).

### Slice 3-4 — DotnetBuildHook + DotnetTestHook
**Critics:** design-conformance, correctness, parity, scope  
**Outcome:** `approve`

DotnetTestHook uses `GeneratedRegex` for test count parsing. Both hooks save artifacts per PS parity. 1 advisory (PS returns extra debug fields not in HookResult) — accepted by design.

### Slice 3-5 — HealthPollHook
**Critics:** design-conformance, correctness, parity, scope, concurrency  
**Outcome:** `approve`

Polls health endpoint with Stopwatch deadline. Handles `HttpRequestException` and `TaskCanceledException`. 4 low advisories (URI composition edge case, etc.) noted, none blocking.

### Slice 3-6 — DotnetStartHook + DotnetStopHook
**Critics:** design-conformance, correctness, parity, scope, concurrency, reliability  
**Outcome:** `approve` (after fix)

**Blocking finding CONC-B1:** Process leak when CancellationToken fires during HealthPollHook.ExecuteAsync in start loop. **Fix:** Added try/catch around health poll to call TryKillProcess before re-throwing; added `process.Dispose()` in TryKillProcess finally block. Verified fix resolves both blocking findings.

DotnetStartHook uses `Process.Start` directly (not IProcessRunner) for long-lived background processes, dynamic port allocation via TcpListener. DotnetStopHook uses `Kill(entireProcessTree: true)` which mitigates missing child-process discovery from PS Win32_Process CIM approach.

### Slice 3-7 — K6RunHook
**Critics:** design-conformance, correctness, parity, scope  
**Outcome:** `approve`

Simple delegation to ILoadTestRunner. 1 accepted deviation: PS failure message includes exit code; C# omits it (abstracted away by ILoadTestRunner).

### Slice 3-8 — ConfigValidator
**Critics:** design-conformance, correctness, parity, scope, test-strategy  
**Outcome:** `approve`

Static validator with exact parity on all migrated rules. Justified deviations: tool availability checks (belong in orchestration startup), PS type checks (unnecessary in C#), target path validation (deferred with pragma). 27 test methods covering every validation rule.

---

## Approved Design Deviations

| Deviation | Rationale | Doc Updated |
|-----------|-----------|-------------|
| `HookType.BuiltIn` replaces PS `Script` + `Shared` | Plan §3.1 approved: C# has no script resolution distinction | No — plan already specifies |
| `Uri?` instead of `string?` for URLs | Type safety; consistent with HookResult.BaseUrl | ✅ Yes |
| `HttpMethod` renamed from `Method` | Clarity; `Method` is ambiguous | ✅ Yes |
| Static `HookResolver` (no DI) | Pure function, no state — matches Phase 2 MetricComparer pattern | No |
| Dropped `targetDir`/`harnessRoot` from HookResolver | Only needed for PS Script/Shared path resolution | No |
| `IBuiltInHookRegistry` abstraction added | Not in plan; enables testable hook lookup without coupling dispatcher to DI container | No |
| Fixture override deferred to orchestration | PS handles in dispatcher; C# separates concerns cleanly | No |
| DotnetStartHook uses `Process.Start` directly | Long-lived background process; IProcessRunner designed for fire-and-forget commands | No |
| DotnetStopHook simplified process discovery | `MainModule.FileName` + bin/ path matching replaces PS Win32_Process CIM + CommandLine parsing | No |
| `Kill(entireProcessTree: true)` in DotnetStopHook | Mitigates missing child-process discovery from PS approach | No |
| PS extra return fields omitted from HookResult | ExitCode, Output, TotalTests etc. are implementation details; HookResult is the contract | No |
| K6RunHook omits exit code from failure message | Exit code is abstracted away by ILoadTestRunner | No |
| ConfigValidator omits tool availability checks | Runtime environment checks belong in orchestration startup, not config validation | No |
| ConfigValidator omits PS type checks | Unnecessary in C# due to strong typing (int, bool, double are compile-time) | No |
| ConfigValidator target path validation deferred | `targetPath` param reserved with pragma; path existence checks will be added when target config loading is implemented | No |

---

## Validation Results

| Check | Result |
|-------|--------|
| `dotnet build Hone.slnx` — zero warnings | ✅ Pass |
| `dotnet test Hone.slnx` — 15 projects, 320 tests, 0 failures | ✅ Pass |
| All 87 Hone.Lifecycle.Tests pass | ✅ Pass |
| All 8 slices reviewed by critic coordinator | ✅ Pass (1 fix iteration on Slice 3-6) |
| No Phase 4+ work performed | ✅ Confirmed |

---

## Risks

- **DotnetStartHook process lifecycle** — The try/catch fix for cancellation-during-health-poll handles the known leak path, but complex process lifecycle code benefits from integration testing with real processes (not feasible in unit tests alone). Monitor during Phase 5+ integration.
- **DotnetStopHook MainModule access** — `Process.MainModule` can throw `Win32Exception` if the process has elevated privileges or is a system process. The current `try/catch` handles this gracefully, but should be validated against real target processes.
- **ConfigValidator tool availability** — Tool checks (dotnet, k6, git) are deferred to orchestration startup. This means invalid environments won't be caught at config-validation time. Phase 4+ should add a `ValidateEnvironment()` method or startup check.
- **TargetConfig path validation** — The `targetPath` parameter is reserved but unused. When target config file loading is implemented, path existence validation should be added.

---

## Recommended Next Phase

**Phase 4: Orchestration & Loop Engine**

- Implements `Hone.Orchestration` and `Hone.Agents.Loop` — the main experiment loop, iteration orchestration, fixture override handling, and agent coordination.
- Worker: `hone-migration-core` (primary), `hone-migration-loop-host` (loop-specific slices)
- Always-on critics plus likely `hone-csharp-concurrency-critic` (loop coordination), `hone-migration-reliability-critic` (error recovery), and `hone-csharp-maintainability-critic` (new abstractions).
