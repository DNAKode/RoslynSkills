# Cache Architecture Options (CLI + Workspace)

Date: 2026-02-25  
Status: design/backlog document

## Problem

We want faster repeated CLI interactions and fewer redundant parses/loads, while staying correct when files changed between invocations.

Key requirement:
- If files did not change, reuse pre-cached parse/analysis structures.
- If files changed, detect it reliably and avoid stale semantic results.

## Current State (What We Have Now)

### Launcher/Binary cache

- `roscli` and `xmlcli` support published-binary cache mode via env flags, otherwise default to `dotnet run`.
- This reduces startup/build overhead, not semantic/parse recomputation per command.

References:
- `scripts/roscli:21`
- `scripts/roscli:57`
- `scripts/roscli:63`
- `scripts/roscli.cmd:19`
- `scripts/roscli.cmd:59`
- `scripts/roscli.cmd:73`
- `scripts/xmlcli:14`
- `scripts/xmlcli:50`
- `scripts/xmlcli:56`
- `scripts/xmlcli.cmd:14`
- `scripts/xmlcli.cmd:54`
- `scripts/xmlcli.cmd:68`
- `scripts/roscli-stable:7`
- `scripts/xmlcli-stable:7`

### Roslyn workspace behavior

- Workspace loading is done per command call in CLI mode.
- Loader tries to resolve workspace candidates and can return:
  - `workspace` mode when MSBuild workspace binding succeeds,
  - `ad_hoc` mode fallback when it fails.
- `require_workspace` can fail closed.

References:
- `src/RoslynSkills.Core/Commands/WorkspaceSemanticLoader.cs:40`
- `src/RoslynSkills.Core/Commands/WorkspaceSemanticLoader.cs:190`
- `src/RoslynSkills.Core/Commands/WorkspaceSemanticLoader.cs:360`
- `src/RoslynSkills.Core/Commands/WorkspaceSemanticLoader.cs:109`
- `src/RoslynSkills.Core/Commands/WorkspaceSemanticLoader.cs:169`
- `src/RoslynSkills.Core/Commands/WorkspaceGuard.cs:7`
- `src/RoslynSkills.Core/Commands/WorkspaceGuard.cs:31`

### Session cache behavior

- `session.*` stores in-memory + persisted session state under temp JSON.
- This cache is file-only and explicitly not project-backed workspace semantics.

References:
- `src/RoslynSkills.Core/Commands/RoslynSessionStore.cs:13`
- `src/RoslynSkills.Core/Commands/RoslynSessionStore.cs:14`
- `src/RoslynSkills.Core/Commands/RoslynSessionStore.cs:46`
- `src/RoslynSkills.Core/Commands/RoslynSessionStore.cs:123`
- `src/RoslynSkills.Core/Commands/SessionOpenCommand.cs:16`
- `src/RoslynSkills.Core/Commands/SessionOpenCommand.cs:102`

### Xml parsing behavior

- `xmlcli` reparses from disk on each command call.
- Backend `xdocument` is strict and write-capable.
- Backend `language_xml` is tolerant read/simulate only.

References:
- `src/XmlSkills.Core/Commands/XmlParsingSupport.cs:156`
- `src/XmlSkills.Core/Commands/XmlParsingSupport.cs:231`
- `src/XmlSkills.Core/Commands/XmlParsingSupport.cs:312`
- `src/XmlSkills.Core/Commands/ValidateDocumentCommand.cs:37`
- `src/XmlSkills.Core/Commands/FileOutlineCommand.cs:40`
- `src/XmlSkills.Core/Commands/FindElementsCommand.cs:42`
- `src/XmlSkills.Core/Commands/ParseCompareCommand.cs:43`
- `src/XmlSkills.Core/Commands/ReplaceElementTextCommand.cs:90`

## Constraints

1. Correctness over speed for semantic commands.
2. Cross-process CLI calls (today) mean in-memory cache is lost between invocations.
3. File changes may happen externally at any time.
4. Project-wide semantic validity may be affected by files other than the target file.

## Invalidation Strategies

All plans should explicitly choose one of these:

1. `strict`: content-hash based (strongest correctness, highest CPU).
2. `balanced`: `(file size + last write utc + cheap hash prefix)` with hash-on-suspicion.
3. `fast`: metadata only (best speed, stale-risk on edge cases).

Recommended default:
- `balanced` for local interactive use.
- `strict` for benchmark/promotion runs.

## Option Set

## Option A: Disk Parse Artifact Cache (No Daemon)

Idea:
- Store per-file parsed artifacts on disk keyed by file fingerprint + parser/backend version.
- Next CLI call loads cached artifact if fingerprint still valid.

Good fit:
- `xmlcli` first.

Possible for `roscli`:
- Syntax-level/line-index artifacts only.
- Full semantic model reuse across processes is limited and complex.

Pros:
- Works with current one-shot CLI process model.
- Simple rollout for xml commands.
- Easy to gate with strict/balanced invalidation modes.

Cons:
- No shared in-memory semantic graph across commands.
- Roslyn semantic commands still pay heavy workspace/compilation load.
- Cache format versioning/invalidation needs discipline.

## Option B: Memory-Mapped Parse Cache File

Idea:
- Write parsed artifact into a binary layout that can be memory-mapped by future processes.
- Next command maps file, reads node/index tables directly.

Important feasibility split:
- `xmlcli`: feasible and attractive.
- `roscli`: full Roslyn object graph memory-mapping is not practical; use custom IR only.

Suggested XML binary layout:
- Header: magic/version/backend/file fingerprint/hash.
- Node table: fixed-size records (name-id, parent-index, offsets, line/column, flags).
- String table: deduplicated names/values.
- Optional attribute table.

Pros:
- Very fast cross-process reload.
- Low GC pressure compared with JSON artifact rehydrate.
- Can reduce repeated parse cost for repeated read-heavy xml flows.

Cons:
- Custom binary format complexity.
- Harder debugging vs JSON.
- Needs robust compatibility checks and corruption handling.

Practical recommendation:
- Do this after Option A proves useful, not before.

## Option C: Persistent Local Daemon (Transport-First)

Idea:
- Keep a long-lived process with in-memory workspace and parse caches.
- CLI becomes thin RPC client to daemon when available.

Good fit:
- `roscli` primary performance path.
- `xmlcli` can also benefit for repeated parse-heavy sequences.

Pros:
- Biggest win for Roslyn semantics (workspace/compilation reuse).
- Enables incremental invalidation via watchers and command-level cache reuse.
- Naturally supports multi-command trajectories with low overhead.

Cons:
- Process lifecycle and health management complexity.
- Need robust fallback to direct CLI path.
- Adds operational surface (port/pipe/auth/isolated session concerns).

## Option D: Hybrid (Daemon + Disk Cache + Fallback)

Idea:
- Preferred path: daemon with in-memory caches.
- Fallback path: one-shot CLI with disk artifact cache.

Pros:
- Best practical balance of speed and reliability.
- Graceful degradation when daemon unavailable.
- Lets us stage improvements without breaking existing flows.

Cons:
- Two cache systems to maintain.
- Requires careful cache contract versioning.

## Option E: Command Result Cache (Selective)

Idea:
- Cache command outputs keyed by command-id + normalized input + fingerprint.

Good candidates:
- `xml.file_outline`, `xml.find_elements`, `xml.validate_document`.
- Some Roslyn read commands when workspace fingerprinting is strong.

Pros:
- Quick win for repeated identical calls.
- Works with existing command pipeline.

Cons:
- Risk of stale/incorrect results if fingerprints are incomplete.
- Lower value if command mix is mostly unique.

## Lateral Ideas

1. Named shared memory segment instead of mmapped file for hot active cache.
2. Two-tier cache:
- warm index in SQLite,
- bulk payload in mmap blobs.
3. Pre-warm command:
- `roscli cache.warm <workspace-path>`
- `xmlcli cache.warm <file-or-root>`
4. Deterministic cache replay mode for benchmark reproducibility.

## Recommended Implementation Plan

## Phase 0: Instrumentation First

- Add per-command timing breakdown fields:
  - process start overhead,
  - parse/load time,
  - semantic analysis time,
  - command execution time.
- Add cache-hit/miss counters to command envelopes.

Acceptance:
- We can attribute speed gains to specific layers, not intuition.

## Phase 1: Xml Disk Artifact Cache (Balanced invalidation)

- Add `XmlParseCache` abstraction.
- Cache parsed XML IR (JSON first).
- Gate by file fingerprint + backend + parser version.
- Add `--cache-mode off|balanced|strict` and env defaults.

Acceptance:
- Repeated xml read commands show measurable parse-time reduction.
- No correctness regressions in fixture suite.

## Phase 2: Roscli Daemon-Preferred Path

- Promote persistent transport server as preferred for high-volume sessions.
- Add CLI auto-connect behavior:
  - try local daemon,
  - fallback to current direct path.
- Add workspace/document invalidation inside daemon.

Acceptance:
- Reduced first-edit latency and round-trips in paired runs.
- Workspace correctness unchanged or improved.

## Phase 3: Xml Mmap Cache Prototype

- Replace JSON artifact with binary mmap format for xml parsed IR.
- Keep JSON fallback for debugging.

Acceptance:
- Additional measurable gain over Phase 1 on repeated read-heavy scenarios.
- Corruption/recovery path tested.

## Phase 4: Selective Result Cache

- Add cache for deterministic read commands with strong fingerprints.
- Include explicit `cache_context` in responses.

Acceptance:
- Lower repeated-call latency with explicit stale-protection guarantees.

## Pros/Cons Summary

| Plan | Speed Potential | Correctness Risk | Complexity | Best For |
|---|---:|---:|---:|---|
| A Disk parse artifact | Medium | Low-Medium | Medium | xmlcli now |
| B Mmap parse artifact | Medium-High | Medium | High | xmlcli later |
| C Persistent daemon | High | Low-Medium | High | roscli primary |
| D Hybrid | High | Medium | High | production posture |
| E Result cache | Low-Medium | Medium-High | Medium | repeated deterministic reads |

## Decision Recommendation

1. Implement Phase 0 and Phase 1 first.
2. In parallel, prioritize Phase 2 for Roslyn trajectories.
3. Treat memory-mapped cache as a targeted xml optimization after baseline wins are measured.

## Open Questions

1. Should daemon become default in `roscli-stable` wrappers?
2. Should cache mode be surfaced in benchmark manifests as a first-class condition?
3. Do we want strict-mode cache validation mandatory for release-candidate benchmark bundles?
4. TODO: how should XAML support split between `xmlcli` structure cache and Roslyn semantic checks (especially `.xaml.cs` binding/navigation scenarios)?
