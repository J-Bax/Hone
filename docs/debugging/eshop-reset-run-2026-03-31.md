# eShopOnWeb Reset and Baseline Investigation - 2026-03-31

## Summary

- Request: fully reset `OptimizationTargets\eShopOnWeb-Honed`, recalculate a new baseline, and run 10 experiments.
- Reset completed before investigation: closed stale experiment PRs, removed local/remote `hone/experiment-*` branches, and cleared generated `results\` artifacts while preserving the local `.hone\config.psd1` diagnostics override and unrelated untracked files.
- The first fresh baseline attempt failed in the `Active` lifecycle hook before baseline artifacts were written.
- Failure mode: `Invoke-ScaleTests.ps1` reported `The term 'Start-Spinner' is not recognized...`.

## Evidence

Baseline attempt output:

```text
[00:02:03] Running k6 scenario: C:\Projects\Hone-Group\OptimizationTargets\eShopOnWeb-Honed\.hone\scenarios\baseline.js against http://localhost:59255
[00:02:03] Running warmup pass
Exception: Lifecycle hook 'Active' failed: k6 scale tests error: The term 'Start-Spinner' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

## Investigation

- `harness\Invoke-ScaleTests.ps1` dot-sources `harness\Show-Progress.ps1` and then calls `Start-Spinner` / `Stop-Spinner` during warmup and measured k6 runs.
- Direct reproductions of both `harness\hooks\k6-run.ps1` and the full `Invoke-Hook.ps1` dispatch path later succeeded, so the missing-helper failure appears intermittent or execution-context-specific rather than a consistently broken code path.
- Even if the helper-loading issue is transient, the harness should not abort a baseline or experiment because a terminal progress helper failed to load.

## Fix

The initial "load `Show-Progress.ps1` and define fallback functions" hardening was
not sufficient. The real issue was that helper lookup by command name was not
reliable in the baseline execution path.

`harness\Invoke-ScaleTests.ps1` was updated to:

1. Load `Show-Progress.ps1` best-effort.
2. Capture `Start-Spinner` / `Stop-Spinner` as scriptblocks when available.
3. Use direct scriptblock invocation for progress updates.
4. Fall back to simple informational output if progress helpers are still
   unavailable.

This removes the runtime dependence on command-name resolution during warmup and
measured k6 runs, which was the actual failure mode.

## Validation

- `.\Invoke-Lint.ps1` passed cleanly after the final fix.
- The eShop baseline was rerun successfully.
- Baseline artifacts were regenerated under `OptimizationTargets\eShopOnWeb-Honed\results\`.

### Primary baseline metrics

| Metric | Value |
|--------|-------|
| p95 latency | 1013.880795 ms |
| RPS | 114.9 |
| Error rate | 0% |
| CPU avg | 0.52% |
| GC heap max | 171.31 MB |

### Additional scenario baselines

| Scenario | p95 | RPS |
|----------|-----|-----|
| `stress` | 1020.0543 ms | 371.7 |
| `stress-catalog` | 1014.34268 ms | 132.3 |
| `stress-auth` | 99.147695 ms | 190.7 |
| `warmup` | 1017.0866 ms | 16.4 |

## Follow-up Issue - Experiment Build Locks

The first full 10-experiment loop after the baseline did not produce valid
results. Experiments 1-3 all failed during the build phase, but the actual
problem was not the generated code.

### What happened

`dotnet build` failed with `MSB3027` / `MSB3021` while copying
`ApplicationCore.dll`, `Infrastructure.dll`, and `BlazorShared.dll` into
`src\PublicApi\bin\Release\net8.0\`.

### Evidence

Build logs for experiments 1 and 2 showed the same lock owner:

```text
The process cannot access the file ...\src\PublicApi\bin\Release\net8.0\ApplicationCore.dll
because it is being used by another process.
The file is locked by: "PublicApi (2449784), PublicApi (2449332)"
```

At the time of investigation, multiple orphaned `PublicApi.exe` processes were
still running from the target checkout.

### Root cause

The harness experiment path relied on the tracked `_Process` reference for
cleanup, but stale target processes can survive outside that reference chain.
When that happens, experiment builds race against still-running `PublicApi`
instances that lock the output assemblies.

### Fix

Two changes were applied:

1. `harness\hooks\dotnet-stop.ps1` now discovers and stops target-related
   processes by project path, not just the tracked `_Process`.
2. `harness\Invoke-IterativeFix.ps1` now invokes the target `Stop` hook before
   each attempt's build so experiment builds start from a clean target process
   state.

### Validation

- The strengthened `dotnet-stop` hook successfully killed stale eShop target
  processes during verification.
- A subsequent process check returned no remaining `PublicApi` processes.
- Harness lint was rerun after the cleanup fix.

## Next Step

Clear the invalid experiment chain from the current rerun attempt
(`hone/experiment-1` through `hone/experiment-3`, PRs `#4` through `#6`, and
partial `experiment-4` artifacts), then restart
`Invoke-HoneLoop.ps1 -TargetPath <eShop target> -MaxExperiments 10` from the
clean post-baseline state under the fixed comparison logic.

## Follow-up Issue - Zero-Baseline Error Acceptance

The next clean rerun got past the stale-process build locks and produced valid
experiment execution, but it exposed a new harness correctness bug in result
comparison.

### What happened

Experiment 1 was accepted correctly, but experiment 2 was also accepted even
though the primary verification run recorded a `28.57%` request error rate.
That should have been a hard regression relative to experiment 1's `0%` errors.
The loop was stopped during experiment 4 so the invalid acceptance could be
fixed before more bad results accumulated.

### Evidence

The experiment 2 verification log showed a non-zero error rate being treated as
no error regression against the previous experiment:

```text
[01:19:45] vs baseline: 99.9% | p95: 1.295625ms (-43% vs prev) | RPS: 345.9 (1% vs prev) | Errors: 28.57% (0% vs prev)
[01:19:45] Improvement detected in at least one metric
[01:19:50] Pull request created: https://github.com/J-Bax/eShopOnWeb-Honed/pull/5
```

By experiment 3, that invalid accepted state became the new reference:

```text
[01:19:50] Reference metrics from experiment 2: p95=1.295625ms, RPS=345.9
[01:19:50] vs baseline: 99.9% | p95: 1.295625ms (0% vs prev) | RPS: 345.9 (0% vs prev) | Errors: 28.57% (0% vs prev)
```

### Root cause

`harness\Compare-Results.ps1` used a shared `Get-PctChange` helper that
returned `0` whenever the reference value was `0`.

That behavior is harmless for some metrics, but it is incorrect for error-rate
gating. When the previous error rate was `0` and the current experiment had any
non-zero error rate, the change was reported as `0%`, so the run was not marked
as regressed even though requests were failing.

### Fix

`Get-PctChange` now treats zero-reference transitions explicitly:

1. `0 -> 0` remains `0%` change.
2. `0 -> positive` is treated as the maximum positive delta (clamped to
   `1000%`, consistent with the helper's existing bounds).
3. `0 -> negative` is treated as the maximum negative delta defensively.

This allows the existing regression gates in `Compare-Results.ps1` to reject
non-zero error rates correctly without special-case branching elsewhere.

### Validation

- Added a unit test in `harness\tests\Compare-Results.Tests.ps1` covering the
  exact `0% -> 28.57%` error-rate regression case.
- `Invoke-Pester` passed for:
  - `harness\tests\Compare-Results.Tests.ps1`
  - `harness\tests\Invoke-HoneLoop.Tests.ps1`
- `.\Invoke-Lint.ps1` passed after the fix, with only the pre-existing warning
  in `harness\Invoke-CompatibilityAgent.ps1`.

## Follow-up Issue - Stacked Branch Publish Corruption

The corrected 10-experiment rerun completed all measurements, but the later
experiments were not published correctly.

### What happened

The final loop metadata showed all 10 experiments completed, but only the first
three experiments had real remote branches and PRs. Experiments 4 through 10
were recorded in `results\run-metadata.json`, yet no matching
`hone/experiment-4` through `hone/experiment-10` branches or PRs existed on the
remote repository.

The target checkout was also left on local `hone/experiment-3`, with the later
experiment commits stacked directly on that branch.

### Evidence

The live target repository showed only the first three experiment branches on
the remote, while local `hone/experiment-3` contained the rest of the run:

```text
## hone/experiment-3...origin/hone/experiment-3 [ahead 12]
* ec13a52 (HEAD -> hone/experiment-3) hone(experiment-10): Replace AutoMapper with manual mapping in paged catalog endpoint
* 00f212b hone(experiment-9): revert — regressed
* 2289364 hone(experiment-9): Use AnyAsync instead of CountAsync for duplicate name check
* d40a079 hone(experiment-8): revert — regressed
* dbbdd9e hone(experiment-8): Eliminate string round-trip in PageCount calculation
* b191be8 hone(experiment-7): Use DbContext pooling to reduce allocation and GC pressure
...
* 051814e (origin/hone/experiment-3) hone(experiment-3): revert — regressed
```

The harness log also showed push failures being followed by success-shaped
messages and failed PR creation:

```text
WARNING: git push failed for revert branch: hone/experiment-8 (exit code 1)
[03:39:08] Revert committed and pushed on branch: hone/experiment-8
Failed to create pull request for rejected experiment 8: GraphQL: Head sha can't be blank, Base sha can't be blank, No commits between main and hone/experiment-8
```

At the same time, the target working tree contained generated runtime files that
change on every experiment:

- `results\run-metadata.json`
- `results\metadata\experiment-queue.json`
- `results\metadata\experiment-queue.md`
- `results\experiment-*\build.log`

### Root cause

There were two tightly coupled bugs in the stacked-diffs publish flow:

1. `harness\Apply-Suggestion.ps1` attempted
   `git checkout -b <branch> <base>`, then fell back to `git checkout <branch>`
   if that failed, but it never verified the branch that was actually checked
   out.
2. Dirty generated runtime artifacts under the target results directory could
   block branch creation. When that happened, the fallback reused the previously
   checked-out branch, so experiments 4 through 10 were committed onto local
   `hone/experiment-3`.
3. `Invoke-HoneLoop.ps1` and `Revert-ExperimentCode.ps1` logged push success and
   attempted PR creation even when `git push` had failed, which hid the real
   failure until the live repository was audited.

### Fix

The stacked-diffs publish path was hardened in four places:

1. `harness\Apply-Suggestion.ps1`
   - merges the target config before resolving the generated results path
   - snapshots the full generated runtime state under the results directory
   - resets that state before branch creation and restores it after checkout
   - verifies the current branch after create/switch and fails fast on branch,
     stage, or commit errors
2. `harness\HoneHelpers.psm1`
   - adds a shared `Invoke-ExperimentBranchPush` helper
   - reuses the same publish-definition resolution for fixture PR creation and
     branch pushes
3. `harness\Invoke-HoneLoop.ps1`
   - only creates experiment PRs after a successful branch push
   - stops logging false publish success when pushes fail
4. `harness\Revert-ExperimentCode.ps1`
   - validates the current branch before revert
   - distinguishes between pushed and local-only revert results

### Validation

- Added regression coverage in:
  - `harness\tests\Apply-Suggestion.Tests.ps1`
  - `harness\tests\Invoke-FailureHandler.Tests.ps1`
- Re-ran the focused publish-flow suite successfully:
  - `Apply-Suggestion.Tests.ps1`
  - `Invoke-FailureHandler.Tests.ps1`
  - `Revert-ExperimentCode.Tests.ps1`
  - `Invoke-HoneLoop.Tests.ps1`
  - Result: `Tests Passed: 6, Failed: 0`
- Re-ran `.\Invoke-Lint.ps1` successfully after the final cleanup. It passed
  with `0 error(s)` and only the pre-existing warning in
  `harness\Invoke-CompatibilityAgent.ps1`.

### Recovery decision

In-place repair of the already-corrupted live run was judged higher risk than a
fresh rerun. The live target had only PRs `#7`, `#8`, and `#9`, but local
`hone/experiment-3` was already `12` commits ahead with experiments 4 through 10
interleaved onto that branch.

Rather than attempting to surgically split and republish those commits, the
safer recovery path is:

1. preserve unrelated local target changes such as `.hone\config.psd1` and
   `.github\agents\`
2. close the remaining experiment PRs and delete experiment branches
3. reset the target back to a clean baseline state
4. rerun the baseline and the full 10-experiment loop with the fixed harness

## Final recovery outcome

The clean rerun completed successfully after the publish-flow fixes.

### Refreshed baseline

| Metric | Value |
|--------|-------|
| p95 latency | 1014.90584 ms |
| RPS | 114.9 |
| Error rate | 0% |
| CPU avg | 0.41% |
| GC heap max | 171.65 MB |

Additional scenario baselines captured successfully:

| Scenario | p95 | RPS |
|----------|-----|-----|
| `stress` | 1017.57986 ms | 376.9 |
| `stress-catalog` | 1014.514115 ms | 132.3 |
| `stress-auth` | 100.919375 ms | 191.0 |
| `warmup` | 1014.97299 ms | 16.5 |

### Final loop result

- Exit reason: `max_experiments`
- Experiments run: `10`
- Successful improvements: `6 / 10`
- Best experiment: `10`
- Best p95: `1.72314 ms`
- Total p95 improvement vs baseline: `99.8%`

### Final experiment outcomes

| Experiment | Outcome | p95 | RPS | PR |
|-----------|---------|-----|-----|----|
| 1 | improved | 1.9661 ms | 341.0 | `#10` |
| 2 | improved | 2.17126 ms | 343.1 | `#11` |
| 3 | improved | 1.77454 ms | 341.4 | `#12` |
| 4 | stale | 2.1598 ms | 341.0 | `#13` |
| 5 | improved | 1.8235 ms | 341.7 | `#14` |
| 6 | stale | 1.834125 ms | 341.1 | `#15` |
| 7 | stale | 1.9954 ms | 341.0 | `#16` |
| 8 | improved | 1.80614 ms | 341.7 | `#17` |
| 9 | no queue items | — | — | — |
| 10 | improved | 1.72314 ms | 343.7 | `#18` |

Experiment 9 did not create a branch or PR because the remaining queued item was
classified as an architecture-level change and skipped. The loop then continued
to experiment 10 with a fresh analysis pass, which is the expected behavior.

### Final publication state

The repaired harness published distinct remote branches and PRs for all
actionable experiments in the rerun:

- accepted PRs: `#10`, `#11`, `#12`, `#14`, `#17`, `#18`
- rejected PRs: `#13`, `#15`, `#16`
- remote branches present: `hone/experiment-1`, `2`, `3`, `4`, `5`, `6`, `7`,
  `8`, and `10`

This confirms the stacked-diffs branch/publish corruption from the earlier run
was fixed and did not recur during the clean rerun.
