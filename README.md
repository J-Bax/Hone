# Hone

**Agentic performance optimization for web APIs.**

Hone is a PowerShell-driven harness that automatically optimizes the performance of API services through an iterative agentic loop. It builds your project, verifies correctness with E2E tests, measures performance with k6 load tests, uses GitHub Copilot CLI to brainstorm optimizations, applies fixes, and repeats — until performance targets are met or iteration limits are reached.

```mermaid
graph LR
    BUILD["🔨 Build"] --> VERIFY["✅ Verify<br/>(E2E Tests)"]
    VERIFY --> MEASURE["📊 Measure<br/>(k6)"]
    MEASURE --> ANALYZE["🧠 Analyze<br/>(Copilot)"]
    ANALYZE --> FIX["🔧 Fix<br/>(Apply)"]
    FIX --> BUILD

    style BUILD fill:#4a90d9,color:#fff
    style VERIFY fill:#50c878,color:#fff
    style MEASURE fill:#f5a623,color:#fff
    style ANALYZE fill:#9b59b6,color:#fff
    style FIX fill:#e74c3c,color:#fff
```

## Architecture Diagrams

### Component Architecture

The Hone harness is a set of PowerShell scripts that orchestrate external tools. The **Target API is a blackbox** — Hone only interacts with it through well-defined boundaries: build commands, test runners, and HTTP endpoints.

```mermaid
graph TB
    subgraph HARNESS["<b>Hone Harness</b> (PowerShell 7.2+)"]
        CONFIG["config.psd1<br/>─────────────<br/>Thresholds, paths,<br/>loop settings"]
        LOOP["Invoke-HoneLoop<br/>─────────────<br/>Main orchestrator"]
        LOG["Write-HoneLog<br/>─────────────<br/>Structured JSON logging"]

        BUILD_S["Build-SampleApi"]
        VERIFY_S["Invoke-E2ETests"]
        START["Start-SampleApi"]
        STOP["Stop-SampleApi"]
        SCALE["Invoke-ScaleTests"]
        BASELINE["Get-PerformanceBaseline"]
        COMPARE["Compare-Results"]
        COPILOT_S["Invoke-CopilotAnalysis"]
        APPLY["Apply-Suggestion"]

        CONFIG --> LOOP
        LOOP --> BUILD_S
        LOOP --> VERIFY_S
        LOOP --> START
        LOOP --> STOP
        LOOP --> SCALE
        LOOP --> BASELINE
        LOOP --> COMPARE
        LOOP --> COPILOT_S
        LOOP --> APPLY
        LOOP --> LOG
    end

    subgraph EXTERNAL["External Tools"]
        DOTNET["dotnet CLI"]
        K6["k6 (Grafana)"]
        GH["gh copilot suggest"]
        GIT["Git"]
    end

    subgraph TARGET["<b>Target API</b> (Blackbox)"]
        direction TB
        API_SRC["API Source Code"]
        E2E_TESTS["E2E Test Suite<br/><i>Must be provided by target</i>"]
        SCALE_TESTS["k6 Stress Tests<br/><i>Must be provided by target</i>"]
    end

    subgraph OUTPUT["Results (sample-api/results/)"]
        METRICS["Performance Metrics<br/>(JSON)"]
        LOGS["Iteration Logs<br/>(JSONL)"]
        REPORTS["Comparison Reports"]
    end

    BUILD_S --> DOTNET
    VERIFY_S --> DOTNET
    START --> DOTNET
    SCALE --> K6
    COPILOT_S --> GH
    APPLY --> GIT

    DOTNET --> API_SRC
    DOTNET --> E2E_TESTS
    K6 --> SCALE_TESTS

    COMPARE --> METRICS
    LOG --> LOGS
    COMPARE --> REPORTS

    style HARNESS fill:#1a1a2e,color:#e0e0e0,stroke:#4a90d9,stroke-width:2px
    style TARGET fill:#2d2d2d,color:#e0e0e0,stroke:#e67e22,stroke-width:3px,stroke-dasharray: 5 5
    style EXTERNAL fill:#1a1a2e,color:#e0e0e0,stroke:#666,stroke-width:1px
    style OUTPUT fill:#1a1a2e,color:#e0e0e0,stroke:#666,stroke-width:1px
    style E2E_TESTS fill:#e67e22,color:#fff
    style SCALE_TESTS fill:#e67e22,color:#fff
```

### Target API Contract (Blackbox Boundary)

Hone treats the target API as an opaque system. It does **not** understand the API's internal implementation. However, the target **must** provide two key artifacts that Hone depends on:

```mermaid
graph LR
    subgraph HONE["Hone Harness"]
        direction TB
        BUILD_CMD["Build Phase<br/><code>dotnet build</code>"]
        TEST_CMD["Verify Phase<br/><code>dotnet test</code>"]
        PERF_CMD["Measure Phase<br/><code>k6 run</code>"]
        FIX_CMD["Fix Phase<br/><code>git branch + commit</code>"]
    end

    subgraph BLACKBOX["Target API (Blackbox)"]
        direction TB
        SRC["Source Code<br/><i>Opaque to Hone</i>"]

        subgraph REQUIRED["<b>Required Contracts</b>"]
            FUNC["🧪 Functional Test Suite<br/>─────────────────────<br/>• Must exist and be runnable<br/>  via <code>dotnet test</code><br/>• Acts as regression gate<br/>• 100% pass = safe to proceed"]
            STRESS["📈 Stress Test Scenarios<br/>─────────────────────<br/>• k6 scenarios exercising<br/>  API endpoints<br/>• Produces measurable metrics<br/>  (p95, RPS, error rate)"]
        end

        subgraph OPAQUE["<b>Opaque Internals</b>"]
            DB["Database schema & queries"]
            LOGIC["Business logic"]
            ROUTES["Route/controller structure"]
            MODELS["Domain models"]
        end
    end

    BUILD_CMD -- "compiles" --> SRC
    TEST_CMD -- "executes" --> FUNC
    PERF_CMD -- "runs" --> STRESS
    FIX_CMD -- "modifies" --> SRC

    style BLACKBOX fill:#2d2d2d,color:#e0e0e0,stroke:#e67e22,stroke-width:3px,stroke-dasharray: 5 5
    style HONE fill:#1a1a2e,color:#e0e0e0,stroke:#4a90d9,stroke-width:2px
    style REQUIRED fill:#3d3020,color:#e0e0e0,stroke:#e67e22,stroke-width:2px
    style OPAQUE fill:#333,color:#999,stroke:#666,stroke-width:1px,stroke-dasharray: 3 3
    style FUNC fill:#e67e22,color:#fff
    style STRESS fill:#e67e22,color:#fff
    style DB fill:#555,color:#999
    style LOGIC fill:#555,color:#999
    style MODELS fill:#555,color:#999
    style ROUTES fill:#555,color:#999
```

### Agentic Loop Flowchart

The complete decision flow for a single invocation of `Invoke-HoneLoop.ps1`:

```mermaid
flowchart TD
    START(["▶ Start"]) --> LOAD_CONFIG["Load config.psd1"]
    LOAD_CONFIG --> CHECK_BASELINE{"Baseline<br/>exists?"}
    CHECK_BASELINE -- No --> RUN_BASELINE["Run Get-PerformanceBaseline"]
    RUN_BASELINE --> SET_ITER
    CHECK_BASELINE -- Yes --> SET_ITER["Iteration = 1"]

    SET_ITER --> BUILD["<b>Phase 1: Build</b><br/>dotnet build"]
    BUILD --> BUILD_OK{"Build<br/>succeeded?"}
    BUILD_OK -- No --> ABORT_BUILD(["🛑 Abort<br/><i>Build error</i>"])
    BUILD_OK -- Yes --> VERIFY

    VERIFY["<b>Phase 2: Verify</b><br/>dotnet test (E2E)"] --> TESTS_OK{"All tests<br/>pass?"}
    TESTS_OK -- No --> ROLLBACK_TEST["Rollback git branch"]
    ROLLBACK_TEST --> ABORT_TEST(["🛑 Abort<br/><i>Regression detected</i>"])
    TESTS_OK -- Yes --> MEASURE

    MEASURE["<b>Phase 3: Measure</b><br/>Start API → k6 run → Stop API"] --> COMPARE

    COMPARE["<b>Phase 4: Compare</b><br/>vs. baseline & thresholds"] --> TARGETS_MET{"All targets<br/>met?"}
    TARGETS_MET -- Yes --> SUCCESS(["🏆 Success<br/><i>Targets achieved</i>"])

    TARGETS_MET -- No --> REGRESSED{"Performance<br/>regressed?"}
    REGRESSED -- Yes --> ROLLBACK_PERF["Rollback git branch"]
    ROLLBACK_PERF --> ABORT_PERF(["🛑 Abort<br/><i>Perf regression</i>"])

    REGRESSED -- No --> MAX_ITER{"Iteration<br/>≥ max?"}
    MAX_ITER -- Yes --> LIMIT(["⏱ Exit<br/><i>Max iterations reached</i>"])

    MAX_ITER -- No --> ANALYZE["<b>Phase 5: Analyze</b><br/>gh copilot suggest<br/>(metrics + source context)"]
    ANALYZE --> FIX["<b>Phase 6: Fix</b><br/>Create branch<br/>Apply changes<br/>Commit"]
    FIX --> INCREMENT["Iteration++"]
    INCREMENT --> BUILD

    style START fill:#4a90d9,color:#fff
    style SUCCESS fill:#50c878,color:#fff
    style ABORT_BUILD fill:#e74c3c,color:#fff
    style ABORT_TEST fill:#e74c3c,color:#fff
    style ABORT_PERF fill:#e74c3c,color:#fff
    style LIMIT fill:#f5a623,color:#fff
    style BUILD fill:#4a90d9,color:#fff
    style VERIFY fill:#50c878,color:#fff
    style MEASURE fill:#f5a623,color:#fff
    style COMPARE fill:#f5a623,color:#fff
    style ANALYZE fill:#9b59b6,color:#fff
    style FIX fill:#e74c3c,color:#fff
```

### Data Flow

How data moves through the system across a single iteration:

```mermaid
flowchart LR
    subgraph INPUTS["Inputs"]
        CFG["config.psd1<br/>(thresholds, paths)"]
        BASELINE["baseline.json<br/>(reference metrics)"]
        PREV["Previous iteration<br/>metrics"]
    end

    subgraph PHASE_BUILD["Build"]
        DOTNET_BUILD["dotnet build"] --> BUILD_OUT["Build output<br/>(pass/fail)"]
    end

    subgraph PHASE_VERIFY["Verify"]
        DOTNET_TEST["dotnet test"] --> TRX["Test results<br/>(pass/fail count)"]
    end

    subgraph PHASE_MEASURE["Measure"]
        K6_RUN["k6 run"] --> K6_JSON["JSON summary<br/>• p95 latency<br/>• RPS<br/>• Error rate"]
    end

    subgraph PHASE_COMPARE["Compare"]
        DELTA["Compute deltas<br/>vs. baseline &<br/>previous iteration"]
    end

    subgraph PHASE_ANALYZE["Analyze"]
        PROMPT["Build prompt:<br/>metrics + deltas +<br/>source context"]
        PROMPT --> COPILOT["gh copilot suggest"]
        COPILOT --> SUGGESTION["Suggested<br/>code changes"]
    end

    subgraph PHASE_FIX["Fix"]
        BRANCH["git branch<br/>hone/iteration-N"]
        COMMIT["Apply changes<br/>+ commit"]
    end

    subgraph OUTPUTS["Outputs"]
        LOG_FILE["hone-*.jsonl<br/>(audit trail)"]
        RESULT_JSON["iteration-N.json<br/>(metrics snapshot)"]
        GIT_BRANCH["Git branch<br/>(rollback point)"]
    end

    CFG --> DOTNET_BUILD
    CFG --> DOTNET_TEST
    CFG --> K6_RUN
    BUILD_OUT --> DOTNET_TEST
    TRX --> K6_RUN
    K6_JSON --> DELTA
    BASELINE --> DELTA
    PREV --> DELTA
    DELTA --> PROMPT
    SUGGESTION --> BRANCH
    BRANCH --> COMMIT

    DELTA --> LOG_FILE
    K6_JSON --> RESULT_JSON
    COMMIT --> GIT_BRANCH

    style INPUTS fill:#1a1a2e,color:#e0e0e0,stroke:#4a90d9
    style OUTPUTS fill:#1a1a2e,color:#e0e0e0,stroke:#50c878
    style PHASE_BUILD fill:#1e2a3a,color:#e0e0e0,stroke:#4a90d9
    style PHASE_VERIFY fill:#1e2a3a,color:#e0e0e0,stroke:#50c878
    style PHASE_MEASURE fill:#1e2a3a,color:#e0e0e0,stroke:#f5a623
    style PHASE_COMPARE fill:#1e2a3a,color:#e0e0e0,stroke:#f5a623
    style PHASE_ANALYZE fill:#1e2a3a,color:#e0e0e0,stroke:#9b59b6
    style PHASE_FIX fill:#1e2a3a,color:#e0e0e0,stroke:#e74c3c
```

## How It Works

1. **Build** — Compiles the target API project (`dotnet build`)
2. **Verify** — Runs functional E2E tests to ensure correctness (`dotnet test`)
3. **Measure** — Executes k6 load tests to capture performance metrics (p95 latency, RPS, error rate)
4. **Analyze** — Sends performance data and hot-path context to GitHub Copilot CLI (`gh copilot suggest`) to brainstorm optimizations
5. **Fix** — Applies Copilot's suggested changes on a new git branch
6. **Repeat** — Loops back to Build, validating the fix doesn't regress functionality or performance

The loop exits when performance targets are met, the maximum iteration count is reached, or a regression is detected.

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| PowerShell | 7.2+ | `winget install Microsoft.PowerShell` |
| .NET SDK | 6.0 | `winget install Microsoft.DotNet.SDK.6` |
| SQL Server LocalDB | 2019+ | Included with Visual Studio or `winget install Microsoft.SQLServer.2019.LocalDB` |
| k6 | Latest | `winget install Grafana.k6` |
| GitHub CLI | 2.0+ | `winget install GitHub.cli` |
| GitHub Copilot CLI | Latest | `gh extension install github/gh-copilot` |

## Quick Start

```powershell
# 1. Clone the repo
git clone https://github.com/your-org/hone.git
cd hone

# 2. Build the sample API
dotnet build sample-api/SampleApi.sln

# 3. Run E2E tests (uses WebApplicationFactory, no running server needed)
dotnet test sample-api/SampleApi.Tests/

# 4. Establish a performance baseline
.\harness\Get-PerformanceBaseline.ps1

# 5. Run the full agentic optimization loop
.\harness\Invoke-HoneLoop.ps1
```

## Project Structure

```mermaid
graph TD
    subgraph REPO["hone/"]
        direction TB

        subgraph CORE["<b>Hone Core</b>"]
            HARNESS["harness/<br/>─────────────<br/>PowerShell orchestration<br/>config.psd1 + scripts"]
            DOCS["docs/<br/>─────────────<br/>Architecture, guides,<br/>configuration reference"]
            GITHUB[".github/<br/>─────────────<br/>Copilot instructions"]
        end

        subgraph TARGET_AREA["<b>Target API</b> (Blackbox — swappable)"]
            API["sample-api/<br/>─────────────<br/>API source code<br/><i>Internal details opaque</i>"]
            TESTS["sample-api/Tests/<br/>─────────────<br/>🧪 Functional test suite<br/><i>MUST be provided</i>"]
            STESTS["sample-api/scale-tests/<br/>─────────────<br/>📈 k6 stress scenarios<br/><i>MUST be provided</i>"]
        end

        RESULTS["sample-api/results/<br/>─────────────<br/>Metrics, logs, reports<br/><i>(gitignored, generated)</i>"]
    end

    HARNESS -- "orchestrates" --> API
    HARNESS -- "runs" --> TESTS
    HARNESS -- "runs" --> STESTS
    HARNESS -- "writes" --> RESULTS

    style CORE fill:#1a1a2e,color:#e0e0e0,stroke:#4a90d9,stroke-width:2px
    style TARGET_AREA fill:#2d2d2d,color:#e0e0e0,stroke:#e67e22,stroke-width:2px,stroke-dasharray: 5 5
    style TESTS fill:#e67e22,color:#fff
    style STESTS fill:#e67e22,color:#fff
    style API fill:#555,color:#ccc
    style RESULTS fill:#333,color:#aaa
    style REPO fill:#111,color:#e0e0e0,stroke:#444
```

> **Key insight**: The target API is a **blackbox** to Hone. The harness only requires that the target provides: **(1)** a buildable source project, **(2)** a functional test suite (regression gate), and **(3)** k6 stress test scenarios (performance measurement). Everything else — database schema, business logic, route structure, domain models — is opaque.

```
hone/
├── .github/                    # GitHub configuration & Copilot instructions
│   ├── copilot-instructions.md
│   └── ISSUE_TEMPLATE/
├── docs/                       # Architecture, guides, and reference docs
│   ├── architecture.md
│   ├── getting-started.md
│   ├── agentic-loop.md
│   └── configuration.md
├── sample-api/                 # Target API (blackbox — swappable)
│   ├── SampleApi/              # API source code (opaque internals)
│   ├── SampleApi.Tests/        # ⚠ Functional test suite (REQUIRED)
│   ├── scale-tests/            # ⚠ k6 load test scenarios (REQUIRED)
│   │   ├── scenarios/
│   │   └── thresholds.json
│   └── results/                # Output: metrics, reports, logs (gitignored)
├── harness/                    # PowerShell orchestration scripts
│   ├── config.psd1             # Harness configuration
│   ├── Invoke-HoneLoop.ps1     # Main entry point
│   └── ...                     # Build, test, measure, analyze, fix scripts
```

## Configuration

Edit `harness/config.psd1` to customize:
- Performance thresholds (p95 latency, RPS targets)
- Maximum optimization iterations
- API URL and project paths
- k6 scenario selection

See [docs/configuration.md](docs/configuration.md) for the full reference.

## Documentation

- [Architecture](docs/architecture.md) — System design and component interactions
- [Getting Started](docs/getting-started.md) — Detailed setup guide
- [Agentic Loop](docs/agentic-loop.md) — Deep dive into each optimization phase
- [Configuration](docs/configuration.md) — All settings explained
