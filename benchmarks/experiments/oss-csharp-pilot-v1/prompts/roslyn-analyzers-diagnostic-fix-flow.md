# Task: Roslyn Analyzers Diagnostic Fix Flow

Repository: `dotnet/roslyn-analyzers`  
Commit: pinned in manifest

## Objective

Make a diagnostic/fix-related change in analyzer or test code that depends on precise symbol/context understanding.

## Constraints

- Keep change limited to one focused diagnostic/fix flow.
- Update tests to encode expected behavior.
- Avoid broad framework changes.

## Acceptance

- `dotnet test --nologo` passes.
- Updated assertions are specific to the intended change.

## Reflection prompt

At end of run, provide the required JSON reflection block from `benchmarks/prompts/agent-eval-reflection-protocol.md`.
