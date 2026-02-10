# RoslynSkill

RoslynSkill is an engineering-first research project that tests whether Roslyn-native tooling materially improves agentic coding workflows in C#/.NET compared with text-first editing.

## Project Intent

- Build production-grade Roslyn command primitives for coding agents.
- Measure end-to-end outcomes using paired control vs Roslyn-enabled runs.
- Optimize for correctness, reliability, and token efficiency with reproducible artifacts.

## Current Status (2026-02-10)

- Active implementation and benchmarking.
- CLI command surface shipped across navigation, context, diagnostics, edits, repair, and live session workflows.
- Paired-run harness supports Codex and Claude runs with control-contamination checks, deterministic post-run constraints, and token/report exports.
- Lightweight real-tool benchmark scaffold is in place and being iterated toward larger, higher-ambiguity tasks.

## Quick Start (Local Repo)

Prerequisites:

- .NET SDK 10.x
- `git`
- `rg` (ripgrep) for benchmark/preflight scripts

Enumerate command surface:

```powershell
dotnet run --project src/RoslynAgent.Cli -- list-commands --ids-only
```

Convenience wrappers from repo root:

```powershell
scripts\roscli.cmd list-commands --ids-only
scripts\roscli.cmd ctx.file_outline src/RoslynAgent.Core/DefaultRegistryFactory.cs
scripts\roscli.cmd nav.find_symbol src/RoslynAgent.Cli/CliApplication.cs HandleRunDirectAsync --brief true --max-results 5
```

## Roslyn Command Surface

Current CLI exposes 32 commands grouped across:

- `nav.*`: semantic symbol/references/overrides/implementations
- `ctx.*`: file/member/call-chain/dependency context extraction
- `diag.*`: file/snapshot/diff/proposed-edit diagnostics
- `edit.*`: structured semantic edits and transactions
- `repair.*`: diagnostics-driven repair planning/application
- `session.*`: immutable in-memory edit sessions with diff/diagnostics/commit

## GitHub Release Artifacts

Release artifacts are produced by:

- local script: `scripts/release/Build-ReleaseArtifacts.ps1`
- GitHub Actions workflow: `.github/workflows/release-artifacts.yml`

For each release version, the pipeline emits:

- `roslyn-agent-bundle-<version>.zip`
  - `cli/` published `RoslynAgent.Cli`
  - `mcp/` published `RoslynAgent.McpServer`
  - `transport/` published `RoslynAgent.TransportServer`
  - `bin/` launchers:
    - `roscli` / `roscli.cmd`
    - `roslyn-mcp` / `roslyn-mcp.cmd`
    - `roslyn-transport` / `roslyn-transport.cmd`
  - `skills/roslyn-agent-research/SKILL.md`
- `RoslynAgent.Cli.<version>.nupkg` (dotnet tool package)
- `roslyn-agent-research-skill-<version>.zip`
- `release-manifest.json`
- `checksums.sha256`

Install from the tool package (local nupkg folder example):

```powershell
dotnet tool install --global RoslynAgent.Cli --version <version> --add-source <release-artifact-folder>
roslyn-agent list-commands --ids-only
```

## Use `roscli` Today

### 1) Codex CLI

1. Download and unzip `roslyn-agent-bundle-<version>.zip`.
2. Use the launcher from the bundle:
   - Windows: `<unzipped>\bin\roscli.cmd`
   - Bash: `<unzipped>/bin/roscli`
3. Run commands:

```powershell
.\bin\roscli.cmd list-commands --ids-only
.\bin\roscli.cmd nav.find_symbol src/RoslynAgent.Cli/CliApplication.cs TryGetCommandAndInputAsync --brief true --max-results 50
```

For high-call sessions, warm and use published-cache mode:

```powershell
scripts\roscli-warm.cmd
$env:ROSCLI_USE_PUBLISHED = "1"
scripts\roscli.cmd system.ping
```

Published mode now defaults to fast-path cache reuse (`ROSCLI_STALE_CHECK=0` unless explicitly enabled).
Use one-call refresh when you know binaries changed:

```powershell
$env:ROSCLI_REFRESH_PUBLISHED = "1"
scripts\roscli.cmd system.ping
$env:ROSCLI_REFRESH_PUBLISHED = "0"
```

Optional development safety mode:

```powershell
$env:ROSCLI_STALE_CHECK = "1"
scripts\roscli.cmd system.ping
```

### 2) Codex App/Web (MCP-capable surfaces)

If your Codex surface supports local MCP servers, configure the release MCP launcher as a stdio server.

Example MCP server config values:

- command: `dotnet`
- args: `["<unzipped>/mcp/RoslynAgent.McpServer.dll"]`

Then use MCP reads/calls against `roslyn://command/...` URIs (same pattern used in benchmark harness MCP lanes).

### 3) Claude Code

You can use either CLI helper commands or MCP mode.

Direct helper usage:

```bash
./bin/roscli list-commands --ids-only
./bin/roscli edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 100
```

For high-call sessions in local shells:

```bash
./scripts/roscli-warm
export ROSCLI_USE_PUBLISHED=1
./scripts/roscli system.ping
```

MCP mode config example (`claude-mcp.json`):

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["<unzipped>/mcp/RoslynAgent.McpServer.dll"],
      "env": {}
    }
  }
}
```

Run Claude with MCP config (example):

```powershell
claude --mcp-config .\claude-mcp.json --strict-mcp-config
```

## What Is A "Skill" Here?

A skill is an instruction package for coding agents, stored as `SKILL.md` plus optional scripts/assets.

In this repo:

- skill name: `roslyn-agent-research`
- source: `skills/roslyn-agent-research/SKILL.md`
- purpose: enforce Roslyn-first workflows (semantic nav/context/diag/edit loops, fallback logging, telemetry discipline)

For Codex-style skill loading, the common shape is:

- `<CODEX_HOME>/skills/<skill-name>/SKILL.md`

The release bundle includes this skill for direct reuse.

Install skill from release zip (example):

```powershell
Expand-Archive .\roslyn-agent-research-skill-<version>.zip -DestinationPath "$env:CODEX_HOME\skills\roslyn-agent-research" -Force
```

## Build Release Artifacts Locally

```powershell
powershell -ExecutionPolicy Bypass -File scripts/release/Build-ReleaseArtifacts.ps1 -Version 0.1.0
```

Default output:

- `artifacts/release/0.1.0`

Optional: skip tests in packaging pass:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/release/Build-ReleaseArtifacts.ps1 -Version 0.1.0 -SkipTests
```

## Roscli Load Tuning

New helper launchers:

- `scripts/roscli-warm.cmd`
- `scripts/roscli-warm`

Wrapper performance env vars:

- `ROSCLI_USE_PUBLISHED=1`: run cached published DLL instead of `dotnet run`.
- `ROSCLI_STALE_CHECK=1`: enable automatic stale-check refresh for published cache (default off for latency).
- `ROSCLI_REFRESH_PUBLISHED=1`: force republish on next call.

Load benchmark script:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Measure-RoscliLoadProfiles.ps1
```

Optional profile toggles:

- `-IncludeStaleCheckOnProfiles` to add explicit stale-check-on lanes.
- `-IncludeStaleCheckOffProfiles` to add explicit stale-check-off lanes.

Latest local benchmark artifact:

- `artifacts/roscli-load-profiles/roscli-load-profiles-v9.md`

## Automated GitHub Release

Workflow: `.github/workflows/release-artifacts.yml`

Triggers:

- push tag `v*` (for example `v0.1.0`)
- manual `workflow_dispatch` with `version`

Behavior:

- builds/tests
- produces release artifacts
- uploads workflow artifact bundle
- creates/updates GitHub Release with zips, nupkg, manifest, and checksums

## Repository Guide

- `src/`: RoslynAgent contracts, core commands, CLI, benchmark tooling.
- `tests/`: command/CLI/benchmark validation suites.
- `benchmarks/`: manifests, scripts, prompts, scoring, and report artifacts.
- `skills/roslyn-agent-research/`: installed Roslyn-first operating guidance for coding agents.
- `AGENTS.md`: execution doctrine and meta-learning log.
- `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`: research questions, hypotheses, and evaluation design.
- `.github/workflows/release-artifacts.yml`: CI/CD release packaging and GitHub Release publishing workflow.
- `scripts/release/Build-ReleaseArtifacts.ps1`: local release artifact builder used by CI.

## Validation

Recommended baseline validation:

```powershell
dotnet test RoslynSkill.slnx -c Release
```

## Benchmark Workflow

Primary A/B evaluation workflow:

```powershell
dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-gate --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir> --fail-on-warnings true
```

Quick paired-run harness:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 -OutputRoot <output-dir>
```

Harness outputs include machine-readable summary JSON and markdown summaries with model token totals, cache-inclusive totals, command round-trips, and Roslyn adoption fields.

## License

MIT (`LICENSE`).
