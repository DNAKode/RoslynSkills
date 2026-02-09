# Questions Backlog

Purpose: collect questions that are not yet blocking execution.  
Do not interrupt workflow for these unless they become blockers or we have major results to review.

## Open (Non-Blocking)

- None currently.

## Resolved

1. Run-quality gate strictness (`2026-02-09`):
   - Decision implemented as policy toggle instead of hard-coded behavior.
   - `agent-eval-validate-runs` and `agent-eval-gate` now support `--fail-on-warnings <true|false>`.
   - Default remains permissive (`false`) for exploratory data collection; strict mode (`true`) is available for promotion gates.

2. Runner execution policy (`2026-02-09`):
   - Enable direct provider execution from benchmark harness/scripts by default (`codex`/`claude` launched by automation when present).
   - Keep manual/offline run-log ingestion as fallback for environments without provider CLIs or credentials.

3. Raw trace retention (`2026-02-09`):
   - Retain full raw transcripts plus normalized run logs for all promotion-grade and report-bound real-agent runs.
   - For exploratory local smoke runs, normalized logs remain required; raw transcript retention is recommended but optional.

4. Model matrix default (`2026-02-09`):
   - Default first full A/B pack to provider default model per agent (`codex` default and `claude` default) for stable reproducibility.
   - Expand to explicit `fast` + `frontier` per provider only after baseline pack is complete and validated.

5. Environment readiness for direct provider runs (`2026-02-09`):
   - Re-checked via `agent-eval-preflight`; both `codex` and `claude` are currently available in this environment.
   - Decision: proceed with direct provider-run automation here; no additional CLI provisioning required at this time.
