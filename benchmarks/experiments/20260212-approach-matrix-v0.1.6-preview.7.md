# Approach Matrix (v0.1.6-preview.7)

Date: 2026-02-12  
Purpose: compare currently available approaches across scenarios while separating experiment/harness failures from tool-behavior signals.

## Sources

- `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-project-matrix-v5/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-singlefile-matrix-v4/paired-run-summary.json`
- `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/paired-run-summary.json`

## Scenario Matrix

### A) Project-backed task (Codex, current version)

| Approach | Run passed | Duration (s) | Total tokens | Round trips | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) |
| --- | --- | ---: | ---: | ---: | --- | --- |
| control | true | 22.626 | 34,150 | 2 | 0/0 | 0/0 |
| treatment (roscli helper) | true | 35.740 | 27,246 | 2 | 1/1 | 0/0 |
| treatment-mcp | true | 27.003 | 66,108 | 4 | 3/3 | 2/0 |

### B) Single-file task (Codex, current version)

| Approach | Run passed | Duration (s) | Total tokens | Round trips | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) |
| --- | --- | ---: | ---: | ---: | --- | --- |
| control | true | 19.980 | 34,037 | 2 | 0/0 | 0/0 |
| treatment (roscli helper) | true | 24.718 | 26,991 | 2 | 1/1 | 0/0 |
| treatment-mcp | true | 35.227 | 79,416 | 5 | 3/3 | 0/2 |

### C) Single-file comparator snapshot (Claude, prior valid LSP bundle)

| Approach | Run passed | Duration (s) | Total tokens | Round trips | Roslyn calls (ok/attempted) | LSP calls (ok/attempted) | LSP tools available |
| --- | --- | ---: | ---: | ---: | --- | --- | --- |
| control | true | 31.772 | 510 | 3 | 0/0 | 0/0 | n/a |
| treatment (roscli) | true | 38.524 | 649 | 4 | 2/2 | 0/0 | n/a |
| treatment-mcp | true | 38.225 | 957 | 5 | 3/3 | 0/0 | n/a |
| treatment-lsp | false | 180.066 | n/a | 2 | 0/0 | 0/1 | true |

## Most Promising Path (Current)

- Default path for practical reliability: `treatment (roscli helper)` in project-backed tasks.
- Why now:
  - passed constraints in current project and single-file runs,
  - lower model-token totals than control in both current codex scenarios,
  - lower operational overhead than MCP on this task family.
- MCP remains valuable when explicit workspace-context evidence is required:
  - project scenario recorded `workspace/ad_hoc = 2/0`,
  - single-file scenario recorded `workspace/ad_hoc = 0/2`.

## Things To Disentangle

1. Claude auth volatility (execution environment)
- Current 2026-02-12 Claude lanes were not runnable due OAuth expiry (`401`), so no fresh Claude comparator data was produced.
- This is an environment gate, not a Roslyn/LSP capability result.

2. LSP reliability vs availability (experimental validity)
- In latest valid LSP comparator bundle (`20260211-lsp-roslyn-v4`), LSP tools were available but first semantic call timed out (`0/1`, 180s).
- Need project-backed replicated LSP runs with valid auth before comparative claims.

3. Helper-path workspace telemetry visibility (instrumentation gap)
- `treatment` helper lane uses `roslyn-rename-and-verify.ps1`; it does not currently emit `workspace_context` counts directly, so helper rows show `0/0`.
- MCP lane provides clear workspace telemetry; helper lane should gain optional explicit workspace-mode probes for parity.

4. Token comparability across providers (measurement caveat)
- Claude rows include large cache-inclusive token components with provider-specific semantics.
- Use per-agent comparisons first, cross-agent token comparisons second.

## Immediate Follow-up

1. Re-run full matrix with Claude after auth refresh (`control`, `treatment`, `treatment-mcp`, `treatment-lsp`) on `TaskShape=project`.
2. Add helper-lane workspace-mode probe option so non-MCP Roslyn runs also report explicit `workspace/ad_hoc` counts.
3. Add first `dotnet-inspect` comparator lanes (`inspect-only`, `roslyn-only`, `combined`) on package/API-sensitive scenarios.
