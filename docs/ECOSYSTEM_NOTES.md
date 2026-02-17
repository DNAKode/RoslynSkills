# Ecosystem Notes: dotnet-inspect and dotnet-skills

Date: 2026-02-11

## Sources Reviewed

- `https://github.com/richlander/dotnet-inspect`
- `https://github.com/richlander/dotnet-skills`
- `https://www.reddit.com/r/dotnet/comments/1qvef17/dotnetinspect_tool_inspect_net_resource_llm/`
- `https://github.com/anthropics/claude-plugins-official/tree/main/plugins/csharp-lsp`
- `https://github.com/razzmatazz/csharp-language-server`

## What They Do

- `dotnet-inspect`: CLI for package/library/platform inspection (API shape, diffs, vulnerabilities, provenance).
- `dotnet-skills`: plugin/skill marketplace packaging for assistants (Copilot and Claude Code) that teaches agents how to use `dotnet-inspect`.

## Distribution and Packaging Patterns Worth Reusing

### 1) Tool package topology

`dotnet-inspect` publishes a pointer package plus RID-specific variants:

- pointer package: `dotnet-inspect`
- RID packages: `dotnet-inspect.win-x64`, `dotnet-inspect.linux-x64`, `dotnet-inspect.osx-arm64`, etc.
- fallback package: `dotnet-inspect.any`

Observed benefit:

- installer chooses best binary for platform (Native AOT where available, fallback otherwise).

Relevance for RoslynSkills:

- keep current simple global tool path as default.
- consider optional RID-specific packaging only if measured startup/runtime gains justify added release complexity.

### 2) Publish workflow separation

`dotnet-inspect` splits CI and publish:

- CI builds/tests/packs artifacts.
- release workflow manually publishes artifacts from a specific CI run ID.

Observed benefit:

- deterministic provenance from tested artifacts to published NuGet/GitHub Release.

Relevance for RoslynSkills:

- our current workflows are simpler and effective.
- if release cadence/variants grow, adopt CI-artifact-to-publish pattern to reduce accidental drift.

### 3) Two-layer LLM docs

`dotnet-inspect` uses:

- `SKILL.md` for fast 80% workflows.
- `llmstxt` command for full reference.

Observed benefit:

- low context overhead by default, deep docs on demand.

Relevance for RoslynSkills:

- continue keeping `skills/roslynskills-research/SKILL.md` focused on practical first steps, and use `skills/roslynskills-tight/SKILL.md` as the low-churn default for agents that over-explore.
- consider adding an explicit `roscli llmstxt`/reference command for deep, machine-friendly command docs beyond `describe-command`.

### 4) Skill marketplace packaging

`dotnet-skills` includes plugin metadata:

- `.claude-plugin/plugin.json`
- `.claude-plugin/marketplace.json`

Observed benefit:

- installation/update path from in-tool plugin manager instead of manual zip copying.

Relevance for RoslynSkills:

- strong candidate next step for adoption.
- should be evaluated as a packaging lane in addition to zip bundles and NuGet tool install.

### 5) `dnx` invocation guidance for agents

`dotnet-inspect` skill emphasizes:

- `dnx <tool> -y -- <args>`

Observed benefit:

- avoids interactive confirmation prompts.
- avoids argument parsing confusion between `dnx` and tool args.

Relevance for RoslynSkills:

- for complementary docs, include known-good invocation patterns and shell-specific examples.

## Complementarity Positioning (RoslynSkills + dotnet-inspect)

Natural split:

- `dotnet-inspect`: external dependency intelligence.
- `RoslynSkills`: in-repo semantic editing/diagnostics/repair.

Suggested combined agent workflow:

1. inspect external API/dependency behavior with `dotnet-inspect`.
2. perform local symbol-targeted changes with `roscli`.
3. verify with local diagnostics/build/tests.

Copy-paste hint for agent sessions:

```text
Use dotnet-inspect for external package/framework API intelligence.
Use roscli for local workspace semantic edits/diagnostics.
For migration tasks: inspect first, then edit with roscli.
```

Pit-of-success pointer for onboarding:

- `roscli quickstart` (runtime guidance)
- `docs/PIT_OF_SUCCESS.md` (project-level canonical guide)

This aligns with community feedback in the referenced thread: package/assembly analysis and Roslyn workspace operations are adjacent but distinct concerns.

## LSP Comparator Implication

- External C# LSP tooling is now an explicit comparator condition in RoslynSkills benchmarks, not a background assumption.
- For Claude-specific runs, treat `csharp-lsp` as a first-class alternative lane and record usage/adoption separately from Roslyn usage.

## Planned Close Comparator: dotnet-inspect / dotnet-skills

- Add explicit benchmark conditions where `dotnet-inspect` is tested:
  - instead of RoslynSkills,
  - and in conjunction with RoslynSkills.
- Scope these comparisons to tasks where package/API intelligence is actually decision-relevant (for example version migration and external API usage), not only local symbol edits.
- Keep condition isolation strict to avoid mixed-tool contamination in control lanes.

## Gemini CLI extension compatibility (initial lane)

Reference:

- `https://developers.googleblog.com/making-gemini-cli-extensions-easier-to-use/`

Status now:

- Added benchmark preflight probe for `gemini` with Windows shim fallbacks (`gemini.cmd`, `gemini.exe`).
- Added regression coverage in `tests/RoslynSkills.Benchmark.Tests/AgentEvalPreflightCheckerTests.cs`.

Guide-aligned implementation checklist for RoslynSkills:

1. Keep extension/tool entrypoints obvious:
   - one canonical startup flow near top of docs (`list-commands -> quickstart -> describe-command`).
2. Minimize "flag overload":
   - default to shorthand commands with sensible defaults; reserve full JSON payload mode for complex operations.
3. Minimize context overhead:
   - use `--brief true` guidance first, then escalate detail on demand.
4. Publish concrete examples:
   - include copy-paste examples for common operations and shell-safe syntax.
5. Fail closed for semantic correctness:
   - prefer workspace-bound invocations with `--require-workspace true` for project code.

Planned next step for Gemini:

- Add a dedicated Gemini lane in paired harness (control/treatment) once transcript parsing and auth-isolation details are validated for reliable metrics capture.

## OpenCode follow-up note

Future research item:

- Primary references:
  - `https://opencode.ai/docs/cli/`
  - `https://github.com/sst/opencode`

- Investigate OpenCode integration and measure parity against Codex/Claude/Gemini lanes for:
  - tool discovery friction,
  - MCP/CLI invocation reliability,
  - transcript telemetry extractability,
  - token/latency overhead under identical Roslyn task prompts.
