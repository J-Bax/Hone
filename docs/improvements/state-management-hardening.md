# State Management Hardening Plan

> Date: 2026-04-18
> Scope: `harness-csharp/` loop state, git state, queue state, recovery, and cleanup
> Trigger: sample-api baseline + 10-experiment session exposed branch lifecycle and cleanup weaknesses

---

## Executive Summary

Hone's loop is already deterministic at the control-flow level, but its state model is still implicit. Durable state is spread across several surfaces:

- in-memory `LoopState`
- `run-metadata.json`
- `experiment-queue.json`
- current git branch and worktree contents
- result directories and hook side effects

This works on the happy path, but it breaks down when a step fails mid-flight or when the process has to recover from a partially completed experiment.

The session on 2026-04-18 exposed two concrete issues:

1. The first run crashed after experiment 2 because rollback assumed `hone/experiment-1` existed even though the change had been committed on the wrong branch.
2. The successful rerun still ended with a dirty worktree on `hone/experiment-7`, with file modifications from rejected experiments 8-10 left behind locally.

The central design change should be:

**Move from implicit state inferred from side effects to explicit durable state plus a hardened single-worktree branch lifecycle.**

This document now recommends **Option A**. A local worktree per experiment would add too much operator confusion, too many local copies, and too much cleanup surface for a loop that is already hard to reason about. The right fix is to keep one canonical working tree and make branch creation, cleanup, rollback, and recovery explicit and durable.

The first implementation slice should stay intentionally small: one authoritative run-state file, SHA-based git invariants, deterministic rejected-experiment cleanup, and a startup `repair_required` gate. Extra control-plane layers such as journals and multiple new managers should be deferred unless that smaller slice proves insufficient.

---

## What This Session Showed

| Observation | Current behavior | Why it is a problem |
|---|---|---|
| Rollback crashed on missing branch | `HoneLoopRunner` assumed the prior accepted branch existed and tried to check it out during failure handling | Branch state is not authoritative or validated before rollback |
| Failure handling relies on `git reset --soft HEAD~1` | Rejected experiments are "reverted" by rewinding the last commit in the current worktree | Soft reset leaves staged or modified files behind and leaks local state across experiments |
| Final local state drifted from durable metadata | `run-metadata.json` said experiments 8-10 were rejected and reverted, but the local worktree still contained modified files from those attempts | The durable record and the physical repo state can disagree |
| Resume reconstructs only part of the truth | `ResumeState` rebuilds the loop from `run-metadata.json` and assumes accepted experiments are enough to infer the current branch | It cannot represent "experiment 8 was half-finished" or "queue item 2 is leased" |
| Control state is split across owners | Queue manager owns queue, loop runner owns metadata, failure handler mutates git, hooks manage processes | There is no single place that can answer "what state is the run in right now?" |

---

## Current State Surfaces

| State surface | Owner today | Durability | Weakness |
|---|---|---|---|
| `LoopState` in memory | `HoneLoopRunner` | Process lifetime only | Lost on crash; partially rebuilt later |
| `run-metadata.json` | `HoneLoopRunner` via `LoopPipelineAdapter` | Durable | Reporting-oriented, not transaction-oriented |
| `experiment-queue.json` | `OptimizationQueueManager` | Durable | Tracks queue status, but not full experiment ownership or recovery intent |
| Git branch / `HEAD` | `GitVersionControl` and loop side effects | Durable | Used as control state without explicit validation |
| Working tree modifications | Side effect of implementer and revert logic | Ephemeral | Can leak across branches and survive failed cleanup |
| Target process / base URL | Lifecycle hooks | Ephemeral | Not represented in recoverable run state |

---

## Design Goals

1. **Single authoritative control-plane state.** One durable document should answer the current run status without having to infer it from git.
2. **Crash-safe recovery.** A crash between any two steps should be resumable or safely abortable.
3. **No dirty stable worktree.** The branch the loop continues from must stay clean after accepted and rejected experiments.
4. **Explicit experiment ownership.** Queue leases, branches, cleanup manifests, and result directories should all tie back to one experiment record.
5. **Idempotent failure handling.** Re-running cleanup or recovery should not make state worse.
6. **Operator-visible invariants.** It should be easy to explain why a run cannot continue and what needs repair.

### Non-goals

- Redesigning the agent pipeline
- Replacing `run-metadata.json` as the user-facing result summary
- Solving cross-run learning or the knowledge-base problem

---

## Design Options

### Option A: Harden the single shared worktree (recommended)

Keep the current single-worktree model, but add:

- a durable state manifest
- startup invariant checks
- explicit branch existence validation
- hard cleanup after rejection
- better recovery and queue leasing

**Pros**

- Smaller refactor
- Keeps current git model mostly intact
- Lower migration risk

**Cons**

- The stable branch and the active experiment still share a worktree
- Cleanup mistakes can still contaminate the next experiment
- Recovery logic remains more complex because local dirt matters

### Option B: Per-experiment git worktrees (not recommended for now)

Each experiment runs in its own git worktree created from the current stable branch. The canonical run state points to:

- the current stable branch
- the active experiment branch
- the active worktree path
- the leased queue item

**Pros**

- Rejected experiments no longer need in-place rollback on the continuation worktree
- Accepted and rejected experiments become easy to archive without polluting the stable branch
- Recovery logic becomes much simpler because the stable worktree can remain clean
- This opens the door to future parallel experiments

**Cons**

- Creates many local copies of the repo state during long runs
- Makes it harder for operators to answer "which copy is the real one?"
- Increases local cleanup complexity and disk churn
- Adds another layer of state to recover after interruption

For the current harness, these costs outweigh the isolation benefit. Option B is still a possible future escape hatch, but it should not be the primary redesign.

---

## Recommended Design

### 1. Introduce an authoritative run-state document

Add a new control-plane file:

`sample-api/.hone/results/metadata/run-state.json`

This should be the authoritative run state. `run-metadata.json` remains the user-facing experiment history, but it should no longer be used as the primary source for recovery decisions.

Suggested top-level shape:

```json
{
  "schemaVersion": 1,
  "runId": "2026-04-18T08-23-34Z",
  "targetName": "SampleApi",
  "stableBranch": "hone/experiment-7",
  "stableHeadSha": "3d1dfbf",
  "status": "candidate_committed",
  "currentExperiment": {
    "number": 8,
    "queueItemId": "2",
    "branchName": "hone/experiment-8",
    "baseBranch": "hone/experiment-7",
    "candidateHeadSha": "8b7a123",
    "cleanupManifestPath": ".hone/results/metadata/cleanup/experiment-8.json",
    "phase": "candidate_committed",
    "startedAt": "2026-04-18T10:16:17Z"
  }
}
```

Key rule:

- `run-state.json` is the control-plane source of truth
- `run-metadata.json` is the reporting-plane summary
- `run-state.json` owns the active queue lease for the current experiment

When the run is idle, `HEAD` must match `stableBranch` **and** `stableHeadSha`. Branch names alone are not enough because they do not prove the right commit is checked out.

### 2. Defer a state journal until the smaller control plane is proven

Do **not** add `state-journal.jsonl` in the first slice.

The current problem is too many state surfaces already drifting apart. Adding a journal creates another durable surface before the primary control plane has been stabilized. Existing JSONL event logging can continue to serve debugging needs, while `run-state.json` remains the only recovery authority.

If a journal is needed later, it should be added as an observability aid rather than as part of the minimal recovery design.

### 3. Replace implicit phases with a small recovery-state model

Use a minimal set of recovery-relevant states:

1. `idle`
2. `experiment_leased`
3. `branch_created`
4. `candidate_committed`
5. `finalizing`
6. `repair_required`

Important rule:

Persist state only at **recovery boundaries**, not after every interesting event. The state model is for recovery decisions, not detailed telemetry.

For example:

- write `experiment_leased` after the queue item is assigned
- write `branch_created` after checkout succeeds
- write `candidate_committed` after the candidate commit SHA is known
- write `finalizing` before cleanup starts
- clear back to `idle` only after the stable branch is verified clean

This keeps the control plane small while still distinguishing the states that imply different repair actions.

### 4. Extend existing components instead of adding a branch coordinator

Do not introduce `ExperimentBranchCoordinator` in the first slice.

Fold the needed behavior into existing components:

- `GitVersionControl` gains:
  - branch existence checks
  - current `HEAD` SHA lookup
  - worktree clean/dirty checks
  - restore tracked paths from a branch
  - remove listed untracked paths
- `HoneLoopRunner` owns run-state transitions and startup/reentry validation
- `ExperimentFailureHandler` owns rejected-experiment cleanup and verification

The important design choice is to verify both branch name and commit SHA at each recovery boundary, instead of inferring correctness from metadata alone.

### 5. Redefine rejection as explicit cleanup plus archival

Current behavior couples two concerns:

- preserving a rejected branch and PR for inspection
- cleaning the local repo so the loop can continue

Those should be separated.

Recommended rejected-experiment flow:

1. Run the experiment on `hone/experiment-N`
2. If it regresses, persist a cleanup manifest containing:
   - experiment number
   - branch name
   - base branch
   - candidate commit SHA
   - tracked files touched by the experiment
   - untracked artifact paths created by the experiment
3. Derive the touched-file set from the actual git diff vs `stableBranch`, not from the queue item
4. Switch back to `stableBranch`
5. Restore tracked files from `stableBranch` for the touched-file set
6. Clear staged changes and remove experiment-owned untracked files according to policy
7. Verify that `HEAD == stableBranch@stableHeadSha` and the working tree is clean
8. Mark the experiment finalized/rejected locally
9. Optionally push the rejected branch and create a PR

Local correctness must not depend on remote publication succeeding. The key point is that cleanup should be path-based and verified, not a blind `git reset --soft HEAD~1`.

### 6. Make lease ownership unambiguous

Today, `OptimizationQueueManager` can mark an item `InProgress`, but the full recovery semantics are weak because the lease is only partially represented.

The active lease should live in `run-state.json`, not be duplicated across multiple authoritative stores.

Keep these fields under `currentExperiment`:

- `leaseRunId`
- `leaseExperiment`
- `leasePhase`
- `leasedAt`

On startup:

- if a queue item is `in_progress` but no matching `run-state.json` entry exists, it should be repairable
- if `run-state.json` references an active lease but the queue does not, the run should enter `repair_required`
- `experiment-queue.json` should only describe queue item status/result, not own the live lease

### 7. Add startup invariant checks

Before starting or resuming a run, check:

1. `stableBranch` exists locally
2. If `status == idle`, `HEAD` must equal `stableBranch` and `HEAD` SHA must equal `stableHeadSha`
3. If `currentExperiment` exists, the actual branch and `HEAD` SHA must match the recorded phase expectations
4. The stable worktree is clean before the next lease is issued
5. There is exactly zero or one active lease, and it matches `run-state.json`
6. `run-state.json` and `run-metadata.json` do not disagree on the last finalized experiment
7. There is no leftover staged or modified source owned by a previously finalized rejected experiment

If any invariant fails, the harness should stop with a state-specific diagnostic and move the run to `repair_required` instead of continuing optimistically.

### 8. Make metadata writes atomic and versioned

`OptimizationQueueManager` already uses temp-file + move for queue JSON. `SaveRunMetadataAsync` should follow the same pattern.

Recommended changes:

- temp file + move for `run-metadata.json`
- temp file + move for `run-state.json`
- include `schemaVersion` in both
- optionally include `updatedAt` and `writtenByRunId`

This reduces the chance of partial writes during interruption.

### 9. Start recovery inside `HoneLoopRunner`

Do not introduce `RunRecoveryManager` in the first slice.

Instead:

- `HoneLoopRunner` should load `run-state.json` at startup
- perform invariant checks before leasing new work
- decide whether to resume, finalize, abort, or stop in `repair_required`

If this logic later becomes too large, it can be extracted. For now, keeping it near the existing orchestration flow is simpler and reduces new abstraction layers.

### 10. Add explicit cleanup behavior to existing components

Cleanup should have its own contract, not be an incidental consequence of branch checkout.

The first slice should extend existing components instead of adding a standalone cleanup subsystem:

- `ExperimentFailureHandler` handles rejected-experiment finalization
- `GitVersionControl` provides low-level restore / remove / verify operations
- `HoneLoopRunner` decides state transitions and when cleanup is required

Each should be idempotent and safe to re-run.

---

## Proposed Ownership Model

| Concern | Recommended owner |
|---|---|
| Stable branch pointer and stable SHA | `RunStateStore` |
| Current experiment phase and active lease | `RunStateStore` |
| Queue item status/result | `OptimizationQueueManager` |
| Branch validation, restore, and cleanup primitives | `GitVersionControl` |
| Rejected-experiment cleanup flow | `ExperimentFailureHandler` |
| User-facing history | `run-metadata.json` |
| Resume and repair decisions | `HoneLoopRunner` |

This keeps each component focused and makes state ownership explicit.

---

## Suggested New Components

### New records

- `RunStateDocument`
- `CurrentExperimentState`
- `CleanupManifest`

### New services

- `IRunStateStore`
- `RunStateStore`

### Likely refactor targets

- `HoneLoopRunner`
- `ExperimentFailureHandler`
- `OptimizationQueueManager`
- `LoopPipelineAdapter`
- `GitVersionControl`

---

## Phased Implementation Plan

### Phase 1: Low-risk hardening

Goal: stop the obvious state leaks without redesigning the entire loop.

1. Add `run-state.json` with `stableBranch`, `stableHeadSha`, active lease, `candidateHeadSha`, and a small recovery-state enum
2. Extend `GitVersionControl` with branch existence, `HEAD` SHA, clean-check, restore-path, and remove-untracked helpers
3. Replace `git reset --soft HEAD~1` with manifest-based rejected-experiment cleanup plus post-cleanup verification
4. Add a startup/reentry gate in `HoneLoopRunner` that stops in `repair_required` on invariant mismatch
5. Make `run-state.json` and `run-metadata.json` writes atomic

### Phase 2: Durable recovery model

Goal: make interrupted runs resumable.

1. Make `run-state.json` the sole owner of the active lease
2. Support resuming or aborting `experiment_leased`, `branch_created`, `candidate_committed`, and `finalizing`
3. Emit clearer `repair_required` diagnostics and operator guidance
4. Keep `ResumeState` focused on summary metrics/history only

### Phase 3: Transactional single-worktree cleanup

Goal: make the shared working tree safe and predictable.

1. Add a cleanup manifest for touched tracked files and experiment-owned artifacts
2. Restore `stableBranch` deterministically before optional rejected-branch publication
3. Verify clean or expected-dirty state after every finalize path
4. Separate source cleanup policy from artifact retention policy

### Phase 4: Operator tooling

Goal: make state issues visible and repairable.

1. Add a `hone doctor state` command
2. Add a `hone repair state` command for safe automated repairs
3. Add richer results/debug views for queue leases, active branch state, cleanup manifests, and recovery decisions
4. Re-evaluate whether an append-only state journal is still needed

---

## Testing Strategy

The state model should be tested with crash injection, not just happy-path unit tests.

### Unit tests

- recovery-state transition validation
- invariant checker
- queue lease ownership rules
- cleanup manifest derivation
- SHA-based branch validation
- atomic write helpers

### Integration tests

- crash after branch creation, before metadata write
- crash after queue lease, before branch preparation
- crash after candidate commit, before cleanup
- wrong branch / wrong SHA detected at startup
- rejected experiment leaves stable worktree clean
- push or PR failure does not block local cleanup

### End-to-end tests

- run 3-5 experiments with forced regressions and verify:
  - stable worktree is clean after each rejection
  - `run-state.json`, queue state, and `run-metadata.json` remain consistent
  - resume works after process kill

---

## Recommended First Slice

If only one short implementation slice is taken first, it should be:

1. Add `run-state.json` with `stableHeadSha` and `candidateHeadSha`
2. Extend `GitVersionControl` with validation and restore helpers
3. Replace soft reset with cleanup-manifest-based restore to `stableBranch`
4. Add a `HoneLoopRunner` startup gate that validates state and enters `repair_required` on mismatch

That sequence gives immediate reliability gains without introducing a second layer of local repo copies.

---

## Bottom Line

Hone's biggest remaining state-management weakness is that the loop still treats git side effects as if they were the state model. That is why branch mistakes surface late, rejected experiments leak local file changes, and recovery requires inference.

The fix is not more defensive `if` statements around checkout. The fix is to:

- make run state explicit,
- make transitions durable,
- make the shared working tree an explicitly managed resource, and
- treat recovery as a designed feature instead of a best-effort reconstruction step.

That will make the loop more reliable, easier to debug, and much safer to run for long unattended experiment chains. The minimal version of that design is one current durable state file plus strict git validation, not a stack of new recovery subsystems.
