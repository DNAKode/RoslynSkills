RoslynSkills is a Roslyn-native CLI/MCP toolchain (`roscli`) plus benchmark harnesses to test whether semantic tooling improves agent coding outcomes vs text-first baselines. Latest local release build is `v0.1.6-preview.17` (bundle + nupkg built via `scripts/release/Build-ReleaseArtifacts.ps1`), with outputs under `artifacts/release/0.1.6-preview.17/`; the current rollup/matrix remains `benchmarks/experiments/20260213-approach-matrix-v0.1.6-preview.14.md` with supporting narrative in `RESEARCH_FINDINGS.md` and the active plan slice in `DETAILED_RESEARCH_PLAN.md` (P8.1).

The most important recent correctness finding is a workspace false-positive: `workspace_context.mode=workspace` can still yield nonsense diagnostics like `CS0518` (core types missing) if MSBuildWorkspace binds to an incompatible MSBuild instance (often VS MSBuild lagging preview TFMs). This is fixed in `src/RoslynSkills.Core/Commands/WorkspaceSemanticLoader.cs` by preferring `.NET SDK` MSBuild (`DiscoveryType.DotNetSdk`) during MSBuildLocator registration; it is regression-covered by `tests/RoslynSkills.Core.Tests/WorkspaceSemanticLoaderTests.cs` and documented as a benchmark validity gate in `ROSLYN_AGENTIC_CODING_RESEARCH_PROPOSAL.md` and `docs/WORKSPACE_CONTEXT_SMOKE.md`.

For fast roscli-vs-base comparisons, the primary harness is `benchmarks/scripts/Run-PairedAgentRuns.ps1`. It supports Codex reasoning effort via `-CodexReasoningEffort` (wired to `codex exec -c model_reasoning_effort="..."`), has guidance profiles including `brief-first-v2`/`workspace-locked`/`diagnostics-first`/`edit-then-verify`, and has an integrity switch `-FailOnMissingTreatmentRoslynUsage` to prevent treatment lanes silently becoming text-only; harness behavior is pinned by `tests/RoslynSkills.Benchmark.Tests/PairedRunHarnessScriptTests.cs`.

Large OSS realism runs are orchestrated by `benchmarks/scripts/Run-OssCsharpPilotRealRuns.ps1` and `benchmarks/experiments/oss-csharp-pilot-v1/manifest.json`. The OSS harness now supports a fail-closed treatment-required condition (`treatment-roslyn-required`) so treatment lanes cannot silently complete with zero Roslyn tool calls. A MediatR treatment-required run is recorded under:

- artifacts: `artifacts/real-agent-runs/20260213-110716-oss-csharp-pilot/*`
- run record: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-110716-oss-csharp-pilot/run-codex-treatment-roslyn-required-mediatr-behavior-targeting-brief-first-r01.json`

The OSS pilot now defines an explicit primary comparison pair (control vs treatment-required) via `primary_control_condition_id`/`primary_treatment_condition_id` in the manifest, and `agent-eval-gate` supports multi-condition experiments accordingly. The OSS harness also now writes schema-aligned `tools_offered`/`tool_calls` fields so OSS run records can be validated/scored with the shared `agent-eval-*` pipeline; older OSS run records can be backfilled via `benchmarks/scripts/Upgrade-OssPilotRunRecords.ps1`.

Latest OSS scope expansion bundle (Codex Spark, low, `brief-first-v4`) with passing gate:

- artifacts: `artifacts/real-agent-runs/20260213-142746-oss-csharp-pilot/*`
- run records: `benchmarks/experiments/oss-csharp-pilot-v1/runs/20260213-142746-oss-csharp-pilot/*.json`
- gate summary: `artifacts/agent-eval/20260213-145801/summary/agent-eval-summary.md`

Immediate next steps: continue roscli vs base comparisons varying prompt posture, model (`gpt-5.3-codex` vs `gpt-5.3-codex-spark`), and reasoning effort, while enforcing validity gates (project tasks must show healthy workspace binding: mode=workspace plus no `CS0518`; treatment runs should fail closed if tools were not used). Use `benchmarks/scripts/Run-PairedAgentRuns.ps1 -SkipClaude -TaskShape project -RoslynGuidanceProfile brief-first-v2 -CodexModel gpt-5.3-codex-spark -CodexReasoningEffort low -FailOnMissingTreatmentRoslynUsage` for microtask sweeps; keep LSP-via-MCP as experimental until timeout/indexing confounds are resolved (see `benchmarks/LSP_COMPARATOR_PLAN.md`).

Checkpoint (2026-02-13): `docs/CHECKPOINT_2026-02-13.md` summarizes the current empirical posture (microtask overhead vs ambiguity value hypothesis) and the pivot to Claude “skill-style” guidance iteration (new paired profile `skill-tight-v1`, which is tuned for Claude to invoke `bash scripts/roscli ...` and clamp output to 2 lines).

Checkpoint (2026-02-17): `docs/CHECKPOINT_2026-02-17.md` captures the widened non-rename scope pass. `Run-PairedAgentRuns.ps1` now supports mixed operation families (`change-signature`, `update-usings`, `add-member`, `replace-member-body`, `create-file`) and a new `operation-neutral-v1` profile to avoid rename-specific guidance leakage. Recent artifacts:

- Claude wide sweep (3 tasks x 4 conditions): `artifacts/skill-tests/20260217-wide-claude-v1/skill-trigger-summary.md`
- Claude paired spot check with `operation-neutral-v1`: `artifacts/real-agent-runs/20260217-claude-opneutral-addmember-v2/paired-run-summary.md`

Current empirical posture in this slice:

- Explicit skill invocation materially increases Roslyn adoption (`with-skill-invoked roslyn_used_rate=1.0` in the wide sweep).
- Non-invoked/budgeted lanes still show adoption instability.
- Treatment correctness is now stable on the add-member paired check after relaxing style-only constraint strictness; overhead remains significant (round-trips and tokens).
- Split-lane tool-thinking analysis now distinguishes "Roslyn used" vs "Roslyn used well" via productive-call metrics (`productive_roslyn_command_count`, `roslyn_used_well_score`) to avoid false positives from schema-probe-only usage.
