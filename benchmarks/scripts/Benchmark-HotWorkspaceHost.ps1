param(
    [string]$WorkspacePath = "RoslynSkills.slnx",
    [string]$FilePath = "src/RoslynSkills.Cli/CliApplication.cs",
    [string]$SymbolName = "TryGetCommandAndInputAsync",
    [string]$Alias = "benchmark-hot",
    [int]$Iterations = 5,
    [string]$OutputJsonPath = "",
    [string]$OutputMarkdownPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-RoscliCall {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [hashtable]$EnvironmentOverrides = @{}
    )

    $previousEnv = @{}
    foreach ($key in $EnvironmentOverrides.Keys) {
        [void]$previousEnv.Set_Item($key, [Environment]::GetEnvironmentVariable($key, "Process"))
        [void][Environment]::SetEnvironmentVariable($key, [string]$EnvironmentOverrides[$key], "Process")
    }

    try {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $output = (& "$PSScriptRoot\..\..\scripts\roscli.cmd" @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $stopwatch.Stop()
        $outputText = if ($output -is [System.Array]) { [string]::Join([Environment]::NewLine, $output) } else { [string]$output }
        return [pscustomobject]@{
            exit_code = $exitCode
            elapsed_ms = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 2)
            output = $outputText
            json = ConvertFrom-RoscliOutput -Output $outputText
        }
    } finally {
        foreach ($key in $EnvironmentOverrides.Keys) {
            [void][Environment]::SetEnvironmentVariable($key, $previousEnv[$key], "Process")
        }
    }
}

function ConvertFrom-RoscliOutput {
    param([Parameter(Mandatory = $true)][string]$Output)

    $lines = $Output -split "`r?`n"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].TrimStart().StartsWith("{")) {
            $start = $i
            break
        }
    }

    if ($start -lt 0) {
        return $null
    }

    $jsonText = [string]::Join([Environment]::NewLine, $lines[$start..($lines.Count - 1)])
    return $jsonText | ConvertFrom-Json
}

function Get-WorkspaceContext {
    param([object]$Json)

    if ($null -eq $Json -or -not (Test-ObjectProperty -Object $Json -PropertyName "Data") -or $null -eq $Json.Data) {
        return $null
    }

    if ((Test-ObjectProperty -Object $Json.Data -PropertyName "workspace_context") -and $null -ne $Json.Data.workspace_context) {
        return $Json.Data.workspace_context
    }

    if ((Test-ObjectProperty -Object $Json.Data -PropertyName "query") -and
        $null -ne $Json.Data.query -and
        (Test-ObjectProperty -Object $Json.Data.query -PropertyName "workspace_context")) {
        return $Json.Data.query.workspace_context
    }

    if ((Test-ObjectProperty -Object $Json.Data -PropertyName "envelope") -and
        $null -ne $Json.Data.envelope -and
        (Test-ObjectProperty -Object $Json.Data.envelope -PropertyName "Data") -and
        $null -ne $Json.Data.envelope.Data -and
        (Test-ObjectProperty -Object $Json.Data.envelope.Data -PropertyName "workspace_context")) {
        return $Json.Data.envelope.Data.workspace_context
    }

    if ((Test-ObjectProperty -Object $Json.Data -PropertyName "envelope") -and
        $null -ne $Json.Data.envelope -and
        (Test-ObjectProperty -Object $Json.Data.envelope -PropertyName "Data") -and
        $null -ne $Json.Data.envelope.Data -and
        (Test-ObjectProperty -Object $Json.Data.envelope.Data -PropertyName "query") -and
        $null -ne $Json.Data.envelope.Data.query -and
        (Test-ObjectProperty -Object $Json.Data.envelope.Data.query -PropertyName "workspace_context")) {
        return $Json.Data.envelope.Data.query.workspace_context
    }

    return $null
}

function Test-ObjectProperty {
    param(
        [object]$Object,
        [string]$PropertyName
    )

    return $null -ne $Object -and $null -ne $Object.PSObject.Properties[$PropertyName]
}

function New-SampleRow {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][int]$Iteration,
        [Parameter(Mandatory = $true)][object]$Run
    )

    $context = Get-WorkspaceContext -Json $Run.json
    return [pscustomobject][ordered]@{
        mode = $Mode
        iteration = $Iteration
        exit_code = $Run.exit_code
        elapsed_ms = $Run.elapsed_ms
        ok = if ($null -ne $Run.json) { [bool]$Run.json.Ok } else { $false }
        resolution_source = if ($null -ne $context) { $context.resolution_source } else { $null }
        workspace_cache_mode = if ($null -ne $context) { $context.workspace_cache_mode } else { $null }
        workspace_cache_hit = if ($null -ne $context) { $context.workspace_cache_hit } else { $null }
        fallback_reason = if ($null -ne $context) { $context.fallback_reason } else { $null }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$workspaceFullPath = if ([System.IO.Path]::IsPathRooted($WorkspacePath)) { (Resolve-Path $WorkspacePath).Path } else { (Resolve-Path (Join-Path $repoRoot $WorkspacePath)).Path }
$fileFullPath = if ([System.IO.Path]::IsPathRooted($FilePath)) { (Resolve-Path $FilePath).Path } else { (Resolve-Path (Join-Path $repoRoot $FilePath)).Path }

$samples = New-Object System.Collections.Generic.List[object]
$directArgs = @("nav.find_symbol", $fileFullPath, $SymbolName, "--workspace-path", $workspaceFullPath, "--require-workspace", "true", "--brief", "true")
$hotArgs = @("nav.find_symbol", $fileFullPath, $SymbolName, "--repo-root", $repoRoot, "--require-workspace", "true", "--brief", "true")

try {
    $preload = Invoke-RoscliCall -Arguments @("workspace.use", $workspaceFullPath, "--repo-root", $repoRoot, "--alias", $Alias, "--require-solution", "true")
    if ($preload.exit_code -ne 0 -or $null -eq $preload.json -or -not [bool]$preload.json.Ok) {
        throw "workspace.use failed before benchmark. Output: $($preload.output)"
    }

    for ($i = 1; $i -le $Iterations; $i++) {
        $directRun = Invoke-RoscliCall -Arguments ($directArgs + @("--no-daemon"))
        $samples.Add((New-SampleRow -Mode "direct_workspace" -Iteration $i -Run $directRun)) | Out-Null

        $hotRun = Invoke-RoscliCall `
            -Arguments $hotArgs `
            -EnvironmentOverrides @{
                ROSCLI_DAEMON = "required"
                ROSCLI_WORKSPACE_ALIAS = $Alias
                ROSCLI_REQUIRE_HOT_WORKSPACE = "1"
            }
        $samples.Add((New-SampleRow -Mode "daemon_hot_workspace" -Iteration $i -Run $hotRun)) | Out-Null
    }

    $strictRefresh = Invoke-RoscliCall -Arguments @("workspace.refresh", $Alias, "--repo-root", $repoRoot, "--mode", "strict")
    $hotRows = @($samples | Where-Object { $_.mode -eq "daemon_hot_workspace" })
    $directRows = @($samples | Where-Object { $_.mode -eq "direct_workspace" })
    $gateChecks = [pscustomobject][ordered]@{
        hot_all_ok = -not [bool](@($hotRows | Where-Object { -not $_.ok -or $_.exit_code -ne 0 }).Count)
        hot_uses_workspace_handle = -not [bool](@($hotRows | Where-Object { $_.resolution_source -ne "workspace_handle" }).Count)
        hot_uses_process_hot_cache = -not [bool](@($hotRows | Where-Object { $_.workspace_cache_mode -ne "process_hot" }).Count)
        hot_no_ad_hoc_fallback = -not [bool](@($hotRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.fallback_reason) }).Count)
        direct_all_ok = -not [bool](@($directRows | Where-Object { -not $_.ok -or $_.exit_code -ne 0 }).Count)
        strict_refresh_ok = $strictRefresh.exit_code -eq 0 -and $null -ne $strictRefresh.json -and [bool]$strictRefresh.json.Ok
    }
    $gatePassed = -not [bool](@($gateChecks.PSObject.Properties | Where-Object { -not [bool]$_.Value }).Count)

    $summary = New-Object System.Collections.Generic.List[object]
    foreach ($mode in @("direct_workspace", "daemon_hot_workspace")) {
        $modeRows = @($samples | Where-Object { $_.mode -eq $mode })
        $elapsedTotal = 0.0
        foreach ($modeRow in $modeRows) {
            $elapsedTotal += [double]$modeRow.elapsed_ms
        }
        $avg = if ($modeRows.Count -gt 0) { [Math]::Round(($elapsedTotal / $modeRows.Count), 2) } else { $null }
        $summary.Add([pscustomobject][ordered]@{
            mode = $mode
            samples = $modeRows.Count
            avg_elapsed_ms = $avg
            failures = @($modeRows | Where-Object { -not $_.ok -or $_.exit_code -ne 0 }).Count
        }) | Out-Null
    }

    $gateChecksForReport = [ordered]@{}
    foreach ($property in $gateChecks.PSObject.Properties) {
        $gateChecksForReport[$property.Name] = [bool]$property.Value
    }

    $report = [ordered]@{
        generated_utc = (Get-Date).ToUniversalTime().ToString("o")
        repo_root = $repoRoot
        workspace_path = $workspaceFullPath
        file_path = $fileFullPath
        symbol_name = $SymbolName
        iterations = $Iterations
        gate_passed = $gatePassed
        gate_checks = $gateChecksForReport
        summary = $summary.ToArray()
        samples = $samples.ToArray()
    }
} finally {
    try {
        [void](Invoke-RoscliCall -Arguments @("workspace.close", $Alias, "--repo-root", $repoRoot))
        [void](Invoke-RoscliCall -Arguments @("daemon.stop", "--repo-root", $repoRoot))
        $aliasFile = Join-Path $repoRoot ".roslynskills/workspaces.json"
        if ((Test-Path $aliasFile) -and ((Get-Content -Path $aliasFile -Raw).Trim() -eq "{}")) {
            Remove-Item -LiteralPath $aliasFile -Force
            $aliasDirectory = Split-Path -Parent $aliasFile
            if ((Test-Path $aliasDirectory) -and -not (Get-ChildItem -LiteralPath $aliasDirectory -Force)) {
                Remove-Item -LiteralPath $aliasDirectory -Force
            }
        }
    } catch {
    }
}

if ([string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $OutputJsonPath = Join-Path $repoRoot "artifacts/hot-workspace-host/hot-workspace-host-benchmark.json"
}
if (-not [System.IO.Path]::IsPathRooted($OutputJsonPath)) {
    $OutputJsonPath = Join-Path $repoRoot $OutputJsonPath
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputJsonPath) | Out-Null
$report | ConvertTo-Json -Depth 20 | Set-Content -Path $OutputJsonPath

if ([string]::IsNullOrWhiteSpace($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path (Split-Path -Parent $OutputJsonPath) "hot-workspace-host-benchmark.md"
}
if (-not [System.IO.Path]::IsPathRooted($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path $repoRoot $OutputMarkdownPath
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Hot Workspace Host Benchmark")
$md.Add("")
$md.Add("- Generated UTC: $($report.generated_utc)")
$md.Add(("- Workspace: ``{0}``" -f $report.workspace_path))
$md.Add(("- Query: ``{0}`` in ``{1}``" -f $report.symbol_name, $report.file_path))
$md.Add(("- Gate passed: ``{0}``" -f $report.gate_passed))
$md.Add("")
$md.Add("## Gate Checks")
$md.Add("")
foreach ($check in $gateChecks.PSObject.Properties) {
    $md.Add(("- {0}: ``{1}``" -f $check.Name, $check.Value))
}
$md.Add("")
$md.Add("## Summary")
$md.Add("")
$md.Add("| Mode | Samples | Avg Elapsed (ms) | Failures |")
$md.Add("|---|---:|---:|---:|")
foreach ($row in $summary) {
    $md.Add("| $($row.mode) | $($row.samples) | $($row.avg_elapsed_ms) | $($row.failures) |")
}
$md | Set-Content -Path $OutputMarkdownPath

Write-Host ("REPORT_JSON={0}" -f $OutputJsonPath)
Write-Host ("REPORT_MD={0}" -f $OutputMarkdownPath)
Write-Host ("GATE_PASSED={0}" -f $report.gate_passed)
if (-not $report.gate_passed) {
    exit 2
}
