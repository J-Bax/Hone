# Getting Started

## Prerequisites

Ensure the following tools are installed and available on your `PATH`:

### Harness

| Tool | Minimum Version | Verify | Install |
|------|----------------|--------|---------|
| .NET SDK | 10.0 | `dotnet --version` | `winget install Microsoft.DotNet.SDK.10` |
| k6 | 0.47+ | `k6 version` | `winget install GrafanaLabs.k6` |
| GitHub CLI | 2.0+ | `gh --version` | `winget install GitHub.cli` |
| GitHub Copilot CLI | Latest | `copilot --version` | [Install standalone CLI](https://docs.github.com/copilot/how-tos/copilot-cli) — separate from `gh` |

### Optional Harness Tools

| Tool | Purpose | Install |
|------|---------|---------|
| PerfView | Deep CPU/GC/memory diagnostic profiling (Windows) | Download from [Microsoft/perfview releases](https://github.com/microsoft/perfview/releases) |

PerfView requires **Administrator privileges** at runtime for kernel-level ETW tracing. If you don't need diagnostic profiling, set `Diagnostics.Enabled: false` in `harness-csharp/config.yaml` or `.hone/config.yaml`.

### Sample API (reference only)

These are only needed if you're using the included sample API as your optimization target:

| Tool | Minimum Version | Verify | Install |
|------|----------------|--------|---------|
| .NET SDK | 6.0 | `dotnet --version` | `winget install Microsoft.DotNet.SDK.6` |
| SQL Server LocalDB | 2019 | `sqllocaldb info` | `winget install Microsoft.SQLServer.2019.Express` ¹ |

¹ There is no dedicated LocalDB winget package. SQL Server Express includes LocalDB, which is what the project uses via `(localdb)\MSSQLLocalDB`.

### Verify Authentication

```sh
# You must be authenticated with GitHub CLI
gh auth status

# If not authenticated:
gh auth login
```

## Quick Setup (Recommended)

After cloning the repo, install prerequisites via winget, then build and publish the harness:

```sh
git clone https://github.com/J-Bax/Hone.git
cd Hone
git submodule update --init --recursive

# Install .NET 10 SDK, k6, GitHub CLI
winget install Microsoft.DotNet.SDK.10 GrafanaLabs.k6 GitHub.cli

# Build the C# harness
dotnet build harness-csharp/Hone.slnx

# (Optional) Publish hone as a global tool or to a local path
dotnet publish harness-csharp/src/Hone.Cli/Hone.Cli.csproj -o ./out
# Then add ./out to your PATH, or invoke directly: ./out/hone
```

> **Note:** Run in an elevated (Administrator) terminal for winget installs and PerfView ETW support. Restart your terminal afterwards to pick up new `PATH` entries.

## Manual Setup

If you prefer to install dependencies individually, follow the steps below.

### 1. Clone the Repository

```sh
git clone https://github.com/J-Bax/Hone.git
cd Hone
git submodule update --init --recursive
```

### 2. Verify LocalDB

```sh
# Start the LocalDB instance
sqllocaldb start MSSQLLocalDB

# Verify connectivity
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "SELECT @@VERSION"
```

### 3. Build the C# Harness

```sh
dotnet build harness-csharp/Hone.slnx
```

All 629+ tests should be green:

```sh
dotnet test harness-csharp/Hone.slnx --verbosity quiet
```

### 4. Build the Sample API

```sh
dotnet build sample-api/SampleApi.sln
```

### 5. Run E2E Tests

The E2E tests use `WebApplicationFactory` so they start their own in-memory test server — no need to manually run the API first.

```sh
dotnet test sample-api/SampleApi.Tests/ --verbosity normal
```

All tests should pass. If any fail, check your LocalDB connection.

### 6. Start the API Manually (Optional)

If you want to explore the API interactively:

```sh
dotnet run --project sample-api/SampleApi/

# In another terminal:
curl http://localhost:5000/api/products
```

### 7. Run a Load Test Manually (Optional)

```sh
# API must be running first (step 6 above)
k6 run --env BASE_URL=http://localhost:5000 sample-api/scale-tests/scenarios/baseline.js
```

### 8. Establish a Performance Baseline

```sh
hone baseline --target sample-api
```

This starts the API, runs the baseline k6 scenario, saves results to `sample-api/.hone/results/baseline.json`, and stops the API.

### 9. Validate the Target Configuration

```sh
hone validate --target sample-api
```

This loads and validates `sample-api/.hone/config.yaml` against the engine schema without running any experiments.

### 10. Run the Full Hone Loop

```sh
hone run --target sample-api
```

This kicks off the agentic optimization cycle. Watch the console output for build results, test outcomes, performance metrics, Copilot suggestions, and experiment summaries.

## What to Expect

On the first run, the Hone loop will analyze the sample API's performance characteristics through k6 metrics and use Copilot to suggest optimizations. Each optimization is applied on a separate git branch and validated before proceeding.

After the loop completes (or between experiments), you can inspect results:

```sh
# View results in terminal
hone results --target sample-api

# Generate HTML dashboard
hone dashboard --target sample-api
```

## Troubleshooting

### LocalDB Connection Failures

```sh
# Reset LocalDB if it's in a bad state
sqllocaldb stop MSSQLLocalDB
sqllocaldb delete MSSQLLocalDB
sqllocaldb create MSSQLLocalDB
sqllocaldb start MSSQLLocalDB
```

### k6 Not Found

```sh
# Verify k6 is on PATH
k6 version

# If installed via winget but not on PATH, restart your terminal
winget install GrafanaLabs.k6
```

### GitHub Copilot CLI Issues

```sh
# Verify the standalone copilot CLI is installed and on PATH
copilot --version

# If not found, install from:
# https://docs.github.com/copilot/how-tos/copilot-cli

# Re-authenticate (copilot CLI uses GH_TOKEN)
gh auth refresh
```

### Harness Build Failures

```sh
# Ensure .NET 10 SDK is installed
dotnet --version   # should show 10.x.x

# Clean and rebuild
dotnet clean harness-csharp/Hone.slnx
dotnet build harness-csharp/Hone.slnx
```

### Config Validation Errors

Run `hone validate --target sample-api` to see detailed validation output. Common issues:

- Missing required fields in `.hone/config.yaml`
- Hook types referencing missing built-in hooks
- File paths that don't exist relative to the target root
- Tool availability checks (dotnet, k6, git, copilot, gh)

