# RoslynSkills Architecture

Date: 2026-11-22
Status: Reflects current implementation (supersedes the Feb 2026 planning docs archived in `docs/archive/`).

## Overview

RoslynSkills provides Roslyn-native CLI (`roscli`) and MCP tooling for coding agents working on C#/.NET and VB.NET repositories. It targets `net10.0` using Roslyn `5.12.0` (`Microsoft.CodeAnalysis.*`), MSBuild `18.12.2` (`Microsoft.Build.*` compile-only via `ExcludeAssets="runtime"`), and `Microsoft.Build.Locator` `1.11.2`, enabling full semantic compilation and source-generator execution against both .NET 10 and .NET 11 SDK workspaces.

## Component Map

### `src/RoslynSkills.Core`
Roslyn workspace management, command implementations, and the command registry.

- **Workspace binding & resolution**: `WorkspaceSemanticLoader` and `StaticAnalysisWorkspace` handle solution/project discovery, `MSBuildLocator` registration (preferring the latest installed .NET SDK MSBuild), auto-restore (`WorkspaceAutoRestore`), and project resolution checks (`WorkspaceProjectResolution`).
- **Commands**: 68 discoverable commands (59 core registry commands across `nav.*`, `ctx.*`, `edit.*`, `diag.*`, `analyze.*`, `repair.*`, `session.*`, `workspace.*`, plus 9 CLI-only lifecycle/onboarding verbs: `agent-begin`, `csharp-start`, `text.measure`, `daemon.*`).
- **Registry**: `DefaultRegistryFactory` wires commands with descriptors (including `RunsOnHotWorkspace`, maturity tags, and traits).

### `src/RoslynSkills.Cli`
Cross-platform CLI host. Shell-agnostic launchers in `scripts/roscli(.cmd)`.

- **Daemon routing**: Routes hot-capable commands to `RoslynSkills.WorkspaceHost` automatically when an alias is bound (`ROSCLI_DAEMON=auto` by default). Setting `ROSCLI_DAEMON=required` fails closed (`hot_workspace_unsupported` or `daemon_unavailable`) instead of reloading the solution in-process. `--no-daemon` or `ROSCLI_DAEMON=off` forces the in-process path.
- **Invocation modes**: `dotnet run` (dev default), published DLL cache (`ROSCLI_USE_PUBLISHED=1` for high-volume agent loops), or installed global tool (`roscli`).
- **Input modes**: direct shorthand (positional args), `--input @file.json`, `--input-stdin`.
- **Output**: Unified flat JSON `CommandEnvelope` with `Ok`, `CommandId`, `Version`, `Preview`, `Data`, `Telemetry`, `Transport`, `Errors`, `TraceId`, `Summary`. The command's own payload always lives directly in top-level `Data` whether executed in-process or via the daemon; `Transport` reports the execution path (`mode`: `"in_process"` or `"daemon"`, plus `routed_alias`, `workspace_handle`, `cache_mode`, `refresh_policy`, `refresh_action`, `daemon_endpoint`, `timings`, and `workspace` metadata).

### `src/RoslynSkills.McpServer`
JSON-RPC MCP server over stdio. Maps the command registry to MCP `tools/list`, `tools/call`, and `roslyn://command/...` resources, emitting the same flat `CommandEnvelope` directly with `Transport.mode = "mcp"`. Emits newline-delimited responses; accepts both framed and newline-delimited inbound. Hot-workspace commands (`RunsOnHotWorkspace`) are routed through `CliApplication` (`HotCommandRouter`, the same path as `roscli run <command> --input <json>`), so with a bound daemon they take the warm pipe route (no per-call solution load); the envelope keeps the daemon endpoint, handle and timings with `mode` relabeled `mcp`. `ROSCLI_DAEMON=off` or `ROSLYNSKILLS_MCP_ROUTING=off` keeps in-process execution.

### `src/RoslynSkills.TransportServer`
Persistent transport server for low-latency warm-state tool calls (experimental lane), returning the same flat `CommandEnvelope` directly with `Transport.mode = "transport_server"`.

### `src/RoslynSkills.WorkspaceHost`
Daemon for hot-workspace reuse over named pipes (Windows) or Unix domain sockets (protocol `2.0`, flat response payload without nested `envelope`).

- **Command coverage**: `workspace.use` / `workspace.preload` loads a solution once and binds an alias (default: `default`). Every command whose descriptor sets `RunsOnHotWorkspace` — all `nav.*` and `analyze.*` commands, all read-only `ctx.*`/`diag.*` semantic commands, `repair.propose_from_diagnostics`/`repair.apply_plan`, and exact/semantic `edit.*` commands (including `edit.apply_code_fix`) — runs directly against the in-memory solution without re-running MSBuild.
- **Freshness before each routed call**: Before executing a routed command, `HotWorkspaceFreshness` reconciles the in-memory Roslyn solution against disk according to `refresh_policy` (`auto` default incrementally updates edited source files, adds new source files matching each loaded project's MSBuild include/exclude rules via `ProjectSourceRules`, removes deleted source files, and ignores non-source, output (`bin/`/`obj/`), directory, and `WatcherError` noise; `strict` also reloads when project or solution files change; `none` skips refresh). Structured hot edits (`edit.rename_symbol`, `edit.change_signature`, `edit.apply_code_fix`, `edit.transaction`, `edit.create_file`) refresh modified files in the host immediately after writing.
- **Zero-stat freshness fast path** (`watcher_fast_path`, reported as `Transport.timings.host_freshness_mode` with `host_freshness_ms`/`host_execute_ms`): the pre-call check no longer stats every tracked file or walks every project directory. `WorkspaceChangeTracker` proves the `FileSystemWatcher` is complete: an *uncertainty generation* moves on watcher errors/overflow and on directory create/rename/delete, a clean *full reconciliation* stamps the generation it saw, and a *watcher barrier* (a uniquely named marker file written into a loaded project's `obj`/`bin` folder; its creation event arrives after every earlier edit because the watcher delivers in order) makes the answer immediate rather than eventually consistent. Pending watcher changes are then the whole truth (tracker-only status); a refresh clears only changes observed before it started. The host falls back to the full scan (`full_scan`) for `refresh_policy: strict`, a moved generation, a barrier timeout (250 ms), or a reconciliation older than the 15 s safety interval (`ROSLYNSKILLS_FRESHNESS_SAFETY_SWEEP_SECONDS`); `ROSLYNSKILLS_FRESHNESS=sweep` restores the sweep on every call.
- **Hot text index and identifier prefilter**: `HotTextIndex` keeps the lines of every `.cs`/`.csx` file under the workspace root (excluding `bin/`, `obj/`, `.vs`, `node_modules`, generated files), updated from the same watcher feed, so `ctx.search_text` over a bound workspace (descriptor `RunsOnHotWorkspace` + `HotWorkspaceOptional`; it still runs in-process, also under `ROSCLI_DAEMON=required`, when no daemon is bound) reads memory instead of walking and reading the disk, with `Directory.EnumerateFiles` ordering and a disk fallback for non-C# globs, `include_generated`, and roots outside the workspace (`analysis_scope.text_source`: `hot_text_index` or `disk`). `IdentifierIndex` caches an identifier set per syntax tree (keyed on the tree object, so edits invalidate by construction) and replaces the per-call `SourceText.ToString().Contains` scan that `nav.find_invocations`/`analyze.impact_slice` used as their candidate-document filter.
- **`cli/run` bridge**: protocol method `cli/run` (`input.args`, `input.cwd`, `input.env`) runs a whole `roscli` argument vector inside the host (`CliBridge` loads `RoslynSkills.Cli.dll` from the host's folder) and returns the exact stdout, so a thin native client (`src/RoslynSkills.FastClient`, `roscli-fast`) skips the .NET startup of the shell CLI. Only hot-workspace registry commands without stdin/`@file` input are eligible; everything else answers `cli_fallback` and the client runs the full CLI.
- **Symbol-name index and selective call sites (Pass 3)**: `--symbol Type.Member` resolves through `SymbolNameIndex`, a per-`Compilation` dictionary (simple name to source types/members, including synthesized positional-record properties, constructors and explicit interface implementations) kept in a `ConditionalWeakTable` and built during warm-up, instead of scanning `SymbolFinder.FindDeclarationsAsync` per project; an edit gives the changed project (and its dependents) a new compilation and therefore a fresh index, untouched projects keep theirs. `nav.find_invocations` and `nav.call_hierarchy` describe their targets as a `CallSiteLookup` (spelled method names; constructor type names plus type aliases resolved to a fixpoint across the compilations in scope; target-typed `new(...)` and `: base(...)`/`: this(...)` initializers; names the compiler calls without spelling them, such as `Add`/`GetEnumerator`/`Deconstruct`/query operators, and non-ordinary targets never narrow). A warm tree answers from its by-callee-name cache; a cold tree is narrowed by `IdentifierIndex` text and bound only at the matching syntax nodes (`CallSiteAnalysis.GetCallSitesForLookup`), never the whole tree. `nav.call_hierarchy` discovers edges lazily per BFS frontier node (incoming: candidate trees by name; outgoing: the declaring trees), and an interface implementation's incoming callers include calls through the interface members it implements (`edges[].implemented_member` names the interface member; `854z`).
- **Solution-wide semantic searches**: Navigation and hierarchy queries (`nav.go_to_definition`, `nav.find_references`, `nav.find_implementations`, `nav.find_overrides`, `nav.find_invocations`, `nav.call_hierarchy`, `nav.call_path`, `analyze.impact_slice`) span every project in the loaded solution. Type reference and invocation queries include explicit and target-typed `new(...)` object creations and primary constructors; call hierarchy follows lambdas (attributing calls to the enclosing member) and method-group initializers; `nav.find_implementations`/`nav.find_overrides` return declaration identifier coordinates; `diag.get_workspace_snapshot` covers every project under a directory or solution (per project, summed).

### `src/XmlSkills.*`
In-repo XML/XAML-focused CLI lane (`xmlcli`). Separate product boundary in monorepo.

## Command Families

| Family | Purpose | Maturity |
|---|---|---|
| `nav.*` | Solution-wide go-to-definition, symbol, references, implementations, overrides, invocations, call hierarchy/path | 54 stable |
| `ctx.*` | File outline, member source, text search, changed files, symbol envelope | + 11 advanced |
| `edit.*` | Semantic renames/signature changes, Roslyn code fixes (`edit.apply_code_fix`), batch-exact edits, replace-in-member, insert/claim/create | + 3 experimental |
| `diag.*` | File, multi-project workspace/directory, and solution diagnostics snapshots and diffs | = 68 total |
| `analyze.*` | Control flow, dataflow, impact slice, async risk, dependency violations, unused symbols | |
| `repair.*` | Hot-workspace Roslyn code-fix planning (`repair.propose_from_diagnostics`) and execution (`repair.apply_plan`), never TODO stubs | |
| `session.*` | Non-destructive .cs/.csx edit loop with diff/commit | |
| `workspace.*` / `daemon.*` | Hot-workspace bind (`workspace.use`), preload, status, refresh, lifecycle | |
| `agent-begin` / `csharp-start` | Supervised first-command onboarding for fresh agent sessions | |

## Transport Modes

| Mode | Use case | Latency profile |
|---|---|---|
| `roscli-fast` + Daemon (`cli/run`) | High-frequency agent loops (NativeAOT client) | ~50–75 ms steady state |
| `dotnet run` | Dev correctness | ~2.9s startup |
| Published cache (`ROSCLI_USE_PUBLISHED=1`) | Agent loops without AOT client | ~150ms startup (~365–465 ms w/ daemon) |
| MCP server (stdio JSON-RPC) | Claude/Codex MCP integration | Warm 15–30 ms (53.85 KB `tools/list` schema tax) |
| Transport server | Experimental warm-state | Cold spikes, sub-5ms steady |
| Hot workspace daemon | Solution reuse across calls | Eliminates per-call MSBuild bind |

## Workspace Model

- **Ad-hoc**: Loose file, no project context (`workspace_context.mode=ad_hoc`).
- **Project**: Single `.csproj`/`.vbproj` binding (`mode=workspace`).
- **Solution**: `.sln`/`.slnx` binding, preferred for repo-wide context. Surfaces `workspace_kind`, `project_count`, `document_count`.
- **Hot daemon**: Preloaded solution in a persistent process (`workspace_cache_mode=process_hot`), aliased as `default` via `workspace.use`. Both daemon manifests (`<hash>.json`) and workspace aliases (`<hash>.workspaces.json`) live under the per-user state folder (`%LOCALAPPDATA%\RoslynSkills\workspace-hosts` on Windows, `$XDG_DATA_HOME/RoslynSkills/workspace-hosts` or `~/.local/share/RoslynSkills/workspace-hosts` on Linux/macOS), leaving no daemon files inside the analysed repository.
- **Concurrent & recoverable host**: `RoslynSkills.WorkspaceHost` dispatches pipe/Unix-socket connections concurrently (serializing MSBuild loads and per-handle mutations internally) so `daemon.status` and `shutdown` stay responsive during full solution reloads, ignores broken-pipe client disconnects, and `daemon.stop`/`daemon.restart` verify and force-kill an unresponsive host process tree before removing its manifest.

Key rules:
- **Restore first in a fresh worktree**: Run `dotnet restore` before loading a solution. When `ROSLYNSKILLS_AUTO_RESTORE` is enabled (the default), workspace load detects projects with zero metadata references or missing `project.assets.json` for `PackageReference`/`PackageDownload` dependencies and runs `dotnet restore` before reloading.
- **Unresolved-project reporting**: Any project that still fails to resolve (missing restore assets, zero metadata references, unresolved `ProjectReference`, or broken `System.Object`) is reported in `unresolved_projects` and as `unresolved_project:` entries in `workspace_diagnostics` on `workspace.use`/`preload`/`status`/`refresh`. Queries against a file in an unresolved project set `workspace_context.project_resolved: false` with `project_resolution_issues`.
- Prefer `.sln`/`.slnx` for repo-wide or hot-host context; use `--require-workspace true` when ad-hoc fallback is unacceptable.
- Use `ROSCLI_DAEMON=required` when a hot workspace is mandatory so unsupported commands or a stopped daemon fail closed instead of reloading in-process.

## Edit Primitives

| Command | Scope | Best for |
|---|---|---|
| `edit.rename_symbol` | Symbol-anchored rename | Solution-wide symbol renames with XML doc crefs and workspace diagnostics |
| `edit.change_signature` | Symbol-anchored signature edit | Solution-wide parameter/name updates and call-site rewrites |
| `edit.apply_code_fix` | Diagnostic-anchored Roslyn fix | Hot-workspace MEF-discovered Roslyn `CodeFixProvider` fixes (add-import, implement interface/abstract class, deterministic compiler fixes) with encoding/line-ending preservation and updated-solution diagnostics |
| `edit.transaction` | Multi-file atomic transaction (`replace_text`, `insert_text`, `replace_span` [1-based line/col or 0-based offset], `set_content`) | Cross-file exact/span/content edits on the hot workspace with unified solution diagnostics and line-ending/BOM preservation |
| `edit.batch_exact` | Thin alias of `edit.transaction` | Delegates directly to `edit.transaction` (identical behavior and schema) |
| `edit.replace_in_member` | Scoped to a unique member/body | Small assertion/property edits with claim-first |
| `edit.replace_text` | Exact text replacement | Single-file exact replacement |
| `edit.insert_text` | Anchor-based before/after insertion | Small evidence/assertion additions |
| `edit.claim` | File/member ownership reservation | Multi-agent coordination; claim before mutation |
| `edit.create_file` | One-shot file bootstrap | Avoid text-file creation fallback; joins project on hot refresh |

## Multi-Agent Coordination

- Shared repository-root discovery (`RepositoryRootDiscovery`) resolves both standard `.git` directories and Git worktree `.git` files (`gitdir:`) consistently across daemon routing, `edit.claim`, `claim_status`, `ctx.changed_files`, and workspace discovery.
- `edit.claim claim <file-path>` reserves file ownership.
- `edit.claim release <claim_id>` releases.
- `edit.claim status` (or `list`) shows current claims.
- Mutation commands (`edit.replace_in_member`, `edit.replace_text`, `edit.insert_text`) emit `claim_status` (claimed/unclaimed) in CLI summaries.
- Disjoint claims required for subagents; shared-file edits serialized through one owner.

## Benchmark Harness

- `benchmarks/scripts/Run-PairedAgentRuns.ps1`: control vs treatment paired runs with guidance profiles, workspace telemetry, A/B integrity gates.
- `benchmarks/scripts/Run-OssCsharpPilotRealRuns.ps1`: large OSS repo realism runs.
- `benchmarks/scripts/Test-ClaudeSkillTriggering.ps1`: Claude skill-adoption studies.
- Shared pipeline: `agent-eval-validate-runs` → `agent-eval-score-runs` → `agent-eval-gate`.

## Distribution

- NuGet: `DNAKode.RoslynSkills.Cli`, `DNAKode.XmlSkills.Cli` (prerelease).
- Release bundles: `roslynskills-bundle-<version>.zip` (CLI + MCP + transport + skills + PIT_OF_SUCCESS.md).
- Skill packs: `roslynskills-tight-skill-<version>.zip`, `roslynskills-research-skill-<version>.zip`.
- CI: `.github/workflows/release-artifacts.yml`, `.github/workflows/publish-nuget-preview.yml`.

## Key Docs

- `README.md` — project overview, 5-minute start, best-results playbook.
- `AGENTS.md` — execution doctrine, applied heuristics, compact meta-learning log.
- `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md` — canonical research design, RQs, hypotheses, evaluation.
- `HANDOVER.md` — session-start reference: current state, harness commands, near-term actions.
- `docs/PIT_OF_SUCCESS.md` — canonical pit-of-success guidance.
- `docs/ROSLYN_HARNESS_INTEGRATION_REPORT.md` — empirical benchmark & architecture study (`0.1.6-preview.31`: CLI+NativeAOT+Daemon vs. LSP vs. MCP vs. `rg`).
- `docs/META_LEARNING_ARCHIVE.md` — historical meta-learning entries (2026-02-08 through 2026-02-25).
- `docs/research-findings.md` — empirical findings log with artifact references.
- `docs/archive/` — initial planning docs (design spec, research plan, implementation plan).
