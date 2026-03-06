# Architecture

## Overview

Hone is an agentic performance optimization system. The **harness** (PowerShell scripts) orchestrates a closed-loop cycle: build the target API, verify correctness, measure performance, analyze bottlenecks with AI, apply fixes, and repeat.

The architecture separates concerns so the harness is reusable across different target projects, while the included sample API provides a concrete, testable reference implementation.

## System Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                        HONE HARNESS                          │
│                   (PowerShell 7.2+ Scripts)                      │
│                                                                  │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────────────┐  │
│  │   config     │  │ Invoke-      │  │  Write-HoneLog     │  │
│  │   .psd1      │  │ HoneLoop │  │  (Structured Logging)  │  │
│  └─────────────┘  └──────┬───────┘  └────────────────────────┘  │
│                          │                                       │
│         ┌────────┬───────┼────────┬──────────┐                   │
│         ▼        ▼       ▼        ▼          ▼                   │
│  ┌──────────┐┌───────┐┌───────┐┌────────┐┌────────┐             │
│  │  Build   ││Verify ││Measure││Analyze ││  Fix   │             │
│  │  (dotnet ││(dotnet││ (k6   ││(gh     ││(git   │             │
│  │  build)  ││ test) ││ run)  ││copilot)││branch)│             │
│  └────┬─────┘└───┬───┘└───┬───┘└───┬────┘└───┬───┘             │
└───────┼──────────┼────────┼────────┼─────────┼───────────────────┘
        │          │        │        │         │
        ▼          ▼        ▼        ▼         ▼
┌──────────────┐ ┌────┐ ┌─────┐ ┌────────┐ ┌─────┐
│ .NET 6 API   │ │xUnit│ │ k6  │ │GitHub  │ │ Git │
│ + EF Core 6  │ │+WAF │ │     │ │Copilot │ │     │
│ + LocalDB    │ │     │ │     │ │CLI     │ │     │
└──────────────┘ └────┘ └─────┘ └────────┘ └─────┘
```

## Components

### Harness (`harness/`)

The core of Hone. A set of PowerShell scripts that orchestrate the optimization loop.

| Script | Responsibility |
|--------|---------------|
| `Invoke-HoneLoop.ps1` | Main entry point — runs the full iterative loop |
| `Build-SampleApi.ps1` | Compiles the target project via `dotnet build` |
| `Start-SampleApi.ps1` | Launches the API as a background process |
| `Stop-SampleApi.ps1` | Gracefully shuts down the API process |
| `Invoke-E2ETests.ps1` | Runs `dotnet test` and parses results |
| `Invoke-ScaleTests.ps1` | Runs a single k6 scenario and parses JSON output |
| `Invoke-AllScaleTests.ps1` | Runs all registered k6 scenarios and returns per-scenario results |
| `Get-PerformanceBaseline.ps1` | Establishes initial performance baseline |
| `Compare-Results.ps1` | Compares current vs. baseline metrics |
| `Show-Results.ps1` | Displays formatted performance comparison tables in the terminal |
| `Invoke-AnalysisAgent.ps1` | Sends perf context to `copilot` CLI for analysis |
| `Invoke-ClassificationAgent.ps1` | Calls the hone-classifier agent to determine optimization scope (NARROW vs ARCHITECTURE) |
| `Invoke-FixAgent.ps1` | Calls the hone-fixer agent to generate optimized file content |
| `Apply-Suggestion.ps1` | Creates a branch and applies suggested changes |
| `Revert-IterationCode.ps1` | Reverts code on failed iterations while preserving artifacts |
| `Invoke-Cooldown.ps1` | Triggers server-side GC and sleeps for a cooldown period between test runs |
| `Reset-Database.ps1` | Drops and recreates the sample API database for clean state |
| `Export-Dashboard.ps1` | Generates an interactive HTML dashboard with Chart.js visualizations |
| `Export-IterationRCA.ps1` | Generates per-iteration root cause analysis markdown |
| `Get-MachineInfo.ps1` | Collects machine hardware, OS, and runtime info for performance context |
| `Start-DotnetCounters.ps1` | Launches `dotnet-counters` collection as a background process |
| `Stop-DotnetCounters.ps1` | Stops `dotnet-counters` and parses CSV output into structured metrics |
| `Update-OptimizationMetadata.ps1` | Maintains optimization log and opportunity queue |
| `Write-HoneLog.ps1` | Structured logging (JSON-lines format) |

### Target API (`sample-api/`)

A .NET 6 Web API + Razor Pages marketplace that serves as the optimization target for the harness.

#### Domain Model

The API models a marketplace with products, categories, reviews, orders, and a session-based shopping cart:

| Entity | Seed Data | Key Relationships |
|--------|-----------|-------------------|
| Category | 10 | Has many Products |
| Product | 1,000 | Belongs to Category; has Reviews, OrderItems, CartItems |
| Review | ~2,000 | Belongs to Product |
| Order | 100 | Has many OrderItems |
| OrderItem | ~300 | Belongs to Order and Product |
| CartItem | 0 (runtime) | Keyed by SessionId + ProductId |

#### API Surface

- **ProductsController** — CRUD + by-category + search (10 endpoints)
- **CategoriesController** — List all + get by ID with products (2 endpoints)
- **ReviewsController** — CRUD + by-product + average rating (6 endpoints)
- **OrdersController** — List/create/detail/status + by-customer (5 endpoints)
- **CartController** — Session-based add/get/update/remove/clear (5 endpoints)

#### Razor Pages Frontend

Server-rendered Bootstrap 5 UI (`/Pages`) with product browsing, cart management, checkout, and order history. Adds realistic browser-like load patterns for k6 testing.

### E2E Tests (`sample-api/SampleApi.Tests/`)

xUnit tests using `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory) with a real LocalDB test database (`HoneSampleDb_Tests`). These are the **regression gate** — the harness will not proceed past a failing test suite.

**43 tests** across 5 test classes sharing a single `SampleApiFactory` via `[Collection("SampleApi")]`:

| Test Class | Count | Covers |
|-----------|-------|--------|
| ProductsEndpointTests | 14 | Products + categories CRUD |
| ReviewsEndpointTests | 8 | Reviews CRUD + by-product + averages |
| OrdersEndpointTests | 7 | Orders create/detail/status/by-customer |
| CartEndpointTests | 7 | Cart add/get/update/remove/clear |
| RazorPagesTests | 7 | All 6 Razor Pages smoke tests |

### Scale Tests (`sample-api/scale-tests/`)

k6 scenarios that generate load against the running API and Razor Pages:

| Scenario | Purpose |
|----------|---------|
| `warmup.js` | Pre-test primer: 5 VUs × 10s to warm JIT compilation and connection pools |
| `baseline.js` | Steady-state: 50 VUs × 30s — hits all API endpoints + cart flow + Razor Pages |
| `stress.js` | Progressive ramp: 10→200 VUs over 2 min — mixed endpoints to find breaking points |
| `stress-products.js` | Product-focused ramp: 10→200 VUs over 2 min — full CRUD lifecycle on products |
| `stress-orders.js` | Order-focused ramp: 10→200 VUs over 2 min — create, fetch, and advance order status |
| `stress-reviews.js` | Review-focused ramp: 10→200 VUs over 2 min — create, query, average, and delete reviews |
| `stress-cart.js` | Cart-focused ramp: 10→200 VUs over 2 min — full cart session (add, read, update, remove, clear) |
| `spike.js` | Sudden burst: 1 VU baseline → instant 100 VU spike for 30s → recovery |

### Results (`sample-api/results/`)

Performance results directory. Baselines, k6 summaries, and run metadata are committed for review; counters, Copilot logs, and operational files are gitignored.

**Root-level (committed):**
- `baseline.json`, `baseline-counters.json`, `baseline-{scenario}.json` — reference metrics
- `run-metadata.json` — machine info and iteration history

**Per-iteration subdirectories (`iteration-{N}/`):**
- `k6-summary.json` / `k6-summary-{scenario}.json` — k6 results (committed)
- `root-cause.md` — root cause analysis document (committed)
- `dotnet-counters.csv` / `dotnet-counters.json` — runtime metrics (gitignored)
- `copilot-prompt.md` / `copilot-response.md` — AI analysis artifacts (gitignored)
- `e2e-results.trx` — E2E test results (gitignored)

**Optimization metadata (`metadata/`, committed):**
- `optimization-log.md` — append-only ledger of tried optimizations with outcomes
- `optimization-queue.md` — ranked list of potential optimizations, checked off when tried

**Root-level (gitignored):**
- `hone.jsonl` — structured run log
- `dashboard.html` — interactive Chart.js report

## Data Flow

```
                    Iteration N
                        │
        ┌───────────────┼───────────────┐
        ▼               ▼               ▼
   dotnet build    dotnet test      k6 run
        │               │               │
        ▼               ▼               ▼
   Build output    TRX results     JSON summary
                        │               │
                        ▼               ▼
                   Pass/Fail?     Compare-Results
                        │               │
                   (if fail,       ▼          ▼
                    ABORT)     Targets met?  Regression?
                                   │              │
                              (if yes,       (if yes,
                               EXIT)          ROLLBACK)
                                   │
                                   ▼
                          Invoke-AnalysisAgent
                                   │
                                   ▼
                           Suggested changes
                                   │
                                   ▼
                           Apply-Suggestion
                           (new git branch)
                                   │
                                   ▼
                            Iteration N+1
```

## Design Principles

1. **Harness is separate from the target** — The PowerShell scripts don't embed API-specific logic. They invoke external tools (`dotnet`, `k6`, `gh`) and parse their output.

2. **Every iteration is a git branch** — In stacked diffs mode (default), iterations form a linear chain. Successful iterations get PRs that diff against the previous success. Failed iterations have code reverted but artifacts preserved. In legacy mode, each iteration branches from master.

3. **E2E tests are the safety net** — No optimization is accepted if it breaks functionality.

4. **Structured data everywhere** — PowerShell objects, JSON files, typed results. No string parsing when avoidable.

5. **Idempotent phases** — Each phase can be re-run independently for debugging.

## Agent Definitions (`.github/agents/`)

Hone uses GitHub Copilot coding agents defined as Markdown files in `.github/agents/`:

| Agent | Purpose |
|-------|---------|
| `hone-analyst.agent.md` | System prompt for the analysis agent — guides Copilot to produce actionable optimization recommendations from performance data |
| `hone-classifier.agent.md` | System prompt for the classification agent — determines whether a proposed optimization is NARROW (single-file) or ARCHITECTURE (cross-cutting) scope |
| `hone-fixer.agent.md` | System prompt for the fix agent — generates optimized source code for a proposed change |
