# Proposal: Align RoslynSkills With Anders Hejlsberg's AI Language-Service Thesis

Date: 2026-05-14
Status: proposal / backlog input
Related artifacts:

- `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`
- `DETAILED_DESIGN_SPEC.md`
- `docs/CACHE_ARCHITECTURE_OPTIONS_2026-02-25.md`
- `docs/TOOL_CALL_PERF_FINDINGS_2026-02-25.md`
- `benchmarks/LSP_COMPARATOR_PLAN.md`
- `docs/PIT_OF_SUCCESS.md`

## Problem Statement

Anders Hejlsberg's observation maps directly onto RoslynSkills' core thesis:

- AI agents often use text search for questions that require symbol identity.
- Ambiguous names such as `Count` or `Address` create high-risk false positives.
- Existing language services already contain much of the needed semantic machinery.
- Those services are not naturally packaged for agents, which prefer simple command/tool surfaces.
- Fast feedback loops, ideally backed by a hot solution/workspace server, will matter more as agents generate and validate code incrementally.

RoslynSkills already addresses parts of this through `nav.*`, `ctx.*`, `edit.*`, `diag.*`, `query.batch`, MCP, and the persistent transport prototype. The next improvement phase should make this thesis explicit: RoslynSkills should become an AI-native language-service facade over Roslyn, optimized for semantic certainty, low-latency feedback, and agent-operable command contracts.

## Constraints And Non-Goals

Constraints:

- Preserve the existing CLI-first contract and stable command IDs.
- Keep MCP and skill wrappers as adapters over equivalent command semantics.
- Keep text fallback available, but measure and log semantic fallback gaps.
- Treat external C# LSP as a comparator and possible complement, not as an enemy.
- Require benchmark evidence before claiming superiority over text-first or LSP-first workflows.

Non-goals:

- Replacing Roslyn or LSP protocols wholesale.
- Building an IDE UI.
- Making cross-language claims in this project phase.
- Optimizing only microbenchmarks while ignoring end-to-end agent trajectory cost.

## Thesis

RoslynSkills should improve from "a Roslyn-native CLI for agents" into "a hot, AI-facing language-service layer for C#/.NET agents."

The difference is operational:

- Commands should answer agent-shaped semantic questions directly, not expose raw IDE primitives.
- The default loop should be semantic locate -> bounded edit -> immediate diagnostics -> repair hint.
- Hot workspace state should be reused across calls whenever possible.
- Results should be compact enough for models and structured enough for deterministic scoring.

## Dependency Graph

P0. Evidence alignment

- Map Anders's claims to existing research questions and command families.
- Identify which claims are already supported by existing artifacts and which need new data.

P1. AI-native semantic query layer

- Add higher-level semantic questions over the current Roslyn command set.
- Reduce multi-call discovery loops for common ambiguity cases.

P2. Hot workspace service

- Promote persistent transport from benchmark lane to first-class local service path.
- Keep process-per-call CLI as fallback.

P3. Incremental validation loop

- Make immediate semantic/diagnostic feedback cheaper and more targeted.
- Prefer workspace-backed diagnostics and repair hints over raw build-output round trips.

P4. LSP interop and comparison

- Keep explicit RoslynSkills vs LSP vs combined benchmark lanes.
- Add command-shape comparison: what LSP offers to agents directly vs what RoslynSkills can package better.

P5. Benchmark expansion

- Add Anders-style ambiguity and hot-feedback tasks to the agent-in-loop benchmark set.
- Require latency, token, correctness, and tool-adoption telemetry.

Edges:

- P0 -> P1, P2, P4, P5
- P1 + P2 -> P3
- P3 + P4 -> P5

## Proposed Improvements

### 1. Add Agent-Shaped Semantic Search Commands

Current commands such as `nav.find_symbol`, `nav.find_references`, `ctx.symbol_envelope`, and `query.batch` are useful but still require the agent to assemble a workflow.

Add a small set of commands that directly answer the common questions agents ask before editing:

- `semantic.resolve_target`
  - Input: natural-ish target phrase, file anchor, optional line/column, optional expected kind.
  - Output: ranked symbol candidates with ambiguity score, stable symbol identity, declaration/reference counts, and recommended next command.
  - Goal: prevent accidental edits to the wrong `Count`, `Address`, `Process`, or overload.

- `semantic.find_usages`
  - Input: symbol anchor or coordinate anchor.
  - Output: semantically verified usage list grouped by project/type/member, with optional write/read/call classification where Roslyn can support it.
  - Goal: replace grep-like "all matching text" with symbol-identity search.

- `semantic.edit_readiness`
  - Input: intended operation plus target anchor.
  - Output: preflight packet: ambiguity status, workspace mode, expected affected files, risky dynamic/reflection/string references, and recommended edit primitive.
  - Goal: make uncertainty explicit before mutation.

Acceptance tests:

- Collision fixtures where multiple members share the same lexical name.
- Overload and inheritance fixtures.
- Project-backed tests requiring `workspace_context.mode = workspace`.
- Output snapshots proving brief mode is compact and contains stable identity fields.

### 2. Promote Hot Workspace Mode To A Product Path

Current evidence shows process startup dominates small commands and workspace load dominates heavier semantic commands. The persistent transport prototype already demonstrates warm-call wins for small commands, but Roslyn workspace reuse is the decisive next step.

Add a first-class daemon/service lane:

- `roscli serve`
  - Starts a local per-workspace service using named pipes or loopback transport.
  - Prefer `.sln`/`.slnx` inputs and maintain hot MSBuild/Roslyn solution state.
  - Allow `.csproj/.vbproj` only for explicitly project-scoped hosts.
  - Supports idle expiry, for example 10 minutes after last request.
  - Exposes the same command contracts as the CLI/MCP path.

- `roscli --use-server auto`
  - Default eventually becomes: use hot service when healthy, direct CLI fallback otherwise.
  - Emits `invocation_mode`, `server_hit`, `workspace_cache_hit`, and invalidation details in telemetry.

- `roscli server.status`
  - Reports loaded workspaces, generation/fingerprint, memory, last access, stale state, workspace kind, project count, and document count.

- `roscli server.stop`
  - Deterministic cleanup for tests and operator control.

Correctness requirements:

- File watcher plus command-time fingerprint validation.
- Solution host must prove full-solution binding through `resolved_workspace_path`, `workspace_kind`, `project_count`, and `document_count`.
- Strict invalidation mode for benchmark/promotion runs.
- Fail-closed option for workspace-backed commands when server state is stale.
- Direct CLI fallback must remain available and auditable.

Benchmark gates:

- Cold start, first semantic call, and warm semantic call must be reported separately.
- Compare process CLI, published CLI, transport server, MCP, LSP MCP, and combined lanes.
- For `nav.find_symbol` and `diag.get_file_diagnostics`, warm workspace service should target order-of-magnitude latency reduction vs process-per-call workspace load on repeated calls.

### 3. Make Semantic Validation Available During Generation

Anders's key forward-looking point is not just post-hoc build validation; it is validating generated code as it is being produced.

Extend the session model from file-only snapshots toward project-backed speculative edits:

- `session.open_workspace`
  - Opens a file inside a workspace-backed server session.
  - Captures project/solution context and generation.

- `session.apply_project_edits`
  - Applies one or more text/span/semantic edits in memory.
  - Returns targeted diagnostics, affected symbols, and changed files without writing disk.

- `session.explain_diagnostics`
  - Converts diagnostics into repair-oriented packets with likely operation suggestions.

- `session.commit_workspace`
  - Commits only if disk fingerprints and expected generation still match.

Design rule:

- Keep this compatible with existing `edit.transaction` and `session.apply_and_commit`; do not create a second unrelated edit model.

Acceptance tests:

- In-memory edit that creates a type error returns immediate workspace-backed diagnostic.
- Multi-file rename/change-signature session reports affected documents before commit.
- External disk change blocks commit with a clear generation/fingerprint error.

### 4. Add Semantic Ambiguity As A First-Class Metric

The quote's `Count`/`Address` example should become a measured scenario family, not just motivation.

Add benchmark dimensions:

- lexical ambiguity count,
- semantic candidate count,
- wrong-target risk,
- agent pre-edit confidence,
- number of tool calls before first mutation,
- whether the first mutation targeted the intended symbol,
- whether semantic tool use prevented a wrong edit.

New scenario examples:

- `count-property-collision`: several `Count` properties across unrelated types.
- `address-property-collision`: DTO/entity/view-model properties with the same name.
- `overload-callsite-targeting`: rename/change call only for one overload.
- `interface-implementation-disambiguation`: edit the implementation, not the interface declaration.
- `extension-method-shadowing`: identify invoked symbol when local and extension methods share names.

Promotion gate:

- Do not claim semantic search superiority from component benchmark accuracy alone.
- Require agent-in-loop paired runs where treatment changes the trajectory: fewer wrong turns, fewer retries, or safer first mutation.

### 5. Treat LSP As Both Comparator And Raw Material

Anders notes that language services already provide much of this. RoslynSkills should not argue "CLI vs LSP" too simplistically.

Instead, split the question:

1. Capability: what semantic facts can LSP provide already?
2. Shape: are those facts exposed in an agent-friendly command contract?
3. Latency: can a hot service deliver them fast enough for iterative generation?
4. Edit safety: can the tool apply semantic transactions, not just navigate?
5. Observability: can the trajectory be logged and scored cleanly?

Concrete work:

- Expand `benchmarks/LSP_COMPARATOR_PLAN.md` with command-shape and latency-shape scoring.
- Add "LSP raw" vs "RoslynSkills packaged" comparison notes per scenario.
- Keep a combined lane where RoslynSkills uses LSP-derived ideas but retains agent-first output contracts.

Likely design stance:

- LSP remains excellent for editor interoperability and a valuable comparator.
- RoslynSkills should specialize in agent-operable workflows: compact schemas, transaction gates, benchmark telemetry, and command sequencing guidance.

### 6. Add Feedback-Loop Policy Modes

Expose explicit policies so the benchmark can test the speed/correctness frontier:

- `feedback=none`
  - No immediate diagnostics; fastest but riskiest.

- `feedback=file`
  - Target file diagnostics after mutation.

- `feedback=affected`
  - Diagnostics for changed files plus known semantic dependents.

- `feedback=workspace-budgeted`
  - Broader diagnostics under a time/file budget.

- `feedback=adaptive`
  - Escalates based on ambiguity, diagnostic severity, and recent failures.

Command integration:

- `edit.rename_symbol --feedback affected`
- `edit.change_signature --feedback affected`
- `edit.transaction --feedback adaptive --diagnostic-budget-ms 1000`
- `session.apply_project_edits --feedback file|affected|adaptive`

Benchmark gate:

- Compare time-to-first-feedback, time-to-green, diagnostic regression rate, and token cost.

### 7. Improve Pit-Of-Success Around Hot Semantic Workflows

Add startup guidance that makes the intended AI-native path obvious:

```text
roscli quickstart --profile hot-semantic
roscli serve --workspace MySolution.slnx
roscli semantic.resolve_target src/Foo.cs Count --expected-kind property
roscli edit.rename_symbol src/Foo.cs 42 17 TotalCount --apply true --feedback affected
```

Update `docs/PIT_OF_SUCCESS.md` and skill files once the commands exist.

Pit-of-success requirements:

- The agent should not need to know Roslyn internals.
- The agent should not need to choose between dozens of primitives for common rename/reference/diagnostic flows.
- Brief output should be the default for discovery.
- Verbose output should be opt-in and justified.

## Validation Gates

Build and tests:

- `dotnet build RoslynSkills.slnx`
- `dotnet test RoslynSkills.slnx`
- targeted command contract tests for new command schemas
- snapshot tests for brief/standard output shapes

Performance:

- Extend `Benchmark-ToolCallPerf.ps1` to include hot workspace semantic calls.
- Report cold start, first call, steady-state, and invalidated-call latency separately.
- Include confidence intervals for ratios against process CLI baseline.

Agent-in-loop:

- Add ambiguity-heavy tasks to paired harness.
- Run at least control, roscli process, roscli hot-server, MCP, LSP, and combined lanes where available.
- Capture tool adoption, first-mutation correctness, total tokens, round trips, elapsed time, and pass/fail.

Correctness:

- No stale symbol results after external file mutation.
- No workspace-backed command silently falls back to `ad_hoc` when `require_workspace=true`.
- Wrong-target rename fixtures must fail or disambiguate, not mutate the wrong symbol.

## Rollback And Fallback Strategy

- Keep current process-per-call CLI as the trusted baseline.
- Gate server usage behind `--use-server auto|on|off` until maturity.
- If the hot server is stale, unhealthy, or missing workspace context, return a typed error and fall back only when policy allows it.
- If new semantic convenience commands are noisy, keep lower-level `nav.*`, `ctx.*`, `edit.*`, and `diag.*` commands as the precise escape hatch.

## Proposed Implementation Slices

### Slice 1: Research And Benchmark Shape

Deliverables:

- Anders-aligned scenario definitions.
- Benchmark metric additions for ambiguity and first-mutation correctness.
- LSP comparator scoring update for command shape and hot-loop latency.

Done when:

- New scenarios run in dry-run/component mode.
- Paired harness schema can record ambiguity metrics.

### Slice 2: Semantic Convenience Commands

Deliverables:

- `semantic.resolve_target`
- `semantic.find_usages`
- `semantic.edit_readiness`
- CLI/MCP schema hints and `describe-command` examples.

Done when:

- Collision-heavy tests pass.
- Brief outputs remain bounded and agent-readable.

### Slice 3: Hot Workspace Service MVP

Deliverables:

- `roscli serve`
- `roscli --use-server auto`
- `server.status`
- strict/balanced invalidation modes.

Done when:

- Repeated semantic queries reuse workspace state.
- Tool-call perf harness reports cold/first/warm/invalidation rows.

### Slice 4: Project-Backed Sessions

Deliverables:

- `session.open_workspace`
- `session.apply_project_edits`
- `session.commit_workspace`
- repair-oriented diagnostic packet integration.

Done when:

- In-memory project-backed edits can be validated before disk write.
- External disk mutation blocks commit safely.

### Slice 5: Agent-In-Loop Promotion Run

Deliverables:

- Replicate-backed paired runs across ambiguity and feedback-loop task families.
- Updated `RESEARCH_FINDINGS.md`.
- Decision note: which hot semantic path should become default guidance.

Done when:

- Data supports or rejects making hot semantic workflow the default.
- Residual failures are classified into command-surface, latency, stale-state, or model-adoption causes.

## Open Questions

1. Should the hot service be implemented as an evolution of `RoslynSkills.TransportServer`, `RoslynSkills.McpServer`, or a shared host used by both?
2. Should `semantic.resolve_target` accept limited natural language, or stay strictly schema-driven with `name`, `kind`, and anchors?
3. What idle timeout balances Anders's "dump it after a while" model with local developer resource expectations?
4. Which diagnostics budget gives the best speed/correctness frontier for `feedback=affected`?
5. Can LSP diagnostics and RoslynSkills diagnostics be normalized enough for fair combined-lane scoring?

## Recommendation

Prioritize the hot workspace service and semantic convenience commands together. The service addresses the feedback-loop performance point; the commands address the agent-accessibility point. Either alone is incomplete:

- Fast raw primitives still leave agents to orchestrate too much.
- Good semantic commands over process-per-call workspace loading remain too slow for tight generation loops.

The highest-value next bet is therefore:

1. `semantic.resolve_target` for ambiguity-safe target selection.
2. hot workspace server path for repeated semantic calls.
3. project-backed speculative session validation.
4. ambiguity-heavy paired benchmarks proving whether these changes improve real agent trajectories.
