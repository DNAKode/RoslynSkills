# Diagnostics (Workspace-Backed First)

## Rule Of Thumb

- For real projects, prefer workspace-backed diagnostics:
  - `diag.get_workspace_snapshot --directory-path <dir> --brief true --include-diagnostics true --require-workspace true` (`~104–240 ms` warm on bound solutions, compiles all projects rooted under `<dir>`)
  - or `diag.get_file_diagnostics <file> --require-workspace true` (`~74 ms` warm)
- Avoid `diag.get_solution_snapshot` unless you explicitly want loose/ad-hoc syntax+file checks without `.sln`/`.slnx` context.
- Do not rely on external LSP diagnostics (`csharp-ls` can push `1,000+` false errors on multi-TFM `.NET 11` solutions; `Roslyn LSP` `workspace/diagnostic` returns `0` items silently on non-open projects).

Why:

- Without `.csproj`/`.sln`/`.slnx` context, ad-hoc compilation produces misleading missing-reference/type errors (`CS0518`/`CS0246`) for real codebases.

## Minimal Triage

Scoped directory snapshot (brief, multi-project):

```powershell
roscli diag.get_workspace_snapshot --directory-path src/MyProject --brief true --include-diagnostics true --require-workspace true
```

File diagnostics (workspace-bound):

```powershell
roscli diag.get_file_diagnostics src/MyFile.cs --workspace-path MySolution.slnx --require-workspace true
```

## Failure Patterns

- `workspace_context.mode=ad_hoc` on a project task:
  - Fix: rerun with `--workspace-path ... --require-workspace true` and choose the correct root.
- `CS0518` (core types missing):
  - Treat as invalid workspace binding; do not trust subsequent diagnostics until corrected.
