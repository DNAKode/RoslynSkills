# RoslynSkills Pit Of Success

Date: 2026-11-26  
Status: active onboarding contract (`0.1.6-preview.32`; empirical comparison in [ROSLYN_HARNESS_INTEGRATION_REPORT.md](ROSLYN_HARNESS_INTEGRATION_REPORT.md))

## Principle

RoslynSkills should make the safest high-value workflow the easiest workflow for agents:

1. use the right lane (**text first, semantic second, scoped diag third**),
2. bind the hot solution once and use `roscli-fast` (`ROSCLI_DAEMON=required`),
3. target directly with `--symbol Type.Member` and keep payloads bounded (`--brief true`; `nav.call_hierarchy` defaults to `--max-depth 1`, and redirected CLI stdout emits compact JSON automatically),
4. verify with scoped `diag.get_workspace_snapshot --directory-path` before full `dotnet build`/`test`.

If an agent has to guess argument shapes or fumble through file types, this contract is not being met.

## Channel & Tool Selection (`0.1.6-preview.32`)

Measured against the 33-project `CalcDb.slnx` solution (`0.1.6-preview.32`; see [ROSLYN_HARNESS_INTEGRATION_REPORT.md](ROSLYN_HARNESS_INTEGRATION_REPORT.md)):

| Channel / Tool | When To Use | Warm Speed & Cost Profile | Guardrail |
|---|---|---|---|
| **Built-in `rg` + `read_file`** | String literals, error codes, docs, non-C# files (`.slnx`, `.csproj`, `.json`), or reading a known 50–150 line slice | `~66–116 ms`; `0` schema/warmup tax | Do not grep common/overloaded C# names (`Execute` `47K` tok, `Kind` `23K` tok) or infer interface callers by text. |
| **`roscli-fast.exe` + `WorkspaceHost`** | **Default C# semantic lane:** `--symbol` lookup, references, invocations, implementations, `nav.call_hierarchy` (`854z`, default `--max-depth 1`), `ctx.file_outline` (hot cached `SyntaxTree`), `query.batch`, `edit.*`, scoped `diag.get_workspace_snapshot` | `~50–75 ms` wall (`3–10 ms` host exec); compact JSON on redirected stdout (`--pretty-json` opt-in); `0` per-turn schema tax | Bind once (`workspace.use <slnx> --require-solution true`), set `ROSCLI_DAEMON=required`, and stop your own daemon (`daemon.stop`) at closeout. |
| **`roscli` (.NET CLI) + `WorkspaceHost`** | Fallback when `roscli-fast.exe` is unavailable (or for CLI-only verbs / `--input-stdin`) | `~365–465 ms` warm (`~330 ms` CLR spawn) | Prefer `roscli-fast.exe` for multi-call loops. |
| **`RoslynSkills.McpServer --mode slim`** | Stdio MCP environments | Direct persistent `WorkspaceHost` pipe (`auto-bind` on first workspace call); **`~4.9 KB` (`~1.2K` tok/turn)** across `10` complete-schema tools | Use `--mode slim` instead of `--mode full` (`59` tools, `53.85 KB` / `~13.5K` tok/turn); `--mode full` stays available for backward compatibility. |
| **External C# LSP (`csharp-ls`, `Roslyn LSP`)** | Interactive IDE viewports only | `Roslyn LSP`: `~515 ms` refs, `0` interface callers (`854z`); `csharp-ls`: `.NET 11` reference breaks & false diagnostics | Never trust LSP results before `projectInitializationComplete`, and do not treat `csharp-ls` diagnostics/refs as truth on multi-TFM `.slnx` repos. |
| **`dotnet build` / `dotnet test`** | Final verification gate | `~47–233 s` on large solutions | Run `diag.get_workspace_snapshot --directory-path <dir>` (`~104–240 ms`) first so broken compiles fail fast. |

## First Minute Workflow

Run this sequence at session start before reading or editing `.cs` files:

```text
roscli --version
roscli csharp-start
roscli ctx.changed_files
roscli workspace.preload MySolution.slnx --alias default --require-solution true
roscli ctx.file_outline tests/MyTests.cs --member-name-contains Target --max-members 20
roscli ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text "ExpectedLiteral" --context-lines-before 3 --context-lines-after 8
roscli describe-command edit.replace_in_member
roscli list-commands --ids-only
roscli list-commands --stable-only --ids-only
```

This gives command discovery, a hot solution workspace, member-scoped source context, and the high-traffic scoped edit schema up front.

`roscli csharp-start` is the compact markdown version intended for fresh agents. Use it when a prompt can only point to one roscli onboarding command.

For supervised fresh-agent trials, use `roscli csharp-start --supervised` as a two-turn protocol. First require the agent to run only `roscli csharp-start` and report its first two headings. Assign the C# slice only after the transcript shows the command actually ran; prose promises are not enough. Turn 2 should preload the solution with `workspace.preload` before `ctx.file_outline` or `ctx.member_source`, should run `describe-command` before the first Roslyn edit command, should keep broad `ctx.search_text` capped and switch to focused outline/member-source reads after high-match results or a `result_guidance` recommendation, and should use `ctx.search_text` or `ctx.member_source` instead of `rg` for `.cs` closeout anchors.

For `roscli agent-start` fresh panes, the next repo-facing action after startup must be `roscli agent-begin`. Do not read docs, list files, inspect git, run searches, or start implementation until `agent-begin` succeeds and its three step summaries are reported.

When the solution filename is known, prefer `roscli csharp-start --supervised --solution <solution.sln|.slnx>` so Turn 2 contains a copyable `workspace.preload` command instead of a placeholder.

For `.cs` orientation in a dirty repo, start with `ctx.changed_files` to see changed C# paths without reading file contents, then use `ctx.file_outline --member-name-contains <term> --max-members 20`, `ctx.member_source --member-name <name> --focus-text <literal>`, capped `ctx.search_text`, or `nav.*` before `git diff`, `rg`, `Get-Content`, `sed`, `cat`, or a patch-editor read. `ctx.search_text` accepts both positional scope and direct aliases such as `roscli ctx.search_text --file-path src/MyFile.cs --text RemoteUserAction --max-results 20 --context-lines 0`. If a search response includes `result_guidance`, follow its suggested narrowing command instead of repeating another broad search. If fallback is required, state which roscli command was missing or insufficient.

## Command Tiers

- `stable`: default path for production agent loops.
- `advanced`: deeper analysis/orchestration; may be slower and/or partially heuristic.
- `experimental`: evolving contract for useful but less-stable outputs.

Default policy:

- Start with `stable` commands.
- Use `advanced`/`experimental` only when the task needs them.
- For non-stable commands, run `describe-command <id>` first and cap scope (`--brief`, small limits).

## Golden Paths

### 1) Fresh C# slice

First-slice budget for fresh agents: target one behavior, one primary claimed file when feasible, and one focused test before wider validation. If focused tests already pass on existing dirty work and no new mutation is needed, report validation-only and choose a different smallest unimplemented increment unless the user asked only to validate. If the work expands past two C# files or more than one behavior, stop at the smallest validated increment and report the remaining work instead of turning the first slice into a broad feature.

```text
roscli csharp-start --supervised
roscli csharp-start --supervised --solution MySolution.slnx
roscli csharp-start
roscli ctx.changed_files
roscli workspace.preload MySolution.slnx --alias default --require-solution true
roscli ctx.file_outline tests/MyTests.cs --member-name-contains Target --max-members 20
roscli ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text "ExpectedLiteral" --context-lines-before 3 --context-lines-after 8
roscli text.measure --text "fixed-width UI label"
roscli edit.claim claim tests/MyTests.cs --reason narrow-csharp-slice
roscli edit.replace_in_member tests/MyTests.cs --member-name TargetTest --old-text "Assert.Equal(1, value);" --new-text "Assert.Equal(2, value);" --preview-chars 256
dotnet test tests/MyTests.csproj --no-restore --filter FullyQualifiedName~TargetTest
roscli edit.claim release <claim_id>
```

### 2) Direct `--symbol` navigation & interface call hierarchy (`0.1.6-preview.32`)

All 6 primary `nav.*` commands (`go_to_definition`, `find_references`, `find_implementations`, `find_overrides`, `find_invocations`, `call_hierarchy`) accept `--symbol Type.Member` (including synthesized positional-record properties and optional parameter types like `Type.Method(ArgType)`), skipping intermediate coordinate-lookup calls:

```text
roscli-fast nav.go_to_definition --symbol MyNamespace.SchemaWriter.CreateTable --brief true
roscli-fast nav.find_references --symbol MyNamespace.DeferredCatalogCommit.Kind --max-results 100
roscli-fast nav.find_invocations --symbol MyNamespace.SchemaWriter.CreateTable --brief true --max-results 100
roscli-fast nav.find_implementations --symbol MyNamespace.ISqlDatabase --max-results 50
roscli-fast nav.call_hierarchy --symbol MyNamespace.ComSqlDatabase.OpenSchemaWriter --direction incoming --brief true
```

`nav.call_hierarchy` defaults to `--max-depth 1` (opt in to `--max-depth 2..8` only when multi-hop traversal is needed, since `depth=2` on high-fan-in methods can exceed 100 KB / ~31K tokens); interface-dispatched edges surface `edges[].implemented_member`.

### 3) Safe symbol rename

```text
roscli-fast nav.find_symbol src/MyProject/Program.cs Process --brief true --max-results 20 --workspace-path MySolution.slnx --require-workspace true
roscli-fast edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true --workspace-path MySolution.slnx --require-workspace true
roscli-fast diag.get_file_diagnostics src/MyProject/Program.cs --workspace-path MySolution.slnx --require-workspace true
```

### 4) Create new file in one shot

```text
roscli edit.create_file src/MyProject/NewType.cs --content "public class NewType { }"
roscli diag.get_file_diagnostics src/MyProject/NewType.cs
```

### 5) Session-based edit loop

```text
roscli session.open src/MyProject/Program.cs demo-session
roscli session.status demo-session
roscli session.diff demo-session
roscli session.commit demo-session --keep-session false --require-disk-unchanged true
```

Note: `session.*` diagnostics are file-only (`ad_hoc`). For project-backed errors/warnings, prefer `diag.get_file_diagnostics` or `diag.get_after_edit` with `--require-workspace true` and pass `--workspace-path` if needed.

### 6) Multi-agent claim-first edit loop

```text
roscli edit.claim list
roscli edit.claim claim src/MyFile.cs --owner agent-a --reason focused-slice
roscli ctx.member_source src/MyFile.cs --member-name HandleInput --focus-text "TargetLiteral" --context-lines-before 3 --context-lines-after 8
roscli edit.replace_in_member src/MyFile.cs --member-name HandleInput --old-text "old exact text" --new-text "new exact text" --preview-chars 256
dotnet test tests/MyTests.csproj --no-restore --filter FullyQualifiedName~FocusedTest
roscli edit.claim release <claim_id>
```

For multiple subagents, use distinct stable `--owner` names and disjoint file claims. Assign known file ownership before spawning; if a subagent discovers a file later, it must claim before its first edit and report the claim id. Serialize shared-file work through one owner. If several changes target the same member or multiple files, combine them into one fresh `edit.replace_in_member` old/new block or one `edit.transaction` (`edit.batch_exact` is its thin alias) using `replace_text`, `insert_text`, or `replace_span` with `expected_text`; do not run parallel edit commands against stale `ctx.member_source` reads. Prefer guarded mutations: `edit.replace_in_member`, `edit.transaction` / `edit.batch_exact` with `expected_text`, or `session.commit --require-disk-unchanged true`.

Mutation command responses include `claim_status` (using shared Git worktree-aware repository-root discovery). If a write summary says `unclaimed`, stop further C# mutation, run `edit.claim claim <file> --owner <owner> --reason <slice>`, then continue with guarded edits.

### 7) Workspace-backed directory triage (before `dotnet build`)

```text
roscli-fast diag.get_workspace_snapshot --directory-path src/MyProject --brief true --include-diagnostics true --require-workspace true
roscli-fast diag.get_workspace_snapshot --directory-path src --brief true --require-workspace true --max-files 500
roscli-fast diag.get_workspace_snapshot --directory-path src --require-workspace true --workspace-path MySolution.slnx
```

Note: `diag.get_workspace_snapshot --directory-path` compiles every project rooted under that directory in `~104–240 ms` warm on a bound solution, catching compiler errors across multiple files/projects before a slower `dotnet build`/`test` run.

## Guardrails (Must Be Explicit)

- **Bind once & fail closed:** In multi-call sessions, bind `roscli workspace.use <MySolution.slnx> --require-solution true` once, set `ROSCLI_DAEMON=required`, and use `roscli-fast` (`%LOCALAPPDATA%\RoslynSkills\bin\roscli-fast.exe`) for hot queries. Stop **only your own** worktree daemon at closeout (`roscli daemon.stop --repo-root <path>`); never run `daemon.prune --all` when sibling agents share the host.
- **Keep outlines filtered on large files:** `ctx.file_outline` is hot-routed via `roscli-fast.exe` (reading the cached `SyntaxTree` from `WorkspaceHost`), so it stays fast even on huge files; still filter with `--member-name-contains <term> --max-members 20` to keep output tokens bounded.

- `session.open` supports only `.cs` and `.csx`.
- `session.*` diagnostics are `ad_hoc` (file-only). Missing type/reference errors may be false negatives until verified with workspace-backed diagnostics (`diag.get_file_diagnostics`).
- Do not open `.sln`, `.slnx`, or `.csproj` with `session.open`.
- Check `workspace_context.mode` on semantic file commands (for example `nav.find_symbol`, `nav.find_references`, `ctx.symbol_envelope`, `diag.get_file_diagnostics`, `diag.get_after_edit`).
- Prefer explicit `.sln`/`.slnx` workspace paths for repo-wide context and hot workspace hosts; use `.csproj` only when intentionally project-scoped.
- Check `workspace_context.resolved_workspace_path`, `workspace_context.workspace_kind`, and `workspace_context.project_count` when full solution context matters.
- If `workspace_context.mode` is `ad_hoc` for project code, rerun with `--workspace-path <.sln|.slnx|.csproj|dir>` and prefer `--require-workspace true`.
- For complex payloads, prefer `--input-stdin` over shell-escaped JSON.
- **Production hybrid split vs. supervised benchmark trials:** In production harnesses, use `rg`/`read_file` for string literals, docs, non-C# files, and known slices, and `roscli-fast` (`nav.*`/`edit.*`/`diag.*`) for semantic symbol navigation, overloads, interface call hierarchies, refactors, and folder diagnostics. In strict supervised Roslyn-only benchmark trials (`csharp-start --supervised`), follow `ctx.changed_files`, `ctx.member_source`, and `ctx.search_text` before `git diff`/`rg`/`Get-Content`, and report any fallback explicitly.

## Complementary Tool Split

Use `dotnet-inspect` for external package/framework API questions.  
Use `roscli` for in-repo semantic navigation, editing, and diagnostics.

Combined migration pattern:

1. inspect external API shape first,
2. execute local semantic changes with `roscli`,
3. verify via diagnostics/build/tests.

## Agent Prompt Block (Production Hybrid)

```text
Use roscli-fast (or roscli) + WorkspaceHost for semantic C# work in this session.
Workflow:
1) Bind the hot solution once: "roscli workspace.use <solution.sln|.slnx> --require-solution true", then set ROSCLI_DAEMON=required.
2) Text first for literals/docs/non-C#/known slices ("rg -n -w", targeted "read_file"); semantic second via "roscli-fast" for overloaded/ambiguous symbols, cross-project callers, and edits.
3) Prefer direct "--symbol Type.Member" on nav.* (go_to_definition, find_references, find_implementations, find_overrides, find_invocations, call_hierarchy); call_hierarchy defaults to "--max-depth 1" (captures interface dispatch via implemented_member).
4) Use "ctx.file_outline <file> --member-name-contains <term> --max-members 20" (hot-routed via roscli-fast) or "ctx.member_source --member-name" for targeted structural reads.
5) Batch read-only queries with "query.batch" (supports nav.find_implementations, nav.find_overrides, diag.get_workspace_snapshot, ctx.symbol_envelope, ctx.changed_files, ctx.file_outline, etc.); for multi-file edits use "edit.rename_symbol", "edit.change_signature", "edit.apply_code_fix", or "edit.transaction" with expected_text.
6) Scoped diag third: check edited folders with "roscli-fast diag.get_workspace_snapshot --directory-path <dir> --brief true --include-diagnostics true --require-workspace true" before running "dotnet build" / "dotnet test".
7) Stop only your own daemon at closeout ("roscli daemon.stop --repo-root <path>") and release any active claims.
```

(For strict supervised Roslyn-only benchmark panes, generate the gated two-turn prompt with `roscli agent-start --solution <solution.sln|.slnx>` or `roscli csharp-start --supervised`.)

## Anti-Patterns

- Starting with full-solution diagnostics or `dotnet build` when `diag.get_workspace_snapshot --directory-path <dir>` or file diagnostics suffice.
- Querying `nav.call_hierarchy` with unbounded `--max-depth` on high-fan-in methods (start with `--max-depth 1 --brief true`).
- Loading 59-tool full MCP (`--mode full`, `~13.5K` tokens/turn) or using external C# LSP (`csharp-ls` / `Roslyn LSP`) when `roscli-fast.exe` + `WorkspaceHost` or `RoslynSkills.McpServer --mode slim` (`~4.9 KB`) is available.
- Using `rg` to enumerate callers/references of common or overloaded C# symbols (`Execute`, `OpenRecordset`, `Kind`) instead of `nav.find_references` / `nav.find_invocations --symbol`.
- Starting `.cs` semantic/structural edits without checking `workspace_context.mode`.
- Repeated command retries without schema discovery (`describe-command`).
- Text edits for multi-file semantic changes before trying Roslyn primitives.
- Treating an LSP lane as valid evidence when tools were not actually available.

## Advanced/Experimental Analyzer Set

Current non-stable analysis commands (use intentionally with bounded scope):

1. `nav.call_path` (`experimental`, `heuristic`, `potentially_slow`)
Bounded shortest-path search between source and target methods.
2. `analyze.unused_private_symbols` (`advanced`, `heuristic`, `derived_analysis`)
Likely-unused private symbol detection for cleanup triage.
3. `analyze.dependency_violations` (`experimental`, `heuristic`, `derived_analysis`)
Namespace-layer rule checks from ordered layer prefixes.
4. `analyze.impact_slice` (`advanced`, `heuristic`, `derived_analysis`)
Bounded impact slice around an anchored symbol.
5. `analyze.override_coverage` (`advanced`, `derived_analysis`)
Override/derived-type coverage hotspot detection for virtual/abstract members.
6. `analyze.async_risk_scan` (`experimental`, `heuristic`, `derived_analysis`)
Common async/sync-mixing risk pattern scan (for example `Task.Wait`, `.Result`, `async void`).

Contract rule:

- Every non-stable command must ship with explicit caveats in `describe-command` notes and bounded options (`max_*`, `brief`).

## Release Artifact Expectations

Every release bundle should include:

- `bin/roscli(.cmd)` launcher,
- this `PIT_OF_SUCCESS.md` guide,
- `skills/roslynskills-research/SKILL.md`,
- `skills/roslynskills-tight/SKILL.md`.

First bundle command should still be:

```text
bin/roscli quickstart
```
