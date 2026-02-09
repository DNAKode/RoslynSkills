# Task 002: Direction Pivot to Clockwork Carnival

Continue from Task 001 and update the same file:

- `benchmarks/experiments/creative-skyforge-planner-v1/workbench/SkyforgePlanner.cs`

Product direction changed from sky-heists to carnival performances.

Pivot requirements:

- Replace the main class with `ClockworkPlanner`.
- Replace mission outcome model with streak-based show outcomes.
- New mechanics:
  - successful show increments current streak
  - failed show resets current streak to 0
  - track `BestStreak`
- Add methods:
  - `MarkShowSuccess(string stageName, int applauseLevel)`
  - `MarkShowFailure(string stageName, string reason)`
  - `UndoLast()`
  - `GetCarnivalReport()`
- Keep `UndoLast()` safe for empty history.

Compatibility guidance:

- You may keep small compatibility wrappers if useful, but final report method should be `GetCarnivalReport()`.
- Keep implementation in one file and keep behavior deterministic.

Output expectations:

- Include a short comment explaining your undo approach.
- Ensure class/method names above are exact.
