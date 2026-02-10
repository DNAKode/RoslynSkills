# RoslynSkills

RoslynSkills is an engineering-first research project that tests whether Roslyn-native tooling materially improves agentic coding workflows in C#/.NET compared with text-first editing.

## Project Intent

- Build production-grade Roslyn command primitives for coding agents.
- Measure end-to-end outcomes using paired control vs Roslyn-enabled runs.
- Optimize for correctness, reliability, and token efficiency with reproducible artifacts.

## Current Status (2026-02-10)

- Active implementation and benchmarking.
- CLI command surface shipped across navigation, context, diagnostics, edits, repair, and live session workflows.
- Paired-run harness supports Codex and Claude runs with control-contamination checks, deterministic post-run constraints, and token/report exports.
- Lightweight real-tool benchmark scaffold is in place and being iterated toward larger, higher-ambiguity tasks.

## Install `roscli` (Global Tool, Recommended)

This is the primary path for users who want to try the tool in their own repositories.

Install from NuGet (preview/stable feed):

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
roscli list-commands --ids-only
```

Update later:

```powershell
dotnet tool update --global DNAKode.RoslynSkills.Cli --prerelease
```

If the package version you want is not on NuGet yet, install from release `.nupkg`:

1. Download `DNAKode.RoslynSkills.Cli.<version>.nupkg` from `https://github.com/DNAKode/RoslynSkills/releases/latest`.
2. Run:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --version <version> --add-source <folder-containing-nupkg>
roscli list-commands --ids-only
```

### Tell Your Agent About `roscli` (Copy/Paste)

Use this at the start of an agentic coding session:

```text
Use roscli for C# work in this session.
Command: roscli

Workflow:
1) Run "roscli list-commands --ids-only" once.
2) Prefer nav.* / ctx.* / diag.* before text-only fallback.
3) Keep diagnostics scoped; avoid full-solution snapshots unless needed.
4) Run build/tests before finalizing changes.
5) If roscli cannot answer a C# query, state why before falling back.
```

Example commands:

```powershell
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20
roscli diag.get_file_diagnostics src/MyProject/File.cs
```

## Optional: Release Bundle (`roscli`, MCP, transport, skill)

Use `roslynskills-bundle-<version>.zip` if you specifically want:

- `roscli`/`roscli.cmd` launchers,
- bundled MCP and transport server binaries,
- bundled `skills/roslyn-agent-research/SKILL.md`.

Bundle download:

- `https://github.com/DNAKode/RoslynSkills/releases/latest`

### MCP Mode (Optional / Experimental)

MCP support is available, but current benchmark evidence is mixed and not yet a default-recommended path over direct CLI tool usage.

If your client supports local MCP servers, point it at:

- command: `dotnet`
- args: `["<unzipped>/mcp/RoslynAgent.McpServer.dll"]`

Example (`claude-mcp.json`):

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

## GitHub Release Artifacts

Release artifacts are produced by:

- local script: `scripts/release/Build-ReleaseArtifacts.ps1`
- GitHub Actions workflow: `.github/workflows/release-artifacts.yml`

For each release version, the pipeline emits:

- `roslynskills-bundle-<version>.zip`
  - `cli/` published `DNAKode.RoslynSkills.Cli`
  - `mcp/` published `RoslynAgent.McpServer`
  - `transport/` published `RoslynAgent.TransportServer`
  - `bin/` launchers:
    - `roscli` / `roscli.cmd`
    - `roslyn-mcp` / `roslyn-mcp.cmd`
    - `roslyn-transport` / `roslyn-transport.cmd`
  - `skills/roslyn-agent-research/SKILL.md`
- `DNAKode.RoslynSkills.Cli.<version>.nupkg` (dotnet tool package)
- `roslyn-agent-research-skill-<version>.zip`
- `release-manifest.json`
- `checksums.sha256`

## Roslyn Command Surface

Current CLI exposes 32 commands grouped across:

- `nav.*`: semantic symbol/references/overrides/implementations
- `ctx.*`: file/member/call-chain/dependency context extraction
- `diag.*`: file/snapshot/diff/proposed-edit diagnostics
- `edit.*`: structured semantic edits and transactions
- `repair.*`: diagnostics-driven repair planning/application
- `session.*`: immutable in-memory edit sessions with diff/diagnostics/commit

## Quick Start (Local Repo Development)

Use this only when you are developing RoslynSkills itself.

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

## Publish NuGet Preview (Maintainers)

Workflow: `.github/workflows/publish-nuget-preview.yml`

Required repository secret:

- `NUGET_API_KEY` (NuGet.org API key with push permission for `DNAKode.RoslynSkills.Cli`)

Run via `workflow_dispatch` with inputs:

- `version`: for example `0.1.1-preview.1`
- `run_tests`: `true` (recommended)
- `publish`: `true` to push to NuGet.org, `false` for dry-run pack artifact only

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

