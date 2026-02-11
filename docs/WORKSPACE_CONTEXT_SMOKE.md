# Workspace Context Smoke

Date: 2026-02-11  
Status: local verification notes

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
