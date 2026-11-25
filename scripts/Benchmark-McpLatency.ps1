#requires -Version 7
<#
.SYNOPSIS
  Steady-state latency of MCP tool calls routed to the warm WorkspaceHost daemon.

.DESCRIPTION
  Starts the MCP server (RoslynSkills.McpServer) once from -RepoRoot, then times -Calls sequential `tools/call`
  requests per query over stdio (the first call of each query is reported separately). Assumes a daemon is bound
  (`roscli workspace.use <solution>`) for -RepoRoot; never starts or stops one.
#>
param(
    [string]$RepoRoot = 'C:\Work\DnaCalc\CalcDb',
    [string]$McpDll = 'C:\Work\RoslynSkills\src\RoslynSkills.McpServer\bin\Release\net10.0\RoslynSkills.McpServer.dll',
    [int]$Calls = 15
)

$ErrorActionPreference = 'Stop'
$psi = [Diagnostics.ProcessStartInfo]::new('dotnet')
$psi.ArgumentList.Add($McpDll)
$psi.WorkingDirectory = $RepoRoot
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$process = [Diagnostics.Process]::Start($psi)
$id = 0

function Invoke-Mcp([hashtable]$Request) {
    $script:id++
    $Request['jsonrpc'] = '2.0'
    $Request['id'] = $script:id
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $process.StandardInput.WriteLine(($Request | ConvertTo-Json -Depth 10 -Compress))
    $process.StandardInput.Flush()
    $line = $process.StandardOutput.ReadLine()
    $sw.Stop()
    return [pscustomobject]@{ Ms = $sw.Elapsed.TotalMilliseconds; Json = ($line | ConvertFrom-Json) }
}

try {
    $null = Invoke-Mcp @{ method = 'initialize'; params = @{ protocolVersion = '2025-06-18' } }
    $queries = @(
        @{ Name = 'nav.find_symbol'; Tool = 'roslyn_nav_find_symbol'; Args = @{ file_path = 'src/CalcDb.Format/TextCodec.cs'; symbol_name = 'TryEncode'; brief = $true } },
        @{ Name = 'nav.find_references'; Tool = 'roslyn_nav_find_references'; Args = @{ file_path = 'src/CalcDb.Format/TextCodec.cs'; line = 46; column = 21; max_results = 200 } },
        @{ Name = 'ctx.search_text'; Tool = 'roslyn_ctx_search_text'; Args = @{ patterns = @('TextCodec'); roots = @('src'); max_results = 200 } }
    )
    $rows = foreach ($query in $queries) {
        $times = New-Object System.Collections.Generic.List[double]
        $first = $null
        $mode = $null
        for ($i = 0; $i -lt $Calls; $i++) {
            $result = Invoke-Mcp @{ method = 'tools/call'; params = @{ name = $query.Tool; arguments = $query.Args } }
            $structured = $result.Json.result.structuredContent
            if (-not $structured.ok) { throw "$($query.Name) failed: $($result.Json | ConvertTo-Json -Depth 6 -Compress)" }
            $mode = "$($structured.transport.mode)/$($structured.transport.daemon_endpoint -ne $null)"
            if ($i -eq 0) { $first = $result.Ms } else { $times.Add($result.Ms) }
        }
        $sorted = $times | Sort-Object
        [pscustomobject]@{
            Query = $query.Name
            FirstMs = [math]::Round($first, 1)
            MedianMs = [math]::Round($sorted[[int][math]::Floor(($sorted.Count - 1) / 2)], 1)
            MinMs = [math]::Round($sorted[0], 1)
            MaxMs = [math]::Round($sorted[-1], 1)
            ModeViaDaemon = $mode
        }
    }
    $rows | Format-Table -AutoSize
}
finally {
    try { $process.StandardInput.Close() } catch { }
    if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
}
