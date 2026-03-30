# Feature: Harness Testing

> **Status:** Design · **Scope:** Hone harness self-testing, regression prevention, and E2E verification
> **Motivation:** Hone is evolving quickly, but the harness itself lacks a durable testing model that lets agents change orchestration logic safely.

---

## Problem Statement

Hone optimizes external targets, but the harness itself is still tested mostly at the helper and contract level. That leaves a large regression surface unprotected:

- lifecycle ordering can drift
- queue behavior can regress
- branch and PR semantics can change subtly
- artifact layouts can break downstream scripts
- accept/reject logic can change without a clear contract
- stacked-diff behavior can become inconsistent across experiments

As agents iterate on the harness, they need a way to prove that the harness still behaves like Hone, not just that a few helper functions still return expected values.

This feature introduces a **harness-testing framework**: a first-class way to test Hone's orchestration, contracts, and end-to-end behavior using deterministic fixtures, invariant checks, and scenario-driven runs.

---

## Goals

1. Make the harness safe to evolve by giving agents a clear regression suite.
2. Define the **contracts that must remain true** even as features change.
3. Prefer **end-to-end and scenario-based tests** over narrow implementation tests.
4. Keep tests deterministic enough to run in automation and by agents.
5. Reuse the current target-centric design instead of inventing a parallel test-only architecture.

## Non-Goals

- Replacing real target testing with mocks only
- Locking the harness to today's exact script boundaries or file names
- Preventing new features from changing behavior intentionally
- Testing third-party tools like `git`, `k6`, or `copilot` beyond Hone's integration contract with them

---

## Why This Matters

Hone is a deterministic orchestrator wrapped around probabilistic agents. That means the harness must enforce the stable rules of the system:

- what an optimization target is
- what an experiment is
- how results are recorded
- what acceptance and rejection mean
- how failed experiments are preserved
- how successful experiments become reviewable PRs

Without explicit contracts, future agents will optimize locally and regress globally.

---

## Core Design

The harness-testing feature should introduce a **three-layer verification model**:

1. **Contract tests** for stable interfaces like target config, hooks, plugin outputs, queue entries, and metric comparison.
2. **Orchestration tests** for phase sequencing, failure handling, branch management, and artifact staging.
3. **End-to-end scenarios** that run synthetic optimization loops against deterministic fixture targets and assert system-level outcomes.

The center of gravity should be layer 3. Hone's risk is not that a single helper fails in isolation; it is that the full loop stops behaving like a reliable optimization harness.

---

## Enduring Invariants and Contracts

These are the truths the harness-testing framework should encode. New features may extend them, but they should not silently violate them.

### 1. Optimization Target Contract

An optimization target is a repository with a `.hone\` directory that declares how Hone can operate on it.

The harness must continue to treat the target as a black box with explicit contracts:

- buildable source
- runnable application or equivalent start/ready lifecycle
- functional regression tests
- measurable load scenarios
- writable results directory
- declared lifecycle hooks for all required phases

This is the foundational contract from `docs\adapter-contracts.md`. Harness features may add new optional capabilities, but they should not remove the requirement that targets declare themselves through `.hone\config.psd1` and lifecycle hooks.

### 2. Experiment Contract

An experiment is a single attempted optimization with a durable record.

Regardless of how agents are added or changed, each experiment must preserve these properties:

- it has a unique experiment number
- it executes against a specific target and branch context
- it records its inputs, outputs, and outcome artifacts
- it ends in one of a bounded set of recognizable states such as improved, regressed, stale, build failed, test failed, or analysis/fix failure
- it is reproducible from saved artifacts closely enough for humans to understand what happened

### 3. Results Contract

Optimization results are not just console output. They are a persistent series of experiment artifacts and reviewable outcomes.

The following must remain true:

- results are written under the target's configured results area
- each experiment has a durable artifact set
- metrics used for decisions are preserved
- failure artifacts are preserved, not discarded
- successful experiments remain explainable after the fact

This matters because Hone is not just a code changer; it is a record-keeping optimization system.

### 4. Branch and PR Contract

Hone's publish model is part of the product, not a side effect.

The framework should preserve these invariants:

- every experiment executes on an experiment branch
- successful experiments produce reviewable PRs or PR-ready metadata
- failed experiments do not silently disappear
- in stacked-diffs mode, the chain remains linear and reviewable
- successful PRs are based on the last successful branch, not a failed one
- rejected experiments preserve their artifacts even if their code is reverted

The specific git plumbing may evolve, but the review model must remain stable.

### 5. Measure/Analyze/Verify Separation Contract

Hone must keep the system distinction between:

- diagnostic measurement for analysis
- evaluation measurement for accept/reject

The harness-testing framework should enforce that:

- profiling overhead does not contaminate accept/reject comparisons
- queue-driven analysis only reruns when required
- evaluation remains the final gate for performance claims
- E2E functional tests remain a hard safety gate before performance acceptance

### 6. Acceptance Contract

An experiment can only be accepted if:

- at least one accepted metric or efficiency criterion improves
- no gated metric regresses beyond policy
- functional regression tests pass
- the harness records the basis for the decision

An experiment must be rejected if:

- build fails
- functional tests fail
- performance regresses beyond configured tolerance
- required artifacts for decisioning are missing or invalid

### 7. Queue Contract

The optimization queue is a planning artifact between analysis and execution. The framework should preserve:

- queue items are durable and machine-readable
- items are consumed one at a time
- depleted queues trigger fresh analysis
- completed or rejected opportunities are not retried blindly
- queue writes are atomic enough to survive interruption without corrupting state

### 8. Hook and Plugin Contract

Hook and plugin systems are extension points and therefore high-risk for regressions.

The following must stay true:

- all declared hooks resolve deterministically
- hook types preserve their execution semantics
- plugin discovery is contract-based, not hardcoded to specific implementations
- collector and analyzer outputs match documented shapes
- failures surface clearly and stop or reject work according to phase semantics

### 9. Artifact Contract

Humans and downstream scripts must be able to find the same classes of artifacts regardless of future feature work:

- prompts and agent responses
- build and test output
- k6 summaries
- diagnostic summaries
- metadata about the run, branch, and outcome

Paths may evolve, but artifact categories must remain durable and documented.

### 10. Determinism Contract for Testing

The self-test framework itself must be trustworthy. That means tests should be able to run with deterministic substitutes for non-deterministic dependencies:

- synthetic or fixture k6 outputs
- mock agent responses
- fixture targets
- fault injection for build, test, queue, and publish failures

This is not a production behavior change. It is a requirement for reliable harness verification.

---

## What the Feature Should Add

### A. Harness Test Fixtures as First-Class Targets

The framework should define one or more fixture targets that behave like real `.hone` targets but are optimized for deterministic testing.

Recommended fixture classes:

- **happy-path target**: build, tests, and metrics all succeed
- **regression target**: build and tests pass, metrics regress
- **build-failure target**: generated change causes compilation failure
- **test-failure target**: build passes, regression tests fail
- **queue target**: exercises queue refill and depletion behavior
- **stacked-diffs target**: exercises success/failure/success branch chains

These should be treated as genuine targets, not special-case shortcuts.

### B. A Scenario Runner for Harness Behavior

The framework should provide a scenario abstraction describing:

- starting state
- target fixture
- agent responses
- measurement outputs
- expected phase transitions
- expected final outcome
- expected artifacts and branch/PR effects

This is the backbone for E2E harness tests. It lets agents add new regression cases without hand-assembling a bespoke test harness every time.

### C. Invariant Assertions

The framework should expose reusable assertions for the contracts above, for example:

- experiment metadata exists and is internally consistent
- accepted runs have a valid improvement basis
- rejected runs preserve failure artifacts
- stacked-diff PR ancestry is correct
- queue state transitions are valid
- hook execution order matches declared lifecycle semantics

These assertions are more valuable than checking internal implementation details.

### D. Fault Injection

The framework should support intentionally triggering:

- build failures
- E2E test failures
- k6 parse failures
- missing artifacts
- queue corruption or interrupted writes
- agent timeout or malformed response
- publish failures

This is how Hone verifies its failure-handling contract, not just the happy path.

### E. Golden Artifact Validation

Where useful, the framework should compare normalized outputs against golden expectations for:

- run metadata shape
- queue item shape
- PR metadata shape
- artifact manifests

This should focus on stable semantics, not fragile byte-for-byte snapshots of noisy output.

---

## Preferred Test Strategy

The framework should bias toward the following mix:

### 1. Contract Tests

Keep and expand the current Pester tests for:

- config merge behavior
- hook resolution
- hook invocation
- plugin output validation
- metric comparison logic
- queue state transitions

These are cheap and high-signal.

### 2. Orchestration Tests

Add tests for:

- phase ordering
- stop/revert behavior
- outcome mapping
- branch selection
- artifact staging
- retry and resume behavior

These should run with mocked external dependencies but real orchestration code.

### 3. End-to-End Scenario Tests

This should be the primary investment. Each test should run a mini loop with a fixture target and assert the actual Hone outcome.

The most important property is not code coverage. It is **behavior coverage**.

---

## High-Value End-to-End Scenarios

The first version of the feature should cover at least these scenarios.

### Scenario 1: Happy Path Improvement

Expected behavior:

- analysis produces an optimization opportunity
- fixer applies a change
- build passes
- E2E tests pass
- evaluation metrics improve
- experiment is accepted
- PR metadata is created correctly
- artifacts are persisted

### Scenario 2: Build Failure Rejection

Expected behavior:

- change is applied
- build fails
- experiment is rejected or reverted according to mode
- failure artifacts are preserved
- no false success metadata is produced

### Scenario 3: Functional Regression Rejection

Expected behavior:

- build passes
- E2E tests fail
- experiment is rejected
- test output is preserved
- no performance acceptance occurs

### Scenario 4: Performance Regression Rejection

Expected behavior:

- build passes
- E2E tests pass
- evaluation metrics regress beyond tolerance
- experiment is rejected
- regression basis is recorded

### Scenario 5: Stale Outcome

Expected behavior:

- build passes
- E2E tests pass
- metrics do not improve and do not regress materially
- stale counters advance correctly
- stop conditions use the right policy

### Scenario 6: Queue Refill and Consumption

Expected behavior:

- analysis runs when queue is empty
- queue items are consumed one at a time
- analysis reruns only after depletion
- queue history prevents blind repetition

### Scenario 7: Stacked Diffs Success-Failure-Success Chain

Expected behavior:

- experiment 1 succeeds
- experiment 2 fails and is reverted
- experiment 3 succeeds
- experiment 3 is based on the last successful branch, not the failed branch
- preserved artifacts still explain experiment 2

### Scenario 8: Resume from Partial State

Expected behavior:

- an interrupted run can resume from persisted metadata
- counters, queue state, and branch ancestry remain coherent
- the harness does not double-count or skip experiments unexpectedly

### Scenario 9: Hook Contract Coverage

Expected behavior:

- Script, Shared, Command, Http, and Skip hooks all behave correctly
- hook failures surface clearly
- lifecycle ordering remains valid

### Scenario 10: Agent and Tool Failure Handling

Expected behavior:

- malformed agent output is surfaced
- timeout paths clean up correctly
- missing or malformed measurement output does not create a false improvement
- publish failures do not corrupt experiment accounting

---

## Proposed Deliverables

The feature should eventually produce:

1. A shared harness-testing module with reusable fixture and assertion helpers.
2. A suite of deterministic fixture targets under the existing test area.
3. Scenario definitions for core harness behaviors.
4. E2E tests that run the harness against those scenarios.
5. Contract tests for stable interfaces and decision logic.
6. CI integration so harness changes are gated by these tests.
7. Documentation telling future agents which contracts are safe to extend and which must remain stable.

---

## Suggested Repository Shape

This feature should build on existing patterns under `harness\tests\` and `harness\test-fixtures\`.

One reasonable end-state shape is:

```text
harness\
  tests\
    contracts\
    orchestration\
    integration\
    resilience\
    fixtures\
      targets\
        happy-path\
        build-failure\
        test-failure\
        regression\
        stacked-diffs\
  test-fixtures\
    agent-responses\
    k6-results\
    diagnostics\
```

The exact layout can vary, but the important part is separating:

- reusable fixtures
- scenario definitions
- contract tests
- full-loop tests

---

## Phased Implementation Plan

### Phase 1: Define the Contracts Explicitly

Create the canonical harness-testing contract document and assertion vocabulary.

Deliverables:

- written invariants for targets, experiments, results, queue, hooks, and PR behavior
- a stable list of experiment outcomes
- a documented artifact manifest for accepted and rejected experiments
- a mapping of current tests to missing contract coverage

Exit criteria:

- the team can point to a single source of truth for what must never regress
- future agents have a checklist for behavior-preserving changes

### Phase 2: Establish Deterministic Fixtures

Build fixture targets and mock external outputs needed for reliable tests.

Deliverables:

- fixture targets for happy path, build failure, test failure, regression, and stacked-diffs flows
- mock agent responses
- fixture k6 summaries and diagnostic outputs
- helper utilities to stage and clean fixture runs

Exit criteria:

- the harness can run repeatable test scenarios without depending on live agent quality or noisy load tests

### Phase 3: Expand Contract and Orchestration Tests

Broaden Pester coverage around stable seams and orchestration rules.

Deliverables:

- tests for metric comparison and acceptance policy
- tests for queue transitions and atomic persistence
- tests for branch ancestry decisions
- tests for artifact staging and outcome recording
- tests for lifecycle ordering and hook failures

Exit criteria:

- contract regressions fail quickly without needing a full E2E run

### Phase 4: Add Core End-to-End Scenarios

Implement the first E2E suite that runs the actual harness flow against deterministic fixtures.

Deliverables:

- happy path scenario
- build failure scenario
- functional regression scenario
- performance regression scenario
- stale scenario

Exit criteria:

- a harness PR that breaks the core loop semantics fails in automation

### Phase 5: Add Stateful and Recovery Scenarios

Cover the behaviors most likely to break during continued agent iteration.

Deliverables:

- queue depletion and refill scenario
- stacked-diffs success/failure/success scenario
- resume-from-partial-state scenario
- publish failure and timeout scenarios

Exit criteria:

- branch history, queue state, and outcome accounting remain stable across interruptions and failures

### Phase 6: Integrate into CI and Agent Workflow

Make the framework a normal part of harness development rather than a one-off test suite.

Deliverables:

- CI job for harness contract and E2E tests
- documented guidance for adding new scenarios
- clear "required before merge" expectations for harness changes

Exit criteria:

- agents changing the harness can run a known test battery and prove they did not regress the system

### Phase 7: Grow with New Features

Treat every new harness feature as a new scenario or invariant addition.

Deliverables:

- scenario templates for new features
- a rule that new orchestration behavior ships with at least one end-to-end regression case
- periodic review of whether current invariants still capture Hone's product identity

Exit criteria:

- the test framework evolves with Hone instead of lagging behind it

---

## Implementation Principles

When this feature is built, the implementation should follow these principles:

1. **Prefer behavioral assertions over implementation assertions.**
2. **Use real harness entry points whenever practical.**
3. **Mock only the unstable or expensive boundaries.**
4. **Preserve target-centric design; do not add a separate fake execution model.**
5. **Make failures explainable through saved artifacts.**
6. **Keep the framework easy for agents to extend with new scenarios.**

---

## Success Criteria

This feature is successful when:

- agents can modify the harness and verify full-loop behavior before shipping
- regressions in branch handling, queue semantics, acceptance policy, or artifact preservation are caught automatically
- the meaning of an optimization target, experiment, and optimization result remains stable across future feature work
- Hone's core identity as a performance optimization harness stays intact even as the implementation evolves

---

## Summary

Hone needs a self-testing system that treats the harness as a product with durable contracts, not just a set of scripts.

The central idea is simple:

- define the invariants
- encode them as reusable assertions
- run the harness end to end against deterministic fixture targets
- make those scenarios the safety net for all future harness work

That gives agents a practical way to iterate on Hone while preserving the behaviors that make Hone recognizable: target-centric execution, measurable optimization, strict regression gates, preserved experiment history, and optimization results delivered as a reviewable series of PRs.
