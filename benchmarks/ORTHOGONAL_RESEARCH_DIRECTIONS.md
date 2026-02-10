# Orthogonal Research Directions

Date: 2026-02-10  
Status: active backlog for next benchmark waves

## Progress Update (2026-02-09)

Completed this cycle:

- Implemented a persistent transport prototype (`src/RoslynAgent.TransportServer/Program.cs`) exposing the same tool registry over long-lived stdio requests.
- Implemented a real MCP server contract (`src/RoslynAgent.McpServer/Program.cs`) with codex-compatible stdio JSON-RPC (`initialize`, `tools/list`, `tools/call`, `resources/*`, `ping`).
- Extended invocation benchmark script to compare:
  - `roscli_script` (process-per-call),
  - `dotnet_run_project` (process-per-call),
  - `dotnet_published_dll` (process-per-call),
  - `transport_published_server` (persistent process).
- Captured reproducible benchmark artifacts:
  - `artifacts/roscli-invocation-benchmark/20260209-192423/roscli-invocation-benchmark.json`
  - `artifacts/roscli-invocation-benchmark/20260209-192423/roscli-invocation-benchmark.md`
- Validated codex MCP treatment execution in paired harness:
  - `artifacts/paired-mcp-codex-resource-v4/paired-run-summary.json`
  - `artifacts/paired-mcp-codex-resource-v4/paired-run-summary.md`
- Validated cross-agent MCP attribution and elapsed breakout on refreshed bundle:
  - `artifacts/paired-mcp-dnakode-v1/paired-run-summary.json`
  - `artifacts/paired-mcp-dnakode-v1/paired-run-summary.md`
- Added paired-harness Roslyn guidance posture control (`Run-PairedAgentRuns.ps1`):
  - `-RoslynGuidanceProfile standard|brief-first|surgical`
  - profile stamp now emitted in `paired-run-summary.md` / run metadata.
- Expanded trajectory telemetry (`Analyze-TrajectoryRoslynUsage.ps1`) to capture:
  - discovery-vs-edit call mix,
  - list-command overhead,
  - pre-edit exploration load,
  - helper-command attribution (`roslyn.rename_and_verify`).
- Added run-level helpfulness/usage rollups (`Analyze-RunEfficiency.ps1`):
  - Roslyn-used run counts,
  - Roslyn successful-call totals (when provided),
  - average `roslyn_helpfulness_score`.
- Ran a live codex paired guidance smoke comparison:
  - `artifacts/paired-guidance-standard-codex-smoke-r1`
  - `artifacts/paired-guidance-surgical-codex-smoke-r2`
- Added `roscli` high-volume tuning path:
  - wrappers now support `ROSCLI_USE_PUBLISHED=1` + optional `ROSCLI_REFRESH_PUBLISHED=1`,
  - warmup launchers: `scripts/roscli-warm(.cmd)`,
  - varied-load benchmark runner: `benchmarks/scripts/Measure-RoscliLoadProfiles.ps1`
  - latest artifact: `artifacts/roscli-load-profiles/roscli-load-profiles-v6.md`

Preliminary read:

- Process startup dominates current CLI latency for many calls.
- Persistent transport shows large warm-call latency reductions while preserving command success.
- MCP/transport direction is now executable end-to-end in the paired-run harness (`-IncludeMcpTreatment`) and ready for replicate runs.
- MCP compatibility required transport-shape hardening (newline-delimited responses + URI normalization) before codex could execute resource-backed Roslyn commands reliably.
- Claude `ReadMcpResourceTool` URI-based Roslyn usage now maps correctly into `roslyn_*` counters on paired summaries.
- On the rename microtask, both `standard` and `surgical` converged to a one-call helper trajectory (`roslyn.rename_and_verify`) with no discovery calls.
- Under varied local command loads (`1/5/20`), cached published `roscli` execution showed major latency gains versus `dotnet run` wrappers (roughly `15x-20x` for ping/list and `~3.8x` for semantic symbol lookup).

## Progress Update (2026-02-10)

Completed this cycle:

- Added automatic stale-cache refresh checks to `scripts/roscli(.cmd)` for published mode (`ROSCLI_STALE_CHECK`, opt-in) plus stamp-aware warmup updates in `scripts/roscli-warm(.cmd)`.
- Completed stale-check overhead profiling and switched published wrapper default to low-latency mode (`ROSCLI_STALE_CHECK=0` unless explicitly enabled).
- Added interval-throttled stale-check probes (`ROSCLI_STALE_CHECK_INTERVAL_SECONDS`) for opt-in safety mode while keeping fast-path cache reuse as default.
- Ran refreshed load-profile matrix with explicit stale on/off comparison lanes:
  - `artifacts/roscli-load-profiles/roscli-load-profiles-v9.json`
  - `artifacts/roscli-load-profiles/roscli-load-profiles-v9.md`
- Extended lightweight real-run harness (`Run-LightweightUtilityGameRealRuns.ps1`) with:
  - condition-level env overrides (`conditions[*].environment` and optional `agent_environment.<agent>`),
  - treatment prompt corrections to favor `run <command-id> --input @payload.json` on baseline CLI surfaces,
  - bounded-diagnostics guidance for `diag.get_solution_snapshot`,
  - gate auto-skip when a run omits control or treatment lanes.
- Extended efficiency reporting (`Analyze-RunEfficiency.ps1`) with generic condition-pair deltas so treatment-only experiments still produce direct comparative tables.
- Executed a two-lane Codex treatment-vs-treatment real run on task `task-001-initial-build`:
  - `treatment-roslyn-optional` (`ROSCLI_USE_PUBLISHED=0`)
  - `treatment-roslyn-published-cache` (`ROSCLI_USE_PUBLISHED=1`)
  - artifact root: `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v4/`
- Executed a follow-up two-lane Codex replicate after wrapper default shift (`ROSCLI_STALE_CHECK=0` by default):
  - same condition ids and task (`task-001-initial-build`)
  - artifact root: `artifacts/real-agent-runs/20260210-lightweight-roscli-mode-v5/`

Observed signal:

- For this single open-ended task replicate, published-cache did not improve end-to-end outcome metrics:
- v4 replicate (stale-check enabled in published lane) did not improve end-to-end metrics:
  - elapsed: `424.935s` (published) vs `330.902s` (dotnet-run)
  - total tokens: `1,390,370` (published) vs `1,268,650` (dotnet-run)
  - command round-trips: `41` (published) vs `33` (dotnet-run)
- v5 replicate (stale-check disabled in published lane) reversed direction:
  - elapsed: `232.819s` (published) vs `367.936s` (dotnet-run)
  - total tokens: `428,723` (published) vs `1,001,800` (dotnet-run)
  - command round-trips: `15` (published) vs `32` (dotnet-run)
- Both lanes succeeded (`build/test` pass) with Roslyn used in each lane.
- Trajectory traces show behavior variance dominated the run (different file decomposition and exploratory call patterns), masking wrapper-level latency gains measured in component load tests.

Interpretation:

- Wrapper startup optimization alone is insufficient to guarantee lower task-level tokens/elapsed on unconstrained creative tasks.
- Prompt/tool-usage posture and command payload discipline remain first-order factors for end-to-end efficiency.
- For local high-call loops, stale-check probes were still a dominant overhead source; defaulting stale-check off produced materially better wrapper responsiveness while preserving explicit refresh controls.

## 1) Transport Layer: CLI vs MCP Server

Question:

- Is overhead dominated by process startup/serialization (`roscli` per-call), or by tool-choice/prompt behavior?

Planned conditions:

- `treatment-cli-cold`: current per-call CLI execution.
- `treatment-cli-warm`: long-lived local worker process with same command surface.
- `treatment-mcp`: MCP adapter exposing equivalent operations over a persistent server.

Controls:

- Same task set, prompts, acceptance checks, and model settings.
- Same operation ids and response schemas (as close as possible).

Primary metrics:

- elapsed duration,
- command round-trips,
- tool call success rate,
- token totals,
- task success/compile/test pass rates.

## 2) Implementation Performance Arms

Question:

- Which implementation architecture gives the best latency/reliability frontier for Roslyn operations?

Candidate arms:

- baseline process-per-call CLI,
- published DLL + thin launcher (already used in harness),
- persistent worker process with request multiplexing,
- shared cache primitives (for example metadata snapshot reuse and incremental workspace warm state).

Notes:

- Memory-mapped/session-backed caches are allowed as experimental arms if they preserve correctness and determinism.
- Any cache strategy must include invalidation semantics and reproducibility controls.

## 3) Trace-Based Theory of Tool Use

Question:

- Why do Roslyn-enabled runs sometimes underperform despite stronger semantics?

Hypotheses to test:

- H1: too many round-trips and oversized payloads erase semantic benefits.
- H2: agents over-explore and delay edits when rich tools are available.
- H3: tool guidance does not sufficiently steer compact/context-efficient usage.
- H4: interface friction (process startup, command syntax, path issues) creates hidden retries.

Evidence to capture:

- per-run command timeline,
- Roslyn attempted/successful call counts,
- command output size proxies,
- time-to-first-edit and time-to-first-Roslyn-call,
- fallback events and contamination checks.

## 4) Context Policy: Brief-First vs Verbose-First

Question:

- Are larger Roslyn payloads improving outcomes, or just inflating token/latency cost?

Planned conditions:

- `treatment-brief-first`,
- `treatment-standard`,
- `treatment-verbose-first`.

Required telemetry:

- `brief` field usage rate by command (`nav.find_symbol`, `ctx.member_source`, `diag.get_solution_snapshot`),
- explicit `--brief` flag usage,
- `output_chars` and `source_chars` proxies,
- elapsed time and total tokens.

Current signal from latest creative run:

- `brief=true` usage was effectively absent; observed Roslyn payloads were mostly non-brief.

## 5) Immediate Next Steps

1. Add a persistent transport condition to the paired-run harness with strict condition isolation equal to current control/treatment safeguards.
2. Run one replicate bundle with three Roslyn treatment variants (`cli-process`, `cli-published`, `transport-persistent`) on the same tasks/prompts.
3. Track cold vs warm latency explicitly (first-call vs steady-state) and include both in reports.
4. Run `Analyze-TrajectoryRoslynUsage.ps1` on the same bundles to correlate latency changes with tool-choice behavior (`brief` usage and round-trip counts).
