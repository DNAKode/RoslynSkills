# Lightweight Utility/Game A/B (v1)

This experiment is a first lightweight, end-to-end dry run of the real agent-eval reporting shape.

- Scope: one creative utility/game task plus two follow-up direction changes.
- Conditions: control text-first vs Roslyn-enabled treatment.
- Primary focus: token efficiency, retries, and trajectory quality.

## Layout

- `manifest.json`: experiment definition.
- `prompts/`: initial prompt and follow-up prompts.
- `runs/`: paired run logs for each task and condition.
- `artifacts/`: validator/scorer/export outputs (generated).
- `artifacts/real-agent-paired-v6`: latest clean real-tool paired run (Codex + Claude, control + treatment).
- `LIGHTWEIGHT_RUN_REPORT.md`: synthesized narrative report.
- `REAL_TOOLS_MINI_BENCHMARK_REPORT.md`: real Codex/Claude CLI mini-benchmark summary.

## Re-run Commands

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-gate `
  --manifest benchmarks/experiments/lightweight-utility-game-v1/manifest.json `
  --runs benchmarks/experiments/lightweight-utility-game-v1/runs `
  --output benchmarks/experiments/lightweight-utility-game-v1/artifacts `
  --fail-on-warnings true
```

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -OutputRoot benchmarks/experiments/lightweight-utility-game-v1/artifacts/real-agent-runs
```
