# Diagnostics (Workspace-Backed First)

## Rule Of Thumb

- For real projects, prefer workspace-backed diagnostics:
  - `diag.get_workspace_snapshot` or `diag.get_file_diagnostics` with `--workspace-path ... --require-workspace true`
- Use `diag.get_solution_snapshot` only when you explicitly want ad-hoc compilation without a project file.

Why:

- Without `.csproj/.sln` context, ad-hoc compilation can produce misleading missing-reference/type errors for real codebases.

## Minimal Triage

Workspace snapshot (brief):

```powershell
roscli diag.get_workspace_snapshot src --brief true --workspace-path MySolution.slnx --require-workspace true
```

File diagnostics (workspace-bound):

```powershell
roscli diag.get_file_diagnostics src/MyFile.cs --workspace-path MyProject.csproj --require-workspace true
```

## Failure Patterns

- `workspace_context.mode=ad_hoc` on a project task:
  - Fix: rerun with `--workspace-path ... --require-workspace true` and choose the correct root.
- `CS0518` (core types missing):
  - Treat as invalid workspace binding; do not trust subsequent diagnostics until corrected.

