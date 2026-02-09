# Agent Eval Summary

- Experiment: `creative-skyforge-planner-v1-real-tools`
- Generated UTC: `2026-02-09T16:27:43.5056154+00:00`
- Total runs: `8`

## Primary Comparison

- Control condition: `control-text-only`
- Treatment condition: `treatment-roslyn-optional`
- Success delta: `0.00%`
- Compile delta: `0.00%`
- Tests delta: `0.00%`
- Treatment Roslyn usage rate: `100.00%`
- Avg total tokens (control): `78463.00`
- Avg total tokens (treatment): `391194.00`
- Avg total tokens delta (treatment-control): `312731.00`
- Token reduction ratio: `-398.57%`

## Condition Summaries

| Condition | Runs | Success | Compile | Tests | Roslyn Used | Roslyn Call Share | Avg Roslyn Helpfulness | Runs w/ Tokens | Avg Total Tokens | Median Total Tokens |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `control-text-only` | 4 | 100.00% | 100.00% | 100.00% | 0.00% | 0.00% | n/a | 4 | 78463.00 | 62366.00 |
| `treatment-roslyn-optional` | 4 | 100.00% | 100.00% | 100.00% | 100.00% | 75.00% | 4.00 | 4 | 391194.00 | 272246.50 |

## Task Comparisons

| Task | Sufficient Data | Control Runs | Treatment Runs | Success Delta | Compile Delta | Tests Delta | Treatment Roslyn Usage | Avg Total Tokens Delta |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `task-001-skyforge-build` | Yes | 2 | 2 | 0.00% | 0.00% | 0.00% | 100.00% | 173323.50 |
| `task-002-carnival-pivot` | Yes | 2 | 2 | 0.00% | 0.00% | 0.00% | 100.00% | 452138.50 |

## Run Validation

- Valid: `True`
- Issues: `2` (errors=0, warnings=2)
- Contaminated control runs: `0`
- Treatment missing Roslyn offered: `0`
- Treatment missing Roslyn usage: `0`

Top validation issues:
- [warning] run=run-codex-treatment-task-001-skyforge-build-r01 task=task-001-skyforge-build cond=treatment-roslyn-optional tool_calls contains 'edit.create_file' which is not listed in tools_offered.
- [warning] run=run-codex-treatment-task-002-carnival-pivot-r01 task=task-002-carnival-pivot cond=treatment-roslyn-optional tool_calls contains 'session.apply_text_edits' which is not listed in tools_offered.
