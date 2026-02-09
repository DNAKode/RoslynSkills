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
- If a `.cs` read/edit falls back to non-Roslyn tooling, add a short self-reflection entry with:
  - exact reason fallback was required/preferred,
  - what Roslyn command was attempted (or missing),
  - proposed Roslyn command/option improvement,
  - expected impact on correctness/latency/token count.
- Treat repeated fallback reasons as tooling defects and prioritize command-surface fixes.

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
- `2026-02-08`: Scoring granularity expanded -> Added per-task control/treatment comparisons alongside aggregate deltas -> Use task-level slices to avoid hiding regressions behind overall averages.
- `2026-02-08`: Reporting pipeline tightened -> Added markdown summary export from scored/validated artifacts -> Keep experiment interpretation auditable and ready for technical-report ingestion.
- `2026-02-08`: Structured edit path initiated -> Added semantic `edit.rename_symbol` command with line/column anchoring and dry-run mode -> Use this as baseline for broader Roslyn edit-transaction experiments.
- `2026-02-08`: Edit-feedback loop tightened -> `edit.rename_symbol` now returns immediate post-edit compiler diagnostics in the same response -> Prefer operation-level feedback over delayed compile-only discovery.
- `2026-02-08`: Retrieval fidelity improved -> `nav.find_symbol` now includes semantic symbol metadata (kind/display/stable id) per match -> Use semantic enrichment to reduce ambiguity in agent tool decisions.
- `2026-02-08`: Structured edit breadth expanded -> Added `edit.replace_member_body` with line/column anchoring and immediate diagnostics -> Build comparative experiments across multiple edit primitives, not rename-only.
- `2026-02-08`: Edit benchmark coverage expanded -> Added RQ2 component diagnostic (structured rename vs naive text replacement) with collision-heavy fixtures -> Use this as a targeted signal for edit precision failure modes.
- `2026-02-08`: Evaluation orchestration simplified -> Added `agent-eval-gate` to run validation, scoring, and summary export with a single pass/fail outcome -> Reduce operator error and make promotion decisions faster.
- `2026-02-09`: Breadth-first operation rollout completed -> Implemented full v1 command surface across `nav.*`, `ctx.*`, `edit.*`, `diag.*`, and `repair.*` with registry integration and coverage tests -> Prioritize interface and reliability hardening before large-scale validation runs.
- `2026-02-09`: Run-quality strictness made configurable -> Added `--fail-on-warnings` policy support to run validation and gate flows with artifact-level policy capture -> Keep default permissive for data collection, enable strict mode for promotion decisions.
- `2026-02-09`: Roslyn-first loop validated in live implementation -> Used `nav.find_symbol` and `ctx.symbol_envelope` for code targeting; `diag.get_solution_snapshot` proved over-noisy for project-grade truth -> Keep Roslyn navigation/context first, but gate correctness with full `dotnet test` until workspace-aware diagnostics mature.
- `2026-02-09`: Fallback accountability tightened -> Any non-Roslyn `.cs` read/edit now requires explicit self-reflection and command-gap capture -> Convert fallback observations into Roslyn command-surface backlog items.
- `2026-02-09`: Wrapper ergonomics corrected -> `roscli` now defaults to shell-agnostic launchers (`scripts/roscli.cmd` and `scripts/roscli`) instead of PowerShell-specific wrapping -> Keep tool traces short without coupling command execution to a single shell.
- `2026-02-09`: CLI friction reduced for simple workflows -> Added direct command invocation and positional shorthand for common read-only commands -> Reserve JSON payload authoring for complex structured operations to reduce transcript/token overhead.
- `2026-02-09`: Exploration fidelity expanded -> Added `ctx.member_source` with body/member modes and bounded source extraction -> Reduce fallback `Get-Content` usage when implementing or reviewing member-local logic.
- `2026-02-09`: Session baseline implemented -> Added `session.open/set_content/get_diagnostics/diff/commit/close` with in-memory immutable snapshots and non-destructive diagnostics/diff loop -> Enable edit-validation trajectories without writing disk changes until commit.
- `2026-02-09`: Live session reliability upgraded -> Added `session.apply_text_edits` wiring plus generation/disk guard patterns (`expected_generation`, `session.status`, guarded commit) to the default workflow -> Prefer span edits for token efficiency and early conflict detection before compile loops.
- `2026-02-09`: Token-efficiency instrumentation clarified -> Added run-level guidance to capture provider token telemetry (or JSON-size/round-trip proxies) and command mix during A/B trials -> Optimize for lower tokens and retries without correctness regressions.
- `2026-02-09`: Tool-evolution fallback gap identified -> Multi-file command-surface updates still require text patching when changing the Roslyn tool itself -> Prioritize a Roslyn-native multi-file edit transaction primitive with semantic anchors and immediate diagnostics.
- `2026-02-09`: Direct CLI ergonomics upgraded -> Added shorthand option parsing (`--name value`, `--name=value`, boolean flags, repeated flags) for Roslyn commands -> Reduce JSON payload boilerplate and token overhead for routine exploratory/diagnostic calls.
- `2026-02-09`: Structured multi-file edits shipped -> Added `edit.transaction` with `replace_span`/`set_content`, immediate diagnostics, dry-run/apply behavior, and repair-plan integration -> Enable safer cross-file refactors with bounded feedback loops.
- `2026-02-09`: Lightweight A/B report shape validated -> Created utility/game prompt pack with paired control/treatment runs and gated scoring artifacts -> Use this as template for larger replicate-backed token/correctness studies.
- `2026-02-09`: Token validation accounting fixed -> `agent-eval-validate-runs` now reports runs with/missing token counts from run telemetry -> Keep token-coverage quality visible in promotion gates.
- `2026-02-09`: Preflight command detection hardened -> Bare-command probing missed Windows npm shims for `codex`/`claude`; added `.cmd`/`.exe` fallback probing with tests -> Treat shell-visible shim resolution as a first-class reliability requirement.
- `2026-02-09`: Real-agent harness reliability corrected -> Native CLI stderr could be misclassified as fatal in PowerShell and mark successful runs as failed; wrapped agent process invocation with controlled native-error semantics and explicit exit capture -> Keep benchmark run status tied to process exit code and preserved transcript evidence.
- `2026-02-09`: Benchmark control contamination observed -> Session-level Roslyn-first directives can leak into control runs and bias A/B outcomes -> Isolate control/treatment environments and artifact-local tool shims to preserve condition integrity.
- `2026-02-09`: CLI observability tuned for truncated tool logs -> Added envelope `Preview`/`Summary` hints with stable ordering so first/last JSON lines carry actionable state -> Keep machine-readable JSON while improving human scanning of command traces.
- `2026-02-09`: Session loop round-trips reduced -> Added `session.apply_and_commit` to combine structured edits, diagnostics snapshot, guarded commit, and optional session persistence in one call -> Prefer one-shot transaction for small/medium scoped changes.
- `2026-02-09`: Direct command ergonomics expanded -> Added positional shorthand for `edit.rename_symbol` and compact `list-commands` modes (`--compact`, `--ids-only`) -> Reduce JSON/payload overhead for common exploration and semantic rename workflows.
- `2026-02-09`: Real-run token attribution expanded -> Added transcript-derived round-trip and character-level attribution fields alongside provider token metrics in benchmark harness outputs -> Track where token/latency overhead originates, not just totals.
- `2026-02-09`: Roscli path robustness fixed -> Updated `scripts/roscli` and `scripts/roscli.cmd` to resolve project paths from script location instead of process cwd -> Keep Roslyn helpers usable from artifact subdirectories without manual repo-root pivots.
- `2026-02-09`: Cross-shell helper guidance stabilized -> Updated paired-run prompts to use shell-specific command forms and `./roslyn-*.ps1` compatibility paths, with sequential invocation guidance -> Reduce Bash/PowerShell escaping failures and spurious Roslyn fallback churn.
- `2026-02-09`: A/B condition integrity tightened -> Control prompt now explicitly forbids Roslyn helpers and harness metadata now separates attempted vs successful Roslyn calls (`roslyn_attempted_calls`, `roslyn_successful_calls`) with `roslyn_used` tied to successful usage -> Prevent false adoption positives in control telemetry.
- `2026-02-09`: Lightweight real-run result held after reliability fixes -> v6 paired run kept correctness and clean Roslyn treatment usage for both Codex/Claude, but treatment token totals still exceeded control on this microtask -> Target multi-file/high-ambiguity tasks for next token-efficiency evidence.
- `2026-02-09`: Snapshot payload pressure identified -> Added `brief` support/defaults across `nav.find_symbol`, `ctx.member_source`, and `diag.get_solution_snapshot` to suppress high-volume fields unless explicitly requested -> Prefer brief mode first for exploration/token control, then escalate detail selectively.
- `2026-02-09`: Harness execution overhead reduced -> Paired-run harness now publishes `RoslynAgent.Cli` once per bundle and issues helper calls against the published DLL instead of per-call `dotnet run` -> Remove repeated build/restore cost and reduce transient lock risk.
- `2026-02-09`: A/B run integrity and quality gates strengthened -> Added per-run agent-home isolation, deterministic rename constraint checks (including Roslyn diagnostics), and optional fail-fast control-contamination policy with markdown summary export -> Treat correctness/contamination/token views as first-class run outputs.
