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
