# Hone

**Agentic performance optimization for web APIs.**

Hone is a PowerShell-driven harness that automatically optimizes API performance through an iterative agentic loop. It measures with k6 load tests, analyzes bottlenecks with GitHub Copilot CLI, applies fixes, validates correctness, and repeats — producing a stack of reviewable PRs with measurable improvements.

```mermaid
graph LR
    MEASURE["📊 Measure"] --> ANALYZE["🧠 Analyze"]
    ANALYZE --> EXPERIMENT["🧪 Experiment"]
    EXPERIMENT --> VERIFY["✅ Verify"]
    VERIFY --> PUBLISH["📦 Publish"]
    PUBLISH --> MEASURE

    style MEASURE fill:#f5a623,color:#fff
    style ANALYZE fill:#9b59b6,color:#fff
    style EXPERIMENT fill:#e74c3c,color:#fff
    style VERIFY fill:#50c878,color:#fff
    style PUBLISH fill:#4a90d9,color:#fff
```

## How It Works

- Hone runs an **iterative optimization loop**: Analyze → Experiment → Verify → Measure → Publish
- A **three-agent AI pipeline** drives each experiment:
  - **Analyst** — examines performance metrics and source code to identify the highest-impact optimization
  - **Classifier** — determines whether the proposed change is narrow (single-file) or architectural (multi-file)
  - **Fixer** — generates the optimized code for narrow-scope changes
- Each experiment runs **multi-run load tests** with k6 and computes median latency with variance analysis
- Results are compared against the previous baseline to compute **measured performance deltas**
- Passing experiments are published as **stacked PRs** — a linear branch chain, each reviewable independently
- Failed experiments (test failures or performance regressions) are **automatically reverted**
- An **optimization history** tracks what has been tried so agents don't repeat failed approaches
- The loop continues until performance targets are met or the configured experiment limit is reached

## Features

- 🔍 **Automatic performance regression detection** — flags experiments that make things worse
- 📊 **Multi-run median with variance analysis** — reduces noise from flaky measurements
- 🔗 **Stacked diffs mode** — linear branch chain with fire-and-forget PRs
- 🤖 **Three-agent AI pipeline** with scope classification (narrow vs. architectural)
- 📝 **Optimization history tracking** — avoids repeating failed approaches across experiments
- 🎯 **Multi-scenario stress testing** with per-scenario baselines and thresholds
- 📈 **.NET runtime counter collection** — CPU, GC, thread pool, working set metrics
- 📋 **HTML dashboard and terminal results display** for at-a-glance comparison

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| PowerShell | 7.2+ | `winget install Microsoft.PowerShell` |
| .NET SDK | 6.0 | `winget install Microsoft.DotNet.SDK.6` |
| SQL Server LocalDB | 2019+ | Included with Visual Studio or `winget install Microsoft.SQLServer.2019.LocalDB` |
| k6 | Latest | `winget install GrafanaLabs.k6` |
| GitHub CLI | 2.0+ | `winget install GitHub.cli` |
| GitHub Copilot CLI | Latest | [Install standalone `copilot` CLI](https://docs.github.com/copilot/how-tos/copilot-cli) |

## Quick Start

```powershell
# 1. Clone the repo
git clone https://github.com/J-Bax/Hone.git
cd Hone
git submodule update --init --recursive

# 2. Build the sample API
dotnet build sample-api/SampleApi.sln

# 3. Run E2E tests (uses WebApplicationFactory, no running server needed)
dotnet test sample-api/SampleApi.Tests/

# 4. Establish a performance baseline
.\harness\Get-PerformanceBaseline.ps1

# 5. Run the full agentic optimization loop
.\harness\Invoke-HoneLoop.ps1
```

## Configuration

Edit `harness/config.psd1` to customize thresholds, experiment limits, API paths, and k6 scenarios. The config file is self-documented with inline comments for every setting.

See [docs/configuration.md](docs/configuration.md) for runtime override syntax.

## Documentation

- [Architecture](docs/architecture.md) — Design principles, loop flow, and decision logic
- [Getting Started](docs/getting-started.md) — Detailed setup guide
- [Configuration](docs/configuration.md) — Config overview and runtime overrides

