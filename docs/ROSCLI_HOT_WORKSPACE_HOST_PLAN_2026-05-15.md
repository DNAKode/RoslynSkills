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
