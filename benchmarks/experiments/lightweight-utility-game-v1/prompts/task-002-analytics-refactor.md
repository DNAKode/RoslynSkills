# Task 002: Refactor and Add Quick Analytics

Continue from Task 001.

## Changes

- Refactor SprintQuest internals to improve testability:
  - extract scoring logic behind an interface
  - isolate storage access from command handling
- Add `sprintquest report` command that prints:
  - total sprints
  - completed sprints
  - abandoned sprints
  - average actual minutes
  - total energy score

## Reliability focus

- Keep behavior backward compatible with existing commands.
- Avoid broad rewrites; prefer focused incremental edits.

## Acceptance

- Build passes.
- Existing tests pass.
- Manual check: report output is consistent after mixed done/abandon actions.

