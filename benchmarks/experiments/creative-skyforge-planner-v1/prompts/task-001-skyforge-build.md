# Task 001: Skyforge Heist Planner

Create a new C# source file at:

- `benchmarks/experiments/creative-skyforge-planner-v1/workbench/SkyforgePlanner.cs`

Build a compact in-memory planner for a sky-heist game universe.

Requirements:

- Define a `SkyforgePlanner` class.
- Define a small mission record/model with:
  - codename
  - planned minutes
  - actual minutes (nullable before completion)
  - state (`planned`, `completed`, `aborted`)
- Implement methods:
  - `AddMission(string codename, int plannedMinutes)`
  - `CompleteMission(int missionId, int actualMinutes)`
  - `AbortMission(int missionId)`
  - `GetStatusReport()`
- Scoring:
  - start at 0
  - completion adds `actualMinutes`
  - completion bonus +12 if `actualMinutes >= plannedMinutes`
  - abort applies -7 penalty
- Keep code readable and self-contained in one file.

Output expectations:

- Include clear comments for any non-obvious logic.
- Ensure method names and class name match exactly.
