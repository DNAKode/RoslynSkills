# RoslynSkills

RoslynSkills is a Roslyn-native toolkit for coding agents working on C#/.NET codebases.

Goal: help agents navigate, edit, and validate C# with semantic correctness, then measure whether this materially beats text-first workflows.

## Start Here (5 Minutes)

If you are trying this from Reddit, use this path first.

Prerequisites:

- .NET 10 SDK
- A C#/.NET repository

Install `roscli` from NuGet preview:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
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
2) Prefer nav.* / ctx.* / diag.* before text-only fallback.
3) Keep diagnostics scoped; avoid full-solution snapshots unless needed.
4) Run build/tests before finalizing changes.
5) If roscli cannot answer a C# query, state why before falling back.
```

First useful commands:

```powershell
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20
roscli ctx.member_source src/MyProject/File.cs 120 10 body --brief true
roscli diag.get_file_diagnostics src/MyProject/File.cs
```

## What You Get

`roscli` currently exposes 32 commands across:

- `nav.*`: semantic symbol/references/implementations/overrides
- `ctx.*`: file/member/call-chain/dependency context
- `diag.*`: diagnostics snapshots/diffs/after-edit checks
- `edit.*`: structured semantic edits and transactions
- `repair.*`: diagnostics-driven repair planning/application
- `session.*`: non-destructive edit/diagnostics/diff/commit loops

## Optional: Release Bundle (CLI + MCP + Transport + Skill)

If you want prebuilt binaries and wrappers, use the latest release bundle:

- https://github.com/DNAKode/RoslynSkills/releases/latest

Main artifacts:

- `roslynskills-bundle-<version>.zip`
- `DNAKode.RoslynSkills.Cli.<version>.nupkg`
- `roslyn-agent-research-skill-<version>.zip`

Bundle contents include:

- `bin/roscli(.cmd)`
- `mcp/RoslynSkills.McpServer.dll`
- `transport/RoslynSkills.TransportServer.dll`
- `skills/roslyn-agent-research/SKILL.md`

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

## For Contributors (Developing RoslynSkills)

Repository layout:

- `src/`: contracts, core commands, CLI, benchmark tooling
- `tests/`: command/CLI/benchmark test suites
- `benchmarks/`: manifests, scripts, prompts, scoring, reports
- `skills/roslyn-agent-research/`: Roslyn-first operating guidance
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
