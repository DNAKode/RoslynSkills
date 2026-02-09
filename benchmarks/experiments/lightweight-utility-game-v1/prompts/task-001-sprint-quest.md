# Task 001: Build SprintQuest CLI Utility-Game

Create a small C# console utility/game called `SprintQuest`.

## Core concept

- A user runs focused work sprints and earns energy points.
- Each sprint has:
  - `title`
  - `planned_minutes`
  - `actual_minutes`
  - `completed` flag
- Energy score:
  - base points = `actual_minutes`
  - bonus +10 if `actual_minutes >= planned_minutes`
  - penalty -5 if sprint is abandoned

## CLI behaviors

- `sprintquest add "<title>" <planned-minutes>`
- `sprintquest done <id> <actual-minutes>`
- `sprintquest abandon <id>`
- `sprintquest status`

## Constraints

- Keep implementation simple and readable.
- Persist state to a JSON file in the working directory.
- Add basic input validation and clear error messages.

## Acceptance

- Build passes.
- Existing test suites continue to pass.
- Manual quick check: add -> done -> status reflects score and completion.

