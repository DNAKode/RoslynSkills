# Approach Matrix (v0.1.6-preview.13 pending)

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
