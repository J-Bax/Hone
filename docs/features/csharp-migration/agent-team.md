# C# Migration Delivery Agent Team

> **Status:** Approved  
> **Date:** 2026-04-04  
> **Companion Documents:** [proposal.md](proposal.md) - migration rationale and target architecture; [phased-plan.md](phased-plan.md) - phase-by-phase implementation plan

---

## Purpose

This document defines the **custom agent team used to execute Hone's PowerShell-to-C# migration**.

It is intentionally separate from Hone's runtime optimization agents documented in [../../agent-designs.md](../../agent-designs.md). Those agents optimize target APIs at runtime; this team exists to deliver the migration project itself.

---

## Decision

Hone should start with an **MVP custom team**, not:

- unmanaged general-purpose agents acting on their own, or
- the full end-state worker and critic swarm on day one.

This preserves Hone's existing design principle of **deterministic orchestration with focused agents**, while keeping the migration process simple enough to debug and evolve.

---

## Operating Model

The migration team follows a **manager + workers + critic coordinator** pattern:

1. A lead orchestrator selects the next migration slice from the approved phased plan.
2. A worker agent implements that bounded slice.
3. A critic coordinator routes the result through the relevant reviewers.
4. Reviewers return one of three outcomes:
   - `approve`
   - `approve-with-doc-update`
   - `reject`
5. Rejections go back to the same worker agent for correction.

The orchestrator owns progress tracking, dependencies, and final gate decisions. Workers do the implementation. Critics do not directly edit production code.

---

## MVP Team (Initial Operating Model)

### Control Plane

| Agent | Responsibility |
| --- | --- |
| `hone-migration-orchestrator` | Owns backlog sequencing, work-package selection, progress tracking, and handoffs. |
| `hone-migration-critic-coordinator` | Runs the review flow, merges critic output, and returns a single gate decision to the worker and orchestrator. |

### Worker Agents

| Agent | Initial Scope |
| --- | --- |
| `hone-migration-bootstrap` | Solution scaffolding, shared build settings, CI, and baseline test infrastructure. |
| `hone-migration-core` | Core models, config, contracts, utilities, and observability. |
| `hone-migration-loop-host` | Orchestration entry points, CLI host, reporting integration, and end-to-end migration glue. |

### Always-On Critics

| Agent | Why It Is Always On |
| --- | --- |
| `hone-migration-design-conformance-critic` | Keeps implementation aligned to `proposal.md` and `phased-plan.md`. |
| `hone-csharp-correctness-critic` | Guards semantic correctness, contract preservation, and nullability/logic issues. |
| `hone-migration-parity-critic` | Ensures the migration remains behavior-preserving against the PowerShell baseline, fixtures, and golden outputs. |
| `hone-csharp-scope-critic` | Prevents API-surface growth and enforces tight visibility (`public` vs `internal` vs `private`). |

### On-Demand Critics During MVP

These are part of the planned team, but they only run when the touched area or risk profile warrants them.

| Agent | Trigger Conditions |
| --- | --- |
| `hone-csharp-concurrency-critic` | Async flows, shared state, file/process coordination, cancellation, retries. |
| `hone-migration-reliability-critic` | Rollback, cleanup, timeout handling, long-running orchestration, partial failure recovery. |
| `hone-csharp-performance-critic` | Hot paths, parsing, serialization, process execution, measurement, queue logic. |
| `hone-migration-security-process-critic` | Tool invocation, path handling, secret boundaries, command construction, temp files. |
| `hone-csharp-maintainability-critic` | New abstractions, cross-project contracts, service boundaries, complexity spikes. |
| `hone-migration-test-strategy-critic` | Test ports, fixtures, validation checkpoints, parity coverage, golden-output checks. |

---

## Implemented Agent Definition Files

The MVP team and specialist critics are defined in `.github/agents/`:

- `hone-migration-orchestrator.agent.md`
- `hone-migration-critic-coordinator.agent.md`
- `hone-migration-bootstrap.agent.md`
- `hone-migration-core.agent.md`
- `hone-migration-loop-host.agent.md`
- `hone-migration-design-conformance-critic.agent.md`
- `hone-csharp-correctness-critic.agent.md`
- `hone-migration-parity-critic.agent.md`
- `hone-csharp-scope-critic.agent.md`
- `hone-csharp-concurrency-critic.agent.md`
- `hone-migration-reliability-critic.agent.md`
- `hone-csharp-performance-critic.agent.md`
- `hone-migration-security-process-critic.agent.md`
- `hone-csharp-maintainability-critic.agent.md`
- `hone-migration-test-strategy-critic.agent.md`

---

## Full End-State Team

The MVP is the starting point, not the ceiling. Once the pilot reveals recurring bottlenecks, the worker and critic set expands.

### Full Worker Set

| Agent | Expanded Scope |
| --- | --- |
| `hone-migration-bootstrap` | Phase 0 |
| `hone-migration-core` | Phase 1 |
| `hone-migration-measurement` | Phase 2 |
| `hone-migration-lifecycle-sourcecontrol` | Phases 3-4 |
| `hone-migration-agent-integration` | Phases 5-6 |
| `hone-migration-loop-host` | Phases 7-10 |

### Full Critic Set

- `hone-migration-design-conformance-critic`
- `hone-csharp-correctness-critic`
- `hone-migration-parity-critic`
- `hone-csharp-scope-critic`
- `hone-csharp-concurrency-critic`
- `hone-migration-reliability-critic`
- `hone-csharp-performance-critic`
- `hone-migration-security-process-critic`
- `hone-csharp-maintainability-critic`
- `hone-migration-test-strategy-critic`

---

## Review Contract

Every worker handoff should include:

- migration phase and component
- target files or modules
- relevant design references from `proposal.md` and `phased-plan.md`
- known risks and open questions
- explicit note of any intentional deviation from the current design docs

Every critic response should include:

- outcome: `approve`, `approve-with-doc-update`, or `reject`
- blocking findings only
- concrete remediation guidance
- whether the problem is in the code, the docs, or both

---

## Design-to-Implementation Deviation Policy

The design-conformance critic is responsible for deciding which of these applies:

1. **Implementation is wrong** - worker must change the code to match the design.
2. **Design is outdated** - implementation is reasonable; docs must be updated in the same slice.
3. **Both are incomplete** - worker must tighten the implementation and update the docs together.

This policy prevents silent drift between the migration design and the delivered C# codebase.

---

## Routing Rules

- Always run the four always-on critics in the MVP flow.
- Add specialist critics only when the changed code crosses their risk boundary.
- Prefer a small number of bounded worker agents over many narrow workers until repeated overload appears.
- General-purpose agents are allowed as helpers, but only under orchestrator control and only for bounded tasks.

---

## Rollout Sequence

1. Define the MVP team in custom agent specs and docs.
2. Pilot the MVP on Phase 0 or Phase 1 work.
3. Track which review failures recur.
4. Promote the most valuable on-demand critics into regular use.
5. Expand to the full worker set when the MVP starts to overload.

---

## Recommended First Pilot Slice

The first pilot should use the MVP team on **Phase 0: Solution Scaffolding**.

| Role | Agent |
| --- | --- |
| Orchestrator | `hone-migration-orchestrator` |
| Worker | `hone-migration-bootstrap` |
| Always-on critics | `hone-migration-design-conformance-critic`, `hone-csharp-correctness-critic`, `hone-migration-parity-critic`, `hone-csharp-scope-critic` |
| Likely on-demand critics | `hone-migration-reliability-critic`, `hone-migration-test-strategy-critic`, `hone-migration-security-process-critic` |

Phase 0 is the best pilot because it exercises scaffolding, structure, naming,
CI, and validation wiring without forcing the full behavioral parity burden of
later orchestration phases.

---

## Success Condition for This Team Design

The migration delivery agent system is working if it:

- keeps work packages aligned to the approved phased migration plan,
- catches design drift before it compounds,
- preserves behavioral parity with the PowerShell baseline, and
- scales from the MVP team to the fuller review/workforce model only when the project proves it needs it.
