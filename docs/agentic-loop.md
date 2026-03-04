# The Agentic Loop

## Overview

The Hone agentic loop is a fully automated optimization cycle. It runs as a single PowerShell invocation (`Invoke-HoneLoop.ps1`) and proceeds through a fixed sequence of phases on each iteration, making decisions based on test results and performance metrics.

## Loop Lifecycle

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

| Condition | Action |
|-----------|--------|
| All thresholds met | Exit loop — success |
| P95 latency increased > 10% from previous iteration | Rollback branch, abort — regression detected |
| Error rate exceeds threshold | Rollback branch, abort — reliability regression |
| No performance change but CPU or working set reduced ≥ 5% | Continue (efficiency tiebreaker) — reset stale count |
| Iteration count = max iterations | Exit loop — limit reached |
| Otherwise | Continue to Analyze phase |

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

1. Creates a new git branch: `hone/iteration-{N}`
2. Applies the code changes suggested by Copilot
3. Commits with a descriptive message including the iteration number and targeted metric

The loop then increments the iteration counter and returns to Phase 1 (Build).

## Exit Conditions

| Exit | Meaning | Result |
|------|---------|--------|
| **Success** | All performance thresholds met | Final branch has all optimizations |
| **Regression** | An optimization broke functionality or degraded performance | Previous branch is the best |
| **No Improvement** | No performance or efficiency gain for consecutive iterations | Best iteration branch identified in summary |
| **Limit** | Max iterations reached without meeting all targets | Best iteration branch identified in summary |
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
