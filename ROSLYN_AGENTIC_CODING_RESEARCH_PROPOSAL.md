# Research Proposal: Roslyn-Native Agentic Coding for C#/.NET

Date: February 8, 2026  
Status: Active implementation and benchmark validation (proposal remains source of truth for RQs/methodology)

## 1. Executive Summary

This project studies whether C#/.NET agentic coding can be materially improved by replacing text-only operating primitives with Roslyn-native semantic and syntax-aware tooling.

The core idea is not "AST everywhere" dogma. The idea is to empirically test multiple operating theories, from lightweight semantic augmentation to fully structured edit transactions, and determine where each approach wins or fails.

Research stance:

- engineering quality first (production-credible tools),
- measurement second (credible comparative benchmarks),
- meta-learning third (improving how we run this work),
- report quality fourth (industrial lab style technical report).

## 2. Scope, Priorities, and Boundaries

### 2.1 Scope

- Language and ecosystem: C#/.NET only.
- Corpora: public OSS repositories only.
- Baseline agent environments: Codex CLI and Claude Code first; extensible to others.
- Interface channels under test: plain CLI first, then skill wrappers, then MCP adapters, with an explicit external C# LSP comparator lane.

### 2.2 Non-goals (for this phase)

- Cross-language generalization claims.
- Fixed timeline promises based on human sprint pacing.
- Premature standardization of one universal operation schema before pilots.

### 2.3 Document boundaries (to avoid duplication)

- This proposal defines what to research, why, and how to evaluate.
- `AGENTS.md` defines execution doctrine and day-to-day operating behavior.

## 3. Motivation and Prior Art

### 3.1 Current agentic coding practice

Modern agentic coding systems remain heavily text-centric:

- text retrieval/search,
- textual patching,
- compile/test as downstream feedback loop.

This works surprisingly well, but has predictable failure modes in C#:

- wrong symbol target under lexical ambiguity,
- partial refactors across call graphs,
- signature changes without complete propagation,
- late discovery of semantic errors.

### 3.2 Roslyn opportunity

Roslyn exposes high-fidelity compiler structures that can be used as first-class tools:

- syntax trees (with full-fidelity text representation),
- semantic models and symbol resolution,
- workspace-level solution/project/document operations,
- analyzers and code-fix infrastructure,
- diagnostics APIs for fast feedback loops.

### 3.3 Adjacent ecosystem signal

- Early Roslyn MCP efforts suggest demand but are fragmented.
- Aider-style benchmarking culture shows that tool claims must be leaderboard-grade and reproducible.
- Agent practitioners increasingly emphasize trajectory quality and context engineering, not only patch generation.

## 4. Research Questions

### RQ1: Structured symbol retrieval vs grep-style retrieval

Question: if symbol search returns structured semantic context (hierarchy + local excerpt + container metadata), do current LLMs act more effectively than with grep-like lines?

Structured result candidates include:

- symbol identity (kind, fully qualified name, containing symbol),
- location envelope (file, project, assembly, span),
- hierarchical context (namespace, type chain, method/property/block),
- concise lexical context window,
- relation hints (references, declarations, overrides, implementations).

Evaluation focus:

- target selection accuracy,
- downstream edit correctness,
- token efficiency and prompt reliability.

Important methodological note:

- RQ1 micro-benchmarks (symbol targeting tests) are **diagnostic**, not decisive proof of agentic value.
- Decisive evidence must come from full agent trajectories on realistic tasks where the agent can choose tools.

### RQ2: Syntax-anchored edits vs text edits

Question: does anchoring edits to syntax/semantic structures outperform regex/patch workflows on correctness and change safety?

Dimensions:

- multi-file consistency,
- refactor completeness,
- formatting/trivia preservation quality,
- failure recovery when requested edit is underspecified.

### RQ3: Immediate compiler feedback loops

Question: should diagnostics be injected after each edit operation, in batches, or adaptively? Which feedback format is most effective for the model?

Competing feedback formats:

- raw compiler output,
- condensed diagnostic tuples (id, severity, symbol, location, message),
- structured repair hints (candidate code actions or operation suggestions),
- hybrid "brief summary + expandable detail".

### RQ4: Interface layer effectiveness (CLI vs skill vs MCP vs external C# LSP)

Question: for coding augmentation, what interface stack provides best latency, reliability, debuggability, and adoption?

Planned priority:

- plain CLI primitives,
- skill wrappers over CLI primitives,
- MCP adapter for broader interoperability,
- external C# LSP lane (for example Claude `csharp-lsp`) as a benchmark comparator.

### RQ5: Operation language design

Question: what command language best balances expressive power, determinism, and model usability?

Competing theories:

- low-level verbs (find-symbol, list-references, apply-operation),
- declarative intent schema (goal + constraints + acceptance),
- hybrid staged protocol (intent -> planner -> concrete ops).

## 5. Why Syntax-Directed Editing Has Historically Seen Mixed Human Adoption

This project must not assume that syntax-aware systems automatically win. Historical lessons from structure/projectional editing matter.

### 5.1 Observed friction patterns

- Rigidity during partial thought: strict structure can fight "incomplete" human drafting.
- Interaction cost: projectional editors may require custom gestures/hotkeys and retraining.
- Representation lock-in fear: teams fear losing plain-text portability and tooling ecosystem fit.
- Mixed tasks reality: engineers often interleave precise transforms with rough exploratory text edits.

### 5.2 Implications for our design

- Preserve text fallback everywhere.
- Support incomplete/placeholder states as first-class workflow.
- Minimize mandatory modality shifts.
- Benchmark mixed-mode workflows, not only idealized structured flows.
- Evaluate not only correctness but also operational friction and time-to-green.

## 6. Architecture Hypotheses (Divergent Theories of Operation)

### H1. Semantic navigation, text persistence

- Roslyn powers retrieval/planning.
- Final writes remain text patches.
- Hypothesis: low-risk improvement to targeting with modest integration complexity.

### H2. Structured operation engine

- LLM emits typed edit ops over syntax/semantic anchors.
- Workspace transaction applies op set and re-renders files.
- Hypothesis: major gains for multi-file correctness and refactor safety.

### H3. Intent-to-CodeAction orchestration

- LLM emits high-level intent.
- Planner maps intent to analyzer/code-fix/refactoring sequences.
- Hypothesis: best reliability for recurring transformation classes.

### H4. Constraint-driven transactional gate

- Candidate edits are validated in-memory against diagnostics/analyzer constraints.
- Failed candidates are repaired before persistence.
- Hypothesis: lower silent regression rate at some latency cost.

### H5. Adaptive feedback policy

- System chooses immediate vs batched diagnostics dynamically by task type and uncertainty.
- Hypothesis: better speed/accuracy frontier than fixed feedback policy.

### H6. Context-shaping optimization

- Roslyn-derived retrieval provides compact, high-signal context packs for LLM calls.
- Hypothesis: less token waste and fewer hallucinated edits than lexical retrieval alone.

## 7. Operation Surface and Scenario Expansion Strategy

### 7.1 Broad operation scope (planned early, implemented iteratively)

Core categories:

- symbol discovery and disambiguation,
- semantic navigation and dependency traversal,
- declaration/reference transforms,
- signature and call-site propagation,
- namespace/usings/type constraint edits,
- analyzer/code-fix mediated edits,
- test impact mapping and targeted test selection,
- diagnostics inspection and repair loops.

### 7.2 Scenario generation framework

Generate a comprehensive scenario matrix across:

- task intent (bug fix, refactor, migration, warning cleanup, API evolution),
- scope (single-file, cross-file, cross-project),
- ambiguity level (low, medium, high),
- code health baseline (clean, warning-heavy, failing tests),
- performance constraints (fast-path vs correctness-max mode),
- feedback policy (immediate, batched, adaptive),
- interface channel (CLI, skill wrapper, MCP, external C# LSP).

Deliverable:

- a living scenario catalog used by benchmark and regression suites.

## 8. Interface Strategy and Distribution

### 8.1 Interface stack

1. **Plain CLI first**
   - cross-platform executable with stable help output,
   - scriptable in shells and CI,
   - easiest for agents to self-discover via `--help`,
   - explicit pit-of-success entrypoint (`quickstart`) plus per-command schema discovery (`describe-command`).
2. **Skill wrappers second**
   - workflow orchestration and prompt guidance over CLI primitives.
3. **MCP adapter third**
   - interoperability layer after CLI contracts stabilize.
4. **External C# LSP comparator lane (parallel)**
   - benchmark against an established LSP path (for example Claude `csharp-lsp`) using matched prompts and acceptance gates.

### 8.2 Pit-of-success requirements

Every interface lane must provide:

- a short startup path that minimizes argument guessing,
- explicit guardrails for common failure modes (for example file-type misuse),
- explicit visibility into workspace binding for file-scoped commands (`workspace_context.mode`) plus an override path (`workspace_path`) when calls are launched from disconnected contexts,
- copy-paste-safe examples for both simple and structured payloads,
- release artifacts that place guidance near runnable launchers.

Architecture or UX changes should be evaluated against "first-minute success" as a primary constraint, not only feature completeness.

### 8.3 Distribution and dogfooding

- Implement tooling in C# to dogfood Roslyn-centric development.
- Package for practical adoption:
  - `dotnet tool` distribution,
  - versioned binary releases,
  - MIT license for all artifacts.

## 9. Evaluation Design

### 9.0 Evaluation hierarchy (realism-first)

Use a layered evaluation model:

- **Layer A: Component diagnostics**  
  Fast micro-benchmarks for retrieval/edit/diagnostic packet behavior (including current RQ1 slice).
- **Layer B: Agent-in-the-loop A/B trials (primary evidence)**  
  Same tasks, same prompts, same model policy; compare conditions where Roslyn tools are unavailable vs available.
- **Layer C: Robustness and scale checks**  
  Multi-run variance, holdout tasks, and broader corpus sweeps.

Decision-making should prioritize Layer B/C evidence over Layer A.

Benchmark design inspiration:

- Use end-to-end, issue-oriented methodology patterns similar to SWE-bench style evaluation, adapted for C# and Roslyn-specific tool-choice questions.

### 9.1 Baseline families

- **B0**: text-first (grep/patch style flow).
- **B1**: H1 semantic retrieval + text writes.
- **B2**: H2 structured edit engine.
- **B3**: H3/H4 orchestration and transactional gating.
- **B4**: external C# LSP-assisted flow (no RoslynSkills commands available).

### 9.2 Model policy

- Start with leading closed models available through Codex CLI and Claude Code paths.
- Include diversity sweeps with smaller/faster variants.
- Keep scaffolding open for optional open-weights integrations (for example, Kimi or comparable endpoints).

### 9.3 Metrics

Primary:

- task success rate,
- first-pass compile success,
- regression introduction rate,
- reviewer cleanup burden.

Secondary:

- time-to-green,
- token/tool-call cost,
- failure-mode distribution,
- retrieval-to-success efficiency.

Behavioral/adoption:

- Roslyn tools offered vs Roslyn tools used rate.
- Roslyn call share among all tool calls.
- Time-to-first Roslyn call.
- Success conditional on Roslyn usage vs non-usage (within Roslyn-enabled condition).
- Explicit post-task agent self-report on tool helpfulness (structured, short-form, not hidden reasoning).
- First-minute onboarding success rate (valid semantic command path reached without argument/file-type confusion).
- Argument-discovery churn (extra turns/retries before first successful tool call).

### 9.4 Human involvement

- Human support expected for environment setup and repository preparation.
- Human review introduced once automated runs are stable enough to avoid noisy scoring.

### 9.5 Agent-in-the-loop trial protocol

For each task and model configuration, run at least:

- **Condition A (control)**: no Roslyn tools exposed.
- **Condition B (treatment)**: Roslyn tools exposed, optional usage.
- **Condition C (LSP comparator)**: external C# LSP tools exposed, Roslyn tools hidden.
- **Condition D (optional mixed lane)**: Roslyn + external C# LSP both exposed.

Both conditions must keep:

- same task statement style,
- same acceptance checks,
- same resource limits,
- same base prompt policy.

At run end, require a short structured self-report field such as:

- which tools were most useful,
- whether Roslyn tools helped or were ignored,
- what was missing in tool capability.

This captures practical utility and adoption, not only raw correctness.

## 10. Program Structure (Dependency-Graph, Not Calendar)

### 10.1 Work packages

- **N0**: harness + telemetry substrate.
- **N1**: baseline B0 implementation.
- **N2**: H1 implementation.
- **N3**: H2 implementation.
- **N4**: H3/H4 and feedback-policy experiments.
- **N5**: comparative study + decision package + publication assets.

### 10.2 Dependencies

- `N0 -> N1`
- `N0 -> N2`
- `N2 -> N3`
- `N3 -> N4`
- `N1 + N2 + N3 + N4 -> N5`

### 10.3 Advancement gates

- No fixed pass/fail threshold is imposed upfront.
- Each node advances on empirical signal quality and engineering readiness.
- Features can be de-scoped by context if evidence shows poor value in specific usage regimes.

## 11. Expected Outputs

1. Production-grade Roslyn augmentation tooling (at least one path ready for wider promotion).
2. Extensive benchmark and regression suite for quantitative impact analysis.
3. Industrial technical report (Bell Labs / Xerox PARC style):
   - premise,
   - hypotheses,
   - implementation approach,
   - results and ablations,
   - limitations and next questions.
4. Meta-learning report integrated with `AGENTS.md`:
   - what worked in this research process,
   - what did not,
   - how to improve future agentic R&D execution.

## 12. Risks and Guardrails

- Overfitting to one benchmark style:
  - mitigate with diverse scenario matrix and holdout tasks.
- Tooling ambition outrunning reliability:
  - enforce production-quality gates before claiming benchmark wins.
- Structured-edit complexity overhead:
  - maintain text fallback and incremental rollout.
- Interface fragmentation:
  - stabilize CLI contract first, then layer skills/MCP adapters and keep external LSP comparisons isolated by condition.

## 13. Immediate Next Steps

1. Produce detailed research plan with actionable projects and acceptance criteria.
2. Define operation-surface taxonomy and command-language candidates.
3. Design benchmark scaffolding requirements and artifact schema.
4. Draft cross-referenced detailed design and implementation/test plans.
5. Implement agent-in-the-loop A/B evaluation protocol with tool-choice and self-report logging.
6. Begin implementation on unblocked highest-value nodes.

---

## References (Primary Sources)

Core platform and tooling:

1. Roslyn repository: https://github.com/dotnet/roslyn
2. Roslyn syntax APIs: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-syntax
3. Roslyn semantic analysis: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis
4. Roslyn workspace model: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace
5. Roslyn analyzer/code-fix tutorial: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/tutorials/how-to-write-csharp-analyzer-code-fix
6. .NET compiler diagnostics APIs: https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.getdiagnostics?view=roslyn-dotnet-4.14.0

Agent interfaces and ecosystems:

7. Anthropic text editor tool docs: https://docs.anthropic.com/en/docs/agents-and-tools/tool-use/text-editor-tool
8. OpenAI Codex repository: https://github.com/openai/codex
9. Aider leaderboards: https://aider.chat/docs/leaderboards/

Roslyn agent-adjacent implementations:

10. SharpToolsMCP: https://github.com/kooshi/SharpToolsMCP
11. roslyn-mcp: https://github.com/egorpavlikhin/roslyn-mcp
12. RoslynMCP: https://github.com/carquiza/RoslynMCP

Syntax/projectional editing and mixed adoption context:

13. Hazelnut (structure editing foundations): https://arxiv.org/abs/1703.08694
14. Efficiency of projectional editing (controlled experiment): https://repository.ubn.ru.nl/bitstream/handle/2066/205212/205212pub.pdf
15. Comprehension vs. Adoption (language workbench study): https://arxiv.org/abs/2610.16383
16. Martin Fowler on language workbenches and lock-in concerns: https://martinfowler.com/articles/languageWorkbench.html

Meta-study inputs on agentic coding practice:

17. Andrej Karpathy, "Vibe coding MenuGen": https://karpathy.bearblog.dev/vibe-coding-menugen/
18. Andrej Karpathy, "2025 LLM Year in Review": https://karpathy.bearblog.dev/2025-llm-year-in-review/
19. Erik Meijer, SPLASH 2025 keynote listing: https://2025.splashcon.org/profile/erikmeijer
20. Erik Meijer, Rebase 2024 abstract: https://www.rebase.co/2024/speakers/erikmeijer
21. Jeffrey Emanuel repositories: https://github.com/Dicklesworthstone
22. Peter Steinberger, "How I ship side projects at lightning speed with AI coding agents": https://steipete.me/posts/2025/how-i-ship-side-projects-at-lightning-speed-with-ai-coding-agents
23. Peter Steinberger, "MCP at Scale": https://steipete.me/posts/2025/mcp-at-scale
24. Paul Gauthier, Aider repository: https://github.com/Aider-AI/aider
25. SWE-bench repository: https://github.com/princeton-nlp/SWE-bench
26. Agentless repository: https://github.com/OpenAutoCoder/Agentless
27. OpenHands R2E2 benchmark write-up: https://www.all-hands.dev/blog/r2e2-realistic-repo-level-benchmark-for-ai-coding-agents
28. csharp-ls repository: https://github.com/razzmatazz/csharp-language-server
29. Claude official C# LSP plugin: https://github.com/anthropics/claude-plugins-official/tree/main/plugins/csharp-lsp
30. dotnet-inspect repository: https://github.com/richlander/dotnet-inspect
31. dotnet-skills repository: https://github.com/richlander/dotnet-skills
