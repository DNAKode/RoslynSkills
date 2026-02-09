# Research Findings

Date started: `2026-02-09`  
Purpose: empirical log for Roslyn-agent benchmark outcomes, with task context and tool identity snapshots.

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
