# Getting Started

## Prerequisites

Ensure the following tools are installed and available on your `PATH`:

### Required

| Tool | Minimum Version | Verify | Install |
|------|----------------|--------|---------|
| PowerShell | 7.2 | `$PSVersionTable.PSVersion` | `winget install Microsoft.PowerShell` |
| .NET SDK | 6.0 | `dotnet --version` | `winget install Microsoft.DotNet.SDK.6` |
| SQL Server LocalDB | 2019 | `sqllocaldb info` | `winget install Microsoft.SQLServer.2019.Express` |
| k6 | 0.47+ | `k6 version` | `winget install GrafanaLabs.k6` |
| GitHub CLI | 2.0+ | `gh --version` | `winget install GitHub.cli` |

### Required Extensions

```powershell
# Install the GitHub Copilot CLI extension
gh extension install github/gh-copilot

# Verify it's installed
gh copilot --version
```

### Verify Authentication

```powershell
# You must be authenticated with GitHub CLI
gh auth status

# If not authenticated:
gh auth login
```

## Quick Setup (Recommended)

After cloning the repo, run the setup script to install and verify all dependencies in one step:

```powershell
git clone https://github.com/your-org/autotune.git
cd autotune

# Install everything via winget
.\Setup-DevEnvironment.ps1
```

The script installs: .NET SDK 6, SQL Server LocalDB, k6, GitHub CLI, the gh-copilot extension, and the `dotnet-counters` global tool. It also starts LocalDB and restores NuGet packages.

> **Note:** Run in an elevated (Administrator) terminal for winget installs. Restart your terminal afterwards to pick up new `PATH` entries.

## Manual Setup

If you prefer to install dependencies individually, follow the steps below.

### 1. Clone the Repository

```powershell
git clone https://github.com/your-org/autotune.git
cd autotune
```

### 2. Verify LocalDB

```powershell
# Start the LocalDB instance
sqllocaldb start MSSQLLocalDB

# Verify connectivity
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "SELECT @@VERSION"
```

### 3. Build the Sample API

```powershell
dotnet build sample-api/SampleApi.sln
```

### 4. Run E2E Tests

The E2E tests use `WebApplicationFactory` so they start their own in-memory test server — no need to manually run the API first.

```powershell
dotnet test sample-api/SampleApi.Tests/ --verbosity normal
```

All tests should pass. If any fail, check your LocalDB connection.

### 5. Start the API Manually (Optional)

If you want to explore the API interactively:

```powershell
dotnet run --project sample-api/SampleApi/

# In another terminal:
Invoke-RestMethod http://localhost:5000/api/products | ConvertTo-Json
```

### 6. Run a Load Test Manually (Optional)

```powershell
# API must be running first (step 5 above)
k6 run --env BASE_URL=http://localhost:5000 scale-tests/scenarios/baseline.js
```

### 7. Establish a Performance Baseline

```powershell
.\harness\Get-PerformanceBaseline.ps1
```

This starts the API, runs the baseline k6 scenario, saves results to `results/baseline.json`, and stops the API.

### 8. Run the Full Autotune Loop

```powershell
.\harness\Invoke-AutotuneLoop.ps1
```

This kicks off the agentic optimization cycle. Watch the console output for build results, test outcomes, performance metrics, Copilot suggestions, and iteration summaries.

## What to Expect

On the first run, the sample API has intentional performance issues:
- N+1 query patterns in the products endpoint
- Missing database indexes
- No response caching
- Large unbounded result sets

The Autotune loop will detect these through k6 metrics and use Copilot to suggest fixes. Each fix is applied on a separate git branch and validated before proceeding.

## Troubleshooting

### LocalDB Connection Failures

```powershell
# Reset LocalDB if it's in a bad state
sqllocaldb stop MSSQLLocalDB
sqllocaldb delete MSSQLLocalDB
sqllocaldb create MSSQLLocalDB
sqllocaldb start MSSQLLocalDB
```

### k6 Not Found

```powershell
# Verify k6 is on PATH
Get-Command k6

# If installed via winget but not on PATH, restart your terminal
```

### GitHub Copilot CLI Issues

```powershell
# Re-authenticate
gh auth refresh

# Reinstall the extension
gh extension remove gh-copilot
gh extension install github/gh-copilot
```
