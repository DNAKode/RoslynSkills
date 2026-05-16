# Roscli Hot Workspace Host Plan

Date: 2026-05-15

## Objective

Make `roscli` work as a command-line client for a long-running Roslyn host that keeps a full solution hot, while relying on Roslyn's own incremental workspace semantics instead of reimplementing semantic invalidation.

## Core Principle

The daemon should not become a second Roslyn project system. Its job is to:

1. Keep an MSBuild/Roslyn `Solution` loaded.
2. Accept command-line-friendly semantic requests.
3. Detect filesystem changes.
4. Feed simple document text changes into Roslyn incrementally.
5. Mark structural changes as requiring reload.
6. Expose freshness, dirty state, and fallback behavior explicitly.

Roslyn owns semantic invalidation. RoslynSkills owns host lifecycle and command ergonomics.

## Target UX

Primary agent flow:

```text
roscli use MySolution.slnx --require-solution true
roscli nav.find_symbol src/App/Foo.cs Foo --require-workspace true
roscli ctx.member_source src/App/Foo.cs 42 17 body --brief true
roscli diag.get_file_diagnostics src/App/Foo.cs --require-fresh true
roscli query.batch --queries @queries.json
roscli workspace.status
```

Explicit lifecycle flow:

```text
roscli daemon.start
roscli workspace.preload MySolution.slnx --alias default --require-solution true
roscli workspace.status default
roscli workspace.refresh default
roscli workspace.close default
roscli daemon.stop
```

One-shot compatibility remains:

```text
roscli nav.find_symbol src/App/Foo.cs Foo --workspace-path MySolution.slnx
roscli --no-daemon nav.find_symbol src/App/Foo.cs Foo
```

## Live Trial Notes

2026-05-16 FrankenTui.NET supervised trial:

- Release/global-tool parity is part of the hot-server surface. A repo-local build that works is insufficient if the machine-wide `roscli` misses host binaries or daemon pseudo-commands.
- The effective startup path is now `roscli daemon.start`, `roscli workspace.preload <solution> --require-solution true`, then ordinary semantic commands without a handle. `workspace.preload` should persist alias `default` unless the caller supplies another alias.
- `ctx.member_source` must advertise line/column usage prominently. Agents attempted non-existent member-name forms when guidance was vague.
- Successful steady-state context calls should be observable through `query.workspace_context.workspace_cache_mode = process_hot` and `workspace_cache_hit = true`; the trial reduced repeated `ctx.member_source` calls from multi-second cold loads to sub-second hot calls.
- Multi-agent work needs coordination before mutation. `edit.claim` now provides repo-local file/member claims with TTL and conflict reporting. This does not replace semantic edit commands; it prevents overlapping agents from editing the same region blindly.
- Remaining adoption gap: agents still prefer text patching for non-trivial body edits after Roslyn navigation. Next ergonomics work should make `edit.transaction`/`session.apply_and_commit` as easy to invoke as `ctx.member_source`, ideally with claim-aware examples and low-friction span/member replacement recipes.

2026-05-16 `.23` follow-up:

- Added `edit.replace_text` as a direct exact-snippet mutation bridge with diagnostics. It works for replacements and gives agents a roscli command that is much closer to patch-editor ergonomics than raw transaction JSON.
- In the next FrankenTui.NET slice the agent still patched C# for two one-line insertions after known evidence fields. This suggests the next command surface should explicitly support `insert before/after exact anchor`, not only replacement.
- Track mutation channel separately: the same trajectory can be a strong success for hot semantic context (`process_hot`), claims, diagnostics, and tests, while still failing the Roslyn-backed mutation adoption goal.

2026-05-16 `.24` follow-up:

- Added `edit.insert_text` for exact-anchor insertions before/after a unique snippet, with direct CLI shorthand and immediate diagnostics.
- This command targets the observed FrankenTui.NET pattern directly: add an evidence field or assertion after a known neighboring line without dropping into patch mode.
- Next supervised prompt should explicitly prefer `edit.insert_text` for one-line additions and require the agent to state the anchor used.

2026-05-16 `.25` follow-up:

- Live `.24` FrankenTui.NET run used roscli hot workspace for search/member context, claims, diagnostics, and focused tests, but still mutated C# through the patch editor for a one-line replacement and test insertion.
- `ctx.member_source` now returns an `edit_workflow` block that puts the claim-first rule, target line range, and preferred mutation commands (`edit.replace_text`, `edit.insert_text`, `edit.transaction`) next to the source payload.
- Fixed `edit.claim release <claim_id>` direct CLI behavior so claim IDs act as explicit release tokens even when the releasing shell does not pass the original owner. Path-based release remains owner-scoped unless forced.
- Next supervised prompt should require the agent to read `ctx.member_source.edit_workflow` and select one listed mutation command before any `.cs` patch-editor fallback.

2026-05-16 `.25` supervised result:

- Positive adoption signal: the FrankenTui.NET agent observed `ctx.member_source.edit_workflow`, explicitly selected `edit.replace_text`, claimed the two C# files, and applied both C# changes via `roscli run edit.replace_text --input-stdin`.
- Remaining product gap: `edit.replace_text` immediate diagnostics are syntax/file-context diagnostics and can look noisy for project-dependent files. The agent correctly used hot `diag.get_file_diagnostics` afterward. Next improvement should let simple edit commands accept/reuse the hot workspace context for post-edit diagnostics.
- Release cleanup signal: `edit.claim release <claim_id>` should now be re-tested in the live pane because `.24` failed there and `.25` smoke passed locally.

2026-05-16 `.26` follow-up:

- `edit.replace_text` and `edit.insert_text` now use workspace-backed updated-source diagnostics when a workspace is available or explicitly supplied. The response includes `diagnostics_after_* .workspace_context` and reports `mode = workspace_updated_source` when project context was used.
- The implementation carries the Roslyn `Document` through `CommandFileAnalysis` and evaluates edits with `Solution.WithDocumentText`, which preserves project references and avoids the false file-only errors seen in the `.25` FrankenTui.NET run.
- Next supervised prompt should check whether the agent can trust `diagnostics_after_replace` directly after exact text edits instead of always adding a separate `diag.get_file_diagnostics` round-trip.

## Architecture

Add a long-running local host process:

```text
RoslynSkills.WorkspaceHost
```

Responsibilities:

- Own one or more loaded workspaces.
- Load `.sln`, `.slnx`, `.csproj`, and `.vbproj` using `MSBuildWorkspace`.
- Prefer solution scope by default.
- Keep current `Solution` state in memory.
- Maintain aliases and handles.
- Serve command requests over local IPC.
- Track filesystem changes.
- Apply incremental source updates through Roslyn APIs.
- Require reload for structural changes.

Transport:

- Windows: named pipe.
- Unix/macOS: Unix domain socket.
- Fallback/debug: stdio transport, similar to current `RoslynSkills.TransportServer`.

Endpoint identity:

```text
user + repo root + workspace path + target framework/environment fingerprint
```

Avoid accidental cross-repo reuse.

## Workspace State Model

Each hosted workspace should maintain:

```json
{
  "workspace_handle": "ws_...",
  "alias": "default",
  "requested_workspace_path": "MySolution.slnx",
  "resolved_workspace_path": "C:/repo/MySolution.slnx",
  "workspace_kind": "slnx",
  "solution_scoped": true,
  "project_count": 12,
  "document_count": 1800,
  "loaded_at_utc": "...",
  "last_refresh_utc": "...",
  "workspace_fingerprint": "...",
  "dirty": false,
  "dirty_kind": "none",
  "requires_reload": false,
  "requires_design_time_build": false,
  "workspace_diagnostics": []
}
```

Internal state:

- `MSBuildWorkspace`
- current `Solution`
- project/document lookup indexes
- tracked file snapshot
- known dirty paths
- reload-required reasons
- command-use telemetry

## Invalidation Boundary

Do not reimplement Roslyn invalidation.

For source changes:

```csharp
solution = solution.WithDocumentText(documentId, newText);
```

or through workspace APIs where appropriate.

Roslyn then invalidates syntax trees, compilations, semantic models, and dependent state incrementally. The host only classifies the filesystem change and pushes the new text into Roslyn.

## Change Classification

Source-document change:

- `.cs`
- `.csx`
- `.vb`

If file belongs to the loaded solution:

```text
dirty_kind = source_change
can_incrementally_update = true
requires_reload = false
```

Refresh action:

- read file
- update document text in current solution
- clear dirty path if successful

Project structure change:

- `.csproj`
- `.vbproj`
- `.sln`
- `.slnx`
- `Directory.Build.props`
- `Directory.Build.targets`
- `Directory.Packages.props`
- `global.json`
- `NuGet.config`
- project reference changes
- package reference changes

Status:

```text
dirty_kind = project_structure_change
can_incrementally_update = false
requires_reload = true
```

Refresh action:

- reload workspace in balanced or strict mode

Analyzer/config change:

- `.editorconfig`
- `.globalconfig`
- analyzer config files
- ruleset/editorconfig affecting diagnostics

Conservative status:

```text
requires_reload = true
```

Generated or uncertain change:

- new source file not currently in solution
- deleted source file
- generated file outside known document set
- unknown additional file

Status:

```text
dirty_kind = unknown_or_membership_change
requires_reload = true
```

Ignored by default:

- `bin/**`
- `obj/**`
- `.git/**`
- test result output
- transient logs

Strict mode may fingerprint more aggressively.

## Refresh Modes

`workspace.refresh --mode balanced`

- Incrementally update changed source files known to Roslyn.
- Mark reload required for structural changes.
- Ignore known noisy outputs.
- Fast default for agent interaction.

`workspace.refresh --mode strict`

- Recheck solution/project/config fingerprints.
- Reload if anything structural changed.
- Used before benchmark scoring, promotion gates, or final verification.

`workspace.refresh --mode reload`

- Force full workspace reload.

## Freshness Policy

Commands should accept:

```text
--require-fresh true
--refresh-policy none|auto|strict
```

Defaults:

- Read-only semantic commands: `refresh-policy=auto`
- Edit commands: `refresh-policy=strict` or explicit preflight
- Benchmark gates: `strict`

Behavior:

```text
none    = report dirty state but do not refresh
auto    = apply incremental source updates; fail if reload required
strict  = refresh/reload as needed; fail if freshness cannot be guaranteed
```

## Command Routing

`roscli` should decide whether to run direct or client mode.

Routing order:

1. `--no-daemon`: run current in-process path.
2. `ROSCLI_DAEMON=off`: run current in-process path.
3. `ROSCLI_DAEMON=required`: connect/start daemon or fail.
4. `ROSCLI_DAEMON=auto`: use daemon for daemon-capable commands.
5. If daemon command fails due to protocol/version mismatch, fallback only when `require-hot-workspace` is false.

Useful environment variables:

```text
ROSCLI_DAEMON=auto|off|required
ROSCLI_WORKSPACE_ALIAS=default
ROSCLI_REQUIRE_HOT_WORKSPACE=true|false
ROSCLI_REFRESH_POLICY=auto|none|strict
```

## Client State

Persist routing metadata only:

```text
.roslynskills/workspaces.json
```

Example:

```json
{
  "default": {
    "workspace_path": "C:/repo/MySolution.slnx",
    "workspace_handle": "ws_abc",
    "daemon_endpoint": "pipe:roslynskills-...",
    "daemon_pid": 12345,
    "workspace_fingerprint": "...",
    "last_seen_utc": "..."
  }
}
```

No semantic state is stored on disk.

## Lifecycle Commands

Daemon:

```text
daemon.start
daemon.status
daemon.stop
daemon.restart
daemon.logs
```

Workspace:

```text
workspace.preload <path> --alias default --require-solution true
workspace.use <path> --alias default --require-solution true
workspace.status [alias]
workspace.refresh [alias] --mode balanced|strict|reload
workspace.close [alias]
workspace.list
workspace.explain [alias]
```

`workspace.explain` should tell the agent:

- what is loaded
- whether it is solution or project scoped
- whether it is dirty
- whether semantic commands are using the handle
- what to do next

## Command Support Matrix

Phase 1: read-only hot path

- `nav.find_symbol`
- `nav.find_symbol_batch`
- `nav.find_references`
- `nav.find_invocations`
- `ctx.member_source`
- `diag.get_file_diagnostics`
- `query.batch`

Phase 2: broader semantic analysis

- `nav.call_hierarchy`
- `nav.call_path`
- `analyze.control_flow_graph`
- `analyze.dataflow_slice`
- `analyze.impact_slice`
- `diag.get_workspace_snapshot`

Phase 3: structured edits

- `edit.rename_symbol`
- `edit.change_signature`
- `edit.transaction`
- `repair.*`

Edit commands need stronger freshness rules.

## Protocol

Use a stable JSON protocol.

Initial protocol contract:

- `docs/ROSCLI_WORKSPACE_HOST_PROTOCOL_2026-05-15.md`
- `src/RoslynSkills.Contracts/WorkspaceHostProtocolContracts.cs`

Request:

```json
{
  "id": "req-001",
  "method": "tool/call",
  "workspace_alias": "default",
  "workspace_handle": "ws_abc",
  "refresh_policy": "auto",
  "command_id": "nav.find_symbol",
  "input": {
    "file_path": "src/App/Foo.cs",
    "symbol_name": "Foo",
    "require_workspace": true
  }
}
```

Response includes freshness metadata:

```json
{
  "id": "req-001",
  "ok": true,
  "workspace": {
    "alias": "default",
    "workspace_handle": "ws_abc",
    "workspace_kind": "slnx",
    "resolution_source": "workspace_handle",
    "refresh_policy": "auto",
    "refresh_action": "incremental_document_update",
    "dirty_before": true,
    "dirty_after": false,
    "requires_reload": false
  },
  "envelope": {}
}
```

## Versioning

Client and daemon must handshake:

```json
{
  "protocol_version": "1.0",
  "cli_version": "1.0.0",
  "host_version": "1.0.0",
  "command_schema_version": "..."
}
```

Mismatch policy:

- compatible minor: proceed
- incompatible major: fail or restart daemon
- stale daemon binary: restart if safe

## Daemon Lifetime

Default:

- start on demand
- idle timeout: 10 minutes
- per-workspace idle timeout configurable
- explicit stop supported

Status should show:

```json
{
  "pid": 12345,
  "uptime_seconds": 410,
  "idle_timeout_seconds": 600,
  "workspace_count": 1,
  "requests_served": 84
}
```

## Correctness Gates

For solution-scoped hot workspace claims:

- `workspace_kind` must be `solution` or `slnx`
- `solution_scoped=true`
- `project_count > 1` for multi-project fixtures
- semantic commands report `resolution_source=workspace_handle`
- `workspace_cache_mode=process_hot`
- no `ad_hoc` fallback when `require_workspace=true`
- dirty/reload state is explicit

## Benchmark Matrix

Compare:

1. cold direct `roscli`
2. direct `roscli --workspace-path`
3. daemon hot workspace
4. MCP warm workspace
5. LSP comparator
6. text/grep baseline

Metrics:

- wall time
- first-call latency
- steady-state latency
- workspace load time
- refresh time
- token/character overhead
- retries
- correctness
- ad-hoc fallback count
- solution-vs-project binding correctness

## Failure Modes

Handle not found:

```text
workspace_handle_not_found
```

Solution required but project loaded:

```text
solution_required
```

Workspace dirty and command requires fresh state:

```text
workspace_stale
```

Reload required but auto refresh cannot reload safely:

```text
workspace_reload_required
```

Daemon unavailable:

```text
daemon_unavailable
```

Protocol mismatch:

```text
daemon_protocol_mismatch
```

Every failure should include a next command suggestion.

## Implementation Sequence

1. Formalize host protocol and response metadata.
2. Create `RoslynSkills.WorkspaceHost`.
3. Move workspace handle store behind a shared service abstraction.
4. Add named pipe / Unix socket transport.
5. Add `roscli` client connection layer.
6. Add daemon lifecycle commands.
7. Add `workspace.use/preload/status/refresh/close/list`.
8. Add client-side alias persistence.
9. Route read-only hot-path commands through daemon.
10. Implement file watching and change classification.
11. Implement incremental source refresh via Roslyn document text updates.
12. Implement strict reload path.
13. Add structured edit support.
14. Add benchmark gates and reports.
15. Harden auth/security/versioning.

## Key Design Decision

The daemon should be conservative about structural state, but optimistic about source text:

- Source text changes: update Roslyn incrementally.
- Project/solution/config changes: reload or fail closed.
- Unknown state: report uncertainty, do not silently continue.

This gives RoslynSkills the "server keeping a project hot" model while respecting Roslyn's existing strengths instead of duplicating them.

## 2026-05-16 Follow-Up: Exact Edit Refresh

The FrankenTui.NET `.26` supervised trial found a correctness hazard in the intended hot loop:

1. `workspace.preload` loaded the solution into the hot host.
2. `edit.replace_text` wrote a tracked `.cs` file.
3. `ctx.search_text` saw the new disk content.
4. `ctx.member_source` still served the pre-edit source from the hot workspace.

The fix is to make exact write commands update any matching in-process hot workspace immediately after a successful file write. `edit.replace_text` and `edit.insert_text` now include `hot_workspace_refresh` in their response. A successful tracked edit should report `matched_workspace_count > 0` and an incremental `refresh_action`, then the next `ctx.member_source` read should reflect the edited source without a manual `workspace.refresh`.

This is intentionally limited to source-text writes. Structural changes such as project files, solution files, generated source configuration, and package references still need explicit refresh/reload semantics.

Remaining workflow issue: agents currently batch several separate exact edit commands in one shell block, which can hide a failed first edit behind later successes. The next editing ergonomics improvement should be a multi-operation exact-edit command with per-operation results, optional fail-fast behavior, and atomic apply where practical.

## 2026-05-16 Follow-Up: Batch Exact Edits

The next FrankenTui.NET supervised run confirmed that `.27` fixed stale hot context for successful exact edits: `edit.replace_text` routed through the hot daemon, refreshed the matching workspace with `incremental_document_update`, and the next `ctx.member_source` saw the edited source.

The same run also confirmed that prompt guidance alone is insufficient to prevent command batching. The agent still executed two exact edits in one shell block; the first failed while the second succeeded, and the visible summary emphasized the later success. After further exact-match churn, the agent started issuing dummy `old_text` probes, which is a poor workflow.

`edit.batch_exact` addresses that failure class:

- `operations[]` supports `replace_text` and `insert_text`.
- A top-level `file_path` can apply to all operations, or each operation can specify its own file.
- Defaults are `apply=true`, `atomic=true`, `continue_on_error=false`, and `include_diagnostics=true`.
- Each operation reports its own `ok`, `match_count`, and error.
- Atomic mode prevents all writes if any operation fails.
- File results include one diagnostics pass and `hot_workspace_refresh` per changed file.

Use this command when an agent would otherwise chain multiple `edit.replace_text` / `edit.insert_text` calls in one shell block. Keep single exact edits on the simpler commands.

## 2026-05-16 Follow-Up: Span-Based Batch Edits From Member Context

The first `.28` FrankenTui.NET supervised batch run improved failure containment: the agent used `edit.batch_exact`, the edit failed with `old_text_not_found`, and atomic mode wrote no partial changes. The remaining friction was that the agent still copied a whole member body from `ctx.member_source` into `old_text`; terminal wrapping/escaping made the copied text fragile.

The next adjustment is to connect context and editing by span rather than copied text:

- `ctx.member_source` now emits `edit_target` with absolute `span_start`, `span_length`, `span_end`, line/column bounds, and a ready-shaped `replace_span_operation`.
- `edit.batch_exact` now supports `kind=replace_span` with `span_start` plus `span_length` or `span_end`, `new_text`, and optional `expected_text`.
- Agents should prefer `replace_span` for whole-member or large-target replacement after claiming the file/member, and use `expected_text` when they want a lightweight stale-source guard.

This keeps multi-agent safety in the claim layer, preserves atomic multi-file/multi-operation behavior in `edit.batch_exact`, and removes the need to paste large escaped source strings through the shell.

## 2026-05-16 Follow-Up: Trivia-Safe Span Replacement Guidance

The next FrankenTui.NET `.29` round validated the span path: `edit.batch_exact` `replace_span` succeeded repeatedly, refreshed the hot workspace, and returned zero edit diagnostics. The focused test still failed for product/layout reasons, but the tooling failure changed shape. Whole-member span replacement can still cause indentation drift if an agent uses line-based `source.text` as `new_text`, because the syntax-node span excludes leading trivia while the file preserves the indentation before `span_start`.

`ctx.member_source` now makes that contract explicit:

- `edit_target.exact_span_text.text` is the copy-safe replacement base that exactly matches `span_start/span_length`.
- `edit_target.trivia.preserved_line_prefix_text` shows the text the file keeps before `span_start`.
- `edit_target.trivia.new_text_first_line_rule` tells agents whether to omit that preserved prefix from `new_text`.
- `describe-command ctx.member_source` and `describe-command edit.batch_exact` point agents to this path.

This should steer agents away from using line-oriented snippets for span replacement and reduce formatting churn in repeated whole-member edits.

## 2026-05-16 Follow-Up: Bootstrap Guidance Alignment

After releasing `.30`, the command implementation had the correct span/trivia payload but some high-level bootstrap surfaces still showed `ctx.member_source --brief true` as the primary recipe. That is appropriate for reading but incomplete for edit construction.

The `quickstart` and `llmstxt` guidance now include a span-member edit recipe:

- Read context with `ctx.member_source ... --include-edit-target-text true`.
- Claim the target file/member.
- Use `edit.batch_exact` `replace_span` from `edit_target.exact_span_text.text`.
- Validate with file diagnostics and focused build/tests.

This keeps the pit-of-success guidance aligned with the behavior observed in the FrankenTui.NET span trials.

## 2026-05-16 Follow-Up: Cross-Repo Daemon Root Inference

When supervising FrankenTui.NET from the RoslynSkills host pane, `workspace.preload C:\Work\FrankenTui.Net\FrankenTui.Net.sln` initially keyed the daemon to the caller's current repo. That made later `daemon.stop --repo-root C:\Work\FrankenTui.Net` miss the host and risked stale global-tool locks.

`workspace.use` and `workspace.preload` now infer the daemon repo root from the target solution/project path when `--repo-root` is omitted. Explicit `--repo-root` still wins. This keeps cross-repo supervision, alias stores, and daemon lifecycle commands aligned with the workspace being loaded instead of the shell location that launched the command.

## 2026-05-16 Follow-Up: File-Based Hot Command Routing

The next cross-repo smoke exposed the companion routing gap: after `workspace.preload C:\Work\FrankenTui.Net\FrankenTui.Net.sln` correctly started the FrankenTui daemon, `ctx.member_source C:\Work\FrankenTui.Net\...\ShowcaseSurface.cs ...` launched from the RoslynSkills host pane still looked for the RoslynSkills cwd daemon and returned `daemon_unavailable`.

Daemon-capable tool calls now infer the daemon repo root from `workspace_path` or `file_path` in the command input, including nested operation payloads such as `edit.batch_exact.operations[].file_path`. This makes the canonical supervised loop work without changing directories:

1. `roscli workspace.preload C:\Work\Target\Target.sln --alias default`
2. `roscli ctx.member_source C:\Work\Target\src\File.cs 42 17 member --include-edit-target-text true`
3. `roscli edit.batch_exact C:\Work\Target\src\File.cs --operation ...`

When a command has no routeable file or workspace path, agents should still pass a `workspace_handle` or run from the target repo root.

## 2026-05-16 Follow-Up: Prefix-Repair Hint for Trivia Spans

The `.33` FrankenTui.NET repair round showed correct agent behavior but one remaining discoverability gap. The agent first used `ctx.member_source --include-edit-target-text true`, saw the duplicated indentation, and only then reasoned that the default span preserved the bad prefix outside `span_start`. Rerunning with `--include-trivia true` produced a span that could repair the indentation.

`ctx.member_source.Data.edit_target.trivia` now includes `prefix_edit_rule`. For the default no-trivia span, it explicitly says to rerun with `include_trivia=true` when the preserved prefix is the thing being fixed, such as duplicated indentation before a member or attribute. This keeps the normal non-trivia span safe for body edits while making prefix repair discoverable at the edit target.

## 2026-05-16 Follow-Up: Expected Text in Span Edit Templates

The next `.34` live round used `edit.batch_exact replace_span` successfully but still built operations by copying only `span_start` and `span_length`. In a multi-agent or long-running hot-workspace session, that leaves span edits vulnerable to stale coordinates if another change shifts or rewrites the same declaration between read and write.

When `ctx.member_source` is called with `include_edit_target_text=true` and the exact span text is not truncated, `edit_target.replace_span_operation` now includes `expected_text`. Agents should keep that field in the final `edit.batch_exact` payload and change only `new_text`; this turns stale span coordinates into a guarded operation failure instead of an accidental overwrite.

## 2026-05-16 Follow-Up: Claim Status Alias

In the next supervised pass, the agent tried `edit.claim list` even though the supported status operation was `edit.claim status`. That is a pit-of-success issue rather than a meaningful user error: agents commonly use `list` for stateful resources.

`edit.claim list` is now accepted as an alias for `edit.claim status` in both direct CLI shorthand and JSON input. Help text advertises `status|list|claim|release`.

## 2026-05-16 Follow-Up: Filtered File Outlines for Large Test Files

The next supervised pass used `ctx.search_text` well, but reported that `ctx.file_outline` stayed noisy on `ShowcaseShellTests.cs`. Large test fixtures need a way to get line/column anchors for one focused test without returning thousands of unrelated members.

`ctx.file_outline` now accepts `member_name_contains` and `type_name_contains`. For example:

```powershell
roscli ctx.file_outline tests/FrankenTui.Tests.Headless/ShowcaseShellTests.cs --member-name-contains EvidenceLedger --max-members 40
```

The command returns matching member outlines plus the containing type, keeping the response small enough to use directly as input for `ctx.member_source`.

## 2026-05-16 Follow-Up: Focused Windows Inside Huge Members

The next supervised pass avoided text fallback, but `ctx.member_source` still returned too much context for very large members such as long input classifiers. The agent only needed the branch around one literal term, while the edit target still needed to stay anchored to the containing member.

`ctx.member_source` now accepts `focus_text`. When supplied, the command searches the anchored member/body span for the first case-insensitive literal match and returns only the requested line window around that match:

```powershell
roscli ctx.member_source apps/FrankenTui.Demo.Showcase/ShowcaseInteractiveProgram.cs 1200 17 member --focus-text overlay_help_scroll_up --context-lines-before 3 --context-lines-after 8 --max-chars 12000
```

The response includes `source.focus` with matched state and line/column. This keeps Roslyn-native exploration practical for huge members without falling back to `rg`/`Get-Content` just to find the relevant branch.

The follow-up supervised run showed one subtle pitfall: focused exploration still inherited the normal `include_edit_target_text=true` default and could return a truncated whole-member `exact_span_text` for huge members. When `focus_text` is supplied, `include_edit_target_text` now defaults to false. If agents explicitly request target text and it truncates, the command marks it unsafe for whole-target `replace_span`, omits `expected_text` with an explicit reason, and tells agents to use the focused `source.text` for small exact edits or rerun without `focus_text` and with a larger `max_chars` for whole-target replacement.

## 2026-05-16 Follow-Up: Exact Edit Recovery Hints

The `.39` supervised round showed `edit.batch_exact` doing the right safety thing on ambiguous `old_text`: atomic mode wrote nothing. The retry still required agent inference. `edit.batch_exact` now includes `operation_results[].recovery_hint` for ambiguous or missing exact text/anchors, steering agents toward `ctx.member_source` and `replace_span` with `expected_text` when span anchoring is safer than making larger copied `old_text` snippets.

## 2026-05-16 Follow-Up: Focus Match in Member Source Preview

The `.41` supervised round stayed on roscli after a stale line anchor, but the first clue was the returned member name rather than the preview line. `ctx.member_source` CLI previews now include focus state when `focus_text` is supplied, for example `focus=matched:719` or `focus=not-found:OverlayHelpClose`. This makes stale anchors and wrong-member reads visible in the one-line command summary before agents inspect the full JSON.

## 2026-05-16 Follow-Up: Member-Name Anchors

Repeated supervised runs showed line numbers in large test files drifting after each edit. `ctx.file_outline --member-name-contains` gives the right member names, but agents still had to copy the current line/column into `ctx.member_source`. `ctx.member_source` now accepts a unique `member_name` anchor:

```powershell
roscli ctx.member_source tests/FrankenTui.Tests.Headless/ShowcaseShellTests.cs --member-name ShowcaseEvidenceJsonlWriterEmitsStatusUnknownForMismatchedStatusHit --focus-text status_unknown --context-lines-before 12 --context-lines-after 12
```

If the name is ambiguous, the command fails closed and asks for a line/column anchor. This gives agents a stable path from filtered outline to member source when edits keep moving line numbers.

## 2026-05-16 Follow-Up: Member-Scoped Exact Replacement

The `.43` supervised FrankenTui.NET round validated member-name anchors: the agent used `ctx.member_source --member-name ... --focus-text ...` for both test and writer members, and the one-line previews showed `focus=matched` without copying line anchors. The round also exercised `edit.batch_exact` recovery hints: an `old_text_not_found` failure correctly steered the agent back to `ctx.member_source` and then to span replacement.

The remaining friction was the recovery step itself. For a small assertion trim inside a known test member, the agent still had to hand-write PowerShell that re-read the full member JSON, extracted `edit_target`, constructed a new full-member string, and called `replace_span`. That is too much shell glue for a common C# edit and creates avoidable line-ending/escaping churn.

`edit.replace_in_member` now covers that middle path:

```powershell
roscli edit.replace_in_member tests/FrankenTui.Tests.Headless/ShowcaseShellTests.cs --member-name ShowcaseEvidenceJsonlWriterEmitsMouseCaptureToggleEvent --old-text "Assert.Equal(2, value);" --new-text "Assert.Equal(3, value);"
```

The command anchors to a unique `member_name` or line/column, confines exact matching to the selected member/body, tolerates LF snippets against CRLF files, refreshes hot workspaces after writes, and returns diagnostics. `ctx.member_source.edit_workflow` now recommends it for small scoped edits, while keeping `edit.batch_exact replace_span` as the preferred path for whole-member or multi-file guarded edits. The multi-agent rule remains claim-first: acquire `edit.claim` before mutation and release after validation.
