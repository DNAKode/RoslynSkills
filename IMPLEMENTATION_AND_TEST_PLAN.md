# Implementation and Test Plan (Cross-Referenced)

Date: February 8, 2026  
Status: Execution plan v1

Cross references:

- Proposal: `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`
- Research plan: `DETAILED_RESEARCH_PLAN.md`
- Design spec: `DETAILED_DESIGN_SPEC.md`

## 1. Implementation Strategy

Principle:

- implement minimum viable vertical slices early,
- validate empirically,
- expand breadth only when quality gates hold.

Execution anchor:

- dependency graph in `DETAILED_RESEARCH_PLAN.md` (P0-P10),
- design contracts in `DETAILED_DESIGN_SPEC.md`.

## 2. Implementation Waves

## Wave A: Foundations and Contracts

Projects:

- P0, P2 (partial), P3 (skeleton), P6 (skeleton)

Deliverables:

- repository layout and artifact schema,
- command registry and schema envelopes,
- CLI host skeleton,
- harness runner abstraction skeleton.

Tasks:

- R-001, R-003, R-004, R-007

Exit criteria:

- commands can be discovered and invoked through stable envelopes,
- harness can run no-op and mock scenarios end-to-end.

## Wave B: Core Retrieval and Diagnostics Loop

Projects:

- P1, P5 (initial), P6 (baseline), P7 (initial)

Deliverables:

- structured symbol retrieval (`nav.find_symbol`, `ctx.symbol_envelope`),
- diagnostics packet command with multiple output modes,
- initial OSS task corpus and ingestion process.

Tasks:

- R-002, R-005, R-006, R-009

Exit criteria:

- first full experiment slice for RQ1 and RQ3 is runnable.

Interpretation rule:

- Wave B micro-benchmark results are diagnostic only; they do not by themselves validate overall agentic utility.

## Wave C: Structured Editing and Transactions

Projects:

- P5 (advanced), P6 (comparative runs)

Deliverables:

- edit anchor resolution,
- transaction pipeline with diagnostics gating,
- initial structured edit operations.

Candidate operations:

- `edit.rename_symbol`,
- `edit.change_signature`,
- `edit.add_member`,
- `edit.apply_code_fix`.

Exit criteria:

- benchmark runs compare B1 vs B2 on multi-file tasks with reproducible metrics.

## Wave D: Adapter Layer and Scale-Out

Projects:

- P4, P8

Deliverables:

- skill wrapper over CLI commands,
- MCP adapter over stable CLI subset,
- comparative interface experiments.

Tasks:

- R-012 plus scenario expansions.

Exit criteria:

- trace-equivalent behavior verified for shared operations across interfaces.

Additional required output:

- agent-in-loop A/B trial harness where Roslyn tools are optional in treatment condition and absent in control.

## Wave E: Production Hardening and Reporting

Projects:

- P9, P10

Deliverables:

- packaging and release pipeline,
- technical report draft with evidence-linked results,
- finalized meta-learning updates.

Tasks:

- R-013, R-014.

Exit criteria:

- production candidate and report package both review-ready.

## 3. Test Strategy

## 3.1 Test layers

1. Contract tests (CLI schema and exit semantics)
2. Unit tests (operation logic)
3. Integration tests (workspace and multi-file behavior)
4. End-to-end runner tests (agent orchestration)
5. Benchmark consistency tests (metric and artifact integrity)

## 3.2 Layer-to-component mapping

`Cli.Host`:

- contract tests for command envelopes and output modes.

`Core.Semantics` and `Core.Operations`:

- unit tests for symbol resolution, anchor matching, and operation preconditions.

`Core.Workspace` and transaction pipeline:

- integration tests for in-memory apply/reject/persist behavior.

`Benchmark.Harness`:

- end-to-end run tests with deterministic fixtures and seeded execution.

## 3.3 Required quality checks per PR

- build success,
- unit tests pass,
- command contract snapshots unchanged or explicitly versioned,
- no unexplained analyzer regression.

## 4. Benchmark Implementation Plan

## 4.1 Scenario registry implementation

Implement machine-readable scenario files with:

- task id and task text,
- repo/commit fixture reference,
- acceptance checks,
- interface/model policy matrix.

## 4.2 Runner implementation order

1. Baseline mock runner (for harness validation)
2. Codex CLI runner
3. Claude Code runner
4. Optional additional runners

Runner logging requirements:

- log tools offered per run,
- log tools called per run,
- classify Roslyn tool usage events,
- capture short structured post-run self-report block.

## 4.3 Metrics pipeline

Implement metrics in this order:

1. task success/failure
2. compile pass rate
3. regression count
4. time-to-green
5. reviewer cleanup proxy metrics
6. Roslyn tool adoption and usage share metrics
7. A/B uplift metrics conditioned on tool usage

## 5. Environment and Tooling Requirements

Baseline requirements:

- .NET SDK (target version to be pinned in implementation kickoff),
- git and shell runtime,
- access to selected OSS fixtures,
- configured model provider credentials for runner adapters.

Preflight requirements in harness:

- tool availability checks,
- writable workspace checks,
- fixture integrity checks,
- deterministic seed and manifest validation.

## 6. Risk-Driven Test Additions

Risk: ambiguous symbol targets  
Test response:

- adversarial fixtures with overloaded names and nested types.

Risk: partial cross-file edits  
Test response:

- API signature migration fixtures requiring full call-site updates.

Risk: diagnostic packet overload  
Test response:

- token-budget stress tests across `raw`/`compact`/`guided` modes.

Risk: interface divergence  
Test response:

- CLI vs skill vs MCP conformance test matrix.

Risk: synthetic benchmark overclaims  
Test response:

- enforce agent-in-loop A/B trial requirement before claiming end-to-end value.

## 7. Traceability Matrix (Projects -> Tasks -> Tests)

P3 (`Cli.Host`) -> R-004 -> contract + snapshot tests  
P5 (`Core.Operations`) -> R-005/R-006 -> unit + integration tests  
P6 (`Benchmark.Harness`) -> R-007/R-008 -> e2e runner + consistency tests  
P7 (corpus) -> R-009 -> fixture integrity + licensing checks  
P8 (experiments) -> R-010/R-011 -> benchmark run validation tests  
P9 (distribution) -> R-013 -> packaging and install smoke tests

## 8. Milestone Meta-Reflection Prompts

Use at each milestone commit:

1. Which test signal changed a decision?
2. Which interface or schema choice reduced agent failure most?
3. Which component causes repeated friction?
4. What should be simplified before scaling breadth?

Record concise answers in `AGENTS.md` meta-learning log.

## 9. Definition of "Ready to Implement at Speed"

This plan is execution-ready when:

- Wave A deliverables are checked in,
- first benchmark slice (RQ1) has a runnable path,
- command and output contracts are stable enough for adapter development,
- test scaffolding catches regressions before benchmark runs.
