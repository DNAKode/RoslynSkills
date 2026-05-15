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

### Not Yet True MSBuild Solution-Backed

These accept `workspace_path` as an analysis root but currently use source scanning/ad-hoc compilation rather than full MSBuild solution membership:

- `analyze.unused_private_symbols`
- `analyze.dependency_violations`
- `analyze.override_coverage`
- `analyze.async_risk_scan`
- `ctx.search_text`
- `diag.get_solution_snapshot`

Implication:

- Passing `.sln/.slnx` may scope the filesystem root, but it does not yet mean "only projects included in this solution with project references and full MSBuild semantics."

Backlog:

- Add a shared solution-backed analysis workspace loader for the `analyze.*` commands that currently use `StaticAnalysisWorkspace`.
- Rename or document ad-hoc snapshot commands so agents do not confuse `diag.get_solution_snapshot` with MSBuild solution loading.
- Add output fields that distinguish `analysis_mode=msbuild_solution|project|directory_scan|ad_hoc`.

### Sessions

Current `session.*` commands are file-scoped and explicitly not solution-backed.

Backlog:

- Add project/solution-backed speculative sessions as planned:
  - `session.open_workspace`
  - `session.apply_project_edits`
  - `session.commit_workspace`

### Hot Workspace Host

Planned host commands should require or strongly prefer `.sln/.slnx`:

```text
roscli workspace.preload MySolution.slnx
roscli workspace.status <handle>
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

## Open Follow-Ups

1. Implement solution-backed `StaticAnalysisWorkspace` replacement or companion.
2. Add `analysis_mode` to root-scanning analysis commands.
3. Add hot-host lifecycle commands with `.sln/.slnx` as the primary examples.
4. Add benchmark gates that fail if a hot-workspace run resolves to a loose project when solution scope was requested.
