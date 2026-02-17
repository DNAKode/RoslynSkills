# Checkpoint 2026-02-13

## Current Reality

- RoslynSkills (`roscli` + MCP) is working and benchmarkable, but on low-ambiguity microtasks the tooling often feels like overhead (more wall-clock time; sometimes more tokens) with no correctness upside.
- Prompt posture is a first-class variable: tight “brief-first” guidance can reliably induce a minimal 2-call workflow (edit, then diagnostics) and clamp output.
- The OSS pilot is now scoreable end-to-end and fail-closed for treatment integrity (`treatment-roslyn-required`), with an explicit primary control/treatment pair in the manifest.

## What Changed Most Recently (Empirical)

- `brief-first-v4` is the current “tight default” for paired microtasks: exactly 2 Roslyn calls when coordinates are known; 1 lookup fallback; 2-line response.
- `brief-first-v5` reduced a large MediatR treatment token blowup vs earlier runs, but also exposed that OSS prompts must be TFM-aware to avoid false failures.

## Why It Still Doesn’t Feel Useful (Hypothesis)

- For trivial/low-ambiguity edits, semantic targeting doesn’t buy much; tool startup + extra turns dominate.
- Value is expected to show up in ambiguity-heavy and multi-file tasks (collision, overloads, cross-project references), where text-first editing has higher error/redo rates.

## Pivot: Claude “Skill-Style” Guidance

Read and extracted the key “Skills for Claude” principles into our harness guidance strategy:

- progressive disclosure (minimal happy path; only escalate to contracts/catalog if failing),
- explicit success criteria (tool call budget, “done” definition),
- embedded troubleshooting (what to do on schema errors, ambiguity, or workspace binding issues),
- performance comparison as the iteration loop (tokens/tool calls/failures vs baseline).

Implementation hook:

- Added a new paired-harness guidance profile: `skill-tight-v1` in `benchmarks/scripts/Run-PairedAgentRuns.ps1`.

Empirical sign of benefit (Claude microtask, single replicate):

- `brief-first-v4` treatment: `883` tokens, `4` round trips, `+20.465s` vs control.
- `skill-tight-v1` treatment: `272` tokens, `2` round trips, `+4.607s` vs control.
- Evidence: `artifacts/real-agent-runs/20260213-claude-brief-first-v4-paired-r1/paired-run-summary.md` and `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-paired-r2/paired-run-summary.md`.

## Next Steps (Fast, Empirical)

1. Replicate the Claude A/B (`brief-first-v4` vs `skill-tight-v1`) to confirm the direction holds across runs.
2. Expand benchmark scope with higher-ambiguity OSS tasks (multi-file, collisions, overloads) using the tight profile, and require replicates before interpreting deltas.
