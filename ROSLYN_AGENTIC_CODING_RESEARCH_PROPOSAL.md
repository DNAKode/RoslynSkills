# Research Proposal: Roslyn-Native Agentic Coding for C#/.NET

Date: February 8, 2026  
Status: Planning / scoping document

## 1. Executive Summary

Modern terminal-style coding agents (Claude Code, Codex CLI style workflows, and similar tools) are strong at iterative execution but usually operate on **text files** as their editing substrate. For C#/.NET codebases, this leaves a lot of capability untapped: Roslyn offers full-fidelity syntax trees, semantic binding, symbol graphs, analyzers, and refactoring/code-fix infrastructure.

This project proposes a rigorous research program to design, implement, and evaluate a **Roslyn-based coding skill** for agentic workflows, and to benchmark it against current text-first practice.

The core question:  
Can Roslyn-backed structured operations materially improve correctness, reliability, and reviewer trust in agentic coding for C#/.NET?

## 2. Landscape Review: What Exists Today

### 2.1 Text-first agent editing is still dominant

- Anthropic's text editor tool is explicitly command-based around file viewing and textual edits (`view`, `str_replace`, `insert`, `create`, `undo_edit` for older versions). This is robust and general, but primarily string-edit oriented.
- OpenAI Codex (cloud and CLI ecosystem) emphasizes reading/editing files, running tests/linters, and command execution in sandboxed environments.

Interpretation: mainstream agent tooling is operationally strong, but generally does not expose compiler-native edit transactions as the primary primitive.

### 2.2 Roslyn provides a richer substrate

Roslyn's official docs and APIs provide:

- Full-fidelity immutable syntax trees (including trivia/comments/whitespace and malformed code states).
- Semantic models that answer scope, type, symbol, and binding questions.
- Workspace model over full solutions/projects/documents.
- Solution-level change application (`Workspace.TryApplyChanges`).
- Analyzer + code-fix infrastructure for structured diagnostics and automated repairs.

Interpretation: the platform has all the primitives needed for semantically directed agent behavior, not just text mutations.

### 2.3 Existing Roslyn-for-agent projects (early prior art)

Emerging OSS work suggests strong interest but no clear standard architecture yet:

- **SharpToolsMCP**: ambitious Roslyn-powered MCP suite for analysis + modification operations and symbol-centric workflows.
- **roslyn-mcp**: lean MCP server focused on file validation + usage finding in project context.
- **RoslynMCP**: MCP server focused on symbol search, references, dependency/complexity analysis.

Interpretation: there is active experimentation, but mostly fragmented tools rather than a rigorously evaluated end-to-end research program with benchmarked outcomes versus text-first agents.

### 2.4 Adjacent mature Roslyn ecosystems

- OmniSharp (Roslyn workspace-based language server platform).
- Roslyn analyzers/code-fixes ecosystem (including Microsoft analyzer tutorials and Roslynator-style tools).

Interpretation: non-agent tooling already proves Roslyn at scale for IDE semantics and safe refactorings; the gap is integrating this deeply into autonomous/agentic coding loops.

## 3. Research Objectives

1. Design and implement a reusable **Roslyn-based coding skill** for terminal-style coding agents.
2. Compare multiple architectural theories of operation (not just one implementation path).
3. Build a comprehensive benchmark and test harness to compare against text-first baselines.
4. Produce decision-grade evidence on quality, speed, failure modes, and developer review burden.

## 4. Hypotheses (Divergent Theories of Operation)

### H1. Roslyn-as-Intelligence, Text-as-Edit (Navigation-first hybrid)

Use Roslyn for high-signal retrieval/planning:

- symbol search, call graph, references, type hierarchy, semantic diagnostics, target selection.

But final edits remain text diffs (`apply_patch`/replace/insert workflows).

Prediction:

- Better target selection and fewer wrong-file edits vs text-only baseline.
- Moderate quality gains with low integration risk.

### H2. Roslyn-Native Syntax/Semantic Editing (Structured-edit first)

Represent edits as typed operations:

- add/replace member
- rename symbol
- change signature + propagate call sites
- update usings/attributes
- apply code fix or refactoring provider

Then render text from updated syntax trees/solutions.

Prediction:

- Significant correctness and compile-pass improvements on multi-file and symbol-sensitive tasks.
- Higher engineering complexity and potential model/tool impedance mismatch.

### H3. Intent-to-CodeAction Orchestration (Planner delegates to Roslyn transformations)

LLM emits high-level intents, not concrete patches:

- "Make method async and update callers"
- "Extract interface from class X"
- "Fix nullable warnings in module Y"

A Roslyn planner maps intents to chains of analyzers/code-fixes/refactorings + guarded custom transforms.

Prediction:

- Highest reliability on recurring transformation classes.
- Best path for enterprise governance and policy controls.

### H4. Constraint-Driven Transactional Editing (Compiler as hard gate)

Agent proposes candidate changes, but commit path is transactional:

- apply in memory
- run semantic + analyzer constraints
- reject/repair if constraints fail
- only persist accepted transactions

Prediction:

- Lower silent-regression rate and better safety for autonomous operation.
- Possible latency/cost tradeoff from stricter validation loops.

## 5. Proposed Skill Architecture

### 5.1 Core components (shared across hypotheses)

1. **Workspace Host**
   - Loads `.sln/.csproj` via `MSBuildWorkspace`.
   - Maintains current immutable `Solution` snapshot + incremental updates.
2. **Semantic Index Layer**
   - Symbol graph, references, dependency summaries, complexity metadata.
3. **Operation Layer**
   - Analysis tools: find symbol, references, diagnostics, semantic queries.
   - Edit tools (hypothesis-dependent): text patch vs structured transforms.
4. **Validation Layer**
   - Build/compile checks, analyzer checks, formatting normalization, test execution.
5. **Agent Adapter**
   - MCP/CLI-compatible interface exposing small, composable tools.
6. **Telemetry + Trace**
   - Captures tool-call sequences, compile states, diff sizes, and failure causes.

### 5.2 Skill packaging concept

Target a reusable skill package with:

- `SKILL.md` workflow instructions for Roslyn-aware operation.
- `scripts/` for deterministic workspace loading, symbol queries, structured edits.
- `references/` for operation schemas and known edge cases.
- Optional `assets/` for benchmark fixtures and trace templates.

## 6. Research Plan and Phases

### Phase 0: Benchmark & Instrumentation Foundation (Weeks 1-4)

- Build reproducible harness for running agent variants on fixed tasks.
- Implement baseline runner (text-first tools only).
- Define metrics, schemas, and artifact capture.

### Phase 1: H1 Prototype (Weeks 5-8)

- Roslyn navigation/analysis tools + text edit persistence.
- Compare to baseline on navigation-heavy and bug-fix tasks.

### Phase 2: H2 Prototype (Weeks 9-14)

- Structured edit operation set with solution transactions.
- Add recovery path when operation synthesis fails (fallback to H1 text edit).

### Phase 3: H3/H4 Prototypes (Weeks 15-20)

- Intent-to-code-action planner.
- Constraint-driven transactional gate.

### Phase 4: Comparative Study + Recommendation (Weeks 21-24)

- Full benchmark runs, ablations, human review study, final decision report.

## 7. Comprehensive Evaluation Suite

### 7.1 Baseline systems

1. **B0 Text-only**: grep/ripgrep + string replace/patch + command/test loop.
2. **B1 H1**: Roslyn analysis + text edit.
3. **B2 H2**: Roslyn structured edits.
4. **B3 H3/H4**: intent orchestration + transactional constraints.

### 7.2 Task families (C#/.NET focused)

1. Single-file bug fix with type-sensitive logic.
2. Cross-file API change (signature updates and call-site propagation).
3. Safe symbol rename across solution.
4. Nullability and analyzer-warning remediation.
5. Refactoring requiring structural transforms (extract member/interface, move type).
6. Dependency-aware change touching project boundaries.
7. Test-update tasks requiring semantic alignment.

### 7.3 Corpus strategy

- Curated real OSS issue tasks (C# repositories).
- Synthetic but realistic mutation tasks with known ground truth.
- Analyzer/code-fix derived transformation tasks.
- Internal "golden tasks" with human-authored expected outcomes.

### 7.4 Metrics

Primary:

- Task success rate (tests/build pass + acceptance criteria).
- First-pass compile success.
- Regression rate (new diagnostics/test failures introduced).
- Reviewer edit distance (human cleanup required).

Secondary:

- Time-to-green.
- Token/tool-call budget.
- Diff entropy/churn (changed LOC vs necessary LOC).
- Failure taxonomy (wrong target, partial edit, semantic breakage, etc.).

Safety/robustness:

- Constraint violations caught pre-write.
- Recovery success after failed candidate edits.

### 7.5 Test suite levels

1. **Unit tests** for each Roslyn tool operation.
2. **Property tests** for syntax/semantic invariants and round-trips.
3. **Integration tests** on fixture solutions (multi-project, generated code, partials, global usings).
4. **End-to-end agent tests** in isolated sandboxes with deterministic seeds.
5. **Adversarial tests** (ambiguous symbols, overloaded APIs, hidden generated sources).
6. **Performance tests** on small/medium/large solutions.

## 8. Experimental Methodology

- Fixed prompt templates across variants to control confounding.
- Same model family and budget limits per run.
- Randomized task order and repeated trials per task.
- Blinded human review scoring for diff quality and maintainability.
- Pre-registered success criteria before full runs.

## 9. Risks and Mitigations

1. **Roslyn workspace load complexity**  
Mitigation: warm caches, incremental loading, robust workspace diagnostics.

2. **Model-tool mismatch for structured operations**  
Mitigation: strict schemas, operation validation, fallback to H1.

3. **Latency inflation from compile/analyzer loops**  
Mitigation: staged validation (fast checks first, full checks on candidate shortlist).

4. **Benchmark overfitting**  
Mitigation: holdout tasks and periodic task refresh.

5. **Generated/source-linked code edge cases**  
Mitigation: explicit policies for generated files and non-editable sources.

## 10. Expected Deliverables

1. Roslyn-based coding skill prototype(s) for agentic use.
2. Reproducible benchmark harness and dataset manifests.
3. Comparative results report with recommendation on preferred architecture.
4. Failure analysis catalog and operational playbook.
5. Implementation roadmap for production hardening.

## 11. Decision Criteria at Project End

Recommend productionization only if at least one Roslyn-enabled variant shows:

- materially higher task success and lower regression rate than text-only baseline,
- neutral or improved reviewer burden,
- acceptable latency/cost envelope for real workflows.

If gains are narrow, adopt phased strategy:

- deploy H1 broadly first, then selectively introduce H2/H3 on high-value task classes.

## 12. Adjacent Ideas Worth Tracking

- Feeding analyzer diagnostics/SARIF as first-class planning context.
- Leveraging Roslyn code-style and naming policies as hard constraints.
- Integrating source generators and incremental generators into evaluation tasks.
- Learned retrieval over symbol graphs (not just lexical retrieval).

---

## References (Primary Sources)

1. Roslyn repo overview: https://github.com/dotnet/roslyn  
2. Roslyn syntax model docs: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-syntax  
3. Roslyn semantic analysis docs: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis  
4. Roslyn workspace model docs: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace  
5. `Workspace.TryApplyChanges` API docs: https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.workspace.tryapplychanges?view=roslyn-dotnet-4.13.0  
6. Roslyn analyzer + code-fix tutorial: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/tutorials/how-to-write-csharp-analyzer-code-fix  
7. OmniSharp Roslyn workspace server: https://github.com/OmniSharp/omnisharp-roslyn  
8. Anthropic text editor tool (str_replace-based operations): https://platform.claude.com/docs/en/agents-and-tools/tool-use/text-editor-tool  
9. OpenAI Introducing Codex (agent workflow context): https://openai.com/index/introducing-codex/  
10. SharpToolsMCP (Roslyn-based AI coding tool suite): https://github.com/kooshi/SharpToolsMCP  
11. roslyn-mcp (Roslyn MCP server): https://github.com/egorpavlikhin/roslyn-mcp  
12. RoslynMCP (Roslyn MCP server): https://github.com/carquiza/RoslynMCP  
