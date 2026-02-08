# Task: Serilog Structured Context Change

Repository: `serilog/serilog`  
Commit: pinned in manifest

## Objective

Introduce a small structured-logging behavior or API-usage change that requires accurate call-site identification and update related tests.

## Constraints

- Preserve backward compatibility where possible.
- Keep edits surgical.
- Ensure test coverage for changed behavior.

## Acceptance

- `dotnet test --nologo` passes.
- No unrelated formatting or broad code motion.

## Reflection prompt

At end of run, provide the required JSON reflection block from `benchmarks/prompts/agent-eval-reflection-protocol.md`.
