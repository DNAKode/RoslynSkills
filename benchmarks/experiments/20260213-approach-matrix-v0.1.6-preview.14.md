# Approach Matrix (v0.1.6-preview.14)

Date: 2026-02-13
Published: v0.1.6-preview.14  
Purpose: consolidate current evidence across approaches/scenarios and separate experimental confounds from Roslyn-tooling learnings.

## Sources

- Paired microtask (project task shape, Codex):
  - `artifacts/real-agent-runs/20260213-081019-paired/paired-run-summary.md`
  - `artifacts/real-agent-runs/20260213-081019-paired/paired-run-summary.json`
- Paired microtask follow-ups (project task shape, Codex):
  - `artifacts/real-agent-runs/20260213-103432-paired/paired-run-summary.md`
  - `artifacts/real-agent-runs/20260213-103545-paired/paired-run-summary.md`
- MCP interop sweep (project task shape, Codex + Spark, low/high):
  - `artifacts/real-agent-runs/20260213-mcp-interop-workspace-v4/codex-mcp-interop-summary.md`
- OSS pilot (Avalonia, Codex Spark, low):
  - `artifacts/real-agent-runs/20260213-075926-oss-csharp-pilot/*`
  - `artifacts/real-agent-runs/20260213-080511-oss-csharp-pilot/*`
  - run records (latest overwrite semantics): `benchmarks/experiments/oss-csharp-pilot-v1/runs/*.json`
- OSS pilot treatment-required (MediatR, Codex Spark, low):
  - `artifacts/real-agent-runs/20260213-110716-oss-csharp-pilot/*`
  - run record: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-110716-oss-csharp-pilot/run-codex-treatment-roslyn-required-mediatr-behavior-targeting-brief-first-r01.json`
- OSS pilot treatment-required (Codex Spark, low, tight prompting):
  - `artifacts/real-agent-runs/20260213-140558-oss-csharp-pilot/*`
  - `artifacts/real-agent-runs/20260213-141123-oss-csharp-pilot/*`
  - run records:
    - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-140558-oss-csharp-pilot/run-codex-control-text-only-mediatr-behavior-targeting-brief-first-v4-r01.json`
    - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-140558-oss-csharp-pilot/run-codex-treatment-roslyn-required-mediatr-behavior-targeting-brief-first-v4-r01.json`
    - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-141123-oss-csharp-pilot/run-codex-control-text-only-fluentvalidation-rule-disambiguation-brief-first-v4-r01.json`
    - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-141123-oss-csharp-pilot/run-codex-treatment-roslyn-required-fluentvalidation-rule-disambiguation-brief-first-v4-r01.json`
- OSS pilot scope expansion (Codex Spark, low, `brief-first-v4`):
  - `artifacts/real-agent-runs/20260213-142746-oss-csharp-pilot/*`
  - run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-142746-oss-csharp-pilot/*.json`
  - gate summary: `artifacts/agent-eval/20260213-145801/summary/agent-eval-summary.md`
- OSS pilot prompt/guidance iteration (Codex Spark, low, `brief-first-v5`):
  - `artifacts/real-agent-runs/20260213-150618-oss-csharp-pilot/*`
  - `artifacts/real-agent-runs/20260213-151544-oss-csharp-pilot/*`
  - run records:
    - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-150618-oss-csharp-pilot/*.json`
    - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-151544-oss-csharp-pilot/*.json`
  - gate summaries:
    - `artifacts/agent-eval/20260213-151416/summary/agent-eval-summary.md`
    - `artifacts/agent-eval/20260213-151923/summary/agent-eval-summary.md`

## A) Project Microtask (Codex Spark, brief-first)

Task shape: project (`TargetHarness.csproj`) rename with overload hazards.

| Approach | Duration (s) | Model total tokens | Round trips | Roslyn used | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) | Outcome |
| --- | ---: | ---: | ---: | --- | --- | --- | --- |
| control (no tools) | 7.722 | 26,630 | 1 | false | 0/0 | 0/0 | passed |
| roscli treatment | 40.336 | 45,546 | 3 | true | 1/2 | 2/0 | passed |
| Roslyn MCP treatment | 15.527 | 55,694 | 3 | true | 2/2 | 2/0 | passed |

Read:
- For this low-ambiguity microtask, tools increased tokens/round-trips without improving correctness.
- Between Roslyn-enabled lanes, roscli was lower token overhead than MCP here (but slower wall-clock in this replicate).



## A2) Project Microtask (Codex Spark, brief-first-v2, fail-closed)

Task shape: project (`TargetHarness.csproj`) rename with overload hazards.

Source:
- `artifacts/real-agent-runs/20260213-094724-paired/paired-run-summary.md`

| Approach | Duration (s) | Model total tokens | Round trips | Roslyn used | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) | Outcome |
| --- | ---: | ---: | ---: | --- | --- | --- | --- |
| control (no tools) | 7.295 | 26,057 | 1 | false | 0/0 | 0/0 | passed |
| roscli treatment | 36.831 | 30,162 | 2 | true | 1/2 | 2/0 | passed |

Read:
- With better prompting and workspace correctness fixed, roscli token overhead on this microtask dropped materially vs earlier runs (delta ~4.1k tokens).
- Wall-clock duration remained high due to one timed-out helper attempt before a successful retry.
- `-FailOnMissingTreatmentRoslynUsage` eliminated treatment lanes silently degrading into text-only.


## A3) Project Microtask (Codex, brief-first-v2, high reasoning)

Source:
- `artifacts/real-agent-runs/20260213-094929-paired/paired-run-summary.md`

| Approach | Duration (s) | Model total tokens | Round trips | Roslyn used | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) | Outcome |
| --- | ---: | ---: | ---: | --- | --- | --- | --- |
| control (no tools) | 17.566 | 40,452 | 2 | false | 0/0 | 0/0 | passed |
| roscli treatment | 33.744 | 34,733 | 2 | true | 1/1 | 2/0 | passed |

Read:
- This replicate shows lower token totals in treatment than control, likely due to trajectory variance (control performed high-output directory listing and raw file reads).
- Duration remained higher for treatment (Roslyn helper latency dominates on this trivial task).


## A4) Project Microtask (Codex Spark, brief-first-v2, low reasoning, with MCP arm)

Source:
- `artifacts/real-agent-runs/20260213-103432-paired/paired-run-summary.md`

Task shape: project (`TargetHarness.csproj`) rename with overload hazards.

| Approach | Duration (s) | Model total tokens | Round trips | Roslyn used | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) | Outcome |
| --- | ---: | ---: | ---: | --- | --- | --- | --- |
| control (no tools) | 6.007 | 25,901 | 1 | false | 0/0 | 0/0 | passed |
| roscli treatment | 19.023 | 20,009 | 1 | true | 1/1 | 2/0 | passed |
| Roslyn MCP treatment | 13.573 | 55,191 | 3 | true | 2/2 | 2/0 | passed |

Read:
- This replicate shows treatment lower tokens than control, likely trajectory variance (control performed raw file read and wrote a detailed explanation; treatment performed one Roslyn helper call and stopped).
- MCP was materially higher token overhead than roscli on this microtask.


## A5) Project Microtask (Codex Spark, brief-first-v2, high reasoning; helper timeout replicate)

Source:
- `artifacts/real-agent-runs/20260213-103545-paired/paired-run-summary.md`

Task shape: project (`TargetHarness.csproj`) rename with overload hazards.

| Approach | Duration (s) | Model total tokens | Round trips | Roslyn used | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) | Outcome |
| --- | ---: | ---: | ---: | --- | --- | --- | --- |
| control (no tools) | 6.688 | 26,602 | 1 | false | 0/0 | 0/0 | passed |
| roscli treatment | 38.647 | 54,330 | 4 | true | 3/4 | 3/0 | passed |

Read:
- This replicate shows tool overhead dominating on a low-ambiguity microtask: duration ratio ~5.8x and token ratio ~2.0x.
- A Roslyn helper attempt timed out, adding extra tool round-trips (and dominating duration).


## A6) Project Microtask (Codex Spark, brief-first-v4 "tight commands" guidance)

Task shape: project (`TargetHarness.csproj`) rename with overload hazards.

Sources:
- low: `artifacts/real-agent-runs/20260213-113738-paired/paired-run-summary.md`
- medium: `artifacts/real-agent-runs/20260213-113818-paired/paired-run-summary.md`
- high: `artifacts/real-agent-runs/20260213-113904-paired/paired-run-summary.md`

Result (Codex Spark, project task shape, `brief-first-v4`, fail-closed):
- low:
  - control: `tokens=35,633`, `duration_seconds=8.904`.
  - treatment: `tokens=28,954`, `duration_seconds=13.991`, `roslyn_calls=2/2`.
- medium:
  - control: `tokens=36,778`, `duration_seconds=10.934`.
  - treatment: `tokens=29,498`, `duration_seconds=15.933`, `roslyn_calls=2/2`.
- high:
  - control: `tokens=27,285`, `duration_seconds=8.107`.
  - treatment: `tokens=29,804`, `duration_seconds=14.544`, `roslyn_calls=2/2`.

Read:
- The v4 guidance reliably induces the intended minimal tool sequence (2 successful Roslyn calls; workspace-bound) and reduces response verbosity.
- Duration remains higher for treatment on this low-ambiguity microtask; token deltas are smaller and can still invert due to control trajectory variance.
## B) MCP Interop Sweep (Codex + Spark)

Task shape: project.

Key outcome (see source table for full grid):
- `control` completes quickly for both models/efforts.
- `roslyn-mcp` completes reliably but uses materially more tokens than control.
- `lsp-mcp` and `roslyn-plus-lsp-mcp` timed out (180s) in this sweep.

## C) Large OSS Pilot (Avalonia)

What we learned (disentangled):
- Tooling/experiment confounds were real and required harness fixes.
- Avalonia requires `git submodule update --init --recursive` (XamlX) and a scoped restore to avoid mobile workload restore failures.

Workspace-binding evidence (treatment lane):
- `nav.find_symbol` returned `workspace_context.mode=workspace` and resolved to `src/Avalonia.Base/Avalonia.Base.csproj` in:
  - `artifacts/real-agent-runs/20260213-080511-oss-csharp-pilot/avalonia-cornerradius-tryparse/treatment-roslyn-optional/run-codex-treatment-roslyn-optional-avalonia-cornerradius-tryparse-brief-first-r01/transcript.jsonl`



## C2) OSS Pilot (FluentValidation)

Task: `fluentvalidation-rule-disambiguation` (workspace expected)

Evidence:
- run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-095150-oss-csharp-pilot/*.json`
- artifacts:
  - control: `artifacts/real-agent-runs/20260213-095150-oss-csharp-pilot/fluentvalidation-rule-disambiguation/control-text-only/*`
  - treatment: `artifacts/real-agent-runs/20260213-095150-oss-csharp-pilot/fluentvalidation-rule-disambiguation/treatment-roslyn-optional/*`

Result (Codex Spark, low reasoning):
- control: failed acceptance (`dotnet test --nologo`), no Roslyn tool calls.
- treatment (Roslyn optional): passed acceptance, but still recorded zero Roslyn tool calls in telemetry (agent solved via text edits).
- both runs reported very large `input_tokens` (~1.1M to ~1.3M), indicating high prompt/context pressure in large-repo lanes.

Read:
- This replicate is useful as an OSS realism check, but it is confounded for tool effectiveness because the treatment lane did not actually use roscli.
- Next step: add/enable a treatment-required lane (or fail-closed integrity policy) so OSS treatment runs must demonstrate at least one successful Roslyn call (and `workspace_context.mode=workspace`).


## C3) OSS Pilot (MediatR, Treatment-Required)

Task: `mediatr-behavior-targeting` (workspace expected)

Evidence:
- artifacts: `artifacts/real-agent-runs/20260213-110716-oss-csharp-pilot/mediatr-behavior-targeting/treatment-roslyn-required/*`
- run record: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-110716-oss-csharp-pilot/run-codex-treatment-roslyn-required-mediatr-behavior-targeting-brief-first-r01.json`

Result (Codex Spark, low reasoning):
- treatment-required: passed acceptance (`dotnet test --nologo`), with `roslyn_used=true`, `roslyn_successful_calls=2`, and `roslyn_workspace_mode_last=workspace`.

Read:
- This removes the prior OSS confound where “treatment” completed with zero Roslyn tool calls.
- Even for moderate-sized repos, token totals can still be very high; prompt posture and anti-churn constraints remain important.


## C4) OSS Pilot (Treatment-Required, Brief-First-V4 Tight Prompting)

Task shape: OSS repo (workspace expected).

### MediatR (Codex Spark, low)

Sources:
- `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-140558-oss-csharp-pilot/run-codex-control-text-only-mediatr-behavior-targeting-brief-first-v4-r01.json`
- `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-140558-oss-csharp-pilot/run-codex-treatment-roslyn-required-mediatr-behavior-targeting-brief-first-v4-r01.json`

Result:
- control: `duration_seconds=76.197`, `total_tokens=826,667`, passed acceptance.
- treatment-required: `duration_seconds=105.407`, `total_tokens=821,083`, passed acceptance, `roslyn_successful_calls=4`, `roslyn_workspace_mode_last=workspace`.

Read:
- Tight prompting successfully enforced Roslyn usage and workspace binding evidence.
- Token totals remain high but comparable between control and treatment; duration is higher for treatment in this replicate.

### FluentValidation (Codex Spark, low)

Sources:
- `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-141123-oss-csharp-pilot/run-codex-control-text-only-fluentvalidation-rule-disambiguation-brief-first-v4-r01.json`
- `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-141123-oss-csharp-pilot/run-codex-treatment-roslyn-required-fluentvalidation-rule-disambiguation-brief-first-v4-r01.json`

Result:
- control: `duration_seconds=152.917`, `total_tokens=1,925,066`, passed acceptance.
- treatment-required: `duration_seconds=191.752`, `total_tokens=2,153,930`, passed acceptance, `roslyn_successful_calls=3`, `roslyn_workspace_mode_last=workspace`.

Read:
- Treatment-required eliminated the “Roslyn optional but unused” confound for FluentValidation.
- In this replicate, treatment incurred a duration/token overhead despite successful tool usage; need ambiguity-heavy tasks with tighter, more specific prompts to see correctness/efficiency wins.

## C5) OSS Pilot (Treatment-Required, More Concrete Small-Scope Tasks)

Task shape: OSS repo (workspace expected).

Evidence:
- run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-142746-oss-csharp-pilot/*.json`
- artifacts: `artifacts/real-agent-runs/20260213-142746-oss-csharp-pilot/*`
- gate summary: `artifacts/agent-eval/20260213-145801/summary/agent-eval-summary.md`

### MediatR (OpenBehavior null-guard correctness)

Result (Codex Spark, low, `brief-first-v4`):
- control: `duration_seconds=36.392`, `total_tokens=148,327`, passed acceptance.
- treatment-required: `duration_seconds=170.898`, `total_tokens=780,384`, passed acceptance, `roslyn_successful_calls=9`, `roslyn_workspace_mode_last=workspace`.

Read:
- For this narrow change, treatment achieved the intended workspace-bound Roslyn usage but incurred large token/time overhead in this single replicate.

### Serilog (LogContext null-enricher guard)

Result (Codex Spark, low, `brief-first-v4`):
- control: `duration_seconds=32.452`, `total_tokens=216,336`, passed acceptance.
- treatment-required: `duration_seconds=49.318`, `total_tokens=179,588`, passed acceptance, `roslyn_successful_calls=4`, `roslyn_workspace_mode_last=workspace`.

Read:
- This replicate shows treatment lower tokens than control despite using Roslyn calls; still higher wall-clock time.
- Concrete, file-scoped tasks reduce “open-ended exploration” confounds, but token deltas can still invert due to trajectory variance.

## C6) OSS Pilot (Brief-First-V5 Tight Prompting, MediatR Token Clamp Attempt)

Task: `mediatr-openbehavior-nullguard` (workspace expected).

### First attempt (failed due to task prompt ambiguity)

Sources:
- run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-150618-oss-csharp-pilot/*.json`
- artifacts: `artifacts/real-agent-runs/20260213-150618-oss-csharp-pilot/*`
- gate: `artifacts/agent-eval/20260213-151416/summary/agent-eval-summary.md`

Outcome:
- Both control and treatment failed acceptance due to a cross-targeting issue: `ArgumentNullException.ThrowIfNull(...)` does not exist for some of MediatR’s TFMs (e.g. `netstandard2.0`).

### Prompt-fixed rerun (passed)

Sources:
- run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-151544-oss-csharp-pilot/*.json`
- artifacts: `artifacts/real-agent-runs/20260213-151544-oss-csharp-pilot/*`
- gate: `artifacts/agent-eval/20260213-151923/summary/agent-eval-summary.md`

Result (Codex Spark, low, `brief-first-v5`):
- control: `duration_seconds=30.249`, `total_tokens=147,108`, passed acceptance.
- treatment-required: `duration_seconds=83.858`, `total_tokens=371,344`, passed acceptance, `roslyn_successful_calls=4`, `roslyn_workspace_mode_last=workspace`.

Read:
- Compared to the earlier passing v4 bundle for this task (`treatment total_tokens=780,384`), the v5 run reduced treatment token spend substantially, but treatment still cost more than control in this replicate.
## Most Promising Path (Current Read)

1. Default lane for day-to-day: roscli (CLI) with `brief-first` posture and explicit `require_workspace=true` on nav/diag for project code.
2. MCP lane: keep for research/transport experiments and high-ambiguity tasks, but treat token overhead as a known cost until optimized.
3. LSP lane: keep in matrix as comparator, but currently blocked by timeouts; do not use it to draw performance conclusions yet.

## Things To Disentangle

1. LSP MCP timeouts: split into (a) server startup/indexing time, (b) tool-call prompting, (c) per-request timeout policy, (d) transcript parsing/termination behavior.
2. When tools are actually useful: define task families where text-first fails (ambiguity, multi-file refactors, workspace-only diagnostics) and measure on those.
3. Workspace prereqs: submodules, restore scope, and solution filters must be encoded in manifests to avoid "tool looks bad" false negatives.
4. Prompt posture: keep `brief-first` as default and explicitly discourage verbose multi-call exploration before editing.



## D) Workspace Reference Assembly Health (CS0518 False-Positive)

New confound identified:

- It is possible to get `workspace_context.mode=workspace` and still see nonsensical compiler diagnostics like `CS0518` (core types missing).
- This is not a code problem; it is a workspace/MSBuild binding problem (wrong MSBuild instance can yield a project missing reference assemblies).

Evidence (pre-fix):

- `artifacts/real-agent-runs/20260213-092429-paired/codex-treatment/transcript.jsonl` contains `diag.get_file_diagnostics` output with `CS0518`.

Status:

- Fixed on `main` (post `v0.1.6-preview.13`) by preferring `.NET SDK` MSBuild in MSBuildLocator registration.
- Research gate update: treat `workspace_context.mode=workspace` as necessary but not sufficient; additionally fail/redo a run if diagnostics include `CS0518`.

Related harness integrity:

- Treatment lanes can silently degrade into text-only if the agent ignores tooling. Prefer `-FailOnMissingTreatmentRoslynUsage true` on paired runs when collecting comparative evidence.
