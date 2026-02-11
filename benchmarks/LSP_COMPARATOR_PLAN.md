# LSP Comparator Plan (RoslynSkills vs External C# LSP)

Date: 2026-02-11

## Problem Statement

Determine whether external C# LSP tooling (for example Claude `csharp-lsp`) is used more effectively than RoslynSkills (`roscli` CLI/MCP), and whether RoslynSkills adds value beyond what LSP already provides.

## Constraints

- Same task prompts and acceptance checks across conditions.
- Condition isolation required:
  - control: no RoslynSkills, no C# LSP.
  - Roslyn treatment: RoslynSkills allowed, C# LSP disallowed.
  - LSP treatment: C# LSP allowed, RoslynSkills disallowed.
  - optional mixed lane: both allowed.
- Agent environments must stay isolated per run (home/config/workspace).
- Scoring must capture tool adoption and usefulness, not only pass/fail.

## Non-Goals

- Declaring a universal winner from one microtask.
- Replacing RoslynSkills roadmap based on anecdotal runs.
- Cross-language conclusions.

## Dependency Graph

- D1: Harness lane support (`Run-PairedAgentRuns.ps1` mode + telemetry).
- D2: LSP usage detection and summary fields.
- D3: Prompt guidance profiles for LSP lane.
- D4: Run-quality gates and contamination checks updated for LSP.
- D5: Replicate bundle execution and artifact scoring.

Edges:

- D1 -> D2
- D1 -> D3
- D2 + D3 -> D4
- D4 -> D5

## Validation Gates

- Build/test baseline passes.
- Harness smoke run succeeds with no agent execution regression.
- Run metadata includes:
  - `lsp_used`,
  - `lsp_attempted_calls`,
  - `lsp_successful_calls`,
  - `lsp_command_round_trips`.
- Summary markdown includes control-vs-treatment-lsp breakout when lane is present.
- Control contamination gate fails if Roslyn or LSP usage appears in control condition.

## Rollback / Fallback Strategy

- Keep LSP lane behind explicit flag (`-IncludeClaudeLspTreatment`).
- If telemetry classification is noisy:
  - keep lane execution enabled,
  - mark usage counters as provisional,
  - rely on transcript evidence until parser rules are refined.
- If condition contamination is unstable, enforce stricter hard-fail policy before interpreting deltas.

## First Experimental Bundle (Planned)

Run paired harness with:

- `-IncludeMcpTreatment`
- `-IncludeClaudeLspTreatment`
- at least two guidance profiles (`standard`, `brief-first`)
- both agents where available (Codex and Claude), noting that LSP lane is Claude-focused.

Then archive:

- `paired-run-summary.json`,
- `paired-run-summary.md`,
- per-run transcripts,
- scored deltas in `RESEARCH_FINDINGS.md`.

## Follow-on Comparator Expansion: dotnet-inspect / dotnet-skills

After first LSP comparator replicates, add a parallel external-tool comparator family focused on dependency/API intelligence tasks:

- control: no RoslynSkills, no dotnet-inspect.
- inspect-only: `dotnet-inspect`/`dotnet-skills` allowed, RoslynSkills hidden.
- roslyn-only: RoslynSkills allowed, `dotnet-inspect` hidden.
- combined: both tool families allowed.

Primary question:

- Does `dotnet-inspect` replace RoslynSkills, or is the best outcome combinational (dependency intelligence + workspace-semantic edits)?

Task-family guidance:

- Use tasks that require package API discovery, version diffs, or extension method discovery before local code edits.
- Avoid pure local-symbol rename tasks for this comparator; those underrepresent `dotnet-inspect` strengths.
