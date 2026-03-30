# Hone Run Analysis — March 10, 2026

## Overview

21 experiments over ~9.2 hours on AMD Ryzen 7 3700X (16 threads, 64GB RAM).
10 PRs created (#22–#31) in a stacked-diffs chain on Hone-SampleAPI.

## Performance Results

| Metric | Baseline | Best (exp-19) | Improvement |
|--------|----------|---------------|-------------|
| p95 latency | 888.5ms | 403.2ms | **54.6% reduction** |
| RPS | 683 | 1,369 | **2.0x throughput** |
| Error rate | 0% | 0% | Maintained |

## Experiment Outcomes

| Outcome | Count | Notes |
|---------|-------|-------|
| ✅ Improved | 10 | Accepted, PR created |
| ⚪ Stale | 7 | No measurable impact |
| ❌ Regressed | 1 | exp-20 (Orders page) |
| 🔴 Test failure | 2 | exp-13, exp-14 |
| ⏭️ Skipped | 1 | exp-21 (classification agent error) |

**Success rate: 48% (10/21)**

## Performance Trajectory

```
p95 (ms)  RPS
888.5    683.2  ── Baseline
644.6    859.0  ── exp-1:  ProductsController server-side queries      (-27%)
583.0    978.6  ── exp-2:  ReviewsController server-side filtering     (-9.6%)
566.4    983.6  ── exp-4:  OrdersController N+1 fix                    (-2.8%)
499.2  1,112.3  ── exp-5:  Product Detail full scan elimination        (-11.9%)
422.1  1,308.8  ── exp-6:  Home page full scan elimination             (-15.4%)
409.7  1,335.7  ── exp-7:  Products page server-side pagination        (-2.9%)
408.6  1,345.6  ── exp-8:  Database indexes on filtered columns        (-0.3%)
407.8  1,359.9  ── exp-12: ReviewsController aggregation combine       (-0.2%)
403.2  1,365.2  ── exp-16: Home page ORDER BY NEWID() replacement      (-1.1%)
403.8  1,369.2  ── exp-19: Cart page N+1 and full scan fix             (+0.2%)
```

Two clear phases:

- **Phase 1 (exp 1–7):** Rapid gains from fixing the biggest full-table-scan antipatterns. Each fix yielded 3–27% p95 improvement.
- **Phase 2 (exp 8–19):** Diminishing returns. Gains <1% per experiment. The harness correctly kept searching but hit a plateau.

## What Went Well

### 1. The harness ran autonomously for 9+ hours

From baseline through 21 experiments with no human intervention. It correctly built, tested, measured, analyzed, fixed, and published PRs in a fully automated loop.

### 2. Stacked diffs worked cleanly

10 PRs chained sequentially (#22→#31), each building on the previous success. Failed experiments were correctly reverted and the chain continued from the last good state.

### 3. Excellent early optimization discovery

The agent correctly identified the dominant antipattern (full table scans + client-side filtering) and systematically eliminated it across all controllers and Razor pages. The first 7 experiments captured 95% of the total improvement.

### 4. Diagnostic profiling drove later discoveries

When the analysis queue was empty, the harness triggered PerfView CPU+GC collection and used cpu-hotspots + memory-gc analyzers to find new optimization targets (experiments 11→12, 16, 19).

### 5. Regression detection works

Experiment 20 caused a 12.8% p95 regression and 15.5% RPS drop. The harness correctly detected it, reverted the code, pushed the branch for the record, and continued.

### 6. Test gate caught breaking changes

Experiments 13 (DbContext pooling) and 14 (result limiting) broke E2E tests and were correctly rejected before any performance measurement ran.

## What Didn't Go Well

### 1. Experiment 20 regression — Orders page fix backfired

The Orders page optimization (RCA-2) caused p95 to jump from 403ms to 455ms. Under stress load, data transfer ballooned to 136GB vs 55KB in successful experiments. The agent's fix likely introduced a query that scales poorly under concurrent load. The harness correctly rejected it, but it wasted ~23 minutes.

**Root issue:** The fixer agent doesn't simulate concurrency. A fix that looks correct for a single request can collapse under 200 concurrent VUs.

### 2. Experiment 13 — DbContext pooling violated DI constraints

The agent tried to enable `AddDbContextPool<>()` but this requires singleton lifetime, conflicting with scoped `DbContextOptions`. This is a well-known .NET DI constraint the agent should have known about.

**Root issue:** The fixer agent lacks awareness of framework-level constraints. The classifier correctly would have flagged this as "architecture" scope, but the classification happened after the fix was generated.

### 3. Experiment 14 — Result limiting broke API contract

Adding `.Take(50)` to product queries was a valid optimization but broke tests that expected all 1,000 products returned. The harness rightly rejected this, but the agent should have anticipated the contract violation.

**Root issue:** The fixer agent doesn't read or consider test expectations when generating fixes.

### 4. Stale streak (experiments 9–11) — diminishing returns detection

Three consecutive stale experiments trying micro-optimizations (AsNoTracking on small tables, Categories endpoint, random sampling). The harness detected the streak and triggered diagnostic profiling, but the 3 wasted experiments took ~87 minutes.

**Root issue:** After the big wins, the analysis agent kept proposing leaf-node optimizations without sufficient impact modeling. The threshold (`MinImprovementPct: 0%`) is too permissive — any measurable improvement counts, but measurement noise at this scale is ±1%.

### 5. Experiment 21 skipped due to classification agent error

The classification agent returned invalid JSON (`NaN`), defaulting to "architecture" scope, which caused the experiment to be skipped. The Checkout page fix was never attempted.

**Root issue:** Agent output parsing is fragile. The `NaN` in JSON is a known issue when the agent includes JavaScript-style NaN literals.

### 6. Duplicate optimization attempts

Experiments 3 and 15 both tried to fix CartController with the same approach — both stale. Experiment 11 (stale) and 16 (improved) both targeted ORDER BY NEWID() on the same file. The agent didn't learn from prior failures.

**Root issue:** The analysis agent is stateless between experiments. It doesn't have access to the experiment log to see what was already tried and failed.

### 7. Missing experiments 13–14 from run-metadata

The run-metadata.json jumps from experiment 12 to experiment 15, omitting the test-failure experiments. This makes the metadata incomplete for post-run analysis.

## Timing Analysis

| Phase | Avg Duration | Notes |
|-------|-------------|-------|
| Fast experiments (no diagnostics) | ~23 min | Build + test + 5×k6 + analysis + fix |
| Diagnostic experiments | ~37 min | Add PerfView collection + export + analysis |
| Test failures | <1 min | Fast-fail before measurement |

The 5 measured k6 runs per experiment (with cooldown) account for ~15 minutes of each experiment. This is the dominant time cost.

## Full Experiment Log

| Exp | Duration | Target File | Optimization | Outcome |
|-----|----------|-------------|--------------|---------|
| 0 | — | — | Baseline measurement | baseline |
| 1 | 34 min | `ProductsController.cs` | Server-side queries + AsNoTracking | ✅ improved |
| 2 | 23 min | `ReviewsController.cs` | Server-side filtering + SQL aggregation | ✅ improved |
| 3 | 23 min | `CartController.cs` | Fix N+1 and full table scans | ⚪ stale |
| 4 | 37 min | `OrdersController.cs` | Fix full table scans and N+1 | ✅ improved |
| 5 | 36 min | `Products/Detail.cshtml.cs` | Eliminate full table scans | ✅ improved |
| 6 | 23 min | `Pages/Index.cshtml.cs` | Eliminate full table scans in Home page | ✅ improved |
| 7 | 23 min | `Products/Index.cshtml.cs` | Server-side pagination and filtering | ✅ improved |
| 8 | 42 min | `Data/AppDbContext.cs` | Add database indexes on filtered columns | ✅ improved |
| 9 | 23 min | `ReviewsController.cs` | Eliminate redundant tracked product queries | ⚪ stale |
| 10 | 23 min | `CategoriesController.cs` | AsNoTracking + optimize GetCategory query | ⚪ stale |
| 11 | 41 min | `Pages/Index.cshtml.cs` | Replace ORDER BY NEWID() with sampling | ⚪ stale |
| 12 | 23 min | `ReviewsController.cs` | Combine GetAverageRating aggregation | ✅ improved |
| 13 | <1 min | `Program.cs` | Enable DbContext pooling | 🔴 test failure |
| 14 | 20 min | `ProductsController.cs` | Add server-side result limiting | 🔴 test failure |
| 15 | 35 min | `CartController.cs` | Fix full table scans and N+1 (retry) | ⚪ stale |
| 16 | 23 min | `Pages/Index.cshtml.cs` | Replace ORDER BY NEWID() (retry) | ✅ improved |
| 17 | 43 min | `ProductsController.cs` | Convert GetProduct to AsNoTracking | ⚪ stale |
| 18 | 23 min | `Program.cs` | Enable response compression middleware | ⚪ stale |
| 19 | 34 min | `Pages/Cart/Index.cshtml.cs` | Fix Cart page N+1 and full table scans | ✅ improved |
| 20 | 23 min | `Pages/Orders/Index.cshtml.cs` | Fix Orders page N+1 and full scans | ❌ regressed |
| 21 | <1 min | `Pages/Checkout/Index.cshtml.cs` | Checkout page N+1 and per-item saves | ⏭️ skipped |

## Recommendations

### High Priority

1. **Feed experiment history to the analysis agent** — Prevent duplicate attempts (exp-3/15, exp-11/16) and help the agent reason about what has already been tried. Pass the experiment-log.md as context to the analyst prompt.

2. **Fix classification agent NaN handling** — Parse agent output more defensively; replace `NaN`/`Infinity` literals before JSON deserialization. Retry on invalid JSON instead of defaulting to "architecture" scope.

3. **Include test-failure experiments in run-metadata** — Experiments 13–14 are missing from run-metadata.json. They're part of the history and useful for post-run analysis.

### Medium Priority

4. **Have the fixer agent read relevant test files** — This would prevent contract-breaking changes like exp-14 (`.Take(50)` breaking tests that expect all 1,000 products). Include test file paths in the fixer prompt context.

5. **Add concurrency-aware validation** — Run a quick stress smoke test (single run, 30s) before the full 5-run evaluation to catch regressions like exp-20 earlier and save ~15 minutes on doomed experiments.

6. **Raise `MinImprovementPct` to 1–2%** — At the plateau stage, measurement noise is ±1%. A 0% threshold accepts noise as improvement and rejects noise as stale, leading to random outcomes.

### Low Priority

7. **Run classification before fix generation** — Experiment 13 would have been caught as an architecture-scope change before the fixer agent spent time generating code that violated DI constraints.

8. **Add impact estimation to the analyst** — Have the analyst estimate what percentage of total request volume an optimization affects, to avoid proposing changes to low-traffic endpoints (like Categories in exp-10).
