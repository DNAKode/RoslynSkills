# Task: MediatR Behavior Targeting

Repository: `jbogard/MediatR`  
Commit: pinned in manifest

## Objective

Implement a small behavioral change that requires precise targeting of pipeline behavior-related symbols and update tests to reflect the new behavior.

## Constraints

- Keep the change minimal and localized.
- Preserve existing public API signatures unless strictly necessary.
- Add or adjust tests so the change is validated.

## Acceptance

- `dotnet test --nologo` passes.
- No unrelated test churn.

## Reflection prompt

At end of run, provide the required JSON reflection block from `benchmarks/prompts/agent-eval-reflection-protocol.md`.
