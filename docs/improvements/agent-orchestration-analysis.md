# Agent Orchestration Analysis — Industry Guidance vs. Hone

> **Date:** 2026-03-28 · **Scope:** Hone agent pipeline, orchestration patterns, and harness architecture
> **Sources:** Anthropic "Building Effective Agents" (2025), Anthropic Multi-Agent Research System (2025), OpenAI Agents SDK guidance (2025–2026), OpenAI Codex Subagents

---

## Executive Summary

Hone's architecture already embodies several best practices from Anthropic and OpenAI's latest guidance — most notably **deterministic orchestration with focused agents**, **structured data flow**, and **measurement-first reasoning**. However, the industry has evolved significantly since Hone's design was established. This document identifies gaps between Hone's current patterns and the state-of-the-art, then proposes concrete improvements organized into a phased refactoring plan.

---

## Part 1: Industry Guidance Summary

### Anthropic — "Building Effective Agents"

Anthropic's flagship guidance emphasizes **simplicity and composability** over monolithic agent designs. Key patterns:

| Pattern | Description | When to Use |
|---------|-------------|-------------|
| **Prompt Chaining** | Sequential prompts where each step's output feeds the next | Well-defined multi-step tasks |
| **Routing** | Classify input → dispatch to specialized handler | Tasks with distinct categories |
| **Evaluator-Optimizer** | Generator produces output; evaluator critiques; loop refines | Quality-critical outputs with clear criteria |
| **Orchestrator-Workers** | Lead agent decomposes task → spawns parallel subagents → synthesizes results | Complex tasks requiring breadth |
| **Parallelization** | Multiple agents work simultaneously on independent subtasks | Independent subtask decomposition |

**Core Principles:**
1. **Start simple.** Only add complexity when simpler patterns fail. Most effective agents are "a few lines of code."
2. **Deterministic orchestration, probabilistic reasoning.** Code controls flow; LLMs handle judgment calls.
3. **Clear tool interfaces.** Agent effectiveness is bounded by tool quality and documentation.
4. **Context is everything.** Missing context causes unpredictable behavior. Provide all essential information upfront.
5. **Explicit delegation.** Each subagent needs crystal-clear objectives, output format, and scope boundaries.

### Anthropic — Multi-Agent Research System

Anthropic's production multi-agent system (Claude Research) demonstrates the **orchestrator-worker** pattern at scale:

- A **lead agent** receives the query, plans strategy, and decomposes into subtasks
- **Subagents** operate in independent context windows, working in parallel
- Each subagent returns compressed findings; the lead agent synthesizes
- 90.2% performance improvement over single-agent baseline on complex tasks
- Dynamic scaling: subagent count adapts to query complexity

**Key engineering lessons:**
- Explicit, non-overlapping delegation prevents redundant work
- Separate context windows bypass single-agent context limits
- Output compression from subagents keeps synthesis manageable
- Token cost is 15× single-agent — justified only for high-value tasks

### OpenAI — Agents SDK & Orchestration Patterns

OpenAI's guidance emphasizes two core orchestration patterns:

**A. Agents-as-Tools (Manager Pattern)**
- Central manager agent delegates to specialist sub-agents invoked as tools
- Manager handles synthesis, context, and guardrails
- Best when centralized quality control is needed

**B. Handoffs (Decentralized/Peer Pattern)**
- Router agent passes control to specialists who become the active agent
- Each specialist customizes its interaction and model
- Best for specialized interactions requiring different prompts/models

**Cross-cutting best practices:**
1. **LLM-driven vs. code-driven orchestration.** Blend both — LLMs for intelligent decisions, code for deterministic steps.
2. **Guardrails at every boundary.** Input, output, and context guardrails enforce safety and quality.
3. **Modular specialization.** Specialized sub-agents limit error blast radius and improve maintainability.
4. **Tracing and observability.** Built-in logging of every agent step, tool call, and failure event.
5. **Start with one agent, scale to many.** Over-engineering the initial design creates more bugs.

---

## Part 2: How This Applies to Hone

### What Hone Already Does Well

Hone's architecture aligns strongly with several core recommendations:

| Industry Best Practice | Hone's Implementation | Assessment |
|------------------------|----------------------|------------|
| Deterministic orchestration | PowerShell scripts control all flow; agents only reason | ✅ Exemplary — matches Anthropic's #1 lesson learned |
| Focused agents over monoliths | 5 specialized agents (cpu-profiler, memory-profiler, analyst, classifier, fixer) | ✅ Strong — tiered analysis was a deliberate improvement |
| Structured data everywhere | JSON schemas for all agent I/O, typed PowerShell objects | ✅ Solid |
| Measurement-first reasoning | PerfView profiling + k6 metrics drive agent prompts | ✅ Core design principle |
| Plugin architecture | Collectors/analyzers as drop-in directories | ✅ Good extensibility |
| Timeout enforcement | `Invoke-CopilotAgent.ps1` with configurable timeouts | ✅ Implemented |
| Retry logic with prompt augmentation | JSON parse retry with strict formatting reminders | ✅ Implemented |
| DryRun/mock support | `-MockResponsePath` for fast iteration without API calls | ✅ Implemented |

### Where Hone Diverges from Current Guidance

#### 1. Single-Shot Fixer (No Evaluator-Optimizer Loop)

**Gap:** Both Anthropic and OpenAI emphasize the **evaluator-optimizer** pattern — where a generator produces output and a critic/evaluator refines it in a loop. Hone's fixer is single-shot: it generates code, and if it fails build/test, the experiment is immediately rejected.

**Impact:** High. Many valid optimizations fail on first attempt due to minor compilation errors or missed edge cases — issues that are easily correctable with error feedback. The single-shot pattern discards potentially valuable optimizations.

**Industry pattern:** Evaluator-Optimizer (Anthropic), Actor-Critic loops (OpenAI), iterative refinement with error feedback.

#### 2. Sequential Diagnostic Analysis (No Parallelization)

**Gap:** Anthropic's multi-agent research system shows that parallel subagent execution dramatically improves both throughput and coverage. Hone's diagnostic analyzers (CPU profiler, memory profiler) run sequentially, even though they operate on independent data and have no dependencies on each other.

**Impact:** Medium. Each analyzer invokes an LLM call with timeout (default 600s). Running two analyzers sequentially doubles the wall-clock time of the diagnostic phase. With additional analyzers (e.g., thread contention, I/O profiling), this becomes a bottleneck.

**Industry pattern:** Orchestrator-Workers with parallelization (Anthropic), parallel tool use (OpenAI).

#### 3. No Formal Guardrails on Agent Output

**Gap:** OpenAI's guidance strongly recommends guardrails at input, output, and context boundaries. Hone has ad-hoc validation (JSON parse retry, JavaScript literal sanitization, scope classification), but no systematic guardrail framework that validates agent outputs against schemas, detects hallucinated file paths, or catches unsafe code patterns.

**Impact:** Medium. The fixer agent can generate code that references non-existent namespaces, introduces security vulnerabilities, or makes changes outside the declared scope. These are caught downstream by compilation and tests, but a pre-execution guardrail could catch them earlier and cheaper.

**Industry pattern:** Output guardrails (OpenAI SDK), critic agents as review gates (Anthropic).

#### 4. Limited Context Management Across the Loop

**Gap:** Anthropic emphasizes that context management is critical — agents need all essential information, but context windows are finite. Hone's `Build-AnalysisContext.ps1` assembles five sections, but the optimization history grows unboundedly. There is no summarization, pruning, or relevance-ranking strategy for the history context as experiments accumulate.

**Impact:** Medium-High. After 30+ experiments, the history section can consume a significant portion of the context window, reducing the quality of analysis. The `future-extensions.md` acknowledges this need for the knowledge base but hasn't addressed it for the existing history context.

**Industry pattern:** Context budget management, summarization/pruning strategies, retrieval-augmented context (Anthropic).

#### 5. No Cross-Experiment Learning (Knowledge Base)

**Gap:** Both Anthropic and OpenAI recommend that agent systems learn from prior executions. Hone tracks optimization history (what was tried, accept/reject), but doesn't extract *patterns* or *lessons* from outcomes. The `future-extensions.md` describes a knowledge base and COE system but they remain unimplemented.

**Impact:** High. Without pattern-level learning, the analyst repeats classes of mistakes (e.g., three failed caching attempts before learning that caching under write contention always regresses on this codebase). The flat history tells the agent "caching failed" but not "why caching fails here."

**Industry pattern:** Persistent knowledge accumulation, pattern extraction, COE analysis (Anthropic), session context stores (OpenAI).

#### 6. No Dynamic Agent Scaling or Routing

**Gap:** Anthropic's multi-agent system dynamically scales subagent count based on query complexity. OpenAI recommends routing to different specialists based on input characteristics. Hone uses a fixed 5-agent pipeline regardless of the optimization's nature — a simple index addition goes through the same pipeline as a complex caching refactor.

**Impact:** Low-Medium. For simple optimizations, the full pipeline (diagnostic profiling → CPU analysis → memory analysis → analyst → classifier → fixer) is overkill. For complex optimizations, a single fixer pass is insufficient. The pipeline doesn't adapt to task complexity.

**Industry pattern:** Routing (Anthropic), handoffs (OpenAI), dynamic agent selection.

#### 7. Observability and Tracing Gaps

**Gap:** OpenAI's guidance emphasizes integrated tracing and observability. Hone has structured logging via `Write-HoneLog.ps1` and saves agent prompts/responses as artifacts, but there's no unified trace view that connects the full experiment lifecycle — from analysis prompt to final accept/reject decision — into a single queryable record.

**Impact:** Low-Medium. Debugging why an experiment failed requires manually correlating log entries, prompt files, response files, and comparison results across multiple directories.

**Industry pattern:** Integrated tracing (OpenAI SDK), step-by-step transparency (Anthropic).

---

## Part 3: Critique of Hone's Current Approach

### Strengths

1. **Deterministic orchestration is the right call.** Hone's architecture document explicitly explains why (Principle #7): early attempts at agent-orchestrated-agents failed beyond 2-3 iterations. This aligns perfectly with Anthropic's top recommendation. The PowerShell harness provides reliable, debuggable flow control while agents handle the judgment calls.

2. **Tiered analysis is well-designed.** The split from one monolithic analyzer to CPU profiler → memory profiler → top-level analyst mirrors Anthropic's "focused agents over monoliths" pattern. Each agent has a clear scope, schema, and role boundary.

3. **The plugin architecture is forward-thinking.** Collector and analyzer plugins with autodiscovery is exactly the kind of modular extensibility that both Anthropic and OpenAI recommend for scaling agent capabilities.

4. **Queue-driven analysis avoids waste.** Only running the full analysis pipeline when the queue is empty is an efficient pattern — it avoids re-analyzing when there are already actionable items.

### Weaknesses

1. **The single-shot fixer is Hone's biggest gap.** This is acknowledged in `future-extensions.md` (Iterative Fixer + Actor-Critic), but remains unimplemented. Given that both Anthropic and OpenAI highlight evaluator-optimizer loops as a core pattern, and Hone's own data shows experiments rejected for fixable compilation errors, this is the highest-impact improvement.

2. **Context management is a ticking time bomb.** The history context grows linearly with experiments. After the sample API's 35 experiments, the history section is substantial. On a larger codebase with 100+ experiments, it will degrade analysis quality. There's no summarization, windowing, or relevance filtering.

3. **No learning between runs.** Each Hone run starts fresh (besides git history). If you run Hone on the same target twice, it may retry approaches that failed before. The optimization history is scoped to a single run, and there's no persistent knowledge that transfers across runs.

4. **Sequential diagnostic analysis wastes wall-clock time.** With PerfView profiling already splitting into separate passes (CPU and GC can't run together), the *analysis* of those passes should at least run in parallel since the data is independent.

5. **No scope-aware pipeline routing.** A simple "add database index" optimization goes through the same heavyweight pipeline as a "redesign the caching layer" refactor. The classifier determines scope *after* analysis, but the fixer doesn't adapt its strategy based on complexity.

---

## Part 4: Proposed Improvements

### Improvement 1: Iterative Fixer with Error Feedback Loop

**What:** Replace the single-shot fixer with a retry loop. On build or test failure, feed the error output back to the fixer agent with the original goal and current file content.

**Why:** Directly implements the evaluator-optimizer pattern. Expected to recover 30-50% of currently rejected experiments that fail due to minor compilation or test errors.

**Design:**
- Configurable retry budget (default: 3 attempts)
- Each retry includes: original goal, current file content, full error output
- Diff size monitoring to detect scope creep across iterations
- Test file modification guard (reject any diff touching test files)
- Detailed per-iteration logging for debugging

### Improvement 2: Actor-Critic Review Gate

**What:** Add a critic agent between the fixer and measurement phases. The critic reviews the diff for correctness, scope adherence, and potential issues before committing to the expensive load-test cycle.

**Why:** Implements Anthropic's evaluator-optimizer pattern and OpenAI's output guardrail concept. Catches semantic issues that compile but are wrong, and prevents scope creep from narrow to architectural.

**Design:**
- Lightweight model (claude-haiku-4.5) since it's evaluating, not generating
- Structured rejection feedback that the fixer can act on
- Scope validation: reject if narrow-classified change touches multiple files or adds dependencies
- Composable with iterative fixer: critic rejection triggers a fixer retry, not experiment rejection

### Improvement 3: Parallel Diagnostic Analysis

**What:** Run analyzer agents (CPU profiler, memory profiler, future analyzers) concurrently instead of sequentially.

**Why:** Directly implements Anthropic's parallelization pattern. Each analyzer operates on independent data — there's no reason to serialize them.

**Design:**
- Use PowerShell `Start-Job` or `ForEach-Object -Parallel` (PS 7.2+) for concurrent analyzer execution
- Aggregate results after all analyzers complete (or timeout)
- Configurable parallelism limit for resource-constrained environments
- Error isolation: one analyzer failure doesn't block others

### Improvement 4: Context Budget Management

**What:** Implement a context windowing and summarization strategy for the optimization history injected into analyst prompts.

**Why:** Prevents context window degradation as experiments accumulate. Matches Anthropic's emphasis on context management as a critical success factor.

**Design:**
- Sliding window: include full detail for the last N experiments (default: 10)
- Compressed summary for older experiments (category, outcome, 1-line lesson)
- Token budget awareness: estimate context size before sending, truncate history if needed
- Prioritize recent failures and patterns over old successes

### Improvement 5: Persistent Optimization Knowledge Base

**What:** Implement the knowledge base system described in `future-extensions.md` — structured extraction of patterns and lessons from each experiment, persisted across runs.

**Why:** Enables cross-run learning. The analyst gets pattern-level understanding ("caching on write-heavy endpoints always regresses") rather than just experiment-level facts.

**Design:**
- JSON-based knowledge store persisted in the target's results directory
- Post-experiment extraction step (deterministic, not agent-based, for reliability)
- Categories: technique, target, outcome, failure mode, lessons
- Analyst prompt injection with relevance filtering
- Summarization for prompt budget management

### Improvement 6: Correction-of-Error Agent for Rejected Experiments

**What:** Implement the COE agent described in `future-extensions.md` — a post-rejection analysis that explains *why* an experiment failed, not just *that* it failed.

**Why:** COE outputs feed the knowledge base with high-quality failure analysis. This is the "why" behind the "what" — turning rejected experiments into learning opportunities.

**Design:**
- Runs asynchronously after experiment rejection (non-blocking)
- Inputs: analyst proposal, fixer diff, error output, metrics, comparison deltas
- Output: structured COE document with root cause, contributing factors, and lessons
- COE lessons feed both the knowledge base and the optimization history

### Improvement 7: Unified Experiment Tracing

**What:** Create a single, queryable trace record for each experiment that links all phases — analysis prompt, classification, fixer output, build result, test result, metrics, comparison, and outcome.

**Why:** Implements OpenAI's observability guidance. Enables rapid debugging and retrospective analysis of the optimization pipeline's effectiveness.

**Design:**
- Single JSON trace file per experiment: `experiment-N/trace.json`
- Timestamped entries for each phase transition
- Links to all artifact files (prompts, responses, metrics)
- Summary dashboard that can render trace data

---

## Part 5: Phased Refactoring Plan

### Phase 1 — Iterative Fixer (Highest Impact)

The single biggest improvement: recover experiments that currently fail due to fixable errors.

**Changes:**
- Modify `Invoke-FixAgent.ps1` to support retry loops with error feedback
- Add build/test error formatting for fixer retry prompts
- Add diff size monitoring and test-file-modification guards
- Add retry budget configuration to `config.psd1`
- Update `Invoke-HoneLoop.ps1` to use the new iterative fixer flow

**Dependencies:** None — can be implemented independently.

### Phase 2 — Parallel Diagnostic Analysis + Context Management

Two independent improvements that reduce wall-clock time and improve analysis quality.

**Changes (Parallelization):**
- Refactor `Invoke-DiagnosticAnalysis.ps1` to run analyzers concurrently
- Add error isolation per-analyzer
- Add parallelism configuration

**Changes (Context Management):**
- Refactor `Build-AnalysisContext.ps1` to implement sliding window + summary
- Add token budget estimation
- Add context management configuration

**Dependencies:** None — both independent of Phase 1.

### Phase 3 — Actor-Critic Review Gate

Layer a critic agent on top of the iterative fixer.

**Changes:**
- Create `Invoke-CriticAgent.ps1` and `hone-critic.agent.md`
- Integrate critic into the fixer retry loop (critic rejection → fixer retry)
- Add scope validation logic (diff analysis for narrow vs. architectural drift)
- Add critic configuration to `config.psd1`
- Update `Invoke-HoneLoop.ps1` experiment flow

**Dependencies:** Phase 1 (Iterative Fixer) — the critic is most valuable when the fixer can iterate on feedback.

### Phase 4 — Knowledge Base + COE Agent

Build persistent learning capabilities.

**Changes (Knowledge Base):**
- Create `Update-KnowledgeBase.ps1` for post-experiment extraction
- Create `Get-KnowledgeContext.ps1` for analyst prompt injection
- Add knowledge base schema and storage format
- Integrate into `Build-AnalysisContext.ps1`

**Changes (COE Agent):**
- Create `Invoke-CoeAgent.ps1` and `hone-coe.agent.md`
- Integrate COE output into knowledge base entries
- Add async execution so COE doesn't block the loop

**Dependencies:** Phase 2 (Context Management) — knowledge base injection needs context budget awareness.

### Phase 5 — Experiment Tracing + Observability

Comprehensive tracing across the full experiment lifecycle.

**Changes:**
- Create `Write-ExperimentTrace.ps1` for phase-level trace recording
- Add trace entries to all phase scripts
- Create trace viewer (HTML dashboard extension or CLI summary)
- Add experiment trace to PR body for reviewer context

**Dependencies:** None strictly, but best implemented after Phases 1-4 when the full pipeline is stable.

---

## References

- [Anthropic — Building Effective Agents](https://www.anthropic.com/research/building-effective-agents)
- [Anthropic — How We Built Our Multi-Agent Research System](https://www.anthropic.com/engineering/multi-agent-research-system)
- [OpenAI — Agent Orchestration (Agents SDK)](https://openai.github.io/openai-agents-js/guides/multi-agent/)
- [OpenAI — A Practical Guide to Building Agents](https://openai.com/business/guides-and-resources/a-practical-guide-to-building-ai-agents/)
- [Hone — Architecture](../architecture.md)
- [Hone — Agent Designs](../agent-designs.md)
- [Hone — Future Extensions](../future-extensions.md)
