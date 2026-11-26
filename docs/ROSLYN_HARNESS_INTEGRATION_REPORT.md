# Roslyn Harness Integration Study: CLI + NativeAOT Daemon vs. LSP vs. MCP vs. Built-In Tools

**Date:** 2026-11-26  
**Version Studied:** RoslynSkills `0.1.6-preview.31` (commit `72a4b26`, Sections 1–9 baseline) through `0.1.6-preview.33` (Pass 5, Section 10)  
**Benchmark Subject:** `CalcDb.slnx` (33 projects, 621–632 source documents, .NET 11.0 / mixed `net11.0` & `net11.0-windows`)  
**Status:** Empirical engineering report & harness integration standard (all Roadmap §8 P0/P1/P2 items shipped in Passes 4–5)

---

## 1. Executive Summary

Integrating Roslyn semantic analysis into an agentic coding harness is fundamentally an **architecture and token-economics problem**, not merely a compiler-API problem. Historical A/B runs in this repository ([research-findings.md](research-findings.md), February 2026) showed that naive CLI invocations paying a ~14–17 s MSBuild reload per call or multi-round-trip MCP schemas repeatedly lost on tokens and wall time to text-first baselines on small edits, even when semantic precision was higher.

Over three optimization passes culminating in `0.1.6-preview.31`, RoslynSkills eliminated the per-call MSBuild reload and CLR launch bottlenecks through a persistent `WorkspaceHost` daemon ([src/RoslynSkills.WorkspaceHost](../src/RoslynSkills.WorkspaceHost/Program.cs)), `FileSystemWatcher` barrier freshness ([HotWorkspaceFreshness.cs](../src/RoslynSkills.Core/Commands/HotWorkspaceFreshness.cs)), per-compilation `SymbolNameIndex` ([SymbolNameIndex.cs](../src/RoslynSkills.Core/Commands/SymbolNameIndex.cs)), selective call-site binding ([CallSiteAnalysis.Targeted.cs](../src/RoslynSkills.Core/Commands/CallSiteAnalysis.Targeted.cs)), interface-aware call hierarchy (`854z`, [CallHierarchyCommand.cs](../src/RoslynSkills.Core/Commands/CallHierarchyCommand.cs)), and a 2.15 MB NativeAOT client (`roscli-fast.exe`, [src/RoslynSkills.FastClient/Program.cs](../src/RoslynSkills.FastClient/Program.cs)).

To determine the optimal harness integration pattern today, we ran controlled, single-host empirical benchmarks on the real 33-project `CalcDb.slnx` codebase comparing four integration architectures side by side:

1. **CLI + NativeAOT + Persistent Daemon (`roscli-fast.exe` + `WorkspaceHost`)** vs. standard `.NET` CLI (`roscli`).
2. **Language Server Protocol (LSP)** via both **`Microsoft.CodeAnalysis.LanguageServer` `5.7.0`** (from the VS Code C# extension) and **`csharp-ls` `0.22.0`** (plus the repo's Node MCP bridge [tools/csharp-lsp-mcp/server.js](../tools/csharp-lsp-mcp/server.js)).
3. **Model Context Protocol (`RoslynSkills.McpServer`)** routed via `HotCommandRouter` vs. in-process fallback (`ROSCLI_DAEMON=off`).
4. **Built-in Non-Roslyn Harness Tools (`rg`, targeted `read_file`, recorded `dotnet build` timings)**.

### Headline Findings

- **NativeAOT CLI + `WorkspaceHost` (`roscli-fast.exe`) matches `rg` speed while delivering compiler truth:** After a one-time `31.5 s` `workspace.use CalcDb.slnx` bind (`770.6 MB` warmed host RAM), `roscli-fast.exe` answers solution-wide `--symbol` queries (`nav.find_references`, `nav.find_invocations`, `nav.find_implementations`, `nav.call_hierarchy --max-depth 1`) in **`56–74 ms` median wall time** (`3–10 ms` inside the host). That is **~6× faster** than spawning the managed `roscli` `.NET` CLI (`365–465 ms`), **~1,000× faster** than cold in-process `roscli --no-daemon` (`70,117 ms`), and **7–9× faster** than Microsoft's own `Microsoft.CodeAnalysis.LanguageServer` on `textDocument/references` (`513.5–517.5 ms` warm median).
- **Both LSP servers exhibit structural agent-safety hazards that fail silently:**
  - **`csharp-ls` `0.22.0`** took **`55.8 s`** to load `CalcDb.slnx` (and `36.4 s` just scanning the tree without `--solution`), picked the `.NET 10` SDK over `global.json` (`11.0.100`), emitted **`108` MSBuild reference warnings**, timed out at **`90.0 s`** on cold `textDocument/definition`, missed cross-project references (`12` vs `15`/`16` on `DeferredCatalogCommit.Kind`), and pushed **`1,159` false diagnostics** on a clean file (`SchemaWriter.cs`). Wrapped in `csharp-lsp-mcp`, it silently returned **`0` references (`isError: false`)** because the bridge queried before solution load finished.
  - **`Microsoft.CodeAnalysis.LanguageServer` `5.7.0`** loaded cleanly in **`10.7 s`** (`826.5 MB` working set), yet before `workspace/projectInitializationComplete` it silently answered `textDocument/references` with an incomplete **`count: 1`**. Even after full load, `callHierarchy/incomingCalls` returned **`0` callers** on `ComSqlDatabase.OpenSchemaWriter` (blind to calls routed through `ISqlSchemaDatabase.OpenSchemaWriter`, whereas `roscli nav.call_hierarchy` returned **`7` edges** with explicit `implemented_member` attribution), `workspace/symbol` returned **`32` noisy substring matches** for `"SchemaWriter.CreateTable"`, `textDocument/diagnostic` took **`14.9 s`** for a single file while `workspace/diagnostic` silently returned **`0` items**, and `--logLevel Information` dropped a **`1.2 MB` binlog folder directly inside the CalcDb repo root**.
- **`RoslynSkills.McpServer` achieves the lowest warm call latency (`15.3–29.6 ms`) via `HotCommandRouter`, but pays a `13.5K–17.4K` token/turn discovery tax:** Exposing all **59** registry commands upfront inflates `tools/list` to **`53,851 bytes` (~`13,463` tokens)** on every turn (plus `15,823 bytes` / `~3,956` tokens in `resources/list`), yet **~38 of the 59 tools still omit parameter schemas** (`additionalProperties: true` only). Without a bound daemon (`ROSCLI_DAEMON=off`), the first call stalls for **`48.3 s`**.
- **Built-in `rg` is unmatched for raw literal/doc lookup (`66–116 ms`), but explodes on overloaded/common C# identifiers:** `rg -n -w "Execute"` dumps **`278` lines (`47.8 KB`, ~`12.0K` tokens)** across `src` (`32.4%` comments/docs across 7+ types) and **`1,166` lines (`189.2 KB`, ~`47.3K` tokens)** across `src + tests`; `rg -n -w "Kind"` emits **`669` lines (`91.8 KB`, ~`23.0K` tokens)** vs **`15` exact semantic matches (`12.1 KB`)** from `roscli-fast nav.find_references --symbol DeferredCatalogCommit.Kind`.

**Bottom-line Recommendation:** Today, the optimal harness architecture for high-velocity C# agentic coding is **CLI + NativeAOT (`roscli-fast.exe`) + `WorkspaceHost` daemon + compact skill/rule guidance**, following a **"text first, semantic second, scoped diag third"** hybrid routing rule. MCP should only become the primary transport after refactoring `RoslynSkills.McpServer` into a **slim 6–10 tool facade** with complete schemas and a direct named-pipe client to `WorkspaceHost`.

---

## 2. Current State of RoslynSkills (`0.1.6-preview.31`)

RoslynSkills targets `net10.0` with Roslyn `5.12.0`, MSBuild `18.12.2`, and `Microsoft.Build.Locator` `1.11.2`, allowing it to load and analyze `.NET 10` and `.NET 11` SDK workspaces ([docs/architecture.md](architecture.md)). As verified via `roscli list-commands --ids-only`, the CLI exposes **68 discoverable command IDs** (`54` `stable`, `11` `advanced`, `3` `experimental`), comprising **59 core registry commands** in [DefaultRegistryFactory.cs](../src/RoslynSkills.Core/DefaultRegistryFactory.cs) plus **9 CLI-only lifecycle/onboarding verbs** (`agent-begin`, `csharp-start`, `text.measure`, `daemon.start/run/stop/restart/list/prune`).

### Key Capabilities Landed Through Pass 3 (`0.1.6-preview.31`)

| Capability | Implementation | Impact on Agent Velocity |
|---|---|---|
| **Persistent `WorkspaceHost` Daemon** | Named pipes (Windows) / Unix sockets (protocol `2.0`), manifest + alias state stored under `%LOCALAPPDATA%\RoslynSkills\workspace-hosts` (never inside repo). | Eliminates repeated `~30–70 s` MSBuild solution loads; zero repo-tree pollution. |
| **Zero-Stat Watcher Freshness** | `WorkspaceChangeTracker` proves `FileSystemWatcher` completeness via uncertainty generation + `obj/bin` marker barrier (`250 ms` timeout, `15 s` safety sweep). | Replaces full-tree disk `stat()` sweeps with `~1.5–2.3 ms` pre-query freshness verification. |
| **NativeAOT Thin Client (`roscli-fast.exe`)** | `2.15 MB` AOT binary ([RoslynSkills.FastClient](../src/RoslynSkills.FastClient/Program.cs)) forwards eligible hot commands to `CliBridge` (`cli/run`) inside the host. | Cuts ~330–400 ms `.NET` CLR startup overhead down to `~50–75 ms` total wall time. |
| **`HotTextIndex` & `IdentifierIndex`** | In-memory line cache of non-generated `.cs`/`.csx` files + per-`SyntaxTree` identifier sets updated via watcher events. | Serves `ctx.search_text` from RAM (`hot_text_index`) and pre-filters semantic scans without disk I/O. |
| **Topological Warmup & `SymbolNameIndex`** | `HotWorkspaceWarmup` compiles projects in topological waves and builds per-`Compilation` `SymbolNameIndex` dictionaries (`ConditionalWeakTable`), including synthesized positional-record properties. | Replaces `SymbolFinder.FindDeclarationsAsync` scans; enables `--symbol Type.Member` across all 6 `nav.*` commands. |
| **Selective Call-Site Binding (`854z`)** | `CallSiteLookup` + `CallSiteAnalysis.Targeted` bind only syntax nodes matching target callee/ctor/alias/implicit names; `nav.call_hierarchy` traverses implemented interface members (`edges[].implemented_member`). | Fixes missing interface-dispatched callers (`OpenSchemaWriter`) and avoids whole-tree `IOperation` binding on cold trees. |
| **Scoped Multi-Project Diagnostics** | `diag.get_workspace_snapshot --directory-path <dir>` compiles all projects rooted under `<dir>` and returns per-project totals. | Provides compiler-grade multi-project validation in `~100–240 ms` without launching a `142–233 s` `dotnet build`/test run. |
| **MCP Daemon Routing** | `HotCommandRouter` routes `RunsOnHotWorkspace` MCP tool calls through `CliApplication` to the bound `WorkspaceHost` daemon (`Transport.mode = "mcp"`). | Lowers steady-state MCP call latency to `15–30 ms` when a daemon is bound. |

---

## 3. Empirical Benchmark Environment & Methodology

All measurements were executed sequentially on a single Windows 11 workstation (31.4 GiB total RAM; ~7.1 GiB free physical RAM alongside two pre-existing worker daemons on separate worktrees) against **`C:\Work\DnaCalc\CalcDb\CalcDb.slnx`** without modifying any tracked file in `CalcDb`.

- **Subject Workspace (`CalcDb.slnx`):** 33 projects (`21` `src/`, `2` `tools/`, `10` `tests/`), 621 Roslyn documents (`546` tracked non-generated paths), `.NET 11.0.100` (`global.json`), mixing `net11.0` and `net11.0-windows` target frameworks, fully restored (`33/33` `project.assets.json` files present; `ROSLYNSKILLS_AUTO_RESTORE=false` during benchmarks).
- **Sequential Isolation:** Only one measured host/server (`WorkspaceHost`, `csharp-ls`, `Microsoft.CodeAnalysis.LanguageServer`, or `McpServer`) ran at a time to prevent memory swapping or CPU contention. All processes and daemons started by this study were stopped and verified absent via `roscli daemon.list` and `Get-Process` before completion.
- **Edit Safety:** All `edit.*` commands (`edit.rename_symbol`, `edit.transaction`, `edit.change_signature`, `edit.apply_code_fix`) ran with `--apply false` (`dry-run`), executing full Roslyn solution transformations and post-edit diagnostics in memory with `wrote_file: false`. LSP `textDocument/rename` likewise computes a `WorkspaceEdit` JSON payload without writing files.
- **Timing & Token Accounting:** Cold latency denotes the first request after startup/bind; steady-state warm latency is reported as the **median** of 5–9 subsequent iterations (3 iterations for slow `csharp-ls` calls). Payload sizes are exact UTF-8 byte counts converted to approximate tokens via `ceiling(bytes / 4)`.
- **Build Baselines:** Because a build lock existed on the host, `dotnet build` / landing-check durations are taken directly from recorded runs in `CalcDb/docs/work/process-log.md` rather than executing redundant builds.

---

## 4. Measured Benchmark Results on `CalcDb.slnx`

### 4.1 Bootstrap, Memory Footprint, and Cold vs. Warm Baseline

| Approach / Mode | Version | Initial Bind / Load (ms) | First Query Cold Latency | Warm Query Steady State | Resident RAM (Working Set / Private) | Repo Pollution Risk |
|---|---|---:|---:|---:|---:|---|
| **`roscli --no-daemon`** (in-process) | `0.1.6-preview.31` | N/A (per-call) | `70,117 ms` (`nav.find_symbol`) | `~70,000 ms` (reloads each call) | Transient per call | None |
| **`roscli` + `WorkspaceHost`** (.NET CLI) | `0.1.6-preview.31` | `31,545 ms` (`workspace.use`) | `~250–25,400 ms` (warmup overlap) | `364.9–464.8 ms` | `391.2 MB` -> `770.6 MB` (after warmup) | None (state in `%LOCALAPPDATA%`) |
| **`roscli-fast.exe` + `WorkspaceHost`** (NativeAOT) | `0.1.6-preview.31` | `31,545 ms` (`workspace.use`) | `130–25,432 ms` (warmup overlap) | **`51.5–143.7 ms`** (most `55–74 ms`) | Shared `770.6 MB` host; tiny client | None (state in `%LOCALAPPDATA%`) |
| **`RoslynSkills.McpServer`** (daemon-routed) | `0.1.6-preview.31` | `1,401 ms` (`init`, daemon warm) | `31.8–7,951 ms` | **`15.3–52.7 ms`** | `55.2 MB` / `19.9 MB` (+ `770.6 MB` daemon) | None |
| **`RoslynSkills.McpServer`** (`ROSCLI_DAEMON=off`) | `0.1.6-preview.31` | `~1,400 ms` (`init`) | `48,264 ms` (`find_symbol`) | `14.7 ms` (in-proc cache, 1 proj) | `175.2 MB` / `67.6 MB` (single proj) | None |
| **`Microsoft.CodeAnalysis.LanguageServer`** | `5.7.0-1.26220.12` | `1,364 ms` init; `10,664 ms` projects | `312–8,640 ms` (incomplete `1` before load!) | `2.5–517.5 ms` | **`826.5 MB` / `695.3 MB`** | Drops `1.2 MB` `.binlog` dir in cwd under `Information` log level |
| **`csharp-ls`** (LSP) | `0.22.0.0` | `3,137 ms` init; `55,834 ms` load | **`>90,000 ms` timeout** (`definition`); `12,198 ms` (`refs`) | `3.6–1,118 ms` (incomplete refs, `1,159` false diags) | `486.7 MB` / `308.2 MB` | None observed |
| **`rg` + `read_file`** (built-in text) | ripgrep | `0 ms` | `~66–116 ms` | `66.0–115.9 ms` | Ephemeral (~10 MB) | None |
| **`dotnet build` / landing check** (recorded) | .NET 11 | N/A | `~47,000 ms` incremental build (`ptrj-fix`); `142–233 s` check median | N/A | High (multi-node MSBuild + tests) | Writes `bin/`/`obj/` |

### 4.2 Query Latency Comparison Across All Four Approaches (ms)

All `roscli-fast` and `McpServer` semantic queries executed against the warm `WorkspaceHost` daemon; LSP queries ran after `projectInitializationComplete` (`Roslyn LSP`) or `Finished loading solution` (`csharp-ls`).

| Query / Operation | `roscli-fast` Cold / Warm Med (Host Exec) | `roscli` (.NET CLI) Warm Med | `McpServer` (Daemon) Cold / Warm Med | `Roslyn LSP 5.7` Cold / Warm Med | `csharp-ls 0.22` Cold / Warm Med | `rg -n -w` Med (`src`) |
|---|---|---:|---:|---:|---:|---:|
| **`nav.find_symbol` (`TextCodec.TryEncode`)** | `368.1` / **`143.7`** (benchmark script: `59.0`, `3.1 ms` host) | `451.2` | `496.7` / **`15.3`** | `361.4` / **`2.6`** (`definition`) | **Timeout `90,000`** (`definition`) | `73.4` (`35.0` single file) |
| **`nav.go_to_definition` (`--symbol SchemaWriter.CreateTable`)** | `242.1` / **`107.3`** (`0.7 ms` host) | — | — | `312.3` / **`2.5`** (needs file+pos) | — | `80.8` (`CreateTable`) |
| **`nav.find_references` (`--symbol DeferredCatalogCommit.Kind`)** | `2,534.1` / **`66.1`** (`9.7 ms` host) | `390.2` | `31.8` / **`27.5`** | `1,040.4` / **`513.5`** | `851.7` / `72.6` (`12` vs `15`!) | `78.8` (`Kind`: `669` noise lines) |
| **`nav.find_references` (`--symbol ISqlDatabase`)** | `214.8` / **`141.2`** (`5.3 ms` host; positional: `56.0`) | — | — | `515.2` / **`517.5`** | `245.7` / `16.1` | `67.6` (`src`: `32` lines) |
| **`nav.find_invocations` (`--symbol SchemaWriter.CreateTable`)** | `2,807.1` / **`61.6`** (`3.1 ms` host) | `364.9` | `72.5` / **`29.0`** | `1,058.7` / **`516.4`** (`refs` only) | — | `80.8` (`src`: `15`, `src+tests`: `66`) |
| **`nav.find_implementations` (`--symbol ISqlDatabase`)** | `5,617.0` / **`218.0`** (`4.1 ms` host; positional: `63.0`) | — | — | `53.6` / **`24.1`** (`13` vs `15`!) | `44.1` / `8.0` (`13` vs `15`!) | Cannot distinguish |
| **`nav.call_hierarchy` (`ComSqlDatabase.OpenSchemaWriter`, `854z`)** | `11,151.1` / **`74.4`** (`10.4 ms` host, `7` edges) | — | `7,951.4` / **`29.6`** | `85.4` / `4.4` (**`0` callers!**) | `21.9` (`prepare` only) | `66.0` (text only) |
| **`nav.call_hierarchy` (`SchemaWriter.CreateTable`, depth=2 / depth=1)** | `25,432` / **`854.8`** (depth=1: `2,243` / **`67.7`**) | — | — | `42.3` / `28.6` (depth=1 only, `24` callers) | — | N/A |
| **`ctx.search_text` (`TextCodec`, `src`)** | `805.3` / **`244.9`** (`13.1 ms` host; script: `62.0`) | `444.2` | `211.1` / **`52.7`** | Unsupported | Unsupported | **`68.2`** |
| **`ctx.file_outline` (`SchemaWriter.cs`, filtered)** | `2,096.6` / **`1,219.4`** (fallback!) | — | — | Unsupported (`documentSymbol` unfiltered) | — | `~70` (`rg` + `read_file`) |
| **`ctx.member_source` (`TextCodec.TryEncode`)** | `321.3` / **`55.0`** (`0.7 ms` host) | — | — | Unsupported | Unsupported | `~0–5` (`read_file` slice) |
| **`diag.get_file_diagnostics` (`SqlBinder.cs`)** | `902.7` / **`73.8`** (`32.2 ms` host) | — | — | **`14,857.1`** (`textDocument/diagnostic`) | Push only (`0` on `SqlBinder`) | Requires `dotnet build` |
| **`diag.get_workspace_snapshot` (`src/CalcDb.Sql`)** | `130.1` / **`103.7`** (`40.3 ms` host; `--brief`: `109.3`) | `464.8` | `261.1` / **`136.3`** | **`18.7`** (`workspace/diag` -> **`0` items!**) | Push only (`1,159` false!) | Requires `dotnet build` |
| **`edit.rename_symbol` (`TryEncode`, `--apply false`)** | `2,994.2` / **`646.3`** (`3` files + diags) | — | `5,060.5` / **`1,184.7`** | `1,550.2` / **`516.3`** (`WorkspaceEdit`, no diags) | `2,143.4` / `1,117.5` (`4` entries) | Unsafe across overloads |
| **`edit.transaction` / `edit.change_signature` (`--apply false`)** | `447.0` / **`169.5`** (`tx`); `1,185` / **`124.2`** (`sig`) | — | — | No atomic transaction / sig change | No atomic transaction / sig change | Manual multi-file edit |
| **`nav.find_symbol_batch` / `query.batch` (3 queries)** | `95.2` / **`51.5`** (`symbol_batch`); `3,319` / **`68.7`** (`query.batch`) | — | — | No batching (3 round-trips) | No batching | Multiple `rg` calls |

### 4.3 Semantic Accuracy, Completeness, and Failure Modes

| Test Case | `roscli-fast` (`0.1.6-preview.31`) | `Microsoft.CodeAnalysis.LanguageServer` (`5.7.0`) | `csharp-ls` (`0.22.0`) | `rg -n -w` (Plain Text) |
|---|---|---|---|---|
| **`DeferredCatalogCommit.Kind`** (synthesized positional record property) | **15 exact refs** via `--symbol` (`12,097 B`) | **16 refs** after load (`2,670 B`); **`1` ref** if queried before load! | **12 refs** (missed cross-project refs due to broken `.csproj` refs) | **`669` lines** for `Kind` (`91.8 KB`), `33` lines for `DeferredCatalogCommit` |
| **`ComSqlDatabase.OpenSchemaWriter`** (called via `ISqlSchemaDatabase`) | **7 edges / 5 nodes**, 2 edges explicitly tagged `implemented_member: ISqlSchemaDatabase.OpenSchemaWriter` | **`0` incoming callers!** Blind to interface dispatch | `prepareCallHierarchy` returned 1 item; cross-project broken | `17` lines (`OpenSchemaWriter` across all classes/interfaces) |
| **`ISqlDatabase` implementations** | **15 implementations** (`13` concrete types + `2` derived interfaces `ISqlActionDatabase`, `ISqlSchemaDatabase`) | **13 implementations** (concrete types only; omitted derived interfaces) | **13 implementations** (concrete types only) | Cannot separate implementations from parameters/fields (`32` `src` / `55` `src+tests` lines) |
| **`SchemaWriter.CreateTable` refs/callers** | **44 refs** (`include_declaration: true`), **42 invocations** | **44 refs**; **24** direct 1-hop `incomingCalls` | Not run (`definition` timed out) | **`15` `src` / `66` `src+tests`** lines (`DdlKind.CreateTable` noise) |
| **Overloaded `Execute` & `OpenRecordset`** | Disambiguated via `file:line:col` or `--symbol Type.Execute(params)` | Disambiguated via `file:line:col` + `didOpen` | Disambiguated (subject to load errors) | `Execute`: **`278` `src` (`90` comments) / `1,166` `src+tests`**; `OpenRecordset`: **`72` `src` / `427` `src+tests`** |
| **Scoped Diagnostics (`src/CalcDb.Sql`)** | **1 project, 13 files, 0 errors, 0 warnings** (`103.7 ms`) | `textDocument/diagnostic`: `14.9 s`; `workspace/diagnostic`: **`0` items silently** | Pushed **`1,159` false diagnostics** on clean `SchemaWriter.cs` | Zero diagnostics without `dotnet build` (`~47–147 s`) |

### 4.4 Discovery Overhead & Payload Token Tax

| Channel / Artifact | Tool / Item Count | Discovery Payload (Bytes) | Per-Turn Context Token Tax (~bytes/4) | Typical Query Response Size (Bytes / ~Tokens) |
|---|---:|---:|---:|---|
| **`RoslynSkills.McpServer` `tools/list`** | 59 tools | `53,851 B` | **`~13,463 tokens / turn`** | `3,401–16,692 B` (`~851–4,173 tokens`, compact JSON) |
| **`RoslynSkills.McpServer` `resources/list`** | 60 resources | `15,823 B` | `+3,956 tokens` (if ingested) | Same envelope |
| **`tools/csharp-lsp-mcp` `tools/list`** | 5 tools | `2,238 B` | `~560 tokens / turn` | `~185–8,484 B` (raw LSP locations; `0` on premature call) |
| **`roscli llmstxt` (default stable)** | 54 commands | `10,767 B` | **`0 / turn`** (one-time `~2,692` on demand) | `2,965–19,851 B` (`~742–4,963 tokens`, indented JSON) |
| **`roscli csharp-start`** | Core workflow | `7,303 B` | **`0 / turn`** (one-time `~1,826` on demand) | N/A |
| **Skill (`roslynskills-tight` `SKILL.md`)** | Targeted rules | `~6,400 B` | `~1,600` (only when skill loaded) | N/A |
| **`nav.call_hierarchy` (`CreateTable`, depth=2 vs 1)** | 118 vs 42 edges | — | — | `124,425 B` (**`~31,107 tokens`**) vs `49,166 B` (**`~12,292 tokens`**) |
| **`rg -n -w "Execute"` (`src` vs `src+tests`)** | 278 vs 1,166 lines | `0` | `0 / turn` | `47,800 B` (`~11,950 tokens`) / `189,188 B` (**`~47,297 tokens`**) |

---

## 5. Architectural Comparison

### 5.1 CLI + NativeAOT (`roscli-fast.exe`) + `WorkspaceHost` Daemon

`roscli-fast.exe` bridges two worlds: the **zero-schema-tax ergonomics of a CLI + Markdown skill** and the **sub-100 ms warm state of an in-memory Roslyn server**. On Windows, a managed `.NET` CLI spawn (`roscli`) pays **`~330–400 ms`** per call simply to start CoreCLR, JIT `CliApplication`, and serialize pipe JSON, even when the server answers in `3 ms`. Compiling `RoslynSkills.FastClient` as a `2.15 MB` NativeAOT executable that shoots `cli/run` directly over `\\.\pipe\roslynskills-<hash>` eliminates the CLR startup tax, bringing wall latency down to **`51–74 ms`**.

Crucially, `WorkspaceHost` exposes **fail-closed workspace semantics** (`--require-workspace true` and `ROSCLI_DAEMON=required`). When `workspace.use CalcDb.slnx --require-solution true` finishes (`31.5 s`), all 33 projects are guaranteed bound (`unresolved_project_count: 0`). Subsequent edits made either by `roscli edit.*` or external agent tools (`edit_file`) are reconciled on the next call in `~1.5–2.3 ms` via `WorkspaceChangeTracker`'s watcher barrier (`watcher_fast_path`) and `HotTextIndex`, without stat-sweeping 621+ files.

One remaining architectural bottleneck uncovered in this study: **`ctx.file_outline` (`FileOutlineCommand`) does not set `RunsOnHotWorkspace = true`**. As a result, `roscli-fast.exe` falls back (`cli_fallback`) to spawning the full `.NET` CLI and re-parsing `SchemaWriter.cs` (8,147 lines) from disk on every invocation, taking **`1,219.4 ms`** instead of `~50–70 ms`. Similarly, **`QueryBatchCommand`** restricts `SupportedQueryFactories` to 18 commands and rejects `nav.find_implementations`, `nav.find_overrides`, `diag.get_workspace_snapshot`, `ctx.symbol_envelope`, and `ctx.changed_files`.

### 5.2 Language Server Protocol (`csharp-ls` and `Microsoft.CodeAnalysis.LanguageServer`)

LSP is optimized for **human IDE interaction inside one active editor viewport**, not for headless coding agents:

1. **Position-coupled and `didOpen`-dependent:** Nearly every useful LSP query (`definition`, `references`, `implementation`, `prepareCallHierarchy`, `rename`) requires a `file:///...` URI, `textDocument/didOpen` synchronization, and exact **0-based** `(line, character)` coordinates. LSP's `workspace/symbol` uses fuzzy IDE substring matching (`"SchemaWriter.CreateTable"` returned 32 test methods first), whereas `roscli --symbol` resolves exact `Type.Member(paramTypes)` declarations via `SymbolNameIndex`.
2. **Asynchronous loading without fail-closed guards:** Both `csharp-ls` and `Microsoft.CodeAnalysis.LanguageServer` acknowledge `initialize`/`initialized` in `1.4–3.1 s` while loading MSBuild projects asynchronously in the background. Any agent or MCP bridge (`csharp-lsp-mcp`) that queries immediately receives silently truncated results (`count: 0` or `count: 1` with `isError: false`) unless it implements Roslyn-specific `workspace/projectInitializationComplete` waits.
3. **Ecosystem SDK fragility (`csharp-ls`) and reference latency (`Roslyn LSP`):** `csharp-ls 0.22.0` failed to detect `.NET 11.0.100`, forced `TargetFramework=net11.0-windows`, emitted `108` project-reference warnings, timed out at `90 s`, and flooded `SchemaWriter.cs` with `1,159` false diagnostics. Even Microsoft's official `5.7.0` Roslyn LSP server exhibited a `~515 ms` floor on every warm `textDocument/references` and `textDocument/rename` call (7–9× slower than `roscli-fast.exe`), missed `854z` interface callers in `callHierarchy/incomingCalls`, returned `0` items silently on `workspace/diagnostic`, took `14.9 s` for single-file pull diagnostics, and wrote a `1.2 MB` `.binlog` directory into the repository root under `--logLevel Information`.
4. **Historical paired-run confirmation:** In earlier Codex/Spark MCP interop sweeps ([research-findings.md](research-findings.md) F-2026-02-12-51 and F-2026-02-13-54), `csharp-ls` MCP lanes repeatedly timed out at `180–300 s` on project-backed repos. On multi-TFM `.NET 11` `.slnx` solutions like `CalcDb`, LSP is strictly inferior to `roscli-fast.exe`.

### 5.3 Model Context Protocol (`RoslynSkills.McpServer`)

Architecturally, `RoslynSkills.McpServer` (`0.1.6-preview.31`) does the right thing at execution time: when `WorkspaceHost` is bound, `HotCommandRouter` delegates `RunsOnHotWorkspace` tools to the shared daemon rather than loading a duplicate `MSBuildWorkspace` inside the MCP server, keeping `McpServer` at just **`55.2 MB`** working set and achieving **`15.3–29.6 ms`** steady-state warm query latency (even faster than `roscli-fast.exe` by avoiding `40–50 ms` Windows process creation).

However, **two severe ergonomics flaws** currently make `RoslynSkills.McpServer` net-negative for general agent harnesses:

1. **Per-turn schema bloat (`13.5K` tokens/turn):** `BuildToolList` exposes all **59** registry commands (`53,851 bytes` of JSON schema), and `BuildResourceList` exposes **60** metadata resources (`15,823 bytes`). Over a 20-turn coding session, `tools/list` alone burns `~270K` input tokens (even with prompt caching, cache-write/read overhead and tool-choice confusion remain substantial—consistent with [research-findings.md](research-findings.md) F-2026-02-12-45/47/48 where `roslyn-mcp` consumed `2.0–2.6×` more tokens than `roscli` on microtasks).
2. **Incomplete parameter schemas (~38/59 tools schema-less):** `AddCommandInputHints` in [RoslynSkills.McpServer/Program.cs](../src/RoslynSkills.McpServer/Program.cs) hardcodes property schemas for only ~21 commands. High-value tools such as `diag.get_workspace_snapshot`, `edit.transaction`, `edit.replace_in_member`, `edit.change_signature`, and `ctx.changed_files` emit only `additionalProperties: true`, inviting parameter-guessing retries.
3. **Redundant `HotCommandRouter` hop & indentation mismatch:** `HotCommandRouter.TryRunAsync` invokes `CliApplication.RunAsync`, which formats a string argv, opens a new `NamedPipeClientStream`, parses indented JSON, and re-serializes both `content[0].text` and `structuredContent`. Conversely, notice `McpServer`'s compact JSON (`WriteIndented = false`) produces ~14% smaller response payloads than `roscli`'s indented stdout (`WriteIndented = true`).

### 5.4 Built-In Non-Roslyn Tools (`rg`, `read_file`, `edit_file`, `dotnet build`)

Plain text tools have **zero warmup latency** (`0 ms` bind) and **`0` schema tax**, and remain indispensable for:

- Finding literal strings, error codes (e.g. `3191`, `0x800A0BCD`), comments, Markdown/book citations, `.csproj`/`.slnx`/`.json` files.
- Locating a unique type declaration filename in `~68 ms` (`rg -n -w "class SqlBinder" src`) and reading a targeted 150-line window (`~1.4–2.3K` tokens).
- Making localized single-file edits once exact lines are known.

Where text tools break down in a 33-project C# codebase is **semantic ambiguity and cross-file propagation**:
- Overloaded/common method names (`Execute`: `47.3K` tokens across `src+tests`; `Kind`: `23.0K` tokens; `OpenRecordset`: `16.6K` tokens) bury true callers in comments, test names, and unrelated overloads.
- Text search cannot distinguish interface implementations (`15` in `ISqlDatabase`), track interface-dispatched incoming callers (`854z`), or rename/refactor a signature safely across 33 projects with immediate post-edit compiler diagnostics.
- Full `dotnet build` + test checks (`142–233 s` on `CalcDb`) are the final correctness gate, never an interactive edit-loop check.

---

## 6. Hybrid Harness Guidelines: "Text First, Semantic Second, Scoped Diag Third"

Empirical latency and token ratios yield a clear division of labor between built-in harness tools and `roscli-fast.exe` (or `roscli`):

| Task Intent | Best Primary Tool | Exact Command / Pattern | Why It Wins |
|---|---|---|---|
| **Repo-wide literal, doc, error-code, or non-C# search** | Built-in `rg` (or `ctx.search_text` if daemon already warm) | `rg -n -w "3191" src tests` | `~66–116 ms`, zero warmup, searches docs/JSON/CSProj too. |
| **Read a known 50–150 line C# block** | Built-in `read_file` (or `ctx.member_source`) | `read_file` with `StartLine`/`EndLine` (`~1.4–2.3K` tok) | `0 ms` startup, exact line window. |
| **Jump to declaration without knowing file/line** | `roscli-fast` `--symbol` | `roscli-fast nav.go_to_definition --symbol Type.Member --brief true` | `0.7 ms` host / `~107 ms` wall; skips `rg` -> `read_file` -> parse loop. |
| **Find true references / callers of overloaded or interface symbols** | `roscli-fast` `nav.find_references` / `nav.find_invocations` | `roscli-fast nav.find_references --symbol Type.Member --max-results 100` | Eliminates `12K–47K` token `rg` noise (`Execute`, `Kind`, `OpenRecordset`); `~56–71 ms`. |
| **Trace incoming callers across interfaces (`854z`)** | `roscli-fast` `nav.call_hierarchy` (**`--max-depth 1` first!**) | `roscli-fast nav.call_hierarchy --symbol Type.Member --direction incoming --max-depth 1 --brief true` | Captures interface dispatch (`implemented_member`) LSP misses; caps payload (`depth=2` on `CreateTable` was `124 KB` / `~31K` tok vs `depth=1` `49 KB`). |
| **Find all implementing classes & derived interfaces** | `roscli-fast` `nav.find_implementations` | `roscli-fast nav.find_implementations --symbol Namespace.IInterface` | Finds derived interfaces too (`15` vs LSP `13`). |
| **Multi-symbol lookup in one round-trip** | `roscli-fast` `nav.find_symbol_batch` / `query.batch` | `roscli-fast nav.find_symbol_batch --input ...` | `~52–69 ms` total; avoids N process spawns. |
| **Solution-wide rename or signature change** | `roscli-fast` `edit.rename_symbol` / `edit.change_signature` | `roscli-fast edit.rename_symbol <file> <line> <col> NewName --apply true --require-workspace true` | Updates XML crefs + all 33 projects atomically + returns `diagnostics_after_edit`. |
| **Multi-file exact/span edits or Roslyn quick fixes** | Built-in `edit_file` (1 file) / `roscli-fast edit.transaction` or `edit.apply_code_fix` | `edit.transaction` with `expected_text` (`~170 ms`) | Atomic multi-file check + solution diagnostics before build. |
| **Check compiler errors in edited folder(s) before building** | `roscli-fast` `diag.get_workspace_snapshot` | `roscli-fast diag.get_workspace_snapshot --directory-path src/Project --brief true --include-diagnostics true --require-workspace true` | **`~104–109 ms` (`~1.1K` tok)** vs **`~47–147 s`** `dotnet build` / check! |
| **Final truth / behavioral verification** | `dotnet build` / `dotnet test` / harness check | Solution filter or `tools/landing-check.ps1` | Roslyn snapshot checks syntax/types; tests verify runtime & analyzers. |

### Pitfalls to Avoid (Measured Failure Modes)

1. **Never query `ctx.file_outline` on giant files (`>2,000` lines) when `rg` + `read_file` or `--symbol` suffices:** Until `FileOutlineCommand` supports `RunsOnHotWorkspace: true`, it falls back out of `roscli-fast.exe` to a full `.NET` CLI spawn (`1,219 ms` on `SchemaWriter.cs`).
2. **Never run `nav.call_hierarchy` with unbounded depth on high-fan-in methods:** `SchemaWriter.CreateTable` at `--max-depth 2` emitted **`124,425 bytes` (~`31,107` tokens)** and took **`25.4 s` cold / `855 ms` warm**. Always start with `--max-depth 1 --max-edges 60 --brief true`.
3. **Never treat `csharp-ls` or unguarded Roslyn LSP answers as authoritative on multi-TFM `.NET 11` `.slnx` repos:** `csharp-ls` silently breaks `ProjectReference`s (`1,159` false errors) and `Roslyn LSP` answers `1` match during async load and `0` on interface-routed incoming callers.
4. **Never skip `workspace.use` / `--require-workspace true`:** Without a bound daemon, `roscli` pays `~70 s` per call on `CalcDb.slnx`, or worse, silently degrades to `ad_hoc` mode (`CS0518` / `0` cross-file refs) if `--require-workspace true` is omitted.

---

## 7. Concrete Recommendations for Agent Harnesses (Antigravity Swarm, Claude Code, Codex)

### 7.1 Daemon Lifecycle & Memory Budgeting in Multi-Agent Swarms

- **One daemon per active writer/reviewer worktree, never more:** Each warmed `CalcDb.slnx` `WorkspaceHost` consumes **`~770–985 MB`** RAM (observed across `CalcDb-w1`, `CalcDb-w2`, and `CalcDb`). On a `32 GB` workstation running 2 active writer slots + checks, keep active daemons strictly bounded (`<= 2–3` active `CalcDb` hosts; default cap is `8`), and stop a slot's daemon (`roscli daemon.stop --repo-root <worktree>`) as soon as a writer or reviewer finishes. **Never run `daemon.prune --all` in a shared multi-agent environment** — it kills sibling workers' live hosts.
- **Prefer `roscli daemon.run` when the harness owns a long-lived runner:** Under Antigravity/CodeAlta job objects (`KILL_ON_JOB_CLOSE`), `workspace.use` falls back to WMI `Win32_Process.Create` (`30 min` idle timeout). Where a harness manages a background runner or sidecar, launching `roscli daemon.run --repo-root <worktree>` makes host lifetime explicit and cleanly tied to the agent lifecycle.
- **Always resolve `roscli-fast.exe` first in agent instructions:** Point agents to `%LOCALAPPDATA%\RoslynSkills\bin\roscli-fast.exe` (with `ROSCLI_DAEMON=required` after `workspace.use`) rather than bare `roscli` in tight loops; this saves **`~300–400 ms`** per call.
- **Keep builds serialized behind a machine lock, semantic queries sequential per worktree:** Do not stack concurrent builds/tests alongside cold `workspace.use` warmups on 32 GB machines.

### 7.2 Prompt Posture & Transport Selection

- **Default to CLI (`roscli-fast.exe`) + concise skill (`roslynskills-tight`) rather than 59-tool MCP:** In empirical paired runs ([README.md](../README.md) Fragment B, [research-findings.md](research-findings.md) F-2026-02-17-75), pre-edit tool discovery (`list-commands` + repeated `describe-command`) and MCP schema overhead caused `+22K` to `+80K` token regressions. Inject a 15-line playbook (bind once, use `--symbol`, `rg` for literals, `--max-depth 1`, scoped `diag.get_workspace_snapshot --directory-path`) and point to `roscli describe-command <id>` only on schema errors.
- **Gate MCP adoption on slimming `McpServer`:** Even though `McpServer` delivers `15–30 ms` warm calls, paying `~13.5K` tokens per turn for 59 tools (38 without property schemas) makes it inferior to `roscli-fast.exe` for multi-turn agent loops today.

---

## 8. Prioritized Roadmap: Next RoslynSkills Improvements

Ranked by **measured impact / engineering effort** from the bottlenecks and defects verified in this study:

### P0 — Quick, High-Impact Fixes (Hours)

1. **Hot-route `ctx.file_outline` (`FileOutlineCommand`):** Set `RunsOnHotWorkspace: true, HotWorkspaceOptional: true` and read the cached `SyntaxTree` from `StaticAnalysisWorkspace` / `CommandFileAnalysis` when a hot workspace is bound. *Expected impact:* cuts `SchemaWriter.cs` outline from **`1,219 ms`** fallback to **`~50–70 ms`** via `roscli-fast.exe`.
2. **Complete `QueryBatchCommand.SupportedQueryFactories`:** Add `nav.find_implementations`, `nav.find_overrides`, `diag.get_workspace_snapshot`, `ctx.symbol_envelope`, and `ctx.changed_files` to [QueryBatchCommand.cs](../src/RoslynSkills.Core/Commands/QueryBatchCommand.cs). *Expected impact:* fixes the `unsupported_command` failure encountered when batching implementation + diagnostic + reference queries.
3. **Compact stdout JSON mode (`--compact-json` or `WriteIndented = false` when stdout is redirected):** `McpServer` payloads were ~14% smaller than `roscli` strictly due to `WriteIndented = false`. Making `roscli` emit compact JSON when piped/in `cli/run` saves ~14% output tokens across every call.
4. **Default `nav.call_hierarchy` `--max-depth` to `1` (from `2`):** Prevents accidental `124 KB` / `~31K` token dumps on high-fan-in methods (`CreateTable`) while keeping `--max-depth 2..8` opt-in.

### P1 — MCP Slim Facade & Unified Schema Source (1–2 Days)

5. **Slim 8–10 Tool MCP Mode (`--mode slim` or default):** Expose a focused high-leverage MCP tool set (`roslyn_nav_symbol`, `roslyn_nav_references`, `roslyn_nav_invocations`, `roslyn_nav_call_hierarchy`, `roslyn_nav_implementations`, `roslyn_diag_snapshot`, `roslyn_edit_rename`, `roslyn_edit_transaction`, `roslyn_edit_code_fix`, `roslyn_run` generic dispatcher) backed by **complete JSON schemas**. *Expected impact:* slashes `tools/list` from **`53,851 bytes` (~`13.5K` tokens/turn)** to **~`4–5 KB` (~`1.1K` tokens/turn)**—a **>90% per-turn context tax reduction**—while retaining `15–30 ms` warm stdio latency.
6. **Direct `WorkspaceHost` pipe client in `RoslynSkills.McpServer`:** Bypass `HotCommandRouter -> CliApplication.RunAsync` string argv reconstruction and per-call pipe connection overhead; keep a persistent pipe/session to `WorkspaceHost` and auto-bind `workspace.use` on first workspace-scoped tool call.
7. **Single schema source of truth across Core, CLI, and MCP:** Generate `AddCommandInputHints` (MCP) and `TryBuildDirectShorthandInput` (CLI) from descriptor-level input contracts ([docs/ROSCLI_DESIGN_IMPLEMENTATION_REVIEW_2026-08-22.md](ROSCLI_DESIGN_IMPLEMENTATION_REVIEW_2026-08-22.md)) so zero commands ever fall back to empty `additionalProperties: true` schemas.

### P2 — Warmup Determinism, Batching, and Edit Safety (2–4 Days)

8. **Warmup readiness visibility & optional `--wait-warmup`:** Surface `warmup_state` (`running` / `complete` / `projects_warmed`) in `workspace.status` and add `--wait-warmup true` on `workspace.use` so latency-sensitive benchmarks or CI callers can separate load+warmup from query execution without racing the `2.5–25 s` first-call compilation tail.
9. **Extend `--symbol` targeting to `edit.*`, `nav.call_path`, and `analyze.*`:** Support `--symbol Type.Member` on `edit.rename_symbol`, `edit.change_signature`, `nav.call_path` (`--source-symbol`, `--target-symbol`), and `analyze.impact_slice` so agents never need a separate coordinate-lookup round-trip before semantic refactoring.
10. **Multi-symbol batching (`--symbols` array) & atomic transaction preconditions:** Allow `nav.find_references` / `nav.find_invocations` to accept a small `--symbols` batch in one compilation pass, and add file-hash preconditions + atomic temp-file write/rename to `edit.transaction` / `edit.batch_exact`.

---

## 9. Verification Summary & Artifact Provenance

All empirical claims in Sections 1–8 were directly observed in sequential benchmark runs executed on `2026-11-26` against `C:\Work\DnaCalc\CalcDb\CalcDb.slnx` using `roscli` / `roscli-fast.exe` `0.1.6-preview.31+72a4b26`, `RoslynSkills.McpServer.dll`, `Microsoft.CodeAnalysis.LanguageServer.exe` `5.7.0-1.26220.12`, `csharp-ls.exe` `0.22.0.0`, `tools/csharp-lsp-mcp/server.js`, and `rg`. Build/test landing timings cite recorded measurements in `CalcDb/docs/work/process-log.md`; historical multi-turn token/trajectory baselines cite [docs/research-findings.md](research-findings.md) and [README.md](../README.md). Section 10 below records the Pass 5 (`0.1.6-preview.33`) transcript audit, architecture, trade-off analysis, and sequential read-only benchmarks measured on the same `CalcDb.slnx` host.

---

## 10. Pass 5 (`0.1.6-preview.33`): Transcript Audit, Live Overlay Index, Compound Mindspace, and Roadmap Closure

Pass 4 (`0.1.6-preview.32`) shipped Roadmap §8 P0 items 1–4 and P1 items 5–6 (hot `ctx.file_outline`, expanded `query.batch`, compact redirected stdout JSON, `nav.call_hierarchy` default `--max-depth 1`, slim 10-tool MCP mode at ~4.9 KB, and direct persistent `WorkspaceHost` pipe routing with auto-bind). Pass 5 (`0.1.6-preview.33`) closes the remaining Roadmap §8 items (**P1.7**, **P2.8**, **P2.9**, **P2.10**) and adds a **live in-memory overlay trigram/token + line-offset index**, **syntax-aware search**, **zero-disk window reads**, and **compound symbol mindspace retrieval** driven directly by real multi-agent transcript measurements.

### 10.1 Empirical Transcript Audit (48 Real CalcDb/C# Sessions)

Read-only analysis across **48 real Antigravity C#/CalcDb worker sessions** (**5,129** tool-bearing turns, **10,137** tool calls) surfaced the exact multi-turn loops where agents burned wall time and context tokens:

| Observed Tool / Pattern | Count / Share | Engineering Diagnosis | Pass 5 Target |
|---|---:|---|---|
| `read_file` total / `.cs` reads | **3,863** / **2,477** (`2,326` windowed, `151` full; med `101` lines, p90 `340` lines) | **64.5%** (`1,598 / 2,477`) were repeat reads of the same `.cs` file in one session, including **551** adjacent window-paging hops on the same file. | Zero-disk `ctx.read_window` (single + batched `windows`) backed by `SourceText.Lines`, returning `file_sha256` & `enclosing_symbol`. |
| `run_command` (`rg`) | **1,140** commands (**1,567** `rg` invocations; **151** manually multi-batched) | **1,071** `rg` queries searched C# identifiers/symbols (mixing comments, strings, declarations, and calls); **699** `.cs` reads immediately followed `rg`. | `WorkspaceOverlayIndex` + syntax-filtered `ctx.search_text` (`code`/`comments`/`strings`/`identifiers`/`declarations`/`invocations`) + batched `queries`. |
| `run_command` (`roscli`) | **114** calls (**1.1%** of all calls) | Top action bigrams were `read_cs -> read_cs` (**1,447**), `edit_cs -> edit_cs` (**1,184**), `rg -> read_cs` (**342**), `read_cs -> rg` (**320**), `rg -> rg` (**297**); agents spent 3–5 sequential turns to assemble one symbol's definition, callers, refs, and diagnostics. | Compound `ctx.symbol_context` / MCP `roslyn_context` collapsing search + read + nav + interface callers + diag into **1 bounded round trip**. |
| `edit_file` (`.cs`) | **1,735** calls; **144** multi-file `.cs` edit bursts (avg **3.37** files/burst) | Multi-file edits lacked content-hash optimistic locking and crash-safe atomic commit across files. | `EditFileText.WriteAtomicBatchAsync` (temp-file + TOCTOU SHA-256 re-check + atomic `File.Move` + rollback) + `expected_sha256` / `expected_file_hashes` on `edit.transaction` / `edit.batch_exact`. |

### 10.2 Live In-Memory Overlay Index & Persistence Trade-Off Analysis

**Architecture (`WorkspaceOverlayIndex.cs`):** Layered directly over `WorkspaceHost`'s already-resident `StaticAnalysisWorkspace.SyntaxTreesByPath` and `SourceTextsByTree` without duplicating Roslyn semantic analysis:

- **Per-tree immutable entry cache:** `ConditionalWeakTable<SyntaxTree, OverlayDocumentEntry>` stores each document's `SourceText`, `FullText`, sorted 48-bit packed case-folded unique trigrams (`ulong[]`), lowercased unique identifier tokens (`string[]`), and lazy encoding-aware SHA-256 (`EditFileText.ComputeEncodedSha256(FullText, SourceText.Encoding)` — zero disk reads and bit-identical to disk bytes for both BOM and no-BOM UTF-8 files).
- **Inverted posting lists + BFS directory parity:** `_trigramPostings` and `_tokenPostings` prune non-matching workspace files via smallest-posting-list intersection (including literal anchor extraction for regex patterns). Surviving candidates sort breadth-first by directory depth/name to match `Directory.EnumerateFiles` order.
- **Zero-duplication AST filtering:** When `syntax_filter` (`code`, `comments`, `strings`, `comments_and_strings`, `non_code`, `identifiers`, `declarations`, `invocations`) or `include_syntax_context` is requested, `WorkspaceOverlayIndex.ClassifySyntaxMatch` inspects Roslyn's cached `SyntaxTree` token/trivia/parent syntax nodes directly (zero semantic compilation duplication).
- **Incremental invalidation by Roslyn tree identity:** Because Roslyn's `Solution` is immutable, unchanged documents preserve `ReferenceEquals(existing.SyntaxTree, tree)`. `SyncWithWorkspace` removes postings only for changed/deleted paths and indexes only modified/new trees (`O(changed files)`).

**Evaluated Trade-Off — Why Incremental In-Memory Wins Over On-Disk Persistence (SQLite / mmap):**

| Dimension | Incremental In-Memory Overlay (Chosen) | On-Disk Persistent Index (SQLite / mmap) |
|---|---|---|
| **Cold build cost on `CalcDb.slnx` (521 docs, 51,530 trigrams, 31,913 tokens)** | Built in parallel during Warmup Phase 0 in **`~100–150 ms`** (~**0.2–0.3%** of the `52.9 s` `workspace.use --wait-warmup` bind+compile cost). | Saves `<150 ms` on a `20–53 s` cold MSBuild load (negligible wall-time gain). |
| **Resident memory overhead** | `~15–25 MB` inside the `1,180.5 MB` warmed `WorkspaceHost` (~2% of host RAM); `SourceText` and `SyntaxTree` are already resident in Roslyn. | Still requires resident `SourceText`/`SyntaxTree` in `WorkspaceHost` plus mmap/page-cache working set. |
| **Incremental edit invalidation** | **Exact, `<1 ms`, zero race conditions:** keyed directly on Roslyn's immutable `SyntaxTree` object identity synced from `WorkspaceChangeTracker` and `HotWorkspaceEditRefresh`. | Must detect branch switches, worktree resets, partial writes, and external edits across restarts; risks stale postings or SQLite write-lock contention across concurrent worktrees (`CalcDb`, `CalcDb-w1`, `CalcDb-w2`). |
| **Disk / hygiene footprint** | **Zero** files written on disk (no cache bloat, no repo pollution, no schema-version migration). | Requires persistent cache directory, compaction/pruning, and corruption recovery. |

*Verdict:* Because `WorkspaceHost` already pays the MSBuild solution load once per worktree session and keeps every `SourceText`/`SyntaxTree` in RAM, a live incremental in-memory overlay delivers sub-2 ms index pruning and `0.2–0.3 ms` host window reads with zero persistence complexity. Persistent disk indexing would only become worthwhile for repositories an order of magnitude larger (`>10,000` source files) without an already-loaded Roslyn workspace.

### 10.3 Measured Pass 5 Benchmarks on `CalcDb.slnx` (`0.1.6-preview.33`)

Measured sequentially and strictly read-only on `C:\Work\DnaCalc\CalcDb\CalcDb.slnx` (`33` projects, `632` Roslyn documents, `521` non-generated overlay documents, `0` unresolved projects, `1,180.5 MB` warmed host):

- **Bind + full 33-project `--wait-warmup` (`workspace.use --wait-warmup true`):** **`52,852.5 ms`** total wall (`~20.3 s` MSBuild bind + `32,569.2 ms` topological warmup across all `33/33` projects, `warmup_state: "complete"`, `overlay_index`: `521` docs, `51,530` trigrams, `31,913` tokens, `full_rebuilds: 1`).

| Category & Command (`0.1.6-preview.33`) | Cold Wall (ms) | Warm Med Wall (ms) | Host Exec Med (ms) | Output Bytes (~Tokens) | Key Observed Result |
|---|---:|---:|---:|---:|---|
| **`rg -n -w Execute (*.cs src tests)`** (baseline) | — | `95.6` | — | `201,252` (`~50.3K`) | `1,248` lines (comments, strings, decls, calls mixed) |
| **`ctx.search_text Execute --syntax-filter code`** | `138.6` | `133.6` | `50.6` | `268,821` (full) | **`541` matches** (eliminates 707 comment/string hits; `350` files pruned) |
| **`ctx.search_text Execute --syntax-filter declarations`** | `112.0` | `106.5` | `52.4` | **`6,649` (`~1.7K`)** | **`15` declarations** (**96.7% token reduction** vs `rg`!) |
| **`ctx.search_text Execute --syntax-filter invocations`** | `160.4` | `111.6` | `51.3` | **`5,755` (`~1.4K`)** | **`428` invocations** (brief capped, `350` files pruned) |
| **`rg -n -w DeferredCatalogCommit (*.cs src tests)`** | — | `94.7` | — | `4,904` (`~1.2K`) | `35` lines (disk walk) |
| **`ctx.search_text DeferredCatalogCommit --syntax-filter code`** | **`61.0`** | **`58.3`** | **`1.5`** | `5,734` (`~1.4K`) | **Faster than `rg` (`58.3` vs `94.7 ms`)**, scans `4` files, prunes **`517 / 521`** (`workspace_overlay_index`) |
| **`ctx.search_text` batched (3 queries, decl+inv)** | `69.6` | **`58.0`** | **`5.5`** | `10,824` (`~2.7K`) | **`46` filtered matches in 1 call** (**~4.9× faster** than 3 sequential `rg` calls) |
| **`ctx.read_window SchemaWriter.cs 1 100`** | `84.1` | **`55.6`** | **`0.3`** | `6,389` (`~1.6K`) | Zero-disk (`workspace_overlay_index`) + exact `file_sha256` |
| **`ctx.read_window` batched (3 files × 60 lines)** | `66.3` | **`54.2`** | **`0.2`** | `12,255` (`~3.1K`) | `overlay_hits=3, disk_reads=0` in **1 round trip** |
| **`ctx.symbol_context ComSqlDatabase.OpenSchemaWriter`** | `1,002.1` | **`64.2`** | **`11.7`** | **`5,404` (`~1.4K`)** | **1 call:** def + `4` interface-aware callers + refs + `0` diag (vs 4-step `228.9 ms` + `14,975 B` + file read!) |
| **`ctx.symbol_context DeferredCatalogCommit.Kind`** | `646.1` | **`64.1`** | **`9.2`** | `6,773` (`~1.7K`) | Def + `14` semantic refs + `0` diag in **1 call** |
| **`ctx.symbol_context SchemaWriter.CreateTable`** | `871.1` | **`69.7`** | **`16.8`** | `24,618` (`~6.2K`) | Def + `42` callers + `20` refs + `0` diag in **1 call** |
| **`nav.find_references --symbols` (2 symbols, P2.10)** | `92.6` | **`71.0`** | **`8.4`** | `35,755` (`~8.9K`) | `2/2` succeeded (`59` refs in 1 call) |
| **`nav.find_invocations --symbols` (2 symbols, P2.10)** | `73.9` | **`59.7`** | **`3.2`** | `16,547` (`~4.1K`) | `2/2` succeeded (`42` calls in 1 combined `CallSiteLookup` pass) |
| **`edit.rename_symbol --symbol TextCodec.Decode` (dry-run, P2.9)** | `2,228.3` | `787.6` | `734.8` | `3,794` (`~0.9K`) | Direct `--symbol` solution rename + post-edit diagnostics |
| **`edit.transaction` + `expected_sha256` (dry-run, P2.10)** | `2,056.7` | **`69.6`** | **`19.3`** | `5,997` (`~1.5K`) | Verified SHA-256 precondition + post-edit diagnostics |
| **`analyze.impact_slice --symbol DeferredCatalogCommit.Kind`** | `43,977` | `49,789` | `49,334` | `3,373` (`~0.8K`) | *Honest finding:* `impact_slice` still binds every matching tree manually (`Kind` appears widely); prefer `ctx.symbol_context` (**770× faster**, `64.1 ms`!) until `impact_slice` adopts `SymbolFinder`/`CallSiteLookup`. |
| **MCP `--mode slim` `tools/list` (12 tools, P1.7)** | `184.0` (init) | — | — | **`6,016` (`~1.5K`)** | **12 complete descriptor-driven tools** (`roslyn_search`, `roslyn_context`, + 10); **93.3% smaller** than Pass 5 `--mode full` (`90,498 B`, 61 complete tools). |
| **MCP `roslyn_search` (`DeferredCatalogCommit`, `code`)** | `170.0` | **`6.6`** | — | `5,664` (`~1.4K`) | Persistent pipe over `WorkspaceOverlayIndex` |
| **MCP `roslyn_context` (`OpenSchemaWriter` / `Kind`)** | `1,052–1,197` | **`13.3–15.6`** | — | `5,190–6,571` | Full symbol mindspace in **~14 ms** over persistent pipe |
| **MCP `roslyn_run` `ctx.read_window` (`SchemaWriter 1..100`)** | `6.2` | **`4.8`** | — | `6,865` (`~1.7K`) | Sub-5 ms zero-disk file read over persistent pipe |

### 10.4 Roadmap §8 Closure & Updated Harness Guidance

Every item in Section 8 is now implemented and verified (`378/378` Release tests passing, `0` warnings, `0` skill-validation warnings):

- **P0 (Items 1–4):** Shipped in Pass 4 (`0.1.6-preview.32`).
- **P1 (Items 5–6):** Shipped in Pass 4 (`0.1.6-preview.32`); **Item 7 (Descriptor-Driven Single Schema Source)** shipped in Pass 5 (`CommandInputSchemaCatalog` drives all `61` Core commands, CLI `describe-command` + schema-aware option coercion, MCP `--mode full` [0 parameterized schema-less tools], and MCP `--mode slim` [12 tools at `6,016 B`]).
- **P2 (Items 8–10):** All shipped in Pass 5 (`warmup_state` + `--wait-warmup` on `workspace.use`/`preload`/`status`; `--symbol` / `--source-symbol` / `--target-symbol` on `edit.rename_symbol`, `edit.change_signature`, `nav.call_path`, `analyze.impact_slice`; `--symbols` multi-symbol batching on `nav.find_references` / `nav.find_invocations`; SHA-256 preconditions + crash-safe atomic temp-file/rename/rollback in `EditFileText.WriteAtomicBatchAsync` + `edit.transaction` / `edit.batch_exact`).
- **Updated Default Guidance:**
  - Start symbol investigations with **`ctx.symbol_context --symbol Type.Member`** (or MCP **`roslyn_context`**): replaces 4–5 separate search/read/nav/diag turns with a single `~64 ms` CLI / `~14 ms` MCP call (`~1.4K–1.7K` tokens) that also returns `file_sha256` ready for guarded edits.
  - When searching C# code on a bound daemon, prefer **`ctx.search_text --syntax-filter code|declarations|invocations --whole-word true`** (or batched `--queries`, or MCP **`roslyn_search`**): faster than `rg` (`58 ms` vs `95 ms` on selective tokens, `6.6 ms` over MCP) while eliminating comment/string noise (`96.7%` smaller on `Execute` declarations). Keep `rg` for non-C# files (`.md`, `.json`, `.csproj`, `.slnx`).
  - Use **`ctx.read_window`** (single or batched `windows`) when reading multiple `.cs` slices or capturing `file_sha256` preconditions before **`edit.transaction` / `edit.batch_exact`** (`expected_sha256` / `expected_file_hashes`).
  - **Future follow-up spotted by benchmarks:** Migrate `analyze.impact_slice`'s internal reference/caller scan loops to reuse `FindReferencesCommand.FindSolutionReferencesAsync` and `CallSiteAnalysis` (matching `ctx.symbol_context`'s `~64 ms` speed instead of scanning all syntax trees).
