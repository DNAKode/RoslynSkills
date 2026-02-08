# Roslyn Agentic Coding Research

Primary artifact: `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`  
This file (`AGENTS.md`) defines how agents should execute the project at maximum practical speed while preserving scientific rigor.

## Project State

Current mode: planning and design, rapidly transitioning to implementation once dependency prerequisites are satisfied.  
Primary objective: prove or disprove that Roslyn-native agent tooling materially outperforms text-first editing workflows for C#/.NET.
Priority order: tooling quality -> benchmark rigor -> meta-learning quality -> industrial research report.

## Distilled External Lessons (Meta-Study Inputs)

These principles are distilled from recent work/posts/talks by:

- Andrej Karpathy
- Erik Meijer (`@headinthebox`)
- Jeffrey Emanuel (`@doodlestein`, `Dicklesworthstone`)
- Peter Steinberger (`@steipete`)
- Paul Gauthier (Aider)

Synthesis to apply here:

- Keep autonomy high but controllable: human sets intent/constraints; agents execute aggressively inside clear guardrails.
- Treat planning as first-class code: do not "wing it"; produce explicit operation plans before large edits.
- Use compiler/semantic systems as truth sources, not optional helpers.
- Measure trajectories and outcomes, not just individual edits.
- Optimize for iteration velocity via task decomposition and parallelism, not by lowering quality bars.

## Applied Heuristics by Source

Karpathy-inspired:

- Use an autonomy slider: increase agent autonomy only when objective, constraints, and guardrails are explicit.
- Keep the human in high-level supervision mode (intent and acceptance), not line-by-line micromanagement.
- Favor practical "build the thing quickly, then harden" loops for early project momentum.

Meijer-inspired:

- Treat formal structure and semantics as core, not decoration.
- Push correctness checks earlier in the loop (compile/semantic constraints during planning and execution, not just after edits).
- Prefer representations and tooling that preserve intent with minimal ambiguity.

Emanuel-inspired:

- Invest heavily in planning quality before long implementation runs.
- Keep operations explicit and reusable (prompts/plans/scripts) to create a compounding execution flywheel.
- Assume context is expensive and scarce: keep guidance concise, executable, and modular.

Steinberger-inspired:

- Ship at "inference speed" by parallelizing independent tasks and using focused toolchains.
- Bias toward local, scriptable workflows that reduce friction and round-trip latency.
- Evaluate full agent trajectories and operational behavior, not just superficial output quality.

Gauthier-inspired:

- Continuously benchmark against strong baselines.
- Treat retrieval/context strategy as a first-class system component.
- Use measurable leaderboards/metrics to guide iteration instead of intuition-only changes.

## Execution Doctrine

1. Operate by dependency graph, not by calendar

- Do not produce fake sprint timelines or "human-paced" milestone theater.
- Build and maintain a DAG of work packages.
- Prioritize highest-value unblocked nodes first.
- Recompute critical path after every major finding.

2. Maximize throughput with bounded risk

- Run independent tasks in parallel whenever tool/environment allows.
- Keep edits atomic and reversible.
- For risky edits, constrain blast radius to one subsystem before expansion.

3. Evidence before opinion

- Every architectural claim must be tied to either:
  - benchmark data,
  - reproducible experiment output, or
  - direct primary source evidence.
- Mark inference clearly when evidence is indirect.

4. Compiler-truth over text-appearance

- Prefer Roslyn semantic certainty over regex confidence.
- For C# changes touching symbols, references, or signatures, use semantic verification paths by default.

## Operating Loop (Default)

For each work package:

1. Define goal + acceptance test
- State exact done criteria (build/test/analyzer/benchmark conditions).

2. Plan the operation graph
- List required prerequisites.
- Identify parallelizable branches.
- Define fastest safe sequence.

3. Execute at maximum safe speed
- Prefer deterministic scripts/tools over ad hoc manual repetition.
- Keep iteration cycle tight: edit -> validate -> record -> continue.

4. Validate with hard gates
- Compile success.
- Relevant tests pass.
- Analyzer policy respected.
- No unintended scope expansion.

5. Log learnings
- Capture what sped us up.
- Capture what caused churn/rework.
- Convert repeated lessons into agent rules.

## Planning and Specification Rules

- Spend substantial effort upfront on exact task specification when uncertainty is high.
- Prefer one precise plan over many speculative versions.
- Use branching plans only when uncertainties are real and decision-relevant.
- Keep plans tool-executable: each plan step must map to concrete commands/actions.

Plan artifacts should include:

- Problem statement
- Constraints and non-goals
- Dependency graph
- Validation gates
- Rollback/fallback strategy

## Implementation Rules

- Prefer small, composable tools with clear I/O contracts.
- Keep interfaces schema-driven where possible.
- Avoid hidden state; persist important state as files/artifacts.
- Prefer local-first reproducible execution.
- Treat docs, scripts, and benchmarks as product code.

## Roslyn-Specific Rules

- Default to Roslyn workspace + semantic model for non-trivial C# operations.
- Keep text-based fallback path for resilience, but log when fallback was required.
- Track structured edit success/failure separately from text-edit success/failure.
- Capture operation traces to support failure taxonomy and future tool improvement.

## Benchmark and Evaluation Doctrine

- Source of truth for baselines, task families, metrics, and methodology: `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md` (Sections 7 and 8).
- Run comparative benchmarks early and continuously, not only at project end.
- Evaluate end-to-end task trajectories; do not judge quality on patch appearance alone.
- Archive run artifacts (inputs, outputs, traces, metrics) for reproducibility and audit.

## Quality and Safety Gates

Before accepting significant changes:

- Build succeeds.
- Relevant tests pass.
- Analyzer warnings do not regress without explicit waiver.
- Diff scope matches declared intent.
- Artifacts needed for reproduction are saved.

For architecture go/no-go decisions, use proposal-defined research gates and decision criteria in `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md` (Sections 6.4 and 11).

## Agent Collaboration Norms

- Communicate short progress updates frequently.
- State assumptions explicitly.
- Prefer directness over verbosity.
- Challenge weak premises quickly, then propose a concrete alternative.

## Meta-Learning Log (Keep Updating)

Use this section as compact project memory. Add short dated notes.

Template:

- `YYYY-MM-DD`: Observation -> Decision -> Rule update

Initial seed entries:

- `2026-02-08`: Project started with research-first scope -> Use proposal as single source of truth -> Keep AGENTS as execution doctrine, not narrative notes.
- `2026-02-08`: User requested speed-maximized, dependency-driven execution -> Drop calendar theater and version churn -> Plan and execute by DAG with strict acceptance gates.
- `2026-02-08`: Scope decision set by user -> Focus only on C#/.NET and public OSS for this phase -> Defer cross-language generalization claims.
- `2026-02-08`: Interface strategy clarified -> Prioritize plain CLI, then skill wrappers, then MCP adapters -> Stabilize CLI contracts before interoperability layers.
- `2026-02-08`: Evaluation philosophy clarified -> No fixed improvement threshold upfront; pursue best-effort gains and context-aware de-scoping -> Let empirical evidence drive inclusion/exclusion.
- `2026-02-08`: Historical caution recognized -> Syntax-aware systems can increase interaction friction for humans -> Preserve text fallback and evaluate mixed-mode workflows.
- `2026-02-08`: Detailed project decomposition created -> Convert proposal into dependency-linked projects (P0-P10) with acceptance gates -> Use project-level graph for execution and parallelization decisions.
- `2026-02-08`: Cross-referenced design and implementation plans created -> Link projects, command theories, interfaces, and test layers explicitly -> Use traceability matrix to keep build and benchmark work aligned.
- `2026-02-08`: Initial implementation slice completed -> Ship CLI contract + Roslyn-backed retrieval/diagnostics commands + test scaffolding early -> Validate architecture with passing build/tests before expanding operation breadth.
- `2026-02-08`: First RQ1 benchmark slice implemented -> Compare structured symbol envelopes against grep-like baseline on ambiguity fixtures with reproducible JSON artifacts -> Use targeted scenario design to expose disambiguation value early.
- `2026-02-08`: Methodology correction after user challenge -> Treat RQ1 component benchmark as diagnostic only and make agent-in-loop A/B trials primary evidence -> Add run schemas and scoring for tool adoption and agent self-report.
- `2026-02-08`: Agent-eval scoring scaffold implemented -> Ingest control/treatment run logs and quantify Roslyn adoption plus self-reported usefulness -> Require this layer before claiming end-to-end utility.
- `2026-02-08`: Agent-eval execution tooling expanded -> Added preflight, run coverage worklists, and pending-run template generation -> Make real A/B trial execution tractable and auditable.
- `2026-02-08`: Data quality gate added for A/B trials -> Validate run logs for contamination, schema quality, and scoring integrity before computing deltas -> Treat run validation as mandatory pre-score step.
- `2026-02-08`: Parallel build caveat observed -> Concurrent `dotnet` commands on the same project can lock build artifacts and create false failures -> Sequence .NET build/run/test commands per project in automation.
