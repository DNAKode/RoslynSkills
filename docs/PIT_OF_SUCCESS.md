# RoslynSkills Pit Of Success

Date: 2026-02-11  
Status: active onboarding contract

## Principle

RoslynSkills should make the safest high-value workflow the easiest workflow for agents:

1. discover commands quickly,
2. choose semantic operations before text fallback,
3. keep context brief-first,
4. verify before finalizing.

If an agent has to guess argument shapes or fumble through file types, this contract is not being met.

## First Minute Workflow

Run this sequence at session start:

```text
roscli list-commands --ids-only
roscli list-commands --stable-only --ids-only
roscli quickstart
roscli describe-command session.open
roscli describe-command edit.create_file
```

This gives command discovery, guardrails, and two high-traffic argument schemas up front.

## Command Tiers

- `stable`: default path for production agent loops.
- `advanced`: deeper analysis/orchestration; may be slower and/or partially heuristic.
- `experimental`: evolving contract for useful but less-stable outputs.

Default policy:

- Start with `stable` commands.
- Use `advanced`/`experimental` only when the task needs them.
- For non-stable commands, run `describe-command <id>` first and cap scope (`--brief`, small limits).

## Golden Paths

### 1) Safe symbol rename

```text
roscli nav.find_symbol src/MyProject/Program.cs Process --brief true --max-results 20 --require-workspace true
roscli edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true
roscli diag.get_file_diagnostics src/MyProject/Program.cs --require-workspace true
roscli diag.get_file_diagnostics src/MyProject/Program.cs --require-workspace true --workspace-path src/MyProject/MyProject.csproj
```

### 2) Create new file in one shot

```text
roscli edit.create_file src/MyProject/NewType.cs --content "public class NewType { }"
roscli diag.get_file_diagnostics src/MyProject/NewType.cs
```

### 3) Session-based edit loop

```text
roscli session.open src/MyProject/Program.cs demo-session
roscli session.status demo-session
roscli session.diff demo-session
roscli session.commit demo-session --keep-session false --require-disk-unchanged true
```

Note: `session.*` diagnostics are file-only (`ad_hoc`). For project-backed errors/warnings, prefer `diag.get_file_diagnostics` or `diag.get_after_edit` with `--require-workspace true` and pass `--workspace-path` if needed.

### 4) Workspace-backed directory triage

```text
roscli diag.get_workspace_snapshot src --brief true --require-workspace true
roscli diag.get_workspace_snapshot src --require-workspace true --max-files 500
roscli diag.get_workspace_snapshot src --require-workspace true --workspace-path MySolution.sln
```

## Guardrails (Must Be Explicit)

- `session.open` supports only `.cs` and `.csx`.
- `session.*` diagnostics are `ad_hoc` (file-only). Missing type/reference errors may be false negatives until verified with workspace-backed diagnostics (`diag.get_file_diagnostics`).
- Do not open `.sln`, `.slnx`, or `.csproj` with `session.open`.
- Check `workspace_context.mode` on semantic file commands (for example `nav.find_symbol`, `nav.find_references`, `ctx.symbol_envelope`, `diag.get_file_diagnostics`, `diag.get_after_edit`).
- If `workspace_context.mode` is `ad_hoc` for project code, rerun with `--workspace-path <.csproj|.sln|.slnx|dir>` and prefer `--require-workspace true`.
- For complex payloads, prefer `--input-stdin` over shell-escaped JSON.
- If RoslynSkills cannot answer a C# query, agent must state why before fallback.

## Complementary Tool Split

Use `dotnet-inspect` for external package/framework API questions.  
Use `roscli` for in-repo semantic navigation, editing, and diagnostics.

Combined migration pattern:

1. inspect external API shape first,
2. execute local semantic changes with `roscli`,
3. verify via diagnostics/build/tests.

## Agent Prompt Block

```text

Use roscli for C# work in this session.
Workflow:
1) run "roscli list-commands --ids-only" once.
2) run "roscli quickstart" and follow its recipes.
3) if argument shape is unclear, run "roscli describe-command <command-id>".
4) prefer nav.* / ctx.* / diag.* before text-only fallback.
5) verify `workspace_context.mode` for nav/diag file commands and force `--workspace-path` when needed; use `--require-workspace true` for fail-closed checks.
6) run diagnostics/build/tests before finalizing.
```

## Anti-Patterns

- Starting with full-solution diagnostics when file-level diagnostics are enough.
- Repeated command retries without schema discovery (`describe-command`).
- Text edits for multi-file semantic changes before trying Roslyn primitives.
- Treating an LSP lane as valid evidence when tools were not actually available.

## Advanced/Experimental Analyzer Set

Current non-stable analysis commands (use intentionally with bounded scope):

1. `nav.call_path` (`experimental`, `heuristic`, `potentially_slow`)
Bounded shortest-path search between source and target methods.
2. `analyze.unused_private_symbols` (`advanced`, `heuristic`, `derived_analysis`)
Likely-unused private symbol detection for cleanup triage.
3. `analyze.dependency_violations` (`experimental`, `heuristic`, `derived_analysis`)
Namespace-layer rule checks from ordered layer prefixes.
4. `analyze.impact_slice` (`advanced`, `heuristic`, `derived_analysis`)
Bounded impact slice around an anchored symbol.
5. `analyze.override_coverage` (`advanced`, `derived_analysis`)
Override/derived-type coverage hotspot detection for virtual/abstract members.
6. `analyze.async_risk_scan` (`experimental`, `heuristic`, `derived_analysis`)
Common async/sync-mixing risk pattern scan (for example `Task.Wait`, `.Result`, `async void`).

Contract rule:

- Every non-stable command must ship with explicit caveats in `describe-command` notes and bounded options (`max_*`, `brief`).

## Release Artifact Expectations

Every release bundle should include:

- `bin/roscli(.cmd)` launcher,
- this `PIT_OF_SUCCESS.md` guide,
- `skills/roslynskills-research/SKILL.md`,
- `skills/roslynskills-tight/SKILL.md`.

First bundle command should still be:

```text
bin/roscli quickstart
```
