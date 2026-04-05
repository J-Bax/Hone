# C# Migration Retrospective

Reflections from orchestrating Phases 4–10 of the PowerShell-to-C# migration across ~7 hours and ~700+ tool calls. These learnings would have reduced friction, saved time, and improved quality if applied from the start.

## Process & Orchestration

### 1. Git config over prompt rules

The `--no-gpg-sign` commit rule was violated in nearly every phase despite being stated explicitly in every agent prompt. Agents would attempt a signed commit, get exit code 128, then retry. Setting `commit.gpgsign=false` in the repo's `.gitconfig` (or a branch-scoped config) would have eliminated an entire class of failures without relying on prompt compliance.

### 2. Missing agent definitions caused silent fallbacks

Three workers (`hone-migration-lifecycle-sourcecontrol`, `hone-migration-measurement`, `hone-migration-agent-integration`) were defined in the agent-team spec but never created as agent files. Phases 2–6 all silently fell back to `hone-migration-core`. Phase 0 should have scaffolded all agent definitions — even as stubs — so routing worked as designed from the start.

### 3. The meta→orchestrator→worker→critic chain is 4 levels deep

Each layer consumed context re-reading the same large documents (`proposal.md`, `phased-plan.md`, `agent-team.md`, prior implementation records). By Phase 10, the prior records alone were substantial. A condensed "migration state" artifact — updated per phase with just current status, conventions, and blockers — would have saved thousands of tokens per agent launch.

### 4. Phases were too serial

The dependency graph technically allows parallelism (e.g., Phase 4 and Phase 5 share only Phase 1 as a dependency; Phase 7 only needs Phases 1+2). Running independent phases concurrently could have cut wall-clock time significantly. The meta-orchestrator should have analyzed the dependency graph and launched parallel phases where safe.

## Technical

### 5. Documentation was deferred entirely to Phase 10

Phase 10 had to rewrite 9 documents covering work from Phases 1–9 that the Phase 10 agent hadn't implemented itself. Incremental doc updates per phase (even just updating `architecture.md` with new modules as they were added) would have been more accurate, less error-prone, and spread the documentation burden.

### 6. Test count parsing was fragile

Verification relied on regex-parsing `dotnet test` console output. A single malformed line (two test results concatenated on one line) produced a wrong count (431 vs the actual 629). Using `dotnet test --logger "trx"` or `--results-directory` for machine-readable results from the start would have been reliable and unambiguous.

### 7. Config conversion (.psd1→YAML) was cleanly bounded but late

Moving config conversion earlier would have let integration tests in Phases 8–9 use real YAML configs instead of test fixtures, catching format issues sooner and giving better end-to-end confidence.

## Agent Design

### 8. The critic review contract added value but was expensive

Every slice ran 4+ critics (design-conformance, correctness, parity, scope). Phases with clean, well-understood patterns (e.g., Phase 7 reporting) rarely had rejections — lighter review would have been fine. Phase 8 (orchestration) genuinely needed heavy review and had meaningful critic rejections. Adaptive critic intensity based on slice complexity would be more efficient.

### 9. "Smallest reviewable slice" varied wildly

Some slices were 2 files; others were 10+. The guidance should have included a rough LOC or file-count ceiling per slice to keep review tractable and review quality consistent.

### 10. Fresh agents lose institutional knowledge

Each phase agent started cold. Patterns discovered in Phase 4 (e.g., how to structure `I*` interfaces with internal implementations, test fixture conventions, project reference patterns) had to be re-derived in later phases. A shared "conventions learned" document updated per phase would have accelerated later agents.

## What Worked Well

- **The phased plan was excellent** — clear scope boundaries prevented scope creep across all 11 phases
- **Critic rejections caught real issues** — Phase 8's HoneLoopRunner and Phase 10's config parity reviews found genuine problems
- **Test count grew monotonically** (320 → 362 → 423 → 491 → 540 → 599 → 612 → 629) with zero regressions across 7 phases
- **The `ps-harness-final` tag** for rollback was a smart cutover safety net
- **Breaking Phase 8 into 5 small slices** with the heaviest critic set paid off — it was the most complex phase and had the most iterations
- **Implementation records per phase** created a clear audit trail and gave each subsequent agent useful context

## Summary

| Category | Key Takeaway |
|----------|-------------|
| Environment | Encode rules in config, not just prompts |
| Scaffolding | Create all agent/tool definitions upfront, even as stubs |
| Context | Maintain a compact rolling state doc across phases |
| Parallelism | Analyze the dependency graph; run independent phases concurrently |
| Documentation | Update docs incrementally, not in a final batch |
| Verification | Use machine-readable output formats for validation |
| Review | Scale critic intensity to slice complexity |
| Knowledge | Accumulate conventions in a shared artifact |
