# roscli Design and Implementation Review — 2026-08-22

Scope: full-repo review with focus on `roscli` (src/RoslynSkills.Cli + RoslynSkills.Core +
RoslynSkills.WorkspaceHost + RoslynSkills.Contracts). Findings combine a structural architecture
map, an implementation-quality pass over the daemon/mutation/workspace paths, and a test/docs
state audit. All findings verified against the working tree at commit `a7a1b85`.

## 1. State at review start

- Build: green, but 144 warnings, all NU1903 (`System.Security.Cryptography.Xml` 9.0.0,
  8 high-severity advisories), transitive from `Microsoft.Build.Tasks.Core` 17.14.28.
- Tests: 4 failing (3 distinct root causes) plus environment-sensitive flakiness.
- Installed global tool `roscli 0.1.6-preview.20`; release artifacts reached
  `0.1.6-preview.99`. Roughly 80 commits of command-surface improvements are NOT in the
  installed tool.

## 2. Fixed during this review (all verified)

| Change | Files | Proof |
|---|---|---|
| Pin patched crypto package over vulnerable transitive 9.0.0 | `src/RoslynSkills.Core/RoslynSkills.Core.csproj` | rebuild: 0 warnings, 0 errors |
| Auto-restore reference-less workspaces; surface diagnostic | `src/RoslynSkills.Core/Commands/WorkspaceAutoRestore.cs` (new), `WorkspaceSemanticLoader.cs`, `StaticAnalysisWorkspace.cs`, `tests/RoslynSkills.Core.Tests/VbCommandTests.cs`, `tests/RoslynSkills.Core.Tests/WorkspaceHostProcessTests.cs` | full suite 271/271; CLI smoke shows `auto_restore` diagnostic + correct matches on unrestored workspace |
| Remove dead pre-rename `RoslynAgent.Cli` fallback branches | `scripts/roscli.cmd`, `scripts/roscli` | both launchers still resolve and run |
| Preload read budget in host process tests 10s -> 60s (parameterized) | `tests/RoslynSkills.Core.Tests/WorkspaceHostProcessTests.cs` | Core suite green twice consecutively |

### Root cause of the four test failures (single cause)

Fresh-checkout projects/solutions loaded via in-process MSBuildWorkspace without NuGet assets
resolve **zero metadata references**. The compilation parses, so workspace load reports success
with *empty* `workspace_diagnostics`, but every semantic query degrades to error types:

- `nav.find_invocations` / `nav.call_hierarchy`: call sites bind as `IInvalidOperation` ->
  silently `total_matches: 0`.
- `edit.replace_text --workspace-path`: cross-file symbols unresolvable -> CS0103 noise.
- `LoadForFileAsync_WithExplicitCsprojWorkspace_HasCoreLibraryTypes`: no `System.String`.

This is a **product defect, not just a test bug**: agents pointing roscli at a fresh clone got
silently wrong semantic results with no signal. Fix: after open, if any project has documents
but zero metadata references, run bounded `dotnet restore` once and reload; emit
`auto_restore:` diagnostics either way. Opt out with `ROSLYNSKILLS_AUTO_RESTORE=false`.

Implementation notes worth keeping:

- Restore child runs with `MSBUILDDISABLENODEREUSE=1`. Without it, MSBuild node-reuse workers
  inherit the redirected stdout/stderr handles and outlive `dotnet restore`, so post-exit pipe
  drain never sees EOF -> indefinite hang (observed as the xunit-only slnx test hang). Drain is
  additionally bounded by a 10s grace window; restore itself by 120s.
- Both loaders (`WorkspaceSemanticLoader.CreateLoadedWorkspaceStateAsync`,
  `StaticAnalysisWorkspace.LoadMsBuildWorkspaceAsync`) share
  `WorkspaceSemanticLoader.CreateTrackedMsBuildWorkspace` for workspace+diagnostics wiring.

## 3. Architecture assessment

Layering: Contracts <- Core (registry + 58 commands) <- {Cli, McpServer, WorkspaceHost,
TransportServer}; XmlSkills lane is a deliberate parallel monorepo lane (in slnx, tested).

Strengths:

- Command definitions live in Core behind `IAgentCommand`/`CommandDescriptor`; CLI/MCP/daemon
  all dispatch to the same registry. Protocol major-version gating client-side is sound;
  fixed-time auth token comparison done correctly.
- Span/exact edit guards (`span_out_of_range`, `old_text_ambiguous`, recovery hints,
  `expected_text`) are well built and well tested.
- Disposal discipline around MSBuild workspaces/watchers (reload disposes old instance,
  watcher disposed on store removal) is correct.

Top design risks (ordered):

1. **HIGH — `CliApplication.cs` is a 4,925-line single sealed class** mixing dispatch, 20+
   builtin verb handlers, daemon routing, the entire shorthand parser, per-command input
   shaping (`TryBuildDirectShorthandInput`, ~655 lines), usage hints (~570 lines), and ~900
   lines of embedded presentation text. Every command addition touches this class; it is the
   merge-conflict hotspot of the repo. Recommendation: split into partials by concern
   (Dispatch / ShorthandParser / InputShaping / Presentation), then move presentation text to
   data files. No behavior change required to start.
2. **HIGH — heuristic argument parsing**: comma-split whitelist (`IsCommaListOption`),
   `IsCompactCommaList` guess, schema-blind scalar coercion (`ParseScalarOptionValue`). Known
   agent failures were patched with hints instead of fixing coercion at the descriptor level.
   Recommendation: derive list-vs-scalar from each descriptor's input schema; treat unknown
   options as errors rather than guesses.
3. **MED — command surface defined in three places** (`DefaultRegistryFactory`, `RunAsync`
   switch, `CliOnlyCommandDescriptors()`); drift yields discoverability gaps (observed:
   `--version` unknown_verb).
4. **MED — frontend pattern forked 3x**: XmlSkills.Cli duplicates the CliApplication shape and
   `CommandContracts.cs` verbatim (already diverged); McpServer re-implements envelope
   construction with a second version constant. Envelope/parser fixes must be applied up to
   three times.
5. **LOW/MED — TransportServer has no test project** (third untested transport lane).

## 4. Implementation assessment (daemon + mutation)

1. **HIGH — no stale-daemon cleanup or kill path.** Manifest written before readiness;
   failed starts orphan a live process plus manifest; `StopAsync` deletes the manifest even
   when shutdown goes unanswered (pid lost while listener lives); next start binds a second
   daemon whose named-pipe server waits forever. No `Process.Kill` anywhere in the CLI.
   Recommendation: PID liveness check + kill-on-stale + manifest cleanup on failed start.
2. **HIGH — `edit.batch_exact` writes are not crash/concurrent-safe**: direct whole-file
   overwrite, no temp-file+rename, no hash precondition; TOCTOU between per-op reads and the
   final write can clobber a concurrent writer. `expected_text` exists only for
   `replace_span`. Recommendation: temp-file+atomic rename + optional content-hash guard for
   every buffered file write.
3. **MED — claim store races**: `.roslynskills/edit-claims.json` read-modify-write with no
   cross-process locking; torn JSON silently treated as empty store (wipes all claims).
   Claims remain advisory (no mutation refuses on foreign claim) — acceptable if documented,
   but silent wipe is not.
4. **MED — daemon serializes everything**: single instance pipe, one connection at a time,
   `CancellationToken.None` on every tool call; one hung request blocks all clients; client
   error mapping collapses distinct failures into `DaemonUnavailable`.
5. **MED — hot store has no eviction/TTL**; recursive FileSystemWatcher per preload;
   O(files) stat sweep per status/refresh; classifier rebuilds sets per event.
6. **LOW** — auth token visible in child process argv on Linux; response-write IOException
   can escape the accept loop; static mutable transport state in Program.cs.

## 5. Test state

- Inventory: Cli.Tests 99, Core.Tests 108, Benchmark.Tests 50, XmlSkills.Cli.Tests 14 = 271.
  Now all green locally (Debug, net10.0, twice for Core).
- Coverage gaps (~12 of 64 commands zero behavioral coverage): `nav.call_path`;
  `ctx.call_chain_slice`, `ctx.dependency_slice`; `diag.get_workspace_snapshot`,
  `diag.get_after_edit`, `diag.diff`; `edit.change_signature`, `edit.add_member`,
  `edit.update_usings` (+ `apply_code_fix` only indirect); `session.get_diagnostics`;
  `workspace.close`, `workspace.list`.
- **CI gap (highest-leverage fix): there is NO push/PR workflow.** Tests run only when a
  human publishes or a tag builds; `publish-nuget-preview.yml` has a `-SkipTests` escape
  hatch before publishing. This is exactly how the four broken tests survived unnoticed.
  Recommendation: add a minimal `dotnet build + dotnet test` workflow on push/PR before any
  new feature work.
- Flakiness: hot-daemon process tests are timing-sensitive under parallel load (preload
  budget now 60s); benchmark script-substring tests hard-code `.ps1` contents (drift-prone);
  `ExternalVbRepoSmokeTests` env-gated, effectively CI-dead.

## 6. Doc state

- Consistent: README/HANDOVER/architecture command counts (~64 = 58 Core + 6 CLI-only),
  install instructions, dev-wrapper flags.
- Minor drift: `docs/architecture.md` family table formatting garbled (numbers correct);
  Commands/ has 79 files vs "64 commands" because support classes live beside commands
  (footnote would help).
- HANDOVER's "Key unresolved" list still accurate (latency overhead, LSP comparator
  confounds, daemon concurrency).

## 7. Recommended next steps (dependency ordered)

1. Add push/PR CI running build+test (blocks nothing, prevents silent rot).
2. Daemon lifecycle hardening: stale detection, kill path, manifest cleanup (unblocks
   reliable multi-agent supervised runs).
3. Atomic file writes + claim-store locking in mutation path (multi-agent safety).
4. Split `CliApplication.cs` into partials; then schema-driven argument coercion.
5. Refresh the published/global tool lane (`preview.20` vs source is ~80 commits stale) and
   re-run a supervised acquisition trial against the refreshed tool.
