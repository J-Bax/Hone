# Configuration

All harness configuration lives in [`harness/config.psd1`](../harness/config.psd1), a PowerShell data file with inline documentation for every setting.

The config file is the single source of truth — every option includes comments explaining its purpose, valid values, and defaults. Refer to the file directly rather than external docs.

## Key Configuration Areas

- **Api** — Solution path, project path, test project, base URL, health endpoint, results directory
- **Tolerances** — Regression threshold, improvement threshold, stale iteration limits, efficiency tiebreaker
- **ScaleTest** — Primary k6 scenario, scenario registry, warmup, measured runs, cooldown
- **Loop** — Max iterations, branch prefix, stacked diffs mode, wait-for-merge behavior
- **Copilot** — AI model selection and per-agent model overrides
- **DotnetCounters** — Runtime counter collection providers and sampling interval
- **Logging** — Log level

## Runtime Overrides

Command-line parameters take precedence over config file values:

```powershell
# Override max iterations
.\harness\Invoke-HoneLoop.ps1 -MaxIterations 10

# Use a different config file
.\harness\Invoke-HoneLoop.ps1 -ConfigPath .\my-config.psd1
```
