# Ecosystem Notes: dotnet-inspect and dotnet-skills

Date: 2026-02-11

## Sources Reviewed

- `https://github.com/richlander/dotnet-inspect`
- `https://github.com/richlander/dotnet-skills`
- `https://www.reddit.com/r/dotnet/comments/1qvef17/dotnetinspect_tool_inspect_net_resource_llm/`

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

- continue keeping `skills/roslynskills-research/SKILL.md` focused on practical first steps.
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

This aligns with community feedback in the referenced thread: package/assembly analysis and Roslyn workspace operations are adjacent but distinct concerns.
