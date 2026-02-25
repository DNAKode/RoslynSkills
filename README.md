<p align="center">
  <img src="docs/images/roslynskills-logo.png" alt="RoslynSkills logo" width="128" />
</p>

# RoslynSkills

`roscli` is a Roslyn-native CLI for coding agents working on C#/.NET repositories.

It is designed to replace fragile text-only edits with semantic symbol targeting, structured edits, and workspace-aware diagnostics.

## Why Roscli

Text-first workflows are fast at first, then degrade on:

- ambiguous symbols and overloads
- cross-file or signature-sensitive refactors
- diagnostics that only make sense in project/solution context

`roscli` focuses directly on those failure modes:

- semantic navigation (`nav.*`)
- structured edits (`edit.*`)
- diagnostics/repair loops (`diag.*`, `repair.*`)
- low-churn calls for agents (`--brief true`, `query.batch`, `nav.find_symbol_batch`)

Canonical pit-of-success guide: `docs/PIT_OF_SUCCESS.md`

## Start in 5 Minutes

Prerequisites:

- .NET 10 SDK
- a C#/.NET repository

Install:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
```

Bootstrap:

```powershell
roscli --version
roscli llmstxt
roscli list-commands --stable-only --ids-only
roscli quickstart
```

First semantic flow:

```powershell
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --first-declaration true --max-results 20 --workspace-path src/MyProject/MyProject.csproj --require-workspace true
roscli edit.rename_symbol src/MyProject/File.cs 42 17 NewName --apply true --workspace-path src/MyProject/MyProject.csproj --require-workspace true
roscli diag.get_file_diagnostics src/MyProject/File.cs --workspace-path src/MyProject/MyProject.csproj --require-workspace true
```

## Best-Results Playbook (For Agents)

Use this sequence to reduce retries and avoid tool-learning churn:

1. `roscli llmstxt` once at session start.
2. `roscli list-commands --stable-only --ids-only`.
3. Use direct commands (`nav.*`, `ctx.*`, `edit.*`, `diag.*`) before exploratory `describe-command`.
4. Keep reads compact (`--brief true`, bounded `--max-results`).
5. For project-backed code, force workspace semantics:
`--workspace-path <.csproj|.sln|.slnx|dir> --require-workspace true`.
6. Batch read-only discovery when possible:
`query.batch`, `nav.find_symbol_batch`.
7. Validate with diagnostics and build/tests before finalizing.

High-call performance mode:

```powershell
scripts\roscli-warm.cmd
$env:ROSCLI_USE_PUBLISHED = "1"
```

After changing RoslynSkills source, refresh published cache once:

```powershell
$env:ROSCLI_REFRESH_PUBLISHED = "1"
```

## Text-First vs Roscli (Real Run Fragments)

These are from recorded paired runs under `artifacts/`.

### Fragment A: roscli can be strictly cheaper

Source: `artifacts/paired-guidance-surgical-codex-smoke-r2/paired-run-summary.md`

- control (text-first): `round-trips=2`, `tokens=36914`, `duration=12.576s`
- treatment (roscli): `round-trips=1`, `tokens=18646`, `duration=11.484s`, Roslyn `1/1`

Result: with a surgical prompt posture, semantic tooling reduced both round-trips and tokens.

### Fragment B: overhead appears when treatment starts with tool-learning loops

Source family: `artifacts/real-agent-runs/*change-signature-named-args-v1*/paired-run-summary.json`

| Profile | Control (rt/tokens/sec) | Treatment (rt/tokens/sec) | Roslyn Calls (ok/attempted) |
| --- | --- | --- | --- |
| `skill-minimal` | `1 / 25267 / 11.168` | `8 / 80681 / 50.986` | `7/7` |
| `surgical` | `2 / 34722 / 18.157` | `4 / 57060 / 35.351` | `1/2` |
| `tool-only-v1` | `3 / 44173 / 20.813` | `5 / 62988 / 37.187` | `3/3` |
| `discovery-lite-v1` (r3) | `2 / 34793 / 16.442` | `4 / 57827 / 33.573` | `3/3` |

Command-sequence fragment from the high-overhead `skill-minimal` treatment:

- `roscli list-commands --ids-only`
- `roscli describe-command nav.find_symbol`
- `roscli describe-command edit.rename_symbol`
- `roscli nav.find_symbol ... Process ...`
- `roscli nav.find_symbol ... left ...`
- `roscli edit.rename_symbol ...`
- `roscli diag.get_file_diagnostics ...`

Interpretation: Roslyn call success alone is not enough. Prompt posture and command-surface ergonomics determine whether semantic tooling is net-positive in end-to-end runs.

Roadmap note:

- TODO: evaluate first-class XAML-aware workflows alongside Roslyn C# semantics, and document recommended mixed-mode handling.

## Agent Intro Prompt (Copy/Paste)

```text
Use roscli for C#/.NET repo edits and diagnostics.
1) Run once: roscli llmstxt
2) Use stable commands first: roscli list-commands --stable-only --ids-only
3) Use describe-command only when argument shape is unclear
4) For project files require workspace semantics:
   --workspace-path <.csproj|.sln|.slnx|dir> --require-workspace true
5) Prefer semantic nav/edit/diag commands before text fallback
6) Keep calls brief/bounded, batch read-only discovery when possible
7) Verify diagnostics/build/tests before final answer
```

## Command Surface

Current catalog snapshot:

- `roscli list-commands --ids-only` -> `47` commands
- maturity counts: `stable=33`, `advanced=11`, `experimental=3`
- `roscli llmstxt` defaults to stable-only startup guidance (`--full` for complete catalog)

Command families:

- `nav.*`: symbol/references/invocations/call-graph/navigation
- `ctx.*`: file/member/search context retrieval
- `analyze.*`: analysis slices and risk scans
- `diag.*`: diagnostics snapshots and diffs
- `edit.*`: structured semantic edits and transactions
- `repair.*`: diagnostics-driven repair planning/application
- `session.*`: non-destructive single-file edit loop (`.cs`/`.csx`)

## XmlSkills Preview (`xmlcli`)

This repo now also includes an XML/XAML-focused CLI lane, `XmlSkills`, with separate projects:

- `src/XmlSkills.Contracts`
- `src/XmlSkills.Core`
- `src/XmlSkills.Cli`
- `tests/XmlSkills.Cli.Tests`

Install preview tool:

```powershell
dotnet tool install --global DNAKode.XmlSkills.Cli --prerelease
```

Startup sequence:

```powershell
xmlcli llmstxt
xmlcli list-commands --ids-only
xmlcli xml.validate_document App.xaml
xmlcli xml.file_outline App.xaml --brief true --max-nodes 120
```

Current stable commands:

- `xml.backend_capabilities`
- `xml.validate_document`
- `xml.file_outline`
- `xml.find_elements`
- `xml.replace_element_text` (dry-run by default, `--apply true` to persist)

Experimental research command:

- `xml.parse_compare` (strict `xdocument` vs tolerant `language_xml` output)

Feature flag for experimental backend mode:

```powershell
$env:XMLCLI_ENABLE_LANGUAGE_XML = "1"
```

Research script:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-XmlParserBackendComparison.ps1 -EnableLanguageXml
```

Guide: `docs/xml/PIT_OF_SUCCESS.md`

## Complementary .NET Tooling

Use `roscli` for in-repo semantic coding tasks.
Use `dotnet-inspect` for external package/framework API intelligence.
Default Rich Lander companion tool for day-to-day .NET API/dependency questions: `dotnet-inspect`.

Attribution:

- `dotnet-inspect`: https://github.com/richlander/dotnet-inspect
- `dotnet-skills`: https://github.com/richlander/dotnet-skills

Note:

- `dotnet-skills` is skill/package distribution around `dotnet-inspect`, not the primary interactive inspection CLI.

Optional install:

```powershell
dotnet tool install --global dotnet-inspect
```

Practical split:

- package/API overload/version questions -> `dotnet-inspect`
- workspace symbol edits/diagnostics -> `roscli`
- migrations -> inspect external API first, then edit with roscli

## Optional: Release Bundle (CLI + MCP + Transport + Skills)

GitHub releases:

- https://github.com/DNAKode/RoslynSkills/releases

Typical artifacts:

- `roslynskills-bundle-<version>.zip`
- `DNAKode.RoslynSkills.Cli.<version>.nupkg`
- `DNAKode.XmlSkills.Cli.<version>.nupkg`
- `roslynskills-research-skill-<version>.zip`
- `roslynskills-tight-skill-<version>.zip`

Bundle includes:

- `bin/roscli(.cmd)`
- `bin/xmlcli(.cmd)`
- `mcp/RoslynSkills.McpServer.dll`
- `transport/RoslynSkills.TransportServer.dll`
- `PIT_OF_SUCCESS.md`
- skills + references

## Install Skills (Claude.ai / Claude Code)

- Claude.ai: Settings -> Capabilities -> Skills -> upload zip
- Claude Code: unzip into skills directory, then restart

Recommended:

- `roslynskills-tight-skill-<version>.zip` for low-churn production loops
- `roslynskills-research-skill-<version>.zip` for deeper guided workflows

## Troubleshooting

If NuGet preview is not visible yet:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
```

If you need an explicit version:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --version <version> --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
```

If installing from downloaded local nupkg:

```powershell
dotnet tool install --global DNAKode.RoslynSkills.Cli --version <version> --add-source <folder-containing-nupkg> --ignore-failed-sources
```

## Optional: MCP Mode

Example MCP server wiring:

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

Best results for Claude: MCP server + `roslynskills-tight` skill.

## Gemini CLI Compatibility (Early Support)

Current status:

- benchmark preflight probes `gemini` plus Windows shims
- recommended startup remains `llmstxt` -> `list-commands` -> targeted commands

Quick check:

```powershell
gemini --version
roscli list-commands --stable-only --ids-only
```

Reference: `docs/ECOSYSTEM_NOTES.md`

## For Maintainers

Release/build pipelines:

- `.github/workflows/release-artifacts.yml`
- `.github/workflows/publish-nuget-preview.yml`
- `scripts/release/Build-ReleaseArtifacts.ps1`

Preview release process:

1. Run `Publish NuGet Preview` workflow.
2. Set:
   - `version`: e.g. `0.1.6-preview.18`
   - `run_tests`: `true`
   - `publish`: `true`
   - `publish_release`: `true`

Tag release process:

- push `v*` tag -> `Release Artifacts` workflow

Required secret:

- `NUGET_API_KEY`

Local baseline validation:

```powershell
dotnet test RoslynSkills.slnx -c Release
```

Claude skill validation scripts:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/skills/Validate-Skills.ps1
powershell -ExecutionPolicy Bypass -File scripts/skills/SmokeTest-ClaudeSkillLoad.ps1 -SkillName roslynskills-tight
```

LSP comparator lane:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 `
  -IncludeMcpTreatment `
  -IncludeClaudeLspTreatment `
  -RoslynGuidanceProfile standard
```

Design notes: `benchmarks/LSP_COMPARATOR_PLAN.md`

## For Contributors (Developing RoslynSkills)

Dual-lane local wrappers:

- stable lane: `scripts\roscli-stable.cmd ...`
- dev lane: `scripts\roscli-dev.cmd ...`
- XML lane: `scripts\xmlcli.cmd ...` (`xmlcli-stable`, `xmlcli-dev`, `xmlcli-warm`)

Stable cache refresh:

```powershell
set ROSCLI_STABLE_REFRESH=1 && scripts\roscli-stable.cmd list-commands --ids-only
```

Wrapper envs:

- `ROSCLI_CACHE_DIR`
- `ROSCLI_USE_PUBLISHED`
- `ROSCLI_REFRESH_PUBLISHED`
- `ROSCLI_STALE_CHECK`

Repository layout:

- `src/`: contracts, core commands, CLI, benchmark tooling
- `tests/`: command/CLI/benchmark test suites
- `benchmarks/`: manifests, scripts, prompts, scoring, reports
- `docs/PIT_OF_SUCCESS.md`: canonical pit-of-success guidance
- `skills/`: reusable agent skill packs
- `AGENTS.md`: execution doctrine and meta-learning log
- `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`: research design and gates

Run CLI from source:

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
