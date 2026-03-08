# Configuration

All harness configuration lives in [`harness/config.psd1`](../harness/config.psd1), a PowerShell data file with inline documentation for every setting.

The config file is the single source of truth — every option includes comments explaining its purpose, valid values, and defaults. Refer to the file directly rather than external docs.

## Key Configuration Areas

- **Api** — Solution path, project path, test project, base URL, health endpoint, results directory
- **Tolerances** — Regression threshold, improvement threshold, stale experiment limits, efficiency tiebreaker
- **ScaleTest** — Primary k6 scenario, scenario registry, warmup, measured runs, cooldown
- **Loop** — Max experiments, branch prefix, stacked diffs mode, wait-for-merge behavior
- **Copilot** — AI model selection and per-agent model overrides
- **DotnetCounters** — Runtime counter collection providers and sampling interval
- **Logging** — Log level

## Runtime Overrides

`Invoke-HoneLoop.ps1` exposes two command-line parameters that take precedence over config file values:

```powershell
# Override max experiments
.\harness\Invoke-HoneLoop.ps1 -MaxExperiments 10

# Use a different config file
.\harness\Invoke-HoneLoop.ps1 -ConfigPath .\my-config.psd1
```

These are the only CLI overrides. To change any other setting (tolerances, scale-test options, model selection, etc.), edit `config.psd1` directly.
