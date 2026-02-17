# Skill Trigger Test Queries (Claude)

Purpose: quick manual regression suite for over/under-triggering, based on Anthropic's skill guide.

How to use:

1. Install the skill zip (Claude.ai or Claude Code).
2. Run these prompts (including paraphrases).
3. Record: did the skill trigger, tool calls count, retries, and whether you had to redirect.

## Automated Checks (Repo Maintainers)

```powershell
powershell -ExecutionPolicy Bypass -File scripts/skills/Validate-Skills.ps1
powershell -ExecutionPolicy Bypass -File scripts/skills/SmokeTest-ClaudeSkillLoad.ps1 -SkillName roslynskills-tight

# Empirical adoption check (runs Claude and inspects transcripts)
powershell -ExecutionPolicy Bypass -Command "& benchmarks/scripts/Test-ClaudeSkillTriggering.ps1 `
  -OutputRoot artifacts/skill-tests/checkpoint-skill-trigger `
  -ClaudeModel sonnet `
  -Replicates 1 `
  -TaskId @('rename-overload-collision-nested-v1') `
  -IncludeExplicitInvocation `
  -IncludeDotnetBuildGate"

# Wider operation scope (signature/usings/member-body/create-file/multi-file).
# Keep auth fail-closed default; use -IgnoreAuthenticationError only for fixture/build validation.
powershell -ExecutionPolicy Bypass -Command "& benchmarks/scripts/Test-ClaudeSkillTriggering.ps1 `
  -OutputRoot artifacts/skill-tests/wide-scope-v1 `
  -ClaudeModel sonnet `
  -Replicates 1 `
  -TaskId @('change-signature-named-args-v1','update-usings-cleanup-v1','add-member-threshold-v1','replace-member-body-guard-v1','create-file-audit-log-v1','rename-multifile-collision-v1') `
  -IncludeDotnetBuildGate `
  -ClaudeTimeoutSeconds 180"
```

## roslynskills-tight

Should trigger:

- "Rename this C# method but only the int overload"
- "This symbol name is ambiguous across overloads, can you rename the right one?"
- "Fix these C# compiler errors and keep the change minimal"
- "Refactor this C# API safely across files without breaking build"
- "Find where this C# symbol is defined and rename it"
- "Search these patterns across C# projects and show exact call sites for this method"

Should NOT trigger:

- "Write a blog post about Roslyn"
- "Summarize this markdown document"
- "Format this JSON file"
- "Explain how git rebase works"
- "Refactor this Python script"

Command-surface smoke (new investigative commands):

```powershell
roscli ctx.search_text "RemoteUserAction" src --mode literal --max-results 40 --brief true
roscli nav.find_invocations src/RoslynSkills.Core/DefaultRegistryFactory.cs 8 24 --brief true --max-results 40
roscli nav.call_hierarchy src/RoslynSkills.Core/DefaultRegistryFactory.cs 8 24 --direction incoming --max-depth 1 --brief true

$payload = @{
  continue_on_error = $true
  queries = @(
    @{
      command_id = "ctx.search_text"
      input = @{
        patterns = @("ctx.search_text", "nav.find_invocations")
        mode = "literal"
        roots = @("src")
        max_results = 20
        brief = $true
      }
    }
  )
}
$payload | ConvertTo-Json -Depth 8 | roscli run query.batch --input-stdin
```

## roslynskills-research

Should trigger:

- "Investigate these C# build errors across the solution and propose a minimal repair plan"
- "I need semantic context for this symbol (not grep): show the declaration and references"
- "Do a safe multi-file refactor and verify diagnostics before committing"
- "Benchmark control vs treatment: record tool calls, tokens, and outcomes"

Should NOT trigger:

- "Write unit tests for a JavaScript function"
- "Plan a vacation itinerary"
- "Draft a press release"
- "Explain what MCP is (no code changes)"
