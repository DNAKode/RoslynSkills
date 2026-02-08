# Detailed Design Specification: Roslyn Agentic Coding Platform

Date: February 8, 2026  
Status: Design baseline v1

Cross references:

- Proposal: `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md`
- Research plan: `DETAILED_RESEARCH_PLAN.md`

## 1. Design Goals

Primary goals:

1. Build production-grade Roslyn augmentation primitives for C# agentic coding.
2. Enable rigorous comparative experiments across retrieval/edit/feedback/interface variants.
3. Keep architecture interface-agnostic with CLI as canonical contract.

Non-goals:

- Generic multi-language engine in this phase.
- Premature commitment to one command language theory before experiments.

Related projects:

- P2, P3, P5, P6, P9.

## 2. System Architecture

## 2.1 Components

1. `Core.Workspace`
- Loads solutions/projects via Roslyn workspace APIs.
- Maintains immutable snapshot graph and change transactions.
- [Ref: P5]

2. `Core.Semantics`
- Symbol resolution, declaration/reference graph, scope hierarchy extraction.
- [Ref: P1, P5]

3. `Core.Operations`
- Typed operation implementations for retrieval, edits, diagnostics, and repair.
- [Ref: P1, P2, P5]

4. `Core.Diagnostics`
- Compiler and analyzer diagnostics normalization.
- Feedback packet shaping (`raw`, `compact`, `guided`).
- [Ref: P5, R-006]

5. `Cli.Host`
- Canonical interface and schema boundary.
- Subcommand and output contract layer.
- [Ref: P3, R-004]

6. `Adapters.Skill`
- Workflow layer over `Cli.Host`.
- [Ref: P4, R-012]

7. `Adapters.Mcp`
- MCP surface mapped from stable CLI command set.
- [Ref: P4, R-012]

8. `Benchmark.Harness`
- Scenario runner, baseline adapters, telemetry and metrics.
- [Ref: P6, P8, R-007..R-011]

## 2.2 Canonical Data Flow

`Agent -> Cli.Host -> Core.Operations -> Core.Workspace/Core.Semantics -> Core.Diagnostics -> Cli.Host -> Agent`

All non-CLI adapters must map through equivalent command contracts to preserve comparability.

## 3. Operation Scope Design

## 3.1 Operation Categories

`nav.*` navigation and symbol discovery  
`ctx.*` context envelope extraction  
`edit.*` structured edit operations  
`diag.*` diagnostics and feedback packets  
`repair.*` guided fix planning and application  
`test.*` test impact and targeted execution hints

[Ref: P1, R-002]

## 3.2 Operation Registry Shape

Each operation must declare:

- `operation_id`
- `version`
- `inputs_schema`
- `outputs_schema`
- `side_effects`
- `preconditions`
- `postconditions`
- `failure_codes`

[Ref: P2, Section 6 of `DETAILED_RESEARCH_PLAN.md`]

## 3.3 Candidate High-Value Operations (v1)

Navigation:

- `nav.find_symbol`
- `nav.find_references`
- `nav.find_implementations`
- `nav.find_overrides`

Context:

- `ctx.symbol_envelope`
- `ctx.call_chain_slice`
- `ctx.dependency_slice`

Structured edits:

- `edit.rename_symbol`
- `edit.change_signature`
- `edit.add_member`
- `edit.replace_member_body`
- `edit.update_usings`
- `edit.apply_code_fix`

Diagnostics:

- `diag.get_after_edit`
- `diag.get_solution_snapshot`
- `diag.diff`

Repair:

- `repair.propose_from_diagnostics`
- `repair.apply_plan`

## 4. Structured Symbol Retrieval Schema

## 4.1 `SymbolEnvelope` (draft)

```json
{
  "symbol_id": "string",
  "display_name": "string",
  "kind": "method|type|property|field|namespace|...",
  "qualified_name": "string",
  "assembly": "string",
  "project": "string",
  "document_path": "string",
  "span": { "start": 0, "length": 0 },
  "hierarchy": {
    "namespace": "string",
    "containing_types": ["string"],
    "containing_member": "string"
  },
  "local_context": {
    "line_start": 0,
    "line_end": 0,
    "snippet": "string"
  },
  "relations": {
    "declaration": true,
    "reference_count_hint": 0,
    "implementation_count_hint": 0
  }
}
```

## 4.2 Output modes

- `brief`: id, qualified name, location, minimal hierarchy.
- `standard`: adds snippet and relation hints.
- `verbose`: adds extended hierarchy and related symbols.

[Ref: RQ1, P3, P5]

## 5. Structured Edit Anchoring Model

## 5.1 Anchor types

- Symbol anchor (`symbol_id`)
- Syntax anchor (`document + syntax_path`)
- Span anchor (`document + stable span hash`)

Use precedence:

1. symbol anchor when available,
2. syntax anchor fallback,
3. span anchor only as last resort.

## 5.2 Transaction model

`propose -> validate preconditions -> apply in-memory -> diagnostics -> accept/reject -> persist`

Transaction must produce:

- changed document list,
- operation trace,
- diagnostics delta.

[Ref: H2/H4, P5]

## 5.3 Failure semantics

Required failure classes:

- `anchor_not_resolved`
- `ambiguous_target`
- `precondition_failed`
- `postcondition_failed`
- `diagnostic_regression`
- `workspace_apply_failed`

## 6. Diagnostic Feedback Packet Design

## 6.1 Packet modes

`raw`:

- near-direct compiler/analyzer output.

`compact`:

- normalized tuples (`id`, `severity`, `location`, `message`, `symbol_hint`).

`guided`:

- compact mode + ranked repair suggestions (operation ids or code actions).

[Ref: RQ3, R-006]

## 6.2 Adaptive policy hooks

Packet selection depends on:

- task class,
- ambiguity score,
- recent failure count,
- token budget pressure.

[Ref: H5, P5, P8]

## 7. Command Language Theories (Design Candidates)

## 7.1 Theory A: Imperative operation calls

- Model directly chooses commands and inputs.
- Strength: deterministic and transparent.
- Risk: higher planning burden on model.

## 7.2 Theory B: Declarative intent packet

- Model states target outcome + constraints; planner chooses operations.
- Strength: abstraction over low-level details.
- Risk: planner complexity and hidden behavior.

## 7.3 Theory C: Hybrid staged protocol

- Stage 1: intent
- Stage 2: tool-generated plan
- Stage 3: explicit operation confirmations
- Strength: auditability with flexibility.
- Risk: additional round trips.

Default expectation: Theory C likely best for research and safety balance.

[Ref: P2, R-003]

## 8. CLI Contract Design

Canonical command patterns:

- `roslyn-agent list-commands`
- `roslyn-agent describe-command <id>`
- `roslyn-agent run <command-id> --input <json>`
- `roslyn-agent validate-input <command-id> --input <json>`

Output contract:

- consistent envelope fields (`ok`, `command_id`, `version`, `data`, `errors`, `trace_id`),
- `--format json|text`,
- strict exit codes by failure class.

[Ref: P3, R-004]

## 9. Adapters Design

## 9.1 Skill wrapper

- Adds workflow guidance and command sequencing.
- Must not alter CLI semantics.

## 9.2 MCP adapter

- Maps CLI commands to MCP tools with input schema translation.
- Includes safety allowlist for stateful/destructive operations.

[Ref: P4]

## 10. Benchmark Harness Design

## 10.1 Core entities

- `Scenario`: task definition, constraints, expected checks.
- `RunConfig`: model, interface channel, policy toggles.
- `RunRecord`: timeline of tool calls and outputs.
- `MetricRecord`: computed metrics and tags.

## 10.2 Runner abstraction

- `IAgentRunner` interface with implementations:
  - `CodexCliRunner`,
  - `ClaudeCodeRunner`,
  - extensible to others.

## 10.3 Comparability constraints

- Same scenario and acceptance checks across variants.
- Same command contract semantics across interfaces.
- Captured provenance for every run.

[Ref: P6, P8]

## 11. Distribution and Operational Design

- Package core CLI as `dotnet tool`.
- Version command schemas independently from binary version.
- Provide compatibility matrix by CLI/schema version.
- Include MIT license and attribution for corpus/task artifacts.

[Ref: P9]

## 12. Open Design Questions (to resolve experimentally)

1. Which context envelope fields help most per token?
2. Which edit operations must remain explicit vs planner-managed?
3. Which feedback packet mode works best by task class?
4. How much latency overhead from transaction gating is acceptable?
5. What minimal stable command set is needed before MCP exposure?

## 13. Decision Logging Requirement

Every major design decision must capture:

- competing options,
- evidence used,
- chosen option and rationale,
- fallback path if evidence changes.

Store decision logs under future `docs/decisions/` once implementation begins.
