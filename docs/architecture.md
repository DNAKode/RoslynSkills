# RoslynSkills Architecture

Date: 2026-05-24
Status: Reflects current implementation (supersedes the Feb 2026 planning docs archived in `docs/archive/`).

## Overview

RoslynSkills provides Roslyn-native CLI (`roscli`) and MCP tooling for coding agents working on C#/.NET repositories. It replaces fragile text-only edits with semantic symbol targeting, structured edits, and workspace-aware diagnostics.

## Component Map

### `src/RoslynSkills.Core`
Roslyn workspace management, command implementations, and the command registry.

- **Workspace binding**: `WorkspaceSemanticLoader` handles solution/project discovery, MSBuildLocator registration (prefers .NET SDK MSBuild), and ad-hoc/workspace mode fallback.
- **Commands**: ~64 commands across families (`nav.*`, `ctx.*`, `edit.*`, `diag.*`, `analyze.*`, `repair.*`, `session.*`, `workspace.*`, plus `agent-begin`/`csharp-start`).
- **Registry**: `DefaultRegistryFactory` wires commands with input/output schemas, maturity tags, and pit-of-success hints.

### `src/RoslynSkills.Cli`
Cross-platform CLI host. Shell-agnostic launchers in `scripts/roscli(.cmd)`.

- **Invocation modes**: `dotnet run` (dev default), published DLL cache (`ROSCLI_USE_PUBLISHED=1` for high-volume agent loops).
- **Input modes**: direct shorthand (positional args), `--input @file.json`, `--input-stdin`.
- **Output**: JSON envelopes with `Ok`, `CommandId`, `Data`, `Preview`, `Summary`, `Errors`.

### `src/RoslynSkills.McpServer`
JSON-RPC MCP server over stdio. Maps the command registry to MCP `tools/list` and `tools/call`. Emits newline-delimited responses; accepts both framed and newline-delimited inbound.

### `src/RoslynSkills.TransportServer`
Persistent transport server for low-latency warm-state tool calls (experimental lane).

### `src/RoslynSkills.WorkspaceHost`
Daemon for hot-workspace reuse. `workspace.preload` loads a solution once; subsequent `ctx.*`, `nav.*`, `edit.*`, `diag.*` commands reuse the in-memory workspace. Eliminates per-call MSBuild workspace bind overhead.

### `src/XmlSkills.*`
In-repo XML/XAML-focused CLI lane (`xmlcli`). Separate product boundary in monorepo.

## Command Families

| Family | Purpose | Maturity |
|---|---|---|
| `nav.*` | Symbol/references/invocations/call-hierarchy navigation | 50 stable |
| `ctx.*` | File outline, member source, text search, changed files, symbol envelope | + 11 advanced |
| `edit.*` | Semantic renames, batch-exact edits, replace-in-member, insert/claim/create | + 3 experimental |
| `diag.*` | File/solution/workspace diagnostics snapshots and diffs | = 64 total |
| `analyze.*` | Control flow, dataflow, async risk, dependency violations, unused symbols | |
| `repair.*` | Diagnostics-driven repair planning and application | |
| `session.*` | Non-destructive .cs/.csx edit loop with diff/commit | |
| `workspace.*` | Hot-workspace preload, status, refresh, lifecycle | |
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

- **Ad-hoc**: Loose file, no project context. `workspace_context.mode=ad_hoc`.
- **Project**: Single `.csproj`/`.vbproj` binding. `mode=workspace`.
- **Solution**: `.sln`/`.slnx` binding, preferred for repo-wide context. Surfaces `workspace_kind`, `project_count`, `document_count`.
- **Hot daemon**: Preloaded solution in a persistent process. `workspace_cache_mode=process_hot`. Aliased as `default` after preload.

Key rules:
- Prefer `.sln`/`.slnx` for repo-wide or hot-host context.
- Use `--require-workspace true` when ad-hoc fallback is unacceptable.
- Verify `workspace_context.mode` on nav/diag file commands.
- `workspace` mode is necessary but not sufficient — validate that diagnostics don't include `CS0518` (missing core reference assemblies).

## Edit Primitives

| Command | Scope | Best for |
|---|---|---|
| `edit.rename_symbol` | Symbol-anchored rename | Single-symbol renames with workspace diagnostics |
| `edit.transaction` | Multi-file span/set-content | Cross-file refactors |
| `edit.batch_exact` | Atomic exact-text replace/insert | Multiple small mutations in one round-trip |
| `edit.replace_in_member` | Scoped to a unique member/body | Small assertion/property edits with claim-first |
| `edit.replace_text` | Exact text replacement | Structured mutation bridge |
| `edit.insert_text` | Anchor-based before/after insertion | Small evidence/assertion additions |
| `edit.claim` | File/member ownership reservation | Multi-agent coordination; claim before mutation |
| `edit.create_file` | One-shot file bootstrap | Avoid text-file creation fallback |

## Multi-Agent Coordination

- `edit.claim claim <file-path>` reserves file ownership.
- `edit.claim release <claim_id>` releases.
- `edit.claim status` (or `list`) shows current claims.
- Mutation commands emit `claim_status` (claimed/unclaimed) in CLI summaries.
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
