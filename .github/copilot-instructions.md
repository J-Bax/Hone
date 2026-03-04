# Copilot Instructions for Hone

## Project Context

Hone is an **agentic performance optimization harness** that automatically improves API service performance through an iterative loop of testing, measuring, analyzing, and fixing.

## Tech Stack

- **Harness**: PowerShell 7.2+ scripts orchestrating the full optimization loop
- **Target API**: .NET 6 Web API with Entity Framework Core 6 and SQL Server LocalDB
- **Load Testing**: k6 (Grafana) with JavaScript scenario scripts
- **E2E Testing**: xUnit with `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory)
- **AI Agent**: GitHub Copilot CLI (standalone `copilot` command, Claude Opus 4.6) for optimization analysis
- **Platform**: Windows-first, PowerShell-native

## Key Directories

| Directory | Purpose |
|-----------|---------|
| `harness/` | PowerShell orchestration scripts — the core of the project |
| `sample-api/SampleApi/` | .NET 6 Web API — the optimization target |
| `sample-api/SampleApi.Tests/` | xUnit E2E tests — the regression gate |
| `sample-api/scale-tests/` | k6 load test scenarios and thresholds |
| `sample-api/results/` | Generated output (gitignored) |
| `docs/` | Architecture and usage documentation |

## Coding Conventions

### PowerShell
- Use approved verbs (`Invoke-`, `Get-`, `Set-`, `Start-`, `Stop-`)
- Prefer `[CmdletBinding()]` and `param()` blocks on all scripts
- Use PowerShell data files (`.psd1`) for configuration, not JSON
- Target PowerShell 7.2+ (`$PSVersionTable.PSVersion`)
- Use `Write-Information` / `Write-Verbose` over `Write-Host`
- Return structured objects, not formatted strings

### C# / .NET
- .NET 6 minimal API style (`WebApplication.CreateBuilder`)
- Use nullable reference types
- Prefer `async`/`await` throughout
- Entity Framework Core 6 with code-first migrations
- Connection string: SQL Server LocalDB (`(localdb)\MSSQLLocalDB`)

### k6 (JavaScript)
- Export `options` and default function per k6 conventions
- Use `check()` for response validation
- Output JSON summary via `--out json` or `handleSummary()`
- Read base URL from `__ENV.BASE_URL`

## Agentic Loop Phases

1. **Build** → `dotnet build`
2. **Verify** → `dotnet test` (E2E, must pass 100%)
3. **Measure** → `k6 run` (capture p95 latency, RPS, error rate)
4. **Analyze** → `copilot --model claude-opus-4.6` with perf context prompt
5. **Fix** → Apply suggestion on a new git branch
6. **Repeat** → Until targets met or max iterations reached

## Important Design Decisions

- The sample API is the optimization target — the agentic loop discovers performance issues through measurement, not hints
- E2E tests use `WebApplicationFactory` so they don't require a running server
- Performance results are stored as JSON in `sample-api/results/` for comparison across iterations
- Each optimization attempt is made on a separate git branch for easy rollback
