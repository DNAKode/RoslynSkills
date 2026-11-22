# RoslynSkills Handover

RoslynSkills (`DNAKode/RoslynSkills`) is an engineering-first research program evaluating whether Roslyn-native tooling materially improves agentic coding for C#/.NET versus text-first workflows.

## Current State (2026-11-22)

### What's implemented

- **`roscli`**: 65 commands across `nav.*`, `ctx.*`, `edit.*`, `diag.*`, `analyze.*`, `repair.*`, `session.*`, `workspace.*`, and `daemon.*` families (51 stable, 11 advanced, 3 experimental). Built on `net10.0` with Roslyn `5.12.0`, MSBuild `18.12.2`, and `Microsoft.Build.Locator` `1.11.2` (supports both .NET 10 and .NET 11 SDK workspaces and source generators).
- **Hot workspace daemon**: `workspace.use <solution.slnx>` starts the daemon, preloads the solution once, and binds the `default` alias. Every read-only semantic command (`nav.*`, `analyze.*`, read-only `ctx.*`/`diag.*`/`repair.propose_*`) plus exact/semantic `edit.*` commands run on the hot workspace (see `docs/architecture.md` for routing, freshness, and `ROSCLI_DAEMON=required` fail-closed behavior).
- **Solution-wide semantic queries**: `nav.go_to_definition` (source and metadata), references (including object creations for types), implementations (identifier coordinates), overrides, invocations (methods, constructors, primary constructors, types, method groups, explicit/target-typed `new`), call hierarchy (following lambdas and field/property method groups), call path, impact slice, and multi-project `diag.get_workspace_snapshot` (per project, summed) span every project in the loaded solution.
- **Restore & unresolved-project reporting**: restore first in a fresh worktree (`dotnet restore`). `workspace.use`/`workspace.preload` auto-restores projects missing `project.assets.json` or metadata references by default (`ROSLYNSKILLS_AUTO_RESTORE`), reports every remaining unresolved project in `unresolved_projects` and `workspace_diagnostics`, and marks `workspace_context.project_resolved: false` on queries against unresolved projects.
- **Edit & coordination primitives**: `edit.rename_symbol`, `edit.change_signature`, `edit.transaction` (canonical multi-file atomic hot-workspace transaction supporting `replace_text`, `insert_text`, `replace_span` [1-based line/column or 0-based offset], and `set_content` with unified solution diagnostics; `edit.batch_exact` is its thin alias), `edit.replace_in_member`, `edit.replace_text`, `edit.insert_text`, `edit.claim`, `edit.create_file`. Shared repository-root discovery (`RepositoryRootDiscovery`) treats Git worktree `.git` files the same as `.git` directories across daemon routing, `edit.claim`, and `claim_status`.
- **Transport modes**: CLI process-per-call, published-DLL cache, hot workspace daemon, MCP server (stdio JSON-RPC), transport server.
- **Supervised onboarding**: `agent-start`, `agent-begin`, and `csharp-start` for fresh agent sessions.
- **Benchmark harness**: Paired-run A/B trials, OSS pilot runs, Claude skill-trigger studies, tool-thinking split-lane analysis.
- **XmlSkills**: In-repo XML/XAML CLI (`xmlcli`) with validate/outline/find/replace commands.

### Active defaults

- Restore first (`dotnet restore`), then bind the solution via `roscli workspace.use <solution.sln|.slnx> --require-solution true`.
- Set `ROSCLI_DAEMON=required` when silent in-process solution reloads are unacceptable.
- Exact-text/span transactions (`edit.transaction`, with thin alias `edit.batch_exact`, plus `edit.replace_in_member`) and semantic edits (`edit.rename_symbol`, `edit.change_signature`) are the primary mutation surface; `edit.claim` coordinates multi-agent ownership.
- `ctx.changed_files` for dirty-worktree orientation; `ctx.member_source` with `focus_text` for large-member exploration.

### Key unresolved

- Roslyn token/latency overhead vs text-first on microtasks — semantic tooling adds cost when ambiguity is low.
- LSP comparator lane remains confounded by timeout/indexing instability.
- Open-ended large-task comparisons confounded by trajectory variance; bounded-run protocol needed.

## Session-Start Quick Reference

```powershell
# Verify tooling
roscli --version
roscli list-commands --ids-only                    # 65 commands

# Run tests
dotnet test RoslynSkills.slnx -c Release

# Local dev wrappers
scripts\roscli.cmd list-commands --ids-only        # dev lane (dotnet run)
scripts\roscli-stable.cmd list-commands --ids-only  # published cache lane
```

## Primary Harness Commands

```powershell
# Paired control vs treatment A/B runs (microtasks)
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -TaskShape project `
  -RoslynGuidanceProfile brief-first `
  -CodexModel gpt-5.3-codex-spark `
  -CodexReasoningEffort low `
  -FailOnMissingTreatmentRoslynUsage `
  -SkipClaude

# OSS realism runs
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-OssCsharpPilotRealRuns.ps1

# Claude skill trigger/adoption studies
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Test-ClaudeSkillTriggering.ps1

# Tool-call performance benchmarks
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Benchmark-ToolCallPerf.ps1
```

## Docs Index

| File | Purpose |
|---|---|
| `README.md` | Project overview, 5-minute start, best-results playbook |
| `AGENTS.md` | Execution doctrine, heuristics, compact meta-learning log |
| `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md` | Research design, RQs, hypotheses, evaluation |
| `HANDOVER.md` | This file — session-start reference |
| `docs/architecture.md` | Implemented architecture (command families, transport, workspace, edits) |
| `docs/PIT_OF_SUCCESS.md` | Canonical pit-of-success guidance |
| `docs/research-findings.md` | Empirical findings log with artifact references |
| `docs/META_LEARNING_ARCHIVE.md` | Historical meta-learning (Feb 2026) |
| `docs/archive/` | Initial planning docs (design spec, research plan, implementation plan) |
