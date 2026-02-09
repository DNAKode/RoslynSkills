# Creative Skyforge Planner A/B (v1)

Creative two-task control vs Roslyn-enabled benchmark pack.

- Scope: build a themed C# planner, then execute a strong direction pivot with rename + behavior changes.
- Conditions: `control-text-only` vs `treatment-roslyn-optional`.
- Goal: compare trajectory quality, contamination integrity, and token/call behavior on an in-file multi-step update sequence.

## Layout

- `manifest.json`: experiment definition.
- `prompts/`: task prompts.
- `workbench/`: task output target path inside each run workspace.
- `artifacts/`: generated run and gate outputs.
- `CREATIVE_SKYFORGE_REPORT.md`: run summary (after execution).

## Run Command

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-LightweightUtilityGameRealRuns.ps1 `
  -ManifestPath benchmarks/experiments/creative-skyforge-planner-v1/manifest.json `
  -OutputRoot benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v1
```
