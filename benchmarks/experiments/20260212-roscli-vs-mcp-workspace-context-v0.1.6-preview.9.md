# Roscli vs MCP Workspace Context Comparison (v0.1.6-preview.9)

Date: 2026-02-12
Local repo describe: `v0.1.6-preview.9-2-gcce42e6`

## Scope

Compare project-shape Codex runs for:

- `control` (text-first)
- `treatment` (roscli helper lane)
- `treatment-mcp` (Roslyn MCP lane)

under workspace-fail-closed guidance (`workspace_path` + `require_workspace=true` where applicable).

## Sources

- `artifacts/real-agent-runs/20260212-roscli-vs-mcp-workspaceguard-v3-surgical/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-roscli-vs-mcp-workspaceguard-v3-brief-first/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-roscli-vs-mcp-workspaceguard-v4-brief-first/paired-run-summary.json`

## Matrix

| Bundle | Profile | Approach | Duration (s) | Tokens | Round trips | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) |
| --- | --- | --- | ---: | ---: | ---: | --- | --- |
| `workspaceguard-v3-surgical` | surgical | control | 26.005 | 34,415 | 2 | 0/0 | 0/0 |
| `workspaceguard-v3-surgical` | surgical | treatment (roscli) | 36.213 | 29,315 | 2 | 1/1 | 1/0 |
| `workspaceguard-v3-surgical` | surgical | treatment-mcp | 41.932 | 64,714 | 4 | 2/2 | 1/0 |
| `workspaceguard-v3-brief-first` | brief-first | control | 24.201 | 33,390 | 2 | 0/0 | 0/0 |
| `workspaceguard-v3-brief-first` | brief-first | treatment (roscli) | 39.753 | 29,475 | 2 | 1/1 | 1/0 |
| `workspaceguard-v3-brief-first` | brief-first | treatment-mcp | 40.756 | 80,374 | 5 | 3/3 | 2/0 |
| `workspaceguard-v4-brief-first` | brief-first (MCP prompt tuned) | control | 22.629 | 33,269 | 2 | 0/0 | 0/0 |
| `workspaceguard-v4-brief-first` | brief-first (MCP prompt tuned) | treatment (roscli) | 37.319 | 29,004 | 2 | 1/1 | 1/0 |
| `workspaceguard-v4-brief-first` | brief-first (MCP prompt tuned) | treatment-mcp | 41.227 | 64,901 | 4 | 2/2 | 1/0 |

## Best MCP vs Best Roscli (Current Snapshot)

Best MCP by tokens in these post-fix runs:

- `treatment-mcp`, `workspaceguard-v3-surgical`: `64,714` tokens, `41.932s`, `4` round trips.

Best roscli lane in same run family:

- `treatment`, `workspaceguard-v4-brief-first`: `29,004` tokens, `37.319s`, `2` round trips.

Current delta (best MCP minus best roscli):

- `+35,710` tokens (`~2.23x`)
- `+4.613s`
- `+2` round trips

## What Logs Say (Why Prompting Still Matters)

1. MCP prompt shape directly changes overhead.
   - `brief-first` MCP before tune (`v3`) used `3` Roslyn MCP calls (`nav + rename + diag`) and `80,374` tokens.
   - After tune (`v4`) it used `2` Roslyn MCP calls (`rename + diag`) and `64,901` tokens.
   - Improvement from prompt-only change: `-15,473` tokens (`-19.3%`) and `-1` round trip.

2. Roscli helper lane now reports workspace context correctly.
   - Post-fix treatment runs show `workspace modes 1/0` instead of prior false-zero counts.

3. MCP remains heavier even after prompt optimization.
   - Transcript `turn.completed.usage.input_tokens` remains much higher in MCP lanes (roughly `64k-79k`) than roscli helper lanes (`~28k-29k`) on the same task shape.
   - This indicates protocol/surface overhead beyond simple command count.

## Disentangle Items

1. MCP payload overhead source split
   - Separate baseline tool-registration/context cost from per-call response payload cost.
2. Roscli vs MCP parity lane
   - Add a roscli lane that avoids helper script wrappers and calls direct `edit.rename_symbol + diag.get_file_diagnostics` for tighter apples-to-apples with MCP `2-call` flow.
3. Multi-task validation
   - Repeat this comparison on larger multi-file/high-ambiguity tasks before default-lane policy changes.