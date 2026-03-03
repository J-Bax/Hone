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
| `Invoke-ScaleTests.ps1` | Runs `k6 run` and parses JSON output |
| `Get-PerformanceBaseline.ps1` | Establishes initial performance baseline |
| `Compare-Results.ps1` | Compares current vs. baseline metrics |
| `Invoke-CopilotAnalysis.ps1` | Sends perf context to `gh copilot suggest` |
| `Apply-Suggestion.ps1` | Creates a branch and applies suggested changes |
| `Write-HoneLog.ps1` | Structured logging (JSON-lines format) |

### Target API (`sample-api/`)

A .NET 6 Web API + Razor Pages marketplace with **intentionally suboptimal patterns** that give the harness real optimization targets:

- **N+1 query patterns** — Fetching related data in loops instead of using `.Include()`
- **Missing database indexes** — No indexes on foreign keys or filter columns
- **No caching** — Every request hits the database
- **No pagination** — API endpoints return full result sets
- **In-memory filtering** — Loads entire tables then filters in C#
- **One-by-one operations** — Cart clear deletes items individually with `SaveChanges()` per item

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
| `baseline.js` | Steady-state: 50 VUs for 30s — hits all API endpoints + cart flow + Razor Pages |
| `stress.js` | Progressive ramp: 10→200 VUs over 2 min — random selection from 14 endpoints |
| `spike.js` | Sudden burst: idle → 100 VUs instant — reviews, products, and pages |

### Results (`sample-api/results/`)

All generated output (gitignored). Each iteration produces:

- k6 JSON summary (latency percentiles, RPS, error rates)
- Comparison report (current vs. baseline deltas)
- Copilot suggestion log
- Iteration metadata (timestamps, branch names, outcomes)

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
                          Invoke-CopilotAnalysis
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

2. **Every iteration is a git branch** — Easy to compare, rollback, or cherry-pick individual optimizations.

3. **E2E tests are the safety net** — No optimization is accepted if it breaks functionality.

4. **Structured data everywhere** — PowerShell objects, JSON files, typed results. No string parsing when avoidable.

5. **Idempotent phases** — Each phase can be re-run independently for debugging.
