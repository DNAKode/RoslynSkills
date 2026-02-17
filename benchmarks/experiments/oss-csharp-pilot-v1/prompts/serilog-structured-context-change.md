# Task: Serilog Structured Context Change (Null Enricher Guard)

Repository: `serilog/serilog`  
Commit: pinned in manifest

## Objective

Tighten `LogContext.Push(...)` structured context safety: passing a sequence/span containing a `null` enricher should throw immediately (instead of pushing a null and failing later).

## Required change

- In `src/Serilog/Context/LogContext.cs`:
  - In the `Push(params ReadOnlySpan<ILogEventEnricher> enrichers)` overload, guard each element and throw if any enricher is `null`.
  - In the `Push(params IEnumerable<ILogEventEnricher> enrichers)` overload, guard each element and throw if any enricher is `null`.
  - Do not change behavior for valid (non-null) enrichers.
- Add tests in `test/Serilog.Tests/Context/LogContextTests.cs`:
  - Verify `LogContext.Push((ReadOnlySpan<ILogEventEnricher>)[null!])` throws `ArgumentNullException`.
  - Verify `LogContext.Push(new ILogEventEnricher[] { null! })` throws `ArgumentNullException`.
  - Verify `LogContext.Push(new[] { (ILogEventEnricher)null! }.AsEnumerable())` throws `ArgumentNullException`.

## Acceptance

- `dotnet test --nologo` passes.
- No unrelated formatting or broad code motion.

## Reflection

At end of run, provide the required JSON reflection block from `benchmarks/prompts/agent-eval-reflection-protocol.md`.
