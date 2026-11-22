# RoslynSkills Architecture

Date: 2026-11-22
Status: Reflects current implementation (supersedes the Feb 2026 planning docs archived in `docs/archive/`).

## Overview

RoslynSkills provides Roslyn-native CLI (`roscli`) and MCP tooling for coding agents working on C#/.NET and VB.NET repositories. It targets `net10.0` using Roslyn `5.12.0` (`Microsoft.CodeAnalysis.*`), MSBuild `18.12.2` (`Microsoft.Build.*` compile-only via `ExcludeAssets="runtime"`), and `Microsoft.Build.Locator` `1.11.2`, enabling full semantic compilation and source-generator execution against both .NET 10 and .NET 11 SDK workspaces.

## Component Map

### `src/RoslynSkills.Core`
Roslyn workspace management, command implementations, and the command registry.

- **Workspace binding & resolution**: `WorkspaceSemanticLoader` and `StaticAnalysisWorkspace` handle solution/project discovery, `MSBuildLocator` registration (preferring the latest installed .NET SDK MSBuild), auto-restore (`WorkspaceAutoRestore`), and project resolution checks (`WorkspaceProjectResolution`).
- **Commands**: 65 commands across families (`nav.*`, `ctx.*`, `edit.*`, `diag.*`, `analyze.*`, `repair.*`, `session.*`, `workspace.*`, `daemon.*`, plus `agent-begin`/`csharp-start`/`text.measure`).
- **Registry**: `DefaultRegistryFactory` wires commands with descriptors (including `RunsOnHotWorkspace`, maturity tags, and traits).

### `src/RoslynSkills.Cli`
Cross-platform CLI host. Shell-agnostic launchers in `scripts/roscli(.cmd)`.

- **Daemon routing**: Routes hot-capable commands to `RoslynSkills.WorkspaceHost` automatically when an alias is bound (`ROSCLI_DAEMON=auto` by default). Setting `ROSCLI_DAEMON=required` fails closed (`hot_workspace_unsupported` or `daemon_unavailable`) instead of reloading the solution in-process. `--no-daemon` or `ROSCLI_DAEMON=off` forces the in-process path.
- **Invocation modes**: `dotnet run` (dev default), published DLL cache (`ROSCLI_USE_PUBLISHED=1` for high-volume agent loops), or installed global tool (`roscli`).
- **Input modes**: direct shorthand (positional args), `--input @file.json`, `--input-stdin`.
- **Output**: JSON envelopes with `Ok`, `CommandId`, `Data`, `Preview`, `Summary`, `Errors`.

### `src/RoslynSkills.McpServer`
JSON-RPC MCP server over stdio. Maps the command registry to MCP `tools/list` and `tools/call`. Emits newline-delimited responses; accepts both framed and newline-delimited inbound.

### `src/RoslynSkills.TransportServer`
Persistent transport server for low-latency warm-state tool calls (experimental lane).

### `src/RoslynSkills.WorkspaceHost`
Daemon for hot-workspace reuse over named pipes (Windows) or Unix domain sockets.

- **Command coverage**: `workspace.use` / `workspace.preload` loads a solution once and binds an alias (default: `default`). Every command whose descriptor sets `RunsOnHotWorkspace` — all `nav.*` and `analyze.*` commands, all read-only `ctx.*`/`diag.*`/`repair.propose_*` semantic commands, and exact/semantic `edit.*` commands — runs directly against the in-memory solution without re-running MSBuild.
- **Freshness before each routed call**: Before executing a routed command, `HotWorkspaceFreshness` checks disk timestamps and applies `refresh_policy` (`auto` default incrementally updates modified source documents in the in-memory Roslyn solution; `strict` also reloads on structural/project changes; `none` skips refresh). Structured hot edits (`edit.rename_symbol`, `edit.change_signature`) refresh modified files in the host immediately after writing.
- **Solution-wide semantic searches**: Navigation and hierarchy queries (`nav.go_to_definition`, `nav.find_references`, `nav.find_implementations`, `nav.find_overrides`, `nav.find_invocations`, `nav.call_hierarchy`, `nav.call_path`, `analyze.impact_slice`) span every project in the loaded solution. Type reference and invocation queries include explicit and target-typed `new(...)` object creations and primary constructors; call hierarchy follows lambdas (attributing calls to the enclosing member) and method-group initializers; `nav.find_implementations`/`nav.find_overrides` return declaration identifier coordinates; `diag.get_workspace_snapshot` covers every project under a directory or solution (per project, summed).

### `src/XmlSkills.*`
In-repo XML/XAML-focused CLI lane (`xmlcli`). Separate product boundary in monorepo.

## Command Families

| Family | Purpose | Maturity |
|---|---|---|
| `nav.*` | Solution-wide go-to-definition, symbol, references, implementations, overrides, invocations, call hierarchy/path | 51 stable |
| `ctx.*` | File outline, member source, text search, changed files, symbol envelope | + 11 advanced |
| `edit.*` | Semantic renames/signature changes, batch-exact edits, replace-in-member, insert/claim/create | + 3 experimental |
| `diag.*` | File, multi-project workspace/directory, and solution diagnostics snapshots and diffs | = 65 total |
| `analyze.*` | Control flow, dataflow, impact slice, async risk, dependency violations, unused symbols | |
| `repair.*` | Diagnostics-driven repair planning and application | |
| `session.*` | Non-destructive .cs/.csx edit loop with diff/commit | |
| `workspace.*` / `daemon.*` | Hot-workspace bind (`workspace.use`), preload, status, refresh, lifecycle | |
| `agent-begin` / `csharp-start` | Supervised first-command onboarding for fresh agent sessions | |

## Transport Modes

| Mode | Use case | Latency profile |
|---|---|---|
| `dotnet run` | Dev correctness | ~2.9s startup |
| Published cache (`ROSCLI_USE_PUBLISHED=1`) | Agent loops | ~150ms startup |
| MCP server (stdio JSON-RPC) | Claude/Codex MCP integration | Warm sub-ms steady-state |
| Transport server | Experimental warm-state | Cold spikes, sub-5ms steady |
| Hot workspace daemon | Solution reuse across calls | Eliminates per-call MSBuild bind |

## Workspace Model

- **Ad-hoc**: Loose file, no project context (`workspace_context.mode=ad_hoc`).
- **Project**: Single `.csproj`/`.vbproj` binding (`mode=workspace`).
- **Solution**: `.sln`/`.slnx` binding, preferred for repo-wide context. Surfaces `workspace_kind`, `project_count`, `document_count`.
- **Hot daemon**: Preloaded solution in a persistent process (`workspace_cache_mode=process_hot`), aliased as `default` via `workspace.use`.

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
- `docs/META_LEARNING_ARCHIVE.md` — historical meta-learning entries (2026-02-08 through 2026-02-25).
- `docs/research-findings.md` — empirical findings log with artifact references.
- `docs/archive/` — initial planning docs (design spec, research plan, implementation plan).
