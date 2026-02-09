# Creative Skyforge Planner A/B Report (v1)

Date: `2026-02-09` (updated with v4 brief-first run)  
Experiment: `creative-skyforge-planner-v1-real-tools`  
Primary artifacts:
- `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v3`
- `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v4-brief-first-20260209-202905`

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

From v3 `agent-eval-report.json`:

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

From v3 run validation:

- `contaminated_control_runs`: `0`
- `treatment_runs_without_roslyn_offered`: `0`
- `treatment_runs_without_roslyn_usage`: `0`

Validation warnings (non-fatal):

1. `run-codex-treatment-task-001-skyforge-build-r01`: `tool_calls` included `edit.create_file` not listed in `tools_offered`.
2. `run-codex-treatment-task-002-carnival-pivot-r01`: `tool_calls` included `session.apply_text_edits` not listed in `tools_offered`.

## Interpretation

This pack shows correctness parity (all runs passed) and clean condition integrity (no control contamination), but a large token/latency cost in treatment for these tasks. For this creative two-task slice, Roslyn adoption was high in treatment but did not produce token efficiency gains.

## v4 Brief-First Update

Run command:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-LightweightUtilityGameRealRuns.ps1 `
  -ManifestPath benchmarks/experiments/creative-skyforge-planner-v1/manifest.json `
  -OutputRoot benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v4-brief-first-20260209-202905 `
  -RoslynGuidanceProfile brief-first
```

Gate status:

- Final gate: **PASS** (`GATE_EXIT=0`)
- `manifest_valid`: `true`
- `runs_valid`: `true`
- `sufficient_data`: `true`
- `run_validation_warning_count`: `4`
- Gate report: `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v4-brief-first-20260209-202905/gate/agent-eval-gate-report.json`

### Codex vs Claude (Control vs Treatment)

| Agent | Control elapsed (s) | Treatment elapsed (s) | Delta (s) | Ratio | Control tokens | Treatment tokens | Token delta | Token ratio |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `codex-cli` | 161.267 | 592.705 | +431.438 | 3.675x | 272,660 | 1,415,261 | +1,142,601 | 5.191x |
| `claude-code` | 183.836 | 272.228 | +88.392 | 1.481x | 7,737 | 12,327 | +4,590 | 1.593x |

Overall elapsed:

- Control total: `345.103s`
- Treatment total: `864.933s`
- Delta: `+519.830s` (`2.507x`)

### Integrity and Isolation Signals (v4)

From `agent-eval-run-validation.json`:

- `contaminated_control_runs`: `1` (Claude control task-001)
- `treatment_runs_without_roslyn_offered`: `0`
- `treatment_runs_without_roslyn_usage`: `0`

### Brief-Field Usage (v4)

From `trajectory-roslyn-analysis.json`:

- Parsed Roslyn command results: `36`
- Results with `query.brief` present: `3`
- `brief=true`: `2`
- `brief=false`: `1`
- Explicit `--brief` flags in command text: `0`

Observed behavior: Codex treatment used brief fields selectively; Claude treatment showed no parsed `query.brief` fields in this bundle.
