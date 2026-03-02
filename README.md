# Autotune

**Agentic performance optimization for web APIs.**

Autotune is a PowerShell-driven harness that automatically optimizes the performance of API services through an iterative agentic loop. It builds your project, verifies correctness with E2E tests, measures performance with k6 load tests, uses GitHub Copilot CLI to brainstorm optimizations, applies fixes, and repeats — until performance targets are met or iteration limits are reached.

```
┌─────────────────────────────────────────────────────────┐
│                    AUTOTUNE LOOP                        │
│                                                         │
│   ┌───────┐   ┌────────┐   ┌─────────┐   ┌─────────┐  │
│   │ BUILD ├──►│ VERIFY ├──►│ MEASURE ├──►│ ANALYZE │  │
│   └───────┘   │ (E2E)  │   │  (k6)   │   │(Copilot)│  │
│       ▲       └────────┘   └─────────┘   └────┬────┘  │
│       │                                        │        │
│       │       ┌────────┐                       │        │
│       └───────┤  FIX   │◄──────────────────────┘        │
│               │(Apply) │                                │
│               └────────┘                                │
└─────────────────────────────────────────────────────────┘
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
git clone https://github.com/your-org/autotune.git
cd autotune

# 2. Build the sample API
dotnet build sample-api/SampleApi.sln

# 3. Run E2E tests (uses WebApplicationFactory, no running server needed)
dotnet test sample-api/SampleApi.Tests/

# 4. Establish a performance baseline
.\harness\Get-PerformanceBaseline.ps1

# 5. Run the full agentic optimization loop
.\harness\Invoke-AutotuneLoop.ps1
```

## Project Structure

```
autotune/
├── .github/                    # GitHub configuration & Copilot instructions
│   ├── copilot-instructions.md
│   └── ISSUE_TEMPLATE/
├── docs/                       # Architecture, guides, and reference docs
│   ├── architecture.md
│   ├── getting-started.md
│   ├── agentic-loop.md
│   └── configuration.md
├── sample-api/                 # Sample .NET 6 Web API (optimization target)
│   ├── SampleApi/              # API project (EF Core + SQL Server LocalDB)
│   └── SampleApi.Tests/        # xUnit E2E tests (WebApplicationFactory)
├── harness/                    # PowerShell orchestration scripts
│   ├── config.psd1             # Harness configuration
│   ├── Invoke-AutotuneLoop.ps1 # Main entry point
│   └── ...                     # Build, test, measure, analyze, fix scripts
├── scale-tests/                # k6 load test scenarios
│   ├── scenarios/
│   └── thresholds.json
└── results/                    # Output: metrics, reports, logs (gitignored)
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
