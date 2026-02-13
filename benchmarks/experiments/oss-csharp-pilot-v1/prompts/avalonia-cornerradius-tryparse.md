# Task: Avalonia CornerRadius.TryParse

Repository: `AvaloniaUI/Avalonia`
Commit: pinned in manifest

## Objective

Add an additive, non-breaking API:

- Implement `public static bool TryParse(string s, out CornerRadius result)` in `src/Avalonia.Base/CornerRadius.cs`.
- Semantics:
  - Return `true` and set `result` when parsing succeeds.
  - Return `false` and set `result` to `default` when parsing fails.
  - Accept the same input forms that `CornerRadius.Parse(string)` currently accepts (single uniform radius, top/bottom, and 4-value form, including spaces).
  - Do not change behavior of `CornerRadius.Parse(string)`.

Add tests in `tests/Avalonia.Base.UnitTests/CornerRadiusTests.cs`:

- `TryParse_Parses_Single_Uniform_Radius`
- `TryParse_Parses_Top_Bottom`
- `TryParse_Parses_TopLeft_TopRight_BottomRight_BottomLeft`
- `TryParse_Accepts_Spaces`
- `TryParse_ReturnsFalse_ForInvalidInput`

Use precise edits only; do not refactor unrelated code.

## Acceptance

- `dotnet test tests/Avalonia.Base.UnitTests/Avalonia.Base.UnitTests.csproj --nologo` passes.

## Reflection

At end of run, provide the required JSON reflection block from `benchmarks/prompts/agent-eval-reflection-protocol.md`.