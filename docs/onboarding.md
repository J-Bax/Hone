# Onboarding Guide

This guide covers how to assess a project's compatibility with Hone and automatically scaffold the `.hone/` configuration directory.

## Overview

Hone provides two CLI commands for onboarding new target projects:

| Command | Purpose |
|---------|---------|
| `hone assess --target <path>` | Read-only compatibility assessment |
| `hone init --target <path>` | Full onboarding: assess + scaffold `.hone/` |

## Quick Start

```sh
# Assess a project (read-only — safe to run anywhere)
hone assess --target /path/to/my-api

# If compatible, scaffold the .hone/ directory
hone init --target /path/to/my-api
```

## `hone assess`

Runs a compatibility assessment against the target project without modifying any files.

### What It Does

1. **Pre-probes** the target directory (project files, git info, directory structure, existing `.hone/`)
2. **Invokes** the `hone-compatibility` AI agent (Opus model) to analyze buildability, test health, API endpoints, and dependencies
3. **Renders** a color-coded terminal report with score, blockers, warnings, and ready items
4. **Writes** the full JSON report to `.hone-assessment.json` in the target directory

### Options

| Flag | Description |
|------|-------------|
| `--target <path>` | **Required.** Path to the target project directory |
| `--model <model>` | Override the AI model (default: `claude-opus-4.6`) |
| `--json` | Output raw JSON instead of the formatted terminal report |

### Example Output

```
══════════════════════════════════════════════════
  Hone Compatibility Assessment: SampleApi
══════════════════════════════════════════════════

  Overall: COMPATIBLE (85/100)

  Ready
    ✅ Git + GitHub — GitHub remote detected (main branch)
    ✅ CLI Build — dotnet build succeeds (12s)

  Warnings
    ⚠️ k6 — No existing k6 scenarios (estimated 1-2h to write)

  Blockers
    (none)

  Next Steps
    Run `hone init` to generate the .hone/ configuration directory.
══════════════════════════════════════════════════
```

### Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Compatible or partially compatible |
| 1 | Incompatible |
| 2 | Error (target not found, agent failure) |

## `hone init`

Runs the full onboarding pipeline: assess → scaffold → write.

### What It Does

1. **Assesses** compatibility (same as `hone assess`)
2. **Generates** configuration files via the `hone-scaffolder` AI agent (Sonnet model)
3. **Migrates** legacy PowerShell harness config if detected (optional, via `hone-migrator` agent)
4. **Writes** the generated `.hone/` directory structure to disk

### Options

| Flag | Description |
|------|-------------|
| `--target <path>` | **Required.** Path to the target project directory |
| `--model <model>` | Override the AI model for all agents |
| `--force` | Proceed even with low compatibility score; overwrite existing files |
| `--dry-run` | Show what would be generated without writing any files |

### Generated Files

A typical `hone init` produces:

```
.hone/
├── config.yaml          # Project configuration (hooks, API settings, scale test)
├── hooks/
│   ├── build.sh         # Build hook script (if Command type)
│   └── ...
└── scenarios/
    ├── baseline.js      # k6 baseline load test scenario
    └── ...
```

### Score Threshold

If the compatibility score is below **40/100**, `hone init` will refuse to proceed unless `--force` is specified. This prevents generating configs for projects that are likely to fail the optimization loop.

### Existing Files

By default, `hone init` **skips** files that already exist in the target directory. Use `--force` to overwrite them.

## Multi-Agent Architecture

The onboarding system uses three specialized AI agents coordinated by a C# orchestrator:

| Agent | Model | Role |
|-------|-------|------|
| `hone-compatibility` | Opus | Assess project readiness (runs commands, reads files) |
| `hone-scaffolder` | Sonnet | Generate `.hone/` config files from assessment |
| `hone-migrator` | Sonnet | Translate legacy PS harness to C# format (conditional) |

The **OnboardingManager** orchestrates the pipeline deterministically — AI is only invoked for intelligence-requiring tasks (analysis, generation, translation). File I/O, validation, and sequencing are handled in C#.

## PowerShell Harness Migration

If the target project has an existing PowerShell-based Hone harness (detected by `config.psd1`, `Invoke-*.ps1` scripts, or `harness-legacy/` directory), the migration agent automatically:

1. Reads the PS config and hook scripts
2. Maps PS settings to C# harness equivalents
3. Maps PS hook scripts to `BuiltIn` hooks where behavior matches
4. Generates `Command` hooks for custom scripts
5. Flags settings with no C# equivalent

Migration output is merged into the scaffold plan — migration values override scaffold defaults for matching config entries.

## After Onboarding

Once `hone init` completes:

1. **Review** the generated `.hone/config.yaml` — verify paths, hooks, and API settings
2. **Validate** with `hone validate --target <path>`
3. **Baseline** with `hone baseline --target <path>`
4. **Run** with `hone run --target <path>`

See [Getting Started](getting-started.md) and [Configuration](configuration.md) for details on each step.
