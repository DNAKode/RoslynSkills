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
- fill in outcome, tool calls, and post-run reflection JSON,
- place completed run JSON in `<runs-dir>`.

Optional shortcut for quick logging:

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-register-run --manifest <manifest.json> --runs <runs-dir> --task <task-id> --condition <condition-id> --succeeded true --compile-passed true --tests-passed true --duration-seconds 120.5
```

Use:

- `benchmarks/prompts/agent-eval-reflection-protocol.md`

for reflection capture format.

## 4. Validate run quality before scoring

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-validate-runs --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir>
```

This check catches:

- control-condition contamination by Roslyn tools,
- malformed run fields,
- invalid Roslyn helpfulness scoring,
- missing or inconsistent run context.

## 5. Score results

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-score --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir>
```

Outputs:

- `agent-eval-report.json` with:
  - control/treatment deltas,
  - Roslyn tool adoption metrics,
  - reflection aggregates.

## 6. Interpretation rule

- Component diagnostics (for example `rq1`) are supporting evidence only.
- End-to-end claims require this A/B pipeline.
