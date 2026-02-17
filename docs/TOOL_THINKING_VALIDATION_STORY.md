# Tool-Thinking Validation Story (Split-Lane Transcript Study)

Date: 2026-02-17

## Goal

Disentangle three effects that are currently confounded in aggregate benchmark deltas:

1. prompt/context overhead introduced by Roslyn guidance/tooling,
2. exploration overhead before first edit,
3. downstream navigation/edit correctness gains once tooling is engaged.

## Experimental shape

Use a medium repository and run two lanes from the same commit:

- `control`: no RoslynSkills usage allowed.
- `treatment`: RoslynSkills enabled with concise pit-of-success guidance.

Both lanes get:

- the same high-level project background (no deep file excerpts),
- the same task statement,
- the same acceptance checks.

Recommended execution style:

- Use Claude fork/split-session behavior (or equivalent in other agents) so both lanes share initial conversational context but diverge only on tool policy.

## New helper tooling

Scaffold experiment artifacts:

```powershell
pwsh benchmarks/scripts/New-ToolThinkingSplitExperiment.ps1 `
  -Agent claude `
  -TaskLabel "medium-repo-null-guard" `
  -BackgroundFile docs/backgrounds/medium-repo.md `
  -TaskPromptFile docs/tasks/null-guard.md `
  -TreatmentGuidanceProfile tight
```

Run full split-lane experiment end-to-end (same commit, isolated lane workspaces, transcripts, acceptance gate, analysis):

```powershell
pwsh benchmarks/scripts/Run-ToolThinkingSplitExperiment.ps1 `
  -Agent claude `
  -RepoPath C:\Work\SomeMediumRepo `
  -GitRef HEAD `
  -TaskLabel "medium-repo-null-guard" `
  -BackgroundFile docs/backgrounds/medium-repo.md `
  -TaskPromptFile docs/tasks/null-guard.md `
  -TreatmentGuidanceProfile tight `
  -AcceptanceCommand "dotnet test --nologo"
```

Note:

- `Run-ToolThinkingSplitExperiment.ps1` now auto-injects a resolved host `roscli` launcher path into treatment prompts (and explicit prohibition in control prompts) so external repos can still access RoslynSkills without local repo scripts.
- Treatment prompt posture is profile-driven: `standard` (pit-of-success full flow) or `tight` (bounded pre-edit Roslyn discovery with direct semantic lookup bias).

Analyze paired transcripts:

```powershell
pwsh benchmarks/scripts/Analyze-ToolThinkingSplit.ps1 `
  -ControlTranscript artifacts/tool-thinking-split-runs/.../control.transcript.jsonl `
  -TreatmentTranscript artifacts/tool-thinking-split-runs/.../treatment.transcript.jsonl `
  -OutputJson artifacts/tool-thinking-split-runs/.../thinking-metrics.json `
  -OutputMarkdown artifacts/tool-thinking-split-runs/.../thinking-summary.md
```

## Primary metrics

- `total_events` delta (interaction overhead).
- `command_round_trips` delta.
- `roslyn_command_count` delta (actual Roslyn adoption, not just offered tools).
- `productive_roslyn_command_count` delta (non-schema Roslyn work, not just discovery/probing).
- `schema_probe_count` delta (argument-discovery churn).
- `events_before_first_edit` delta (upfront exploration cost).
- `events_before_first_productive_roslyn` (time-to-first productive semantic action).
- `discovery_commands_before_first_edit` and `failed_commands_before_first_edit` deltas.
- `semantic_edit_first_try_success_rate` delta.
- `verify_after_edit_rate` delta.
- `roslyn_used_well_score` delta (composite trajectory quality score; zero when no productive Roslyn command occurs).
- `retry_recoveries` delta (error recovery loops).
- token deltas where transcript usage fields are available (`total_tokens`, `cache_inclusive_total_tokens`).
- pass/fail against acceptance checks.

## Interpretation rules

- If treatment has higher pre-edit overhead but better pass rate/correctness, optimization target is prompt/tool posture, not tool removal.
- If treatment overhead is high and correctness is flat, reduce guidance and command-discovery churn.
- If treatment is both lower-overhead and higher-correctness, promote that profile to default benchmark posture.

## Artifact requirements

For each split run keep:

- both raw transcripts,
- paired metric summary (`thinking-metrics.json`, `thinking-summary.md`),
- task acceptance results,
- short reflection note in `RESEARCH_FINDINGS.md`.

## Latest empirical snapshot (external medium repo)

Repository/task:

- MediatR (`be61f5a`), fix invalid non-notification publish error message and add regression assertion (`dotnet test test/MediatR.Tests/MediatR.Tests.csproj --nologo` acceptance).

Runs:

- Codex (with launcher-injected treatment): `artifacts/tool-thinking-split-runs/20260217-085723-codex-mediatr-invalid-notification-codex-v2/`
- Claude (with launcher-injected treatment): `artifacts/tool-thinking-split-runs/20260217-090210-claude-mediatr-invalid-notification-claude-v1/`

Observed deltas (treatment - control):

- Codex:
  - `roslyn_command_count`: `+3`
  - `command_round_trips`: `+2`
  - `events_before_first_edit`: `+11`
  - `discovery_commands_before_first_edit`: `+3`
  - `failed_commands_before_first_edit`: `+1`
  - `total_tokens`: `+31134`
- Claude:
  - `roslyn_command_count`: `+1`
  - `command_round_trips`: `+1`
  - `events_before_first_edit`: `+5`
  - `discovery_commands_before_first_edit`: `+1`
  - `failed_commands_before_first_edit`: `+0`
  - `total_tokens`: `+74` (`cache_inclusive_total_tokens`: `+22270`)

Interpretation:

- With explicit launcher injection, treatment Roslyn usage became reliable enough for both agents on an external repo while keeping control uncontaminated (`control_roslyn_contamination=false`).
- Overhead remains concentrated pre-edit (discovery/schema steps before first edit), but absolute overhead in this task family is now bounded rather than runaway.
- New `roslyn_used_well` signals show a critical distinction between Roslyn presence and Roslyn quality:
  - Codex runs produced non-zero productive Roslyn commands and non-zero used-well scores.
  - Claude runs in this task family only executed schema/probe Roslyn calls (`productive_roslyn_command_count=0`), resulting in `roslyn_used_well_score=0`.

## Prompt profile iteration snapshot (MediatR)

Codex matched pair (`standard` vs `tight`):

- Standard:
  - `artifacts/tool-thinking-split-runs/20260217-091745-codex-mediatr-invalid-notification-codex-v3-standard/`
  - `roslyn_command_count +11`, `command_round_trips +12`, `events_before_first_edit +34`, `total_tokens +180969`
- Tight:
  - `artifacts/tool-thinking-split-runs/20260217-091218-codex-mediatr-invalid-notification-codex-v3-tight/`
  - `roslyn_command_count +4`, `command_round_trips +6`, `events_before_first_edit +17`, `total_tokens +74894`

Claude direction check:

- Baseline profile:
  - `artifacts/tool-thinking-split-runs/20260217-090210-claude-mediatr-invalid-notification-claude-v1/`
- Tight:
  - `artifacts/tool-thinking-split-runs/20260217-092330-claude-mediatr-invalid-notification-claude-v2-tight/`
- Tight reduced `events_before_first_edit` (`+5` -> `+3`) and non-cache token delta (`+74` -> `+48`) while preserving Roslyn usage (`+1`).
