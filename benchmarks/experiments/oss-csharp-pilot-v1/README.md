# OSS C# Pilot v1

This is the first realistic public-OSS trial pack for control/treatment A/B runs.

## Files

- `manifest.json`: experiment definition
- `prompts/*.md`: task-specific instructions used by run operators/agents

## Suggested flow

1. Validate manifest:
   - `dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-validate-manifest --manifest benchmarks/experiments/oss-csharp-pilot-v1/manifest.json`
2. Generate worklist and pending templates:
   - `dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-init-runs --manifest benchmarks/experiments/oss-csharp-pilot-v1/manifest.json --runs <runs-dir>`
3. Execute control/treatment runs and save JSON outputs under `<runs-dir>`.
4. Validate runs:
   - `dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-validate-runs --manifest benchmarks/experiments/oss-csharp-pilot-v1/manifest.json --runs <runs-dir>`
5. Score:
   - `dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-score --manifest benchmarks/experiments/oss-csharp-pilot-v1/manifest.json --runs <runs-dir>`
6. Export markdown summary:
   - `dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-export-summary --report <agent-eval-report.json> --run-validation <agent-eval-run-validation.json>`
7. Or run gate end-to-end in one command:
   - `dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-gate --manifest benchmarks/experiments/oss-csharp-pilot-v1/manifest.json --runs <runs-dir>`
