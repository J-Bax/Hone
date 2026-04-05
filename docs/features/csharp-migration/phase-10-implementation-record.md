# Phase 10 Implementation Record — Target Migration & Cutover

> **Status:** Complete
> **Date:** 2026-07-21
> **Phase:** 10 — Target Migration & Cutover (FINAL PHASE)
> **Baseline:** 612 tests → 629 tests (+17 new)
> **Build:** 0 warnings, 0 errors

---

## Summary

Phase 10 is the final phase of Hone's PowerShell-to-C# migration. It converts all `.hone/config.psd1` files to YAML, validates the full C# harness end-to-end, rewrites all documentation for the C# tech stack, archives the PowerShell harness to `harness-legacy/`, and establishes the C# harness as the primary entry point with xUnit CI.

### PowerShell Files Replaced/Archived

| Action | Scope |
|---|---|
| `harness/` → `harness-legacy/` | Full PowerShell harness archived (107 files) |
| `.PSScriptAnalyzerSettings.psd1` | Deleted — PS lint config |
| `Invoke-Lint.ps1` | Deleted — PS lint runner |
| `.githooks/pre-commit`, `.githooks/pre-commit.ps1` | Deleted — PS lint git hook |
| `Setup-DevEnvironment.ps1` | Deleted — PS dev setup |
| 8 `.psd1` config files | Converted to equivalent `.yaml` configs |

---

## Slices Executed

### Slice 1: Target Config Migration (§10.1)

**Worker:** `hone-migration-loop-host`
**Critics:** 5 (4 always-on + parity-heavy)
**Gate:** REJECT → APPROVE (2 iterations)

**Files created:**
- `harness-csharp/config.yaml` — Engine defaults (full HoneConfig with all 9 sections, inline YAML comments)
- `sample-api/.hone/config.yaml` — SampleApi target config (Name, BaseBranch, Hooks + API/ScaleTest overrides)
- `harness-legacy/tests/fixtures/mock-target/.hone/config.yaml` — MockTarget fixture
- `harness-legacy/tests/fixtures/targets/happy-path/.hone/config.yaml` — HappyPath fixture
- `harness-legacy/tests/fixtures/targets/regression/.hone/config.yaml` — Regression fixture
- `harness-legacy/tests/fixtures/targets/test-failure/.hone/config.yaml` — TestFailure fixture
- `harness-legacy/tests/fixtures/targets/build-failure/.hone/config.yaml` — BuildFailure fixture
- `harness-legacy/tests/fixtures/targets/stacked-diffs/.hone/config.yaml` — StackedDiffs fixture
- `harness-csharp/tests/Hone.Core.Tests/Config/ConfigYamlMigrationTests.cs` — 10 parity validation tests

**Files modified:**
- `harness-csharp/src/Hone.Core/Config/ConfigLoader.cs` — Added `WithTypeMapping` for `IReadOnlyList<string>`, `IReadOnlyDictionary<string, CollectorSettingsEntry>`, `IReadOnlyDictionary<string, AnalyzerSettingsEntry>` (YamlDotNet 16 cannot instantiate interface types without explicit mappings)

**Key conversion mapping:**

| PowerShell | YAML / C# |
|---|---|
| `.psd1` format | `.yaml` format (PascalCase keys) |
| `Copilot` section | `Agents` section |
| `Copilot.Model` | `Agents.DefaultModel` |
| `Copilot.FixModel` | `Agents.ImplementerModel` |
| `Fixer` section | `Implementer` section |
| `$true` / `$false` | `true` / `false` |
| `$null` | `null` or omitted |
| Hook Type `Script` | `Command` (with `pwsh -NonInteractive -File ...`) |
| Hook Type `Shared` | `BuiltIn` (native C# implementation) |
| Hook Type `Skip` | `Skip` (unchanged) |
| Hook Type `Http` | `Http` (unchanged) |
| `HarnessTesting` section | Omitted (PS-specific test harness; C# uses mock-based tests) |

**Critic rejection findings (fixed):**
- B1: eShopOnWeb not in repository — documented as approved deviation (not in this repo)
- B2: Missing `perfview-gc.StopTimeoutSec` and `ExportTimeoutSec` test assertions — added
- B3: `Prepare` hook mapped as `BuiltIn` but no C# `PrepareHook` exists — changed to `Command` type with `pwsh` invocation

### Slice 2: E2E Validation Tests (§10.2)

**Worker:** `hone-migration-loop-host`
**Critics:** 5 (4 always-on + test-strategy)
**Gate:** APPROVE
**Iterations:** 1

**Files created:**
- `harness-csharp/tests/Hone.Integration.Tests/EndToEndValidationTests.cs` — 7 E2E validation tests

**Files modified:**
- `harness-csharp/tests/Hone.Integration.Tests/Hone.Integration.Tests.csproj` — Added Hone.Measurement and Hone.Reporting project references
- `harness-csharp/src/Hone.Reporting/Hone.Reporting.csproj` — Added InternalsVisibleTo for Hone.Integration.Tests

**Tests implemented:**

| Test | §10.2 Requirement | Validates |
|---|---|---|
| `ConfigMergeFromYaml_EngineAndTarget_ProducesCorrectMerge` | Config merge | Real YAML → ConfigLoader → ConfigMerger → target overrides win |
| `MetricComparison_SnapshotTest_MatchesExpected` | Metric comparison | Known inputs → MetricComparer → correct ComparisonResult + JSON round-trip |
| `QueueState_InitConsumeMarkDone_CorrectJsonSequence` | Queue state | Init → GetNext×3 → MarkDone×3 → correct JSON statuses |
| `BranchStructure_StackedDiffs_CorrectBranchChain` | Branch structure | 3 stacked experiments → correct `hone/experiment-N` naming and base branch chaining |
| `PrBody_SnapshotTest_MatchesExpectedMarkdown` | PR body | Known data → PrBodyBuilder → markdown with all expected sections |
| `RcaExport_SnapshotTest_MatchesExpectedMarkdown` | RCA | Known data → RcaBuilder → markdown with root cause, metrics, fix sections |
| `FullLoop_DryRun_CompletesWithCorrectLifecycle` | Full loop | Dry-run mode → HoneLoopRunner → correct event lifecycle |

### Slice 3: Documentation Updates (§10.3)

**Worker:** `hone-migration-loop-host`
**Critics:** 4 (4 always-on)
**Gate:** APPROVE-WITH-DOC-UPDATE
**Iterations:** 1

**Files modified (8 existing docs rewritten):**

| Document | Key Changes |
|---|---|
| `docs/architecture.md` | Rewritten for C# harness: .NET 10, module dependency graph, `HoneEventBus`/`IHoneEventSink` observability, `IAgentRunner`/`IProcessRunner` |
| `docs/getting-started.md` | Prerequisites: .NET 10 SDK, removed PowerShell/Pester; commands: `hone run`, `hone validate`, `hone baseline` |
| `docs/configuration.md` | Full rewrite for YAML: three-layer merge, all 9 config sections with YAML examples, `Agents`/`Implementer` section names |
| `docs/plugin-contracts.md` | Rewritten with C# interfaces: `ICollectorPlugin`, `IAnalyzerPlugin`, typed return records |
| `docs/adapter-contracts.md` | Updated: `config.yaml`, `BuiltIn`/`Command`/`Http`/`Skip` hook types, `hone run --target` invocation |
| `docs/agent-designs.md` | Added `IAgentRunner`/`CopilotCliAgentRunner`/`AgentInvoker` architecture section, updated Mermaid diagram labels |
| `.github/copilot-instructions.md` | Full rewrite: C# tech stack, `harness-csharp/` directories, C# coding conventions |
| `README.md` | Updated: .NET 10 prerequisites, `hone run` quick start, YAML config, migration summary link |

**Files created (1 new doc):**
- `docs/features/csharp-migration/migration-summary.md` — Complete migration summary: phase-by-phase table, before/after comparison, test coverage, remaining gaps

**Critic-identified fixes applied:**
- `agent-designs.md`: "the fixer" → "the implementer"
- `migration-summary.md`: `RcaExporter` → `RcaBuilder` (actual class name)
- `migration-summary.md`: Completion date corrected to Phase 10

### Slice 4: Cutover Steps (§10.4 + §10.5)

**Worker:** `hone-migration-loop-host`
**Critics:** 5 (4 always-on + reliability)
**Gate:** APPROVE-WITH-DOC-UPDATE
**Iterations:** 1

**Cutover actions:**
1. `git mv harness harness-legacy` — Archived full PowerShell harness (107 files), preserving git history
2. Updated `harness-csharp/config.yaml` — `CollectorsPath`/`AnalyzersPath` paths updated to `harness-legacy/`
3. Created `.github/workflows/ci.yml` — xUnit CI pipeline (push/PR to main and feature/*)
4. Deleted 5 PS-specific files: `.PSScriptAnalyzerSettings.psd1`, `Invoke-Lint.ps1`, `Setup-DevEnvironment.ps1`, `.githooks/pre-commit`, `.githooks/pre-commit.ps1`
5. Created `harness-legacy/README.md` — Archive notice with step-by-step rollback instructions
6. Created git tag `ps-harness-final` marking the last commit with PowerShell harness in `harness/`

**Rollback plan (§10.5):**
- `harness-legacy/` contains the full, immediately-runnable PowerShell harness
- `ps-harness-final` git tag enables `git revert` or branch restoration
- `harness-legacy/README.md` documents the exact revert procedure

---

## Files Created

### Source / Config

| File | Description |
|---|---|
| `harness-csharp/config.yaml` | Engine defaults YAML (all 9 HoneConfig sections) |
| `sample-api/.hone/config.yaml` | SampleApi target config YAML |
| `harness-legacy/tests/fixtures/mock-target/.hone/config.yaml` | MockTarget fixture YAML |
| `harness-legacy/tests/fixtures/targets/happy-path/.hone/config.yaml` | HappyPath fixture YAML |
| `harness-legacy/tests/fixtures/targets/regression/.hone/config.yaml` | Regression fixture YAML |
| `harness-legacy/tests/fixtures/targets/test-failure/.hone/config.yaml` | TestFailure fixture YAML |
| `harness-legacy/tests/fixtures/targets/build-failure/.hone/config.yaml` | BuildFailure fixture YAML |
| `harness-legacy/tests/fixtures/targets/stacked-diffs/.hone/config.yaml` | StackedDiffs fixture YAML |
| `.github/workflows/ci.yml` | xUnit CI pipeline |
| `harness-legacy/README.md` | Archive notice with rollback instructions |
| `docs/features/csharp-migration/migration-summary.md` | Migration completion summary |

### Tests

| Test File | Tests |
|---|---|
| `ConfigYamlMigrationTests.cs` | 10 (config parity validation) |
| `EndToEndValidationTests.cs` | 7 (E2E validation) |
| **Total new** | **17** |

### Files Modified

| File | Change |
|---|---|
| `ConfigLoader.cs` | Added type mappings for YamlDotNet interface deserialization |
| `Hone.Integration.Tests.csproj` | Added Hone.Measurement and Hone.Reporting references |
| `Hone.Reporting.csproj` | Added InternalsVisibleTo for Hone.Integration.Tests |
| `docs/architecture.md` | Full rewrite for C# harness |
| `docs/getting-started.md` | Updated for .NET 10 + CLI |
| `docs/configuration.md` | Full rewrite for YAML |
| `docs/plugin-contracts.md` | Rewritten with C# interfaces |
| `docs/adapter-contracts.md` | Updated for YAML + BuiltIn hooks |
| `docs/agent-designs.md` | Added IAgentRunner architecture |
| `.github/copilot-instructions.md` | Full rewrite for C# stack |
| `README.md` | Updated for C# harness + YAML |

### Files Removed

| File | Reason |
|---|---|
| `.PSScriptAnalyzerSettings.psd1` | PowerShell lint config — replaced by .editorconfig + C# analyzers |
| `Invoke-Lint.ps1` | PowerShell lint runner — no longer needed |
| `Setup-DevEnvironment.ps1` | PowerShell setup script — replaced by `dotnet build`/`dotnet test` |
| `.githooks/pre-commit` | PS lint git hook shell wrapper |
| `.githooks/pre-commit.ps1` | PS lint git hook implementation |

### Files Renamed (archived)

| Original | Archived |
|---|---|
| `harness/` (107 files) | `harness-legacy/` |

---

## Critic Review Summary

| Slice | Critics Run | Initial Gate | Final Gate | Iterations |
|---|---|---|---|---|
| 1 (Config Migration) | 5 (4 always-on + parity-heavy) | REJECT | APPROVE | 2 |
| 2 (E2E Validation) | 5 (4 always-on + test-strategy) | APPROVE | APPROVE | 1 |
| 3 (Documentation) | 4 (4 always-on) | APPROVE-WITH-DOC-UPDATE | APPROVE | 1 |
| 4 (Cutover) | 5 (4 always-on + reliability) | APPROVE-WITH-DOC-UPDATE | APPROVE | 1 |

---

## Approved Deviations

1. **eShopOnWeb not migrated:** The phased plan §10.1 lists `eShopOnWeb/.hone/config.psd1` for conversion. This project does not exist in this repository — it was referenced as a planned future target. No YAML config was created. If eShopOnWeb is added to this repo, its `.hone/config.psd1` should be created as `.hone/config.yaml` following the same pattern as `sample-api/.hone/config.yaml`.

2. **Prepare hook mapped to `Command` type instead of `BuiltIn`:** The PS `Script` hook type for `Prepare` was mapped to `Type: Command` with `Value: 'pwsh -NonInteractive -File .hone\hooks\prepare.ps1'` rather than `Type: BuiltIn`. This is because no C# `PrepareHook` implementation exists yet. The `Command` type correctly delegates to the existing PS script. When a native `PrepareHook` is implemented, the YAML can be updated to `Type: BuiltIn`.

3. **`HarnessTesting` section omitted from YAML configs:** The PS fixture configs include a `HarnessTesting` section for the PowerShell test runner. This section has no C# equivalent — the C# integration tests use mock-based testing through `ILoopPipeline`/`IImplementerPipeline`. The section is intentionally omitted.

4. **ConfigLoader enhanced with type mappings:** The `ConfigLoader` required `WithTypeMapping` calls for `IReadOnlyList<string>` → `List<string>`, `IReadOnlyDictionary<string, CollectorSettingsEntry>` → `Dictionary<string, CollectorSettingsEntry>`, etc. This is a backwards-compatible enhancement — existing tests continue to pass, and the new mappings enable YAML deserialization of dictionary and list properties from disk files.

5. **Collector/analyzer plugin .psd1 files not converted to YAML:** The PS collector manifests (`collector.psd1`) and analyzer manifests (`analyzer.psd1`) are metadata files for the PS plugin framework. The C# diagnostic plugin system (Phase 6) uses built-in C# types (`CpuHotspotsAnalyzer`, `MemoryGcAnalyzer`, etc.) instead of plugin discovery via manifest files. The .psd1 manifests are preserved in `harness-legacy/` for reference.

---

## Validation Results

- **Build:** 0 warnings, 0 errors (full solution)
- **Tests:** 629 total (612 baseline + 17 new)
  - `Hone.Core.Tests`: 187 (+10 ConfigYamlMigration tests)
  - `Hone.Integration.Tests`: 21 (+7 EndToEndValidation tests)
  - All other test projects: unchanged
- **YAML config loading:** All 8 YAML configs load successfully via `ConfigLoader.Load()`
- **Config merge:** Engine YAML + target YAML → correct merged HoneConfig (target overrides win, engine defaults preserved)
- **CI pipeline:** `.github/workflows/ci.yml` targets `harness-csharp/Hone.slnx` with restore → build → test
- **Git tag:** `ps-harness-final` marks the last commit with PS harness in `harness/`

---

## Risks

1. **Prepare hook requires PowerShell at runtime:** The `Prepare` hook in target configs uses `Type: Command` with a `pwsh` invocation of `prepare.ps1`. This means the C# harness still depends on PowerShell being installed for the Prepare lifecycle hook. A native C# `PrepareHook` should be implemented to fully remove the PowerShell dependency.

2. **Plugin directory paths point to harness-legacy:** The engine defaults YAML has `CollectorsPath: "harness-legacy/collectors"` and `AnalyzersPath: "harness-legacy/analyzers"`. These are default values; targets should override them. If the legacy directory is ever removed, these defaults will break.

3. **No true E2E test with `hone` CLI binary:** The `FullLoop_DryRun` integration test uses the `HoneLoopRunner` directly with mocked pipeline interfaces. No test exercises the actual `hone run --target sample-api --dry-run` command through `Program.cs` → config loading → service registration → pipeline execution. This would require building the CLI and running it as a subprocess.

4. **sample-api submodule contains untracked config.yaml:** The `sample-api/.hone/config.yaml` was created inside the submodule directory. If sample-api is an external submodule, this file may need to be committed separately in that repository.

5. **Stub CLI commands remain:** `hone baseline`, `hone results`, and `hone dashboard` are stubs (from Phase 9). They print "not yet implemented" messages. These should be wired up with result file loading infrastructure.

---

## Migration Complete

Phase 10 is the **final phase** of Hone's PowerShell-to-C# migration. The migration is now complete:

| Metric | Before (PowerShell) | After (C#) |
|---|---|---|
| **Harness** | 46 PowerShell scripts (8,799 LOC) | 15 C# projects (Hone.slnx) |
| **Tests** | Pester test suite | 629 xUnit tests across 16 projects |
| **Config** | `.psd1` (PowerShell data files) | `.yaml` (YAML with PascalCase keys) |
| **CLI** | `Invoke-HoneLoop.ps1 -TargetPath` | `hone run --target <path>` |
| **Runtime** | PowerShell 7.2+ | .NET 10 |
| **CI** | (none configured) | `.github/workflows/ci.yml` (xUnit) |
| **Observability** | `Write-HoneLog` (file) | `HoneEventBus`/`IHoneEventSink` (structured events) |

The PowerShell harness is preserved in `harness-legacy/` and tagged at `ps-harness-final` for rollback if needed.
