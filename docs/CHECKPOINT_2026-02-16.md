# Checkpoint 2026-02-16

## What Changed

- Expanded `benchmarks/scripts/Test-ClaudeSkillTriggering.ps1` scope beyond rename-only tasks:
  - `change-signature-named-args-v1`
  - `update-usings-cleanup-v1`
  - `add-member-threshold-v1`
  - `replace-member-body-guard-v1`
  - `create-file-audit-log-v1`
  - `rename-multifile-collision-v1`
- Added stronger benchmark gates:
  - workspace-level change detection (`workspace_changed`)
  - per-file assertions for multi-file/create-file tasks
  - per-run timeout (`ClaudeTimeoutSeconds`)
  - auth/access error classification (`auth_error`) with fail-closed default
- Logged run-fragment learnings and command-surface proposals in:
  - `ROSLYN_FALLBACK_REFLECTION_LOG.md` (`2026-02-16` entry)
- Implemented identified command-surface gaps:
  - `ctx.search_text`
  - `nav.find_invocations`
  - `nav.call_hierarchy`
  - `query.batch`
  - plus CLI shorthand and MCP input-schema hints.

- Refined naming/discoverability guidance:
  - call hierarchy usage hints now expose full option set (`context_lines`, `include_generated`).
- Added command maturity model (`stable`, `advanced`, `experimental`) with surfaced metadata in:
  - CLI `list-commands`/`describe-command`
  - MCP command catalog/tool annotations
  - pit-of-success and skill guidance (`stable` first, intentional use of non-stable commands).

## Latest Widened Sweep (Empirical Status)

- Full widened run completed:
  - `artifacts/skill-tests/wide-scope-v2/skill-trigger-summary.md`
- Aggregate results (6 tasks x 2 conditions x 1 replicate):
  - `no-skill`: `pass_rate=0.833`, `dotnet_build_pass_rate=1.0`, `roslyn_used_rate=0.0`, `timeout_rate=0.0`
  - `with-skill`: `pass_rate=0.667`, `dotnet_build_pass_rate=0.833`, `roslyn_used_rate=0.333`, `timeout_rate=0.333`
- Interpretation:
  - Skill condition did not produce consistent Roslyn adoption yet.
  - Timeouts and schema/trajectory churn still dominate on some operations.

## Validation Performed

- Fixture/build validation pass (auth errors ignored intentionally) succeeded:
  - `artifacts/skill-tests/wide-scope-fixture-validation-v2/skill-trigger-summary.md`
- Result:
  - all expanded task fixtures compile in baseline workspace (`dotnet_build_pass_rate = 1.0` for both conditions)
  - confirmed harness/task readiness before full empirical sweep
- Command implementation validation:
  - `dotnet build RoslynSkills.slnx`
  - `dotnet test tests/RoslynSkills.Core.Tests/RoslynSkills.Core.Tests.csproj --filter "FullyQualifiedName~BreadthCommandTests|FullyQualifiedName~CommandTests|FullyQualifiedName~SessionAndExplorationCommandTests"`
  - `dotnet test tests/RoslynSkills.Cli.Tests/RoslynSkills.Cli.Tests.csproj --filter "FullyQualifiedName~CliApplicationTests"`

## Current Blocker

- Not account access in this run (`auth_error_rate=0`), but usefulness: the current tight prompt is not reliably improving outcomes across varied operation families.

## Next Run Plan

1. Add explicit invocation arm to reduce passive non-adoption:
   - rerun with `-IncludeExplicitInvocation`
2. Add per-task call budgets in prompt guidance:
   - cap exploratory/tool-schema loops before first edit
3. Promote only if:
   - `with-skill pass_rate >= no-skill pass_rate`
   - `with-skill roslyn_used_rate` materially increases over current `0.333`
   - `timeout_rate` drops from current `0.333` without constraint/build regressions

## Follow-up Implementation Slice (2026-02-16, later)

- Added per-task call-budget guidance and explicit budget telemetry in:
  - `benchmarks/scripts/Test-ClaudeSkillTriggering.ps1`
  - new run fields: `task_call_budget`, `task_call_budget_applied`, `task_call_budget_exceeded`
  - new summary rates: `task_call_budget_applied_rate`, `task_call_budget_exceeded_rate`
- Added robust transcript parsing for mixed Claude stream event shapes:
  - `Parse-Transcript` now guards all optional fields under strict mode.

Empirical smoke run (single task, all conditions):

- artifact: `artifacts/skill-tests/checkpoint-explicit-budget-smoke/skill-trigger-summary.md`
- conditions: `no-skill`, `with-skill`, `with-skill-invoked`, `with-skill-invoked-budgeted`
- result:
  - all conditions passed task/build gates (`pass_rate=1.0`, `dotnet_build_pass_rate=1.0`)
  - no Roslyn usage yet (`roslyn_used_rate=0.0` across conditions)
  - budgeted condition respected budget (`task_call_budget_exceeded_rate=0.0`)
- interpretation:
  - instrumentation and fail-closed budget plumbing is now stable,
  - but prompt/skill posture still needs stronger Roslyn adoption forcing on this task family.

## Flow-analysis Surface Expansion (Roslyn tooling-oriented)

- Implemented new Roslyn-backed commands:
  - `analyze.control_flow_graph` (stable)
  - `analyze.dataflow_slice` (advanced)
- Added command plumbing updates:
  - registry integration (`DefaultRegistryFactory`)
  - `query.batch` support
  - CLI direct shorthand/help/describe coverage
  - MCP schema hints + command catalog examples
- Added tests:
  - C# command tests in `BreadthCommandTests`
  - VB parity tests in `VbCommandTests`
  - external large-VB smoke harness test (`ROSLYNSKILLS_DWSIM_PATH`) in `ExternalVbRepoSmokeTests`
