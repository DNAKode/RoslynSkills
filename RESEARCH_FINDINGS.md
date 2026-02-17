# Research Findings

Date started: `2026-02-09`  
Purpose: empirical log for Roslyn-agent benchmark outcomes, with task context and tool identity snapshots.

Naming note: this file contains historical observations captured before the internal rename to `RoslynSkills.*` (2026-02-10), so some entries intentionally reference former `RoslynAgent.*` paths/identifiers.

## Tool Identity Snapshot

Captured on `2026-02-09`:

- Repo commit: `f783155e36be62143e76f6854b44777bdb1a5b00`
- Roslyn CLI command schema version (`system.ping` envelope): `Version: 1.0`
- Roslyn CLI framework (`system.ping`): `.NET 10.0.2`
- Roslyn CLI command surface (`list-commands --compact`): `32` commands
- CLI project identity (`src/RoslynAgent.Cli/RoslynAgent.Cli.csproj`):
  - `PackageId`: `RoslynAgent.Cli`
  - `TargetFramework`: `net10.0`
  - `ToolCommandName`: `roslyn-agent`

Note: we currently do not stamp a semantic CLI build version in run artifacts; commit SHA is the primary build identity.

## Task Family Context (Current Evidence)

Primary bundle family so far: creative C# utility/refactor tasks in `creative-skyforge-planner-v1`.

- `task-001-skyforge-build`: implement a small in-memory planner class and scoring behavior.
- `task-002-carnival-pivot`: perform concept/model/API pivot refactor with behavior constraints.

This family is:

- medium ambiguity,
- moderate edit scope,
- dominated by file-local/class-local changes,
- acceptance-check driven.

## Findings Log

### F-2026-02-09-01: Treatment still costs more elapsed time and tokens than control in this task family

Evidence:

- `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v3`
- `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v4-brief-first-20260209-202905`

Per-agent control vs treatment deltas:

| Bundle | Agent | Elapsed ratio (treat/control) | Token ratio (treat/control) | Elapsed delta (s) | Token delta |
| --- | --- | ---: | ---: | ---: | ---: |
| `v3` | `codex-cli` | 2.983x | 5.087x | +411.457 | +1,244,981 |
| `v3` | `claude-code` | 1.614x | 1.644x | +142.357 | +5,943 |
| `v4-brief-first` | `codex-cli` | 3.675x | 5.191x | +431.438 | +1,142,601 |
| `v4-brief-first` | `claude-code` | 1.481x | 1.593x | +88.392 | +4,590 |

Interpretation:

- correctness parity is maintained, but treatment remains substantially more expensive for this task type.
- `brief-first` improved some Claude overhead, but did not reverse treatment > control.

### F-2026-02-09-02: Condition integrity regressed in v4 (control contamination observed)

Evidence:

- v3 validation: `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v3/gate/validation/agent-eval-run-validation.json`
  - `contaminated_control_runs: 0`
- v4 validation: `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v4-brief-first-20260209-202905/gate/validation/agent-eval-run-validation.json`
  - `contaminated_control_runs: 1`

Interpretation:

- prompt-only control constraints are insufficient.
- harness-level blocking/isolation is required to preserve A/B validity.

### F-2026-02-09-03: `brief` usage improved in v4 but adoption is still low

Evidence:

- v3 analysis: `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v3/trajectory-roslyn-analysis.json`
- v4 analysis: `benchmarks/experiments/creative-skyforge-planner-v1/artifacts/real-agent-runs-v4-brief-first-20260209-202905/trajectory-roslyn-analysis.json`

Trajectory `brief` summary:

| Bundle | Parsed Roslyn results | With brief field | brief=true | brief=false |
| --- | ---: | ---: | ---: | ---: |
| `v3` | 39 | 3 | 0 | 3 |
| `v4-brief-first` | 36 | 3 | 2 | 1 |

Interpretation:

- explicit `brief-first` guidance changes behavior, but only in a small share of calls.
- command guidance and defaults still need stronger steering if token efficiency is the objective.

### F-2026-02-09-04: Persistent transport cuts invocation latency by large margins vs process-per-call CLI

Evidence:

- invocation benchmark report:
  - `artifacts/roscli-invocation-benchmark/20260209-192423/roscli-invocation-benchmark.json`
  - `artifacts/roscli-invocation-benchmark/20260209-192423/roscli-invocation-benchmark.md`
- implementation artifacts:
  - `src/RoslynAgent.TransportServer/Program.cs`
  - `benchmarks/scripts/Benchmark-RoscliInvocationModes.ps1`

Result (avg elapsed ms, 2 iterations per mode/command):

| Command | `roscli_script` avg ms | `dotnet_published_dll` avg ms | `transport_published_server` avg ms |
| --- | ---: | ---: | ---: |
| `system.ping` | 2718.662 | 241.567 | 111.178 |
| `cli.list_commands.compact` | 2917.549 | 128.183 | 5.479 |
| `diag.get_file_diagnostics.program` | 4048.872 | 1057.138 | 513.518 |

Warm-state signal from transport samples:

- `system.ping`: iter1 `221.371` ms, iter2 `0.986` ms
- `cli.list_commands.compact`: iter1 `9.576` ms, iter2 `1.382` ms
- `diag.get_file_diagnostics.program`: iter1 `924.267` ms, iter2 `102.77` ms

Interpretation:

- a major portion of current CLI cost is process startup/initialization overhead, not just command logic.

### F-2026-02-15-01: Claude skill adoption is measurable and reproducible; Bash-first guidance reduces wrapper friction

Evidence:

- skill prompt update: `skills/roslynskills-tight/SKILL.md` (Bash-first `bash scripts/roscli ...` guidance)
- empirical trigger/adoption harness: `benchmarks/scripts/Test-ClaudeSkillTriggering.ps1`
- example run summary: `artifacts/skill-tests/checkpoint-20260215i/skill-trigger-summary.md`
- example run transcript: `artifacts/skill-tests/checkpoint-20260215i/rename-overload-collision-nested-v1-with-skill-invoked-r01/transcript.jsonl`

Result (single task, 1 replicate):

- `skill_loaded_rate`: 1.0 for installed conditions
- `roslyn_used_rate`: 1.0 for installed conditions (detected via transcript tool-use lines)
- `dotnet_build_pass_rate`: 1.0 across conditions (harness gate)

Interpretation:

- "skill loads" is not sufficient; we need transcript-based usage accounting and file-change/build gates to detect hallucinated/non-applied edits.
- Claude Code often executes via the `Bash` tool even on Windows; skill examples must prioritize `bash scripts/roscli ...` to avoid path separator churn.
- published-DLL CLI already removes most `dotnet run` overhead, but persistent transport removes additional per-call launch cost.
- warm calls on a persistent process are often an order of magnitude faster than published-DLL process-per-call execution.

Decision:

- promote persistent transport/MCP-style serving from "idea" to active experimental arm.
- update benchmark harness to run three treatment variants under identical tasks:
  - process-per-call CLI,
  - published-DLL process-per-call CLI,
  - persistent transport server.
- add explicit warmup policy (discard warmup call or report warm/cold separately) so latency comparisons are statistically fair.

### F-2026-02-09-05: Real MCP server contract is implemented and harness-isolated MCP lane is ready

Evidence:

- server implementation:
  - `src/RoslynAgent.McpServer/Program.cs`
  - `src/RoslynAgent.McpServer/RoslynAgent.McpServer.csproj`
- harness integration:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`-IncludeMcpTreatment`)
- smoke artifacts:
  - `artifacts/paired-mcp-smoke/paired-run-summary.json`
  - `artifacts/paired-mcp-smoke/tools/roslyn-mcp/RoslynAgent.McpServer.dll`

Result:

- MCP server now speaks codex-compatible stdio JSON-RPC (accepts framed or newline-delimited inbound messages and emits newline-delimited responses), supporting:
  - `initialize`
  - `tools/list`
  - `tools/call`
  - `ping`
- Tool surface is mapped from command registry to MCP-safe names (`roslyn_<command_id_with_underscores>`).
- Paired harness can run an additional `treatment-mcp` lane with per-run isolated MCP setup (no host/global config mutation required for benchmark lanes).

Interpretation:

- MCP is no longer a speculative direction; it is executable inside the existing benchmark framework.
- We can now test interface-channel impact (CLI vs MCP) under the same control/treatment integrity gates.

Decision:

- schedule the next replicate bundle with `control`, `treatment` (CLI helpers), and `treatment-mcp` (native MCP tools) under identical prompts/tasks.
- report per-agent elapsed/token/round-trip deltas with explicit `mcp_enabled` slice in summary exports.

### F-2026-02-09-06: MCP-lane dry run surfaced two harness defects (auth seeding and Roslyn telemetry false positives)

Evidence:

- attempted codex MCP bundle:
  - `artifacts/paired-mcp-codex-smoke/paired-run-summary.json`
- harness fixes:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`New-AgentEnvironmentOverrides`, `Get-CodexRoslynUsage`/`Get-CodexRoslynInvocationText`)

Result:

- Initial codex runs failed with `401 Unauthorized` under isolated `CODEX_HOME` because auth files were not copied into run-local agent homes.
- Control contamination check falsely triggered when workspace paths contained `roslyn-agent-paired-runs` (substring-based telemetry detection was too broad).

Interpretation:

- Session isolation must preserve auth material while still preventing config/tool cross-talk.
- Roslyn usage detection must key on explicit command/tool signatures, not generic `roslyn` substrings in filesystem paths.

Decision:

- Copy minimal auth artifacts into isolated homes (`auth.json`/`cap_sid` for Codex, `.credentials.json` for Claude) before agent invocation.
- Restrict Roslyn telemetry detection to explicit command patterns and MCP tool names (`roslyn_*`) rather than free-text substring checks.
- Re-run MCP-enabled paired bundle after these fixes before drawing MCP vs CLI performance conclusions.

### F-2026-02-09-07: Codex MCP treatment lane is now operational with successful Roslyn resource calls

Evidence:

- `artifacts/paired-mcp-codex-resource-v4/paired-run-summary.json`
- `artifacts/paired-mcp-codex-resource-v4/paired-run-summary.md`

Result:

- `control`: `roslyn_used=false`, `duration_seconds=13.172`, `total_tokens=36,904`
- `treatment` (CLI): `roslyn_used=true (3/4)`, `duration_seconds=24.822`, `total_tokens=50,592`
- `treatment-mcp`: `roslyn_used=true (3/3)`, `duration_seconds=14.893`, `total_tokens=80,954`

Interpretation:

- MCP server/client interoperability for Codex is now working in paired-run harness conditions (resource-template discovery + command execution via `read_mcp_resource`).
- `treatment-mcp` is close to control on elapsed time (`+1.721s`) while preserving correctness/constraint checks.
- Token overhead remains high in `treatment-mcp` on this microtask (`+44,050` vs control), so transport viability is proven but token efficiency is not yet improved.

Decision:

- Keep MCP as an active benchmark arm, but optimize prompt flow to avoid high-cost exploratory calls (for example large command-catalog reads) unless required.
- Run next replicate with task families where semantic disambiguation is expected to amortize MCP/tooling overhead.

### F-2026-02-09-08: MCP transport compatibility required protocol and URI normalization fixes

Evidence:

- `src/RoslynAgent.McpServer/Program.cs`
- `artifacts/paired-mcp-codex-resource-v3/paired-run-summary.json`
- `artifacts/paired-mcp-codex-resource-v4/paired-run-summary.json`

Result:

- v3 failed MCP startup/initialize with client parse errors due stdio framing mismatch.
- MCP reliability improved after:
  - accepting both framed and newline-delimited inbound JSON-RPC,
  - emitting newline-delimited outbound responses,
  - normalizing `roslyn://commands` vs `roslyn://commands/`,
  - enforcing explicit `server=roslyn` prompt guidance.

Interpretation:

- "MCP support" is not binary; transport framing details can dominate usability.
- Protocol-shape and URI-shape compatibility must be tested with real agent clients, not inferred from spec conformance alone.

Decision:

- Treat client/server compatibility checks as a mandatory MCP preflight gate before comparative benchmark runs.
- Keep regression tests around initialization + `resources/templates/list` + `resources/read` (`roslyn://commands` and `roslyn://command/...`) to prevent silent protocol drift.

### F-2026-02-09-09: Cross-agent paired MCP run shows large elapsed win for MCP vs CLI treatment, but mixed token outcomes

Evidence:

- `artifacts/paired-mcp-codex-claude-v1/paired-run-summary.json`
- `artifacts/paired-mcp-codex-claude-v1/paired-run-summary.md`

Result:

- Codex:
  - control: `14.44s`, `36,215` tokens
  - treatment (CLI): `24.56s` (`+10.12s`), `49,284` tokens (`+13,069`)
  - treatment-mcp: `15.869s` (`+1.429s` vs control, `-8.691s` vs treatment), `80,750` tokens (`+44,535` vs control, `+31,466` vs treatment)
- Claude:
  - control: `28.219s`, `847` tokens
  - treatment (CLI): `44.565s` (`+16.346s`), `1,059` tokens (`+212`)
  - treatment-mcp: `27.472s` (`-0.747s` vs control, `-17.093s` vs treatment), `759` tokens (`-88` vs control, `-300` vs treatment)

Interpretation:

- MCP treatment now consistently improves elapsed time versus CLI treatment for both agents on this microtask.
- Token behavior diverges by agent: Codex MCP is materially higher-token, while Claude MCP is lower-token than both control and CLI treatment in this run.

Telemetry caveat:

- Current summary undercounts Claude MCP Roslyn usage (`roslyn_used=false` in `claude-treatment-mcp`) because usage parsing does not yet fully classify `ReadMcpResourceTool` URI-based Roslyn calls.
- Transcript evidence in the same artifact shows successful Roslyn MCP command URIs (`roslyn://command/nav.find_symbol`, `edit.rename_symbol`, `diag.get_file_diagnostics`).

Decision:

- Keep elapsed and token comparisons from this bundle, but treat Claude MCP `roslyn_used` counters as provisional until parser update lands. (superseded by `F-2026-02-09-10`)
- Prioritize parser fix for Claude MCP resource-tool attribution before using `roslyn_used` as a hard gate in MCP lanes. (completed in `F-2026-02-09-10`)

### F-2026-02-09-10: Claude MCP Roslyn-attribution fix is validated on a fresh cross-agent bundle

Evidence:

- `artifacts/paired-mcp-dnakode-v1/paired-run-summary.json`
- `artifacts/paired-mcp-dnakode-v1/paired-run-summary.md`
- `artifacts/paired-mcp-dnakode-v1/agent-breakout.md`
- `artifacts/paired-mcp-dnakode-v1/claude-treatment-mcp/transcript.jsonl`

Result:

- `claude-treatment-mcp` now reports `roslyn_used=true`, `roslyn_attempted_calls=3`, `roslyn_successful_calls=3`.
- `codex-treatment-mcp` also reports `3/3` successful Roslyn calls in the same bundle.
- Elapsed deltas (`control -> treatment -> treatment-mcp`):
  - Codex: `12.687s -> 16.771s -> 15.743s` (MCP `-1.028s` vs CLI treatment, `+3.056s` vs control).
  - Claude: `22.406s -> 32.270s -> 30.964s` (MCP `-1.306s` vs CLI treatment, `+8.558s` vs control).

Interpretation:

- The `ReadMcpResourceTool` URI classification fix now closes the prior telemetry gap; Roslyn usage counters for Claude MCP lanes are no longer undercounted for this benchmark shape.
- MCP remains faster than CLI treatment for both agents on this microtask, while still not beating control elapsed time.

Decision:

- Promote Claude MCP `roslyn_*` telemetry from provisional to trusted for this harness version.
- Keep collecting replicate bundles before claiming stable MCP-vs-control latency wins.

### F-2026-02-09-11: Paired guidance-profile lane now supports direct prompt-posture experiments; early codex smoke converged to the same one-call Roslyn helper path

Evidence:

- Harness/profile implementation:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`-RoslynGuidanceProfile standard|brief-first|surgical`)
- New codex paired bundles:
  - `artifacts/paired-guidance-standard-codex-smoke-r1/paired-run-summary.json`
  - `artifacts/paired-guidance-surgical-codex-smoke-r2/paired-run-summary.json`
- Trajectory telemetry:
  - `artifacts/paired-guidance-standard-codex-smoke-r1/trajectory-roslyn-analysis.md`
  - `artifacts/paired-guidance-surgical-codex-smoke-r2/trajectory-roslyn-analysis.md`

Result:

- `standard` bundle:
  - control: `15.365s`, `36,923` tokens
  - treatment: `11.942s`, `19,065` tokens
  - treatment Roslyn usage: `1/1` successful call
- `surgical` bundle:
  - control: `12.576s`, `36,914` tokens
  - treatment: `11.484s`, `18,646` tokens
  - treatment Roslyn usage: `1/1` successful call
- In both bundles, trajectory analysis reports:
  - command: `roslyn.rename_and_verify` (helper family),
  - discovery calls before first edit: `0`,
  - `cli.list_commands` calls: `0`.

Interpretation:

- The new guidance-profile arm is working and measurable.
- On this rename microtask, codex treatment behavior appears dominated by the one-shot helper path, so `standard` and `surgical` produce effectively the same Roslyn usage trajectory.
- For this specific task shape, compact/surgical guidance did not unlock a distinct call pattern because the model already selected the minimal path.

Decision:

- Keep guidance-profile experiments enabled for paired runs.
- Use higher-ambiguity/multi-step tasks to test whether profiles diverge in discovery load and token efficiency; this microtask is now mainly a harness sanity check.

### F-2026-02-10-12: Roscli cached published mode materially reduces per-call latency under varied command loads

Evidence:

- Wrapper changes:
  - `scripts/roscli.cmd` (`ROSCLI_USE_PUBLISHED`, `ROSCLI_REFRESH_PUBLISHED`)
  - `scripts/roscli` (`ROSCLI_USE_PUBLISHED`, `ROSCLI_REFRESH_PUBLISHED`)
  - `scripts/roscli-warm.cmd`
  - `scripts/roscli-warm`
- Load benchmark:
  - `benchmarks/scripts/Measure-RoscliLoadProfiles.ps1`
  - `artifacts/roscli-load-profiles/roscli-load-profiles-v6.json`
  - `artifacts/roscli-load-profiles/roscli-load-profiles-v6.md`

Result (avg ms/call, dotnet-run vs published-prewarmed):

- `system.ping`:
  - load 1: `2909.06` vs `147.79` (`19.68x` speedup)
  - load 5: `2886.67` vs `142.50` (`20.26x`)
  - load 20: `2891.20` vs `147.81` (`19.56x`)
- `cli.list_commands.compact`:
  - load 1: `2786.11` vs `150.88` (`18.47x`)
  - load 5: `2947.12` vs `188.43` (`15.64x`)
  - load 20: `2928.56` vs `145.03` (`20.19x`)
- `nav.find_symbol`:
  - load 1: `3742.49` vs `995.82` (`3.76x`)
  - load 5: `3833.99` vs `992.58` (`3.86x`)
  - load 20: `3864.51` vs `1011.55` (`3.82x`)

Interpretation:

- Process-per-call `dotnet run` overhead remains dominant for lightweight Roslyn calls.
- Published cached execution sharply improves responsiveness for repeated agent tool calls, including semantic calls.
- Prewarmed and cached profiles are near-identical in steady state; prewarm mainly removes first-call variance.

Decision:

- Keep default wrapper behavior unchanged for development correctness (`dotnet run`), but use `ROSCLI_USE_PUBLISHED=1` in high-volume agent sessions.
- Add explicit warm commands (`roscli-warm`) and include this mode in skill/README guidance.

### F-2026-02-10-13: Published-cache wrapper mode improved call startup cost but did not yet translate into better end-to-end agent metrics on a single open-ended task replicate

Evidence:

- Real-run bundle:
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v4/trajectory-run-summary.json`
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v4/run-efficiency-analysis.md`
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v4/trajectory-roslyn-analysis.md`
- Harness/prompt updates used in this run:
  - `benchmarks/scripts/Run-LightweightUtilityGameRealRuns.ps1`
  - `benchmarks/experiments/lightweight-utility-game-v1/manifest.json`

Result (Codex, task `task-001-initial-build`, both lanes succeeded):

- `treatment-roslyn-optional` (`ROSCLI_USE_PUBLISHED=0`):
  - elapsed `330.902s`
  - total tokens `1,268,650`
  - command round-trips `33`
- `treatment-roslyn-published-cache` (`ROSCLI_USE_PUBLISHED=1`):
  - elapsed `424.935s`
  - total tokens `1,390,370`
  - command round-trips `41`

Interpretation:

- Startup optimization is real at component level (see `F-2026-02-10-12`) but can be overshadowed by trajectory variance on unconstrained tasks.
- Treatment runs diverged in code structure and exploration depth (more files/round-trips in published lane), dominating wrapper-level savings.
- `diag.get_solution_snapshot` remained a high-payload risk when not tightly scoped, reinforcing bounded-diagnostics guidance.

Decision:

- Keep published mode as a recommended high-call execution setting, but do not claim end-to-end speed/token benefit from single creative-task replicates.
- Prioritize replicate-backed comparisons on tighter, higher-ambiguity edit tasks with stricter trajectory controls (same acceptance clauses, constrained edit targets, bounded diagnostic payload policies).

### F-2026-02-10-14: Efficiency reporting needed pairwise deltas beyond control-vs-treatment for orthogonal treatment-only experiments

Evidence:

- Script update:
  - `benchmarks/scripts/Analyze-RunEfficiency.ps1`
- Regenerated report:
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v4/run-efficiency-analysis.md`

Result:

- Added `by_agent_condition_pair_delta` JSON section and markdown `Condition Pair Delta` table.
- Treatment-only runs now emit direct condition comparisons without requiring a control lane.
- Example from `20260210-lightweight-roscli-mode-v4`:
  - baseline `treatment-roslyn-optional` vs compare `treatment-roslyn-published-cache`
  - elapsed ratio `1.2842`
  - token ratio `1.0959`
  - round-trip delta `+8`

Interpretation:

- Orthogonal research arms (transport mode, wrapper mode, guidance posture) frequently compare treatment variants directly; control-only deltas were insufficient for fast decision-making.

Decision:

- Keep control-vs-treatment deltas for core claims, and use pairwise condition deltas as default readout for treatment-only optimization experiments.

### F-2026-02-10-15: Published-cache stale-check probes impose significant per-call overhead; low-latency default should keep stale-check opt-in

Evidence:

- Wrapper behavior:
  - `scripts/roscli.cmd`
  - `scripts/roscli`
- Load benchmark artifacts:
  - `artifacts/roscli-load-profiles/roscli-load-profiles-v9.json`
  - `artifacts/roscli-load-profiles/roscli-load-profiles-v9.md`

Result (stale-check on/off ratios within v9):

- `published_cached` vs `published_cached_stale_off`:
  - `system.ping`: `3.15x-3.70x` slower with stale-check on (loads `5/20`).
  - `cli.list_commands.compact`: `3.22x-3.66x` slower with stale-check on (loads `5/20`).
  - `nav.find_symbol`: `1.29x-1.42x` slower with stale-check on (loads `5/20`).
- Same pattern held for prewarmed profile (`published_prewarmed` vs `published_prewarmed_stale_off`).

Interpretation:

- The stale-check path currently adds enough wrapper overhead to erase much of the published-cache latency benefit for light commands.
- This overhead is wrapper-level and independent from agent trajectory variance.

Decision:

- Set wrapper default to fast mode (`ROSCLI_STALE_CHECK=0` unless explicitly enabled).
- Keep explicit safety knobs:
  - `ROSCLI_REFRESH_PUBLISHED=1` for one-call forced refresh.
  - `ROSCLI_STALE_CHECK=1` for source-development loops where automatic refresh checks matter more than per-call speed.

### F-2026-02-10-16: Follow-up treatment replicate after stale-check default shift showed large published-mode gains, but direction is still replicate-sensitive

Evidence:

- Prior replicate:
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v4/run-efficiency-analysis.md`
- Follow-up replicate:
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/run-efficiency-analysis.md`
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/trajectory-roslyn-analysis.md`
  - `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/trajectory-run-summary.json`

Result (Codex, same task id, treatment-vs-treatment):

- v4 (`published` stale-check on):
  - elapsed ratio (published/dotnet-run): `1.2842`
  - token ratio: `1.0959`
  - round-trip delta: `+8`
- v5 (`published` stale-check off):
  - elapsed ratio (published/dotnet-run): `0.6328`
  - token ratio: `0.428`
  - round-trip delta: `-17`

Interpretation:

- Wrapper-mode defaults materially influence agent trajectory cost in this task family.
- Single-run directional claims are unstable; the same task can reverse treatment ranking across adjacent replicates.

Decision:

- Keep stale-check disabled by default in published mode.
- Require replicate bundles (not single-run reads) before promoting wrapper-mode claims to report-level conclusions.

### F-2026-02-10-17: Shell-specific skill-introduction guidance materially reduced onboarding failure overhead, but schema-first remains a high-cost default

Evidence:

- Baseline guidance-profile bundle:
  - `artifacts/skill-intro-ablation/20260210-v2/skill-intro-ablation-report.json`
- Updated guidance-profile bundle:
  - `artifacts/skill-intro-ablation/20260210-v3b-guidance-fix/skill-intro-ablation-report.json`
- Prompt/guidance updates:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`Get-CliRoslynGuidanceBlock`, shell-specific `schema-first`/`surgical` instructions)

Result (treatment vs control deltas, v2 -> v3b):

- `schema-first`:
  - Codex: duration `+189.601s -> +42.478s` (`-147.123s`), token delta `+403,442 -> +102,293` (`-301,149`), round-trips `+26 -> +10`.
  - Claude: duration `+83.289s -> +44.147s` (`-39.142s`), token delta `+2,948 -> +877` (`-2,071`), round-trips `+16 -> +10`.
- `surgical`:
  - Codex: duration `-1.171s -> -3.205s`, token delta `-18,246 -> -18,269`, round-trips stayed `-1`.
  - Claude: duration `+27.57s -> +19.634s` (`-7.936s`), token delta `+438 -> -53`, round-trips `+3 -> +1`.

Interpretation:

- Inference from bundle comparison: shell-matched examples and safer input transport reduce failure-retry loops substantially.
- `schema-first` shifted from catastrophic overhead to manageable but still expensive overhead; it remains better suited to contract/debug scenarios than default execution.
- `surgical` remains the strongest low-friction default posture for small, tightly-scoped edits, especially for Codex.

Decision:

- Keep `standard` and `surgical` as default onboarding profiles for most runs.
- Keep `schema-first` as an explicit optional arm for contract-validation experiments, not as baseline treatment guidance.
- Continue shell-specific prompt examples (PowerShell vs Bash) as a required harness guideline.

### F-2026-02-10-18: PowerShell `@file.json` parsing requires quoted `--input` values in schema-first Codex guidance

Evidence:

- Transitional run showing splatting parse failure and recovery:
  - `artifacts/skill-intro-ablation/20260210-v3b-guidance-fix/paired-schema-first`
- Follow-up smoke after quoting fix:
  - `artifacts/skill-intro-ablation/20260210-v3c-schema-smoke-codex/paired-run-summary.json`
- Prompt fix:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`--input \"@*.json\"` in PowerShell schema-first examples)

Result:

- Unquoted `--input @nav.find_symbol.json` can be interpreted by PowerShell as splatting in some command contexts.
- Quoted forms (`--input \"@nav.find_symbol.json\"`) removed that failure mode in follow-up smoke runs.

Interpretation:

- Small shell-grammar details in guidance text can dominate trajectory quality.
- Profile prompts should be treated as executable interfaces and regression-tested like code.

Decision:

- Keep quoted `@file` syntax in PowerShell profile guidance.
- Add this as a standing prompt-quality check in profile experimentation.

### F-2026-02-11-19: External ecosystem scan supports a complementary positioning and highlights distribution upgrades for RoslynSkills

Evidence:

- `dotnet-inspect` repo/docs/workflows:
  - `https://github.com/richlander/dotnet-inspect/blob/main/README.md`
  - `https://github.com/richlander/dotnet-inspect/blob/main/src/dotnet-inspect/dotnet-inspect.csproj`
  - `https://github.com/richlander/dotnet-inspect/blob/main/.github/workflows/ci.yml`
  - `https://github.com/richlander/dotnet-inspect/blob/main/.github/workflows/release.yml`
  - `https://github.com/richlander/dotnet-inspect/blob/main/docs/llm-design.md`
- `dotnet-skills` repo:
  - `https://github.com/richlander/dotnet-skills/blob/main/README.md`
  - `https://github.com/richlander/dotnet-skills/blob/main/.claude-plugin/plugin.json`
  - `https://github.com/richlander/dotnet-skills/blob/main/.claude-plugin/marketplace.json`
  - `https://github.com/richlander/dotnet-skills/blob/main/skills/dotnet-inspect/SKILL.md`
- Announcement thread:
  - `https://www.reddit.com/r/dotnet/comments/1qvef17/dotnetinspect_tool_inspect_net_resource_llm/`
- Internal research notes:
  - `docs/ECOSYSTEM_NOTES.md`
  - `docs/ANNOUNCEMENT_NOTES.md`

Result:

- `dotnet-inspect` focuses on package/library/platform inspection and is distributed as a global tool with RID-aware package variants plus a pointer package.
- `dotnet-skills` focuses on skill/plugin distribution ergonomics (marketplace metadata + simple install/update flows).
- Announcement framing and comments reinforce demand for source/workspace-aware semantics as a separate but adjacent capability.

Interpretation:

- RoslynSkills and dotnet-inspect occupy adjacent layers of the same agentic workflow:
  - dependency/package intelligence (dotnet-inspect),
  - workspace-semantic editing/diagnostics (RoslynSkills).
- Distribution UX is a primary adoption lever; packaging, skill metadata, and copy-paste-safe onboarding commands materially affect real usage.

Decision:

- Position RoslynSkills explicitly as complementary to package-inspection tooling in user-facing docs.
- Keep investing in low-friction onboarding surfaces (command discoverability, shell-safe examples, and skill packaging paths).
- Track marketplace/plugin packaging as a candidate distribution lane alongside NuGet and release bundles.

### F-2026-02-11-20: External C# LSP is now an explicit benchmark lane with harness telemetry support

Evidence:

- Harness updates:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`-IncludeClaudeLspTreatment`, `treatment-lsp` mode, LSP usage telemetry fields)
- Scope/plan updates:
  - `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md` (RQ4 and trial conditions now include external C# LSP)
  - `DETAILED_RESEARCH_PLAN.md`
  - `IMPLEMENTATION_AND_TEST_PLAN.md`
  - `benchmarks/LSP_COMPARATOR_PLAN.md`

Result:

- Comparative research scope now includes three primary interface families:
  - RoslynSkills CLI/MCP,
  - external C# LSP comparator (Claude `csharp-lsp` lane),
  - text-only control.
- Paired-run metadata now captures:
  - `lsp_used`,
  - `lsp_attempted_calls`,
  - `lsp_successful_calls`,
  - `lsp_command_round_trips`.

Interpretation:

- We can now evaluate "does LSP make RoslynSkills obsolete?" as a repeatable condition-level question rather than anecdotal transcript interpretation.
- This reduces risk of over-claiming Roslyn value (or under-claiming LSP value) from mixed, uncontrolled sessions.

Decision:

- Treat external LSP as mandatory comparator lane in future interface-effectiveness claims.
- Require replicate-backed control vs Roslyn vs LSP bundles before architecture-level conclusions.

### F-2026-02-11-21: dotnet-inspect is complementary by architecture, and should be compared as both substitute and combined lane

Evidence:

- `https://github.com/richlander/dotnet-inspect/blob/main/README.md`
- `https://github.com/richlander/dotnet-inspect/blob/main/docs/llm-design.md`
- `https://github.com/richlander/dotnet-inspect/blob/main/src/dotnet-inspect/dotnet-inspect.csproj`
- `https://github.com/richlander/dotnet-skills/blob/main/README.md`
- `https://github.com/richlander/dotnet-skills/blob/main/skills/dotnet-inspect/SKILL.md`

Result:

- `dotnet-inspect` emphasizes package/library/assembly/API inspection, diffing, and provenance/vulnerability audit with LLM-oriented output and guidance.
- `dotnet-skills` emphasizes distribution/marketplace onboarding for that workflow.
- No Roslyn workspace command surface is exposed; this is not a direct substitute for workspace-semantic local edit operations.

Interpretation:

- Overlap with RoslynSkills exists at "help agent pick the right symbol/API" level.
- Core strengths differ:
  - `dotnet-inspect`: external dependency/API intelligence,
  - RoslynSkills: in-repo semantic navigation/edit/diagnostics.
- Best performance may come from combined usage on dependency-driven tasks.

Decision:

- Add explicit benchmark matrix for `inspect-only`, `roslyn-only`, and `combined` conditions on package/API-sensitive tasks.

### F-2026-02-11-22: In completed profile ablations, `brief-first` remained the lowest-friction Roslyn guidance posture while preserving correctness

Evidence:

- `artifacts/skill-intro-ablation/20260211-cycle1/skill-intro-ablation-report.json`
- `artifacts/skill-intro-ablation/20260211-cycle1/paired-brief-first/paired-run-summary.json`
- `artifacts/skill-intro-ablation/20260211-cycle1/paired-skill-minimal/paired-run-summary.json`
- `artifacts/skill-intro-ablation/20260211-cycle1/paired-standard/paired-run-summary.json`

Result:

- Codex treatment vs control:
  - `brief-first`: duration `+6.878s`, tokens `-6,970`, round-trips `+0`.
  - `skill-minimal`: duration `+29.033s`, tokens `+37,189`, round-trips `+5`.
- Claude treatment vs control:
  - `brief-first`: duration `+22.516s`, tokens `+215`, round-trips `+2`.
  - `skill-minimal`: duration `+34.968s`, tokens `+515`, round-trips `+5`.
- Completed-profile runs remained correctness-clean (`run_passed=true`) with successful Roslyn usage in treatment lanes.

Interpretation:

- Inference from `20260211-cycle1` completed profiles: concise posture (`brief-first`) reduces trajectory churn versus `skill-minimal` without sacrificing task success.
- `skill-minimal` remains useful as a stress/diagnostic posture but is too expensive as default guidance.

Decision:

- Keep `brief-first` as the default profile for mixed-agent research runs.
- Keep `skill-minimal` as an explicit diagnostic arm only.

### F-2026-02-11-23: LSP-lane integrity telemetry now cleanly distinguishes missing LSP setup from Roslyn contamination

Evidence:

- `artifacts/real-agent-runs/20260211-lsp-guard-final/paired-run-summary.json`
- `artifacts/real-agent-runs/20260211-lsp-guard-final/paired-run-summary.md`
- `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`lsp_tools_available`, `lsp_tools_unavailable_detected`, `lsp_lane_roslyn_contamination_detected`)

Result:

- Claude `treatment-lsp` run reported:
  - `run_passed=true`,
  - `roslyn_used=false`,
  - `lsp_used=false`,
  - `lsp_tools_available=false`,
  - `lsp_tools_unavailable_detected=true`,
  - `lsp_lane_roslyn_contamination_detected=false`.
- Control and Roslyn treatment lanes in the same bundle remained uncontaminated and passed constraints.

Interpretation:

- We can now separate three failure/neutral cases:
  - LSP not available in environment,
  - LSP available but not adopted,
  - Roslyn contamination in LSP-only lane.
- This removes a major ambiguity that previously made LSP comparisons hard to trust.

Decision:

- Require `lsp_tools_available=true` before interpreting LSP-lane performance as capability evidence.
- Keep `FailOnMissingLspTools` default permissive for data collection, but enable it for strict promotion bundles.

### F-2026-02-11-24: Ablation rollups now preserve nulls for missing comparator lanes instead of coercing to synthetic deltas

Evidence:

- `benchmarks/scripts/Run-SkillIntroductionAblation.ps1` (`Safe-Delta`, `Safe-Ratio` null handling)
- regenerated report:
  - `artifacts/skill-intro-ablation/20260211-cycle1/skill-intro-ablation-report.json`

Result:

- For agents/conditions without `treatment-lsp` runs (for example Codex rows in this bundle), fields such as:
  - `treatment_lsp_vs_control_duration_delta`,
  - `treatment_lsp_vs_control_token_delta`
  now remain `null` rather than showing misleading negative values derived from implicit zero treatment values.

Interpretation:

- Prior rollups could accidentally imply LSP wins/losses where the lane was never executed.
- Null-preserving aggregation is required for trustworthy cross-condition dashboards.

Decision:

- Treat absent-lane metrics as null by construction.
- Require explicit lane presence before including comparator deltas in summaries or decisions.

### F-2026-02-11-25: Pit-of-success guidance is now embedded as a runtime surface and release deliverable (not docs-only)

Evidence:

- CLI/runtime:
  - `src/RoslynSkills.Cli/CliApplication.cs` (`quickstart`, help hints, list-commands `pit_of_success` hints)
  - `tests/RoslynSkills.Cli.Tests/CliApplicationTests.cs` (quickstart/help/unknown-command guidance assertions)
- canonical guide:
  - `docs/PIT_OF_SUCCESS.md`
- distribution path:
  - `scripts/release/Build-ReleaseArtifacts.ps1` (bundle now includes `PIT_OF_SUCCESS.md` and quickstart-first README note)
  - smoke artifact: `artifacts/release/0.1.6-pitlocal/roslynskills-bundle/PIT_OF_SUCCESS.md`

Result:

- Agents can now discover onboarding guidance through multiple redundant channels:
  - `--help`,
  - `list-commands`,
  - `quickstart`,
  - release bundle contents.
- Release artifacts now carry pit-of-success guidance adjacent to launchers, reducing dependence on repository browsing.

Interpretation:

- Prior posture still required too much docs navigation and prompt luck.
- Embedding guidance directly in runtime and artifacts should reduce first-command errors and argument confusion.

Decision:

- Treat pit-of-success guidance as part of the command/distribution contract.
- Require future command-surface changes to preserve startup-path discoverability (`list-commands` -> `quickstart` -> `describe-command`).

### F-2026-02-11-26: With Claude auth valid, LSP availability was confirmed but the first semantic call still timed out under current task shape

Evidence:

- `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/paired-run-summary.json`
- `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/claude-treatment-lsp/transcript.jsonl`
- `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/claude-treatment-lsp/run-metadata.json`

Result:

- In `treatment-lsp`, Claude reported `lsp_tools_available=true` with indicators `tool:LSP` and `plugin:csharp-lsp`.
- First LSP call (`operation=findReferences`) was attempted (`lsp_attempted_calls=1`) and then the run timed out at `180.066s` with no successful edits.
- In the same bundle, Roslyn CLI and Roslyn MCP lanes both passed with clean constraints (`run_passed=true`).

Interpretation:

- This is not a pure "missing LSP setup" failure; LSP was present but operational reliability degraded on the first semantic lookup in this run shape.
- LSP-vs-Roslyn conclusions remain provisional until we have replicate-backed runs on project-backed tasks.

Decision:

- Keep LSP as a mandatory comparator lane.
- Move future LSP comparator bundles to project-backed fixtures and require replicate counts before drawing winner/loser claims.

### F-2026-02-11-27: Paired harness now supports project-backed comparator tasks, reducing loose-file bias against LSP workflows

Evidence:

- `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`-TaskShape single-file|project`)
- `artifacts/real-agent-runs/20260211-codex-roslyn-v5-project/paired-run-summary.json`

Result:

- Added `TaskShape` with `project` mode that scaffolds:
  - `Target.cs`,
  - `Program.cs`,
  - `TargetHarness.csproj`.
- Run metadata now records `task_shape`.
- Codex project-backed control/treatment/treatment-mcp lanes stayed green (`run_passed=true` across all three).

Interpretation:

- Comparator infrastructure can now run in a workspace form that better reflects C# LSP expectations (project context).
- This removes a major fairness confound where LSP was evaluated on a loose file while Roslyn helpers still had sufficient context.

Decision:

- Use `-TaskShape project` as the default for future LSP-vs-Roslyn comparator bundles.
- Keep `single-file` as a diagnostic microtask mode only.

### F-2026-02-11-28: Claude auth volatility can invalidate full bundles; auth preflight is now a hard gate by default

Evidence:

- failed bundle:
  - `artifacts/real-agent-runs/20260211-lsp-roslyn-v5-project/paired-run-summary.json`
- preflight implementation:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`Test-ClaudeAuthentication`, `-FailOnClaudeAuthUnavailable`)
- auth smoke error:
  - `artifacts/real-agent-runs/20260211-auth-preflight-smoke2` (run aborted before lane execution)

Result:

- In `20260211-lsp-roslyn-v5-project`, Claude control completed, but treatment lanes failed with zero-token trajectories due OAuth/authentication errors.
- Added Claude auth preflight before launching Claude lanes; default policy now fails fast with a concrete remediation (`claude /login`).

Interpretation:

- Without auth preflight, a bundle can look partially successful while comparator lanes are silently non-executable.
- Fast-fail gating is required to keep run artifacts promotion-safe.

Decision:

- Keep `FailOnClaudeAuthUnavailable=true` as default.
- Treat zero-token Claude treatment lanes as invalid data for comparative interpretation.

### F-2026-02-11-29: File-scoped Roslyn commands now expose workspace-binding state and default to project semantic context when available

Evidence:

- Implementation:
  - `src/RoslynSkills.Core/Commands/WorkspaceSemanticLoader.cs`
  - `src/RoslynSkills.Core/Commands/CommandFileAnalysis.cs`
  - `src/RoslynSkills.Core/Commands/FindSymbolCommand.cs`
  - `src/RoslynSkills.Core/Commands/GetFileDiagnosticsCommand.cs`
  - `src/RoslynSkills.Cli/CliApplication.cs`
  - `src/RoslynSkills.McpServer/Program.cs`
- Tests:
  - `tests/RoslynSkills.Core.Tests/CommandTests.cs`
  - `tests/RoslynSkills.Cli.Tests/CliApplicationTests.cs`
- Smoke notes:
  - `docs/WORKSPACE_CONTEXT_SMOKE.md`

Result:

- `nav.find_symbol` and `diag.get_file_diagnostics` now include `workspace_context` payloads with:
  - `mode` (`workspace` or `ad_hoc`),
  - resolution source (`auto` or explicit `workspace_path`),
  - resolved workspace/project paths when available,
  - fallback reason and attempted paths when fallback occurs.
- Project-backed smoke calls report `workspace_context.mode=workspace`.
- Loose-file smoke calls report `workspace_context.mode=ad_hoc` with explicit fallback reason.
- CLI preview/summary hints now include workspace mode for both commands.

Interpretation:

- Agents can now detect when a file-level command is operating outside real project context instead of silently trusting ad-hoc diagnostics.
- This directly addresses disconnected-invocation failure mode (file looks broken because project context was not loaded).

Decision:

- Treat `workspace_context.mode=workspace` as the expected state for project-backed `nav.find_symbol` and `diag.get_file_diagnostics` calls.
- Update pit-of-success and paired-run guidance to rerun with explicit workspace binding (`workspace_path`) when mode is `ad_hoc`.

### F-2026-02-12-30: Project-shape paired runs were initially confounded by harness self-collision, now fixed and regression-tested

Evidence:

- harness fix:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`TargetHarness.csproj` now excludes `Target.original.cs`)
- regression test:
  - `tests/RoslynSkills.Benchmark.Tests/PairedRunHarnessScriptTests.cs`
- post-fix clean project bundle:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-project-matrix-v2/paired-run-summary.json`

Result:

- before fix, project-shape runs produced duplicate-type/member errors unrelated to the edit task.
- after fix, codex `control`, `treatment`, and `treatment-mcp` all passed constraint checks in project shape.

Interpretation:

- this was a harness validity bug, not a Roslyn capability issue.
- separating harness defects from tool behavior materially changes interpretation quality.

Decision:

- treat generated fixture compile-surface as part of experiment correctness gates.
- keep explicit test coverage for task-shape project generation.

### F-2026-02-12-31: Paired harness now emits workspace-context mode telemetry that distinguishes workspace-backed vs ad-hoc runs

Evidence:

- metadata/summary instrumentation:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`roslyn_workspace_mode_workspace_count`, `roslyn_workspace_mode_ad_hoc_count`, `roslyn_workspace_mode_last`)
- refreshed project bundle:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-project-matrix-v5/paired-run-summary.json`
- refreshed single-file bundle:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-singlefile-matrix-v4/paired-run-summary.json`

Result:

- codex `treatment-mcp` project shape reports `workspace/ad_hoc = 2/0`.
- codex `treatment-mcp` single-file shape reports `workspace/ad_hoc = 0/2`.

Interpretation:

- workspace-context mode behavior now appears directly in run metadata, reducing transcript-only ambiguity.
- scenario-level context differences (project vs loose file) are now measurable and auditable.

Decision:

- include workspace-mode counters in future promotion/readout tables.
- use `TaskShape=project` as default for context-sensitive comparator claims.

### F-2026-02-12-32: Current cross-scenario approach matrix favors roscli helper as default path, with MCP as explicit-context path

Evidence:

- matrix artifact:
  - `benchmarks/experiments/20260212-approach-matrix-v0.1.6-preview.7.md`
- codex bundles:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-project-matrix-v5/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.7-singlefile-matrix-v4/paired-run-summary.json`
- latest valid Claude comparator with LSP lane:
  - `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/paired-run-summary.json`

Result:

- codex project shape:
  - control: `22.626s`, `34,150` tokens
  - treatment: `35.740s`, `27,246` tokens
  - treatment-mcp: `27.003s`, `66,108` tokens
- codex single-file:
  - control: `19.980s`, `34,037` tokens
  - treatment: `24.718s`, `26,991` tokens
  - treatment-mcp: `35.227s`, `79,416` tokens
- Claude prior LSP comparator (`v4`) kept Roslyn lanes passing, while `treatment-lsp` timed out (`180.066s`, `0/1` successful LSP calls).

Interpretation:

- for this task family, `treatment` (roscli helper) remains the most practical default:
  - consistent pass behavior,
  - lower token totals than control in current codex runs,
  - materially lower token/round-trip overhead than MCP.
- MCP is useful when explicit workspace-mode evidence is required, but still costs more tokens/round-trips.

Decision:

- keep roscli helper path as default treatment baseline.
- use MCP selectively for context assurance/debugging and structured multi-step operations.

### F-2026-02-12-33: Comparator reliability is currently limited by execution-environment issues, not only tool behavior

Evidence:

- current run logs (2026-02-12 bundles) showed Claude auth preflight failures (`401 OAuth token expired`) and skipped Claude lanes.
- prior LSP-enabled bundle still showed first-call timeout despite LSP availability:
  - `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/paired-run-summary.json`

Result:

- fresh codex data is clean and reproducible on current version.
- fresh Claude/LSP data is currently blocked by auth and prior LSP timeout behavior.

Interpretation:

- experimental infrastructure and account/plugin health are still first-order confounds for cross-agent conclusions.

Decision:

- treat Claude auth as a hard precondition for matrix refresh runs.
- rerun full project-backed comparator (`control`, `treatment`, `treatment-mcp`, `treatment-lsp`) after auth recovery before updating architecture-level claims.

### F-2026-02-12-34: Current release snapshot published at `v0.1.6-preview.8` with fresh project matrix artifacts

Evidence:

- release workflow run:
  - https://github.com/DNAKode/RoslynSkills/actions/runs/21934666374
- release/tag:
  - `v0.1.6-preview.8`
- fresh project matrix artifacts:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.8-project-matrix-v4/paired-run-summary.json`
  - `benchmarks/experiments/20260212-approach-matrix-v0.1.6-preview.8.md`

Result:

- release publish succeeded (`NuGet push` + `GitHub Release` assets).
- project matrix lanes passed constraints for `control`, `treatment`, and `treatment-mcp`.

Interpretation:

- current exploration state is now tied to a published release tag plus same-day matrix artifact.

Decision:

- use `v0.1.6-preview.8` as the current baseline reference for follow-up comparator runs.

### F-2026-02-12-35: Workspace telemetry parser had a PowerShell compatibility defect (`ConvertFrom-Json -Depth`) that caused false-zero helper counts

Evidence:

- telemetry parser update:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`Get-RoslynWorkspaceContextUsage`)
- regression coverage:
  - `tests/RoslynSkills.Benchmark.Tests/PairedRunHarnessScriptTests.cs`
- compatibility observation:
  - local PowerShell reported `ConvertFrom-Json` does not support `-Depth`, causing parse failures to be swallowed by best-effort catches.

Result:

- parser now uses compatible `ConvertFrom-Json -ErrorAction Stop` calls and explicit envelope extraction for:
  - direct CLI command outputs (`aggregated_output`),
  - MCP wrapper payloads (`result.content[].text` and nested `contents[].text`).

Interpretation:

- previous helper-lane `workspace/ad_hoc=0/0` values could be instrumentation artifacts when parser paths failed.
- telemetry interpretation must include parser/runtime compatibility checks.

Decision:

- treat transcript parser compatibility as a hard validity gate for workspace-mode telemetry.
- keep MCP lane as primary workspace-mode evidence lane until helper prompts explicitly emit workspace-context-bearing responses consistently.

### F-2026-02-12-36: Updated matrix interpretation separates trajectory policy effects from workspace-binding evidence

Evidence:

- current-version matrix:
  - `benchmarks/experiments/20260212-approach-matrix-v0.1.6-preview.8.md`
- project run summary:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.8-project-matrix-v4/paired-run-summary.json`

Result:

- codex project snapshot (`v0.1.6-preview.8`) shows:
  - control: `20.531s`, `34,112` tokens
  - treatment: `43.119s`, `38,280` tokens
  - treatment-mcp: `32.656s`, `102,195` tokens, workspace `2/0`

Interpretation:

- `treatment` vs `control` overhead in this replicate is trajectory/prompt-behavior dominated, not a direct proof that Roslyn primitives regressed.
- `treatment-mcp` still provides explicit project-context evidence (`workspace=2`, `ad_hoc=0`) on current release.

Decision:

- keep practical default as helper treatment for routine tasks, but use MCP lane when explicit workspace-context evidence is required for claims.
- require replicate-backed interpretation before promoting latency/token conclusions.

### F-2026-02-12-37: Guidance profile is now a dominant trajectory variable on v0.1.6-preview.9

Evidence:

- matrix artifact:
  - `benchmarks/experiments/20260212-approach-matrix-v0.1.6-preview.9.md`
- paired summaries (project + single-file profile sweeps):
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-project-*/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-*/paired-run-summary.json`

Result:

- project shape:
  - `surgical+treatment`: `26,932` tokens vs control `34,875` (lower tokens, moderate duration overhead).
  - `schema-first+treatment`: `89,629` tokens and `77.249s` (large overhead).
  - `skill-minimal+treatment`: `75,420` tokens and `70.301s` (large overhead).
- single-file shape:
  - `brief-first/surgical+treatment`: `~27k` tokens (lower than control `~34k`).
  - `schema-first/skill-minimal+treatment`: `64k-98k` tokens with large duration overhead.

Interpretation:

- prompt posture is not a cosmetic variable; it materially changes tool call count, retries, and token/latency envelope.
- `brief-first` and `surgical` are currently the strongest defaults; `schema-first` and `skill-minimal` should be reserved for debugging/contract validation.

Decision:

- keep `brief-first`/`surgical` as primary optimization lanes.
- treat `schema-first`/`skill-minimal` as explicit stress lanes, not default guidance.

### F-2026-02-12-38: Workspace telemetry for helper lanes is now empirically improved after parser hardening

Evidence:

- parser update:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1`
- regression assertions:
  - `tests/RoslynSkills.Benchmark.Tests/PairedRunHarnessScriptTests.cs`
- before/after run artifacts:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-schema-first-v1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-singlefile-schema-first-telemetryfix-v2/paired-run-summary.json`

Result:

- before fix (`schema-first`, single-file, treatment): workspace counters `0/0` despite transcript outputs showing `workspace=ad_hoc`.
- after fix (`schema-first`, single-file, treatment): workspace counters `0/2`, aligning with observed ad-hoc workspace responses.

Interpretation:

- previous zero workspace counters in helper lanes were sometimes instrumentation artifacts, not tool-behavior evidence.
- this reduces confounding between telemetry defects and Roslyn workspace semantics in analysis.

Decision:

- treat post-fix counters as the current baseline for workspace-mode analysis.
- re-run key historical bundles when strict longitudinal comparability is required.

### F-2026-02-12-39: LSP lane remains an environment-availability confound in current-day matrixing

Evidence:

- current all-approach codex bundle:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-project-all-approaches-v1/paired-run-summary.json`
- latest valid LSP snapshot:
  - `artifacts/real-agent-runs/20260211-lsp-roslyn-v4/paired-run-summary.json`

Result:

- same-day (`2026-02-12`) matrix contains codex `control/treatment/treatment-mcp` lanes.
- freshest valid `treatment-lsp` evidence is still prior-day Claude snapshot, where `treatment-lsp` failed (`run_passed=false`, `lsp calls 0/1`).

Interpretation:

- LSP comparator conclusions are currently bottlenecked by environment/auth/tool-availability stability, not just task quality.

Decision:

- keep LSP rows in the matrix with explicit freshness/confound labeling.
- require fresh auth-preflight-passing project-shape replicates before updating Roslyn-vs-LSP claims.


### F-2026-02-12-40: Codex LSP-over-MCP lane is now executable with `csharp-ls` via `cclsp`

Evidence:

- harness/script updates:
  - `benchmarks/scripts/Run-CodexMcpInteropExperiments.ps1`
  - `tests/RoslynSkills.Benchmark.Tests/CodexMcpInteropScriptTests.cs`
- successful Codex LSP MCP runs:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v6b-cclsp-duration/codex-mcp-interop-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v9-codex-lsp-medium-xhigh/codex-mcp-interop-summary.json`

Result:

- `lsp-mcp` passed in Codex across `low/high/medium/xhigh` effort settings.
- LSP call counters were non-zero on all successful LSP lanes (`2-3` calls depending on lane/profile).

Interpretation:

- previous LSP gaps were primarily MCP-bridge/configuration availability issues, not an inherent inability for Codex to run LSP-backed MCP workflows.

Decision:

- keep `cclsp` as the default practical bridge for Codex LSP comparator lanes in current experiments.

### F-2026-02-12-41: Spark model availability changed in-session; current account context now supports Spark runs

Evidence:

- earlier unsupported snapshot:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v1/codex-mcp-interop-summary.json`
- successful Spark smoke + full matrix:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v7-spark-cclsp-duration-smoke/codex-mcp-interop-summary.json`
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v8-spark-cclsp-duration/codex-mcp-interop-summary.json`

Result:

- Spark now passes `control`, `roslyn-mcp`, `lsp-mcp`, and `roslyn-plus-lsp-mcp` lanes at `low` and `high` efforts.

Interpretation:

- model-access status is temporally unstable and can confound interpretation if not revalidated before each matrix sweep.

Decision:

- treat model availability preflight as mandatory for cross-model comparator runs.

### F-2026-02-12-42: For this project-backed rename microtask, `lsp-mcp` is currently the strongest semantic-cost lane

Evidence:

- Codex `low/high` all-scenario matrix:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v6b-cclsp-duration/codex-mcp-interop-summary.json`
- Codex `medium/xhigh` extension:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v9-codex-lsp-medium-xhigh/codex-mcp-interop-summary.json`
- Spark `low/high` all-scenario matrix:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v8-spark-cclsp-duration/codex-mcp-interop-summary.json`

Result:

- Codex low effort:
  - control: `43.424s`, `64,343` tokens
  - lsp-mcp: `49.926s`, `63,765` tokens
  - roslyn-mcp: `49.120s`, `123,739` tokens
- Spark high effort:
  - control: `70.439s`, `87,229` tokens
  - lsp-mcp: `67.187s`, `88,308` tokens
  - roslyn-mcp: `107.873s`, `224,090` tokens

Interpretation:

- semantic tooling does not need to imply Roslyn-heavy call volume on this task family; LSP MCP achieved much closer control-like cost than Roslyn MCP while preserving semantic operations.
- combined lane can be fast in isolated runs but still shows elevated token usage versus control/LSP-only.

Decision:

- keep `lsp-mcp` and `roslyn-plus-lsp-mcp` as active optimization lanes, and treat `roslyn-mcp` as precision-heavy but currently high-overhead on microtasks.

### F-2026-02-12-43: One transient harness failure (`Invoke-CodexRun` process-start) was observed and not reproduced immediately

Evidence:

- transient failure during first duration-enabled run:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v6-cclsp-duration`
- immediate successful rerun:
  - `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v6b-cclsp-duration/codex-mcp-interop-summary.json`

Result:

- initial run aborted mid-sweep with process start error.
- rerun completed full matrix without recurrence.

Interpretation:

- this is currently an execution-environment transient, not yet a deterministic harness defect.

Decision:

- record as an open disentangle item and only promote it to backlog defect status if reproducible in subsequent sweeps.

### F-2026-02-12-44: Roscli helper lane now emits workspace context directly, and workspace counters are no longer false-zero on project tasks

Evidence:

- helper output + parser updates:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1`
  - `tests/RoslynSkills.Benchmark.Tests/PairedRunHarnessScriptTests.cs`
- post-fix project bundles:
  - `artifacts/real-agent-runs/20260212-roscli-vs-mcp-workspaceguard-v3-surgical/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-roscli-vs-mcp-workspaceguard-v3-brief-first/paired-run-summary.json`

Result:

- `treatment` (roscli helper lane) now reports workspace counts as expected on project-shape runs (`workspace/ad_hoc = 1/0`) instead of prior `0/0` false-zero under helper-driven runs.

Interpretation:

- workspace-mode telemetry is now suitable for comparing roscli and MCP workspace binding behavior in the same matrix.

Decision:

- treat post-fix (`workspaceguard-v3+`) bundles as the baseline for workspace-context interpretation.

### F-2026-02-12-45: MCP prompt tightening reduced overhead materially, but roscli remains lower-cost on this microtask

Evidence:

- MCP guidance updates (skip pre-rename nav when line/column is already known):
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1`
- before/after bundles:
  - `artifacts/real-agent-runs/20260212-roscli-vs-mcp-workspaceguard-v3-brief-first/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260212-roscli-vs-mcp-workspaceguard-v4-brief-first/paired-run-summary.json`
- consolidated readout:
  - `benchmarks/experiments/20260212-roscli-vs-mcp-workspace-context-v0.1.6-preview.9.md`

Result:

- MCP `brief-first` moved from `80,374` tokens / `5` round trips (`3` Roslyn calls) to `64,901` tokens / `4` round trips (`2` Roslyn calls).
- prompt-only change produced `-15,473` tokens (`-19.3%`) and one fewer round trip.
- best MCP snapshot in this run family (`64,714` tokens, `41.932s`) still trails best roscli lane (`29,004` tokens, `37.319s`) on this microtask.

Interpretation:

- prompting is a first-order variable for MCP overhead, but current MCP transport/surface still carries substantial fixed+per-call token cost relative to roscli on small, anchored edits.

Decision:

- keep MCP optimization active, but maintain roscli as default practical lane for microtasks where line/column anchors are already known.

### F-2026-02-12-46: Lightweight real-run harness had a Roscli project-identity defect on historical workspaces (fixed)

Evidence:

- failure mode observed in treatment lane transcripts under baseline commit workspaces containing `src/RoslynAgent.Cli` while generated shim expected `src/RoslynSkills.Cli`.
- fixes:
  - `benchmarks/scripts/Run-LightweightUtilityGameRealRuns.ps1`
  - `scripts/roscli`
  - `scripts/roscli.cmd`

Result:

- pre-fix treatment runs could fail Roscli invocation with path-not-found and drift into non-Roslyn behavior.
- shim and launcher resolution now probes both identities (`RoslynSkills.Cli` and `RoslynAgent.Cli`) and selects the one present.

Interpretation:

- a large share of “treatment underperformed” evidence in historical-workspace lanes can be experiment plumbing, not tool-surface quality.

Decision:

- treat pre-fix lightweight treatment runs as conditionally contaminated unless transcript confirms successful Roscli call paths.
- require project-identity self-check in future harness preflight for baseline-commit workflows.

### F-2026-02-12-47: Paired Codex/Spark matrix still favors Roscli over Roslyn-MCP on token efficiency for anchored edits

Evidence:

- `benchmarks/experiments/20260212-codex-approach-matrix-v0.1.6-preview.9.md`
- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-codex-project/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-codex-singlefile/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-spark-project/paired-run-summary.json`
- `artifacts/real-agent-runs/20260212-model-taskshape-roscli-mcp-v1-spark-singlefile/paired-run-summary.json`

Result:

- Codex project: roscli `29,235` vs MCP `64,893` tokens.
- Codex single-file: roscli `28,170` vs MCP `63,732` tokens.
- Spark project: roscli `29,756` vs MCP `39,644` tokens.
- Spark single-file: roscli `19,836` vs MCP `52,882` tokens.

Interpretation:

- Roscli remains the lower-overhead semantic lane on this task family, even where MCP can occasionally be faster on wall-clock.

Decision:

- keep Roscli as default Codex lane for anchored rename/edit flows.
- continue MCP optimization work, but gate default-lane promotion on replicate-backed token parity or better.

### F-2026-02-12-48: MCP scenario sweeps show Roslyn-MCP is consistently the highest-cost MCP arm in Codex/Spark low-effort settings

Evidence:

- `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v6b-cclsp-duration/codex-mcp-interop-summary.json`
- `artifacts/real-agent-runs/20260212-v0.1.6-preview.9-codex-mcp-interop-v8-spark-cclsp-duration/codex-mcp-interop-summary.json`
- matrix rollup: `benchmarks/experiments/20260212-codex-approach-matrix-v0.1.6-preview.9.md`

Result (low effort):

- Codex: control `64,343`, LSP-MCP `63,765`, Roslyn-MCP `123,739`, combined `89,019` tokens.
- Spark: control `51,309`, LSP-MCP `74,626`, Roslyn-MCP `149,819`, combined `92,710` tokens.

Interpretation:

- for this microtask family, Roslyn-MCP is a precision-heavy lane with substantial token overhead.
- combined Roslyn+LSP can reduce that overhead versus Roslyn-only, but still trails control and often trails LSP-only.

Decision:

- keep Roslyn-MCP as a specialized lane for ambiguity-heavy operations where semantic precision is expected to amortize overhead.
- prioritize LSP-only and combined-lane prompt/runtime tuning for broader MCP viability.

### F-2026-02-12-49: Open-ended larger-task comparisons are still confounded by trajectory variance and need bounded-run protocol

Evidence:

- `artifacts/real-agent-runs/20260212-revisionsize-codex-v2/runs/run-codex-control-control-text-only-task-001-initial-build-r01.json`
- `artifacts/real-agent-runs/20260212-revisionsize-codex-v3-task001/runs/run-codex-control-control-text-only-task-001-initial-build-r01.json`
- `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/runs/run-codex-treatment-treatment-roslyn-optional-task-001-initial-build-r01.json`
- `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/runs/run-codex-treatment-treatment-roslyn-published-cache-task-001-initial-build-r01.json`

Result:

- control `task-001` moved from `417,091` to `996,811` tokens across runs.
- treatment optional vs published-cache differed by `573,077` tokens in the same task family.

Interpretation:

- uncontrolled exploration/planning breadth can dominate token deltas and obscure lane-level effects.

Decision:

- introduce bounded-run large-task protocol (step caps, early acceptance-check checkpoint, required sequencing) before claiming Roscli vs MCP vs control outcomes on open-ended tasks.
## Token-to-Information Efficiency (Proxy Metrics)

Current telemetry allows two practical proxies:

1. `tokens_per_1k_command_output_chars`  
Definition: `total_tokens / (command_output_chars / 1000)`  
Interpretation: token cost paid per 1k chars of tool-returned information.

2. `tokens_per_round_trip`  
Definition: `total_tokens / command_round_trips`  
Interpretation: token cost per tool interaction loop.

v4 snapshot:

| Agent | Condition | Tokens | Command output chars | Round trips | Tokens / 1k output chars | Tokens / round trip |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `codex-cli` | `control-text-only` | 272,660 | 44,619 | 15 | 6,110.85 | 18,177.33 |
| `codex-cli` | `treatment-roslyn-optional` | 1,415,261 | 113,375 | 53 | 12,483.01 | 26,703.04 |
| `claude-code` | `control-text-only` | 7,737 | 21,825 | 13 | 354.50 | 595.15 |
| `claude-code` | `treatment-roslyn-optional` | 12,327 | 70,301 | 14 | 175.35 | 880.50 |

Early read:

- Codex treatment currently has worse proxy efficiency than Codex control on both metrics.
- Claude treatment has better token-per-output-char but worse token-per-round-trip; this suggests richer outputs but not fewer loops.

## Next Measurement Upgrades

To better optimize "tokens per information rate", add these fields per run:

- `accepted_constraints_count`: count of acceptance-check clauses satisfied.
- `constraint_weighted_score`: weighted task-completion value.
- `roslyn_result_chars_used`: subset of command output chars actually referenced in agent reasoning/final summary.
- `retry_count_by_command`: repeated calls to same command+target with no net progress.
- `information_efficiency_score`: `constraint_weighted_score / total_tokens`.

Proposed decision rule for default guidance changes:

- require non-inferior correctness plus at least one improved efficiency metric (`tokens_per_1k_command_output_chars`, `tokens_per_round_trip`, or `information_efficiency_score`) across replicate bundles.

## Entry Template

Use this for new findings:

```md
### F-YYYY-MM-DD-XX: <short claim>
Evidence:
- <artifact paths>
Result:
- <key numbers>
Interpretation:
- <what this means>
Decision:
- <what changes now>
```


### F-2026-02-12-50: Codex Spark roscli treatment reduced tokens and round-trips vs control on task-001 (1 replicate)

Evidence:
- `artifacts/real-agent-runs/20260212-roscli-vs-base-task001-spark-low-brief/gate/summary/agent-eval-summary.md`
- `artifacts/real-agent-runs/20260212-roscli-vs-base-task001-spark-low-brief/runs/run-codex-control-control-text-only-task-001-initial-build-r01.json`
- `artifacts/real-agent-runs/20260212-roscli-vs-base-task001-spark-low-brief/runs/run-codex-treatment-treatment-roslyn-published-cache-task-001-initial-build-r01.json`

Result:
- tokens: control `830,221` vs treatment `640,223` (delta `-189,998`, `-22.89%`).
- command round trips: control `45` vs treatment `24`.
- success/tests: both passed.

Interpretation:
- even on an open-ended build task (high variance), offering roscli correlated with fewer loops and lower token spend in this replicate.

Decision:
- update treatment prompt guidance to start with compact `list-commands --ids-only` (avoid verbose dumps);
- continue replicate runs with thinking-effort variation and explicitly measure how many successful Roslyn calls happen before editing.

### F-2026-02-12-51: LSP MCP lane via csharp-ls remains high-overhead and failure-prone (timeouts + weak diagnostics)

Evidence:
- `artifacts/real-agent-runs/20260212-mcp-interop-lsp-sanity/lsp-mcp-gpt-5.3-codex-spark-low/transcript.jsonl`
- `artifacts/real-agent-runs/20260212-mcp-interop-lsp-sanity/codex-mcp-interop-summary.md`
- `artifacts/real-agent-runs/20260212-mcp-interop-lsp-sanity2/codex-mcp-interop-summary.md`

Result:
- `gpt-5.3-codex-spark` (project shape, lsp-mcp): runs were manually terminated after ~5 minutes with `lsp_calls=5..6` and `total_tokens=63k..93k`.
- observed repeated `textDocument/references` timeouts; no pushed diagnostics observed.

Interpretation:
- current LSP lane is confounded by server indexing/restore latency and unreliable diagnostics surfaces; it is not yet a competitive baseline against roscli for workspace-semantics.

Decision:
- treat LSP MCP as experimental only; require explicit warm-up and long timeouts, and avoid using it as primary evidence for overall tool effectiveness until stabilized.

### F-2026-02-13-52: Roscli workspace binding works on a large OSS repo (Avalonia) when commands are run inside the repo and `require_workspace=true`

Evidence:
- `artifacts/real-agent-runs/20260213-080511-oss-csharp-pilot/avalonia-cornerradius-tryparse/treatment-roslyn-optional/run-codex-treatment-roslyn-optional-avalonia-cornerradius-tryparse-brief-first-r01/transcript.jsonl`
- `benchmarks/experiments/oss-csharp-pilot-v1/runs/run-codex-treatment-roslyn-optional-avalonia-cornerradius-tryparse-brief-first-r01.json`

Result:
- `nav.find_symbol` returned `workspace_context.mode=workspace` and resolved to `src/Avalonia.Base/Avalonia.Base.csproj`.
- run outcome: `succeeded=true`, `duration_seconds=132.602`, `total_tokens=273,439` (Codex Spark, low reasoning).

Interpretation:
- This directly addresses the "false error floods" failure mode: when a file belongs to a project, RoslynSkills can bind to the correct `.csproj` workspace and report symbol/diagnostic results in that context.

Decision:
- Keep `workspace_context` surfaced on nav/diag commands and treat `require_workspace=true` as the pit-of-success default for project code.

### F-2026-02-13-53: Paired (project-shape) rename shows Roslyn tooling overhead on low-ambiguity tasks; roscli is cheaper than Roslyn MCP here

Evidence:
- `artifacts/real-agent-runs/20260213-081019-paired/paired-run-summary.md`
- `artifacts/real-agent-runs/20260213-081019-paired/paired-run-summary.json`

Result (Codex Spark, project task shape):
- control: `model_total_tokens=26,630`, passed.
- roscli treatment: `model_total_tokens=45,546`, passed.
- MCP treatment: `model_total_tokens=55,694`, passed.

Interpretation:
- For simple edits with unambiguous targets, tool usage increases tokens/round-trips without improving correctness.
- Between Roslyn-enabled lanes, `roscli` was lower overhead than MCP on this task.

Decision:
- Update guidance to use Roslyn calls sparingly and "brief-first": one targeted call for ambiguity, then edit, then one verification call.
- Keep MCP lane for warm-call latency experiments and multi-step/ambiguity-heavy tasks, not as default for micro-edits.

### F-2026-02-13-54: LSP via MCP remains non-viable in Codex in current configuration (timeouts)

Evidence:
- `artifacts/real-agent-runs/20260213-mcp-interop-workspace-v4/codex-mcp-interop-summary.md`

Result:
- `lsp-mcp` and `roslyn-plus-lsp-mcp` timed out at ~180s across multiple model/effort cells.
- `roslyn-mcp` succeeded but had significantly higher token totals than control in this microtask.

Interpretation:
- LSP MCP stability/perf is currently confounded by initialization/indexing latency and/or tool-call prompting; treat as experimental until we can get consistent completion.

Decision:
- Keep LSP MCP in the matrix but exclude it from "best approach" claims until it can reliably complete bounded tasks.


### F-2026-02-13-55: Workspace mode can be false-positive if MSBuildLocator registers an incompatible MSBuild instance (CS0518 core types)

Observation:

- A run can report `workspace_context.mode=workspace` while still producing unusable diagnostics (`CS0518` for predefined/core types like `System.String`).
- Root cause: MSBuildWorkspace can load a project with missing reference assemblies when MSBuildLocator binds to a VS MSBuild instance behind preview TFMs (for example `net10.0`).

Evidence:

- `artifacts/real-agent-runs/20260213-092429-paired/codex-treatment/transcript.jsonl` (UTF-16LE) contains `diag.get_file_diagnostics` output with `CS0518`.
- `artifacts/real-agent-runs/20260213-092429-paired/paired-run-summary.md` shows the run completed but was diagnostic-noisy.

Fix:

- `WorkspaceSemanticLoader.TryEnsureMsBuildRegistered` now prefers `.NET SDK` MSBuild (`DiscoveryType.DotNetSdk`) over VS instances.
- Post-fix check: running `diag.get_file_diagnostics` against the same `TargetHarness.csproj` now returns `errors=0` with `workspace=workspace`.

Rule update:

- Treat `workspace_context.mode=workspace` as necessary but not sufficient for project-shaped benchmark validity; additionally fail/redo if diagnostics include `CS0518` or workspace diagnostics indicate MSBuild evaluation failure.


### F-2026-02-13-56: Fail-closed treatment integrity + better prompts can reduce roscli token overhead on microtasks

Evidence:
- `artifacts/real-agent-runs/20260213-094724-paired/paired-run-summary.md`

Result (Codex Spark, project task shape, `brief-first-v2`, fail-closed):
- control: `total_tokens=26,057`, `duration_seconds=7.295`, passed.
- roscli treatment: `total_tokens=30,162`, `duration_seconds=36.831`, passed.
- token delta: `+4,105` (ratio `1.158`).

Interpretation:
- With prompts that (a) explicitly require a Roslyn attempt and (b) keep the command sequence minimal, roscli overhead can be brought down near parity on a trivial edit.
- Duration is still sensitive to wrapper/tool invocation latency (one timed-out helper attempt occurred before a successful retry).

Decision:
- Keep `-FailOnMissingTreatmentRoslynUsage` available and recommended for paired runs used as comparative evidence.
- Continue optimizing the helper path (timeouts/warm reuse) and test Spark vs non-Spark at low/high reasoning to see if duration improves without token regression.


### F-2026-02-13-57: Treatment token impact can invert depending on trajectory variance; keep runs bounded and fail-closed on condition integrity

Evidence:
- `artifacts/real-agent-runs/20260213-094929-paired/paired-run-summary.md`

Result (Codex, high reasoning, project task shape, `brief-first-v2`):
- control: `total_tokens=40,452`, `duration_seconds=17.566`.
- roscli treatment: `total_tokens=34,733`, `duration_seconds=33.744`.

Interpretation:
- The direction of the token delta is not stable on microtasks because agents may take divergent high-output trajectories (e.g. directory listings / raw dumps) in control.
- This reinforces the need to: (a) keep prompts tightly bounded, (b) record round-trips/output chars, and (c) use multiple replicates before drawing token conclusions.

Decision:
- For microtasks, treat token totals as noisy; emphasize reliability/condition integrity (fail-closed on missing tool usage) and shift primary evidence to ambiguity-heavy and multi-file tasks.


### F-2026-02-13-58: New paired replicates reconfirm microtask overhead sensitivity (and MCP token overhead)

Evidence:
- `artifacts/real-agent-runs/20260213-103432-paired/paired-run-summary.md`
- `artifacts/real-agent-runs/20260213-103545-paired/paired-run-summary.md`

Results (Codex Spark, project task shape, `brief-first-v2`):
- low reasoning replicate (`20260213-103432-paired`):
  - control: `total_tokens=25,901`, `duration_seconds=6.007`.
  - roscli treatment: `total_tokens=20,009`, `duration_seconds=19.023`.
  - MCP treatment: `total_tokens=55,191`, `duration_seconds=13.573`.
- high reasoning replicate (`20260213-103545-paired`):
  - control: `total_tokens=26,602`, `duration_seconds=6.688`.
  - roscli treatment: `total_tokens=54,330`, `duration_seconds=38.647`.

Interpretation:
- Token deltas can invert on trivial tasks due to divergent control trajectories, but duration overhead remains consistently higher when tools are invoked (wrapper latency dominates).
- MCP remains materially higher token overhead than roscli on this microtask family.

Decision:
- Keep microtasks as a diagnostic for overhead sources, but prioritize ambiguity-heavy and multi-step tasks for primary effectiveness claims.


### F-2026-02-13-59: OSS pilot now supports a treatment-required lane and a MediatR run passed with workspace binding evidence

Evidence:
- manifest updated with `treatment-roslyn-required`: `benchmarks/experiments/oss-csharp-pilot-v1/manifest.json`
- harness updated to fail-closed on missing required Roslyn usage/workspace evidence: `benchmarks/scripts/Run-OssCsharpPilotRealRuns.ps1`
- run record:
  - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-110716-oss-csharp-pilot/run-codex-treatment-roslyn-required-mediatr-behavior-targeting-brief-first-r01.json`

Result (Codex Spark, low reasoning, `mediatr-behavior-targeting`, treatment-required):
- `succeeded=true`, `duration_seconds=66.808`, `total_tokens=766,072`.
- `roslyn_used=true`, `roslyn_successful_calls=2`, `roslyn_workspace_mode_last=workspace`.

Interpretation:
- This removes the prior OSS confound where “treatment” completed with zero Roslyn tool calls.
- Large-repo lanes can still generate very high token totals; strict prompt/trajectory bounding remains critical.

Decision:
- Use `treatment-roslyn-required` (or equivalent fail-closed integrity) for OSS treatment evidence going forward.


### F-2026-02-13-60: A tighter tool prompt (brief-first-v4) reliably induces a 2-call roscli workflow with bounded output

Evidence:
- `artifacts/real-agent-runs/20260213-113738-paired/paired-run-summary.md` (low reasoning)
- `artifacts/real-agent-runs/20260213-113818-paired/paired-run-summary.md` (medium reasoning)
- `artifacts/real-agent-runs/20260213-113904-paired/paired-run-summary.md` (high reasoning)

Result (Codex Spark, project task shape, `brief-first-v4`):
- treatment runs consistently executed exactly the intended minimal roscli sequence:
  - `edit.rename_symbol` (workspace-bound) then `diag.get_file_diagnostics` (workspace-bound)
  - `roslyn_successful_calls=2` with `workspace_context.mode=workspace`
- treatment responses were constrained to the requested two-line format (reduced output verbosity).

Interpretation:
- Prompt posture is a controllable variable that can reliably reduce “exploration churn” and verbosity in Roslyn-enabled lanes, without sacrificing correctness on this microtask.
- Even with tighter prompting, tool invocation still increases wall-clock time vs control on trivial edits; the primary value hypothesis remains in ambiguity-heavy and multi-step tasks.

Decision:
- Keep `brief-first-v4` as the default “tight” tool-prompt variant for future paired microtask sweeps, and use it as a baseline when optimizing transport latency and wrapper overhead.


### F-2026-02-13-61: OSS pilot scope expansion requires explicit primary comparison selection and schema-aligned run records

Evidence:
- manifest primary-comparison selection:
  - `benchmarks/experiments/oss-csharp-pilot-v1/manifest.json` (`primary_control_condition_id`, `primary_treatment_condition_id`)
- scorer support for multi-condition experiments:
  - `src/RoslynSkills.Benchmark/AgentEval/AgentEvalModels.cs`
  - `src/RoslynSkills.Benchmark/AgentEval/AgentEvalValidation.cs`
  - `src/RoslynSkills.Benchmark/AgentEval/AgentEvalScorer.cs`
- OSS harness run-record shaping + backfill:
  - `benchmarks/scripts/Run-OssCsharpPilotRealRuns.ps1` (writes `tools_offered` + `tool_calls`)
  - `benchmarks/scripts/Upgrade-OssPilotRunRecords.ps1` (backfills older OSS runs)
- scope-expanded run bundle + gate:
  - run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-142746-oss-csharp-pilot/*.json`
  - gate summary: `artifacts/agent-eval/20260213-145801/summary/agent-eval-summary.md`

Result (Codex Spark, low, `brief-first-v4`, treatment-required primary):
- `mediatr-openbehavior-nullguard`:
  - control: `duration_seconds=36.392`, `total_tokens=148,327`, passed.
  - treatment-required: `duration_seconds=170.898`, `total_tokens=780,384`, passed; workspace-bound Roslyn calls.
- `serilog-structured-context-change`:
  - control: `duration_seconds=32.452`, `total_tokens=216,336`, passed.
  - treatment-required: `duration_seconds=49.318`, `total_tokens=179,588`, passed; workspace-bound Roslyn calls.

Interpretation:
- Once the OSS pilot included multiple Roslyn-enabled conditions (optional + required), the benchmark scorer needed an explicit definition of which control/treatment pair is the primary comparison; otherwise the gate would report insufficient data even when the intended pair was present.
- Without emitting schema-aligned `tools_offered`/`tool_calls`, OSS run records could not be validated/scored by the shared `agent-eval-*` pipeline (a hard failure mode); backfilling run records restores end-to-end gate usability.

Decision:
- Keep `treatment-roslyn-required` as the primary treatment condition for the OSS pilot until optional-vs-required is separately replicate-backed.
- Continue adding concrete, file-scoped OSS tasks to reduce open-ended exploration churn, and add replicate coverage before interpreting token deltas.


### F-2026-02-13-62: Brief-first-v5 reduced MediatR treatment token blowup, but required task prompt tightening to avoid TFM traps

Evidence:
- failed first attempt (prompt ambiguity + TFM issue):
  - `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-150618-oss-csharp-pilot/*.json`
  - acceptance log showing `ArgumentNullException.ThrowIfNull` missing for `netstandard2.0`:
    - `artifacts/real-agent-runs/20260213-150618-oss-csharp-pilot/mediatr-openbehavior-nullguard/control-text-only/run-codex-control-text-only-mediatr-openbehavior-nullguard-brief-first-v5-r01/acceptance.log`
- prompt tightened to forbid `ThrowIfNull` and to test `new OpenBehavior(null!, ...)` directly:
  - `benchmarks/experiments/oss-csharp-pilot-v1/prompts/mediatr-openbehavior-nullguard.md`
- passing rerun:
  - run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-151544-oss-csharp-pilot/*.json`
  - gate summary: `artifacts/agent-eval/20260213-151923/summary/agent-eval-summary.md`

Result (Codex Spark, low, `brief-first-v5`, task `mediatr-openbehavior-nullguard`):
- control: `duration_seconds=30.249`, `total_tokens=147,108`, passed.
- treatment-required: `duration_seconds=83.858`, `total_tokens=371,344`, passed, `roslyn_successful_calls=4`, `workspace_mode_last=workspace`.

Interpretation:
- The tighter v5 guidance correlated with fewer Roslyn calls (4 successful calls vs 9 in the earlier v4 passing run) and substantially lower treatment token spend compared to the earlier v4 MediatR run (`780,384` tokens), but treatment still cost more tokens/time than control in this replicate.
- Task prompts for OSS work must be TFM-aware: recommending APIs unavailable in some target frameworks (like `ThrowIfNull`) creates false failures unrelated to Roslyn/tool value.

Decision:
- Keep `brief-first-v5` available for “token clamp” experiments, but treat task-prompt quality (TFM correctness + exact test target) as a first-class experimental variable in OSS lanes.

### F-2026-02-13-63: Claude skill-style tight guidance can cut Roslyn overhead (fewer round-trips, lower tokens) on a microtask

Evidence:
- baseline Claude paired run (brief-first-v4):
  - `artifacts/real-agent-runs/20260213-claude-brief-first-v4-paired-r1/paired-run-summary.md`
  - `artifacts/real-agent-runs/20260213-claude-brief-first-v4-paired-r1/paired-run-summary.json`
- tight skill-style Claude paired run (skill-tight-v1, fixed to use `bash scripts/roscli`):
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-paired-r2/paired-run-summary.md`
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-paired-r2/paired-run-summary.json`
- guidance implementation:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`RoslynGuidanceProfile=skill-tight-v1`)

Result (Claude, task shape `project`, both passed constraint checks):
- brief-first-v4:
  - control: `duration_seconds=16.150`, `total_tokens=553`, `round_trips=3`
  - treatment: `duration_seconds=36.615`, `total_tokens=883`, `round_trips=4`, `roslyn_successful_calls=2/3`
  - delta: `+330` tokens, `+1` round trip, `+20.465s`
- skill-tight-v1:
  - control: `duration_seconds=17.333`, `total_tokens=554`, `round_trips=3`
  - treatment: `duration_seconds=21.940`, `total_tokens=272`, `round_trips=2`, `roslyn_successful_calls=2/2`
  - delta: `-282` tokens, `-1` round trip, `+4.607s`

Interpretation:
- A skill-style prompt (explicit call budget + progressive disclosure + environment-correct invocation + hard output clamp) can materially reduce tool churn and token overhead for Claude in Roslyn-enabled lanes, even on a trivial task.
- Wall-clock time still increased versus control (tool invocation latency dominates), but the delta was substantially smaller in the tight profile.

Decision:
- Use `skill-tight-v1` as the default Claude treatment profile for subsequent paired runs, and carry its structure forward when expanding benchmark scope to ambiguity-heavy/multi-file tasks.

### F-2026-02-15-64: Ambiguity-heavy overload rename tasks show mixed token deltas under skill-tight-v1; elapsed overhead persists

Evidence:
- Per-replicate paired summaries:
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-classes-r1b/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-classes-r2/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-nested-r1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-nested-r2/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-generic-r1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260213-claude-skill-tight-v1-generic-r2/paired-run-summary.json`
- Aggregate rollup:
  - `artifacts/real-agent-runs/20260215-aggregate-skill-tight-v1-summary.md`
  - `artifacts/real-agent-runs/20260215-aggregate-skill-tight-v1-summary.json`
- Harness inputs:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`-TaskId rename-overload-collision-classes-v1|rename-overload-collision-nested-v1|rename-overload-collision-generic-v1`, `-RoslynGuidanceProfile skill-tight-v1`, `-FailOnMissingTreatmentRoslynUsage`)

Result (Claude-only, 3 tasks x 2 replicates, all runs passed constraint checks, treatment required Roslyn usage):

| Task | n | Avg delta tokens (treat-control) | Avg delta seconds | Avg delta round-trips | Token deltas |
| --- | ---: | ---: | ---: | ---: | --- |
| `rename-overload-collision-classes-v1` | 2 | `-101.0` | `+14.623` | `+0.000` | `-87, -115` |
| `rename-overload-collision-generic-v1` | 2 | `+48.5` | `+14.056` | `+0.000` | `-13, +110` |
| `rename-overload-collision-nested-v1` | 2 | `+183.0` | `+21.434` | `+1.000` | `+155, +211` |

Overall averages across all 6 replicates:
- `avg_delta_tokens=+43.5`
- `avg_delta_seconds=+16.704`
- `avg_delta_round_trips=+0.333`

Interpretation:
- `skill-tight-v1` keeps the treatment trajectory predictable (3 successful Roslyn calls, workspace-bound) and can be token-competitive on some ambiguity tasks (class-collision wins consistently; generic is mixed), but nested-scope ambiguity still drives extra round-trips and token overhead.
- Wall-clock remains worse for treatment on every replicate in this task family; tool invocation latency dominates at this scale.

Decision:
- Keep `skill-tight-v1` as the default Claude treatment posture for ambiguity-heavy overload tasks, but treat “nested ambiguity” as a target for command-surface improvement: reduce the required call budget (ideally 2 calls) by allowing rename targeting by signature/containing type without a preflight `nav.find_symbol`.

### F-2026-02-15-65: skill-tight-v2 (skip explicit diag when edit returns diagnostics) cuts nested-task overhead (2 Roslyn calls, fewer round-trips, lower tokens)

Evidence:
- New paired run (nested ambiguity, Claude-only):
  - `artifacts/real-agent-runs/20260215-claude-skill-tight-v2-nested-r1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260215-claude-skill-tight-v2-nested-r1/paired-run-summary.md`
- Guidance implementation:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`RoslynGuidanceProfile=skill-tight-v2`)

Result (`rename-overload-collision-nested-v1`, both passed constraint checks, treatment required Roslyn usage):
- control: `total_tokens=503`, `round_trips=2`, `duration_seconds=16.034`
- treatment (skill-tight-v2): `total_tokens=457`, `round_trips=2`, `duration_seconds=25.534`, `roslyn_successful_calls=2`
- delta: `-46` tokens, `+0` round-trips, `+9.500s`

Interpretation:
- The prior `skill-tight-v1` “always run diag” rule was unnecessary for this task family because `edit.rename_symbol` already reports `diagnostics_after_edit` in its response (0/0 here). Allowing the agent to stop after `nav.find_symbol` + `edit.rename_symbol` removes one Roslyn call and eliminates the nested-task round-trip regression, while improving token efficiency.

Decision:
- Promote `skill-tight-v2` as the default Claude treatment profile for paired microtasks where `edit.*` commands return post-edit diagnostics reliably; keep `skill-tight-v1` available as a conservative “always verify” profile.

### F-2026-02-15-66: Anthropic skill-guide inspired “critical-first + call budget + validate-before-edit” tightened prompt (skill-tight-v3) further reduces nested-task token overhead

Evidence:
- Guidance profile implementation:
  - `benchmarks/scripts/Run-PairedAgentRuns.ps1` (`RoslynGuidanceProfile=skill-tight-v3`)
- Paired runs (nested ambiguity task):
  - `artifacts/real-agent-runs/20260215-claude-skill-tight-v3-nested-r1/paired-run-summary.json`
  - `artifacts/real-agent-runs/20260215-claude-skill-tight-v3-nested-r2/paired-run-summary.json`
- Comparator (same task, earlier profile):
  - `artifacts/real-agent-runs/20260215-claude-skill-tight-v2-nested-r1/paired-run-summary.json`

Result (`rename-overload-collision-nested-v1`, all runs passed constraint checks, treatment required Roslyn usage):
- `skill-tight-v2` (r1): delta tokens `-46`, delta round-trips `+0`, delta seconds `+9.500`, Roslyn calls `2`
- `skill-tight-v3` (r1/r2): token deltas `-152, -158` (avg `-155`), delta round-trips `+0`, avg delta seconds `+8.311`, Roslyn calls `2`

Interpretation:
- Making the “non-negotiables” explicit at the top (critical rules), constraining behavior via a tight call budget, and forcing validation against `symbol_display` before rename correlates with lower token overhead without adding tool calls.
- This matches the skill-guide warning that instructions fail when they are verbose/buried or non-actionable; `skill-tight-v3` reduces ambiguity in what to do next and what to avoid.

Decision:
- Keep `skill-tight-v3` as the default Claude treatment posture for these paired overload-rename tasks, and only fall back to `skill-tight-v1` when an explicit post-edit diagnostic call is required for trust (or when diagnosing harness/tool regressions).

### F-2026-02-16-67: Claude skill trigger benchmark scope expanded beyond rename to six additional operation families with stronger gates

Evidence:
- Harness implementation:
  - `benchmarks/scripts/Test-ClaudeSkillTriggering.ps1`
- New task families added:
  - `change-signature-named-args-v1`
  - `update-usings-cleanup-v1`
  - `add-member-threshold-v1`
  - `replace-member-body-guard-v1`
  - `create-file-audit-log-v1`
  - `rename-multifile-collision-v1`
- Gate hardening:
  - workspace-level change detection (`workspace_changed`)
  - per-file constraint assertions (`Add-CountCheckInFile`/`Add-MatchCheckInFile`)
  - per-run timeout (`ClaudeTimeoutSeconds`) and timeout telemetry

Result:
- A fixture-validation sweep (`artifacts/skill-tests/wide-scope-fixture-validation-v2`) completed across all six new task families and both conditions.
- Baseline project compile gates were green for all fixture families (`dotnet_build_pass_rate=1.0`), indicating task fixtures are benchmark-ready once agent auth is available.

Interpretation:
- The benchmark now measures a meaningfully wider operational surface than rename-only tasks and can detect create-file/multi-file edits reliably.
- Scope expansion is ready for empirical A/B runs; the current blocker is Claude account access, not harness/task construction.

Decision:
- Keep the expanded task set as the default widened scope for Claude skill-adoption studies.

### F-2026-02-16-68: Authentication/access failures can silently produce misleading zero-usage benchmark outputs unless treated as hard gate

Evidence:
- Auth-failed run artifacts:
  - `artifacts/skill-tests/wide-scope-timeout-smoke/`
  - `artifacts/skill-tests/wide-scope-timeout-smoke-opus/`
- Transcript symptom:
  - assistant error text: "Your account does not have access to Claude. Please login again or contact your administrator."
- Harness hardening:
  - `Parse-Transcript` now classifies auth failures (`auth_error`, `auth_error_message`)
  - default fail-closed behavior on auth failures (unless `-IgnoreAuthenticationError` is explicitly set)

Result:
- Without auth-gating, summaries showed all-zero tool usage and false-negative pass/adoption signals.
- With auth-gating enabled, runs fail fast and surface root cause clearly.

Interpretation:
- Auth/access state is an experimental integrity precondition, not incidental runtime noise.
- Failing closed on auth errors prevents corrupting comparative benchmark conclusions.

Decision:
- Keep auth-failure fail-closed semantics as default in `Test-ClaudeSkillTriggering.ps1`.

### F-2026-02-16-69: Wider operation-family sweep shows current tight Claude skill prompt still under-delivers on consistency (adoption and pass-rate regressions vs no-skill)

Evidence:
- Widened sweep artifacts:
  - `artifacts/skill-tests/wide-scope-v2/skill-trigger-summary.md`
  - `artifacts/skill-tests/wide-scope-v2/skill-trigger-summary.json`
- Tasks included:
  - `change-signature-named-args-v1`
  - `update-usings-cleanup-v1`
  - `add-member-threshold-v1`
  - `replace-member-body-guard-v1`
  - `create-file-audit-log-v1`
  - `rename-multifile-collision-v1`

Result:
- `no-skill` aggregate:
  - `pass_rate=0.833`
  - `dotnet_build_pass_rate=1.000`
  - `timeout_rate=0.000`
- `with-skill` aggregate:
  - `pass_rate=0.667`
  - `dotnet_build_pass_rate=0.833`
  - `roslyn_used_rate=0.333`
  - `timeout_rate=0.333`
  - `avg_tool_uses=6.5` (vs `3.0` for no-skill)
- Auth integrity was clean in this run (`auth_error_rate=0`), so the regressions are not explained by account access issues.

Interpretation:
- The skill guidance is still too weak for broad operation families: it increases tool activity but does not yet convert that into reliable success gains.
- Observed transcript patterns include command-schema retries and trailing verification loops that consume budget and trigger timeout outcomes.

Decision:
- Keep `roslynskills-tight` as experimental, not yet promoted as first-class default for broad-scope Claude tasks.
- Next iteration should explicitly optimize for:
  - higher adoption reliability (`roslyn_used_rate`),
  - lower timeout rate,
  - no pass-rate regression relative to no-skill baseline.

### F-2026-02-16-70: Implemented the three high-friction investigative command gaps from fallback logs (`ctx.search_text`, `nav.find_invocations`, `query.batch`) with CLI/MCP guidance and regression coverage

Evidence:
- New commands:
  - `src/RoslynSkills.Core/Commands/SearchTextCommand.cs` (`ctx.search_text`)
  - `src/RoslynSkills.Core/Commands/FindInvocationsCommand.cs` (`nav.find_invocations`)
  - `src/RoslynSkills.Core/Commands/QueryBatchCommand.cs` (`query.batch`)
- Registry wiring:
  - `src/RoslynSkills.Core/DefaultRegistryFactory.cs`
- CLI guidance/shorthand updates:
  - `src/RoslynSkills.Cli/CliApplication.cs`
- MCP schema hint updates:
  - `src/RoslynSkills.McpServer/Program.cs`
- Regression coverage:
  - `tests/RoslynSkills.Core.Tests/BreadthCommandTests.cs`
  - `tests/RoslynSkills.Cli.Tests/CliApplicationTests.cs`

Result:
- Command inventory increased from 34 to 37 commands, including:
  - `ctx.search_text`
  - `nav.find_invocations`
  - `query.batch`
- Validation gates:
  - `dotnet build RoslynSkills.slnx` passed.
  - `dotnet test tests/RoslynSkills.Core.Tests/RoslynSkills.Core.Tests.csproj --filter "FullyQualifiedName~BreadthCommandTests|FullyQualifiedName~CommandTests|FullyQualifiedName~SessionAndExplorationCommandTests"` passed (`43` tests).
  - `dotnet test tests/RoslynSkills.Cli.Tests/RoslynSkills.Cli.Tests.csproj --filter "FullyQualifiedName~CliApplicationTests"` passed (`27` tests).

Interpretation:
- The identified fallback pain points now have first-class Roslyn command paths:
  - workspace/file text hunting can stay in Roslyn envelopes (`ctx.search_text`),
  - call-site tracing can use semantic method anchoring (`nav.find_invocations`),
  - investigative multi-query flows can be bundled into one round-trip (`query.batch`).

Decision:
- Promote these commands into the benchmark prompt/guidance profiles for cross-project investigative tasks and measure whether fallback `rg` usage and retry churn decrease.

### F-2026-02-17-71: Claude wider non-rename sweep shows explicit skill invocation is currently the strongest Roslyn-adoption lever

Evidence:
- Sweep artifacts:
  - `artifacts/skill-tests/20260217-wide-claude-v1/skill-trigger-summary.md`
  - `artifacts/skill-tests/20260217-wide-claude-v1/skill-trigger-summary.json`
- Tasks:
  - `change-signature-named-args-v1`
  - `replace-member-body-guard-v1`
  - `create-file-audit-log-v1`
- Conditions:
  - `no-skill`, `with-skill`, `with-skill-invoked`, `with-skill-invoked-budgeted`

Result:
- `pass_rate` was `1.0` in all four conditions for this slice.
- `roslyn_used_rate`:
  - `no-skill`: `0.0`
  - `with-skill`: `0.333`
  - `with-skill-invoked`: `1.0`
  - `with-skill-invoked-budgeted`: `0.333`

Interpretation:
- Explicit skill invocation remains the most reliable way to trigger Roslyn usage across non-rename operations.
- Budget-constrained prompting reduced call volume but also reduced adoption consistency in this run.

Decision:
- Keep explicit invocation arm in widened sweeps and treat budget posture as a separate optimization axis, not an unconditional improvement.

### F-2026-02-17-72: Rename-centric treatment guidance causes avoidable failure on non-rename paired tasks; operation-neutral guidance corrects targeting behavior

Evidence:
- Pre-fix paired run (rename-biased profile on add-member task):
  - `artifacts/real-agent-runs/20260217-broadened-addmember-v1/paired-run-summary.md`
  - treatment attempted `nav.find_symbol Target.cs Process` and failed constraints.
- Harness update:
  - Added `operation-neutral-v1` profile and mixed task families in `benchmarks/scripts/Run-PairedAgentRuns.ps1`.
- Post-fix paired run:
  - `artifacts/real-agent-runs/20260217-claude-opneutral-addmember-v2/paired-run-summary.md`

Result:
- With `operation-neutral-v1`, both control and treatment passed on `add-member-threshold-v1`.
- Treatment showed successful Roslyn usage (`roslyn_calls 1/1`) without rename mis-targeting.

Interpretation:
- Guidance/profile alignment to task family is a first-order correctness requirement for treatment validity on mixed operation benchmarks.

Decision:
- Use `operation-neutral-v1` (or task-family-specific guidance) as baseline for non-rename paired experiments.

### F-2026-02-17-73: Style-specific constraint assertions can hide true capability by marking semantically correct outputs as failures

Evidence:
- Earlier add-member paired runs failed despite semantically correct implementation due block-body-only check.
- Constraint patch in `benchmarks/scripts/Run-PairedAgentRuns.ps1` now accepts:
  - block-bodied
  - expression-bodied
  forms for `HasLongName`.

Result:
- Re-run after patch (`20260217-claude-opneutral-addmember-v2`) produced `run_passed=True` for both lanes.

Interpretation:
- Benchmark acceptance gates must prioritize semantic intent over formatting style to avoid false negative conclusions.

Decision:
- Continue auditing constraint checks for style rigidity whenever a control/treatment diff appears semantically valid but fails gates.

### F-2026-02-17-74: Codex operation-neutral treatment on non-rename task still incurs heavy exploratory overhead despite successful Roslyn usage

Evidence:
- Paired run artifact:
  - `artifacts/real-agent-runs/20260217-codex-opneutral-changesig-v1/paired-run-summary.md`
- Task/profile:
  - `change-signature-named-args-v1`
  - `operation-neutral-v1`

Result:
- Control: pass, no Roslyn usage.
- Treatment: pass, Roslyn usage observed (`11/17` successful/attempted calls).
- Overhead vs control:
  - duration ratio: `7.739`
  - token ratio: `8.809`
  - round trips: `20` vs `2`

Interpretation:
- Roslyn adoption alone is not sufficient; trajectory discipline dominates cost.
- Major overhead source is redundant command-discovery/validation/retry loops before and around the actual operation.

Decision:
- Keep operation-neutral guidance for correctness alignment, but add stricter per-task call-budget rails in paired harness prompts before promoting it as an efficiency profile.

### F-2026-02-17-75: Transcript-split deep-dive confirms overhead is concentrated pre-edit and dominated by discovery/schema-probe churn

Evidence:
- New trajectory-grade split analyzer outputs:
  - `artifacts/tool-thinking-split/codex-opneutral-changesig-v1-thinking-metrics.json`
  - `artifacts/tool-thinking-split/claude-opneutral-addmember-v2-thinking-metrics.json`

Result:
- Codex (`change-signature-named-args-v1`):
  - treatment vs control deltas:
    - `command_round_trips`: `+18`
    - `events_before_first_edit`: `+18`
    - `discovery_commands_before_first_edit`: `+6`
    - `failed_commands_before_first_edit`: `+3`
    - `schema_probe_count`: `+5`
- Claude (`add-member-threshold-v1`):
  - treatment vs control deltas:
    - `command_round_trips`: `+8`
    - `events_before_first_edit`: `+15`
    - `discovery_commands_before_first_edit`: `+5`
    - `failed_commands_before_first_edit`: `+2`
    - `schema_probe_count`: `+5`

Interpretation:
- In both agents, the dominant treatment overhead is before first productive edit.
- Overhead profile is consistent with repeated command-shape discovery (`describe-command` / `validate-input`) and retry loops, not with post-edit verification cost.

Decision:
- Prioritize reducing pre-edit schema/discovery loops (stronger task-scoped command hints, examples, and call-budget rails) before broader optimization of post-edit diagnostics flow.

### F-2026-02-17-76: External medium-repo split runs required explicit Roslyn launcher injection; once injected, treatment Roslyn usage became measurable with clean control lanes

Evidence:
- Runner update:
  - `benchmarks/scripts/Run-ToolThinkingSplitExperiment.ps1` now resolves and injects host launcher path (`scripts/roscli-stable.cmd` fallback chain) into treatment prompts and explicit prohibition into control prompts.
- Artifacts:
  - Pre-injection Codex run (`treatment_roslyn=0`):  
    `artifacts/tool-thinking-split-runs/20260217-084934-codex-mediatr-invalid-notification-codex-v1/`
  - Post-injection Codex run (`treatment_roslyn=3`):  
    `artifacts/tool-thinking-split-runs/20260217-085723-codex-mediatr-invalid-notification-codex-v2/`
  - Post-injection Claude run (`treatment_roslyn=1`):  
    `artifacts/tool-thinking-split-runs/20260217-090210-claude-mediatr-invalid-notification-claude-v1/`
- Task/repo:
  - MediatR (`be61f5a`) invalid non-notification publish-message fix with acceptance  
    `dotnet test test/MediatR.Tests/MediatR.Tests.csproj --nologo`

Result:
- Control contamination stayed clean in post-injection runs (`control_roslyn_contamination=false`).
- Codex post-injection deltas (treatment-control):
  - `roslyn_command_count +3`
  - `command_round_trips +2`
  - `events_before_first_edit +11`
  - `discovery_commands_before_first_edit +3`
  - `failed_commands_before_first_edit +1`
  - `total_tokens +31134`
- Claude post-injection deltas:
  - `roslyn_command_count +1`
  - `command_round_trips +1`
  - `events_before_first_edit +5`
  - `discovery_commands_before_first_edit +1`
  - `failed_commands_before_first_edit +0`
  - `total_tokens +74` (`cache_inclusive_total_tokens +22270`)

Interpretation:
- On external repos, treatment can silently degrade to text-only unless a concrete launcher path is provided in-prompt.
- With launcher availability fixed, Roslyn usage appears with bounded pre-edit overhead rather than large uncontrolled exploratory churn.

Decision:
- Keep launcher injection as default split-run behavior for external repos.
- In future split experiments, treat `treatment_roslyn_command_count > 0` as a validity gate before interpreting overhead/correctness outcomes.

### F-2026-02-17-77: Tight treatment guidance reduced pre-edit/tool overhead versus standard in matched Codex split runs while preserving Roslyn usage

Evidence:
- Matched Codex runs on same MediatR task/repo:
  - Standard profile:
    `artifacts/tool-thinking-split-runs/20260217-091745-codex-mediatr-invalid-notification-codex-v3-standard/`
  - Tight profile:
    `artifacts/tool-thinking-split-runs/20260217-091218-codex-mediatr-invalid-notification-codex-v3-tight/`
- Claude direction check:
  - baseline profile:
    `artifacts/tool-thinking-split-runs/20260217-090210-claude-mediatr-invalid-notification-claude-v1/`
  - tight profile:
    `artifacts/tool-thinking-split-runs/20260217-092330-claude-mediatr-invalid-notification-claude-v2-tight/`

Result:
- Codex (tight vs standard, treatment-control deltas):
  - `roslyn_command_count`: `+4` vs `+11`
  - `command_round_trips`: `+6` vs `+12`
  - `events_before_first_edit`: `+17` vs `+34`
  - `failed_commands_before_first_edit`: `+4` vs `+9`
  - `total_tokens`: `+74894` vs `+180969`
- Claude direction check:
  - `roslyn_command_count` remained `+1`
  - `events_before_first_edit` reduced (`+5` -> `+3`)
  - `total_tokens` delta reduced (`+74` -> `+48`) while cache-inclusive deltas remained noisy.

Interpretation:
- Tight guidance does not eliminate overhead, but it meaningfully bounds pre-edit exploration and retry churn compared with broader standard flow in this task family.
- Token/caching telemetry remains agent-specific; cache-inclusive totals need cautious interpretation.

Decision:
- Keep `tight` as default treatment profile for split-lane experiments.
- Retain `standard` as an explicit comparator arm for periodic drift checks.

### F-2026-02-17-78: “Roslyn used” and “Roslyn used well” diverged in split runs; productive-call scoring exposed quality gap hidden by raw usage counts

Evidence:
- Analyzer update:
  - `benchmarks/scripts/Analyze-ToolThinkingSplit.ps1` now emits:
    - `productive_roslyn_command_count`
    - `events_before_first_productive_roslyn`
    - `semantic_edit_first_try_success_rate`
    - `verify_after_edit_rate`
    - `roslyn_used_well_score`
  - score contract: `roslyn_used_well_score = 0` when no productive Roslyn command occurs.
- Re-scored artifacts:
  - Codex standard: `artifacts/tool-thinking-split-runs/20260217-091745-codex-mediatr-invalid-notification-codex-v3-standard/`
  - Codex tight: `artifacts/tool-thinking-split-runs/20260217-091218-codex-mediatr-invalid-notification-codex-v3-tight/`
  - Claude baseline/tight:
    - `artifacts/tool-thinking-split-runs/20260217-090210-claude-mediatr-invalid-notification-claude-v1/`
    - `artifacts/tool-thinking-split-runs/20260217-092330-claude-mediatr-invalid-notification-claude-v2-tight/`

Result:
- Codex:
  - standard: `treatment_roslyn=11`, `productive_roslyn=8`, `used_well_score=49.55`
  - tight: `treatment_roslyn=4`, `productive_roslyn=1`, `used_well_score=40`
- Claude:
  - baseline: `treatment_roslyn=1`, `productive_roslyn=0`, `used_well_score=0`
  - tight: `treatment_roslyn=1`, `productive_roslyn=0`, `used_well_score=0`

Interpretation:
- Raw Roslyn adoption can overstate utility when calls are schema probes only.
- Productive-call and used-well metrics better capture whether RoslynSkills materially influenced task execution quality.

Decision:
- Treat `productive_roslyn_command_count > 0` and non-zero `roslyn_used_well_score` as required indicators for “Roslyn used well” claims.
- Keep reporting both usage presence and used-well quality to avoid false positives.
