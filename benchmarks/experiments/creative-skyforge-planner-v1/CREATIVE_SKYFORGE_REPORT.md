# Creative Skyforge Planner A/B Report (v1)

Date: `2026-02-09`  
Experiment: `creative-skyforge-planner-v1-real-tools`  
Artifacts: `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v3`

## Execution

Run command:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-LightweightUtilityGameRealRuns.ps1 `
  -ManifestPath benchmarks/experiments/creative-skyforge-planner-v1/manifest.json `
  -OutputRoot benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v3
```

All 8 trajectories executed successfully (Codex + Claude, control + treatment, 2 tasks).

## Gate Status

- Final gate: **PASS**
- `manifest_valid`: `true`
- `runs_valid`: `true`
- `sufficient_data`: `true`
- `run_validation_warning_count`: `2`
- Gate report: `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v3/gate/agent-eval-gate-report.json`

Note: the first gate attempt failed due `task_prompt_file` paths in `manifest.real-tools.json` being non-resolvable from the artifacts directory. After fixing prompt-path anchoring and re-running `agent-eval-gate` on the same run set, gate passed.

## Core Results

From `agent-eval-report.json`:

- Success delta (treatment-control): `0.00%`
- Compile delta: `0.00%`
- Tests delta: `0.00%`
- Treatment Roslyn usage rate: `100.00%`
- Avg total tokens (control): `78,463`
- Avg total tokens (treatment): `391,194`
- Avg token delta (treatment-control): `+312,731`

Per-task token deltas:

- `task-001-skyforge-build`: `+173,323.5`
- `task-002-carnival-pivot`: `+452,138.5`

## Integrity Signals

From run validation:

- `contaminated_control_runs`: `0`
- `treatment_runs_without_roslyn_offered`: `0`
- `treatment_runs_without_roslyn_usage`: `0`

Validation warnings (non-fatal):

1. `run-codex-treatment-task-001-skyforge-build-r01`: `tool_calls` included `edit.create_file` not listed in `tools_offered`.
2. `run-codex-treatment-task-002-carnival-pivot-r01`: `tool_calls` included `session.apply_text_edits` not listed in `tools_offered`.

## Interpretation

This pack shows correctness parity (all runs passed) and clean condition integrity (no control contamination), but a large token/latency cost in treatment for these tasks. For this creative two-task slice, Roslyn adoption was high in treatment but did not produce token efficiency gains.
