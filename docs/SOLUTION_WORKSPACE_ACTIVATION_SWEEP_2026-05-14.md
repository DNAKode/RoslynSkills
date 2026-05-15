# Solution Workspace Activation Sweep

Date: 2026-05-14
Status: implementation follow-up notes

## Goal

Make full-solution workspace loading the natural path for hot RoslynSkills hosts and make solution-vs-project binding observable in every agent-facing workflow where it matters.

## Implemented This Pass

- Workspace candidate inference now prefers discovered `.sln`/`.slnx` files before loose `.csproj/.vbproj` files when auto-resolving from a source file or explicit directory.
- Explicit `.csproj/.vbproj` still works and remains the intentional project-scoped path.
- Workspace payloads now expose:
  - `workspace_kind`
  - `project_count`
  - `document_count`
- CLI quickstart, `llmstxt`, command usage hints, MCP schema descriptions, README, NuGet README, skills, and pit-of-success docs now steer repo-wide/hot-host workflows toward `.sln/.slnx`.
- Loader tests now cover:
  - explicit directory containing nested project plus root `.slnx`,
  - auto-inference from a file under a git-rooted solution,
  - both must resolve to the `.slnx` and report multi-project solution state.

## Surface Sweep

### Solution-Appropriate And Supported Now

These use `WorkspaceSemanticLoader` and can load `.sln/.slnx` with MSBuild/Roslyn workspace semantics:

- `nav.find_symbol`
- `nav.find_symbol_batch`
- `nav.find_references`
- `nav.find_implementations`
- `nav.find_overrides`
- `nav.find_invocations`
- `nav.call_hierarchy`
- `nav.call_path`
- `ctx.symbol_envelope`
- `ctx.member_source`
- `ctx.dependency_slice`
- `ctx.call_chain_slice`
- `diag.get_file_diagnostics`
- `diag.get_after_edit`
- `diag.get_workspace_snapshot`
- `repair.propose_from_diagnostics`
- `edit.rename_symbol`
- `edit.replace_member_body`
- `analyze.control_flow_graph`
- `analyze.dataflow_slice`
- `analyze.impact_slice`

Recommended activation:

```text
roscli nav.find_symbol src/MyProject/File.cs SymbolName --brief true --workspace-path MySolution.slnx --require-workspace true
```

Acceptance check:

- `workspace_context.mode = workspace`
- `workspace_context.resolved_workspace_path` ends with `.sln` or `.slnx`
- `workspace_context.workspace_kind = solution|slnx`
- `workspace_context.project_count` is consistent with expected solution size

### Project-Scoped By Design

Explicit `.csproj/.vbproj` remains appropriate when the user wants narrow project semantics:

```text
roscli diag.get_file_diagnostics src/MyProject/File.cs --workspace-path src/MyProject/MyProject.csproj --require-workspace true
```

This should be treated as a project-scoped host, not a whole-solution host.

### Analysis Commands Now MSBuild Solution-Aware

These commands use `StaticAnalysisWorkspace` and can load `.sln/.slnx/.csproj/.vbproj` via MSBuild workspace semantics. Directory/file roots still use directory-scan/ad-hoc compilation.

- `analyze.unused_private_symbols`
- `analyze.dependency_violations`
- `analyze.override_coverage`
- `analyze.async_risk_scan`

Acceptance check:

- `analysis_scope.analysis_mode = msbuild_solution|msbuild_project|directory_scan`
- `analysis_scope.resolved_workspace_path` identifies the actual loaded solution/project/root
- `analysis_scope.workspace_kind` distinguishes `solution`, `slnx`, `project`, `directory`, or `file`
- `analysis_scope.project_count` and `analysis_scope.document_count` are non-zero for MSBuild solution/project modes

### Still Not True MSBuild Solution-Backed

These accept `workspace_path` as an analysis root but currently use source scanning/ad-hoc compilation rather than full MSBuild solution membership:

- `ctx.search_text`
- `diag.get_solution_snapshot`

Implication:

- Passing `.sln/.slnx` may scope the filesystem root, but it does not yet mean "only projects included in this solution with project references and full MSBuild semantics."

Backlog:

- Rename or document ad-hoc snapshot commands so agents do not confuse `diag.get_solution_snapshot` with MSBuild solution loading.

Current telemetry:

- `ctx.search_text` reports `analysis_scope.analysis_mode = directory_scan`.
- `diag.get_solution_snapshot` reports `analysis_scope.analysis_mode = ad_hoc_compilation`.

### Sessions

Current `session.*` commands are file-scoped and explicitly not solution-backed.

Backlog:

- Add project/solution-backed speculative sessions as planned:
  - `session.open_workspace`
  - `session.apply_project_edits`
  - `session.commit_workspace`

### Hot Workspace Host

Host lifecycle commands now require or strongly prefer `.sln/.slnx`:

```text
roscli workspace.preload MySolution.slnx --require-solution true
roscli workspace.status <handle>
roscli workspace.close <handle>
```

Project files should remain allowed only with explicit project-scoped telemetry.

Required host telemetry:

- `workspace_kind`
- `resolved_workspace_path`
- `projects_loaded`
- `documents_loaded`
- `workspace_fingerprint`
- `dirty`
- `invalidated_paths`

Current implementation:

- `workspace.preload` loads through the solution-aware workspace path and returns `workspace_handle`, `workspace_kind`, `analysis_mode`, `projects_loaded`, `documents_loaded`, `workspace_fingerprint`, `dirty`, and `invalidated_paths`.
- `workspace.preload --require-solution true` fails with `solution_required` if the resolved host is a project/directory/file instead of `.sln/.slnx`.
- `workspace.status` reports the retained process-hot host state.
- `workspace.close` discards the retained handle.

## Open Follow-Ups

1. Teach high-traffic semantic commands to accept `workspace_handle` and reuse process-hot state rather than only reporting lifecycle state.
2. Add benchmark gates that fail if a hot-workspace run resolves to a loose project when solution scope was requested. Prefer `workspace.preload --require-solution true` as the enforcement hook.
3. Rename or document ad-hoc snapshot commands so agents do not confuse `diag.get_solution_snapshot` with MSBuild solution loading.
