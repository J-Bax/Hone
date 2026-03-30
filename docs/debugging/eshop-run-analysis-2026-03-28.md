# eShopOnWeb Run Analysis — 2026-03-28

## Summary

Hone ran 5 optimization experiments against eShopOnWeb-Honed (forked from
MicrosoftLearning/eShopOnWeb). **All 5 failed.** The harness itself executed
correctly — build, measure, detect, revert all worked. The failures are in
five systemic gaps between what Hone assumes about targets and what eShopOnWeb
actually is.

| Exp | Optimization | Outcome | Category |
|-----|-------------|---------|----------|
| 0 | Baseline | ⚠️ 99.75% error rate, p95=12.7 s | Broken baseline |
| 1 | DbContext pooling (`Dependencies.cs`) | ❌ Build failed — 7 errors | Code-gen gap |
| 2 | ThreadPool pre-warming (`Program.cs`) | ❌ Build failed — 1 error | Code-gen gap |
| 3 | In-memory caching (`CatalogBrandListEndpoint.cs`) | ❌ Build failed — 6 errors | Code-gen gap |
| 4 | Sync → async (`EfRepository.cs`) | ❌ Regressed +63 % p95 | Over-aggressive fix |
| 5 | DbContext model cache (`CatalogContext.cs`) | ❌ No code block returned | Agent failure |

---

## Gap 1 — Baseline Is Broken (99.75 % Error Rate)  🔴 CRITICAL

### What happens

The baseline measurement itself shows 99.75 % errors, p95 = 12 703 ms,
RPS = 4.2. Every subsequent analysis and fix attempt reasons about a
fundamentally broken system, not a "slow but working" one.

### Root cause

`prepare.ps1` drops `CatalogDb` via `sqlcmd`. Before the first run the API is
not yet started, so the drop succeeds cleanly. But between measured runs 1–5
(inside `Invoke-ScaleTests.ps1`) the harness calls
`Invoke-LifecycleHook -Name 'Prepare'` while the API **is** running:

1. EF Core's cached `DbContext` pool holds connections to a now-deleted DB.
2. 50 concurrent VUs hit the missing DB simultaneously.
3. Re-seeding under load creates massive contention (88.9 % GC pause ratio).
4. Runs 2–5 produce p95 of 13–42 s with ~99 % errors.

### Evidence

- `docs/features/generalizingphase1/implementationlearnings.md` Lesson #15
  documents this exact issue.
- `results/experiment-0/` shows run-1 at 0 ms p95 (no traffic yet), runs 3–5
  at 21–42 s p95 with 99 %+ errors.
- `results/baseline-counters.json`: GC pause ratio 99 %, 0.52 % CPU,
  156 MB heap — the app is not CPU-bound, it is waiting on DB.

### Why sample-api works

sample-api uses `EnsureCreated()` + `SeedData.Initialize()` on every startup,
so the DB auto-recreates gracefully after a drop.

### Fix options

| Option | Pros | Cons |
|--------|------|------|
| Stop → Prepare → Start → Ready cycle between runs | Correct, no target changes | Adds ~2 min per run |
| Add `/diag/reset` endpoint to eShopOnWeb | Fast, in-process | Requires target-side code |
| Make baseline k6 scenario fully idempotent | Avoids needing reset entirely | Limits what baseline can measure |

---

## Gap 2 — Fix Agent Lacks Cross-Project Context  🔴 CRITICAL

### What happens

Experiments 1–3 all failed with compiler errors because the AI generated code
that was syntactically valid in isolation but broke cross-project references.

| Exp | File | Error Pattern |
|-----|------|---------------|
| 1 | `src/Infrastructure/Dependencies.cs` | Missing `using` for 6 ApplicationCore types + `IConfiguration.Get` |
| 2 | `src/PublicApi/Program.cs` | Introduced `using SampleApi.Data` (wrong project namespace) |
| 3 | `src/PublicApi/CatalogBrandEndpoints/CatalogBrandListEndpoint.cs` | Removed `using System.Threading` → broke `Task<>`, `CancellationToken` |

### Root cause

`Invoke-FixAgent.ps1` passes only the **single target file** to the fix agent.
For the sample-api (~15 files, single project), one file usually contains
enough context. For eShopOnWeb (multi-project solution, 3+ assemblies,
cross-project references), the agent cannot see the types it must preserve.

`Build-AnalysisContext.ps1` *does* collect source files from `SourceCodePaths`
for the analysis agent, but the fix agent only receives:
- The single file being modified
- The analysis suggestion text

It does **not** receive neighboring files, project references, or the full set
of `using` statements from the original file.

### Fix options

1. Include related files in fix prompt — parse `using` statements from the
   target file to identify dependencies; include at least files from the same
   directory and referenced types.
2. Always include the **full original file** as "preserve this structure"
   context alongside the suggested change.
3. Implement the iterative fixer (see Gap 3) so build errors get fed back.

---

## Gap 3 — No Iterative Fix Loop  🟡 HIGH

### What happens

When the fix agent generates code that does not compile, the experiment is
immediately marked `build_failure`. The harness reverts the code, creates a
rejected PR, and picks the next queue item. **The fix agent never sees the
compiler errors it caused.**

### Impact

3 of 5 experiments (60 %) failed at build. The underlying optimizations were
all sound — the analysis agent correctly identified real problems. Only the
code generation was broken. An iterative fixer could have corrected these.

### Evidence

A spec already exists in `docs/future-extensions.md` ("Iterative Fixer"):
- On build/test failure, feed error output back to fixer
- Fixer generates corrected version
- Continue until fix passes or retry budget exhausted
- Prevent scope creep through diff-size monitoring
- Never modify test files

### Recommendation

Implement the iterative fixer with max 3 retries. Feed: (1) original file,
(2) failed attempt, (3) compiler error output. Monitor diff size per retry.

---

## Gap 4 — eShopOnWeb Missing Diagnostic Endpoints  🟡 MEDIUM

### What happens

The `.hone/config.psd1` declares:

```powershell
GcEndpoint = '/diag/gc'
Cooldown   = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
```

eShopOnWeb's PublicApi does not ship with a `/diag/gc` endpoint. The sample-api
has this built in. If the endpoint returns 404, the cooldown hook silently
fails and GC pressure accumulates across runs.

### Fix

Add a minimal `/diag/gc` endpoint to eShopOnWeb's PublicApi:

```csharp
app.MapPost("/diag/gc", () =>
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    return Results.Ok();
});
```

Also add `/health` if not already present, and have `Test-HoneConfig.ps1`
validate that declared endpoints are reachable during config validation.

---

## Gap 5 — Agent Hallucinates sample-api Patterns  🟡 MEDIUM

### What happens

Experiment 2's fix introduced `using SampleApi.Data` — a namespace that exists
in the sample-api, not in eShopOnWeb. The fix agent was primed on sample-api
patterns and leaked them into eShopOnWeb code.

### Evidence

Build log for experiment 2:
```
CS0246: The type or namespace name 'SampleApi' could not be found
```

### Root cause

Agent prompts may include sample-api examples, or the agent's context window
still contains sample-api code from prior analysis. The fix agent does not
receive enough eShopOnWeb-specific context (project namespace, assembly names)
to generate correct references.

### Fix options

1. Include the target project's root namespace in the fix prompt (extractable
   from `.csproj` `<RootNamespace>` or first `namespace` declaration in files).
2. Add explicit negative constraint: "You are fixing code in the
   **{targetName}** project. Do NOT reference SampleApi or any namespace that
   does not appear in the provided source files."
3. Post-generation validation: scan the generated code for `using` statements
   and reject any that reference namespaces not found in `SourceCodePaths`.

---

## Experiment-by-Experiment Detail

### Experiment 1 — DbContext Pooling (BUILD FAILED)

**Analysis:** 42.1 % CPU in JIT compilation, 68.9 % in initialization overhead.
Agent correctly identified `AddDbContext<>()` → `AddDbContextPool<>()` as the
fix.

**Fix attempt:** Replaced service registration block in `Dependencies.cs` with
`AddDbContextPool(…, poolSize: 128)`. The replacement dropped all
`using` statements for ApplicationCore types.

**Errors:**
```
CS0246: 'OrderService' could not be found
CS0246: 'ICatalogItemViewModelService' could not be found
CS0246: 'CachedCatalogItemViewModelService' could not be found
CS0246: 'ICatalogService' could not be found
CS0246: 'CatalogService' could not be found
CS0246: 'UriComposer' could not be found
CS1061: 'IConfiguration' does not contain a definition for 'Get'
```

### Experiment 2 — ThreadPool Pre-warming (BUILD FAILED)

**Analysis:** 88.9 % GC pause ratio, 20–28 s GC pauses, thread pool starvation
under burst load.

**Fix attempt:** Add `ThreadPool.SetMinThreads(100, 100)` to top of
`Program.cs`. The code generation corrupted the file's `using` block,
introducing `using SampleApi.Data`.

**Errors:**
```
CS0246: The type or namespace name 'SampleApi' could not be found
```

### Experiment 3 — In-Memory Caching (BUILD FAILED)

**Analysis:** 28.6 % of traffic to `catalog-brands` and `catalog-types`
endpoints returns static data.

**Fix attempt:** Inject `IMemoryCache` with 60 s TTL for brands/types. The
rewrite removed `using System;` and `using System.Threading;`, broke the
`HandleAsync` override signature.

**Errors:**
```
CS0234: 'CatalogAggregate' does not exist in namespace '…Entities'
CS0534: Does not implement inherited abstract member 'HandleAsync(…)'
CS0246: 'Task<>' could not be found
CS0246: 'CancellationToken' could not be found
CS0246: 'CatalogBrand' could not be found (×2)
```

### Experiment 4 — Sync → Async (REGRESSED)

**Analysis:** 86.2 % GC pause ratio, `EfRepository<T>` using synchronous EF
Core calls (`.Result`, `SaveChanges()`) causing thread pool starvation.

**Build:** ✅ Succeeded (18.9 s).

**Performance:**

| Metric | Baseline | Exp 4 | Delta |
|--------|----------|-------|-------|
| p95 | 12 703 ms | 20 709 ms | +63 % ❌ |
| RPS | 4.2 | 2.5 | −41 % ❌ |
| Error rate | 99.75 % | 99.64 % | −0.11 % ✓ |
| Run variance | — | CV = 50 % | Unreliable |

**Hypothesis:** The async conversion was too aggressive — converting short
in-memory lookups to async introduced more Task allocation and context-switch
overhead than the thread starvation it tried to fix.

### Experiment 5 — DbContext Model Cache (FIX FAILED)

**Analysis:** 30.6 % CPU in JIT, per-request DbContext model recompilation.

**Fix attempt:** Agent response did not contain a code block. The harness
recorded `Fix agent response did not contain a code block` and terminated
after reaching `max_experiments = 5`.

---

## Recommended Fix Order

```
Priority 1 (fix the baseline — nothing else matters if measurements are garbage):
  ├── T1: Fix between-run DB reset (Stop→Prepare→Start→Ready cycle)
  └── T2: Make baseline k6 scenarios idempotent

Priority 2 (fix code generation quality):
  ├── T3: Enrich fix agent with cross-project context
  └── T4: Prevent sample-api pattern leakage in prompts

Priority 3 (safety net for remaining failures):
  └── T5: Implement iterative fixer with build error feedback

Priority 4 (nice-to-have):
  └── T6: Add /diag/gc and /health endpoints to eShopOnWeb
```

---

## Machine & Environment Context

| Field | Value |
|-------|-------|
| Machine | AMD Ryzen 7 3700X, 8-core, 16 logical, 63.9 GB RAM |
| OS | Windows 10 |
| .NET SDK | 8.0.419, CLR 8.0.21 |
| PowerShell | 7.4.13 |
| Loop duration | 2026-03-23 22:58 → 2026-03-24 00:18 (1 h 20 min) |
| Experiments | 5 (1 baseline + 5 attempts) |
