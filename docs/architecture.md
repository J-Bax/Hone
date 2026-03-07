# Architecture

## Overview

Hone is an agentic performance optimization system. A set of PowerShell scripts (the "harness") orchestrate a closed-loop cycle: analyze bottlenecks with AI, apply a fix, build, verify correctness, measure performance, and decide whether to keep or revert the change. The target API is treated as a **blackbox** — Hone only requires buildable source, a functional test suite, and k6 stress tests.

## Design Principles

1. **Harness is separate from the target.** The PowerShell scripts contain no API-specific logic. They invoke external tools (`dotnet`, `k6`, `copilot`, `git`) and parse their output. Any API that provides the required contracts can be optimized.

2. **The target API is a blackbox.** Hone does not understand the API's internals. It requires three contracts: (1) a buildable source project, (2) a functional test suite acting as a regression gate, and (3) k6 stress test scenarios producing measurable metrics.

3. **E2E tests are the safety net.** No optimization is accepted if it breaks functionality. Every code change must pass 100% of tests before performance is even measured.

4. **Relative improvement, not absolute targets.** The loop accepts any measurable improvement and rejects regressions beyond a configured tolerance. It stops when the optimization surface is exhausted.

5. **Every iteration is a git branch.** Code changes are isolated on branches. Successful iterations produce PRs; failed iterations are reverted but preserved for the record.

6. **Structured data everywhere.** PowerShell objects, JSON metrics, typed results. No string parsing when avoidable.

## Single Iteration Flow

Each iteration is a self-contained cycle of 7 phases:

```mermaid
flowchart TD
    ANALYZE["<b>1. Analyze</b><br/>Build prompt with metrics + source context<br/>Call copilot CLI → get optimization suggestion<br/>Classify scope (NARROW vs ARCHITECTURE)<br/><i>Architecture changes are queued, not applied</i>"]
    FIX["<b>2. Fix</b><br/>Create git branch from current position<br/>Generate optimized code, apply change, commit"]
    BUILD["<b>3. Build</b><br/>dotnet build<br/><i>Failure → revert code, push branch, continue</i>"]
    VERIFY["<b>4. Verify</b><br/>dotnet test (E2E suite)<br/><i>Failure → revert code, push branch, continue</i>"]
    MEASURE["<b>5. Measure</b><br/>Start API → k6 run (median of N runs) → Stop API<br/>Capture p95 latency, RPS, error rate<br/>Run diagnostic stress scenarios"]
    COMPARE["<b>6. Compare</b><br/>Compare vs. previous iteration and baseline<br/>Decision: improved / regressed / stale"]
    PUBLISH["<b>7. Publish or Revert</b><br/>Improved → push branch, create PR, continue<br/>Regressed → revert code, push branch, continue<br/>Stale → revert code, push branch, continue"]

    ANALYZE --> FIX --> BUILD --> VERIFY --> MEASURE --> COMPARE --> PUBLISH

    style ANALYZE fill:#9b59b6,color:#fff
    style FIX fill:#e74c3c,color:#fff
    style BUILD fill:#4a90d9,color:#fff
    style VERIFY fill:#50c878,color:#fff
    style MEASURE fill:#f5a623,color:#fff
    style COMPARE fill:#f5a623,color:#fff
    style PUBLISH fill:#2c3e50,color:#fff
```

## Decision Logic

After measuring, the harness compares three metrics against the previous iteration:

| Metric | Improved when | Regressed when |
|--------|--------------|----------------|
| p95 Latency | Decreased | Increased > MaxRegressionPct (default 10%) AND absolute delta > MinAbsoluteP95DeltaMs (default 5ms) |
| Requests/sec | Increased | Decreased > MaxRegressionPct |
| Error Rate | Decreased | Increased > MaxRegressionPct |

**Accept** if at least one metric improved and none regressed. **Reject** if any metric regressed beyond tolerance. **Stale** if nothing changed.

When performance is flat but OS-level resource usage (CPU or working set) decreased, the **efficiency tiebreaker** accepts the iteration — preventing premature stops when there are genuine resource gains.

## Stacked Diffs (Continuous Mode)

In the default stacked diffs mode, iterations form a **linear branch chain**. Each iteration branches from the previous one, regardless of outcome.

```mermaid
graph TD
    M["master"] --> I1["hone/iteration-1<br/>✅ improved → PR #12"]
    I1 --> I2["hone/iteration-2<br/>❌ regressed → reverted"]
    I2 --> I3["hone/iteration-3<br/>✅ improved → PR #15<br/><i>base=iteration-1</i>"]
    I3 --> I4["hone/iteration-4<br/>❌ stale → reverted"]
    I4 --> I5["hone/iteration-5<br/>✅ improved → PR #18<br/><i>base=iteration-3</i>"]

    style M fill:#333,color:#fff
    style I1 fill:#50c878,color:#fff
    style I2 fill:#e74c3c,color:#fff
    style I3 fill:#50c878,color:#fff
    style I4 fill:#e74c3c,color:#fff
    style I5 fill:#50c878,color:#fff
```

- **Successful iterations** get PRs that diff against the last successful branch — reviewers see only the incremental optimization.
- **Failed iterations** have their code change reverted in-place, but the branch is pushed with artifacts preserved (k6 results, analysis, root cause) for the record.
- PRs are **fire-and-forget** — the loop creates them and continues immediately without waiting for merge.

## Exit Conditions

The loop stops when any of these conditions is met:

| Condition | Meaning |
|-----------|---------|
| **Max consecutive failures** | Too many consecutive regressions + stale iterations (default 10) |
| **Max iterations** | Configured iteration limit reached |
| **Build failure** | Code doesn't compile (non-stacked mode) |
| **Test failure** | E2E regression detected (non-stacked mode) |

In stacked mode, build and test failures trigger a revert-and-continue rather than an abort, allowing the loop to recover and try different optimizations.
