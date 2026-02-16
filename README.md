<p align="center">
  <img src="docs/images/roslynskills-logo.png" alt="RoslynSkills logo" width="128" />
</p>

# RoslynSkills

Roslyn-powered C# tools for coding agents.

Goal: help agents navigate, edit, and validate C# with semantic correctness, then measure whether this materially beats text-first workflows.

Core principle: define a clear pit of success where semantic-first, brief-first, verify-before-finalize is the default path an agent naturally follows.

Pit-of-success guide: `docs/PIT_OF_SUCCESS.md`

## Start Here (5 Minutes)

Use this path first.

Prerequisites:

- .NET 10 SDK
- A C#/.NET repository

Install `roscli` from NuGet preview:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
```

If install fails or NuGet cannot find the package yet, jump to `Troubleshooting`.

Verify install:

```powershell
roscli --version
roscli list-commands --ids-only
roscli list-commands --stable-only --ids-only
roscli quickstart
```

You should see command ids like `nav.find_symbol`, `ctx.member_source`, `ctx.search_text`, `nav.find_invocations`, `nav.call_hierarchy`, `analyze.unused_private_symbols`, `analyze.impact_slice`, `query.batch`, `diag.get_file_diagnostics`, `edit.rename_symbol`, and `session.*`.
You should also get an explicit pit-of-success brief from `roscli quickstart`.

Command maturity model:

- `stable`: default-safe contract for normal agent workflows.
- `advanced`: deeper analysis/orchestration; can be slower or partially heuristic.
- `experimental`: evolving contract; helpful signals with lower stability guarantees.

## Tell Your Agent About `roscli`

Paste this at the start of an agentic coding session:

```text
Use roscli for C# and VB.NET work in this session.
Command: roscli

Workflow:
1) Run "roscli list-commands --ids-only" once.
2) Prefer stable commands first via "roscli list-commands --stable-only --ids-only" when uncertainty is high.
3) Run "roscli quickstart" to load the built-in pit-of-success brief.
4) If command arguments are unclear, run "roscli describe-command <command-id>".
5) Prefer direct command shorthand for common calls; use "run ... --input" for complex JSON payloads.
6) Prefer nav.* / ctx.* / diag.* before text-only fallback.
7) For external package/API questions, use "dnx dotnet-inspect -y -- ..." before editing local code.
8) Keep diagnostics scoped; avoid full-solution snapshots unless needed.
9) For nav/diag file commands, check response "workspace_context.mode". If it is "ad_hoc" for project code, rerun with "--workspace-path <.csproj|.vbproj|.sln|.slnx|dir>" and prefer "--require-workspace true" for fail-closed behavior.
10) Run build/tests before finalizing changes.
11) If roscli cannot answer a C# query, state why before falling back.
```

First useful commands:

```powershell
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20 --require-workspace true
roscli ctx.member_source src/MyProject/File.cs 120 10 body --brief true
roscli ctx.search_text "RemoteUserAction" src --mode literal --max-results 100
roscli nav.find_invocations src/MyProject/File.cs 120 10 --brief true --require-workspace true
roscli nav.call_hierarchy src/MyProject/File.cs 120 10 --direction both --max-depth 2 --brief true --require-workspace true
roscli analyze.unused_private_symbols src --brief true --max-symbols 100
roscli analyze.impact_slice src/MyProject/File.cs 120 10 --brief true --include-callers true --include-callees true
roscli analyze.override_coverage src --coverage-threshold 0.6 --brief true
roscli analyze.async_risk_scan src --max-findings 200 --severity-filter warning --severity-filter info
roscli diag.get_file_diagnostics src/MyProject/File.cs
roscli diag.get_file_diagnostics src/MyProject/File.cs --workspace-path src/MyProject/MyProject.csproj --require-workspace true
roscli edit.create_file src/MyProject/NewType.cs --content "public class NewType { }"
```

Note: `session.open` is for C# source files (`.cs`/`.csx`) only. Do not use `session.open` on `.sln`, `.slnx`, or `.csproj`.
Note: `nav.find_symbol` and `diag.get_file_diagnostics` return `workspace_context` metadata; treat `mode=workspace` as the expected state for project-backed files. Use `--require-workspace true` when ad-hoc fallback should fail closed.
Tip: for simple rename/fix tasks, start with a minimal flow (`edit.rename_symbol` then `diag.get_file_diagnostics`) before broader exploration.
Tip: when you need multiple read-only lookups, use `query.batch` to reduce round-trips.
If command arguments are unclear in-session, run:

```powershell
roscli describe-command session.open
roscli describe-command edit.create_file
```

Deep guidance reference: `docs/PIT_OF_SUCCESS.md`

## What You Get

`roscli` currently exposes 44 commands across:

- `nav.*`: semantic symbol/references/implementations/overrides
- `ctx.*`: file/member/call-chain/dependency context
- `analyze.*`: lightweight static analysis (unused private symbols, dependency violations, impact slices, override coverage, async risk scan)
- `diag.*`: diagnostics snapshots/diffs/after-edit checks
- `edit.*`: structured semantic edits and transactions
- `repair.*`: diagnostics-driven repair planning/application
- `session.*`: non-destructive edit/diagnostics/diff/commit loops for a single C# file (`.cs`/`.csx`)

## Complementary .NET Tooling

For external package/assembly API intelligence, the strongest companion here is `dotnet-inspect` by Rich Lander (and related `dotnet-skills` work), which is designed for ecosystem/package inspection workflows rather than in-repo Roslyn edits.

Attribution and links:

- `dotnet-inspect`: https://github.com/richlander/dotnet-inspect
- `dotnet-skills`: https://github.com/richlander/dotnet-skills

Install companion tool (optional):

```powershell
dotnet tool install --global dotnet-inspect
```

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

If you want prebuilt binaries and wrappers, use the releases page:

- https://github.com/DNAKode/RoslynSkills/releases

Pick the newest `RoslynSkills v*` entry (including preview releases) and download:

Main artifacts:

- `roslynskills-bundle-<version>.zip`
- `DNAKode.RoslynSkills.Cli.<version>.nupkg`
- `roslynskills-research-skill-<version>.zip`
- `roslynskills-tight-skill-<version>.zip`

Bundle contents include:

- `bin/roscli(.cmd)`
- `mcp/RoslynSkills.McpServer.dll`
- `transport/RoslynSkills.TransportServer.dll`
- `PIT_OF_SUCCESS.md`
- `skills/roslynskills-research/SKILL.md`
- `skills/roslynskills-research/references/`
- `skills/roslynskills-tight/SKILL.md`

### Install Skills (Claude.ai / Claude Code)

The `roslynskills-*-skill-<version>.zip` artifacts are uploadable "Claude skills" (a folder with `SKILL.md` and optional `references/`).

- Claude.ai: Settings -> Capabilities -> Skills -> Upload the skill zip.
- Claude Code: unzip the folder into your Claude Code skills directory (see Claude Code docs), then restart Claude Code.

Recommended:

- `roslynskills-tight-skill-<version>.zip`: minimal-call, low-churn guidance.
- `roslynskills-research-skill-<version>.zip`: deeper workflows + progressive disclosure via `references/`.

## Troubleshooting

If `dotnet tool install` cannot find `DNAKode.RoslynSkills.Cli`:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
```

If a just-published preview is still indexing:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --version <version> --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
```

If you downloaded `DNAKode.RoslynSkills.Cli.<version>.nupkg` from GitHub Releases and want local install:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --version <version> --add-source <folder-containing-nupkg> --ignore-failed-sources
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

For Claude users, MCP works best paired with a skill:

- Connect the MCP server (above) so Claude has the tools.
- Install `roslynskills-tight-skill-<version>.zip` so Claude reliably uses the tools with a low-churn, minimal-call workflow.

## For Maintainers

Release/build pipelines:

- `.github/workflows/release-artifacts.yml`
- `.github/workflows/publish-nuget-preview.yml`
- `scripts/release/Build-ReleaseArtifacts.ps1`

Regular preview release process:

1. Run `Publish NuGet Preview` (`.github/workflows/publish-nuget-preview.yml`).
2. Set:
   - `version`: e.g. `0.1.6-preview.6`
   - `run_tests`: `true` (recommended)
   - `publish`: `true` (pushes NuGet package)
   - `publish_release`: `true` (refreshes GitHub Release assets for the same version)
3. The workflow now builds one artifact set and reuses it for both NuGet publish and GitHub Release assets.

Tag-driven release process:

- Pushing a `v*` tag triggers `Release Artifacts` (`.github/workflows/release-artifacts.yml`), which rebuilds and publishes GitHub release assets.

Required secret for NuGet publish workflow:

- `NUGET_API_KEY` (NuGet.org API key with push permission for `DNAKode.RoslynSkills.Cli`)

Local validation baseline:

```powershell
dotnet test RoslynSkills.slnx -c Release
```

Claude skill validation (load + adoption smoke):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/skills/Validate-Skills.ps1
powershell -ExecutionPolicy Bypass -File scripts/skills/SmokeTest-ClaudeSkillLoad.ps1 -SkillName roslynskills-tight

# Produces artifacts/*/skill-trigger-summary.md and transcripts.
powershell -ExecutionPolicy Bypass -Command "& benchmarks/scripts/Test-ClaudeSkillTriggering.ps1 `
  -OutputRoot artifacts/skill-tests/checkpoint-skill-trigger `
  -ClaudeModel sonnet `
  -Replicates 1 `
  -TaskId @('rename-overload-collision-nested-v1') `
  -IncludeExplicitInvocation `
  -ClaudeTimeoutSeconds 180 `
  -IncludeDotnetBuildGate"

# Wider operation scope:
powershell -ExecutionPolicy Bypass -Command "& benchmarks/scripts/Test-ClaudeSkillTriggering.ps1 `
  -OutputRoot artifacts/skill-tests/wide-scope-v1 `
  -ClaudeModel sonnet `
  -Replicates 1 `
  -TaskId @('change-signature-named-args-v1','update-usings-cleanup-v1','add-member-threshold-v1','replace-member-body-guard-v1','create-file-audit-log-v1','rename-multifile-collision-v1') `
  -ClaudeTimeoutSeconds 180 `
  -IncludeDotnetBuildGate"
```

`Test-ClaudeSkillTriggering.ps1` fails closed on Claude authentication/access errors by default to prevent misleading all-zero summaries. Use `-IgnoreAuthenticationError` only for fixture/build validation.

LSP comparator research lane (Claude `csharp-lsp` vs RoslynSkills):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -IncludeMcpTreatment `
  -IncludeClaudeLspTreatment `
  -RoslynGuidanceProfile standard
```

Design notes: `benchmarks/LSP_COMPARATOR_PLAN.md`.

## For Contributors (Developing RoslynSkills)

Dual-lane local launcher flow (dogfooding while evolving roscli):

- Stable lane (pinned published cache): `scripts\roscli-stable.cmd ...`
- Dev lane (current source via `dotnet run`): `scripts\roscli-dev.cmd ...`
- Stable cache location defaults to `artifacts\roscli-stable-cache`.
- Refresh stable cache explicitly when desired:
  - one call: `set ROSCLI_STABLE_REFRESH=1 && scripts\roscli-stable.cmd list-commands --ids-only`
  - then reset/omit `ROSCLI_STABLE_REFRESH`.

Generic wrapper envs now support custom cache roots:

- `ROSCLI_CACHE_DIR` for `scripts/roscli(.cmd)` and `scripts/roscli-warm(.cmd)`.
- Keep `ROSCLI_USE_PUBLISHED`, `ROSCLI_REFRESH_PUBLISHED`, and `ROSCLI_STALE_CHECK` behavior unchanged.

Repository layout:

- `src/`: contracts, core commands, CLI, benchmark tooling
- `tests/`: command/CLI/benchmark test suites
- `benchmarks/`: manifests, scripts, prompts, scoring, reports
- `docs/PIT_OF_SUCCESS.md`: canonical pit-of-success guidance for agents
- `skills/roslynskills-research/`: Roslyn-first operating guidance
- `skills/roslynskills-tight/`: a tighter, low-churn Roslyn usage skill (minimal calls, progressive disclosure)
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

