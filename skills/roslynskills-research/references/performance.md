# Performance (Hot Workspace Daemon & NativeAOT Client)

## Fastest Mode (`0.1.6-preview.31`, Recommended)

Bind the solution once into `WorkspaceHost`, fail closed with `ROSCLI_DAEMON=required`, and invoke the 2.15 MB NativeAOT client (`roscli-fast.exe`) instead of spawning a `.NET` CLI process per call:

```powershell
roscli workspace.use MySolution.slnx --require-solution true
$env:ROSCLI_DAEMON = "required"
& "$env:LOCALAPPDATA\RoslynSkills\bin\roscli-fast.exe" nav.find_references --symbol MyType.Member --max-results 100
```

Measured steady state on 33-project `CalcDb.slnx`:
- `roscli --no-daemon`: `~70 s` per call (reloads MSBuild)
- `roscli` (.NET CLI -> daemon): `~365–465 ms` warm
- `roscli-fast.exe` (`cli/run` -> daemon): **`~50–75 ms` warm** (`watcher_fast_path` freshness `~1.5–2.3 ms`)
- Stop **only your own** worktree daemon at closeout: `roscli daemon.stop --repo-root <path>` (`~770 MB` RAM per bound 33-project host).

## Fallback Published Wrapper Mode (When Developing From Source)

When `roscli-fast.exe` is not built/installed and you run `scripts/roscli` many times in one session:

PowerShell:

```powershell
scripts\roscli-warm.cmd
$env:ROSCLI_USE_PUBLISHED = "1"
```

Bash:

```bash
bash scripts/roscli-warm
export ROSCLI_USE_PUBLISHED=1
```

Refresh published binaries once (after editing roscli source):

- PowerShell: `$env:ROSCLI_REFRESH_PUBLISHED = "1"`
- Bash: `export ROSCLI_REFRESH_PUBLISHED=1`

Practical cache policy:

- Default fast loop: `ROSCLI_USE_PUBLISHED=1` and keep stale checks off unless you are debugging tool builds.
- After changing roscli source, run exactly one refreshed call (`ROSCLI_REFRESH_PUBLISHED=1`) and then continue normal cached mode.
- If behavior seems inconsistent with recent source edits, clear/refresh published cache before benchmarking.

Note: published mode is for speed; when debugging the roscli tool itself, you may prefer `dotnet run` style execution.
