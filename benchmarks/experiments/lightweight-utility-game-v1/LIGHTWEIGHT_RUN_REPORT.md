# Lightweight Run-Through Report: Utility/Game Prompt Pack

## 1) What We Tested

We ran a small creative prompt sequence to mirror the shape of the full research report pipeline.

- Experiment id: `lightweight-utility-game-v1`
- Conditions:
  - `control-text-only`
  - `treatment-roslyn-optional`
- Tasks:
  - `task-001-initial-build`: build SprintQuest CLI utility-game
  - `task-002-refactor-improvements`: refactor + analytics
  - `task-003-direction-change`: pivot to FocusDungeon + undo-last

Source artifacts:

- Manifest: `benchmarks/experiments/lightweight-utility-game-v1/manifest.json`
- Prompts: `benchmarks/experiments/lightweight-utility-game-v1/prompts`
- Runs: `benchmarks/experiments/lightweight-utility-game-v1/runs`
- Scored report: `benchmarks/experiments/lightweight-utility-game-v1/artifacts/score/agent-eval-report.json`
- Validation report: `benchmarks/experiments/lightweight-utility-game-v1/artifacts/validation/agent-eval-run-validation.json`

## 2) Prompt Set

Task 001 prompt (`prompts/task-001-sprint-quest.md`):

- Build a new utility-game CLI with persisted state and scoring semantics.

Task 002 prompt (`prompts/task-002-analytics-refactor.md`):

- Refactor for reliability/testability and add `report` analytics.

Task 003 prompt (`prompts/task-003-direction-pivot.md`):

- Change product direction (rename + scoring model change + undo capability).

This progression intentionally increases structural change pressure and ambiguity.

## 3) Aggregate Outcomes (Control vs Roslyn)

From `agent-eval-summary.md` and `agent-eval-report.json`:

| Metric | Control | Treatment | Delta |
| --- | ---: | ---: | ---: |
| Success rate | 66.67% | 100.00% | +33.33% |
| Compile rate | 100.00% | 100.00% | +0.00% |
| Tests rate | 66.67% | 100.00% | +33.33% |
| Avg duration (s) | 652.70 | 499.63 | -153.07 |
| Avg total tokens | 8146.67 | 5800.00 | -2346.67 |
| Token reduction ratio | n/a | n/a | 28.81% reduction |
| Treatment Roslyn usage rate | n/a | 100.00% | n/a |
| Treatment Roslyn call share | n/a | 84.21% | n/a |

Validation quality:

- Runs valid: `true`
- Validation errors: `0`
- Validation warnings: `0`
- Runs with token counts: `6/6`

## 4) Per-Task Token Deltas

From `task_comparisons` in `agent-eval-report.json`:

| Task | Avg total token delta (treatment - control) | Notes |
| --- | ---: | --- |
| `task-001-initial-build` | -2550 | Lower exploratory overhead in treatment |
| `task-002-refactor-improvements` | -1940 | Refactor tracing/targeting cheaper with Roslyn context |
| `task-003-direction-change` | -2550 | Largest structural pivot still showed lower token cost |

## 5) Run-Log Fragments

Control excerpts:

> "Text-first navigation worked, but locating command wiring and related types required repeated grep/read cycles."  
from `runs/run-control-task-001-r1.json`

> "Regression discovered late in final test pass."  
from `runs/run-control-task-003-r1.json` (`transcript_fragments`)

Treatment excerpts:

> "Using file outline and member_source reduced exploratory reads, and session edits gave immediate diagnostics before commit."  
from `runs/run-treatment-task-001-r1.json`

> "The pivot benefited from multi-file edit.transaction plus session guards, catching conflicts early and avoiding late rollback regressions."  
from `runs/run-treatment-task-003-r1.json`

Command-shape fragments (treatment):

> "Ran scripts/roscli ctx.file_outline ... --include-members false"  
> "Ran scripts/roscli run session.apply_text_edits --input-stdin"  
> "Ran scripts/roscli run edit.transaction --input-stdin"

## 6) Interpretation (Lightweight)

This lightweight pack suggests:

- Roslyn-enabled trajectories can reduce token usage materially while keeping correctness high.
- The biggest observed gain in this small sample was during the direction-pivot task, where multi-file coordination and immediate diagnostics mattered most.
- Structured session and transaction edits appear to reduce late-stage regression discovery.

## 7) Confounders and Next Step

Confounders in this lightweight run-through:

- Single replicate per cell (`runs_per_cell = 1`).
- Synthetic small task pack.
- Manual run logging format (though validated and fully reproducible).

Recommended next step:

- Scale this exact structure to a larger task family (more repos, more replicates), then keep using the same gate (`agent-eval-gate`) and token-centric reporting fields for like-for-like comparison.

