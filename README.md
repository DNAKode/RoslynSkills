<p align="center">
  <img src="docs/images/roslynskills-logo.png" alt="RoslynSkills logo" width="128" />
</p>

# RoslynSkills

RoslynSkills is a Roslyn-native toolkit for coding agents working on C#/.NET codebases.

Goal: help agents navigate, edit, and validate C# with semantic correctness, then measure whether this materially beats text-first workflows.

## Start Here (5 Minutes)

Use this path first.

Prerequisites:

- .NET 10 SDK
- A C#/.NET repository

Install `roscli` from NuGet preview:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
```

Optional companion tool for external package/API intelligence:

```powershell
dotnet tool install --global dotnet-inspect
```

Verify install:

```powershell
roscli list-commands --ids-only
```

You should see command ids like `nav.find_symbol`, `ctx.member_source`, `diag.get_file_diagnostics`, `edit.rename_symbol`, and `session.*`.

## Tell Your Agent About `roscli`

Paste this at the start of an agentic coding session:

```text
Use roscli for C# work in this session.
Command: roscli

Workflow:
1) Run "roscli list-commands --ids-only" once.
2) If command arguments are unclear, run "roscli describe-command <command-id>".
3) Prefer direct command shorthand for common calls; use "run ... --input" for complex JSON payloads.
4) Prefer nav.* / ctx.* / diag.* before text-only fallback.
5) For external package/API questions, use "dnx dotnet-inspect -y -- ..." before editing local code.
6) Keep diagnostics scoped; avoid full-solution snapshots unless needed.
7) Run build/tests before finalizing changes.
8) If roscli cannot answer a C# query, state why before falling back.
```

First useful commands:

```powershell
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20
roscli ctx.member_source src/MyProject/File.cs 120 10 body --brief true
roscli diag.get_file_diagnostics src/MyProject/File.cs
roscli edit.create_file src/MyProject/NewType.cs --content "public class NewType { }"
```

Note: `session.open` is for C# source files (`.cs`/`.csx`) only. Do not use `session.open` on `.sln`, `.slnx`, or `.csproj`.
Tip: for simple rename/fix tasks, start with a minimal flow (`edit.rename_symbol` then `diag.get_file_diagnostics`) before broader exploration.
If command arguments are unclear in-session, run:

```powershell
roscli describe-command session.open
roscli describe-command edit.create_file
```

## What You Get

`roscli` currently exposes 33 commands across:

- `nav.*`: semantic symbol/references/implementations/overrides
- `ctx.*`: file/member/call-chain/dependency context
- `diag.*`: diagnostics snapshots/diffs/after-edit checks
- `edit.*`: structured semantic edits and transactions
- `repair.*`: diagnostics-driven repair planning/application
- `session.*`: non-destructive edit/diagnostics/diff/commit loops

## Complementary .NET Tooling

RoslynSkills sits well alongside other .NET agent tools:

- `dotnet-inspect`: https://github.com/richlander/dotnet-inspect
- `dotnet-skills`: https://github.com/richlander/dotnet-skills

Practical split of responsibilities:

1. Use `dotnet-inspect` for package/assembly intelligence (external APIs, overload discovery, version diffs, vulnerability metadata).
2. Use `roscli` for workspace-native Roslyn operations (symbol navigation, structured edits, diagnostics, repair) in your current repo.

Quick "which tool" hints:

- "What overloads/members does package X expose?" -> `dotnet-inspect`
- "What changed from package version A to B?" -> `dotnet-inspect`
- "Where is symbol Y used in this repo?" -> `roscli`
- "Rename/update code safely across this workspace" -> `roscli`

Agent session hint block (combined mode):

```text
Use both tools in this session:
- dotnet-inspect for external package/library API intelligence.
- roscli for local workspace semantic navigation, edits, and diagnostics.

Rule of thumb:
1) If the question is about a NuGet/package/framework API, start with:
   dnx dotnet-inspect -y -- <command>
2) If the task is editing/diagnosing this repo, use roscli commands.
3) For migration/refactor tasks, do both: inspect external API first, then edit locally with roscli.
```

Example combined workflow:

```powershell
# External API discovery
dnx dotnet-inspect -y -- api JsonSerializer --package System.Text.Json

# Local workspace edit + verify
roscli edit.rename_symbol src/MyProject/File.cs 42 17 NewName --apply true
roscli diag.get_file_diagnostics src/MyProject/File.cs
```

## Optional: Release Bundle (CLI + MCP + Transport + Skill)

If you want prebuilt binaries and wrappers, use the latest release bundle:

- https://github.com/DNAKode/RoslynSkills/releases/latest

Main artifacts:

- `roslynskills-bundle-<version>.zip`
- `DNAKode.RoslynSkills.Cli.<version>.nupkg`
- `roslynskills-research-skill-<version>.zip`

Bundle contents include:

- `bin/roscli(.cmd)`
- `mcp/RoslynSkills.McpServer.dll`
- `transport/RoslynSkills.TransportServer.dll`
- `skills/roslynskills-research/SKILL.md`

If needed, install directly from downloaded `.nupkg`:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --version <version> --add-source <folder-containing-nupkg>
```

## Optional: MCP Mode

MCP support is available for clients that can run local MCP servers.

Example server wiring:

- command: `dotnet`
- args: `"<unzipped>/mcp/RoslynSkills.McpServer.dll"`

Example `claude-mcp.json`:

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["<unzipped>/mcp/RoslynSkills.McpServer.dll"],
      "env": {}
    }
  }
}
```

## For Maintainers

Release/build pipelines:

- `.github/workflows/release-artifacts.yml`
- `.github/workflows/publish-nuget-preview.yml`
- `scripts/release/Build-ReleaseArtifacts.ps1`

Required secret for NuGet publish workflow:

- `NUGET_API_KEY` (NuGet.org API key with push permission for `DNAKode.RoslynSkills.Cli`)

Local validation baseline:

```powershell
dotnet test RoslynSkills.slnx -c Release
```

LSP comparator research lane (Claude `csharp-lsp` vs RoslynSkills):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -IncludeMcpTreatment `
  -IncludeClaudeLspTreatment `
  -RoslynGuidanceProfile standard
```

Design notes: `benchmarks/LSP_COMPARATOR_PLAN.md`.

## For Contributors (Developing RoslynSkills)

Repository layout:

- `src/`: contracts, core commands, CLI, benchmark tooling
- `tests/`: command/CLI/benchmark test suites
- `benchmarks/`: manifests, scripts, prompts, scoring, reports
- `skills/roslynskills-research/`: Roslyn-first operating guidance
- `AGENTS.md`: execution doctrine and meta-learning log
- `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`: research design and gates

Run CLI directly from source:

```powershell
dotnet run --project src/RoslynSkills.Cli -- list-commands --ids-only
```

Repo-root wrappers:

```powershell
scripts\roscli.cmd list-commands --ids-only
scripts\roscli.cmd ctx.file_outline src/RoslynSkills.Core/DefaultRegistryFactory.cs
```

## License

MIT (`LICENSE`).
