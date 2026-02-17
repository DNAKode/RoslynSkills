# Task: MediatR OpenBehavior Null-Guard Correctness

Repository: `jbogard/MediatR`  
Commit: pinned in manifest

## Objective

Fix a correctness issue in `OpenBehavior` null guarding and add a focused unit test.

## Required change

- In `src/MediatR/Entities/OpenBehavior.cs`, fix `ValidatePipelineBehaviorType(Type openBehaviorType)` so passing `null` throws an `ArgumentNullException` with `ParamName == "openBehaviorType"`.
  - Do **not** use `ArgumentNullException.ThrowIfNull(...)` (this repo multi-targets TFMs where it is unavailable).
  - Use `throw new ArgumentNullException(nameof(openBehaviorType));` (or equivalent that sets `ParamName` correctly).
  - Do not change any non-null behavior of the validation.
- Add a unit test in the `test/MediatR.Tests` project verifying the `ParamName` is correct when `null` is passed to `new OpenBehavior(null!, ...)`.
  - Do not change `MediatRServiceConfiguration.AddOpenBehavior(...)` (this task is only about `MediatR.Entities.OpenBehavior`).
  - Keep the test minimal and targeted.

## Acceptance

- `dotnet test --nologo` passes.
- No unrelated formatting/refactors.

## Reflection

At end of run, provide the required JSON reflection block from `benchmarks/prompts/agent-eval-reflection-protocol.md`.
