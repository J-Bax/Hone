# Hone Harness — Improvement Recommendations

> **Date:** 2026-03-15 · **Scope:** `harness/` scripts, configuration, and plugin framework
> **Methodology:** Full codebase read of 30+ PowerShell scripts, config, collectors, and analyzers

---

## Executive Summary

The Hone harness is functionally complete and well-designed for its core mission—automated, iterative API performance optimization. The 5-phase loop (Measure → Analyze → Experiment → Verify → Publish), queue-driven analysis, stacked-diffs mode, and plugin-based diagnostic framework are all solid foundations.

However, the codebase has accumulated patterns typical of rapid feature iteration: **duplicated boilerplate, a monolithic main loop, missing abstractions, and insufficient robustness** around error handling, timeouts, and cleanup. This document organizes improvements into tiers by impact and provides specific, actionable recommendations for each.

---

## Table of Contents

1. [Tier 1 — Reliability & Safety](#1-tier-1--reliability--safety)
2. [Tier 2 — Code Duplication & Shared Abstractions](#2-tier-2--code-duplication--shared-abstractions)
3. [Tier 3 — Main Loop Decomposition](#3-tier-3--main-loop-decomposition)
4. [Tier 4 — Robustness & Resource Lifecycle](#4-tier-4--robustness--resource-lifecycle)
5. [Tier 5 — Testability](#5-tier-5--testability)
6. [Tier 6 — Configuration & Validation](#6-tier-6--configuration--validation)
7. [Tier 7 — Performance & Minor Improvements](#7-tier-7--performance--minor-improvements)
8. [Appendix — File-by-File Quick Reference](#appendix--file-by-file-quick-reference)

---

## 1. Tier 1 — Reliability & Safety

These issues can cause hangs, data corruption, or incorrect results.

### 1.1 Add Timeouts to Copilot CLI Calls

**Problem:** `Invoke-AnalysisAgent.ps1`, `Invoke-FixAgent.ps1`, and `Invoke-ClassificationAgent.ps1` all invoke `copilot` with no timeout. If an agent hangs, the entire harness loop hangs indefinitely.

**Files:** `Invoke-AnalysisAgent.ps1:149`, `Invoke-FixAgent.ps1:106`, `Invoke-ClassificationAgent.ps1:101`

**Recommendation:** Wrap copilot CLI calls in a `Start-Process` + `WaitForExit` pattern with a configurable timeout (e.g., `Copilot.AgentTimeoutSec = 600`). Kill the process if it exceeds the deadline. The classification agent already has retry logic—extend the same pattern to analysis and fix agents.

```powershell
# Example: Timeout wrapper
$proc = Start-Process -FilePath 'copilot' -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardOutput $outFile
if (-not $proc.WaitForExit($timeoutMs)) {
    Stop-Process -Id $proc.Id -Force
    throw "Copilot agent timed out after $($timeoutMs/1000)s"
}
```

### 1.2 Add Timeouts to k6 Diagnostic Runs

**Problem:** `Invoke-DiagnosticMeasurement.ps1:159` runs k6 with no timeout. A malformed scenario could run indefinitely.

**Recommendation:** Add a `Diagnostics.K6TimeoutSec` config option (default: 300) and use the same start-process-with-timeout pattern.

### 1.3 Sanitize Database Name in Reset-Database

**Problem:** `Reset-Database.ps1:57` interpolates a regex-captured database name directly into SQL. If the name contains `]` or other special characters, this is a SQL injection vector.

**Recommendation:** Escape with `$dbName.Replace(']', ']]')` or use a parameterized query via `Invoke-Sqlcmd -Variable`.

### 1.4 Validate File Paths Before Applying Fixes

**Problem:** `Apply-Suggestion.ps1:59` strips `sample-api/` prefix and uses the path directly. If the analysis agent returns a path like `../../etc/hosts`, it could write outside the project.

**Recommendation:** After normalizing the path, validate it resolves within `$repoRoot/sample-api/`:

```powershell
$resolvedPath = [System.IO.Path]::GetFullPath($fullTargetPath)
if (-not $resolvedPath.StartsWith((Join-Path $repoRoot 'sample-api'), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Path traversal blocked: $targetFile resolves outside sample-api"
}
```

### 1.5 Handle Division by Zero in Metric Comparisons

**Problem:** `Compare-Results.ps1:74` — `Get-PctChange` divides by `$Previous`. While it guards against `$Previous -eq 0`, floating-point edge cases (e.g., very small baseline error rates) could produce `[double]::PositiveInfinity`.

**Recommendation:** Add a range guard that caps the return value, and document the expected input ranges.

---

## 2. Tier 2 — Code Duplication & Shared Abstractions

These refactors reduce maintenance burden and improve consistency.

### 2.1 Extract Shared Config Loading Helper

**Problem:** Nearly every script in `harness/` repeats the same 3-line config loading pattern:

```powershell
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'config.psd1' }
$config = Import-PowerShellDataFile -Path $ConfigPath
```

**Recommendation:** Create `harness/Get-HoneConfig.ps1` as a dot-sourceable helper:

```powershell
function Get-HoneConfig {
    param([string]$ConfigPath)
    if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'config.psd1' }
    Import-PowerShellDataFile -Path $ConfigPath
}
```

### 2.2 Unify Agent Invocation Boilerplate

**Problem:** The three agent scripts (`Invoke-AnalysisAgent.ps1`, `Invoke-FixAgent.ps1`, `Invoke-ClassificationAgent.ps1`) all repeat:
- UTF-8 encoding save/restore (`[Console]::OutputEncoding`)
- Model selection logic (per-agent override → global → hardcoded fallback)
- Spinner start/stop
- Response file saving
- JSON code-fence stripping

**Files:** `Invoke-AnalysisAgent.ps1:134-153`, `Invoke-FixAgent.ps1:91-112`, `Invoke-ClassificationAgent.ps1:78-109`

**Recommendation:** Create `Invoke-CopilotAgent.ps1` (a general-purpose agent runner) that handles:
1. Model resolution from config
2. UTF-8 encoding management
3. Spinner lifecycle
4. Timeout enforcement (Tier 1.1)
5. Response saving to experiment directory
6. JSON extraction from markdown fences
7. Retry logic (currently only in classification agent)

Each specific agent script becomes thin: build prompt → call runner → parse domain-specific output.

### 2.3 Extract Git Artifact Staging Logic

**Problem:** The same artifact staging block (analysis prompts, k6 summaries, metadata files, analyzer outputs) is duplicated in three places:
- `Apply-Suggestion.ps1:84-127`
- `Revert-ExperimentCode.ps1:79-121`
- `Invoke-HoneLoop.ps1:1454-1492` (amend path)

**Recommendation:** Create `Stage-ExperimentArtifacts.ps1` that takes an experiment number and stages all standard artifacts. All three call sites invoke this one function.

### 2.4 Unify Health Check Retry Logic

**Problem:** Health check retry loops are independently implemented in:
- `Start-SampleApi.ps1:56-71` (timeout-based, 1s sleep)
- `Invoke-ScaleTests.ps1:78-99` (5 attempts, 2s sleep)

**Recommendation:** Create a `Wait-ApiHealthy` helper with configurable retries, interval, and timeout. Both scripts call it.

### 2.5 Extract Write-Status to Shared Module

**Problem:** The `Write-Status` function is identically redefined in 7+ scripts:
- `Invoke-HoneLoop.ps1:56-61`
- `Invoke-AnalysisAgent.ps1:55-61`
- `Invoke-FixAgent.ps1:42-48`
- `Invoke-ClassificationAgent.ps1:34-40`
- `Invoke-DiagnosticMeasurement.ps1:54-60`
- `Invoke-DiagnosticCollection.ps1:66-72`
- `Invoke-DiagnosticAnalysis.ps1:55-61`
- `Export-Dashboard.ps1:36-42`

**Recommendation:** Define `Write-Status` in a shared module (e.g., `harness/HoneHelpers.ps1`) and dot-source it.

---

## 3. Tier 3 — Main Loop Decomposition

### 3.1 Break Invoke-HoneLoop.ps1 into Phase Modules

**Problem:** `Invoke-HoneLoop.ps1` is ~1900 lines and handles all 5 experiment phases, state management, PR creation, branch management, and summary reporting. It contains:
- 6 inline helper functions
- ~200 lines of rejection handling duplicated across 5 failure modes (build failure, test failure, API start failure, scale test failure, regression)
- Complex stacked-diffs vs legacy mode branching throughout

**Recommendation:** Extract each phase into its own script:

| Module | Responsibility |
|--------|---------------|
| `Invoke-MeasurePhase.ps1` | Load baseline, run comparison for analysis context |
| `Invoke-AnalyzePhase.ps1` | Diagnostic profiling + analysis agent + queue init |
| `Invoke-ExperimentPhase.ps1` | Classification → fix → apply → build |
| `Invoke-VerifyPhase.ps1` | E2E tests → scale tests → compare results |
| `Invoke-PublishPhase.ps1` | Push branch, create PR (accepted or rejected) |
| `Invoke-FailureHandler.ps1` | Unified revert + rejected PR creation + metadata update |

The rejection handling is the highest-value extraction. The current code has **5 near-identical rejection blocks** (~80 lines each) for build failure, test failure, API start failure, scale test failure, and regression/stale. These all:
1. Revert code via `Revert-ExperimentCode.ps1`
2. Update metadata via `Update-OptimizationMetadata.ps1`
3. Mark queue item done via `Manage-OptimizationQueue.ps1`
4. Update branch/failure tracking variables
5. Build rejected PR body
6. Create rejected PR via `New-ExperimentPR`
7. Check consecutive failure limit

A single `Invoke-FailureHandler` taking `$outcome` and `$outcomeLabel` would eliminate ~400 lines of duplication.

### 3.2 Extract PR Body Construction

**Problem:** PR body markdown is constructed inline in `Invoke-HoneLoop.ps1` with here-strings containing interpolated metrics tables. Both the accepted PR body (~50 lines) and rejected PR body (`Build-RejectedPRBody` function + inline metrics assembly) are complex.

**Recommendation:** Move PR body templates to a `Build-PRBody.ps1` script that accepts structured data and returns the markdown. This also makes it easier to customize PR formatting.

---

## 4. Tier 4 — Robustness & Resource Lifecycle

### 4.1 Add try/finally Guards for Process Cleanup

**Problem:** Multiple scripts start external processes (API, dotnet-counters, PerfView) without guaranteed cleanup on error. If a script throws between `Start-Process` and the corresponding `Stop-*` call, orphan processes remain.

**Key locations:**
- `Invoke-DiagnosticMeasurement.ps1:114` — API started, collectors started, but only API stop is in `finally`
- `Invoke-ScaleTests.ps1:152-171` — Counter process started, no `finally` guard

**Recommendation:** Wrap all start→stop pairs in `try/finally`:

```powershell
$counterHandle = $null
try {
    $counterHandle = & Start-DotnetCounters.ps1 ...
    # ... work ...
}
finally {
    if ($counterHandle) { & Stop-DotnetCounters.ps1 -CounterHandle $counterHandle }
}
```

### 4.2 Use Atomic Writes for Queue State

**Problem:** `Manage-OptimizationQueue.ps1:88` writes directly to `experiment-queue.json` via `Out-File`. If the process is interrupted mid-write, the file becomes corrupt.

**Recommendation:** Write to a temp file, then `Move-Item -Force` (atomic on NTFS):

```powershell
$tempPath = "$queueJsonPath.tmp"
$Queue | ConvertTo-Json -Depth 10 | Out-File -FilePath $tempPath -Encoding utf8
Move-Item -Path $tempPath -Destination $queueJsonPath -Force
```

### 4.3 Validate dotnet-counters Attachment

**Problem:** `Start-DotnetCounters.ps1:104` sleeps 2 seconds and assumes the counters are attached. There's no verification the process actually connected.

**Recommendation:** After starting, check that the CSV output file is being written to (size > 0 after a brief wait), or parse stderr for the "Press p to pause" startup message.

### 4.4 Add Log Rotation

**Problem:** `Write-HoneLog.ps1` appends to `hone.jsonl` without bounds. Over many experiments, this file can grow unbounded.

**Recommendation:** Add optional rotation: when the file exceeds a configurable size (e.g., 50MB), rename it to `hone.jsonl.1` and start a new file. Alternatively, integrate with `Logging.MaxFileSizeMB` in config.

### 4.5 Handle Port Reuse Race Condition

**Problem:** `Start-SampleApi.ps1:34-39` finds a free port via `TcpListener`, immediately releases it, then passes it to `dotnet run`. Another process could claim the port in between.

**Recommendation:** This is inherently racy on all platforms. Mitigate by retrying port selection if `dotnet run` fails to bind, or by using port 0 natively in `dotnet run --urls http://localhost:0` and extracting the actual port from process output.

---

## 5. Tier 5 — Testability

### 5.1 Design for Dependency Injection

**Problem:** Most scripts are untestable because they directly call:
- `copilot` CLI (no mock seam)
- `dotnet build/test/run` (no mock seam)
- `k6 run` (no mock seam)
- `git` operations (filesystem side effects)
- `Write-HoneLog.ps1` (file I/O side effects)

**Recommendation:** For scripts where testability matters most (Compare-Results, metrics parsing, queue management), extract pure computational logic into functions that take inputs and return outputs with no side effects. Keep I/O at the script boundary.

Example refactoring of `Compare-Results.ps1`:

```powershell
# Pure function — easily testable
function Compare-Metrics {
    param($Current, $Previous, $Tolerances)
    # ... returns comparison result without logging
}

# Script boundary — handles I/O
$comparison = Compare-Metrics -Current $CurrentMetrics -Previous $reference -Tolerances $tolerances
# ... logging happens after
```

### 5.2 Add Integration Test Points

**Recommendation:** The DryRun mode is a good foundation. Extend it to support:
- `$DryRun -and $MockAgentResponses` — use canned JSON responses instead of calling copilot
- A test fixture that sets up a minimal experiment directory structure for exercising Compare-Results, Manage-OptimizationQueue, etc. without needing a running API

### 5.3 Document Plugin Interface Contracts

**Problem:** Collector and analyzer plugins must return specific object shapes, but these contracts exist only in code comments.

**Recommendation:** Add a `docs/plugin-contracts.md` document specifying the exact return shapes:

| Plugin Method | Required Return Fields |
|--------------|----------------------|
| `Start-Collector.ps1` | `@{ Success; Handle }` |
| `Stop-Collector.ps1` | `@{ Success; ArtifactPaths = @(...) }` |
| `Export-CollectorData.ps1` | `@{ Success; ExportedPaths; Summary }` |
| `Invoke-Analyzer.ps1` | `@{ Success; Report; Summary; PromptPath; ResponsePath }` |

---

## 6. Tier 6 — Configuration & Validation

### 6.1 Add Config Validation on Startup

**Problem:** `config.psd1` values are never validated. Nonsensical values (e.g., `MaxRegressionPct = 500`, negative cooldowns, empty paths) silently produce incorrect behavior.

**Recommendation:** Add a `Test-HoneConfig.ps1` script that validates:
- Tolerance percentages are in [0, 1] range
- Paths exist (SolutionPath, ScenarioPath, etc.)
- Port numbers are valid
- Required tools are installed
- Boolean fields are actually booleans

Run it at the start of `Invoke-HoneLoop.ps1` before entering the experiment loop.

### 6.2 Document Config Interaction Warnings

**Problem:** Certain config combinations interact in non-obvious ways:

| Setting A | Setting B | Interaction |
|-----------|-----------|-------------|
| `StackedDiffs = $true` | `WaitForMerge = $true` | Works but defeats the purpose of stacked diffs |
| `DiagnosticRuns = 0` | `Diagnostics.Enabled = $true` | Collectors start but no k6 data collected |
| `MeasuredRuns = 1` | `Tolerances.MaxRegressionPct < 0.05` | Single run noise may exceed tight tolerance |

**Recommendation:** Add a "Configuration Interactions" section to `docs/configuration.md`.

### 6.3 Add Per-Metric Absolute Thresholds

**Problem:** Only p95 has a `MinAbsoluteP95DeltaMs` guard against false positives. RPS and error rate comparisons lack equivalent guards.

**Recommendation:** Add `MinAbsoluteRPSDelta` and `MinAbsoluteErrorRateDelta` to prevent false positives on metrics with small baselines.

---

## 7. Tier 7 — Performance & Minor Improvements

### 7.1 Cache Config and Path Computations

**Problem:** Many scripts call `Import-PowerShellDataFile` on every invocation, even when called multiple times per experiment (e.g., `Write-HoneLog.ps1` is called 20+ times).

**Recommendation:** For `Write-HoneLog.ps1` specifically, accept a `$LogPath` parameter (the caller already knows it) or cache the config in a script-scoped variable.

### 7.2 Reduce Redundant Health Checks

**Problem:** `Invoke-ScaleTests.ps1:78-99` runs a 5-retry health check before every k6 run. If the API just passed E2E tests, it's definitely healthy.

**Recommendation:** Accept a `-SkipHealthCheck` switch for cases where the caller has already verified health.

### 7.3 Use PowerShell Module Instead of Dot-Sourcing

**Problem:** Helper scripts are invoked via `& (Join-Path $PSScriptRoot 'Script.ps1')` which starts a new script scope each time. For frequently-called helpers (logging, progress), this adds overhead.

**Recommendation:** Consider packaging shared helpers into a `.psm1` module that loads once per session. This also improves discoverability and avoids path-joining boilerplate.

### 7.4 Remove Legacy Format Support

**Problem:** `Invoke-AnalysisAgent.ps1:173-232` supports both a legacy response format (`{filePath, explanation, additionalOpportunities}`) and the current format (`{opportunities: [...]}`). This adds ~60 lines of complexity.

**Recommendation:** Since the agent prompts control the output format, remove legacy support and simplify to a single parser.

---

## Appendix — File-by-File Quick Reference

| Script | LOC | Key Issues | Priority |
|--------|-----|-----------|----------|
| `Invoke-HoneLoop.ps1` | ~1900 | Monolithic; 5 duplicated rejection blocks; no timeout on agents | High |
| `Invoke-AnalysisAgent.ps1` | 276 | No timeout; fragile JSON parsing; duplicated boilerplate | High |
| `Invoke-FixAgent.ps1` | 161 | No timeout; code block regex fragile; no code validation | High |
| `Invoke-ClassificationAgent.ps1` | 174 | Retry logic not shared; duplicated boilerplate | Medium |
| `Compare-Results.ps1` | 342 | Division-by-zero edge; logging mixed with logic | Medium |
| `Apply-Suggestion.ps1` | 160 | Path traversal risk; duplicated artifact staging | High |
| `Revert-ExperimentCode.ps1` | 175 | Duplicated artifact staging | Medium |
| `Invoke-ScaleTests.ps1` | 370 | Hardcoded retries; no counter collection timeout | Medium |
| `Invoke-DiagnosticMeasurement.ps1` | 227 | No k6 timeout; complex multi-pass orchestration | Medium |
| `Invoke-DiagnosticCollection.ps1` | 299 | Plugin discovery assumptions; Group-Object shadowing | Low |
| `Invoke-DiagnosticAnalysis.ps1` | 174 | Inconsistent error handling (exception vs. Success=false) | Low |
| `Manage-OptimizationQueue.ps1` | 253 | Non-atomic writes; linear search; markdown coupling | Medium |
| `Start-SampleApi.ps1` | 103 | Port reuse race; unconfigurable priority elevation | Medium |
| `Stop-DotnetCounters.ps1` | 211 | Fragile CSV parsing; complex nested loops | Low |
| `Start-DotnetCounters.ps1` | 143 | No attachment validation; hardcoded sleep | Medium |
| `Reset-Database.ps1` | 101 | SQL injection risk; fragile regex parsing | High |
| `Write-HoneLog.ps1` | 84 | No log rotation; redundant config loading | Low |
| `Show-Progress.ps1` | 107 | No ASCII fallback; potential timer leak | Low |
| `Build-AnalysisContext.ps1` | 180 | Full scenario content in prompt; fragile path math | Low |
| `Export-Dashboard.ps1` | ~800 | Large inline HTML template; not modular | Low |

---

## Recommended Implementation Order

1. **Quick wins (Tier 1):** Timeouts, path validation, SQL sanitization — immediate reliability improvement
2. **Shared abstractions (Tier 2):** Config helper, agent runner, artifact stager — reduces duplication by ~300 lines
3. **Failure handler extraction (Tier 3.1):** Single biggest impact — eliminates ~400 lines of duplication in the main loop
4. **Resource guards (Tier 4):** try/finally, atomic writes — prevents data corruption
5. **Config validation (Tier 6.1):** Catches misconfigurations before they waste an experiment cycle
6. **Testability (Tier 5):** Extract pure functions, add mock seams
7. **Phase decomposition (Tier 3):** Full main loop breakup — largest effort but biggest long-term payoff

---

*Generated by analysis of the Hone harness codebase at commit HEAD.*
