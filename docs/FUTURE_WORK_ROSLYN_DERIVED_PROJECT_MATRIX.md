# Future Work: Roslyn-Derived Project Pattern Matrix

Date: 2026-02-25  
Status: backlog planning artifact

## Goal

Capture external Roslyn-derived project patterns that can materially improve:

- `roscli` throughput (fewer round-trips, lower latency),
- `xmlcli` structure tooling quality (especially malformed XML/XAML handling),
- benchmark rigor and operational reliability.

## Candidate Projects

1. `dotnet/roslyn`  
   Link: `https://github.com/dotnet/roslyn`
2. `dotnet/roslyn-sdk`  
   Link: `https://github.com/dotnet/roslyn-sdk`
3. `OmniSharp/omnisharp-roslyn`  
   Link: `https://github.com/OmniSharp/omnisharp-roslyn`
4. `dotnet/roslynator`  
   Link: `https://github.com/dotnet/roslynator`
5. `DotNetAnalyzers/StyleCopAnalyzers`  
   Link: `https://github.com/DotNetAnalyzers/StyleCopAnalyzers`
6. `meziantou/Meziantou.Analyzer`  
   Link: `https://github.com/meziantou/Meziantou.Analyzer`
7. `SonarSource/sonar-dotnet`  
   Link: `https://github.com/SonarSource/sonar-dotnet`
8. `KirillOsenkov/XmlParser`  
   Link: `https://github.com/KirillOsenkov/XmlParser`

## What To Steal Matrix

| Project | Stealable pattern | Why it matters here | RoslynSkills target | Priority |
|---|---|---|---|---|
| `dotnet/roslyn` | Workspace lifetime + semantic cache discipline | We still pay repeated per-call semantic load costs in CLI mode | Transport/MCP-first lanes + optional persistent workspace host for CLI bundles | High |
| `dotnet/roslyn-sdk` | Analyzer/code-fix test harness patterns | Stronger command regression coverage for semantic invariants | Add fixture-first tests for edit/diagnostic commands | Medium |
| `OmniSharp/omnisharp-roslyn` | Long-lived request server model and incremental document updates | Directly addresses process-per-call overhead and repeated parse/compilation | Expand `RoslynSkills.TransportServer` into a first-class persistent execution lane | High |
| `dotnet/roslynator` | Rich rule taxonomy, deterministic fixability metadata | Better command discoverability and safer auto-repair planning | Enrich `describe-command`/`llmstxt` with rule/fixability metadata | Medium |
| `StyleCopAnalyzers` | Strict diagnostic categorization and suppression hygiene | Cleaner benchmark pass/fail interpretation and lower false positives | Add severity/category filter conventions to `diag.*` and benchmark gates | Medium |
| `Meziantou.Analyzer` | Practical high-signal analyzer rule design | Useful model for low-noise command outputs and actionable diagnostics | Add signal-first filtering presets to `diag.get_workspace_snapshot` | Medium |
| `sonar-dotnet` | Scalable rule execution/reporting architecture | Useful for larger repo scans and stable summary formats | Improve aggregated diagnostic reporting format and trendability | Low-Medium |
| `KirillOsenkov/XmlParser` | Roslyn-style full-fidelity XML parsing + tolerant model | Best external lead for future `xmlcli` tolerant parse + rich structure model | `xmlcli` backend evolution beyond current `XDocument`/`language_xml` split | High |

## Immediate Experiments

1. Persistent Roslyn workspace lane
- Hypothesis: persistent process + in-memory workspace context reduces treatment round-trips and latency.
- Experiment:
  - Compare `roscli` CLI per-call vs `RoslynSkills.TransportServer` on identical task bundles.
  - Track first-edit latency, total round-trips, and mutation channel.
- Acceptance:
  - Lower median duration and pre-edit calls with no correctness regressions.

2. Xml parser model evolution spike
- Hypothesis: Roslyn-style XML model can improve malformed/XAML tooling quality without exploding payload size.
- Experiment:
  - Prototype parser adapter behind `xml.parse_compare` with richer node fidelity flags.
  - Compare node/path/line stability on malformed fixtures.
- Acceptance:
  - Better tolerant-parse usefulness on malformed inputs and stable output contracts.

3. Diagnostic signal shaping
- Hypothesis: rule/category-aware output shaping lowers verification churn.
- Experiment:
  - Add optional category/severity bucketing presets for `diag.*` summary outputs.
  - Measure effect on post-edit diagnostics loops in paired runs.
- Acceptance:
  - Fewer redundant diagnostic calls with same pass rate.

## Implementation Notes

- Keep project boundaries explicit:
  - `roscli`: C# semantic navigation/edit/diagnostic truth.
  - `xmlcli`: XML/XAML structure truth.
- Keep transport choices benchmarked:
  - CLI call-path and persistent server-path should remain comparable and auditable.
- Treat prompt/profile updates as product changes:
  - Any guidance shift should be measured, not assumed.

## Deferred / Watchlist

- `dotnet/format` (archived): useful historical reference, but not primary implementation target.
- `dotnet/roslyn-analyzers` (archived): use as historical rule design reference only; active analyzer evolution moved elsewhere.

