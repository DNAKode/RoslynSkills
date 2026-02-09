# Real Tools Mini-Benchmark (2026-02-09, v6)

## Scope

- Task: overload-safe rename in `Target.cs` (`Process(int)` -> `Handle(int)`; update only `Process(1)`).
- Conditions:
  - control: explicit no-Roslyn baseline (text-only operations),
  - treatment: Roslyn helpers available and recommended.
- Agents:
  - Codex CLI (`codex`)
  - Claude Code CLI (`claude`)
- Run bundle: `artifacts/real-agent-paired-v6`

## Harness Improvements Applied Before v6

- `scripts/roscli` and `scripts/roscli.cmd` made cwd-independent.
- Treatment prompts split by agent/shell; Bash-safe helper paths use `./...`.
- Control prompt now explicitly forbids Roslyn helper usage.
- `roslyn_used` now means successful Roslyn calls (`roslyn_successful_calls > 0`), while attempts are tracked separately.

## Outcome Summary

- Correctness: all four runs produced constraint-correct edits.
- Reliability: all four runs exited cleanly (`exit=0`).
- Adoption separation:
  - control runs: `roslyn_used=false`, `roslyn_successful_calls=0`.
  - treatment runs: `roslyn_used=true` for both agents.

## Measured Metrics (v6)

| Agent | Mode | Exit | Roslyn Used | Roslyn Attempted | Roslyn Successful | Round Trips | Prompt Tokens | Completion Tokens | Total Tokens | Cache Read / Cached Input | Cache Creation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| codex | control | 0 | false | 0 | 0 | 2 | 48921 | 761 | 49682 | 43776 | n/a |
| codex | treatment | 0 | true | 5 | 5 | 6 | 107623 | 2209 | 109832 | 91008 | n/a |
| claude | control | 0 | false | 0 | 0 | 0 | 2 | 567 | 569 | 54013 | 5433 |
| claude | treatment | 0 | true | 2 | 2 | 2 | 2 | 1095 | 1097 | 98086 | 7632 |

## Token Delta (Treatment - Control, v6)

- Codex (prompt+completion): `+60,150` (`+121.07%`).
- Codex cache-inclusive (`prompt + completion + cached_input`):
  - control: `93,458`
  - treatment: `200,840`
  - delta: `+107,382` (`+114.90%`)
- Claude (prompt+completion): `+528` (`+92.79%`).
- Claude cache-inclusive (`prompt + completion + cache_read + cache_creation`):
  - control: `60,015`
  - treatment: `106,815`
  - delta: `+46,800` (`+77.98%`)

## Interpretation

- This microtask still shows token increase under Roslyn treatment for both agents.
- Reliability and measurement quality improved materially versus earlier bundles:
  - clean control/treatment separation,
  - successful Roslyn invocation in both treatment runs,
  - reduced spurious fallback attempts.
- For low-ambiguity single-file edits, semantic setup overhead dominates. Larger multi-file/high-ambiguity tasks remain the key target for token-efficiency wins.

## Confounders and Limits

- Cross-provider token semantics differ (especially cache fields).
- Single microtask; not representative of broader coding workloads.
- Provider/system prompts still influence trajectory style and verbosity.

## Artifact Paths

- Paired run bundle: `benchmarks/experiments/lightweight-utility-game-v1/artifacts/real-agent-paired-v6`
- Consolidated metrics JSON: `benchmarks/experiments/lightweight-utility-game-v1/artifacts/real-tools-mini-benchmark-metrics.json`
- Harness script: `benchmarks/scripts/Run-PairedAgentRuns.ps1`
