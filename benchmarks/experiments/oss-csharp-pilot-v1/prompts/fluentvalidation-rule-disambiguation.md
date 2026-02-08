# Task: FluentValidation Rule Disambiguation

Repository: `FluentValidation/FluentValidation`  
Commit: pinned in manifest

## Objective

Apply a focused rule/validator change where symbol naming collisions or similar member names make lexical targeting error-prone.

## Constraints

- Keep scope constrained to the task intent.
- Ensure any updates are covered by tests.
- Avoid incidental refactors.

## Acceptance

- `dotnet test --nologo` passes.
- Modified tests clearly justify the change.

## Reflection prompt

At end of run, provide the required JSON reflection block from `benchmarks/prompts/agent-eval-reflection-protocol.md`.
