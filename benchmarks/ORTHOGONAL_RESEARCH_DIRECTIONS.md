# Orthogonal Research Directions

Date: 2026-02-09  
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

Preliminary read:

- Process startup dominates current CLI latency for many calls.
- Persistent transport shows large warm-call latency reductions while preserving command success.
- MCP/transport direction is now executable end-to-end in the paired-run harness (`-IncludeMcpTreatment`) and ready for replicate runs.
- MCP compatibility required transport-shape hardening (newline-delimited responses + URI normalization) before codex could execute resource-backed Roslyn commands reliably.
- Claude `ReadMcpResourceTool` URI-based Roslyn usage now maps correctly into `roslyn_*` counters on paired summaries.

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
