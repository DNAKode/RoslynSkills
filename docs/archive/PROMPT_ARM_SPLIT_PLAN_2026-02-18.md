# Prompt Arm Split Plan (2026-02-18)

Date: 2026-02-18  
Status: active execution plan for prompt methodology isolation

## Problem Statement

Current treatment prompts blend two different evaluations:

- guided execution (agent follows explicit task-specific Roslyn recipes),
- discovery execution (agent discovers command usage via command surfaces).

This conflates interface quality and prompt coaching quality, and can produce cross-purpose conclusions.

## Objective

Isolate prompt posture as an explicit independent variable and measure:

1. Roslyn adoption reliability,
2. pre-edit discovery churn,
3. round-trip and token overhead,
4. correctness under task constraints.

## Experimental Arms

- `A0 control-text`: no Roslyn tools.
- `A1 roslyn-guided`: tightly guided, task-aware commands (for example `surgical`).
- `A2 roslyn-discovery`: minimal guidance, requires command discovery/contract lookup (for example `skill-minimal`/`schema-first`).
- `A3 roslyn-tool-only`: no recipe guidance; prompt only states tool availability and optional single bootstrap command (`llmstxt`).

Interpretation rule:

- compare `A1` vs `A0` for practical uplift,
- compare `A2` vs `A1` to estimate command-surface self-discovery friction.
- compare `A3` vs `A2` to isolate the impact of explicit discovery coaching vs pure tool-presence prompting.

## Immediate Task Slice

Use one project-shaped non-rename task:

- `change-signature-named-args-v1`

Rationale:

- requires semantic edit + propagation,
- less likely than rename microtasks to hide command-surface friction.

## Hard Gates

Per run:

- `run_passed=true`
- `constraint_checks_passed=true`
- `control_contamination_detected=false`
- treatment run records at least one successful Roslyn call (`roslyn_successful_calls>=1`)

Per comparison:

- record `duration_seconds`, `total_tokens`, `command_round_trips`, `roslyn_attempted_calls`, `roslyn_successful_calls`
- include trajectory split metrics (`discovery_calls`, `edit_like_calls`, `avg_roslyn_calls_before_first_edit`)

## Node Plan (Do-Now)

- `N1`: run `A2` probe (`skill-minimal`) for `change-signature-named-args-v1`.
- `N2`: compare with latest `A1` run (`surgical` schema-safe).
- `N3`: run `A2b` (`discovery-lite-v1`) with bounded discovery and early semantic mutation, then re-measure.
- `N4`: run `A3` probe (`tool-only-v1`) on the same task to measure true no-recipe tool adoption and success.

## Command Templates

Guided arm (`A1`):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -OutputRoot artifacts/real-agent-runs/<bundle-guided> `
  -SkipClaude -TaskShape project -TaskId change-signature-named-args-v1 `
  -RoslynGuidanceProfile surgical -FailOnMissingTreatmentRoslynUsage -CodexReasoningEffort low
```

Discovery arm (`A2`):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -OutputRoot artifacts/real-agent-runs/<bundle-discovery> `
  -SkipClaude -TaskShape project -TaskId change-signature-named-args-v1 `
  -RoslynGuidanceProfile skill-minimal -FailOnMissingTreatmentRoslynUsage -CodexReasoningEffort low
```

Tool-only arm (`A3`):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -OutputRoot artifacts/real-agent-runs/<bundle-tool-only> `
  -SkipClaude -TaskShape project -TaskId change-signature-named-args-v1 `
  -RoslynGuidanceProfile tool-only-v1 -FailOnMissingTreatmentRoslynUsage -CodexReasoningEffort low
```

A2b discovery-lite arm (`A2b`):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -OutputRoot artifacts/real-agent-runs/<bundle-discovery-lite> `
  -SkipClaude -TaskShape project -TaskId change-signature-named-args-v1 `
  -RoslynGuidanceProfile discovery-lite-v1 -FailOnMissingTreatmentRoslynUsage -CodexReasoningEffort low
```

A2c tightened discovery-lite arm (`A2c`):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -OutputRoot artifacts/real-agent-runs/<bundle-discovery-lite-v2> `
  -SkipClaude -TaskShape project -TaskId change-signature-named-args-v1 `
  -RoslynGuidanceProfile discovery-lite-v2 -FailOnMissingTreatmentRoslynUsage -CodexReasoningEffort low
```

Trajectory analysis:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Analyze-TrajectoryRoslynUsage.ps1 -TrajectoriesRoot <bundle>
```

## Rollback / Fallback

- If a run fails due infra/auth/transient issues, rerun once with identical args.
- If failures persist, halt interpretation and log harness-level blocker.
- Do not merge arm conclusions across mixed prompt postures.

## Exploration Log

- 2026-02-19 (N1 complete): ran discovery arm bundle `20260218-arm-discovery-skillminimal-changesig` on `change-signature-named-args-v1`.
  - treatment: `round_trips=8`, `total_tokens=80681`, `duration_seconds=50.986`, `roslyn_successful_calls=7/7`.
  - trajectory: `discovery_calls=6`, `edit_like_calls=1`, `avg_roslyn_calls_before_first_edit=5`, `list_commands_calls=1`.
- 2026-02-19 (comparison): guided arm baseline bundle `20260218-dag-node2-change-signature-surgical-schemafix`.
  - treatment: `round_trips=4`, `total_tokens=57060`, `duration_seconds=35.351`, `roslyn_successful_calls=1/2`.
  - trajectory: `discovery_calls=1`, `edit_like_calls=1`, `avg_roslyn_calls_before_first_edit=0`, `list_commands_calls=0`.
- 2026-02-19 (N4 complete): ran tool-only arm bundle `20260219-arm-toolonly-v1-changesig` on `change-signature-named-args-v1`.
  - treatment: `round_trips=5`, `total_tokens=62988`, `duration_seconds=37.187`, `roslyn_successful_calls=3/3`.
  - trajectory: `discovery_calls=2`, `edit_like_calls=0`, `avg_roslyn_calls_before_first_edit=2`, `list_commands_calls=0`.
  - note: run used Roslyn for bootstrap/diagnostics (`llmstxt`, `describe-command`, `diag.get_file_diagnostics`) but still performed the edit via plain text patching.
- 2026-02-19 (N3 complete): ran discovery-lite arm bundle `20260219-arm-discovery-lite-v1-changesig` on `change-signature-named-args-v1`.
  - treatment: `round_trips=5`, `total_tokens=78269`, `duration_seconds=50.713`, `roslyn_successful_calls=4/4`.
  - trajectory: `discovery_calls=2`, `edit_like_calls=1`, `avg_roslyn_calls_before_first_edit=0`, `list_commands_calls=0`.
  - note: bounded discovery plus mandatory semantic edit worked, but verbose bootstrap/extra verification drove high token and latency cost.
- 2026-02-19 (replicate): ran tool-only replicate bundle `20260219-arm-toolonly-v1-changesig-r2` (valid).
  - treatment: `round_trips=8`, `total_tokens=63703`, `duration_seconds=38.086`, `roslyn_successful_calls=3/3`.
  - trajectory: `discovery_calls=2`, `edit_like_calls=0`, `avg_roslyn_calls_before_first_edit=2`.
- 2026-02-19 (replicate): ran discovery-lite replicate bundle `20260219-arm-discovery-lite-v1-changesig-r2` (invalid pair: control run failed constraints).
- 2026-02-19 (replicate rerun): ran discovery-lite bundle `20260219-arm-discovery-lite-v1-changesig-r3` (valid).
  - treatment: `round_trips=4`, `total_tokens=57827`, `duration_seconds=33.573`, `roslyn_successful_calls=3/3`.
  - trajectory: `discovery_calls=2`, `edit_like_calls=1`, `avg_roslyn_calls_before_first_edit=1`.
- 2026-02-25 (N6 complete): upgraded trajectory analyzer to classify direct `llmstxt` bootstrap calls and transcript-level `mutation_channel` (`roslyn_semantic_edit` vs `text_patch_or_non_roslyn_edit` vs `no_mutation_observed`).
  - re-analysis bundles (`trajectory-roslyn-analysis.v2.json`) confirm both valid `A3 tool-only-v1` runs used `llmstxt` bootstrap (`transcripts_with_llmstxt_calls=1`) and mutated via text patch (`mutation_channel_text_patch_or_non_roslyn_edit=1`, `edit_like_calls=0`).
  - `A2b discovery-lite-v1` valid runs preserved semantic edit channel (`mutation_channel_roslyn_semantic_edit=1`), with lower pre-edit churn than `A2 skill-minimal`.
- 2026-02-25 (N7 prep): added `discovery-lite-v2` harness profile in `Run-PairedAgentRuns.ps1` for CLI/MCP/LSP lanes with:
  - optional single discovery call,
  - first mutation within first 2 Roslyn calls,
  - at-most-one conditional post-edit diagnostics call.
  - smoke-validated profile selection via `Run-PairedAgentRuns.ps1 -RoslynGuidanceProfile discovery-lite-v2 -SkipCodex -SkipClaude`.

Current interpretation:

- Replicate-backed mean (`valid runs`): `A3 tool-only-v1` => `rt=6.5`, `tokens=63345.5`, `duration=37.636` (n=2).
- Replicate-backed mean (`valid runs`): `A2b discovery-lite-v1` => `rt=4.5`, `tokens=68048`, `duration=42.143` (n=2; one invalid replicate excluded).
- `A3` remains better on overhead than `A2` and slightly better than `A2b` on mean tokens/duration, but still shows semantic-edit underuse (`edit_like_calls=0` across both valid runs).
- `A2b` preserves semantic-edit usage (`edit_like_calls=1` in both valid runs) but still incurs overhead from discovery/bootstrap and extra verification loops.
- Reliability gap is now instrumented: mutation channel explicitly distinguishes Roslyn semantic edits from text-patch mutation paths.

## Current A1/A2/A3/A2b Snapshot (Replicate-Aware)

- `A1 surgical` (n=1 valid): treatment `round_trips=4`, `total_tokens=57060`, `duration_seconds=35.351`, `roslyn_successful_calls=1/2`.
- `A2 skill-minimal` (n=1 valid): treatment `round_trips=8`, `total_tokens=80681`, `duration_seconds=50.986`, `roslyn_successful_calls=7/7`.
- `A3 tool-only-v1` (n=2 valid): mean treatment `round_trips=6.5`, `total_tokens=63345.5`, `duration_seconds=37.636`, mean Roslyn success rate `1.0`.
- `A2b discovery-lite-v1` (n=2 valid, 1 invalid excluded): mean treatment `round_trips=4.5`, `total_tokens=68048`, `duration_seconds=42.143`, mean Roslyn success rate `1.0`.
- Trajectory pattern: `A3` valid runs show `edit_like_calls=0`; `A2b` valid runs show `edit_like_calls=1`.
- Caution: cross-arm control baselines differ; keep interpretation directional until normalized replicate matrix is complete.

## Next Node Recommendation

- `N7` (active): tighten discovery-lite prompt to cap diagnostics loops (single post-edit diagnostics pass unless errors) and compare against `tool-only-v1`.
- `N5` (active): implement constrained bootstrap experiments (`llmstxt` section-targeting or one concise contract hint) and rerun A2b vs A3 with normalized control baselines.
- `N6` (complete): trajectory scoring now classifies `llmstxt` bootstrap calls and reports transcript-level `mutation_channel`.
