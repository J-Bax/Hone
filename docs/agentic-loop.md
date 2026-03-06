# The Agentic Loop

## Overview

The Hone agentic loop is a fully automated optimization cycle. It runs as a single PowerShell invocation (`Invoke-HoneLoop.ps1`) and proceeds through a fixed sequence of phases on each iteration, making decisions based on test results and performance metrics.

## Loop Lifecycle

The loop supports two modes: **stacked diffs** (default) and **legacy**. In stacked mode, iterations form a linear chain where each branches from the previous. In legacy mode, each iteration branches from `master`.

### Stacked Diffs Mode (default)

```
START
  │
  ▼
Load config.psd1
  │
  ▼
currentBranch = master
  │
  ├───────────────────────────────────────────────────────┐
  ▼                                                       │
┌─── PHASE 1: ANALYZE ─────────────────────────────────┐  │
│ Build context prompt, call copilot CLI               │  │
│ Classify scope (NARROW vs ARCHITECTURE)              │  │
│ ► Architecture → queue for manual review, continue   │  │
└───────────────┬──────────────────────────────────────┘  │
                ▼                                         │
┌─── PHASE 2: FIX ────────────────────────────────────┐  │
│ Generate fix, create branch from currentBranch       │  │
│ Apply code change, commit                            │  │
└───────────────┬──────────────────────────────────────┘  │
                ▼                                         │
┌─── PHASE 3: BUILD ──────────────────────────────────┐  │
│ dotnet build                                         │  │
│ ► Fail → revert code, push branch, continue          │  │
└───────────────┬──────────────────────────────────────┘  │
                ▼                                         │
┌─── PHASE 4: VERIFY ────────────────────────────────┐   │
│ dotnet test (E2E)                                    │  │
│ ► Fail → revert code, push branch, continue          │  │
└───────────────┬──────────────────────────────────────┘  │
                ▼                                         │
┌─── PHASE 5: MEASURE ───────────────────────────────┐   │
│ Start API → k6 run → Stop API                        │  │
│ Parse JSON results                                   │  │
└───────────────┬──────────────────────────────────────┘  │
                ▼                                         │
┌─── PHASE 6: COMPARE ───────────────────────────────┐   │
│ Compare vs. baseline / previous iteration            │  │
└───────────────┬──────────────────────────────────────┘  │
                ▼                                         │
┌─── PHASE 7: PUBLISH or REVERT ─────────────────────┐   │
│ ► Improved → push, create stacked PR, continue       │  │
│ ► Regression → revert code, push branch, continue    │  │
│ ► Stale → revert code, push branch, continue         │  │
│ ► MaxConsecutiveFailures → EXIT                      │  │
└───────────────┬──────────────────────────────────────┘  │
                │                                         │
                ▼                                         │
          currentBranch = this iteration                   │
          Iteration++                                     │
                │                                         │
                └─────────────────────────────────────────┘
```

#### Branch Chain Example

```
master
  └── hone/iteration-1  (improved ✓ → PR #12, base=master)
        └── hone/iteration-2  (regressed ✗ → code reverted, pushed)
              └── hone/iteration-3  (stale ✗ → code reverted, pushed)
                    └── hone/iteration-4  (improved ✓ → PR #15, base=iteration-1)
                          └── hone/iteration-5  (improved ✓ → PR #18, base=iteration-4)
```

PRs only show the incremental change between successful iterations. Failed iterations' reverted code is invisible in the diff. Each PR's `--base` points to the last successful iteration branch.

### Legacy Mode

Set `StackedDiffs = $false` in config to use legacy mode, which preserves the original behavior:

```
START
  │
  ▼
Load config.psd1
  │
  ▼
Iteration = 1
  │
  ├─────────────────────────────────────┐
  ▼                                     │
┌─── PHASE 1: BUILD ────────────────┐   │
│ dotnet build                      │   │
│ ► Fail → ABORT with build error   │   │
└───────────────┬───────────────────┘   │
                ▼                       │
┌─── PHASE 2: VERIFY ──────────────┐   │
│ dotnet test (E2E)                 │   │
│ ► Fail → ROLLBACK to previous    │   │
│          branch, ABORT            │   │
└───────────────┬───────────────────┘   │
                ▼                       │
┌─── PHASE 3: MEASURE ─────────────┐   │
│ Start API process                 │   │
│ k6 run (selected scenario)       │   │
│ Stop API process                  │   │
│ Parse JSON results                │   │
└───────────────┬───────────────────┘   │
                ▼                       │
┌─── PHASE 4: COMPARE ─────────────┐   │
│ Compare vs. baseline/thresholds   │   │
│ ► Targets met → EXIT (success)    │   │
│ ► Regression  → ROLLBACK, ABORT   │   │
│ ► Max iterations → EXIT (limit)   │   │
└───────────────┬───────────────────┘   │
                ▼                       │
┌─── PHASE 5: ANALYZE ─────────────┐   │
│ Build prompt with:                │   │
│   - Current metrics               │   │
│   - Baseline metrics              │   │
│   - Delta / % change              │   │
│   - Source code of hot paths      │   │
│ Send to: copilot CLI               │   │
│ Parse response                    │   │
└───────────────┬───────────────────┘   │
                ▼                       │
┌─── PHASE 6: FIX ─────────────────┐   │
│ Create git branch                 │   │
│   hone/iteration-{N}          │   │
│ Apply suggested code changes      │   │
│ Commit changes                    │   │
└───────────────┬───────────────────┘   │
                │                       │
                ▼                       │
          Iteration++                   │
                │                       │
                └───────────────────────┘
```

## Phase Details

### Phase 1: Build

**Script**: `Build-SampleApi.ps1`

Runs `dotnet build` on the target project. A failed build is an immediate abort — the harness does not attempt to fix build errors (that would require a different kind of agent).

**Inputs**: Project path from `config.psd1`
**Outputs**: Build success/failure, build output log
**Exit condition**: Build failure → abort loop

### Phase 2: Verify (E2E Tests)

**Script**: `Invoke-E2ETests.ps1`

Runs the xUnit E2E test suite via `dotnet test`. These tests use `WebApplicationFactory`, so they don't need the API to be running externally. This is the **regression gate** — if any applied optimization breaks functionality, the loop detects it here.

**Inputs**: Test project path from `config.psd1`
**Outputs**: Test pass/fail count, TRX results file
**Exit condition**: Any test failure → rollback branch, abort loop

### Phase 3: Measure (Scale Tests)

**Script**: `Invoke-ScaleTests.ps1` (calls `Start-SampleApi.ps1` and `Stop-SampleApi.ps1`)

Starts the API as a background process, waits for a health check to pass, then executes the configured k6 scenario. After the test completes, stops the API and parses the JSON summary.

**Inputs**: k6 scenario path, API URL, thresholds from `config.psd1`
**Outputs**: Structured performance object:

```powershell
@{
    Timestamp       = [datetime]
    Iteration       = [int]
    HttpReqDuration = @{
        Avg  = [double]  # ms
        P50  = [double]
        P90  = [double]
        P95  = [double]
        P99  = [double]
        Max  = [double]
    }
    HttpReqs        = @{
        Count = [int]
        Rate  = [double]  # requests/sec
    }
    HttpReqFailed   = @{
        Count = [int]
        Rate  = [double]  # error percentage
    }
}
```

### Phase 4: Compare

**Script**: `Compare-Results.ps1`

Compares the current iteration's metrics against the baseline (from `Get-PerformanceBaseline.ps1`) and the configured thresholds.

**Decisions made**:

| Condition | Stacked Mode | Legacy Mode |
|-----------|-------------|-------------|
| All thresholds met | Exit loop — success | Exit loop — success |
| P95 latency increased > 10% | Revert code, push branch, continue | Rollback branch, abort |
| Error rate exceeds threshold | Revert code, push branch, continue | Rollback branch, abort |
| No performance change but CPU or working set reduced ≥ 5% | Accept (efficiency tiebreaker) — reset failure count | Accept (efficiency tiebreaker) — reset stale count |
| MaxConsecutiveFailures reached | Exit loop — max failures | N/A |
| StaleIterationsBeforeStop reached | N/A | Exit loop — no improvement |
| Iteration count = max iterations | Exit loop — limit reached | Exit loop — limit reached |
| Otherwise | Continue to next iteration | Continue to Analyze phase |

> **Efficiency tiebreaker**: When performance metrics (p95, RPS, error rate) are flat — no
> improvement and no regression — the loop checks OS-level resource usage. If average CPU
> or peak working set decreased beyond the configured threshold (default 5%), the iteration
> is accepted and the stale counter resets. This prevents the loop from stopping prematurely
> when there are genuine efficiency gains to pursue. Only CPU and working set are evaluated
> because these are the resources that matter on a shared-VM architecture.

### Phase 5: Analyze (Copilot)

**Script**: `Invoke-AnalysisAgent.ps1`

Constructs a detailed prompt for GitHub Copilot CLI that includes:

1. **Current performance metrics** — p95 latency, RPS, error rate
2. **Baseline comparison** — how far from targets
3. **Performance delta** — improvement or regression vs. previous iteration
4. **Source code context** — relevant controller/query code
5. **Optimization history** — what was already tried in previous iterations

The prompt asks Copilot to suggest a specific, targeted code change to improve performance. The response is parsed and prepared for the Fix phase.

### Phase 6: Fix (Apply Changes)

**Script**: `Apply-Suggestion.ps1`

1. Creates a new git branch: `hone/iteration-{N}` (from `currentBranch` in stacked mode, or `master` in legacy mode)
2. Applies the code changes suggested by Copilot
3. Commits with a descriptive message including the iteration number and targeted metric

The loop then increments the iteration counter and returns to Phase 1 (Build).

### Phase 7: Publish or Revert (Stacked Mode)

**Script**: `Revert-IterationCode.ps1` (for failed iterations)

In stacked diffs mode, this phase handles three outcomes:

- **Improved**: Amend commit with artifacts, push branch, create PR with `--base` set to the last successful iteration branch. Continue immediately (fire-and-forget).
- **Regressed/Stale**: Call `Revert-IterationCode.ps1` to undo the code change while preserving iteration artifacts (k6 results, RCA, metadata). Push the branch for the record. Increment the failure counter and continue.

The revert script restores the modified file via `git checkout HEAD~1 -- <file>`, stages the revert plus any iteration artifacts, and commits with a descriptive message. The branch is pushed to origin so the failed attempt is preserved remotely.

## Exit Conditions

### Stacked Diffs Mode

| Exit | Meaning | Result |
|------|---------|--------|
| **Max Consecutive Failures** | Too many consecutive failures (stale + regression) | PR stack contains all successful iterations |
| **Limit** | Max iterations reached | PR stack contains all successful iterations |
| **PR Rejected** | A PR was closed without merging (when WaitForMerge is on) | Loop stops |

### Legacy Mode

| Exit | Meaning | Result |
|------|---------|--------|
| **Regression** | An optimization degraded performance | Previous branch is the best |
| **No Improvement** | No gain for consecutive iterations | Best iteration branch identified |
| **Limit** | Max iterations reached | Best iteration branch identified |
| **Build Error** | Code doesn't compile | Manual intervention needed |

> **Note**: Efficiency-only improvements (CPU or working set reduction with flat performance)
> reset the stale iteration counter, preventing a premature `No Improvement` exit.

## Future: Efficiency-Only Optimization Phase

Once the performance optimization loop converges (exits with `No Improvement` and the
efficiency tiebreaker can no longer find gains), a second optimization phase could target
resource efficiency as the primary goal:

1. **Primary acceptance criteria**: CPU usage or working set must decrease
2. **Guard rails**: p95 latency, RPS, and error rate must not regress beyond tolerance
3. **Exit**: When no further efficiency gain can be found

This would allow the harness to first maximize throughput and latency, then minimize the
resource cost of delivering that performance — directly optimizing for shared-VM density.
This phase is not yet implemented.

## Logging

Every phase logs structured data to `sample-api/results/hone-{timestamp}.jsonl` via `Write-HoneLog.ps1`. Each log entry includes:

```json
{
  "timestamp": "2026-03-02T10:15:30.000Z",
  "iteration": 2,
  "phase": "measure",
  "level": "info",
  "message": "k6 scenario completed",
  "data": { "p95": 245.3, "rps": 1520, "errorRate": 0.001 }
}
```

This log file provides a complete audit trail of the optimization process.
