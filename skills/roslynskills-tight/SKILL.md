---
name: roslynskills-tight
description: Precise C#/.NET navigation, rename/refactor, and diagnostics using RoslynSkills (roscli). Use when user asks to rename/refactor a C# symbol, disambiguate overloads/collisions, fix C# build/diagnostic errors, or make safe multi-file edits. Avoid for non-C# repos or pure formatting.
license: MIT
compatibility: Claude.ai / Claude Code. Requires local shell access (Bash or PowerShell) plus RoslynSkills CLI (roscli) or RoslynSkills MCP server.
metadata:
  author: DNA Kode
  mcp-server: roslynskills
allowed-tools: "Bash Read Write Edit Grep Glob"
---

# RoslynSkills (Tight)

This skill is optimized for low-churn, "just enough Roslyn" workflows.

## Boundaries

- `roscli`: in-repo semantic work (symbol targeting, edits/refactors, diagnostics).
- Not `roscli`: external package API lookup/version diff/security intelligence.
- For external API/package questions, use dotnet helper tooling first (for example `dotnet-inspect`), then apply in-repo edits with `roscli`.

## Rules (Pit Of Success)

- **Hybrid routing ("overlay/text first, compound mindspace second, scoped diag third"):**
  - Use `rg` / `read_file` for non-C# files (`.md`, `.slnx`, `.csproj`, `.json`) and quick slices when no daemon is bound (`~66–116 ms`).
  - Use `roscli` / `roscli-fast` (`0.1.6-preview.33`) on a bound daemon for C# discovery & edits:
    - **`ctx.symbol_context --symbol Type.Member`** (1-call mindspace: zero-disk def + `file_sha256` + refs + interface-aware callers + callees + impls + file diag in `~64 ms` CLI / `~14 ms` MCP).
    - **`ctx.search_text --syntax-filter code|declarations|invocations --whole-word true`** (zero-disk overlay trigram/token index, `~58 ms` selective, batched `queries`) and **`ctx.read_window`** (zero-disk single/batched windows + `file_sha256`, `0.2 ms` host).
    - `--symbol` / `--symbols` across `nav.*`, `edit.rename_symbol`, `edit.change_signature`, `nav.call_path` (`--source-symbol`/`--target-symbol`), atomic SHA-256 `edit.transaction` / `edit.batch_exact`, and scoped `diag.get_workspace_snapshot --directory-path`.
- **Fast daemon workflow (`0.1.6-preview.33`):**
  - Bind the solution once: `roscli workspace.use <MySolution.slnx> --require-solution true` (add `--wait-warmup true` to wait for `warmup_state=complete`), then set `ROSCLI_DAEMON=required`.
  - Prefer `roscli-fast` (`%LOCALAPPDATA%\RoslynSkills\bin\roscli-fast.exe`) over managed `roscli` for `~54–71 ms` warm queries (vs `~365–465 ms` CLR spawn); redirected stdout emits compact JSON automatically (`--pretty-json` opt-in). Stop only your own daemon (`roscli daemon.stop`) when finished.
- **Channel choice (CLI vs MCP vs LSP):**
  - Default to `roscli-fast` / `roscli` + this skill (`0` per-turn schema tax). When using MCP, launch `RoslynSkills.McpServer --mode slim` (`12` descriptor-driven tools including `roslyn_search` + `roslyn_context`, `6,016 B` `tools/list`, `4.8–15.6 ms` warm over a persistent `WorkspaceHost` pipe with auto-bind) rather than `--mode full` (`61` tools, `90.5 KB`).
  - Prefer `roscli-fast` over C# LSP (`csharp-ls` / `Roslyn LSP`) on multi-project `.slnx` repos: `roscli` fails closed (`--require-workspace true`), resolves `--symbol` directly, captures interface callers (`edges[].implemented_member`), and compiles folder diagnostics in `~104–240 ms`.
- Default to `stable` commands; use `advanced`/`experimental` commands intentionally (`nav.call_hierarchy`, `diag.get_workspace_snapshot`).
- Keep Roslyn calls sequential per workspace (avoid parallel `dotnet` locks).
- Prefer `--brief true`, small `--max-results` / `--max-diagnostics`; `nav.call_hierarchy` defaults to `--max-depth 1` (opt in to `--max-depth 2..8` only when needed).
- Filter `ctx.file_outline` with `--member-name-contains <term> --max-members 20` on large files (hot-routed via `roscli-fast.exe` using the cached `SyntaxTree`).
- For project code, require a workspace: add `--workspace-path <.sln|.slnx|.csproj|dir> --require-workspace true`.
- Prefer `.sln`/`.slnx` for repo-wide or hot-workspace context; use `.csproj` only when intentionally project-scoped.
- Treat `workspace_context.mode=ad_hoc` as "not safe enough" for refactors.
- Command output always puts the payload directly in top-level `Data` (whether via daemon or in-process); check `Transport.mode` (`daemon` vs `in_process`) for routing info.
- If using Claude Code's `Bash` tool (common even on Windows), always run RoslynSkills as `bash scripts/roscli ...` (no backslashes, do not call `scripts\roscli.cmd`).
- Never claim you used RoslynSkills unless you actually ran `roscli` / `roscli-fast` / `scripts/roscli` commands; otherwise state the fallback explicitly.
- For high-call loops without `roscli-fast`, use cached published wrappers (`ROSCLI_USE_PUBLISHED=1`); after editing roscli itself, force one refresh (`ROSCLI_REFRESH_PUBLISHED=1`).

## Start (2 commands)

If you're using the `Bash` tool (recommended for portability):

```bash
bash scripts/roscli list-commands --ids-only
bash scripts/roscli list-commands --stable-only --ids-only
bash scripts/roscli quickstart
```

If you're using PowerShell and `scripts\roscli.cmd` exists:

```powershell
scripts\roscli.cmd list-commands --ids-only
scripts\roscli.cmd list-commands --stable-only --ids-only
scripts\roscli.cmd quickstart
```

If `roscli` is on PATH (either shell):

```powershell
roscli list-commands --ids-only
roscli list-commands --stable-only --ids-only
roscli quickstart
```

## Minimal Workflows

### 1) One-call mindspace, overlay search & direct `--symbol` workflows (`0.1.6-preview.33`)

Use `ctx.symbol_context --symbol` first, syntax-filtered `ctx.search_text` / zero-disk `ctx.read_window` on the live overlay index, `--symbols` batching on refs/invocations, and `--symbol` directly on `edit.rename_symbol` / `edit.change_signature` / `nav.call_path`:

```powershell
roscli workspace.use MySolution.slnx --require-solution true
roscli-fast ctx.symbol_context --symbol MyNamespace.ComSqlDatabase.OpenSchemaWriter --brief true
roscli-fast ctx.search_text --pattern Execute --whole-word true --syntax-filter declarations
roscli-fast ctx.read_window src/MyProject/File.cs 1 100
roscli-fast nav.find_references --symbols MyNamespace.DeferredCatalogCommit.Kind,MyNamespace.SchemaWriter.CreateTable --max-results 50
roscli-fast edit.rename_symbol --symbol MyNamespace.MyType.OldName --new-name NewName --apply true --require-workspace true
roscli-fast diag.get_workspace_snapshot --directory-path src/MyProject --brief true --include-diagnostics true --require-workspace true
```

Note: `ctx.symbol_context` and `nav.call_hierarchy` resolve incoming callers across implemented interface members (`edges[].implemented_member`), and `ctx.read_window` / `ctx.symbol_context` return `file_sha256` for optimistic locking with `edit.transaction --expected-sha256` / `expected_file_hashes`.

### 2) Ambiguous target & rename (2 calls, then stop)

```bash
bash scripts/roscli nav.find_symbol src/MyFile.cs MySymbol --brief true --max-results 50 --workspace-path MySolution.slnx --require-workspace true
bash scripts/roscli edit.rename_symbol src/MyFile.cs 42 17 NewName --apply true --max-diagnostics 50 --workspace-path MySolution.slnx --require-workspace true
```

Success criteria:

- 2 calls total.
- In the `edit.rename_symbol` response, `diagnostics_after_edit` reports `0` errors.

Only if the edit response does not include diagnostics (or reports errors/warnings), add one verification call:

```powershell
scripts\roscli.cmd diag.get_file_diagnostics src/MyFile.cs --workspace-path MySolution.slnx --require-workspace true
```

### 3) Known coordinates (1 call, then stop)

```bash
bash scripts/roscli edit.rename_symbol src/MyFile.cs 42 17 NewName --apply true --max-diagnostics 50 --workspace-path MySolution.slnx --require-workspace true
```

### 4) Cross-project investigation (text + calls, bounded)

```bash
bash scripts/roscli ctx.search_text "RemoteUserAction" src --mode literal --max-results 100 --brief true
bash scripts/roscli nav.find_invocations --symbol MyNamespace.MyType.Method --brief true --max-results 100
bash scripts/roscli nav.call_hierarchy --symbol MyNamespace.MyType.Method --direction both --max-depth 1 --brief true
bash scripts/roscli diag.get_workspace_snapshot --directory-path src/MyProject --brief true --include-diagnostics true --require-workspace true
bash scripts/roscli analyze.control_flow_graph src/MyFile.cs 42 17 --brief true --max-blocks 120 --max-edges 260 --workspace-path MySolution.slnx --require-workspace true
bash scripts/roscli analyze.dataflow_slice src/MyFile.cs 42 17 --brief true --max-symbols 120 --workspace-path MySolution.slnx --require-workspace true
bash scripts/roscli analyze.unused_private_symbols src --brief true --max-symbols 120
bash scripts/roscli analyze.impact_slice src/MyFile.cs 42 17 --brief true --include-callers true --include-callees true
bash scripts/roscli analyze.async_risk_scan src --brief true --max-findings 120 --severity-filter warning --severity-filter info
```

If you need several read-only checks in one round-trip, use `query.batch` (supports `ctx.*`, `nav.*` including `nav.find_implementations` and `nav.find_overrides`, `analyze.*`, `diag.get_file_diagnostics`, and `diag.get_workspace_snapshot`):

```bash
bash scripts/roscli run query.batch --input '{"queries":[{"command_id":"ctx.search_text","input":{"patterns":["RemoteUserAction","ReplicationUpdate"],"roots":["src"],"mode":"literal","max_results":120}},{"command_id":"nav.find_invocations","input":{"file_path":"src/MyFile.cs","line":42,"column":17,"brief":true,"workspace_path":"MySolution.slnx","require_workspace":true}}],"continue_on_error":true}'
```

## Troubleshooting (Do This, Not That)

- Input/schema error:
  - run `scripts\roscli.cmd describe-command <command-id>` once, fix args, retry.
- Workspace is `ad_hoc` on project code:
  - rerun with `--workspace-path ... --require-workspace true` (fail closed).
- Solution scope is expected but `resolved_workspace_path` is a project file:
  - rerun with the `.sln`/`.slnx` path.
- You see `CS0518` (missing core types):
  - treat as invalid workspace binding; retry with explicit workspace args or different workspace root.
- If you must fall back to text-based edits:
  - say so in your final report, with RoslynSkills version (`roscli --version`), reason, and the Roslyn command/option that would have avoided the fallback.
