# Checkpoint 2026-02-17

## What Changed

- Expanded `benchmarks/scripts/Run-PairedAgentRuns.ps1` task scope from rename-only to mixed operation families:
  - `change-signature-named-args-v1`
  - `update-usings-cleanup-v1`
  - `add-member-threshold-v1`
  - `replace-member-body-guard-v1`
  - `create-file-audit-log-v1`
- Added operation-specific task definitions, project-shape `Program.cs` templates, and task-aware constraint checks.
- Added `operation-neutral-v1` Roslyn guidance profile to reduce rename-specific prompt leakage on non-rename tasks.
- Strengthened create-file validation by checking `AuditLog.cs` diagnostics in addition to content checks.
- Relaxed `add-member-threshold-v1` constraint to accept both block-bodied and expression-bodied method forms when semantics match.
- Added benchmark script regression test coverage in:
  - `tests/RoslynSkills.Benchmark.Tests/PairedRunHarnessScriptTests.cs`

## Empirical Runs This Slice

### Claude skill sweep (wider non-rename scope)

- Artifact root:
  - `artifacts/skill-tests/20260217-wide-claude-v1/`
- Tasks:
  - `change-signature-named-args-v1`
  - `replace-member-body-guard-v1`
  - `create-file-audit-log-v1`
- Conditions:
  - `no-skill`
  - `with-skill`
  - `with-skill-invoked`
  - `with-skill-invoked-budgeted`

Condition summary (`skill-trigger-summary.md`):

- `no-skill`: `pass_rate=1.0`, `roslyn_used_rate=0.0`, `avg_tool_uses=3.0`
- `with-skill`: `pass_rate=1.0`, `roslyn_used_rate=0.333`, `avg_tool_uses=5.333`
- `with-skill-invoked`: `pass_rate=1.0`, `roslyn_used_rate=1.0`, `avg_tool_uses=9.333`
- `with-skill-invoked-budgeted`: `pass_rate=1.0`, `roslyn_used_rate=0.333`, `task_call_budget_exceeded_rate=0.667`

Interpretation:

- Explicit skill invocation is currently the strongest Roslyn-adoption lever (`1.0` usage rate here).
- Budget prompts reduced calls but also reduced Roslyn adoption consistency in this slice.

### Paired harness spot check (Claude, operation-neutral profile)

- Artifact root:
  - `artifacts/real-agent-runs/20260217-claude-opneutral-addmember-v2/`
- Task:
  - `add-member-threshold-v1` (project shape)
- Profile:
  - `operation-neutral-v1`

Result (`paired-run-summary.md`):

- Control: passed, no Roslyn usage.
- Treatment: passed, Roslyn usage `1/1`.
- Treatment overhead remained high in this microtask (`duration` and `tokens` higher than control), with most round-trips still non-Roslyn.

Interpretation:

- Operation-neutral guidance improved correctness alignment (no rename misdirection on non-rename task).
- Cost/latency remain dominated by extra trajectory/tooling behavior, not command execution correctness.

### Paired harness spot check (Codex, operation-neutral profile)

- Artifact root:
  - `artifacts/real-agent-runs/20260217-codex-opneutral-changesig-v1/`
- Task:
  - `change-signature-named-args-v1` (project shape)
- Profile:
  - `operation-neutral-v1`

Result (`paired-run-summary.md`):

- Control: passed, no Roslyn usage.
- Treatment: passed, Roslyn usage `11/17` successful/attempted calls.
- Overhead was very high in this single run:
  - duration ratio `7.739x`
  - token ratio `8.809x`
  relative to control.

Interpretation:

- The profile can trigger successful Roslyn usage in Codex on non-rename tasks.
- Current prompt/tooling loop still allows large exploratory churn (extra describe/validation/retry behavior), overwhelming any command-level efficiency gains.

## Key Learnings

- Guidance profile text is still a first-order variable: rename-biased recipes can directly invalidate non-rename treatment runs.
- Constraint checks should encode semantic intent, not one syntax style, or benchmark pass/fail becomes noisy.
- For Claude skill evaluation, explicit invocation remains necessary to get reliable Roslyn adoption.

## Next Steps

1. Add a multi-task paired-run driver for codex/claude that accepts task arrays without PowerShell binding friction.
2. Create task-family-specific call budgets for `operation-neutral-v1` and track budget compliance in paired runs.
3. Run a replicate-backed codex+claude matrix over at least 4 mixed operations using `operation-neutral-v1`, then update `RESEARCH_FINDINGS.md`.

## Tool-Thinking Split Deep Dive Update

### Tooling hardening

- Fixed a strict-mode analyzer reliability bug in `Analyze-ToolThinkingSplit.ps1` where scalar collections could throw on `.Count`.
- Added executable regression coverage in:
  - `tests/RoslynSkills.Benchmark.Tests/ToolThinkingSplitScriptTests.cs`
- Updated split runner to inject a concrete host roscli launcher path into treatment prompts and explicit control-lane prohibition:
  - `benchmarks/scripts/Run-ToolThinkingSplitExperiment.ps1`

### External medium-repo split runs (MediatR)

- Task:
  - fix invalid non-notification publish error-message formatting and add regression assertion.
- Acceptance:
  - `dotnet test test/MediatR.Tests/MediatR.Tests.csproj --nologo`

Artifacts:

- Pre-injection Codex (treatment Roslyn usage missing):
  - `artifacts/tool-thinking-split-runs/20260217-084934-codex-mediatr-invalid-notification-codex-v1/`
- Post-injection Codex:
  - `artifacts/tool-thinking-split-runs/20260217-085723-codex-mediatr-invalid-notification-codex-v2/`
- Post-injection Claude:
  - `artifacts/tool-thinking-split-runs/20260217-090210-claude-mediatr-invalid-notification-claude-v1/`

Post-injection deltas (treatment-control):

- Codex:
  - `roslyn_command_count +3`
  - `command_round_trips +2`
  - `events_before_first_edit +11`
  - `discovery_commands_before_first_edit +3`
  - `failed_commands_before_first_edit +1`
  - `total_tokens +31134`
- Claude:
  - `roslyn_command_count +1`
  - `command_round_trips +1`
  - `events_before_first_edit +5`
  - `discovery_commands_before_first_edit +1`
  - `failed_commands_before_first_edit +0`
  - `total_tokens +74` (`cache_inclusive_total_tokens +22270`)

Interpretation:

- On external repos, launcher discoverability is a validity gate for treatment lanes; without it, treatment can silently degrade to text-only.
- With launcher injection, both agents showed non-zero Roslyn usage with clean control lanes (`control_roslyn_contamination=false`), and overhead remained concentrated pre-edit.

### Prompt-profile iteration (`standard` vs `tight`)

Codex matched pair on the same MediatR task:

- Standard:
  - `artifacts/tool-thinking-split-runs/20260217-091745-codex-mediatr-invalid-notification-codex-v3-standard/`
  - deltas: `roslyn +11`, `round_trips +12`, `events_before_first_edit +34`, `total_tokens +180969`
- Tight:
  - `artifacts/tool-thinking-split-runs/20260217-091218-codex-mediatr-invalid-notification-codex-v3-tight/`
  - deltas: `roslyn +4`, `round_trips +6`, `events_before_first_edit +17`, `total_tokens +74894`

Claude direction check:

- baseline:
  - `artifacts/tool-thinking-split-runs/20260217-090210-claude-mediatr-invalid-notification-claude-v1/`
- tight:
  - `artifacts/tool-thinking-split-runs/20260217-092330-claude-mediatr-invalid-notification-claude-v2-tight/`
- effect:
  - `events_before_first_edit` improved (`+5` -> `+3`) with same Roslyn usage (`+1`).
