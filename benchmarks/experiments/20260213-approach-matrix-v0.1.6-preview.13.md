# Approach Matrix (v0.1.6-preview.13)

Date: 2026-02-13  
Purpose: consolidate current evidence across approaches/scenarios and separate experimental confounds from Roslyn-tooling learnings.

## Sources

- Paired microtask (project task shape, Codex):
  - `artifacts/real-agent-runs/20260213-081019-paired/paired-run-summary.md`
  - `artifacts/real-agent-runs/20260213-081019-paired/paired-run-summary.json`
- MCP interop sweep (project task shape, Codex + Spark, low/high):
  - `artifacts/real-agent-runs/20260213-mcp-interop-workspace-v4/codex-mcp-interop-summary.md`
- OSS pilot (Avalonia, Codex Spark, low):
  - `artifacts/real-agent-runs/20260213-075926-oss-csharp-pilot/*`
  - `artifacts/real-agent-runs/20260213-080511-oss-csharp-pilot/*`
  - run records (latest overwrite semantics): `benchmarks/experiments/oss-csharp-pilot-v1/runs/*.json`

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
