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

Run this sequence at session start before reading or editing `.cs` files:

```text
roscli --version
roscli csharp-start
roscli ctx.changed_files
roscli workspace.preload MySolution.slnx --alias default --require-solution true
roscli ctx.file_outline tests/MyTests.cs --member-name-contains Target --max-members 20
roscli ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text "ExpectedLiteral" --context-lines-before 3 --context-lines-after 8
roscli describe-command edit.replace_in_member
roscli list-commands --ids-only
roscli list-commands --stable-only --ids-only
```

This gives command discovery, a hot solution workspace, member-scoped source context, and the high-traffic scoped edit schema up front.

`roscli csharp-start` is the compact markdown version intended for fresh agents. Use it when a prompt can only point to one roscli onboarding command.

For supervised fresh-agent trials, use `roscli csharp-start --supervised` as a two-turn protocol. First require the agent to run only `roscli csharp-start` and report its first two headings. Assign the C# slice only after the transcript shows the command actually ran; prose promises are not enough. Turn 2 should preload the solution with `workspace.preload` before `ctx.file_outline` or `ctx.member_source`, should run `describe-command` before the first Roslyn edit command, should keep broad `ctx.search_text` capped and switch to focused outline/member-source reads after high-match results or a `result_guidance` recommendation, and should use `ctx.search_text` or `ctx.member_source` instead of `rg` for `.cs` closeout anchors.

For `roscli agent-start` fresh panes, the next repo-facing action after startup must be `roscli agent-begin`. Do not read docs, list files, inspect git, run searches, or start implementation until `agent-begin` succeeds and its three step summaries are reported.

When the solution filename is known, prefer `roscli csharp-start --supervised --solution <solution.sln|.slnx>` so Turn 2 contains a copyable `workspace.preload` command instead of a placeholder.

For `.cs` orientation in a dirty repo, start with `ctx.changed_files` to see changed C# paths without reading file contents, then use `ctx.file_outline --member-name-contains <term> --max-members 20`, `ctx.member_source --member-name <name> --focus-text <literal>`, capped `ctx.search_text`, or `nav.*` before `git diff`, `rg`, `Get-Content`, `sed`, `cat`, or a patch-editor read. `ctx.search_text` accepts both positional scope and direct aliases such as `roscli ctx.search_text --file-path src/MyFile.cs --text RemoteUserAction --max-results 20 --context-lines 0`. If a search response includes `result_guidance`, follow its suggested narrowing command instead of repeating another broad search. If fallback is required, state which roscli command was missing or insufficient.

## Command Tiers

- `stable`: default path for production agent loops.
- `advanced`: deeper analysis/orchestration; may be slower and/or partially heuristic.
- `experimental`: evolving contract for useful but less-stable outputs.

Default policy:

- Start with `stable` commands.
- Use `advanced`/`experimental` only when the task needs them.
- For non-stable commands, run `describe-command <id>` first and cap scope (`--brief`, small limits).

## Golden Paths

### 1) Fresh C# slice

First-slice budget for fresh agents: target one behavior, one primary claimed file when feasible, and one focused test before wider validation. If focused tests already pass on existing dirty work and no new mutation is needed, report validation-only and choose a different smallest unimplemented increment unless the user asked only to validate. If the work expands past two C# files or more than one behavior, stop at the smallest validated increment and report the remaining work instead of turning the first slice into a broad feature.

```text
roscli csharp-start --supervised
roscli csharp-start --supervised --solution MySolution.slnx
roscli csharp-start
roscli ctx.changed_files
roscli workspace.preload MySolution.slnx --alias default --require-solution true
roscli ctx.file_outline tests/MyTests.cs --member-name-contains Target --max-members 20
roscli ctx.member_source tests/MyTests.cs --member-name TargetTest --focus-text "ExpectedLiteral" --context-lines-before 3 --context-lines-after 8
roscli text.measure --text "fixed-width UI label"
roscli edit.claim claim tests/MyTests.cs --reason narrow-csharp-slice
roscli edit.replace_in_member tests/MyTests.cs --member-name TargetTest --old-text "Assert.Equal(1, value);" --new-text "Assert.Equal(2, value);" --preview-chars 256
dotnet test tests/MyTests.csproj --no-restore --filter FullyQualifiedName~TargetTest
roscli edit.claim release <claim_id>
```

### 2) Safe symbol rename

```text
roscli nav.find_symbol src/MyProject/Program.cs Process --brief true --max-results 20 --workspace-path MySolution.slnx --require-workspace true
roscli edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true --workspace-path MySolution.slnx --require-workspace true
roscli diag.get_file_diagnostics src/MyProject/Program.cs --workspace-path MySolution.slnx --require-workspace true
```

### 3) Create new file in one shot

```text
roscli edit.create_file src/MyProject/NewType.cs --content "public class NewType { }"
roscli diag.get_file_diagnostics src/MyProject/NewType.cs
```

### 4) Session-based edit loop

```text
roscli session.open src/MyProject/Program.cs demo-session
roscli session.status demo-session
roscli session.diff demo-session
roscli session.commit demo-session --keep-session false --require-disk-unchanged true
```

Note: `session.*` diagnostics are file-only (`ad_hoc`). For project-backed errors/warnings, prefer `diag.get_file_diagnostics` or `diag.get_after_edit` with `--require-workspace true` and pass `--workspace-path` if needed.

### 5) Multi-agent claim-first edit loop

```text
roscli edit.claim list
roscli edit.claim claim src/MyFile.cs --owner agent-a --reason focused-slice
roscli ctx.member_source src/MyFile.cs --member-name HandleInput --focus-text "TargetLiteral" --context-lines-before 3 --context-lines-after 8
roscli edit.replace_in_member src/MyFile.cs --member-name HandleInput --old-text "old exact text" --new-text "new exact text" --preview-chars 256
dotnet test tests/MyTests.csproj --no-restore --filter FullyQualifiedName~FocusedTest
roscli edit.claim release <claim_id>
```

For multiple subagents, use distinct stable `--owner` names and disjoint file claims. Assign known file ownership before spawning; if a subagent discovers a file later, it must claim before its first edit and report the claim id. Serialize shared-file work through one owner. If several changes target the same member, combine them into one fresh `edit.replace_in_member` old/new block or one `edit.batch_exact` `replace_span` operation with `expected_text`; do not run parallel edit commands against stale `ctx.member_source` reads. Prefer guarded mutations: `edit.replace_in_member`, `edit.batch_exact` with `expected_text`, or `session.commit --require-disk-unchanged true`.

Mutation command responses include `claim_status`. If a write summary says `unclaimed`, stop further C# mutation, run `edit.claim claim <file> --owner <owner> --reason <slice>`, then continue with guarded edits.

### 6) Workspace-backed directory triage

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
- Prefer explicit `.sln`/`.slnx` workspace paths for repo-wide context and hot workspace hosts; use `.csproj` only when intentionally project-scoped.
- Check `workspace_context.resolved_workspace_path`, `workspace_context.workspace_kind`, and `workspace_context.project_count` when full solution context matters.
- If `workspace_context.mode` is `ad_hoc` for project code, rerun with `--workspace-path <.sln|.slnx|.csproj|dir>` and prefer `--require-workspace true`.
- For complex payloads, prefer `--input-stdin` over shell-escaped JSON.
- Do not use `git diff`, `rg`, `Get-Content`, `sed`, `cat`, or patch-editor reads for `.cs` orientation until a roscli `ctx.*` or `nav.*` command has been tried. In dirty repos, `ctx.changed_files` is the roscli-native first check for changed path scope.
- Do not use `rg`/`git diff`/file reads merely to report the line number of a changed `.cs` assertion; use `ctx.search_text` or `ctx.member_source`.
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
1) before reading or editing .cs files, run "roscli --version" and "roscli csharp-start".
2) in dirty repos, run "roscli ctx.changed_files" for changed C# path scope instead of "git diff --stat".
3) preload the solution with "roscli workspace.preload <solution.sln|.slnx> --alias default --require-solution true".
4) orient with "roscli ctx.file_outline" and "roscli ctx.member_source"; avoid git diff/rg/Get-Content/sed/cat for .cs orientation unless roscli cannot answer.
4a) keep broad search capped; if "ctx.search_text" returns many matches, narrow with "ctx.file_outline --member-name-contains" or "ctx.member_source --focus-text" instead of repeating broad searches.
5) if argument shape is unclear, run "roscli describe-command <command-id>".
6) claim before .cs mutation with "roscli edit.claim claim <file> --reason <reason>".
7) for small member-local edits, prefer "roscli edit.replace_in_member"; if ctx.member_source returns focus=not-found, rerun with a better literal, smaller context window, or include_source_text=false before reading larger source; for large member edits, use ctx.member_source include_edit_target_text=true then edit.batch_exact replace_span from edit_target.exact_span_text.text.
8) if an edit command reports claim_status.claimed=false or summary "unclaimed", claim before further C# mutation.
9) if multiple edits touch the same member, re-read ctx.member_source once and combine them into one guarded edit; do not issue parallel same-member edit commands.
10) run diagnostics/build/tests and release claims before finalizing; final responses with active claims are non-compliant, so run `edit.claim list` after release and report active claim count 0.
```

## Anti-Patterns

- Starting with full-solution diagnostics when file-level diagnostics are enough.
- Starting `.cs` orientation with `git diff`, `rg`, `Get-Content`, `sed`, `cat`, or patch-editor reads before trying roscli `ctx.*`/`nav.*`.
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
