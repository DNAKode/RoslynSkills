---
name: roslynskills-research
description: Run Roslyn-first coding research workflows for C#/.NET repositories using RoslynSkills CLI commands. Use when you need semantic navigation, context envelopes, diagnostics triage, non-destructive edit validation, or evidence capture for control vs Roslyn-enabled agent comparisons.
---

# RoslynSkills Research

## Session start

Run command inventory first:

```powershell
roscli list-commands --ids-only
roscli quickstart
```

Cross-platform alternative:

```bash
roscli list-commands --ids-only
roscli quickstart
```

If `roscli` is not on PATH, in this repository use:

```powershell
scripts\roscli.cmd list-commands --ids-only
scripts\roscli.cmd quickstart
```

Pit-of-success contract:

- Discover: `list-commands --ids-only`
- Orient: `quickstart`
- Disambiguate args: `describe-command <command-id>`
- Execute semantic-first: `nav.*`, `ctx.*`, `diag.*` before text fallback
- Verify before finalize: diagnostics + build/tests

Prefer Roslyn commands for navigation, context, diagnostics, repair, and structured edits before text-only fallbacks.

Mandatory policy: if a `.cs` read/edit uses non-Roslyn tooling, append a self-reflection entry to `ROSLYN_FALLBACK_REFLECTION_LOG.md` before continuing.

## Complementary dependency intelligence (optional)

When the task is about external package APIs (overloads, version diffs, vulnerability metadata), combine RoslynSkills with `dotnet-inspect` if available:

```powershell
dnx dotnet-inspect -y -- api JsonSerializer --package System.Text.Json
```

Use `dotnet-inspect` for dependency/package discovery, then use `roscli` for workspace-local semantic edits and diagnostics.

Selection hints:

- external package API shape / overloads / version diffs: `dotnet-inspect`
- in-repo symbol targeting / edits / diagnostics: `roscli`
- dependency-driven local changes: use both (inspect first, edit second)

Fast combined pattern:

```powershell
# 1) Inspect external API
dnx dotnet-inspect -y -- api JsonSerializer --package System.Text.Json

# 2) Make local semantic change
roscli nav.find_symbol src/MyProject/File.cs JsonSerializer --brief true --max-results 20
roscli edit.rename_symbol src/MyProject/File.cs 42 17 NewName --apply true
roscli diag.get_file_diagnostics src/MyProject/File.cs
```

## Roscli performance mode (high call volume)

For longer agent loops with many Roslyn calls, prefer cached published execution:

```powershell
scripts\roscli-warm.cmd
$env:ROSCLI_USE_PUBLISHED = "1"
```

Cross-platform:

```bash
./scripts/roscli-warm
export ROSCLI_USE_PUBLISHED=1
```

Use `ROSCLI_REFRESH_PUBLISHED=1` for one call when binaries need refresh after local source changes.
`ROSCLI_STALE_CHECK` is disabled by default in published mode for low latency; enable `ROSCLI_STALE_CHECK=1` during active roscli source development when automatic republish checks matter more than call speed.

## Simple command mode (positional + optional flags)

Use direct command invocation for common workflows and append optional flags directly:

```powershell
scripts\roscli.cmd ctx.file_outline src/RoslynSkills.Core/DefaultRegistryFactory.cs
scripts\roscli.cmd ctx.file_outline src/RoslynSkills.Core/DefaultRegistryFactory.cs --include-members false --max-types 1
scripts\roscli.cmd ctx.member_source src/RoslynSkills.Cli/CliApplication.cs 236 25 body --brief true
scripts\roscli.cmd ctx.member_source src/RoslynSkills.Cli/CliApplication.cs 236 25 --mode body --include-source-text true --context-lines-before 2
scripts\roscli.cmd diag.get_file_diagnostics src/RoslynSkills.Core/DefaultRegistryFactory.cs
scripts\roscli.cmd diag.get_solution_snapshot src --brief true
scripts\roscli.cmd diag.get_solution_snapshot src --mode compact --severity-filter Error --severity-filter Warning
scripts\roscli.cmd nav.find_symbol src/RoslynSkills.Cli/CliApplication.cs TryGetCommandAndInputAsync --brief true --max-results 200
scripts\roscli.cmd edit.rename_symbol src/RoslynSkills.Core/Commands/RenameSymbolCommand.cs 19 20 ValidateName --apply false
scripts\roscli.cmd edit.create_file src/RoslynSkills.Core/Commands/NewCommand.cs --content "public sealed class NewCommand { }"
scripts\roscli.cmd session.open src/RoslynSkills.Cli/CliApplication.cs demo-session
scripts\roscli.cmd session.get_diagnostics demo-session
scripts\roscli.cmd session.commit demo-session --keep-session false --require-disk-unchanged true
scripts\roscli.cmd session.diff demo-session
scripts\roscli.cmd session.close demo-session
```

Flag syntax supports:

- `--name value`
- `--name=value`
- `--flag` (implies `true`)
- `--no-flag` (implies `false`)
- repeated flags for arrays (for example `--severity-filter Error --severity-filter Warning`)

Keep JSON input for complex/structured operations (especially `edit.transaction`, `session.apply_text_edits`, `repair.apply_plan`, and advanced `diag.*` payloads).
When unsure about arguments for any command, run `roscli describe-command <command-id>` first.

Use JSON for `session.set_content` (full source payload) to avoid shell escaping issues.
Use `session.apply_and_commit` for one-shot structured edits + guarded commit when you do not need a long-lived session.
For simple read/navigation calls, avoid JSON payload piping and call the shorthand command directly to reduce token and transcript overhead.
`session.open` supports only `.cs`/`.csx` source files; do not open `.sln`, `.slnx`, or `.csproj`.

## Input transport (avoid temp files)

Prefer stdin JSON instead of `--input @temp.json`:

```powershell
$payload = @{ file_path = "src/MyFile.cs"; symbol_name = "TargetSymbol" }
$payload | ConvertTo-Json -Depth 8 | scripts\roscli.cmd run nav.find_symbol --input-stdin
```

Also supported:

- `--input -` to read JSON from stdin.
- `--input @path.json` only when stdin piping is impractical.

## Diagnostic triage workflow

Use `diag.get_solution_snapshot` in progressive detail:

1. Brief triage (lowest payload):

```powershell
scripts\roscli.cmd diag.get_solution_snapshot src --brief true
```

2. Compact triage (default):

```powershell
$payload = @{ directory_path = "src"; recursive = $true }
$payload | ConvertTo-Json -Depth 8 | scripts\roscli.cmd run diag.get_solution_snapshot --input-stdin
```

3. Guided triage (adds operation suggestions):

```powershell
$payload = @{
  directory_path = "src"
  mode = "guided"
  include_unfiltered = $false
  include_query = $false
  max_files_in_output = 12
}
$payload | ConvertTo-Json -Depth 8 | scripts\roscli.cmd run diag.get_solution_snapshot --input-stdin
```

4. Raw detail (includes diagnostic tuples):

```powershell
$payload = @{ directory_path = "src"; mode = "raw"; max_diagnostics = 2000 }
$payload | ConvertTo-Json -Depth 8 | scripts\roscli.cmd run diag.get_solution_snapshot --input-stdin
```

Useful filters:

- `severity_filter` (default: `Error`, `Warning`)
- `diagnostic_ids` for targeted triage
- `include_generated` (default `false`)
- `use_sdk_defaults` (default `true`) for realistic SDK-style compilation context

## Non-destructive analysis with immutable trees

Roslyn syntax trees/compilations are immutable snapshots. Use `file_overrides` for what-if diagnostics without editing files:

```powershell
$payload = @{
  file_paths = @("src/MyFile.cs")
  mode = "guided"
  file_overrides = @(
    @{
      file_path = "src/MyFile.cs"
      content = "public class Demo { public void M() { int x = ; } }"
    }
  )
}
$payload | ConvertTo-Json -Depth 12 | scripts\roscli.cmd run diag.get_solution_snapshot --input-stdin
```

After triage, keep edit loop non-destructive first:

1. `diag.get_after_edit` with `proposed_content`
2. chosen `edit.*` command in dry-run mode when available
3. apply only after diagnostics are acceptable

For iterative in-memory edit loops:

1. `session.open <file-path> [session-id]`
2. `session.status <session-id>` (capture `generation` and sync state)
3. Prefer `session.apply_text_edits` for local changes (small token payloads); use `session.set_content` for full rewrites.
4. `session.get_diagnostics`
5. `session.diff`
6. `session.commit` with conflict guards when ready (`expected_generation`, optional `require_disk_unchanged`)
7. `session.close`

Fast-path alternative:

1. `session.open <file-path> [session-id]`
2. `session.apply_and_commit --input-stdin` with `session_id`, `edits`, and optional `expected_generation`
3. Continue with a fresh `session.open` only if more edits are needed

Example structured span edit without temp files:

```powershell
$payload = @{
  session_id = "demo-session"
  expected_generation = 0
  edits = @(
    @{
      start_line = 42
      start_column = 13
      end_line = 42
      end_column = 22
      new_text = "newValue"
    }
  )
}
$payload | ConvertTo-Json -Depth 10 | scripts\roscli.cmd run session.apply_text_edits --input-stdin
```

Reliability rule:

- Before each mutating session step, pass `expected_generation` from the latest session result.
- Before commit, use `session.status` and fail closed if sync state indicates external disk drift (`disk_changed_external`).
- Keep `session.*` mutations sequential; do not run `session.commit` and `session.close` in parallel.
- Prefer `session.commit` as the terminal operation, and only call `session.close` separately when a committed session is intentionally kept open.

For coordinated multi-file changes in one step, use `edit.transaction`:

```powershell
$payload = @{
  apply = $false
  operations = @(
    @{
      operation = "replace_span"
      file_path = "src/A.cs"
      start_line = 12
      start_column = 19
      end_line = 12
      end_column = 24
      new_text = "NewName"
    },
    @{
      operation = "set_content"
      file_path = "src/B.cs"
      new_content = "public class B { }"
    }
  )
}
$payload | ConvertTo-Json -Depth 12 | scripts\roscli.cmd run edit.transaction --input-stdin
```

For micro-benchmark rename tasks, prefer one-shot verification helper where available:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\roslyn-rename-and-verify.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -OldName Process -ExpectedNewExact 2 -ExpectedOldExact 2 -RequireNoDiagnostics
```

## Token-efficiency tracking

For each paired run (baseline vs Roslyn-enabled), record:

- total model tokens (prompt/completion) when provider telemetry is available
- fallback proxy when telemetry is missing:
  - request JSON character count
  - response JSON character count
  - number of command round-trips
- command mix (`session.apply_text_edits` vs `session.set_content` vs text fallback)

Interpretation rule:

- Prefer workflows that reduce total tokens and retries while preserving correctness.

## Exploration gap logging

If Roslyn commands cannot answer a task and you must use plain file reads:

1. Record the gap and reason.
2. Propose a candidate exploratory command (`nav.*`/`ctx.*`/`diag.*`).
3. Add regression tests for the new command once implemented.

Use this template in `ROSLYN_FALLBACK_REFLECTION_LOG.md`:

- Date
- Task/context
- Fallback action (`read`/`edit`/`both`)
- Why Roslyn path was not used
- Roslyn command attempted (if any)
- Missing command/option hypothesis
- Proposed improvement
- Expected impact (correctness, latency, token_count)

