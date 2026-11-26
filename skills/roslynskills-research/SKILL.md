---
name: roslynskills-research
description: Roslyn-first C#/.NET coding workflow using RoslynSkills (roscli). Use when user asks for symbol navigation, semantic context, structured refactors (rename/transactions), workspace-backed diagnostics triage, or evidence capture for benchmark runs. Avoid for non-C# repos or tasks that do not need semantic certainty.
license: MIT
compatibility: Claude.ai / Claude Code. Requires local shell access (Bash or PowerShell) plus RoslynSkills CLI (roscli) and/or the RoslynSkills MCP server.
metadata:
  author: DNA Kode
  mcp-server: roslynskills
allowed-tools: "Bash Read Write Edit Grep Glob"
---

# RoslynSkills Research

Goal: combine text speed with Roslyn compiler truth (**text first, semantic second, scoped diag third**) via `roscli-fast.exe` + `WorkspaceHost`.

## Channel & Tool Decision Rules (`0.1.6-preview.32`)

- **Built-in `rg` + `read_file`:** Use for string literals, docs, error codes, non-C# files (`.slnx`, `.csproj`, `.json`), and reading a known 50–150 line slice (`~66–116 ms`, `0` tax). Do **not** rely on `rg` for overloaded/common symbols (`Execute` `47K` tokens, `Kind` `23K` tokens) or interface implementations/callers.
- **`roscli-fast.exe` + `WorkspaceHost` (Preferred semantic lane):** Bind once (`roscli workspace.use MySolution.slnx --require-solution true`), set `ROSCLI_DAEMON=required`, and call `%LOCALAPPDATA%\RoslynSkills\bin\roscli-fast.exe` (`~50–75 ms` warm vs `~365–465 ms` `.NET` CLI spawn and `~70 s` `--no-daemon`). Redirected stdout emits compact JSON automatically (opt in to `--pretty-json` for indented output).
- **`RoslynSkills.McpServer --mode slim`:** Exposes `10` focused complete-schema tools at `~4.9 KB` (`~1.2K` tokens/turn, >90% smaller than `--mode full`'s `53.85 KB` / `59` tools) over a direct persistent `WorkspaceHost` pipe with automatic workspace binding on first semantic call.
- **External C# LSP (`csharp-ls`, `Roslyn LSP`):** Do not treat as equivalent to `roscli-fast`: `csharp-ls` fails `.NET 11` `ProjectReference`s and emits false errors; `Roslyn LSP` answers incomplete `1` match during async load, `~515 ms` on refs, `0` interface callers (`854z`), and `0` items on `workspace/diagnostic`.

## What Roscli Is / Is Not

- `roscli` is for **in-repo** semantic navigation, edits, diagnostics, and repair on C#/.NET source.
- `roscli` is **not** a package registry/API catalog/version-diff tool.
- Use dotnet helper tools (for example `dotnet-inspect`) for external package/framework API intelligence, then return to `roscli` for local code changes.

## Session Start

Run command inventory first:

```powershell
roscli list-commands --ids-only
roscli list-commands --stable-only --ids-only
roscli quickstart
```

If `roscli` is not on PATH, in this repository use:

```powershell
scripts\roscli.cmd list-commands --ids-only
scripts\roscli.cmd list-commands --stable-only --ids-only
scripts\roscli.cmd quickstart
```

Cross-platform wrapper:

```bash
bash scripts/roscli list-commands --ids-only
bash scripts/roscli list-commands --stable-only --ids-only
bash scripts/roscli quickstart
```

Pit-of-success contract:

- Bind hot workspace once: `roscli workspace.use <solution.sln|.slnx> --require-solution true` (set `ROSCLI_DAEMON=required`, prefer `roscli-fast` for warm `~50–75 ms` calls; stop only your own daemon with `roscli daemon.stop` at exit).
- Discover: `list-commands --ids-only` (68 commands: `54` stable / `11` advanced / `3` experimental)
- Disambiguate args: `describe-command <command-id>`
- Target directly via `--symbol Type.Member` on all 6 `nav.*` commands (`go_to_definition`, `find_references`, `find_implementations`, `find_overrides`, `find_invocations`, `call_hierarchy`); `nav.call_hierarchy` defaults to `--max-depth 1` (captures interface callers via `edges[].implemented_member`)
- Filter `ctx.file_outline` with `--member-name-contains <term> --max-members 20` on large files (hot-routed via `roscli-fast` using the cached `SyntaxTree`)
- Start stable-first: use `list-commands --stable-only --ids-only`; only step into `advanced`/`experimental` commands when needed
- Verify workspace binding: use `--workspace-path ... --require-workspace true` on project code
- Prefer `.sln`/`.slnx` for repo-wide or hot-workspace context; use `.csproj` only when intentionally project-scoped
- Read payloads directly from top-level `Data` in both daemon and in-process runs (`Transport.mode` reports `daemon` vs `in_process`)
- Verify before finalize: scoped `diag.get_workspace_snapshot --directory-path <dir> --brief true --include-diagnostics true --require-workspace true` (`~104–240 ms`), then `dotnet build` / `dotnet test`

Cached invocation guidance (throughput vs freshness):

- For many Roslyn calls in one session, prefer published mode wrappers (`ROSCLI_USE_PUBLISHED=1`) to reduce repeated CLI startup overhead.
- After changing roscli source, refresh once (`ROSCLI_REFRESH_PUBLISHED=1`) before the next run.
- If debugging roscli behavior itself, prefer non-cached execution path for deterministic tool-dev feedback.

Mandatory policy: if you use non-Roslyn tooling to read/edit `.cs`, say so in your final report: RoslynSkills version (`roscli --version`), reason, missing command hypothesis, and expected impact.

## Canonical Workflow (Find -> Edit -> Verify)

1) Find declaration / references / interface callers directly via `--symbol` (with `WorkspaceHost` bound):

```powershell
roscli workspace.use MySolution.slnx --require-solution true
roscli-fast nav.find_references --symbol MyNamespace.DeferredCatalogCommit.Kind --max-results 100
roscli-fast nav.find_invocations --symbol MyNamespace.SchemaWriter.CreateTable --brief true --max-results 100
roscli-fast nav.call_hierarchy --symbol MyNamespace.ComSqlDatabase.OpenSchemaWriter --direction incoming --max-depth 1 --brief true
```

Or anchor from a file when symbol path is unknown:

```powershell
roscli-fast nav.find_symbol src/MyFile.cs MySymbol --brief true --max-results 50 --workspace-path MySolution.slnx --require-workspace true
```

2) Apply a structured edit:

```powershell
roscli-fast edit.rename_symbol src/MyFile.cs 42 17 NewName --apply true --max-diagnostics 50 --workspace-path MySolution.slnx --require-workspace true
```

3) Verify folder/project compilation fast before running full test suites:

```powershell
roscli-fast diag.get_workspace_snapshot --directory-path src/MyProject --brief true --include-diagnostics true --require-workspace true
```

Investigative tracing (when target is unclear across many files):

```powershell
roscli-fast ctx.search_text "RemoteUserAction" src --mode literal --max-results 120 --brief true
roscli-fast nav.find_invocations --symbol MyNamespace.MyType.Method --brief true --max-results 100
roscli-fast nav.call_hierarchy --symbol MyNamespace.MyType.Method --direction both --max-depth 1 --brief true
roscli-fast analyze.control_flow_graph src/MyFile.cs 42 17 --brief true --max-blocks 120 --max-edges 260 --workspace-path MySolution.slnx --require-workspace true
roscli-fast analyze.dataflow_slice src/MyFile.cs 42 17 --brief true --max-symbols 120 --workspace-path MySolution.slnx --require-workspace true
roscli-fast analyze.unused_private_symbols src --brief true --max-symbols 120
roscli-fast analyze.dependency_violations src MyApp.Web MyApp.Application MyApp.Domain --direction toward_end --brief true
roscli-fast analyze.impact_slice src/MyFile.cs 42 17 --brief true --include-callers true --include-callees true
roscli-fast analyze.override_coverage src --coverage-threshold 0.6 --brief true
roscli-fast analyze.async_risk_scan src --brief true --max-findings 120 --severity-filter warning --severity-filter info
```

Bundle multiple read-only probes in one call when useful (`query.batch` supports `ctx.*`, `nav.*` including `nav.find_implementations` and `nav.find_overrides`, `analyze.*`, `diag.get_file_diagnostics`, and `diag.get_workspace_snapshot`):

```powershell
roscli run query.batch --input "{`"queries`":[{`"command_id`":`"ctx.search_text`",`"input`":{`"patterns`":[`"RemoteUserAction`",`"ReplicationUpdate`"],`"roots`":[`"src`"],`"mode`":`"literal`"}},{`"command_id`":`"nav.find_invocations`",`"input`":{`"file_path`":`"src/MyFile.cs`",`"line`":42,`"column`":17,`"brief`":true,`"workspace_path`":`"MySolution.slnx`",`"require_workspace`":true}}],`"continue_on_error`":true}"
```

## Troubleshooting (Fast Fail-Closed)

- Args/schema error: run `roscli describe-command <command-id>` once, fix args, retry.
- Workspace is `ad_hoc` for project code: rerun with explicit `--workspace-path ... --require-workspace true`.
- Solution scope is expected but `resolved_workspace_path` is a project file: rerun with the `.sln`/`.slnx` path.
- You see `CS0518` (missing core types): treat as invalid workspace binding and retry with correct workspace root.

## Deep Reference (Progressive Disclosure)

Keep this SKILL.md small. Use these references only when needed:

- `references/diagnostics.md`: workspace vs solution snapshots, common failure modes
- `references/sessions.md`: non-destructive sessions, apply-and-commit
- `references/performance.md`: `roscli-fast.exe` + `WorkspaceHost` daemon and published-mode speed knobs (`ROSCLI_USE_PUBLISHED`)
- `references/dependency-intel.md`: when to combine with `dotnet-inspect`
