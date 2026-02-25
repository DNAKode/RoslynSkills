# Tool Call Performance Benchmark (roscli + xmlcli)

Date: 2026-02-25  
Status: active local benchmark workflow

## Goal

Measure local tool-call performance with enough detail to separate:

1. launcher/process overhead,
2. command execution time,
3. workspace/parse timing signals,
4. cache profile effects.

The script emits raw samples and aggregate rollups for both `roscli` and `xmlcli`.

## Script

- `benchmarks/scripts/Benchmark-ToolCallPerf.ps1`

## Quick Run

```powershell
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass `
  -File benchmarks/scripts/Benchmark-ToolCallPerf.ps1 `
  -Iterations 3 `
  -WarmupIterations 1 `
  -IncludeDotnetRunNoBuildProfiles `
  -IncludeJitSensitivityProfiles `
  -IncludeBootstrapCi `
  -BootstrapResamples 2000 `
  -IncludeStaleCheckOnProfiles
```

Outputs:

- `artifacts/tool-call-perf/<timestamp>/tool-call-perf.json`
- `artifacts/tool-call-perf/<timestamp>/tool-call-perf.md`

## Measured Profiles

Per tool (`roscli` and `xmlcli`):

- `dotnet_run`
- `published_cached_stale_off`
- `published_prewarmed_stale_off`
- optional: `published_*_stale_on`
- optional: `dotnet_run_no_build_release`
- optional (`roscli`): `transport_persistent_server`
- optional JIT sensitivity: `*_jit_forced` profiles (`DOTNET_ReadyToRun=0`, `DOTNET_TieredCompilation=0`, quick-jit disabled)
- optional bootstrap CI: `-IncludeBootstrapCi` with configurable `-BootstrapResamples` and `-BootstrapSeed`

Use `-IncludeRoscliTransportProfile` to add the persistent transport lane.

## Focused Command Sets

Use command filters to benchmark only the commands you care about:

```powershell
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass `
  -File benchmarks/scripts/Benchmark-ToolCallPerf.ps1 `
  -Iterations 2 `
  -WarmupIterations 0 `
  -IncludeRoscliTransportProfile `
  -IncludeCommands system.ping,nav.find_symbol.cliapp `
  -ExcludeCommands xml.parse_compare.layout
```

Filters match either `command_id` (`system.ping`) or tool-qualified form (`roscli:system.ping`).
The harness normalizes common shell formats, so comma-delimited single strings and quoted lists are both accepted.

## Key Fields To Compare

Per sample:

- `wall_ms`: end-to-end process call duration.
- `telemetry_total_ms`: command telemetry total from envelope.
- `startup_overhead_ms`: `wall_ms - telemetry_total_ms`.
- `binary_launch_mode`: reported launcher mode.
- `workspace_load_ms`/`msbuild_registration_ms`: Roslyn workspace timing.
- `command_parse_ms`, `parse_cache_mode`, `parse_cache_hit`: XML parse lane telemetry.

Per aggregate row:

- `wall_ms_avg`, `wall_ms_p50`, `wall_ms_p95`
- `wall_ms_ci95_low`, `wall_ms_ci95_high` (normal-approx 95% CI)
- `wall_ms_bootstrap_ci95_low`, `wall_ms_bootstrap_ci95_high` (bootstrap percentile CI when enabled)
- `first_measure_wall_ms`, `steady_wall_ms_avg`, `steady_vs_first_wall_ms_ratio`
- `telemetry_total_ms_avg`
- `startup_overhead_ms_avg`
- `workspace_load_ms_avg`, `command_parse_ms_avg`
- `request_chars_avg`
- `parse_cache_hit_rate`

Additional rollups:

- `baseline_deltas`: per `tool + command`, each profile compared to `dotnet_run` (`delta` and `ratio` for wall/startup/telemetry), with optional bootstrap CI fields for ratio/delta (`*_bootstrap_ci95_*`).
- `cold_warm_summary`: explicit first-call vs steady-state table (`first_minus_steady_wall_ms`, `first_over_steady_wall_ratio`, steady CI95).

## Optimization Guidance

Expected interpretation pattern:

1. If `startup_overhead_ms_avg` dominates, prefer published cache profiles and prewarm where practical.
2. If `telemetry_total_ms_avg` dominates, optimize command internals (workspace/parse paths).
3. For repeated local loops, evaluate `dotnet_run_no_build_release` as a fast iteration mode.
4. Keep stale-check-on profiles as safety arms, not default speed arms, unless evidence shows negligible overhead.
