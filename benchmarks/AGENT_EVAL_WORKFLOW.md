# Agent-In-Loop A/B Workflow

This workflow is the primary evidence path for Roslyn utility claims.

## 1. Prepare manifest

Start from:

- `benchmarks/templates/agent-eval-manifest.template.json`

You can also use the first OSS pilot pack:

- `benchmarks/experiments/oss-csharp-pilot-v1/manifest.json`

Set:

- `conditions` (control/treatment),
- `tasks`,
- `runs_per_cell`,
- `roslyn_tool_prefixes`.

## 1.5 Run environment preflight

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-preflight --output <artifact-dir>
```

Required checks:

- `dotnet`, `git`, `rg`

Optional checks:

- `codex`, `claude`

## 1.7 Validate manifest quality

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-validate-manifest --manifest <manifest.json> --output <artifact-dir>
```

## 2. Generate worklist and pending run templates

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-init-runs --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir>
```

Outputs:

- `agent-eval-worklist.json`
- `pending-run-templates/*.json`

## 3. Execute real agent runs

For each pending template:

- run the task in the target agent environment (Codex CLI, Claude Code, etc.),
- fill in outcome, tool calls, token counts (`prompt_tokens`, `completion_tokens`, `total_tokens`), and post-run reflection JSON,
- place completed run JSON in `<runs-dir>`.

When available, capture short transcript fragments (command snippets and failure/recovery highlights) in run notes so qualitative impact can be quoted in reports.

Optional shortcut for quick logging:

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-register-run --manifest <manifest.json> --runs <runs-dir> --task <task-id> --condition <condition-id> --succeeded true --compile-passed true --tests-passed true --duration-seconds 120.5
```

Use:

- `benchmarks/prompts/agent-eval-reflection-protocol.md`

for reflection capture format.

For lightweight real-agent bundles, `Run-LightweightUtilityGameRealRuns.ps1` now supports:

- `-ConditionIds` for explicit condition selection from manifest.
  - PowerShell example: `-ConditionIds @('control-text-only','treatment-roslyn-optional')`.
- `-RoslynGuidanceProfile <standard|brief-first|surgical|skill-minimal|schema-first>` for treatment prompt posture experiments.
- Condition-level environment overrides via manifest `conditions[*].environment` plus optional agent-scoped `conditions[*].agent_environment.<agent>`.
- Per-task agent-home isolation (`CODEX_HOME` / `CLAUDE_CONFIG_DIR` + profile/appdata overrides) to reduce cross-run/session leakage.
- Control-workspace Roslyn wrapper disablement (`scripts/roscli*` exits non-zero in control condition) to harden contamination prevention.
- Host anchor checks around run/gate stages (fail-fast if harness cwd/git-root drifts from launch context).
- Automatic gate skip when selected conditions do not include both control and treatment (writes explicit skip reason to `gate/agent-eval-gate.log`).

## 4. Validate run quality before scoring

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-validate-runs --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir> [--fail-on-warnings true]
```

This check catches:

- control-condition contamination by Roslyn tools,
- malformed run fields,
- invalid Roslyn helpfulness scoring,
- missing or inconsistent run context.

`--fail-on-warnings true` makes warning-level findings fail the command (exit code 2) for stricter gating.

## 5. Score results

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-score --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir>
```

Outputs:

- `agent-eval-report.json` with:
  - control/treatment deltas,
  - per-task control/treatment comparison slices,
  - Roslyn tool adoption metrics,
  - reflection aggregates.
- `run-efficiency-analysis.(json|md)` can now include generic condition-pair deltas (for treatment-only optimization bundles without control lanes).

Optional report export:

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-export-summary --report <agent-eval-report.json> --run-validation <agent-eval-run-validation.json> --output <artifact-dir>
```

Optional single-command gate run (runs validation + scoring + summary export):

```powershell
dotnet run --project src/RoslynSkills.Benchmark -- agent-eval-gate --manifest <manifest.json> --runs <runs-dir> --output <artifact-dir> [--fail-on-warnings true]
```

With `--fail-on-warnings true`, the gate treats run-validation warnings as hard failures.

## 5.5 Optional paired real-agent harness

For fast paired control/treatment smoke runs with transcript + token attribution artifacts:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-PairedAgentRuns.ps1 -OutputRoot <artifact-dir> [-IsolationRoot <temp-dir>] [-KeepIsolatedWorkspaces] [-RoslynGuidanceProfile <standard|brief-first|surgical|skill-minimal|schema-first>]
```

Optional MCP treatment arm:

- Add `-IncludeMcpTreatment` to run an additional `treatment-mcp` lane per agent.
- The harness publishes `RoslynSkills.McpServer` once per bundle and wires MCP in isolated per-run configs only.
- Control lanes remain MCP-free under the same agent-home/session isolation guarantees.
- For codex MCP runs, prompt guidance should use resource APIs with explicit server id (`server=roslyn`) and command URIs (for example `roslyn://command/edit.rename_symbol?...`).
- Use `-RoslynGuidanceProfile` to compare treatment prompt posture:
  - `standard`: broad helper guidance (discovery + edit paths).
  - `brief-first`: compact-first guidance (skip catalog unless blocked).
  - `surgical`: minimum-call path (rename/verify first, fallback only on failure).
  - `skill-minimal`: realistic skill-style onboarding (discover commands, infer usage).
  - `schema-first`: contract-first onboarding (`describe-command`/`validate-input` before mutation).

Current guidance from skill-intro ablations (`artifacts/skill-intro-ablation/20260210-v2` and `artifacts/skill-intro-ablation/20260210-v3b-guidance-fix`):

- Default to `standard` or `surgical` for simple scoped tasks.
- Treat `schema-first` as a debugging/contract-validation lane, not a default execution lane.
- Keep prompt examples shell-specific (PowerShell vs Bash) and avoid inline JSON quoting in profile guidance.

Isolation and integrity defaults:

- Agent execution workspaces are created outside the repository tree by default (temp directory isolation).
- The harness hard-fails if an execution workspace is inside the host repo or any existing git repo.
- After each run, the harness verifies and restores host context (`cwd`, git top-level, and `HEAD`) so post-run work remains anchored to the primary repo session.
- Use `-KeepIsolatedWorkspaces` only when you need to inspect temp run environments after completion.

Current harness outputs include:

- `paired-run-summary.json`
- `paired-run-summary.md` with:
  - run-by-run table,
  - per-agent `control vs treatment` elapsed/token delta breakout,
  - per-agent `control vs treatment-mcp` elapsed/token delta breakout (when MCP lane is enabled),
- per-run metadata with:
  - control contamination detection,
  - deterministic rename constraint checks,
  - Roslyn attempted/successful call counts,
  - `duration_seconds` elapsed time per run,
  - `mcp_enabled` and MCP config file paths when applicable,
  - model token totals and cache-inclusive token totals,
  - command round-trip and transcript character attribution.

For artifact-local manifests generated by automation (for example `manifest.real-tools.json`), ensure `tasks[*].task_prompt_file` paths resolve from the artifact location (absolute paths are preferred) so post-run `agent-eval-gate` replays remain valid.

Optional trajectory usage analysis (Roslyn payload/`brief` adoption):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Analyze-TrajectoryRoslynUsage.ps1 -TrajectoriesRoot <artifact-dir-or-trajectories-dir>
```

This emits:

- `trajectory-roslyn-analysis.json`
- `trajectory-roslyn-analysis.md`

Use it to quantify:

- `brief` usage vs non-brief usage by lane/command,
- explicit `--brief` flag adoption,
- output-size proxies (`output_chars`, `source_chars`) for payload pressure diagnostics.
- discovery vs edit-like call mix (`cli/nav/ctx/diag` vs `edit/session/helper` families),
- `cli.list_commands` overhead,
- pre-edit exploration load (`roslyn_calls_before_first_edit`, `discovery_calls_before_first_edit`).

Optional roscli load profiling (wrapper mode tuning):

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Measure-RoscliLoadProfiles.ps1 [-IncludeStaleCheckOnProfiles] [-IncludeStaleCheckOffProfiles]
```

Wrapper knobs for high-call local sessions:

- `ROSCLI_USE_PUBLISHED=1` to use cached published CLI instead of `dotnet run`.
- `ROSCLI_STALE_CHECK=1` (optional; default is off) to auto-refresh published cache when watched source/config inputs change.
- `ROSCLI_REFRESH_PUBLISHED=1` for one-call republish refresh.
- `scripts/roscli-warm(.cmd)` to prewarm cache before long runs.

## 6. Interpretation rule

- Component diagnostics (for example `rq1`) are supporting evidence only.
- End-to-end claims require this A/B pipeline.

