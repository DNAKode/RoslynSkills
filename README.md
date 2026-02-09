# RoslynSkill

RoslynSkill is an engineering-first research project that tests whether Roslyn-native tooling materially improves agentic coding workflows in C#/.NET compared with text-first editing.

## Project Intent

- Build production-grade Roslyn command primitives for coding agents.
- Measure end-to-end outcomes using paired control vs Roslyn-enabled runs.
- Optimize for correctness, reliability, and token efficiency with reproducible artifacts.

## Current Status (2026-02-09)

- Active implementation and benchmarking.
- CLI command surface shipped across navigation, context, diagnostics, edits, repair, and live session workflows.
- Paired-run harness supports Codex and Claude runs with control-contamination checks, deterministic post-run constraints, and token/report exports.
- Lightweight real-tool benchmark scaffold is in place and being iterated toward larger, higher-ambiguity tasks.

## Roslyn Command Surface

Current CLI exposes 32 commands grouped across:

- `nav.*`: semantic symbol/references/overrides/implementations
- `ctx.*`: file/member/call-chain/dependency context extraction
- `diag.*`: file/snapshot/diff/proposed-edit diagnostics
- `edit.*`: structured semantic edits and transactions
- `repair.*`: diagnostics-driven repair planning/application
- `session.*`: immutable in-memory edit sessions with diff/diagnostics/commit

Enumerate commands:

```powershell
dotnet run --project src/RoslynAgent.Cli -- list-commands --ids-only
```

Convenience wrappers:

```powershell
scripts\roscli.cmd list-commands --ids-only
scripts\roscli.cmd ctx.file_outline src/RoslynAgent.Core/DefaultRegistryFactory.cs
scripts\roscli.cmd nav.find_symbol src/RoslynAgent.Cli/CliApplication.cs HandleRunDirectAsync --brief true --max-results 5
```

## Repository Guide

- `src/`: RoslynAgent contracts, core commands, CLI, benchmark tooling.
- `tests/`: command/CLI/benchmark validation suites.
- `benchmarks/`: manifests, scripts, prompts, scoring, and report artifacts.
- `skills/roslyn-agent-research/`: installed Roslyn-first operating guidance for coding agents.
- `AGENTS.md`: execution doctrine and meta-learning log.
- `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`: research questions, hypotheses, and evaluation design.

## Validation

Recommended baseline validation:

```powershell
dotnet test RoslynSkill.slnx -c Release
```

## Benchmark Workflow

Primary A/B evaluation workflow:

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-gate --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir> --fail-on-warnings true
```

Quick paired-run harness:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 -OutputRoot <output-dir>
```

Harness outputs include machine-readable summary JSON and markdown summaries with model token totals, cache-inclusive totals, command round-trips, and Roslyn adoption fields.

## License

MIT (`LICENSE`).
