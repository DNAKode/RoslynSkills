# Whole-Solution Plan (2026-03-25)

Date: 2026-03-25  
Owner: active coding session  
Status: planning artifact grounded in measured `Aims` evidence

## Problem Statement

`roscli` now shows a real warm-path win inside one process, but the cold whole-solution bind is still the dominating cost on large real solutions.

Measured evidence (`C:\Work\RoninSoftware\Aims`):

- tracked evidence note: `benchmarks/results/roscli-vs-rg-aims-20260322-223301.md`
- query family: three member-anchor lookups (`UpdateContentAt`, `AuditCompletionStoreContentViewModel`, `UpdateCompletionsFirstTime`)
- results:
  - `rg`: `66.75ms` total (`22.25ms` avg)
  - `roscli_single`: `106075.043ms` total (`35358.348ms` avg)
  - `roscli_batch`: `36171.18ms` total with `2/3` cache hits
  - inside `roscli_batch`:
    - first query: `34921ms`, `workspace_cache_hit=false`
    - second query: `6ms`, `workspace_cache_hit=true`
    - third query: `12ms`, `workspace_cache_hit=true`

Interpretation:

- Roslyn query execution after whole-solution binding can already be fast enough for agent loops.
- The project condemnation remains valid for cold starts: the first workspace bind is still too slow.
- The next system objective is not "make every single one-shot CLI call beat `rg`"; it is "pay the whole-solution bind once, keep it hot, and make warm semantic queries the default path."

## Decision

Adopt a daemon-first whole-solution architecture for Roslyn-heavy sessions.

Principle:

- `rg` remains the right baseline for raw text grep.
- `roscli` must win on agent usefulness by offering:
  - one explicit whole-solution load,
  - cheap repeated semantic queries after load,
  - lower query ambiguity than raw text,
  - fail-closed correctness when strict workspace semantics are required.

## Scope

In scope:

- whole-solution preload and bound-workspace reuse
- transport/daemon-first execution path
- command-surface additions for explicit workspace lifecycle
- benchmark methodology for cold vs warm vs `rg`
- correctness/invalidation rules for long-lived workspaces

Out of scope for this slice:

- full cross-process serialized Roslyn object caching
- claiming `roscli` should replace `rg` for all text search
- broad rewrite of existing command catalog
- generalized XML/XAML cache work

## Target UX

Canonical high-volume path:

1. `roscli workspace.preload <solution-path>`
2. `roscli workspace.status <handle>`
3. repeated `nav.*`, `ctx.*`, `diag.*`, `query.batch` calls using the bound handle
4. `roscli workspace.close <handle>` when done

Wrapper posture:

- transport/daemon preferred when available
- direct CLI fallback remains supported
- user/agent should not need to rediscover the solution on every call
- solution files (`.sln`/`.slnx`) are the default target for hot workspace hosts; project files are supported only for intentional project-scoped work

## Proposed Command Surface

### New lifecycle commands

- `workspace.preload`
  - input: `workspace_path`, optional `mode=balanced|strict`, optional `include_generated`
  - preferred input: `.sln`/`.slnx`; `.csproj/.vbproj` is allowed only for intentional project-scoped hosts
  - output: `workspace_handle`, `workspace_fingerprint`, `projects_loaded`, `documents_loaded`, `workspace_load_duration_ms`, `cache_mode`
- `workspace.status`
  - output: `workspace_handle`, `loaded`, `dirty`, `last_refresh_utc`, `tracked_project_count`, `tracked_document_count`, `invalidated_paths`
- `workspace.refresh`
  - explicit reload/rebind for changed workspaces or strict-mode promotion runs
- `workspace.close`
  - dispose bound workspace state

### Existing commands extended

- `nav.find_symbol`, `nav.find_references`, `nav.find_invocations`, `ctx.member_source`, `diag.get_file_diagnostics`, `query.batch`
  - accept `workspace_handle`
  - prefer bound workspace over auto-inference when handle is supplied
- `query.batch`
  - support a shared `workspace_handle` at top level to avoid per-query workspace resolution

### Wrapper/transport behavior

- `roscli` wrapper should attempt daemon transport when `ROSCLI_PREFER_TRANSPORT=1` or equivalent default-ready mode is enabled
- fallback to direct CLI if:
  - daemon unavailable,
  - protocol mismatch,
  - requested command is unsupported in transport lane

## Architecture

### Phase A: Bound workspace host

Use the existing long-lived transport process as the first real whole-solution host.

Responsibilities:

- own loaded `MSBuildWorkspace` and current `Solution`
- maintain a `workspace_handle -> loaded workspace state` table
- expose lifecycle commands over the same registry/protocol surface

### Phase B: Balanced invalidation

Default interactive invalidation policy:

- always track:
  - solution/project files
  - the explicitly queried source file
  - workspace-handle generation/reload counter
- ignore volatile `obj/` and `bin/` churn for hot-path reuse
- mark workspace dirty when watched stable inputs change
- allow explicit `workspace.refresh` to reconcile broader changes

Strict benchmark/promotion mode:

- broader fingerprinting or full reload before scoring
- no claims based on balanced-mode speedups alone if strict mode contradicts correctness

### Phase C: Auto-connect CLI path

After the daemon lane is stable:

- wrapper auto-connects to a local daemon keyed by workspace path
- one-shot CLI remains available as a fallback/debug mode

## Dependency Graph

- `N0` Benchmark baseline lock
  - Keep `Aims` benchmark scenario and report path as the reference cold/warm member-anchor slice.
  - Deps: none.
- `N1` Workspace lifecycle contract
  - Define `workspace.preload/status/refresh/close` schemas and CLI shorthand.
  - Deps: `N0`.
- `N2` Transport-host workspace registry
  - Add bound-workspace table and lifecycle command execution inside transport server.
  - Deps: `N1`.
- `N3` Command binding
  - Teach high-traffic read commands to consume `workspace_handle`.
  - Deps: `N2`.
- `N4` Invalidation policy
  - Implement balanced invalidation + dirty-state surfacing.
  - Deps: `N2`.
- `N5` Wrapper auto-connect prototype
  - Add daemon preference/fallback in `scripts/roscli*`.
  - Deps: `N2`, `N3`.
- `N6` Comparator benchmark pack
  - Extend `Benchmark-RoscliVsRgQueries.ps1` with:
    - cold single-call
    - cold preload
    - warm bound-handle
    - `rg`
  - Deps: `N3`, `N4`.
- `N7` Agent-in-loop validation
  - Run paired trajectories where treatment uses preload + bound workspace.
  - Deps: `N5`, `N6`.

Critical path:

`N0 -> N1 -> N2 -> N3 -> N4 -> N6 -> N7`

## Acceptance Gates

### Functional gates

- `workspace.preload` successfully binds a real `.sln/.slnx` and returns a stable handle with `projects_loaded > 1` on multi-project solutions.
- `.csproj/.vbproj` preload remains supported for explicit project scope but must be labeled as project-scoped in output telemetry.
- Subsequent read commands using the same handle do not re-run workspace discovery.
- `workspace.status` reflects dirty/reload state explicitly.
- `workspace.close` releases host-side state.

### Performance gates

For the `Aims` member-anchor scenario:

- warm bound-workspace queries after preload should stay in the low-millisecond range on the same handle
- `workspace_cache_hit=true` (or equivalent bound-hit signal) must be observable on post-preload queries
- cold preload must be reported separately from warm queries
- benchmark artifacts must show:
  - preload wall time
  - warm query wall time
  - `rg` wall time
  - workspace mode / cache mode / handle usage

### Research gates

- no "Roslyn beats `rg`" headline claim from one-shot cold-call data
- whole-solution plan is only promotable if bound-workspace treatment improves agent trajectory usefulness, not just component timings
- strict-mode correctness spot checks must not regress relative to current workspace-backed behavior

## Benchmark Matrix

Primary comparator conditions for this slice:

- `rg_text_only`
- `roscli_single_cold`
- `roscli_batch_single_process`
- `roscli_preload_then_bound_queries`
- `roscli_transport_bound_queries`

Primary task families:

- member-anchor lookup
- ambiguous type/member disambiguation
- cross-file reference chase
- diagnostics triage on project-backed files

Metrics:

- cold preload wall time
- first useful answer latency
- warm query p50/p95
- cache-hit rate / handle-hit rate
- command round-trips
- output size / token proxy
- workspace fallback rate

## Rollout Strategy

### Slice 1

- command contracts + transport-host registry
- manual preload and explicit handle usage
- benchmark harness update

### Slice 2

- wrapper auto-connect
- daemon lifecycle management and reuse policy
- better status/dirty reporting

### Slice 3

- strict-mode refresh flows
- broader agent-in-loop A/B runs
- promotion decision on daemon-preferred default posture

## Risks

- Long-lived workspace state can go stale in subtle ways if invalidation is too weak.
- Overly strict invalidation can erase the warm-path advantage and recreate the current cold-start problem.
- Auto-connect behavior can add confusing failure modes unless fallback is explicit and observable.
- Memory footprint of multiple loaded solutions may require bounded eviction and policy tuning.

## Rollback / Fallback

- If transport-host workspace binding proves unstable, keep the current batch/single-process cache path as the bounded fallback.
- If auto-connect causes reliability regressions, gate it behind an opt-in env var and keep direct CLI as the default.
- If balanced invalidation is too risky for benchmark promotion, keep balanced for interactive use and require strict reloads for scored runs.

## Immediate Next Actions

1. Define and implement `workspace.preload`, `workspace.status`, and `workspace.close`.
2. Extend `query.batch` and high-traffic `nav.*` commands to accept `workspace_handle`.
3. Upgrade `Benchmark-RoscliVsRgQueries.ps1` to include explicit preload/bound-handle lanes.
4. Run the updated benchmark again on `Aims` and at least one additional large OSS solution before changing wrapper defaults.
