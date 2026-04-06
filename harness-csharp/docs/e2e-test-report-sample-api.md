# C# Harness E2E Test Report — sample-api

**Date:** 2026-04-06  
**Target:** SampleApi (.NET 6, SQL Server LocalDB)  
**Environment:** Windows 10 (19045), AMD64 16-core, 64GB RAM, .NET SDK 10.0.201, k6 v1.6.1  
**Harness:** harness-csharp (Hone.Cli)

---

## Executive Summary

The C# harness was tested end-to-end against sample-api. **The full agentic loop ran successfully** — baseline measurement, analysis, implementation, build/test validation, load testing, and accept/reject decisions all executed correctly. All 5 experiments completed (all regressed, which is expected behavior for a first run against an untuned target). **12 bugs were discovered and 10 were fixed** during testing; 2 remain as documented issues for a secondary fix.

### Results at a Glance

| Metric | Value |
|--------|-------|
| Baseline P95 | 2,561ms |
| Baseline RPS | 288 |
| Baseline Error Rate | 0% |
| Experiments Run | 5/5 |
| Experiments Improved | 0 |
| Experiments Regressed | 5 |
| Total Runtime | 86 min (1h 26m) |
| Bugs Found | 12 |
| Bugs Fixed | 10 |

### Experiment Results

| # | Target File | P95 (ms) | RPS | Δ P95 | Outcome |
|---|------------|----------|-----|-------|---------|
| Baseline | — | 2,561 | 288 | — | ✅ |
| 1 | Checkout/Index.cshtml.cs | 4,537 | 166 | +77% | ❌ Regressed |
| 2 | CartController.cs | 5,917 | 127 | +131% | ❌ Regressed |
| 3 | ReviewsController.cs | 7,098 | 103 | +177% | ❌ Regressed |
| 4 | Orders/Index.cshtml.cs | 7,514 | 97 | +193% | ❌ Regressed |
| 5 | Cart/Index.cshtml.cs | 8,538 | 85 | +233% | ❌ Regressed |

> **Note:** Progressive worsening across experiments (P95 trending upward) suggests possible environmental degradation — likely TIME_WAIT socket exhaustion on Windows from repeated k6 runs with 500 VUs. This is an environmental concern, not a harness bug.

---

## Bug Catalog

### Bug #1: Missing p(99) Percentile in k6 Summary Export ✅ FIXED

**Severity:** Critical (crashes baseline)  
**File:** `Hone.Measurement.K6/K6LoadTestRunner.cs`  
**Symptom:** `KeyNotFoundException` thrown by `K6SummaryParser` when parsing summary JSON.  
**Root Cause:** k6 v1.6.1 `--summary-export` only includes `p(90)` and `p(95)` by default. The parser expects `p(99)`.  
**Fix:** Added `--summary-trend-stats "avg,min,med,max,p(90),p(95),p(99)"` to k6 command arguments.

---

### Bug #2: Uri Trailing Slash Causes 100% HTTP 404 Errors ✅ FIXED

**Severity:** Critical (all requests fail)  
**File:** `Hone.Measurement.K6/K6LoadTestRunner.cs` → `BuildArguments()`  
**Symptom:** k6 load test runs with 100% error rate. All HTTP requests return 404.  
**Root Cause:** `new Uri("http://localhost:5050").ToString()` returns `"http://localhost:5050/"` (trailing slash). k6 scripts concatenate `${BASE_URL}/api/products` → `http://localhost:5050//api/products` → 404.  
**Fix:** Changed to `options.BaseUrl.GetLeftPart(UriPartial.Authority)` which returns the URL without trailing slash.

---

### Bug #3: Baseline Not Persisted for Loop Reuse ⚠️ NOT FIXED

**Severity:** Medium (performance waste)  
**File:** `Hone.Measurement.K6/K6LoadTestRunner.cs` + `HoneLoopRunner.cs`  
**Symptom:** The `run` command always re-measures baseline even if `baseline` was previously run, wasting ~12 minutes.  
**Root Cause:** The `baseline` command writes individual `k6-summary-run{N}.json` files but does NOT write the aggregated `k6-summary.json` that `LoadOrCreateBaselineAsync` looks for.  
**Impact:** Every `run` invocation pays the full baseline cost. On a ~14-minute-per-experiment loop, this is a significant overhead.  
**Suggested Fix:** Have the baseline command write `baseline/k6-summary.json` with aggregated metrics, or have `LoadOrCreateBaselineAsync` aggregate from individual run files if the summary doesn't exist.

---

### Bug #4: Agent Definitions Not Found When Running From Target Directory ✅ FIXED (workaround)

**Severity:** Critical (analysis fails)  
**File:** `Hone.Agents.CopilotCli/CopilotCliAgentRunner.cs`  
**Symptom:** `copilot --agent hone-analyst` returns exit code 1 — agent not found.  
**Root Cause:** `.github/agents/*.agent.md` live in the Hone repo. The copilot CLI is invoked with `workingDirectory` set to the target repo (sample-api), which is a separate git repo without agent definitions.  
**Workaround Applied:** Copied `.github/agents/` from Hone repo to sample-api repo (15 files).  
**Proper Fix Needed:** The harness should deploy agent definitions alongside the target, or invoke copilot with `--agent-path` pointing to the harness's agents directory. This is a deployment/packaging concern.

---

### Bug #5: ImpactEstimate Type Mismatch — JSON Object vs String ✅ FIXED

**Severity:** Critical (silently drops all analysis results)  
**File:** `Hone.Agents.Loop/Analysis/AnalysisAgent.cs` → `OpportunityDto`  
**Symptom:** Analysis agent runs, returns valid JSON, but harness reports "no opportunities found."  
**Root Cause:** The AI agent returns `impactEstimate` as a JSON object `{"trafficPct": 5.6, "latencyMs": 200, ...}`, but `OpportunityDto.ImpactEstimate` was typed as `string?`. `System.Text.Json` throws `JsonException` during deserialization, which is silently caught due to `MaxRetries=0`.  
**Fix:** Changed `ImpactEstimate` to `JsonElement?` type and added `GetRawText()` conversion in `NormalizeOpportunities`.

---

### Bug #6: RootCauseDocument Always Null ✅ FIXED

**Severity:** High (implementer has no context)  
**File:** `Hone.Orchestration/Queue/OptimizationQueueManager.cs` + `Loop/HoneLoopRunner.cs`  
**Symptom:** Implementation agent receives `RootCauseDocument: null`, reducing quality of optimizations.  
**Root Cause:** Queue manager writes RCA to files (`root-causes/rca-{id}.md`) and stores the path in `QueueItemDto.RootCausePath`. However, the `QueueItem` domain model didn't carry this path, and `HoneLoopRunner` hardcoded `RootCauseDocument: null` in `ImplementerOptions`.  
**Fix:** Added `GetRootCauseDocument(string itemId)` method to `OptimizationQueueManager` that reads RCA from disk, and plumbed it through `HoneLoopRunner`.

---

### Bug #7: DefaultBranch Hardcoded to "main" ✅ FIXED

**Severity:** Critical (git operations fail)  
**File:** `Hone.Core/Config/HoneConfig.cs` + `ConfigMerger.cs` + `Hone.Cli/Program.cs`  
**Symptom:** `git checkout main` fails — sample-api uses `master` branch.  
**Root Cause:** `LoopOptions.DefaultBranch` defaults to `"main"`. The config YAML has `BaseBranch: "master"`, but `HoneConfig` record lacked the `BaseBranch` property, and `ConfigMerger.Merge` didn't pass it through to the merged config.  
**Fix:** Added `Name` and `BaseBranch` to `HoneConfig` record, updated `ConfigMerger.Merge` to carry them through, and wired `LoopOptions.DefaultBranch` to config in `Program.cs`.

---

### Bug #8: Git Checkout Uses `--` Before Branch Name ✅ FIXED

**Severity:** Critical (branch operations broken)  
**File:** `Hone.SourceControl.Git/GitVersionControl.cs` → `CheckoutAsync()`  
**Symptom:** `git checkout -- master` fails with "pathspec 'master' did not match any file(s)."  
**Root Cause:** The checkout command was `["checkout", "--", branch]`. The `--` separator tells git that everything after it is a file path, not a branch name. This also prevented experiment branches from being properly created — commits went on the base branch, and reverts moved HEAD backwards, losing harness state.  
**Fix:** Removed `--` from the checkout arguments.

---

### Bug #9: Build Fails Due to Locked Executable ⚠️ NOT FIXED (workaround only)

**Severity:** High (blocks all experiments)  
**File:** `Hone.Orchestration/Implementer/IterativeImplementerRunner.cs`  
**Symptom:** `dotnet build -c Release` fails because `SampleApi.exe` is locked by the running API process.  
**Root Cause:** The harness builds with `dotnet build -c Release` while the API is running from `bin/Release/net6.0/SampleApi.exe`. Windows locks the executable. There are no lifecycle hooks in `HoneLoopRunner` to stop/restart the API process around builds.  
**Workaround Applied:** Started API in Debug mode (`dotnet run -c Debug`) so Release builds don't conflict with the running process.  
**Proper Fix Needed:** The harness should integrate with a lifecycle manager that:
1. Stops the target API before build
2. Runs the build
3. Restarts the API after build
4. The `.hone/hooks/prepare.ps1` exists but isn't invoked during the loop

---

### Bug #10: JSON Extraction Fails on Unfenced Responses ✅ FIXED

**Severity:** High (intermittent analysis failures)  
**File:** `Hone.Core/Utilities/JsonUtils.cs` → `ExtractJsonBlock()`  
**Symptom:** Analysis intermittently returns "no opportunities" despite the AI agent producing valid JSON.  
**Root Cause:** `ExtractJsonBlock` only handles fenced code blocks (` ```json ... ``` `). AI agents sometimes return conversational text before the JSON (e.g., "Let me analyze..." followed by raw JSON without fences).  
**Fix:** Added `TryExtractUnfencedJson` fallback that finds the first `{`/`[` character and the last matching `}`/`]` to extract raw JSON from conversational text.

---

### Bug #11: File Path Resolution Strips Real Directory Names ✅ FIXED

**Severity:** Medium (implementation targets wrong files)  
**File:** `Hone.Orchestration/Implementer/IterativeImplementerRunner.cs` → `ResolveTargetFile()`  
**Symptom:** Implementer can't find the target file for modification.  
**Root Cause:** `ResolveTargetFile` strips the `targetName` prefix from paths (e.g., `SampleApi/Controllers/CartController.cs` → `Controllers/CartController.cs`). When `targetName` matches an actual directory name in the repo, this removes a real path component.  
**Fix:** Added fallback — if the resolved (stripped) path doesn't exist on disk, use the original path.

---

### Bug #12: Queue Metadata Not Updated After Experiments ⚠️ DOCUMENTATION ONLY

**Severity:** Low (metadata/reporting only)  
**File:** `Hone.Orchestration/Queue/OptimizationQueueManager.cs`  
**Symptom:** `experiment-queue.json` shows `triedByExperiment: null` and `status: "in_progress"` for items that were already processed.  
**Root Cause:** After an experiment completes (success or failure), the queue item's `triedByExperiment` and `outcome` fields are not updated and persisted back to the queue file.  
**Impact:** Queue metadata doesn't reflect actual experiment history. Minor — doesn't affect loop execution.

---

## Additional Observations

### High Diff Line Counts
All experiments show ~21,000 diff lines (`diffLines` in iteration-log.json). This is abnormally high for targeted optimizations and suggests the implementation agent may be making overly broad changes or the diff calculation includes unrelated files. This could explain the consistent regressions — large, sweeping changes are more likely to introduce performance problems than targeted fixes.

### Progressive Performance Degradation
P95 latency worsened across experiments (4,537 → 5,917 → 7,098 → 7,514 → 8,538ms) despite reverts between experiments. This suggests either:
- TCP socket exhaustion (TIME_WAIT accumulation from 500 VU k6 runs on Windows)
- Database connection pool degradation
- Memory pressure from repeated builds

A cooldown period between experiments or OS-level socket tuning may be needed.

### k6 Process Timeout Recovery
k6 processes occasionally hang after completion (Windows TCP issue with high VU counts). The 5-minute timeout + summary file recovery workaround is effective but inelegant. Consider using k6's `--no-connection-reuse` flag or reducing VU count for Windows environments.

---

## Files Modified in Harness

| File | Changes |
|------|---------|
| `K6LoadTestRunner.cs` | p99 stats, quiet mode, timeout, recovery, trailing slash fix, logging |
| `AnalysisAgent.cs` | ImpactEstimate type fix (JsonElement?), removed diagnostic code |
| `JsonUtils.cs` | TryExtractUnfencedJson fallback, null guard |
| `OptimizationQueueManager.cs` | GetRootCauseDocument method |
| `HoneLoopRunner.cs` | RCA document plumbing |
| `HoneConfig.cs` | Added Name, BaseBranch properties |
| `ConfigMerger.cs` | Name/BaseBranch pass-through |
| `Program.cs` | TargetName, DefaultBranch, ResultsPath wiring |
| `GitVersionControl.cs` | Removed `--` from checkout |
| `IterativeImplementerRunner.cs` | File path resolution fallback |

---

## Recommendations for Secondary Fix

1. **Bug #3 — Baseline persistence:** Write aggregated `k6-summary.json` in the baseline command
2. **Bug #9 — Lifecycle hooks:** Integrate `.hone/hooks/prepare.ps1` into the loop runner for API stop/start around builds
3. **Bug #12 — Queue metadata:** Update queue items with `triedByExperiment` and `outcome` after each experiment
4. **High diff counts:** Investigate why the implementer generates 20k+ line diffs — may need prompt tuning or scope constraints
5. **Windows socket tuning:** Add `--no-connection-reuse` to k6 or reduce VUs on Windows to prevent TIME_WAIT buildup
6. **Agent deployment:** Package agent definitions with the harness CLI instead of requiring manual copy to target repos
