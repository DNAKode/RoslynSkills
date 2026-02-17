# Static Analysis Landscape And RoslynSkills Advanced Catalog (2026-02-16)

## Purpose

Capture a practical feature map of the static-analysis ecosystem and translate it into an implementable `analyze.*` roadmap for RoslynSkills, with explicit Roslyn API anchors and clear experimental vs stable command posture.

## External Signals (Primary Sources)

### Roslyn intended-for-tooling surfaces

- Syntax + immutable full-fidelity trees for C# and VB.
  - https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/syntax-analysis
- Semantic model and compilation as semantic truth.
  - https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis
- Workspace model as solution/project/document substrate.
  - https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace
- Symbol graph APIs (`SymbolFinder.FindReferencesAsync`).
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.findsymbols.symbolfinder.findreferencesasync?view=roslyn-dotnet-4.14.0
- Analyzer + code-fix provider flow (diagnostics/code actions/suppressors).
  - https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/tutorials/how-to-write-csharp-analyzer-code-fix
- Control/data-flow APIs (`ControlFlowGraph.Create`, `AnalyzeDataFlow` in C# and VB extensions).
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.flowanalysis.controlflowgraph.create?view=roslyn-dotnet-4.14.0
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.csharp.csharpextensions.analyzedataflow?view=roslyn-dotnet-4.14.0
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.visualbasic.visualbasicextensions.analyzedataflow?view=roslyn-dotnet-4.14.0

### SonarQube model (quality governance + security review workflow)

- Issue taxonomy: bug, vulnerability, code smell.
  - https://docs.sonarsource.com/sonarqube-server/10.5/user-guide/issues
- Security Hotspot review workflow and distinction vs vulnerabilities.
  - https://docs.sonarsource.com/sonarqube-community-build/user-guide/security-hotspots/
- Quality gates and conditions on new/overall code.
  - https://docs.sonarsource.com/sonarqube-server/quality-standards-administration/managing-quality-gates/introduction-to-quality-gates
- Quality profiles: rule activation, inheritance, severity/parameter customization.
  - https://docs.sonarsource.com/sonarqube-server/quality-standards-administration/managing-quality-profiles/understanding-quality-profiles

### CodeQL model (query-pack and model-pack ecosystem)

- Query/library/model pack architecture and customization route.
  - https://docs.github.com/en/code-security/concepts/code-scanning/codeql/codeql-query-packs

### Semgrep model (taint + cost/precision tradeoffs)

- Taint mode source/sink/sanitizer/propagator model, intra/interprocedural options, inter-file cost profile.
  - https://semgrep.dev/docs/writing-rules/data-flow/taint-mode/overview

### NStatic historical signal (high-expressivity symbolic analysis goals)

- NStatic design goals and example findings (symbolic simplification, redundant-parameter reasoning, infinite loops with no side effects).
  - https://www.infoq.com/news/2007/02/NStatic/
- Later community trace on status/plans.
  - https://stackoverflow.com/questions/3306178/whatever-happened-to-nstatic

### Rope (refactoring ergonomics benchmark, not .NET-specific)

- Breadth of practical refactoring operations and stop/resume ergonomics.
  - https://rope.readthedocs.io/en/latest/overview.html
  - https://rope.readthedocs.io/en/latest/library.html

## RoslynSkills Catalog Direction

## Command maturity lanes

- `stable`: deterministic, bounded runtime, low heuristic ambiguity.
- `advanced`: potentially slower or context-sensitive; deterministic enough for production with clear caveats.
- `experimental`: heuristic/speculative, may require larger graph exploration or partial-program assumptions.

## Proposed advanced commands

### Flow/graph analysis

- `analyze.control_flow_graph`
  - Build control-flow graph summary from `ControlFlowGraph.Create`.
  - Output: basic blocks, branch edges, exit blocks, exceptional edges.
- `analyze.dataflow_slice`
  - Def-use/use-def slicing from `AnalyzeDataFlow` + operation graph.
  - Output: influencing and influenced statements for a symbol span.
- `analyze.pure_candidates`
  - Heuristic purity scan for methods (no writes, no impure calls, no IO side effects).

### Correctness risk

- `analyze.nullability_contracts`
  - Identify nullability contract drift between declaration, overrides, and callsites.
- `analyze.exception_flow`
  - Summarize thrown exception surface and unhandled propagation boundaries.
- `analyze.dead_store`
  - Detect writes never read before overwrite/exit.

### Security-oriented (Roslyn-native)

- `analyze.taint_paths` (experimental initially)
  - Lightweight sourceâ†’sink propagation with configurable sanitizers.
  - Model inspired by Semgrep/Sonar taint concepts.
- `analyze.secrets_patterns`
  - Structured secret-pattern detection with syntax/semantic filters to reduce false positives.

### Architecture/governance

- `analyze.quality_gate_eval`
  - Evaluate command findings against configurable gate conditions (`new_code_only`, thresholds, fail/pass).
- `analyze.profile_apply`
  - Apply a named rule profile (Sonar-style profile concept) for repeatable policy sets.
- `analyze.hotspot_queue`
  - Emit review queue for â€œneeds human reviewâ€ findings (Sonar hotspot-style workflow).

## Short-term implementation order

1. `analyze.control_flow_graph` (stable)
2. `analyze.dataflow_slice` (advanced)
3. `analyze.dead_store` (advanced)
4. `analyze.quality_gate_eval` (stable)
5. `analyze.taint_paths` (experimental)

## Implementation status (current)

- Implemented:
  - `analyze.control_flow_graph`
  - `analyze.dataflow_slice`
- In progress / next:
  - `analyze.dead_store`
  - `analyze.quality_gate_eval`
  - `analyze.taint_paths`

## Validation strategy

- Add fixture-driven component tests for every new command.
- Run mixed-language fixtures (C# + VB) by default.
- Add large-repo smoke tracks (including VB-heavy external corpus runs) as opt-in integration suites.
- Measure false-positive/false-negative directionally against curated expected findings.
- Track runtime and token/roundtrip budget to keep agent workflows practical.

## Notes for RoslynSkills docs/skills

- Expose maturity and caveats in `describe-command`.
- Surface bounded defaults (`max_nodes`, `max_paths`, `max_files`) in all advanced/experimental commands.
- Keep â€œpit of successâ€ defaults: brief summaries first, detail on demand.
