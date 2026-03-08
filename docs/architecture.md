# Architecture

## Overview

Hone is an agentic performance optimization system. A set of PowerShell scripts (the "harness") orchestrate a closed-loop cycle: stress-test the API to find bottlenecks, analyze the measurements with AI to propose a fix, experiment by implementing the fix, verify that it actually works (functionally and performance-wise), then publish the results. The target API is treated as a **blackbox** — Hone only requires buildable source, a functional test suite, and k6 stress tests.

## Design Principles

1. **Harness is separate from the target.** The PowerShell scripts contain no API-specific logic. They invoke external tools (`dotnet`, `k6`, `copilot`, `git`) and parse their output. Any API that provides the required contracts can be optimized.

2. **The target API is a blackbox.** Hone builds its own understanding of the API's internals by analyzing the source code during the optimization process. It requires three contracts: (1) a buildable source project, (2) a functional test suite acting as a regression gate, and (3) stress test scenarios producing measurable metrics to find hot spots.

3. **Measure first, then think.** Every experiment starts with measurement. You can't optimize what you haven't measured. The agent analyzes real stress test data — not guesses.

4. **Relative improvement, not absolute targets.** The loop accepts any measurable performance improvement and rejects regressions beyond a configured tolerance. It stops when the optimization surface is exhausted.

5. **Every experiment is a git branch.** Code changes are isolated on branches. Successful experiments produce PRs; failed experiments are reverted but preserve the experiment and measurement artifacts for the record.

6. **Structured data everywhere.** PowerShell objects, JSON metrics, typed results. No string parsing when avoidable.

## Single Experiment Flow

Each experiment is a self-contained cycle of 5 phases:

```mermaid
flowchart TD
    subgraph MEASURE["📊 1. Measure"]
        direction TB
        M1["Reference metrics"]
        M1 -.-> M2["API metrics (p95, RPS)"]
        M1 -.-> M3["Efficiency (CPU, memory)"]
    end

    subgraph ANALYZE["🧠 2. Analyze (conditional)"]
        direction TB
        A0{"Queue empty?"}
        A0 -->|Yes| A1["Metrics + source code"]
        A1 --> A2["Identify bottlenecks"]
        A2 --> A3["Propose 3-5 optimizations → queue"]
        A0 -->|No| A4["Skip analysis"]
    end

    subgraph EXPERIMENT["🧪 3. Experiment"]
        direction LR
        E1["Pick from queue"] --> E2["Classify scope"] --> E3["Implement + build"]
    end

    subgraph VERIFY["✅ 4. Verify"]
        direction LR
        V1["Functional tests"] --> V2["Stress-test"] --> V3["Accept if no regression"]
    end

    subgraph PUBLISH["📦 5. Publish"]
        direction LR
        P1["Create PR"] --> P2["Preserve artifacts"]
    end

    MEASURE --> ANALYZE --> EXPERIMENT --> VERIFY --> PUBLISH

    style MEASURE fill:#f5a623,color:#fff
    style ANALYZE fill:#9b59b6,color:#fff
    style EXPERIMENT fill:#e74c3c,color:#fff
    style VERIFY fill:#50c878,color:#fff
    style PUBLISH fill:#4a90d9,color:#fff
```

### Queue-Driven Analysis

The analysis agent (Phase 2) only runs when the **optimization queue** is empty. Each analysis pass produces 3-5 ranked optimization opportunities stored in `optimization-queue.json`. Subsequent experiments pick from this queue one at a time. When the queue is exhausted, the analysis agent runs again with fresh post-experiment metrics.

This design is efficient (analysis is the most expensive AI call) and ensures the loop doesn't re-analyze the entire codebase before every single code change.

## Decision Logic

After measuring, the harness compares three metrics against the previous experiment:

| Metric | Improved when | Regressed when |
|--------|--------------|----------------|
| p95 Latency | Decreased | Increased > MaxRegressionPct (default 10%) AND absolute delta > MinAbsoluteP95DeltaMs (default 5ms) |
| Requests/sec | Increased | Decreased > MaxRegressionPct |
| Error Rate | Decreased | Increased > MaxRegressionPct |

**Accept** if at least one metric improved and none regressed. **Reject** if any metric regressed beyond tolerance. **Stale** if nothing changed.

When performance is flat but OS-level resource usage (CPU or working set) decreased, the **efficiency tiebreaker** accepts the experiment — preventing premature stops when there are genuine resource gains.

## Stacked Diffs (Continuous Mode)

In the default stacked diffs mode, experiments form a **linear branch chain**. Each experiment branches from the previous one, regardless of outcome.

```mermaid
graph TD
    M["master"] --> I1["hone/experiment-1<br/>✅ improved → PR #12"]
    I1 --> I2["hone/experiment-2<br/>❌ regressed → reverted"]
    I2 --> I3["hone/experiment-3<br/>✅ improved → PR #15<br/><i>base=experiment-1</i>"]
    I3 --> I4["hone/experiment-4<br/>❌ stale → reverted"]
    I4 --> I5["hone/experiment-5<br/>✅ improved → PR #18<br/><i>base=experiment-3</i>"]

    style M fill:#333,color:#fff
    style I1 fill:#50c878,color:#fff
    style I2 fill:#e74c3c,color:#fff
    style I3 fill:#50c878,color:#fff
    style I4 fill:#e74c3c,color:#fff
    style I5 fill:#50c878,color:#fff
```

- **Successful experiments** get PRs that diff against the last successful branch — reviewers see only the incremental optimization.
- **Failed experiments** have their code change reverted in-place, but the branch is pushed with artifacts preserved (k6 results, analysis, root cause) for the record.
- PRs are **fire-and-forget** — the loop creates them and continues immediately without waiting for merge.

## Exit Conditions

The loop stops when any of these conditions is met:

| Condition | Meaning |
|-----------|---------|
| **Max consecutive failures** | Too many consecutive regressions + stale experiments (default 10) |
| **Max experiments** | Configured experiment limit reached |
| **Build failure** | Code doesn't compile (non-stacked mode) |
| **Test failure** | E2E regression detected (non-stacked mode) |

In stacked mode, build and test failures trigger a revert-and-continue rather than an abort, allowing the loop to recover and try different optimizations.

## Dual-Phase Measurement

Hone uses two distinct measurement phases with different purposes:

### Evaluation Measurement (Phase 4: Verify)

Fair benchmarking used for **accept/reject decisions**. Runs k6 with median-of-5 selection and lightweight dotnet-counters only. No heavy profiling tools. This is the source of truth for whether an optimization improved performance.

### Diagnostic Measurement (Phase 2: Analyze)

Deep profiling used **only as analysis input**. Runs PerfView ETW collection alongside a single k6 pass to capture:
- **CPU sampling stacks** — flamegraph data showing which methods dominate CPU time
- **GC events** — detailed garbage collection statistics (generation counts, pause times, heap sizes)
- **Allocation tick sampling** — which types are allocated most frequently and from which call-sites

Diagnostic measurement runs once per analysis cycle (when the optimization queue is empty). Since profiling tools add 5–15% overhead to latency/throughput, these numbers are **never used for regression testing** — they exist solely to give the analysis agent deeper insight into where bottlenecks live.

## Diagnostic Plugin Architecture

The diagnostic framework uses a **plugin model** for both data collection and analysis. New profiling tools can be added by dropping in a directory — no orchestrator changes needed.

### Collector Plugins (`harness/collectors/<name>/`)

Each collector is a self-contained directory with 4 files:

| File | Purpose |
|------|---------|
| `collector.psd1` | Metadata: name, description, RequiresAdmin, OverheadImpact, DefaultSettings |
| `Start-Collector.ps1` | Start data collection targeting an API process → returns handle |
| `Stop-Collector.ps1` | Stop collection → returns raw artifact paths |
| `Export-CollectorData.ps1` | Convert raw artifacts to analysis-friendly format |

Built-in collectors: `perfview-cpu`, `perfview-gc`, `dotnet-counters`

### Analyzer Plugins (`harness/analyzers/<name>/`)

Each analyzer is a self-contained directory with 3 files:

| File | Purpose |
|------|---------|
| `analyzer.psd1` | Metadata: name, RequiredCollectors, AgentName, DefaultSettings |
| `Invoke-Analyzer.ps1` | Build prompt from collector data, call AI agent, parse response |
| `agent.md` | Copilot agent definition (also symlinked to `.github/agents/`) |

Built-in analyzers: `cpu-hotspots` (reads folded CPU stacks), `memory-gc` (reads GC report)

### Adding a New Plugin

**New collector** (e.g., `thread-contention`):
1. Create `harness/collectors/thread-contention/` with the 4 standard files
2. Add settings under `Diagnostics.CollectorSettings.'thread-contention'` in `config.psd1`

**New analyzer** (e.g., `thread-hotspots`):
1. Create `harness/analyzers/thread-hotspots/` with the 3 standard files
2. Add settings under `Diagnostics.AnalyzerSettings.'thread-hotspots'` in `config.psd1`
3. Copy or symlink `agent.md` to `.github/agents/`

The orchestrators (`Invoke-DiagnosticCollection.ps1`, `Invoke-DiagnosticAnalysis.ps1`) automatically discover and run all enabled plugins.
