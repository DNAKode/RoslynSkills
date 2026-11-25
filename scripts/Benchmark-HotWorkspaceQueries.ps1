#requires -Version 7
<#
.SYNOPSIS
  Wall-clock and stage-timing benchmark of hot-workspace roscli queries against `rg`, on a real solution.

.DESCRIPTION
  Assumes a daemon is already bound (`roscli workspace.use <solution> --require-solution true`) from -RepoRoot.
  Each query runs -Runs times with ROSCLI_DAEMON=required; the first run is reported separately (warm-up) and the
  median/min of the rest are the steady state. Stage timings come from the envelope (Telemetry.timing and
  Transport.timings), so the table shows where the time goes: CLI process spawn + pipe round trip (wall minus total_ms),
  the host's freshness/status check, and the command execution itself.

  Always finish with `roscli daemon.stop` from -RepoRoot (this script never starts or stops a daemon).
#>
param(
    [string]$RepoRoot = 'C:\Work\DnaCalc\CalcDb',
    [string]$Roscli = 'roscli',
    [int]$Runs = 7,
    [string]$OutputJson = ''
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot
$env:ROSCLI_DAEMON = 'required'

$queries = @(
    @{ Name = 'nav.find_symbol';          Args = @('nav.find_symbol', 'src/CalcDb.Format/TextCodec.cs', 'TryEncode');
       Rg = @('-n', '-w', 'TryEncode', 'src/CalcDb.Format/TextCodec.cs') },
    @{ Name = 'nav.find_references';      Args = @('nav.find_references', 'src/CalcDb.Format/TextCodec.cs', '46', '21', '--max-results', '200');
       Rg = @('-n', '-w', 'TextCodec', 'src', '--glob', '*.cs') },
    @{ Name = 'nav.find_references (iface)'; Args = @('nav.find_references', 'src/CalcDb.Sql/SqlBinder.cs', '14', '18', '--max-results', '200');
       Rg = @('-n', '-w', 'ISqlDatabase', 'src', '--glob', '*.cs') },
    @{ Name = 'nav.find_invocations';     Args = @('nav.find_invocations', 'src/CalcDb.Format/TextCodec.cs', '72', '24', '--max-results', '200');
       Rg = @('-n', '-w', 'TryEncode', 'src', '--glob', '*.cs') },
    @{ Name = 'nav.find_implementations'; Args = @('nav.find_implementations', 'src/CalcDb.Sql/SqlBinder.cs', '14', '18', '--max-results', '200');
       Rg = @('-n', '-w', 'ISqlDatabase', 'src', '--glob', '*.cs') },
    @{ Name = 'ctx.search_text';          Args = @('ctx.search_text', 'TextCodec', 'src', '--max-results', '200');
       Rg = @('-n', '-w', 'TextCodec', 'src') },
    @{ Name = 'diag.get_workspace_snapshot'; Args = @('diag.get_workspace_snapshot', '--directory-path', 'src/CalcDb.Sql', '--include-diagnostics', 'true');
       Rg = $null }
)

function Measure-Once([scriptblock]$Action) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $Action
    $sw.Stop()
    return [pscustomobject]@{ Ms = $sw.Elapsed.TotalMilliseconds; Output = $out }
}

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = $Values | Sort-Object
    return $sorted[[int][math]::Floor(($sorted.Count - 1) / 2)]
}

$rows = foreach ($q in $queries) {
    $walls = New-Object System.Collections.Generic.List[double]
    $totals = New-Object System.Collections.Generic.List[double]
    $executes = New-Object System.Collections.Generic.List[double]
    $hostMs = New-Object System.Collections.Generic.List[double]
    $freshMs = New-Object System.Collections.Generic.List[double]
    $hostExec = New-Object System.Collections.Generic.List[double]
    $roundtrip = New-Object System.Collections.Generic.List[double]
    $freshMode = $null
    $first = $null
    $ok = $true
    $matches = $null
    for ($i = 0; $i -lt $Runs; $i++) {
        $m = Measure-Once { & $Roscli @($q.Args) 2>&1 | Out-String }
        $json = $null
        try { $json = $m.Output | ConvertFrom-Json } catch { }
        if ($i -eq 0) { $first = $m.Ms } else { $walls.Add($m.Ms) }
        if ($null -eq $json -or -not $json.Ok) { $ok = $false }
        if ($i -gt 0 -and $null -ne $json) {
            if ($json.Telemetry -and $json.Telemetry.timing) {
                $totals.Add([double]$json.Telemetry.timing.total_ms)
                $executes.Add([double]$json.Telemetry.timing.execute_ms)
            }
            if ($json.Transport -and $json.Transport.timings -and $json.Transport.timings.host_elapsed_ms) {
                $hostMs.Add([double]$json.Transport.timings.host_elapsed_ms)
                $roundtrip.Add([double]$json.Transport.timings.roundtrip_ms)
                if ($null -ne $json.Transport.timings.host_freshness_ms) { $freshMs.Add([double]$json.Transport.timings.host_freshness_ms) }
                if ($null -ne $json.Transport.timings.host_execute_ms) { $hostExec.Add([double]$json.Transport.timings.host_execute_ms) }
                if ($json.Transport.timings.host_freshness_mode) { $freshMode = $json.Transport.timings.host_freshness_mode }
            }
        }
    }

    $rgMs = $null
    if ($q.Rg) {
        $rgRuns = 1..$Runs | ForEach-Object { (Measure-Once { & rg @($q.Rg) | Out-Null }).Ms }
        $rgMs = Get-Median ([double[]]($rgRuns | Select-Object -Skip 1))
    }

    [pscustomobject]@{
        Query       = $q.Name
        Ok          = $ok
        FirstMs     = [math]::Round($first, 0)
        WallMedian  = [math]::Round((Get-Median $walls.ToArray()), 0)
        WallMin     = [math]::Round(($walls | Measure-Object -Minimum).Minimum, 0)
        TotalMs     = if ($totals.Count) { [math]::Round((Get-Median $totals.ToArray()), 1) } else { $null }
        ExecuteMs   = if ($executes.Count) { [math]::Round((Get-Median $executes.ToArray()), 1) } else { $null }
        HostMs      = if ($hostMs.Count) { [math]::Round((Get-Median $hostMs.ToArray()), 1) } else { $null }
        FreshMs     = if ($freshMs.Count) { [math]::Round((Get-Median $freshMs.ToArray()), 1) } else { $null }
        HostExecMs  = if ($hostExec.Count) { [math]::Round((Get-Median $hostExec.ToArray()), 1) } else { $null }
        PipeMs      = if ($roundtrip.Count -and $hostMs.Count) { [math]::Round((Get-Median $roundtrip.ToArray()) - (Get-Median $hostMs.ToArray()), 1) } else { $null }
        FreshMode   = $freshMode
        RgMedian    = if ($null -ne $rgMs) { [math]::Round($rgMs, 0) } else { $null }
    }
}

$rows | Format-Table -AutoSize
if ($OutputJson) {
    $rows | ConvertTo-Json | Set-Content -Path $OutputJson -Encoding utf8
}
