# Questions Backlog

Purpose: collect questions that are not yet blocking execution.  
Do not interrupt workflow for these unless they become blockers or we have major results to review.

## Open (Non-Blocking)

1. Runner execution policy:
   - Should benchmark automation be allowed to launch `codex` / `claude` CLI processes directly from harness commands, or should runs be captured manually and only scored by this repo?
2. Raw trace retention:
   - For real agent trials, do you want full raw tool-call transcripts stored (larger artifacts), or summarized normalized run logs only?
3. Model matrix default:
   - Preferred default model variants for first full A/B trial pack (for example `fast` + `frontier` per provider).
4. Environment readiness for direct provider runs:
   - Current preflight on `2026-02-08` shows `codex` and `claude` binaries are missing in this environment (optional checks). Should we provision these CLIs here, or continue with offline run-log ingestion only?
5. Run-quality gate strictness:
   - Should `agent-eval-validate-runs` warnings (for example control contamination or missing Roslyn usage in treatment) fail the experiment gate, or should only validation errors block scoring?

## Resolved

- None yet.
