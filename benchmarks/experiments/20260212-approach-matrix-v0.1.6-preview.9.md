# Approach Matrix (v0.1.6-preview.9)

Date: 2026-02-12  
Purpose: consolidate current-version evidence across approaches/scenarios and separate experimental confounds from Roslyn-tooling learnings.

## Sources

- project + profile matrix (Codex):
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-project-all-approaches-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-project-brief-first-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-project-surgical-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-project-schema-first-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-project-skill-minimal-v1/paired-run-summary.json`
- single-file + profile matrix (Codex):
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-standard-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-brief-first-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-surgical-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-schema-first-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-skill-minimal-v1/paired-run-summary.json`
- telemetry-fix validation rerun:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-schema-first-telemetryfix-v2/paired-run-summary.json`
- latest valid LSP comparator snapshot (Claude, prior day):
  - `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/paired-run-summary.json`

## A) Project Task Shape (Codex, Current Version)

| Profile | Approach | Duration (s) | Total tokens | Round trips | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) |
| --- | --- | ---: | ---: | ---: | --- | --- |
| standard | control | 21.649 | 34,150 | 2 | 0/0 | 0/0 |
| standard | treatment | 44.824 | 38,343 | 3 | 2/2 | 0/0 |
| standard | treatment-mcp | 35.298 | 102,421 | 6 | 3/3 | 2/0 |
| brief-first | control | 20.021 | 33,795 | 2 | 0/0 | 0/0 |
| brief-first | treatment | 32.316 | 27,005 | 2 | 1/1 | 0/0 |
| brief-first | treatment-mcp | 27.179 | 66,015 | 4 | 3/3 | 2/0 |
| surgical | control | 24.884 | 34,875 | 2 | 0/0 | 0/0 |
| surgical | treatment | 32.602 | 26,932 | 2 | 1/1 | 0/0 |
| surgical | treatment-mcp | 22.479 | 50,205 | 3 | 2/2 | 1/0 |
| schema-first | control | 21.663 | 34,299 | 3 | 0/0 | 0/0 |
| schema-first | treatment | 77.249 | 89,629 | 12 | 10/10 | 0/0 |
| schema-first | treatment-mcp | 33.375 | 81,515 | 7 | 6/6 | 2/0 |
| skill-minimal | control | 20.418 | 33,749 | 2 | 0/0 | 0/0 |
| skill-minimal | treatment | 70.301 | 75,420 | 7 | 6/6 | 0/0 |
| skill-minimal | treatment-mcp | 32.322 | 88,591 | 5 | 3/3 | 2/0 |

## B) Single-File Task Shape (Codex, Current Version)

| Profile | Approach | Duration (s) | Total tokens | Round trips | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) |
| --- | --- | ---: | ---: | ---: | --- | --- |
| standard | control | 20.070 | 34,020 | 2 | 0/0 | 0/0 |
| standard | treatment | 35.143 | 38,087 | 3 | 2/2 | 0/0 |
| standard | treatment-mcp | 45.611 | 118,862 | 7 | 3/3 | 0/2 |
| brief-first | control | 20.656 | 33,757 | 2 | 0/0 | 0/0 |
| brief-first | treatment | 26.303 | 27,198 | 2 | 1/1 | 0/0 |
| brief-first | treatment-mcp | 39.039 | 79,239 | 5 | 3/3 | 0/2 |
| surgical | control | 20.611 | 34,086 | 2 | 0/0 | 0/0 |
| surgical | treatment | 26.415 | 26,929 | 2 | 1/1 | 0/0 |
| surgical | treatment-mcp | 30.614 | 63,604 | 4 | 2/2 | 0/1 |
| schema-first | control | 22.934 | 34,213 | 2 | 0/0 | 0/0 |
| schema-first | treatment | 57.391 | 64,442 | 5 | 3/3 | 0/0 |
| schema-first | treatment-mcp | 40.923 | 110,509 | 9 | 7/7 | 0/2 |
| skill-minimal | control | 21.917 | 34,158 | 2 | 0/0 | 0/0 |
| skill-minimal | treatment | 79.518 | 98,584 | 9 | 6/6 | 0/0 |
| skill-minimal | treatment-mcp | 35.200 | 105,214 | 6 | 4/4 | 0/2 |

## C) Empirical Trace Improvement (Workspace Telemetry)

Focused replay on the same scenario/profile (`single-file`, `schema-first`) after parser fix:

| Bundle | Approach | Roslyn calls (ok/attempted) | Workspace modes (workspace/ad_hoc) | Key observation |
| --- | --- | --- | --- | --- |
| `20260212-v0.1.6-preview.9-singlefile-schema-first-v1` | treatment | 3/3 | 0/0 | transcript showed `workspace=ad_hoc`, but counters were zero |
| `20260212-v0.1.6-preview.9-singlefile-schema-first-telemetryfix-v2` | treatment | 10/10 | 0/2 | counters now reflect ad-hoc workspace events in CLI run output |

This improvement came from parser changes in `benchmarks/scripts/Run-PairedAgentRuns.ps1` (multi-shape text extraction + JSON fallback) plus regression assertions in `tests/RoslynSkills.Benchmark.Tests/PairedRunHarnessScriptTests.cs`.

## D) Every Approach Including LSP (Current + Last Valid LSP Snapshot)

Current-version (2026-02-12) codex lanes cover `control`, `treatment`, `treatment-mcp`.  
Latest valid `treatment-lsp` evidence is currently from 2026-02-11:

| Agent | Profile | Approach | Run passed | Duration (s) | Total tokens | LSP calls (ok/attempted) | Note |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| claude | brief-first | control | true | 31.772 | 510 | 0/0 | baseline |
| claude | brief-first | treatment | true | 38.524 | 649 | 0/0 | roscli lane |
| claude | brief-first | treatment-mcp | true | 38.225 | 957 | 0/0 | roslyn MCP lane |
| claude | brief-first | treatment-lsp | false | 180.066 | n/a | 0/1 | timeout/failure in last valid snapshot |

## Most Promising Path (Current Read)

1. Default practical lane: `treatment` with `brief-first` or `surgical`, especially on project tasks.
2. Best current tradeoff for project tasks:
   - `project + surgical + treatment` lowers tokens vs control (`26,932` vs `34,875`) with moderate time overhead.
3. Best current workspace-evidence lane:
   - `project + surgical + treatment-mcp` gives explicit project-mode evidence (`1/0`) and was faster than control in this replicate (`22.479s` vs `24.884s`), but token cost remains higher.
4. `schema-first` and `skill-minimal` are useful stress/debug lanes but not promising defaults due large trajectory overhead.

## Things To Disentangle

1. Fresh LSP comparator state is blocked by Claude auth/session availability, so current LSP row is not same-day with `v0.1.6-preview.9`.
2. Single-file task shape intentionally lacks `.csproj`; `ad_hoc` workspace mode is expected there and should not be misclassified as Roslyn failure.
3. Workspace counters are now improved, but parser changes should be validated on additional mixed-output trajectories before treating time-series comparisons as fully stable.
4. Cross-agent token comparisons remain secondary; primary interpretation should stay within-agent and within task shape/profile.


## E) Follow-on Codex MCP Interop Matrix (Same-Day)

A deeper same-day codex/spark MCP interop sweep (including `csharp-ls` via `cclsp`, cross-model low/high, and codex medium/xhigh extensions) is captured in:

- `benchmarks/experiments/20260212-codex-mcp-interop-matrix-v0.1.6-preview.9.md`

Use that matrix as the latest detailed source for:

- control vs roslyn-mcp vs lsp-mcp vs combined lane metrics,
- model-effort interactions (`gpt-5.3-codex` and `gpt-5.3-codex-spark`),
- disentangled setup/model-availability confounds.

## F) Focused Roscli vs MCP Follow-on (Workspaceguard v3/v4)

For post-fix workspace telemetry and MCP prompt-sequencing optimization details, see:

- `benchmarks/experiments/20260212-roscli-vs-mcp-workspace-context-v0.1.6-preview.9.md`

Highlights from that follow-on:

- roscli helper lane now reports project workspace mode directly (`workspace/ad_hoc = 1/0`) in focused project-shape runs.
- MCP `brief-first` token overhead dropped from `80,374` to `64,901` after prompt tuning that removes mandatory pre-rename nav lookup when line/column anchors are already known.
- Even after this prompt improvement, best MCP snapshot remains materially above roscli on this microtask family.
