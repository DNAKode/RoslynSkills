# Task 003: Direction Pivot

Product direction changed. Keep the current codebase, but pivot the experience:

- Rename user-facing concept from `SprintQuest` to `FocusDungeon`.
- Replace "energy score" with "streak points":
  - successful completion increases streak by 1
  - abandonment resets streak to 0
  - report shows current streak and best streak
- Add `focusdungeon undo-last` command:
  - reverts the latest state-changing action (`done` or `abandon`)
  - keep storage consistent and avoid corrupting history

## Requirements

- Minimize regression risk while changing naming and scoring semantics.
- Keep CLI behavior coherent during rename transition (aliases acceptable if needed).
- Document any compatibility decisions in code comments or output notes.

## Acceptance

- Build passes.
- Existing tests pass (or are updated with rationale).
- Manual check: complete -> abandon -> undo-last restores expected streak/history.

