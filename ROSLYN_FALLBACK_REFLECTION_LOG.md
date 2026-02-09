# Roslyn Fallback Reflection Log

Purpose: record every fallback to non-Roslyn `.cs` reading/editing so fallback patterns become Roslyn tool improvements.

## Entry Template

- Date:
- Task/Context:
- Fallback action:
  - `read` | `edit` | `both`
- Why Roslyn path was not used:
- Roslyn command attempted (if any):
- Missing command/option hypothesis:
- Proposed improvement:
- Expected impact:
  - correctness:
  - latency:
  - token_count:
- Follow-up issue/test link:

## Entries

- `2026-02-09`: Bootstrap policy entry -> Added mandatory fallback reflection rule to `AGENTS.md` and skill workflow -> Use this log as source for exploratory command backlog.
