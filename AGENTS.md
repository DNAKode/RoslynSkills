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

## Pit-Of-Success-First Doctrine

- Design command surfaces so the best workflow is the easiest workflow for agents.
- For high-traffic commands, ensure argument discovery is explicit via `describe-command` examples and guardrails.
- Maintain a short canonical startup path: `list-commands` -> `quickstart` -> targeted `describe-command`.
- Treat onboarding friction (argument confusion, bad first command choices, fallback churn) as a core quality metric, not docs polish.
- Keep release artifacts and skill packages aligned with this doctrine (include pit-of-success guidance near launchers).

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
Historical note: entries before the 2026-02-10 internal rename may reference the former `RoslynAgent.*` project/assembly names.

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
- `2026-02-09`: Session integrity hardening applied -> Paired-run harness now executes agent runs in isolated temp workspaces outside the host repo and enforces post-run `cwd`/git-root/`HEAD` restoration checks -> Prevent cross-talk between benchmark sessions and active coding session state.
- `2026-02-09`: Real-run manifest anchoring fixed -> `Run-LightweightUtilityGameRealRuns` now writes absolute `task_prompt_file` paths in generated `manifest.real-tools.json` -> Keep post-run gate validation reproducible from artifact directories without prompt-path drift.
- `2026-02-09`: Brief-mode adoption gap measured -> Latest creative-run trajectories showed near-zero `brief=true` usage despite high payload pressure -> Add explicit brief-vs-verbose guidance experiments plus trajectory-level `brief` usage analysis to inform default policy changes.
- `2026-02-09`: Control contamination recurred in lightweight real-runs -> Blocking by prompt text alone is insufficient -> Enforce control-condition Roslyn wrapper disablement and treat contamination checks as mandatory post-run integrity gate.
- `2026-02-09`: Lightweight harness isolation hardened -> Added per-task agent-home env overrides (`CODEX_HOME`/`CLAUDE_CONFIG_DIR`) and host cwd/git-root anchor assertions before/after agent and gate operations -> Fail closed on session drift and reduce cross-talk with the active coding session.
- `2026-02-09`: Empirical memory formalized -> Added `RESEARCH_FINDINGS.md` with tool identity snapshot, task-family context, v3/v4 measured outcomes, and token-to-information proxy metrics -> Treat findings updates as required after each benchmark bundle.
- `2026-02-09`: Persistent transport prototype benchmarked -> Added `RoslynAgent.TransportServer` plus invocation-mode benchmark arm and observed large warm-call latency reductions vs process-per-call CLI -> Prioritize MCP/transport parity experiments with explicit cold-vs-warm reporting in next bundle.
- `2026-02-09`: Real MCP execution lane enabled -> Implemented `RoslynAgent.McpServer` (framed JSON-RPC MCP protocol) and added `Run-PairedAgentRuns -IncludeMcpTreatment` isolated wiring -> Compare CLI vs MCP under identical task prompts and integrity gates before interface-level claims.
- `2026-02-09`: MCP lane integration defects discovered and patched -> Isolated Codex homes initially lost auth (401) and control contamination checks overmatched `roslyn` path substrings -> Seed minimal auth into isolated homes and constrain Roslyn telemetry to explicit command/tool signatures only.
- `2026-02-09`: MCP transport mismatch resolved -> Codex MCP client expected newline-delimited JSON-RPC responses while server initially emitted `Content-Length` frames -> Accept both inbound formats and emit newline-delimited responses to restore handshake reliability.
- `2026-02-09`: MCP resource URI normalization fixed -> Codex resolved `roslyn://commands` as `roslyn://commands/`, causing false unknown-resource failures -> Normalize host/path matching for catalog URI handling.
- `2026-02-09`: Codex MCP treatment validated in harness -> `paired-mcp-codex-resource-v4` achieved successful Roslyn MCP calls (`3/3`) with passing constraint checks -> Keep MCP as active arm while optimizing token overhead.
- `2026-02-09`: Cross-agent MCP replicate completed -> `paired-mcp-codex-claude-v1` showed MCP faster than CLI treatment for both Codex and Claude, with divergent token impact by agent -> Keep per-agent optimization tracks instead of assuming uniform token behavior.
- `2026-02-09`: Claude MCP attribution gap observed -> `ReadMcpResourceTool` URI-based Roslyn calls were successful but undercounted in `roslyn_used` summary fields -> Treat Claude MCP Roslyn-usage counters as provisional until parser classification update is validated.
- `2026-02-09`: Claude MCP attribution fix validated -> Fresh paired bundle (`paired-mcp-dnakode-v1`) now reports `claude-treatment-mcp` Roslyn usage as `3/3` with transcript/summary agreement -> Treat `ReadMcpResourceTool` URI-based Roslyn usage as production telemetry.
- `2026-02-09`: Reporting ergonomics improved -> Paired-run markdown now includes explicit per-agent control-vs-treatment and control-vs-treatment-mcp elapsed/token delta tables -> Use these breakout sections as the default quick-read for Codex vs Claude comparisons.
- `2026-02-09`: Paired-harness guidance posture became an explicit variable -> Added `Run-PairedAgentRuns -RoslynGuidanceProfile <standard|brief-first|surgical>` with profile stamping in run summaries -> Treat prompt posture as a first-class experimental arm, not ad hoc prompt text drift.
- `2026-02-09`: Trajectory usage telemetry undercounted helper-driven Roslyn flows -> `Analyze-TrajectoryRoslynUsage` now classifies helper outputs (for example `roslyn.rename_and_verify`) and reports discovery-vs-edit/pre-edit exploration metrics -> Use these fields to detect overhead from exploratory tool calls before editing.
- `2026-02-09`: Run-efficiency rollups lacked direct usefulness context -> `Analyze-RunEfficiency` now reports Roslyn-used run counts and average `roslyn_helpfulness_score` by condition -> Keep usefulness scores visible alongside elapsed/token deltas when assessing guidance changes.
- `2026-02-10`: Roscli startup overhead tuning validated under varied loads -> Added cached published wrapper mode (`ROSCLI_USE_PUBLISHED`) plus warm launchers and load-profile benchmark script -> Prefer published mode for high-volume agent loops while retaining `dotnet run` default for local correctness.
- `2026-02-10`: Published-cache safety hardened -> Added automatic stale-cache detection (`ROSCLI_STALE_CHECK`, initially default-enabled) and stamp-aware warmup updates -> Prevent stale binary drift while preserving high-call wrapper speed.
- `2026-02-10`: Lightweight real-run prompt/tool mismatch observed -> Treatment guidance assumed shorthand CLI on baseline bundles that only expose `run <command-id>` -> Update harness prompts to prefer `--input @payload.json` and bounded diagnostic calls before build/test gates.
- `2026-02-10`: Targeted treatment-vs-treatment real run completed (`ROSCLI_USE_PUBLISHED=0/1`) -> Single open-ended replicate showed trajectory variance overpowering wrapper startup gains -> Require replicate-backed, tighter-scope tasks before promoting published mode as an end-to-end speed/token win.
- `2026-02-10`: Efficiency reporting generalized for orthogonal arms -> `Analyze-RunEfficiency` now emits condition-pair deltas (not only control-vs-treatment) -> Use pairwise condition tables for treatment-only optimization experiments.
- `2026-02-10`: Stale-check cost quantified under varied loads -> Wrapper-level stale probes remained a dominant overhead source even with interval gating -> Default published mode to `ROSCLI_STALE_CHECK=0` and keep stale-check as an explicit opt-in safety mode.
- `2026-02-10`: Wrapper defaults rebalanced for real-agent usability -> Keep fast published cache reuse by default and rely on one-call refresh (`ROSCLI_REFRESH_PUBLISHED=1`) for deterministic cache invalidation -> Prioritize lower-latency tool loops unless actively editing roscli source itself.
- `2026-02-10`: Follow-up treatment replicate captured after wrapper default shift -> Published-cache lane moved from worse-than-baseline (v4) to better-than-baseline (v5) on elapsed/tokens/round-trips for the same task id -> Treat single-run direction as provisional and require replicate-backed interpretation.
- `2026-02-10`: Load-profile harness semantics aligned with new defaults -> `Measure-RoscliLoadProfiles.ps1` now treats published mode as stale-check-off baseline and adds explicit `-IncludeStaleCheckOnProfiles` arm -> Keep benchmark profiles representative of actual wrapper defaults.
- `2026-02-10`: Public distribution naming normalized for launch readiness -> Standardized external package/release branding on `RoslynSkills` (`DNAKode.RoslynSkills.Cli`, `roscli`, `roslynskills-bundle-*`) while retaining internal `RoslynAgent.*` assemblies for compatibility -> Decouple user-facing adoption path from internal refactor cost before repo rename and broader publishing.
- `2026-02-10`: Internal identity migration completed -> Renamed solution/projects/namespaces and assembly outputs from `RoslynAgent.*` to `RoslynSkills.*` (`RoslynSkills.slnx`, `src/tests` project graph, wrappers, benchmark scripts) with passing build/test/release smoke -> Keep external and internal naming aligned to reduce onboarding friction and path drift.
- `2026-02-10`: Repo-rename stabilization pass completed -> Updated local `origin` to `DNAKode/RoslynSkills`, clarified historical-name notes in handoff/findings docs, and validated workflow_dispatch runs for `Release Artifacts` and `Publish NuGet Preview` on the renamed repo -> Treat live Actions runs as the final guard against residual naming drift after repo/folder renames.
- `2026-02-10`: Agent argument-discovery failures observed in live Claude usage -> Added `describe-command` usage hints, MCP `tools/list` input-schema property/required hints for high-traffic commands, and explicit `session.open` file-type guardrails (`.cs/.csx` only) -> Prefer fail-closed command validation plus self-describing argument surfaces over permissive ambiguity.
- `2026-02-10`: One-shot file bootstrap gap closed -> Added `edit.create_file` with optional diagnostics and CLI shorthand wiring (`edit.create_file <file-path> --content ...`) plus tests -> Reduce fallback text-file creation churn in agent loops and keep file creation inside Roslyn command trajectories.
- `2026-02-10`: Skill-onboarding posture proved a first-class performance variable -> Shell-specific `schema-first`/`surgical` guidance cut major retry overhead in paired ablations while preserving correctness -> Treat prompt-profile text as executable interface code and benchmark it like command implementations.
- `2026-02-10`: PowerShell `--input @file.json` parsing created avoidable schema-first failures -> Standardized quoted `--input "@file.json"` examples in Codex/PowerShell guidance -> Encode shell-grammar-safe forms in all profile prompts to reduce non-semantic churn.
- `2026-02-11`: Ecosystem scan clarified complementary tool boundaries -> `dotnet-inspect`/`dotnet-skills` emphasize package+assembly intelligence and plugin marketplace distribution, while RoslynSkills emphasizes workspace-semantic edits -> Position RoslynSkills as complementary rather than substitutive in docs and announcement strategy.
- `2026-02-11`: Skill distribution ergonomics became a first-class adoption factor -> Marketplace/plugin metadata and dual-layer docs (`SKILL.md` + deep reference command) reduce onboarding friction for agents -> Treat packaging/distribution surface as product work, not post-launch polish.
- `2026-02-11`: LSP comparison was promoted from speculation to explicit benchmark scope -> Added external C# LSP comparator conditions (including Claude `csharp-lsp`) to proposal/plan artifacts and paired-run harness telemetry fields -> Require repeatable Roslyn-vs-LSP-vs-control runs before making obsolescence/complementarity claims.
- `2026-02-11`: External tool comparator scope expanded beyond LSP -> Added planned `dotnet-inspect`/`dotnet-skills` condition matrix (inspect-only, roslyn-only, combined) for dependency/API-intelligence task families -> Test complementarity vs substitution empirically under isolated conditions.
- `2026-02-11`: Complementary-tool onboarding friction reduced -> Added explicit "which tool when" and combined-mode agent hint blocks in README/skill docs for `dotnet-inspect` + `roscli` -> Improve first-session adoption and reduce tool-selection confusion.
- `2026-02-11`: Guidance-profile ablation reconfirmed posture sensitivity -> `brief-first` remained lower-overhead than `skill-minimal` across completed Codex/Claude profiles while preserving pass rates -> Keep `brief-first` default and reserve `skill-minimal` for stress diagnostics.
- `2026-02-11`: LSP lane ambiguity reduced with explicit availability telemetry -> Added `lsp_tools_available`/`lsp_tools_unavailable_detected` and verified clean LSP-lane Roslyn contamination checks in fresh Claude run artifacts -> Gate LSP efficacy claims on confirmed tool availability, not lane label alone.
- `2026-02-11`: Ablation rollup null-handling corrected -> Missing lane deltas (for example Codex `treatment-lsp`) were being coerced into synthetic values -> Keep absent-lane metrics as null to avoid false comparator conclusions.
- `2026-02-11`: Pit-of-success posture elevated to core design rule -> Added CLI `quickstart` guidance surface, canonical guide (`docs/PIT_OF_SUCCESS.md`), and release-bundle inclusion requirements -> Treat onboarding success and first-command correctness as first-class quality gates.
- `2026-02-11`: Pit-of-success guidance embedded across runtime and artifacts -> Added `list-commands` pit hints, stronger `--help`, quickstart recipes, NuGet/readme/skill references, and bundle-level `PIT_OF_SUCCESS.md` inclusion -> Avoid relying on single-entry docs and make guidance discoverable from every likely starting point.
- `2026-02-11`: Installed-tool discoverability gap surfaced in real usage -> `roscli --version` previously failed with `unknown_verb` despite being a standard CLI expectation -> Added explicit `--version`/`-v`/`version` support and updated quick verification docs to reduce onboarding confusion.
- `2026-02-11`: Version output clarity and session intent were still ambiguous in first-run usage -> `cli.version` now exposes a concise `cli_version` plus raw `informational_version`, and `session.open` no longer accepts misleading `--solution` shorthand -> Keep file-scoped session semantics explicit and reduce avoidable agent confusion.
- `2026-02-11`: README onboarding route was overloaded with fallback paths -> Kept main path roscli-first, moved install fallback/local `.nupkg` flows into troubleshooting, and placed Rich Lander companion tooling context/attribution later -> Preserve pit-of-success momentum while still documenting escape hatches.
- `2026-02-11`: LSP comparator fairness and reliability gaps were observed under loose-file tasks and unstable auth state -> Added project-backed paired-run task shape (`TargetHarness.csproj` + `Program.cs`) plus Claude auth preflight fail-fast in the harness -> Require project-context comparator runs and valid agent auth before interpreting Roslyn-vs-LSP outcomes.
- `2026-02-11`: Preview distribution flow required too many manual steps across separate workflows -> Updated `Publish NuGet Preview` to build one artifact set, publish NuGet, and refresh GitHub Release assets in the same run -> Treat this unified workflow as the default regular release path for preview versions.
- `2026-02-11`: File-scoped Roslyn commands could silently degrade to ad-hoc semantics and report misleading diagnostics -> Added workspace auto-resolution + explicit `workspace_path` override with surfaced `workspace_context` metadata in `nav.find_symbol`/`diag.get_file_diagnostics` and aligned pit-of-success guidance/harness prompts -> Require agents to verify `workspace_context.mode` and force workspace binding when mode is `ad_hoc` on project-backed files.
- `2026-02-12`: Project-task benchmark runs were falsely failing due harness artifact leakage (`Target.original.cs` compiled into generated project) -> Excluded `Target.original.cs` from `TargetHarness.csproj` and added regression test coverage -> Treat run-harness file layout as part of experiment validity gates.
- `2026-02-12`: Workspace-mode evidence was hard to aggregate across transcripts -> Added paired-run metadata fields for Roslyn workspace mode counts and updated summary markdown/workflow docs -> Use `workspace/ad_hoc` counters as first-class comparability telemetry in scenario matrices.
- `2026-02-12`: Helper-lane workspace telemetry undercounted due parser/runtime mismatch (`ConvertFrom-Json -Depth` unsupported in local PowerShell) -> Reworked workspace-mode extraction to parse command/MCP envelopes with compatible JSON parsing and added benchmark script regression assertions -> Treat parser-runtime compatibility as a required validity check for telemetry claims.
- `2026-02-12`: Current state anchor needed explicit release-to-matrix linkage -> Published `v0.1.6-preview.8` and added a version-stamped approach matrix artifact (`20260212-approach-matrix-v0.1.6-preview.8.md`) -> Always include current published version id in active matrix/readout docs.
- `2026-02-12`: High-effort profile sweeps on `v0.1.6-preview.9` showed strong posture sensitivity (`brief-first`/`surgical` lower overhead; `schema-first`/`skill-minimal` high overhead) across project and single-file task shapes -> Keep lightweight guidance profiles as defaults and reserve contract-heavy profiles for debugging lanes -> Treat prompt posture as a first-class experimental variable with explicit scenario tagging.
- `2026-02-12`: Workspace telemetry parser fix was empirically validated with a focused rerun (`singlefile-schema-first-telemetryfix-v2`) where helper-lane workspace counters moved from false-zero to observed ad-hoc counts -> Keep parser hardening and regression checks in benchmark harness core path -> Re-baseline workspace-mode trend interpretation on post-fix bundles.
- `2026-02-12`: Current-state matrix expanded to `20260212-approach-matrix-v0.1.6-preview.9.md` with full codex approach/profile grid plus explicit stale LSP comparator labeling -> Separate experimental environment blockers from tooling conclusions in reporting -> Require freshness tags on any cross-day comparator row.
- `2026-02-12`: Codex LSP MCP lane was blocked by missing bridge command in early runs -> Added `Run-CodexMcpInteropExperiments` support for `cclsp` discovery plus auto-generated run-local `cclsp.json` (`csharp-ls` mapping) and explicit unresolved-command skip telemetry -> Treat LSP bridge bootstrap as first-class harness responsibility, not an ad hoc operator step.
- `2026-02-12`: Interop matrix needed speed alongside token/call metrics -> Added per-run `duration_seconds` capture/output in Codex MCP interop summaries and added benchmark script regression tests -> Require elapsed-time reporting in comparator artifacts before claiming practical wins.
- `2026-02-12`: `gpt-5.3-codex-spark` availability changed during the same research session (early unsupported, later fully passing) -> Revalidated with fresh Spark smoke and full low/high scenario matrix -> Run model-availability preflight at bundle start and treat availability drift as a confound dimension.
- `2026-02-12`: New Codex/Spark interop matrices on project-backed tasks showed `lsp-mcp` materially closer to control cost than `roslyn-mcp`, while combined Roslyn+LSP lanes remained higher-token but sometimes competitive on elapsed time -> Keep `lsp-mcp` and combined lane as active optimization arms and reserve Roslyn-heavy lane for precision-focused scenarios.
- `2026-02-12`: Workspace binding still leaked ambiguity in disconnected roscli call contexts -> Added fail-closed `require_workspace` for `nav.find_symbol`/`diag.get_file_diagnostics`, propagated usage hints through CLI/MCP surfaces, and hardened paired-run helper scripts/constraint checks to auto-bind `TargetHarness.csproj` in project shape -> Default project-grade nav/diag guidance to explicit workspace binding with fail-closed semantics.
- `2026-02-12`: Helper-driven roscli runs underreported workspace mode (`0/0`) despite explicit project binding -> Extended helper output to emit `workspace_context` and hardened transcript parser to recurse arbitrary JSON shapes -> Treat `workspaceguard-v3+` bundles as the baseline for workspace-context telemetry interpretation.
- `2026-02-12`: MCP prompt sequencing on anchored rename tasks created avoidable pre-rename nav overhead -> Updated paired-run MCP guidance to prefer direct `edit.rename_symbol` + `diag.get_file_diagnostics` and only use `nav.find_symbol` as fallback when coordinates are ambiguous -> Keep prompt posture as a first-class optimization lever and validate via before/after bundle deltas.
- `2026-02-12`: Baseline-commit lightweight runs exposed Roscli identity drift (`RoslynSkills.Cli` hardcoded vs historical `RoslynAgent.Cli`) -> Updated generated lightweight shims and local `scripts/roscli*` launchers to auto-resolve either project identity -> Treat historical-workspace project-name probing as mandatory preflight.
- `2026-02-12`: Lightweight manifest acceptance checks were stale (`tests/RoslynSkills.*`) for historical baseline commits -> Corrected task acceptance paths to `tests/RoslynAgent.*` -> Treat acceptance-check path validation as a hard gate before promotion runs.
- `2026-02-12`: Cross-model matrix refresh (`20260212-codex-approach-matrix-v0.1.6-preview.9`) reconfirmed Roscli as lowest-token semantic lane on anchored edits while MCP lanes remain higher-overhead -> Keep Roscli default, keep MCP as optimization/research arm, and require bounded-run protocol for open-ended large-task claims.
- `2026-02-13`: OSS harness hit Windows path-length checkout failures when worktrees were nested under deep artifact dirs -> Moved OSS repo cache + workspaces to short `_tmp/oss-csharp-pilot/*` roots -> Always keep large OSS worktrees on short absolute paths to avoid checkout/restore flakiness.
- `2026-02-13`: OSS setup commands must be explicit and repo-specific -> Avalonia required `git submodule update --init --recursive` plus scoped restore to avoid workload requirements -> Treat missing submodule/restore scope as harness defects and encode in manifests.
- `2026-02-13`: Roscli shim correctness is a first-class pit-of-success gate -> A bad batch quoting bug made `scripts\\roscli.cmd` appear "missing" to agents -> Always validate one `nav.find_symbol --require-workspace true` call succeeds in the transcript before interpreting Roslyn-adoption deltas.
- `2026-02-16`: Cross-project investigative fallback gaps were productized -> Implemented `ctx.search_text`, `nav.find_invocations`, and `query.batch` with CLI shorthand/MCP schema hints and passing regression coverage -> Use these commands as default Roslyn-first path before shell `rg`/manual multi-query loops in benchmark prompts.
- `2026-02-16`: Mixed-language context parity surfaced as a practical adoption gate -> Added VB support + tests for `ctx.file_outline` and `ctx.member_source`, and added MCP input hints/examples for both commands -> Treat C# and VB context-command parity and schema discoverability as non-optional pit-of-success requirements.
- `2026-02-17`: Wider non-rename benchmark slice exposed rename-guidance leakage into unrelated tasks -> Added `operation-neutral-v1` profile and expanded paired harness task families (`change-signature`/`update-usings`/`add-member`/`replace-member-body`/`create-file`) -> Require task-family-aligned guidance before interpreting treatment overhead deltas.
- `2026-02-17`: Constraint strictness created false negatives on semantically equivalent syntax (`add-member` expression-bodied form) -> Relaxed checks to accept block-bodied and expression-bodied variants -> Encode benchmark acceptance by semantic intent, not single style form.
- `2026-02-17`: Codex non-rename treatment still showed high round-trip/token overhead despite successful Roslyn usage -> Attribute cost to exploratory command loops (describe/validate/retry churn) rather than command correctness -> Add stronger per-task call-budget rails and trajectory-level guard prompts for efficiency experiments.
- `2026-02-17`: Command naming clarity update approved -> Renamed stable CFG command id from `analyze.cfg` to `analyze.control_flow_graph` across CLI/MCP/query-batch/skills/tests with no alias -> Prefer explicit Roslyn-domain naming for discoverability and onboarding.
- `2026-02-17`: Cross-agent compatibility scope expanded -> Added `gemini` preflight probing (with Windows shim fallbacks), documented Gemini extension-guideline alignment, and logged OpenCode as a comparator follow-up -> Treat agent-platform compatibility and transcript telemetry viability as first-class benchmark infrastructure.
- `2026-02-17`: New validation story introduced for tool-thinking overhead isolation -> Added split-lane scaffold/analyzer scripts and a transcript-focused methodology doc -> Evaluate context overhead separately from downstream semantic-edit quality before changing default tool guidance.
- `2026-02-17`: Tool-thinking analyzer upgraded from coarse event counts to trajectory metrics (schema-probe churn, retry recoveries, discovery/edit split, pre-edit overhead windows, token/caching telemetry) and validated on real Codex/Claude artifacts -> Focus optimization on pre-edit command-shape/discovery loops before post-edit phases.
- `2026-02-17`: Split-analyzer strict-mode reliability bug surfaced on single-collection outputs (`.Count` on scalar) -> Added collection-count normalization helper plus executable script regression test -> Treat scalar-vs-array robustness as mandatory for transcript analysis scripts.
- `2026-02-17`: External-repo treatment lanes often miss Roslyn usage without explicit launcher path -> `Run-ToolThinkingSplitExperiment` now injects resolved host `roscli` launcher into treatment prompts and explicit control prohibition -> Require launcher availability guidance before interpreting treatment-vs-control overhead on non-RoslynSkills repos.
- `2026-02-17`: Treatment prompt posture is a direct overhead lever in split-lane runs -> Added `TreatmentGuidanceProfile` (`standard|tight`) to split scaffold/runner and measured lower pre-edit overhead under `tight` in matched MediatR Codex runs -> Use `tight` as default and keep `standard` as comparator for drift.
- `2026-02-17`: Roslyn-adoption counts were insufficient to characterize trajectory quality -> Added "used-well" split metrics (productive Roslyn calls, first productive timing, semantic-edit first-try success, verify-after-edit rate, composite used-well score with zero-floor when productive usage is absent) -> Gate "used well" claims on productive usage, not presence-only telemetry.
- `2026-02-17`: Release CI failures traced to benchmark test hardcoding `powershell` executable -> Updated script-invocation test path to resolve `pwsh` first with Windows fallback -> Treat cross-platform shell executable resolution as a mandatory gate for script-backed tests and release workflows.
- `2026-02-17`: `NU1903` security warnings were release-noisy due `Microsoft.CodeAnalysis.Workspaces.MSBuild` pulling vulnerable `Microsoft.Build.Tasks.Core` 17.7.2 -> Added explicit `Microsoft.Build.Tasks.Core` pin to 17.14.28 with `ExcludeAssets="runtime"` in `RoslynSkills.Core` to satisfy MSBuildLocator runtime guardrails -> Prefer targeted transitive security overrides plus runtime-asset exclusion over broad Roslyn package jumps when minimizing compatibility risk.
