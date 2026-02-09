# Agent-In-Loop A/B Workflow

This workflow is the primary evidence path for Roslyn utility claims.

## 1. Prepare manifest

Start from:

- `benchmarks/templates/agent-eval-manifest.template.json`

You can also use the first OSS pilot pack:

- `benchmarks/experiments/oss-csharp-pilot-v1/manifest.json`

Set:

- `conditions` (control/treatment),
- `tasks`,
- `runs_per_cell`,
- `roslyn_tool_prefixes`.

## 1.5 Run environment preflight

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-preflight --output <artifact-dir>
```

Required checks:

- `dotnet`, `git`, `rg`

Optional checks:

- `codex`, `claude`

## 1.7 Validate manifest quality

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-validate-manifest --manifest <manifest.json> --output <artifact-dir>
```

## 2. Generate worklist and pending run templates

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-init-runs --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir>
```

Outputs:

- `agent-eval-worklist.json`
- `pending-run-templates/*.json`

## 3. Execute real agent runs

For each pending template:

- run the task in the target agent environment (Codex CLI, Claude Code, etc.),
- fill in outcome, tool calls, token counts (`prompt_tokens`, `completion_tokens`, `total_tokens`), and post-run reflection JSON,
- place completed run JSON in `<runs-dir>`.

When available, capture short transcript fragments (command snippets and failure/recovery highlights) in run notes so qualitative impact can be quoted in reports.

Optional shortcut for quick logging:

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-register-run --manifest <manifest.json> --runs <runs-dir> --task <task-id> --condition <condition-id> --succeeded true --compile-passed true --tests-passed true --duration-seconds 120.5
```

Use:

- `benchmarks/prompts/agent-eval-reflection-protocol.md`

for reflection capture format.

## 4. Validate run quality before scoring

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-validate-runs --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir> [--fail-on-warnings true]
```

This check catches:

- control-condition contamination by Roslyn tools,
- malformed run fields,
- invalid Roslyn helpfulness scoring,
- missing or inconsistent run context.

`--fail-on-warnings true` makes warning-level findings fail the command (exit code 2) for stricter gating.

## 5. Score results

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-score --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir>
```

Outputs:

- `agent-eval-report.json` with:
  - control/treatment deltas,
  - per-task control/treatment comparison slices,
  - Roslyn tool adoption metrics,
  - reflection aggregates.

Optional report export:

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-export-summary --report <agent-eval-report.json> --run-validation <agent-eval-run-validation.json> --output <artifact-dir>
```

Optional single-command gate run (runs validation + scoring + summary export):

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-gate --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir> [--fail-on-warnings true]
```

With `--fail-on-warnings true`, the gate treats run-validation warnings as hard failures.

## 5.5 Optional paired real-agent harness

For fast paired control/treatment smoke runs with transcript + token attribution artifacts:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 -OutputRoot <artifact-dir>
```

Current harness outputs include:

- `paired-run-summary.json`
- `paired-run-summary.md`
- per-run metadata with:
  - control contamination detection,
  - deterministic rename constraint checks,
  - Roslyn attempted/successful call counts,
  - model token totals and cache-inclusive token totals,
  - command round-trip and transcript character attribution.

## 6. Interpretation rule

- Component diagnostics (for example `rq1`) are supporting evidence only.
- End-to-end claims require this A/B pipeline.
