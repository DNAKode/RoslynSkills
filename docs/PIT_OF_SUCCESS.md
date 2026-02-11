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
roscli quickstart
roscli describe-command session.open
roscli describe-command edit.create_file
```

This gives command discovery, guardrails, and two high-traffic argument schemas up front.

## Golden Paths

### 1) Safe symbol rename

```text
roscli nav.find_symbol src/MyProject/Program.cs Process --brief true --max-results 20
roscli edit.rename_symbol src/MyProject/Program.cs 42 17 Handle --apply true
roscli diag.get_file_diagnostics src/MyProject/Program.cs
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

## Guardrails (Must Be Explicit)

- `session.open` supports only `.cs` and `.csx`.
- Do not open `.sln`, `.slnx`, or `.csproj` with `session.open`.
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
5) run diagnostics/build/tests before finalizing.
```

## Anti-Patterns

- Starting with full-solution diagnostics when file-level diagnostics are enough.
- Repeated command retries without schema discovery (`describe-command`).
- Text edits for multi-file semantic changes before trying Roslyn primitives.
- Treating an LSP lane as valid evidence when tools were not actually available.

## Release Artifact Expectations

Every release bundle should include:

- `bin/roscli(.cmd)` launcher,
- this `PIT_OF_SUCCESS.md` guide,
- `skills/roslynskills-research/SKILL.md`.

First bundle command should still be:

```text
bin/roscli quickstart
```
