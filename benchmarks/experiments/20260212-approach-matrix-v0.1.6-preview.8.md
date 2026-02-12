# Approach Matrix (v0.1.6-preview.8)

Date: 2026-02-12  
Purpose: keep a current-version snapshot while separating tool behavior from harness/telemetry issues.

## Sources

- current project matrix (codex):
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.8-project-matrix-v4/paired-run-summary.json`
- latest single-file matrix (codex):
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-singlefile-matrix-v4/paired-run-summary.json`
- latest LSP comparator snapshot (Claude):
  - `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/paired-run-summary.json`

## Scenario Matrix

### A) Project-backed task (Codex, current release)

| Approach | Run passed | Duration (s) | Total tokens | Round trips | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) |
| --- | --- | ---: | ---: | ---: | --- | --- |
| control | true | 20.531 | 34,112 | 2 | 0/0 | 0/0 |
| treatment (roscli helper) | true | 43.119 | 38,280 | 3 | 2/2 | 0/0 |
| treatment-mcp | true | 32.656 | 102,195 | 6 | 3/3 | 2/0 |

Notes:

- this treatment trajectory used helper calls without explicit workspace-context-returning diag/nav responses, so workspace counters remain `0/0`.
- `treatment-mcp` confirms project-backed workspace binding (`2/0`) on current release.

### B) Single-file task (Codex, latest available)

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

- default path for practical reliability: `treatment (roscli helper)` for routine tasks.
- explicit context-validation path: `treatment-mcp` when workspace-mode evidence is required in-trace.
- keep `TaskShape=project` as default for workspace-sensitive claims.

## Things To Disentangle

1. Helper-lane workspace telemetry interpretation
- helper runs can validly show `workspace/ad_hoc=0/0` when no response payload with `workspace_context` is emitted.
- do not interpret `0/0` as ad-hoc fallback by itself.

2. Cross-agent availability confounds
- fresh Claude lanes were blocked by auth expiry on 2026-02-12; this is infra state, not Roslyn/LSP capability evidence.

3. LSP stability under replicated project tasks
- latest valid LSP snapshot still shows timeout on first semantic call.
- needs fresh auth + project-backed replications before updating comparative claims.

4. Token comparability across providers
- compare within-agent first; use cross-agent token numbers only as secondary context.

## Immediate Follow-up

1. Add explicit helper-lane workspace probe option in prompts for runs where workspace telemetry parity is required.
2. Re-run full comparator matrix (`control`, `treatment`, `treatment-mcp`, `treatment-lsp`) after Claude auth refresh.
3. Add first `dotnet-inspect` comparator lane on package/API-centric tasks.
