# Tool Call Perf Findings (2026-02-25)

Run artifact:
- `artifacts/tool-call-perf/20260225-133134/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-133134/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-140237/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-140237/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-140752/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-140752/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-140947/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-140947/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-141302/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-141302/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-142049/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-142049/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-142336/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-142336/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-145434/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-145434/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-192452/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-192452/tool-call-perf.md`
- `artifacts/tool-call-perf/20260225-193720/tool-call-perf.json`
- `artifacts/tool-call-perf/20260225-193720/tool-call-perf.md`

Scope:
- `roscli` + `xmlcli`
- profiles: `dotnet_run`, `dotnet_run_no_build_release`, `published_cached_stale_off`, `published_prewarmed_stale_off`, optional `*_jit_forced`, optional `transport_persistent_server*`
- transport spot-check profile: `transport_persistent_server` (`roscli`, filtered `system.ping`)
- latest replicate pack: `5` iterations (`20260225-145434`)
- follow-up replicate packs now include:
  - JIT sensitivity profiles (`*_jit_forced`),
  - warmup contrast (`WarmupIterations=1` and `WarmupIterations=0`),
  - first-vs-steady breakout fields in aggregate rows,
  - wall-time 95% confidence intervals (`normal_approx_95`).

## Key Observations

1. Process overhead dominates small commands.
- `roscli system.ping`: `2660.57ms` (`dotnet_run`) -> `943.11ms` (`dotnet_run_no_build_release`) -> `172.12ms` (`published_prewarmed_stale_off`).
- `xmlcli system.ping`: `2567.85ms` -> `960.80ms` -> `160.96ms`.
- Envelope telemetry for these commands is near-zero; almost all time is launch overhead.

2. `*_DOTNET_RUN_NO_BUILD + Release` is a practical speed optimization.
- Across all `xmlcli` measured commands: ~`0.37-0.38x` of baseline `dotnet_run`.
- For heavy Roslyn workspace commands: still improves (`~0.89-0.90x`) by reducing startup tax.

3. Published cache profiles remove most startup cost but do not fix heavy semantic load cost.
- `roscli nav.find_symbol.cliapp`:
  - `dotnet_run`: `17387.37ms`
  - `published_cached_stale_off`: `14845.29ms`
- `workspace_load_ms` remains around `14s`, so workspace load is now the dominant bottleneck.

4. XML parse internals are currently cheap relative to launch overhead.
- `xml.validate_document.valid_basic`: `command_parse_ms` around `11-12ms`.
- `xml.file_outline.layout_brief`: `command_parse_ms` around `10-11ms`.
- Current parse-cache work should be evaluated, but launcher/process overhead must stay in focus.

5. Persistent transport lane is now integrated in the same harness and shows a large warm-call win over process-per-call profiles.
- Focused replicate run (`20260225-141302`, warmup enabled):
  - `roscli system.ping`: `3065.46ms` (`dotnet_run`) -> `0.74ms` (`transport_persistent_server`), ratio effectively near-zero at 3-decimal precision.
  - `roscli cli.list_commands.ids`: `2961.10ms` -> `2.17ms`.
- Process-per-call published wrappers are still strong (`~170-182ms` for roscli ping/list), but warm transport is another order of magnitude lower on these micro-commands.

6. Cold-vs-warm transport behavior is materially different and should be separated in reporting.
- Single-shot transport smoke (`20260225-140237`) included first-call effects and showed higher numbers.
- Warmed replicate run (`20260225-141302`, warmup 1) shows steady-state sub-5ms behavior for ping/list.
- Transport `tool/list` does not return command telemetry envelopes, so startup/telemetry fields are intentionally null for that command.

7. JIT sensitivity is now directly measured, and confirms dependency/JIT cost is substantial for process-per-call wrappers.
- In no-warmup replicate (`20260225-142336`), forcing JIT-heavy runtime settings (`DOTNET_ReadyToRun=0`, tiered compilation off) increased published wrapper command latency by roughly `3x-5x`:
  - `roscli system.ping`: `196.56ms` -> `686.22ms`
  - `roscli cli.list_commands.ids`: `196.76ms` -> `657.74ms`
  - `xmlcli system.ping`: `171.27ms` -> `603.14ms`
  - `xml.validate_document.valid_basic`: `200.37ms` -> `864.42ms`
- This confirms large framework/dependency assemblies materially affect startup/JIT cost for process-per-call lanes.

8. We are not effectively "JIT-warming everything" in process-per-call profiles.
- Even with warmup iterations, each measured process-per-call sample starts a new process, so JIT state is not reused across samples.
- Warmup mainly helps caches external to process lifetime (publish artifacts, filesystem, wrapper state), not full in-process JIT reuse.
- Persistent transport is different: one long-lived process keeps JIT hot after first call.

9. Warmup effect is stark for transport, especially with JIT-forced settings.
- `transport_persistent_server` (`system.ping`): warmup0 `23.82ms` avg with first call `70.24ms`; warmup1 `2.30ms` avg.
- `transport_persistent_server_jit_forced` (`system.ping`): warmup0 `184.80ms` avg with first call `553.31ms`; warmup1 `0.82ms` avg.
- This is the cleanest evidence that first-call JIT/assembly load should be reported separately from steady-state transport performance.

10. Confidence intervals now make profile comparisons less anecdotal.
- Example from `20260225-145434`:
  - `xmlcli system.ping` `published_cached_stale_off`: `160.775ms`, CI95 `[153.141, 168.410]`
  - `xmlcli system.ping` `published_cached_stale_off_jit_forced`: `572.223ms`, CI95 `[544.758, 599.687]`
- The intervals are clearly separated, reinforcing the JIT-sensitivity conclusion for process-per-call lanes.

11. Bootstrap CI is now integrated as an optional robustness layer.
- New run (`20260225-192452`) used `-IncludeBootstrapCi -BootstrapResamples 1000`.
- Report now emits both:
  - normal-approx CI (`wall_ms_ci95_low/high`),
  - bootstrap percentile CI (`wall_ms_bootstrap_ci95_low/high`),
  plus steady-state bootstrap CI in `cold_warm_summary`.
- Example (`xmlcli system.ping`, `published_cached_stale_off`):
  - normal CI95: `[154.832, 163.230]`
  - bootstrap CI95: `[156.704, 163.311]`
- both are tight and directionally consistent.

12. Baseline-ratio bootstrap CIs are now emitted and validated on a larger replicate (`10` iterations).
- New run (`20260225-193720`) includes `baseline_deltas` ratio bootstrap bounds (`wall_ms_ratio_bootstrap_ci95_low/high`, plus startup/telemetry ratio bounds when present).
- Examples:
  - `roscli cli.list_commands.ids` `published_prewarmed_stale_off`: wall ratio `0.051`, bootstrap CI `[0.048, 0.054]`.
  - `xmlcli system.ping` `published_prewarmed_stale_off`: wall ratio `0.052`, bootstrap CI `[0.049, 0.055]`.
  - `xml.validate_document.valid_basic` `published_cached_stale_off_jit_forced`: wall ratio `0.280`, bootstrap CI `[0.260, 0.299]`.
- This makes the comparative ratio claims directly auditable without assuming normality in the ratio distribution.

## Applied Optimization This Round

Added wrapper knobs for fast local loops:
- `ROSCLI_DOTNET_RUN_NO_BUILD=1`
- `ROSCLI_DOTNET_RUN_CONFIGURATION=Release`
- `XMLCLI_DOTNET_RUN_NO_BUILD=1`
- `XMLCLI_DOTNET_RUN_CONFIGURATION=Release`

And integrated this as a benchmark arm: `dotnet_run_no_build_release`.

## Benchmark Harness Notes

- New harness primes published profiles once (`REFRESH_PUBLISHED=1`) before measurement to avoid stale-cache contamination.
- Harness uses envelope telemetry fields (`timing`, `cache_context`, command telemetry) plus wall-time so startup vs execution is explicit.
- Harness now supports command filtering (`-IncludeCommands`, `-ExcludeCommands`) and emits baseline deltas (`baseline_deltas`) against `dotnet_run` for each command/profile pair.
- Aggregate rows now include `invocation_modes` and `request_chars_avg` so transport-vs-cli shape is visible in one table.
- Aggregate rows now include CI fields (`wall_ms_ci95_low`, `wall_ms_ci95_high`) and the markdown includes an explicit `Cold vs Steady Summary` table.
- When enabled, aggregate and cold/warm tables also include bootstrap CI fields.

## Research Ideas (Next)

1. Run replicate-backed mixed command packs (`Iterations >= 5`) across `dotnet_run`, `published_*`, and `transport_persistent_server`.
2. Keep first-call vs warm-call breakout as a mandatory read in all transport comparisons; avoid averaging-only summaries.
3. Add optional bootstrap CIs as a robustness cross-check against normal-approx CIs.
4. Add stale-check-on/off focused packs for short commands to isolate stale-check tax.
5. Add micro-batch lane (`query.batch` or equivalent) to quantify startup amortization vs transport.
