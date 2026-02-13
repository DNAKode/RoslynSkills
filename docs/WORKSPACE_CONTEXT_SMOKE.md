# Workspace Context Smoke

Date: 2026-02-12  
Status: local verification notes (updated with paired-run telemetry)

## Goal

Verify that high-traffic file-scoped commands expose workspace binding state and default to project context when available.

## Commands and Observations

### 1) Project-backed diagnostics resolves workspace

Command:

```text
roscli diag.get_file_diagnostics src/RoslynSkills.Core/DefaultRegistryFactory.cs
```

Observed summary:

- `Preview`: `diag.get_file_diagnostics ok: total=0, errors=0, warnings=0, workspace=workspace`
- `Data.workspace_context.mode`: `workspace`
- `Data.workspace_context.resolved_workspace_path`: `src/RoslynSkills.Core/RoslynSkills.Core.csproj`

### 2) Project-backed symbol search resolves workspace

Command:

```text
roscli nav.find_symbol src/RoslynSkills.Core/DefaultRegistryFactory.cs Create --brief true --max-results 20
```

Observed summary:

- `Preview`: `nav.find_symbol ok: matches=1, workspace=workspace`
- `Data.query.workspace_context.mode`: `workspace`
- semantic fields populated (`symbol_kind=Method`, `is_resolved=true`)

### 3) Loose-file diagnostics falls back with explicit reason

Command:

```text
roscli diag.get_file_diagnostics <temp-file>.cs
```

Observed summary:

- `Preview`: `diag.get_file_diagnostics ok: total=0, errors=0, warnings=0, workspace=ad_hoc`
- `Data.workspace_context.mode`: `ad_hoc`
- `Data.workspace_context.fallback_reason` explains missing `.csproj/.sln/.slnx` candidates.

## Expected Agent Behavior

For project-backed files:

1. read `workspace_context.mode`,
2. require `workspace`,
3. if `ad_hoc`, rerun with explicit `workspace_path` (`.csproj`, `.sln`, `.slnx`, or workspace directory).

## Paired-Run Trace Confirmation (v0.1.6-preview.7)

Artifacts:

- `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-project-matrix-v5/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-singlefile-matrix-v4/paired-run-summary.json`

Observed in codex `treatment-mcp` lane:

- project task shape:
  - `roslyn_workspace_mode_workspace_count=2`
  - `roslyn_workspace_mode_ad_hoc_count=0`
- single-file task shape:
  - `roslyn_workspace_mode_workspace_count=0`
  - `roslyn_workspace_mode_ad_hoc_count=2`

Interpretation:

- the same Roslyn MCP command path (`nav.find_symbol` + `diag.get_file_diagnostics`) binds to project context when `.csproj` is present and falls back to ad-hoc when it is absent.
- this confirms that workspace-mode differences are scenario-driven, not random tool instability.

## Current Release Confirmation (v0.1.6-preview.8)

Artifacts:

- `artifacts/real-agent-runs/20260212-v0.1.6-preview.8-project-matrix-v4/paired-run-summary.json`

Observed in codex `treatment-mcp` lane:

- `roslyn_workspace_mode_workspace_count=2`
- `roslyn_workspace_mode_ad_hoc_count=0`

Interpretation:

- on the current published release, MCP-backed symbol/diagnostic calls still bind to project workspace context for project-shaped tasks.

### 4) Explicit `.slnx` workspace binding works

Command:

```text
roscli nav.find_symbol src/RoslynSkills.Core/DefaultRegistryFactory.cs Create --brief true --workspace-path RoslynSkills.slnx --require-workspace true
```

Expected:

- `workspace_context.mode`: `workspace`
- `workspace_context.resolved_workspace_path`: `RoslynSkills.slnx`

### 5) OSS repo confirmation: workspace resolves to the correct `.csproj`

Evidence (Avalonia pilot):

- `artifacts/real-agent-runs/20260213-080511-oss-csharp-pilot/avalonia-cornerradius-tryparse/treatment-roslyn-optional/run-codex-treatment-roslyn-optional-avalonia-cornerradius-tryparse-brief-first-r01/transcript.jsonl`

Observed in `nav.find_symbol` output:

- `workspace_context.mode`: `workspace`
- `workspace_context.resolved_workspace_path`: `src/Avalonia.Base/Avalonia.Base.csproj`

This is the key signal that we are not accidentally running file-only compilation for project code.

## Current Release Confirmation (v0.1.6-preview.13)

Published: 2026-02-13

Notes:
- `.slnx` is supported as `--workspace-path` for workspace-bound operations.
- `diag.get_workspace_snapshot` is available for project-backed diagnostics over file sets.
- OSS pilot traces confirm `workspace_context.mode=workspace` with resolved `.csproj` binding on a large repo.
