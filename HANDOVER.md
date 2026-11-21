# RoslynSkills Handover

RoslynSkills (`DNAKode/RoslynSkills`) is an engineering-first research program evaluating whether Roslyn-native tooling materially improves agentic coding for C#/.NET versus text-first workflows.

## Current State (2026-05-24)

### What's implemented

- **`roscli`**: 64 commands across `nav.*`, `ctx.*`, `edit.*`, `diag.*`, `analyze.*`, `repair.*`, `session.*`, and `workspace.*` families (50 stable, 11 advanced, 3 experimental).
- **Transport modes**: CLI process-per-call, published-DLL cache, MCP server (stdio JSON-RPC), transport server, hot workspace daemon.
- **Edit primitives**: `edit.rename_symbol`, `edit.transaction`, `edit.batch_exact`, `edit.replace_in_member`, `edit.replace_text`, `edit.insert_text`, `edit.claim`, `edit.create_file`.
- **Multi-agent coordination**: Claim-based file/member ownership with claim-before-mutation workflow.
- **Hot workspace**: Daemon-backed solution preload with `workspace.preload`, aliased as `default` for zero-handle context/editing commands.
- **Supervised onboarding**: `agent-start` and `csharp-start` with two-turn transcript-verification protocol for fresh agent sessions.
- **Benchmark harness**: Paired-run A/B trials, OSS pilot runs, Claude skill-trigger studies, tool-thinking split-lane analysis.
- **XmlSkills**: In-repo XML/XAML CLI (`xmlcli`) with validate/outline/find/replace commands, feature-gated parser backend comparison.

### Active architecture

- Solution workspace binding preferred over project-scoped; hot daemon for repeated calls.
- Exact-text edits (`edit.batch_exact`, `edit.replace_in_member`) are the primary mutation surface for agents.
- `edit.claim` required before mutation in supervised multi-agent runs.
- `agent-start` / `csharp-start` are the recommended fresh-pane onboarding commands.
- `ctx.changed_files` for dirty-worktree orientation; `ctx.member_source` with `focus_text` for large-member exploration.

### Key unresolved

- Roslyn token/latency overhead vs text-first on microtasks — semantic tooling adds cost when ambiguity is low.
- LSP comparator lane remains confounded by timeout/indexing instability.
- Open-ended large-task comparisons confounded by trajectory variance; bounded-run protocol needed.
- Hot workspace daemon reliability under concurrent access.

## Session-Start Quick Reference

```powershell
# Verify tooling
roscli --version
roscli list-commands --ids-only                    # 64 commands

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
