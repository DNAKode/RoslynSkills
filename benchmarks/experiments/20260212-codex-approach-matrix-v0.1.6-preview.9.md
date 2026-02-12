# Codex Tooling Approach Matrix (v0.1.6-preview.9)

Date: 2026-02-12
Local repo describe: `v0.1.6-preview.9-3-g1b06367`

## Scope

This matrix compares currently explored Codex-side approaches:

- `control` (no Roslyn/LSP tools)
- `roscli` (CLI helper lane)
- `roslyn-mcp`
- `lsp-mcp` (`cclsp` via MCP)
- `roslyn-plus-lsp-mcp`

across model variants and task shapes, with explicit notes where results are non-comparable due experiment defects.

## Sources

- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-codex-project/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-codex-singlefile/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-spark-project/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-spark-singlefile/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v6b-cclsp-duration/codex-mcp-interop-summary.json`
- `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v8-spark-cclsp-duration/codex-mcp-interop-summary.json`
- `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v9-codex-lsp-medium-xhigh/codex-mcp-interop-summary.json`
- `artifacts/real-agent-runs/20260212-revisionsize-codex-v2/runs/run-codex-control-control-text-only-task-001-initial-build-r01.json`
- `artifacts/real-agent-runs/20260212-revisionsize-codex-v3-task001/runs/run-codex-control-control-text-only-task-001-initial-build-r01.json`
- `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/runs/run-codex-treatment-treatment-roslyn-optional-task-001-initial-build-r01.json`
- `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/runs/run-codex-treatment-treatment-roslyn-published-cache-task-001-initial-build-r01.json`

## Matrix A: Roscli vs MCP vs Control (paired rename)

| Model | Task shape | Approach | Duration (s) | Tokens | Round trips | Roslyn calls | Workspace ctx (workspace/ad_hoc) |
| --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| gpt-5.3-codex | project | control | 22.498 | 33,662 | 3 | 0 | 0/0 |
| gpt-5.3-codex | project | roscli | 34.980 | 29,235 | 2 | 1 | 1/0 |
| gpt-5.3-codex | project | roslyn-mcp | 36.237 | 64,893 | 4 | 2 | 1/0 |
| gpt-5.3-codex | single-file | control | 21.248 | 33,590 | 2 | 0 | 0/0 |
| gpt-5.3-codex | single-file | roscli | 29.082 | 28,170 | 2 | 1 | 0/1 |
| gpt-5.3-codex | single-file | roslyn-mcp | 33.441 | 63,732 | 4 | 2 | 0/1 |
| gpt-5.3-codex-spark | project | control | 24.296 | 34,748 | 2 | 0 | 0/0 |
| gpt-5.3-codex-spark | project | roscli | 46.630 | 29,756 | 2 | 1 | 1/0 |
| gpt-5.3-codex-spark | project | roslyn-mcp | 13.215 | 39,644 | 2 | 2 | 1/0 |
| gpt-5.3-codex-spark | single-file | control | 12.395 | 25,832 | 1 | 0 | 0/0 |
| gpt-5.3-codex-spark | single-file | roscli | 19.064 | 19,836 | 1 | 1 | 0/1 |
| gpt-5.3-codex-spark | single-file | roslyn-mcp | 19.014 | 52,882 | 3 | 2 | 0/1 |

Readout:

- Roscli is consistently lower-token than control and roslyn-mcp in this paired task family.
- Roslyn-mcp remains significantly higher-token than roscli in all four cells.
- Spark project-shape had one outlier where roslyn-mcp was fastest wall-clock but still higher-token than roscli.

## Matrix B: MCP approach sweep (project, low effort)

| Model | Effort | Scenario | Duration (s) | Tokens | Round trips | Roslyn calls | LSP calls |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| gpt-5.3-codex | low | control | 43.424 | 64,343 | 6 | 0 | 0 |
| gpt-5.3-codex | low | lsp-mcp | 49.926 | 63,765 | 10 | 0 | 2 |
| gpt-5.3-codex | low | roslyn-mcp | 49.120 | 123,739 | 11 | 8 | 0 |
| gpt-5.3-codex | low | roslyn-plus-lsp-mcp | 43.801 | 89,019 | 12 | 5 | 2 |
| gpt-5.3-codex-spark | low | control | 29.886 | 51,309 | 4 | 0 | 0 |
| gpt-5.3-codex-spark | low | lsp-mcp | 41.073 | 74,626 | 8 | 0 | 2 |
| gpt-5.3-codex-spark | low | roslyn-mcp | 60.320 | 149,819 | 10 | 7 | 0 |
| gpt-5.3-codex-spark | low | roslyn-plus-lsp-mcp | 58.011 | 92,710 | 12 | 5 | 1 |

Readout:

- `roslyn-mcp` is the highest-token lane in both Codex and Spark low-effort settings.
- `lsp-mcp` can stay near control for Codex low in token count, but usually costs extra round-trips/time.
- `roslyn-plus-lsp-mcp` can reduce tokens vs roslyn-mcp, but remains above control.

## Matrix C: Open-ended larger revision task (`task-001`) status snapshot

| Bundle | Model | Condition | Duration (s) | Tokens | Round trips | Roslyn calls | Comparable with control? |
| --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| revisionsize-codex-v2 | gpt-5.3-codex | control-text-only | 281.021 | 417,091 | 20 | 0 | Yes (control only) |
| revisionsize-codex-v3-task001 | gpt-5.3-codex | control-text-only | 377.323 | 996,811 | 31 | 0 | Yes (control only) |
| lightweight-roscli-mode-v5 | codex-default | treatment-roslyn-optional | 367.936 | 1,001,800 | 32 | 2 | Not directly paired |
| lightweight-roscli-mode-v5 | codex-default | treatment-roslyn-published-cache | 232.819 | 428,723 | 15 | 3 | Not directly paired |

Readout:

- Open-ended task trajectories show extreme variance (for example control moved from 417k to 997k tokens across runs).
- Roscli published-cache can materially reduce treatment overhead vs process-per-call within treatment-only comparisons.
- We still need a clean, fully paired control vs roscli large-task replicate under the fixed shim version.

## Disentangle Items (experiment defects vs tool signal)

1. Historical workspace identity mismatch (fixed)
- Issue: lightweight harness roscli shims were hardcoded to `src/RoslynSkills.Cli`, but baseline commits may contain `src/RoslynAgent.Cli`.
- Impact: treatment lanes could silently fail into non-Roslyn behavior.
- Fix applied: dynamic project resolution in generated shims and local `scripts/roscli*` launchers.

2. Stale acceptance-check paths (fixed)
- Issue: lightweight manifest referenced `tests/RoslynSkills.*` paths while baseline commit uses `tests/RoslynAgent.*`.
- Impact: false-negative run outcomes despite correct edits.
- Fix applied: manifest acceptance paths updated to `RoslynAgent.*`.

3. Parallel .NET gate commands in agent trajectories (known confound)
- Issue: agents often run `dotnet build` and `dotnet test` concurrently.
- Impact: intermittent asset cache/lock warnings and occasional runtime instability noise.
- Mitigation: treat serialized reruns as truth, and keep sequencing guidance explicit in prompts/harness policy.

4. Open-ended prompt drift dominates large-task token variance (known confound)
- Issue: uncontrolled planning/exploration breadth causes very large token swings even under same condition.
- Impact: hard to attribute deltas solely to tooling lane.
- Mitigation: add bounded-run policies (step caps, required early acceptance checks, fixed sequence) for large-task A/B cells.

## Current best practical path (from this matrix)

1. Use `roscli` as default for Codex in small/medium anchored edits.
2. Keep MCP lanes as optimization/research tracks, especially for mixed Roslyn+LSP workflows.
3. For large open-ended tasks, prefer `roscli` with published cache mode and stricter trajectory constraints before making comparative claims.