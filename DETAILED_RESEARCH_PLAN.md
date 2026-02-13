# Detailed Research Plan: Roslyn Agentic Coding

Date: February 8, 2026  
Status: Active planning artifact

## 1. Purpose

This plan turns `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md` into actionable project work packages.

Use this document for:

- dependency-aware execution planning,
- concrete deliverable tracking,
- acceptance-gated progress.

Do not use this file to redefine research motivation or hypotheses; that belongs in the proposal.

## 2. Priority Model

Follow project priority order:

1. Tooling quality and reliability
2. Benchmark credibility and coverage
3. Meta-learning quality (research process improvement)
4. Industrial technical report quality

## 3. Program Decomposition (Projects)

## P0. Program Foundation and Reproducibility

Objective:

- Establish reproducible experiment lifecycle and artifact standards.

Outputs:

- repository structure for tools, harness, datasets, reports,
- run manifest schema,
- artifact retention policy,
- deterministic execution checks.

Dependencies:

- none.

Acceptance gate:

- repeated run on same commit/config produces equivalent metrics and artifacts.

## P1. Operation Scope Discovery and Taxonomy

Objective:

- Define broad operation surface for Roslyn augmentation and classify by value/risk.

Outputs:

- operation taxonomy (navigation, transforms, diagnostics, repair, test impact),
- operation matrix by task type and ambiguity,
- early de-scope candidates for low-value operations.

Dependencies:

- P0.

Acceptance gate:

- operation catalog covers all benchmark task families in proposal Section 7.

## P2. Command Language and Protocol Theories

Objective:

- Define candidate command languages for LLM-to-tool interaction.

Theories to evaluate:

- low-level imperative op language,
- declarative intent language,
- staged hybrid protocol (intent -> plan -> concrete ops).

Outputs:

- command schemas (JSON-first),
- validation grammar and error semantics,
- compatibility mapping across CLI/skill/MCP plus external C# LSP comparator conditions.

Dependencies:

- P1.

Acceptance gate:

- each theory supports at least one end-to-end task without manual intervention.

## P3. Agent-Facing CLI (Primary Interface)

Objective:

- Build cross-platform, self-documenting CLI designed for agent consumption.

Requirements:

- stable machine-readable and human-readable output modes,
- predictable exit codes and error classes,
- high-quality `--help` and subcommand discoverability,
- explicit pit-of-success startup surface (`quickstart`) and guardrails for common misuse,
- JSON output schema versioning,
- low-latency behavior for iterative agent calls.

Outputs:

- `roscli` CLI skeleton and core subcommands,
- command contract tests,
- compatibility docs for Codex CLI and Claude Code workflows,
- canonical pit-of-success guide wired into release artifacts and skill docs.

Dependencies:

- P2.

Acceptance gate:

- key workflows scriptable in shell with no hidden state assumptions.
- first-minute onboarding path succeeds without command-argument guesswork (`list-commands` -> `quickstart` -> `describe-command`).

## P4. Interface Adapters (Skills, then MCP)

Objective:

- Layer interface adapters over stable CLI primitives.

Outputs:

- skill wrapper package for guided workflows,
- MCP adapter that exposes selected CLI capability safely,
- adapter conformance tests against CLI contract,
- comparator guidance pack for external C# LSP runs (for example Claude `csharp-lsp`) with condition-isolation checks.

Dependencies:

- P3.

Acceptance gate:

- adapter behavior is trace-equivalent to direct CLI calls for shared operations.

## P5. Roslyn Core Engine and Operation Implementations

Objective:

- Implement operation surface across hypotheses H1-H4/H5/H6.

Outputs:

- semantic retrieval engine,
- structured edit operation engine,
- transactional validation pipeline,
- adaptive feedback policy experiments.

Dependencies:

- P1, P2, P3.

Acceptance gate:

- operations pass unit/integration tests and meet minimum reliability bar on pilot corpus.

## P6. Benchmark Harness and Scaffolding

Objective:

- Create rigorous comparative benchmark system across interfaces and model variants.

Detailed requirements:

- scenario registry with parameterized task definitions,
- run orchestration with seeded reproducibility,
- baseline support (B0-B4),
- telemetry collection (tool calls, timings, token usage, diagnostics),
- explicit tool availability and tool usage logging per run,
- structured post-run agent self-report capture,
- metric computation and report generation,
- replay capability for failed runs,
- environment capability detection and setup checks.

Dependencies:

- P0, P3.

Acceptance gate:

- harness can execute at least one full benchmark slice end-to-end with artifact completeness.

## P7. Public OSS Corpus Pipeline

Objective:

- Curate, version, and maintain public C# task corpus.

Outputs:

- repository selection policy,
- task extraction pipeline,
- holdout split policy,
- licensing and attribution records (MIT-friendly outputs).

Dependencies:

- P6.

Acceptance gate:

- corpus includes representative tasks across all task families and ambiguity levels.

## P8. Comparative Experiments and Ablations

Objective:

- Run systematic experiments to answer RQ1-RQ5 and architecture hypotheses.

Outputs:

- experiment matrix,
- randomized A/B trial matrix (Roslyn unavailable vs available),
- ablation reports (context shape, feedback timing, interface channel),
- failure taxonomy report,
- recommendation package for production path.

Dependencies:

- P5, P6, P7.

Acceptance gate:

- results are reproducible and decision-ready.

### P8.1 Next Steps (2026-02-13): Roscli vs Base Comparator Refresh

Objective:

- Re-run roscli vs base comparisons with systematic variation across prompt posture, model, and reasoning effort.
- Add at least one large OSS repo run where workspace binding is expected (`workspace_context.mode=workspace`).

Plan:

- Vary tool prompts:
  - Use existing guidance profiles (`brief-first`, `surgical`, `standard`, `schema-first`, `skill-minimal`).
  - Add 3-5 improved prompt variants focused on lowering overhead:
    - `brief-first-v2`: 1 targeted `nav.find_symbol` (or direct `edit.rename_symbol` when coordinates are known) + 1 verification `diag.get_file_diagnostics`.
    - `workspace-locked`: always pass explicit `--workspace-path <.csproj|.sln|.slnx>` for project code; treat `ad_hoc` as retry-required.
    - `edit-then-verify`: forbid exploratory `nav.*` unless ambiguity is detected; prefer direct edit primitives + post-edit diagnostics.
    - `diagnostics-first`: start with `diag.get_workspace_snapshot` scoped to the touched directory before choosing an edit.

- Vary Codex model and reasoning effort (extra effort here):
  - Models: `gpt-5.3-codex`, `gpt-5.3-codex-spark`.
  - Reasoning efforts: `low`, `medium`, `high`, `xhigh`.
  - Update harnesses where needed to parameterize reasoning effort (matrix sweeps), not just guidance profile.

- Vary task size and project shape:
  - Micro (paired harness): `benchmarks/scripts/Run-PairedAgentRuns.ps1` (project + single-file) to keep variance low.
  - Medium (multi-step): `benchmarks/scripts/Run-LightweightUtilityGameRealRuns.ps1` (roscli vs base) for longer trajectories.
  - Large OSS (real repo): `benchmarks/scripts/Run-OssCsharpPilotRealRuns.ps1`.

- Large OSS repo target (pick one and encode as a task):
  - Preferred candidate: `grpc/grpc-dotnet` (large, idiomatic .NET, usually `dotnet test` friendly).
  - Alternative fallback if build/test is too heavy: `Spectre.Console/Spectre.Console` (smaller but reliable).
  - For any OSS task, encode:
    - required `git submodule update --init --recursive` (if applicable),
    - scoped restore/test commands that avoid optional workloads,
    - workspace-mode gate evidence in transcripts.

Acceptance/validity gates (must hold before interpreting deltas):

- Project-shaped tasks: at least one Roslyn nav/diag call reports `workspace_context.mode=workspace`.
- Workspace health gate: fail/redo if diagnostics include `CS0518` (missing core reference assemblies) even when mode reports `workspace`.
- Treatment integrity gate: prefer failing closed if a treatment lane completes with `roslyn_used=false` (prevents silent text-only treatment confounds).
- Single-file tasks: `ad_hoc` is expected and must not be treated as a tool failure.
- Report within-agent, within-scenario deltas; do not compare across agents as primary evidence.

Outputs:

- New version-stamped matrix markdown in `benchmarks/experiments/` and corresponding `RESEARCH_FINDINGS.md` update with artifact links.
## P9. Production Hardening and Distribution

Objective:

- Promote best-performing tooling path to production-ready quality.

Outputs:

- packaging (`dotnet tool` and binary releases),
- operational docs and support matrix,
- CI quality gates,
- upgrade/migration policy.

Dependencies:

- P8.

Acceptance gate:

- release candidate meets quality, reliability, and operational criteria for wider adoption.

## P10. Technical Report and Meta-Learning Capture

Objective:

- Produce industrial report and codify process lessons.

Outputs:

- industrial technical report draft/final,
- appendix with benchmark methodology and artifact references,
- updated `AGENTS.md` meta-learning section with validated process lessons.

Dependencies:

- P8 (core), P9 (for production narrative).

Acceptance gate:

- report is internally reviewable and evidence-linked.

## 4. Dependency Graph (Project Level)

- `P0 -> P1 -> P2 -> P3 -> P4`
- `P1 + P2 + P3 -> P5`
- `P0 + P3 -> P6 -> P7`
- `P5 + P6 + P7 -> P8 -> P9 -> P10`

Parallelization notes:

- P6 can begin once P3 CLI contracts are minimally stable.
- P5 and P7 should run in parallel once unblocked.
- P4 can progress in parallel with early P5 implementation after CLI stabilization.

## 5. Actionable Backlog (Initial)

`R-001` Define repository structure and artifact schemas (P0)  
`R-002` Draft operation taxonomy v0 and scenario matrix template (P1)  
`R-003` Draft three command-language theory specs with examples (P2)  
`R-004` Implement CLI skeleton and command contract format (P3)  
`R-005` Implement symbol retrieval command with structured output v0 (P5/P3)  
`R-006` Implement diagnostics feedback command with multiple formats (P5/P3)  
`R-007` Build benchmark harness skeleton and runner abstractions (P6)  
`R-008` Build Codex CLI + Claude Code adapter runners (P6)  
`R-009` Define OSS corpus selection rubric and task ingestion scripts (P7)  
`R-010` Implement first benchmark slice for RQ1 (structured retrieval vs grep) (P8)  
`R-011` Implement first benchmark slice for RQ3 (feedback timing policy) (P8)  
`R-012` Draft skill wrapper and MCP adapter thin layers (P4)  
`R-013` Add packaging pipeline for `dotnet tool` preview release (P9)  
`R-014` Create technical report template with evidence links (P10)
`R-015` Define agent-in-loop run manifest schema with control/treatment conditions (P6/P8)  
`R-016` Add Codex CLI and Claude Code runner logging for tool offered/used events (P6)  
`R-017` Implement scorer for adoption metrics and post-run agent self-report fields (P6/P8)  
`R-018` Build first realistic A/B trial pack on public OSS tasks (P8)

## 6. Command Language Design Work (Deep Focus)

For P2, produce at least the following artifacts:

- Operation Manifest spec
  - command id, input schema id, output schema id, side effects, invariants.
- Edit Intent spec
  - goal statement, constraints, scope bounds, acceptance checks.
- Planner Trace spec
  - step id, evidence, chosen command, fallback path, result status.
- Diagnostics Packet spec
  - normalized diagnostic payload variants (`raw`, `compact`, `guided`).

Evaluation criteria:

- expressiveness,
- model usability,
- determinism,
- safety under malformed or partial inputs,
- compatibility with CLI/skill/MCP channels and external C# LSP comparator lanes.

## 7. CLI Requirements for Agentic Use

Functional:

- deterministic command outputs,
- stable schemas with version tags,
- discoverable command inventory (`help`, `list-commands`, `describe-command`, `quickstart`),
- explicit workspace-binding visibility for file-scoped semantic/diagnostic commands (`workspace_context.mode` + optional `workspace_path` override),
- context-size aware output modes (`brief`, `standard`, `verbose`).

Context policy experiment (new high-priority branch):

- Compare treatment conditions with:
  - `brief-first` Roslyn guidance (compact by default, escalate on demand),
  - `verbose-first` Roslyn guidance (rich by default, compress when needed),
  - baseline `standard` guidance,
  - external C# LSP guidance profiles (for example `csharp-lsp` standard vs brief-first) under Roslyn-hidden conditions.
- Required metrics per condition:
  - task success/acceptance rate,
  - elapsed duration,
  - token totals and command round-trips,
  - `brief` adoption and payload proxies (`output_chars`, `source_chars`) from trajectory analysis.
- Decision rule:
  - keep any default/guidance change only if correctness is non-inferior and latency/token burden improves on paired tasks.

Operational:

- cross-platform support (Windows/Linux/macOS),
- no hidden global mutable state,
- clear retry and timeout semantics,
- low-overhead startup path for frequent short calls.

Quality:

- contract tests for all commands,
- snapshot tests for output schemas,
- regression tests for exit code semantics,
- onboarding-path tests for pit-of-success guidance continuity.
- project-backed smoke checks that fail if high-traffic file commands regress to ad-hoc context silently.

## 8. Tool Distribution Requirements

- MIT license for code and docs.
- Release channels:
  - local development builds,
  - prerelease packages,
  - stable tagged releases.
- Distribution targets:
  - `dotnet tool`,
  - standalone binaries where feasible.
- Provenance:
  - changelog,
  - release notes,
  - signed artifacts if feasible.

## 9. Test Scaffolding Requirements

Harness layers:

- command contract test layer,
- Roslyn operation unit/integration layer,
- agent-run orchestration layer,
- benchmark aggregation/reporting layer.

Environment support:

- preflight checks (SDK/tool availability),
- fixture provisioning and cleanup,
- result cache controls,
- reproducibility checks with seed + manifest.

Data and reporting:

- normalized run record format,
- per-task timeline trace,
- metric rollups by hypothesis/interface/model,
- failure taxonomy tagging.

## 10. Dogfooding Strategy

- Build core tooling in C#.
- Use the developed tooling to maintain and evolve itself where practical.
- Track "dogfood incidents" as first-class signals:
  - what was smooth,
  - what required manual bypass,
  - what blocked autonomous flow.

## 11. Meta-Reflection Checklist (per milestone)

At each milestone commit, capture:

- what assumptions were validated,
- what assumptions were falsified,
- what was slower than expected and why,
- what should change in process or architecture next.

Record concise results in `AGENTS.md` meta-learning log.
