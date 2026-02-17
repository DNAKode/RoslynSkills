# Performance (Many Roslyn Calls)

When running long agent loops with many Roslyn calls, prefer published execution:

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

Note: published mode is for speed; when debugging the roscli tool itself, you may prefer `dotnet run` style execution.

