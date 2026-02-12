# Codex MCP Interop Matrix (v0.1.6-preview.9)

Date: 2026-02-12  
Purpose: isolate model/tooling behavior from experiment plumbing issues for Codex MCP lanes (`roslyn`, `csharp-ls` via MCP bridge) across model and reasoning settings.

## Version Snapshot

- Local repo state: `v0.1.6-preview.9-1-gaded211` (`git describe --tags --always`)
- Latest published tag in prior release flow: `v0.1.6-preview.8`

## Sources

- Baseline attempt before LSP MCP bridge wiring:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v1/codex-mcp-interop-summary.json`
- Codex full matrix (`low/high`) with cclsp bridge:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v6b-cclsp-duration/codex-mcp-interop-summary.json`
- Codex extension (`medium/xhigh`) for LSP and combined lanes:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v9-codex-lsp-medium-xhigh/codex-mcp-interop-summary.json`
- Codex prior Roslyn-only effort sweep (`low/medium/high/xhigh`, tokens only):
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v2-codex-effort-sweep/codex-mcp-interop-summary.json`
- Spark full matrix (`low/high`) with cclsp bridge:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v8-spark-cclsp-duration/codex-mcp-interop-summary.json`
- Spark availability confirmation smoke:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v7-spark-cclsp-duration-smoke/codex-mcp-interop-summary.json`

## A) Codex (`gpt-5.3-codex`) Low/High Matrix

| Effort | Scenario | Passed | Duration (s) | Tokens | Roslyn calls | LSP calls |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| low | control | true | 43.424 | 64,343 | 0 | 0 |
| low | roslyn-mcp | true | 49.120 | 123,739 | 8 | 0 |
| low | lsp-mcp | true | 49.926 | 63,765 | 0 | 2 |
| low | roslyn-plus-lsp-mcp | true | 43.801 | 89,019 | 5 | 2 |
| high | control | true | 66.851 | 95,764 | 0 | 0 |
| high | roslyn-mcp | true | 87.132 | 169,875 | 6 | 0 |
| high | lsp-mcp | true | 89.381 | 140,277 | 0 | 3 |
| high | roslyn-plus-lsp-mcp | true | 93.026 | 195,178 | 5 | 4 |

## B) Codex Medium/XHigh Extension

| Effort | Scenario | Passed | Duration (s) | Tokens | Roslyn calls | LSP calls | Source bundle |
| --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| medium | control | true | 35.556 | 52,853 | 0 | 0 | v9 |
| medium | roslyn-mcp | true | n/a | 148,572 | 7 | 0 | v2 |
| medium | lsp-mcp | true | 61.785 | 86,153 | 0 | 2 | v9 |
| medium | roslyn-plus-lsp-mcp | true | 54.059 | 106,354 | 4 | 1 | v9 |
| xhigh | control | true | 61.175 | 75,994 | 0 | 0 | v9 |
| xhigh | roslyn-mcp | true | n/a | 155,845 | 7 | 0 | v2 |
| xhigh | lsp-mcp | true | 100.886 | 117,182 | 0 | 2 | v9 |
| xhigh | roslyn-plus-lsp-mcp | true | 82.224 | 116,238 | 5 | 3 | v9 |

Note: `v2` predates `duration_seconds` capture, so medium/xhigh `roslyn-mcp` duration is unavailable.

## C) Spark (`gpt-5.3-codex-spark`) Low/High Matrix

| Effort | Scenario | Passed | Duration (s) | Tokens | Roslyn calls | LSP calls |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| low | control | true | 29.886 | 51,309 | 0 | 0 |
| low | roslyn-mcp | true | 60.320 | 149,819 | 7 | 0 |
| low | lsp-mcp | true | 41.073 | 74,626 | 0 | 2 |
| low | roslyn-plus-lsp-mcp | true | 58.011 | 92,710 | 5 | 1 |
| high | control | true | 70.439 | 87,229 | 0 | 0 |
| high | roslyn-mcp | true | 107.873 | 224,090 | 9 | 0 |
| high | lsp-mcp | true | 67.187 | 88,308 | 0 | 3 |
| high | roslyn-plus-lsp-mcp | true | 72.149 | 99,249 | 4 | 2 |

## D) Disentangled Issues

1. Missing LSP MCP bridge in initial runs was an experiment setup defect, not model/tool quality.
   - `v1` skipped all `lsp-mcp`/combined rows with `No LSP MCP command configured/found`.
   - Fixed by adding `cclsp` MCP bridge support and run-local `cclsp.json` bootstrap in `Run-CodexMcpInteropExperiments.ps1`.
2. Model availability changed during the day.
   - Early runs showed Spark unsupported in this account context.
   - Later reruns (`v7`, `v8`) completed Spark lanes successfully.
3. One transient harness execution fault occurred (`v6`) with `Invoke-CodexRun` process start.
   - Immediate rerun (`v6b`) completed all rows successfully; treat as transient until reproduced.

## Current Promising Path (This Task Shape)

1. For low-effort Codex tasks, `lsp-mcp` is close to control in duration and essentially equal in tokens (`63,765` vs `64,343`) while preserving semantic tooling.
2. `roslyn-mcp` remains the highest-overhead lane for this microtask under both models.
3. Combined lane can be fast (`43.801s` at Codex low), but token cost is still materially above control/LSP-only.
4. Spark is now viable in this environment; on Spark high, `lsp-mcp` outperformed control on duration (`67.187s` vs `70.439s`) with near-token parity.

## Next Disentangle Steps

1. Re-run Codex `roslyn-mcp` medium/xhigh with duration capture to complete a single-bundle effort curve.
2. Repeat `v6b` and `v8` as at least one replicate each to check trajectory variance before promoting defaults.
3. Add a workspace-stress scenario (missing restore / unresolved references) to measure whether Roslyn or LSP MCP lanes recover workspace context more reliably than control.
