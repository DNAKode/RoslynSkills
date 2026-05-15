param(
    [Parameter(Mandatory = $true)][string]$TargetRoot,
    [string]$QueriesFile = "",
    [string]$WorkspacePath = "",
    [string]$RoscliPath = "",
    [int]$Iterations = 1,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Iterations -lt 1) {
    throw "Iterations must be >= 1."
}

function Resolve-RepoRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptRoot)

    return (Resolve-Path (Join-Path $ScriptRoot "..\..")).Path
}

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$RequestedPath
    )

    $resolved = $RequestedPath
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        $resolved = Join-Path $RepoRoot "artifacts/roscli-vs-rg/$stamp"
    } elseif (-not [System.IO.Path]::IsPathRooted($resolved)) {
        $resolved = Join-Path $RepoRoot $resolved
    }

    New-Item -ItemType Directory -Force -Path $resolved | Out-Null
    return (Resolve-Path $resolved).Path
}

function Resolve-RoscliPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if ([System.IO.Path]::IsPathRooted($RequestedPath)) {
            return $RequestedPath
        }

        return Join-Path $RepoRoot $RequestedPath
    }

    if ($IsWindows) {
        return Join-Path $RepoRoot "scripts/roscli.cmd"
    }

    return Join-Path $RepoRoot "scripts/roscli"
}

function Convert-ToText {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    if ($Value -is [System.Array]) {
        return [string]::Join([Environment]::NewLine, $Value)
    }

    return [string]$Value
}

function Try-ParseJsonEnvelope {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $trimmed = $Text.Trim()
    try {
        return ($trimmed | ConvertFrom-Json -Depth 100 -ErrorAction Stop)
    } catch {
        # fall through
    }

    $lines = $trimmed -split "`r?`n"
    for ($i = $lines.Length - 1; $i -ge 0; $i--) {
        $candidate = $lines[$i].Trim()
        if (-not $candidate.StartsWith("{", [System.StringComparison]::Ordinal)) {
            continue
        }

        try {
            return ($candidate | ConvertFrom-Json -Depth 100 -ErrorAction Stop)
        } catch {
            # keep scanning
        }
    }

    return $null
}

function Resolve-TargetPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$PathValue
    )

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return (Resolve-Path $PathValue).Path
    }

    return (Resolve-Path (Join-Path $Root $PathValue)).Path
}

function Resolve-WorkspacePathValue {
    param(
        [Parameter(Mandatory = $true)][string]$TargetRootPath,
        [string]$RequestedWorkspacePath,
        [string]$ScenarioWorkspacePath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedWorkspacePath)) {
        return Resolve-TargetPath -Root $TargetRootPath -PathValue $RequestedWorkspacePath
    }

    if (-not [string]::IsNullOrWhiteSpace($ScenarioWorkspacePath)) {
        return Resolve-TargetPath -Root $TargetRootPath -PathValue $ScenarioWorkspacePath
    }

    $candidate = Get-ChildItem -Path $TargetRootPath -Filter *.sln -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -First 1
    if ($null -ne $candidate) {
        return $candidate.FullName
    }

    $candidate = Get-ChildItem -Path $TargetRootPath -Filter *.slnx -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -First 1
    if ($null -ne $candidate) {
        return $candidate.FullName
    }

    $candidate = Get-ChildItem -Path $TargetRootPath -Filter *.csproj -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -First 1
    if ($null -ne $candidate) {
        return $candidate.FullName
    }

    throw "Could not infer workspace path under '$TargetRootPath'. Pass -WorkspacePath or include workspace_path in the scenario file."
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $output = $null
    $exitCode = 1
    Push-Location $WorkingDirectory
    try {
        $output = & $Executable @Arguments 2>&1
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    } catch {
        $output = $_
        $exitCode = if ($null -eq $LASTEXITCODE) { 1 } else { [int]$LASTEXITCODE }
    } finally {
        Pop-Location
        $stopwatch.Stop()
    }

    return [pscustomobject]@{
        elapsed_ms = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        exit_code = $exitCode
        succeeded = ($exitCode -eq 0)
        output_text = Convert-ToText $output
    }
}

function Get-Average {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    return [Math]::Round(($Values | Measure-Object -Average).Average, 3)
}

$repoRoot = Resolve-RepoRoot -ScriptRoot $PSScriptRoot
$targetRootPath = (Resolve-Path $TargetRoot).Path
$outputRoot = Resolve-OutputDirectory -RepoRoot $repoRoot -RequestedPath $OutputDirectory
$roscliExecutable = Resolve-RoscliPath -RepoRoot $repoRoot -RequestedPath $RoscliPath

if (-not (Test-Path $roscliExecutable -PathType Leaf)) {
    throw "Roscli launcher '$roscliExecutable' was not found."
}

$scenarioPath = $QueriesFile
if ([string]::IsNullOrWhiteSpace($scenarioPath)) {
    $scenarioPath = Join-Path $repoRoot "benchmarks/scenarios/roscli-vs-rg-aims-symbol-queries.json"
}
if (-not [System.IO.Path]::IsPathRooted($scenarioPath)) {
    $scenarioPath = Join-Path $repoRoot $scenarioPath
}
if (-not (Test-Path $scenarioPath -PathType Leaf)) {
    throw "Scenario file '$scenarioPath' was not found."
}

$scenario = Get-Content -Path $scenarioPath -Raw | ConvertFrom-Json -Depth 100
$scenarioWorkspacePath = if ($scenario.PSObject.Properties.Name -contains "workspace_path") { [string]$scenario.workspace_path } else { "" }
$queriesRaw = if ($scenario -is [System.Array]) { $scenario } else { $scenario.queries }
if ($null -eq $queriesRaw -or $queriesRaw.Count -eq 0) {
    throw "Scenario '$scenarioPath' did not contain any queries."
}

$resolvedWorkspacePath = Resolve-WorkspacePathValue `
    -TargetRootPath $targetRootPath `
    -RequestedWorkspacePath $WorkspacePath `
    -ScenarioWorkspacePath $scenarioWorkspacePath

$queries = New-Object System.Collections.Generic.List[object]
$index = 0
foreach ($query in $queriesRaw) {
    $index++
    $label = if ($query.PSObject.Properties.Name -contains "label" -and -not [string]::IsNullOrWhiteSpace([string]$query.label)) {
        [string]$query.label
    } else {
        "query-$index"
    }

    $queries.Add([pscustomobject]@{
            index = $index
            label = $label
            file_path = Resolve-TargetPath -Root $targetRootPath -PathValue ([string]$query.file_path)
            symbol_name = [string]$query.symbol_name
        }) | Out-Null
}

$batchPayloadPath = Join-Path $outputRoot "batch-input.json"
$batchPayload = [ordered]@{
    queries = @(
        $queries | ForEach-Object {
            [ordered]@{
                label = $_.label
                file_path = $_.file_path
                symbol_name = $_.symbol_name
            }
        }
    )
    brief = $true
    first_declaration = $true
    require_workspace = $true
    workspace_path = $resolvedWorkspacePath
}
$batchPayload | ConvertTo-Json -Depth 100 | Set-Content -Path $batchPayloadPath -Encoding utf8

$samples = New-Object System.Collections.Generic.List[object]
$batchRuns = New-Object System.Collections.Generic.List[object]

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    foreach ($query in $queries) {
        $roscliSingle = Invoke-ExternalCommand `
            -Executable $roscliExecutable `
            -Arguments @(
                "nav.find_symbol",
                $query.file_path,
                $query.symbol_name,
                "--brief",
                "true",
                "--first-declaration",
                "true",
                "--require-workspace",
                "true",
                "--workspace-path",
                $resolvedWorkspacePath
            ) `
            -WorkingDirectory $repoRoot
        $roscliEnvelope = Try-ParseJsonEnvelope -Text $roscliSingle.output_text
        $roscliWorkspace = $null
        $totalMatches = $null
        if ($null -ne $roscliEnvelope -and $null -ne $roscliEnvelope.Data -and $null -ne $roscliEnvelope.Data.query) {
            $roscliWorkspace = $roscliEnvelope.Data.query.workspace_context
            $totalMatches = $roscliEnvelope.Data.total_matches
        }

        $samples.Add([pscustomobject]@{
                mode = "roscli_single"
                iteration = $iteration
                label = $query.label
                symbol_name = $query.symbol_name
                file_path = $query.file_path
                elapsed_ms = $roscliSingle.elapsed_ms
                exit_code = $roscliSingle.exit_code
                succeeded = $roscliSingle.succeeded
                total_matches = $totalMatches
                workspace_mode = if ($null -eq $roscliWorkspace) { $null } else { [string]$roscliWorkspace.mode }
                workspace_cache_mode = if ($null -eq $roscliWorkspace) { $null } else { [string]$roscliWorkspace.workspace_cache_mode }
                workspace_cache_hit = if ($null -eq $roscliWorkspace) { $null } else { [bool]$roscliWorkspace.workspace_cache_hit }
                workspace_load_duration_ms = if ($null -eq $roscliWorkspace) { $null } else { $roscliWorkspace.workspace_load_duration_ms }
                preview = if ($null -eq $roscliEnvelope) { $null } else { [string]$roscliEnvelope.Preview }
            }) | Out-Null

        $rgRun = Invoke-ExternalCommand `
            -Executable "rg" `
            -Arguments @(
                "-n",
                "--fixed-strings",
                $query.symbol_name,
                $query.file_path
            ) `
            -WorkingDirectory $targetRootPath
        $rgSucceeded = $rgRun.exit_code -eq 0 -or $rgRun.exit_code -eq 1
        $rgLines = if ($rgSucceeded) {
            @($rgRun.output_text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        } else {
            @()
        }
        $samples.Add([pscustomobject]@{
                mode = "rg"
                iteration = $iteration
                label = $query.label
                symbol_name = $query.symbol_name
                file_path = $query.file_path
                elapsed_ms = $rgRun.elapsed_ms
                exit_code = $rgRun.exit_code
                succeeded = $rgSucceeded
                total_matches = $rgLines.Count
                workspace_mode = $null
                workspace_cache_mode = $null
                workspace_cache_hit = $null
                workspace_load_duration_ms = $null
                preview = ($rgLines | Select-Object -First 3) -join " | "
            }) | Out-Null
    }

    $batchRun = Invoke-ExternalCommand `
        -Executable $roscliExecutable `
        -Arguments @(
            "run",
            "nav.find_symbol_batch",
            "--input",
            "@$batchPayloadPath"
        ) `
        -WorkingDirectory $repoRoot
    $batchEnvelope = Try-ParseJsonEnvelope -Text $batchRun.output_text
    $batchResults = @()
    if ($null -ne $batchEnvelope -and $null -ne $batchEnvelope.Data -and $null -ne $batchEnvelope.Data.results) {
        $batchResults = @($batchEnvelope.Data.results)
    }

    $batchRuns.Add([pscustomobject]@{
            mode = "roscli_batch"
            iteration = $iteration
            elapsed_ms = $batchRun.elapsed_ms
            exit_code = $batchRun.exit_code
            succeeded = $batchRun.succeeded
            query_count = $queries.Count
            preview = if ($null -eq $batchEnvelope) { $null } else { [string]$batchEnvelope.Preview }
            query_results = @(
                $batchResults | ForEach-Object {
                    $workspace = $null
                    if ($null -ne $_.data -and $null -ne $_.data.query) {
                        $workspace = $_.data.query.workspace_context
                    }

                    [pscustomobject]@{
                        label = $_.label
                        file_path = $_.file_path
                        symbol_name = $_.symbol_name
                        ok = $_.ok
                        elapsed_ms = $_.elapsed_ms
                        total_matches = if ($null -eq $_.data) { $null } else { $_.data.total_matches }
                        workspace_mode = if ($null -eq $workspace) { $null } else { [string]$workspace.mode }
                        workspace_cache_mode = if ($null -eq $workspace) { $null } else { [string]$workspace.workspace_cache_mode }
                        workspace_cache_hit = if ($null -eq $workspace) { $null } else { [bool]$workspace.workspace_cache_hit }
                        workspace_load_duration_ms = if ($null -eq $workspace) { $null } else { $workspace.workspace_load_duration_ms }
                    }
                }
            )
        }) | Out-Null
}

$summaryRows = @(
    $samples |
    Group-Object mode |
    ForEach-Object {
        $values = @($_.Group | ForEach-Object { [double]$_.elapsed_ms })
        [pscustomobject]@{
            mode = $_.Name
            sample_count = $_.Count
            success_count = @($_.Group | Where-Object { $_.succeeded }).Count
            elapsed_ms_avg = Get-Average -Values $values
            elapsed_ms_total = [Math]::Round(($values | Measure-Object -Sum).Sum, 3)
        }
    }
)

$batchSummary = @(
    $batchRuns | ForEach-Object {
        [pscustomobject]@{
            mode = $_.mode
            iteration = $_.iteration
            elapsed_ms = $_.elapsed_ms
            exit_code = $_.exit_code
            succeeded = $_.succeeded
            cache_hits = @($_.query_results | Where-Object { $_.workspace_cache_hit -eq $true }).Count
            cache_misses = @($_.query_results | Where-Object { $_.workspace_cache_hit -eq $false }).Count
            query_count = $_.query_count
        }
    }
)

$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    target_root = $targetRootPath
    workspace_path = $resolvedWorkspacePath
    scenario_file = (Resolve-Path $scenarioPath).Path
    roscli_path = $roscliExecutable
    iterations = $Iterations
    query_count = $queries.Count
    queries = @($queries.ToArray())
    samples = @($samples.ToArray())
    batch_runs = @($batchRuns.ToArray())
    summary = @($summaryRows)
    batch_summary = @($batchSummary)
}

$jsonPath = Join-Path $outputRoot "report.json"
$report | ConvertTo-Json -Depth 100 | Set-Content -Path $jsonPath -Encoding utf8

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Roscli vs rg Query Benchmark")
$md.Add("")
$md.Add("- target_root: $targetRootPath")
$md.Add("- workspace_path: $resolvedWorkspacePath")
$md.Add("- scenario_file: $((Resolve-Path $scenarioPath).Path)")
$md.Add("- iterations: $Iterations")
$md.Add("- query_count: $($queries.Count)")
$md.Add("")
$md.Add("## Summary")
$md.Add("")
$md.Add("| Mode | Samples | Success | Avg ms | Total ms |")
$md.Add("| --- | ---: | ---: | ---: | ---: |")
foreach ($row in $summaryRows | Sort-Object mode) {
    $md.Add("| $($row.mode) | $($row.sample_count) | $($row.success_count) | $($row.elapsed_ms_avg) | $($row.elapsed_ms_total) |")
}
$md.Add("")
$md.Add("## Batch Runs")
$md.Add("")
$md.Add("| Iteration | Elapsed ms | Success | Cache hits | Cache misses | Query count |")
$md.Add("| --- | ---: | ---: | ---: | ---: | ---: |")
foreach ($row in $batchSummary) {
    $successText = if ($row.succeeded) { "yes" } else { "no" }
    $md.Add("| $($row.iteration) | $($row.elapsed_ms) | $successText | $($row.cache_hits) | $($row.cache_misses) | $($row.query_count) |")
}
$md.Add("")
$md.Add("## Per-Query Samples")
$md.Add("")
$md.Add("| Mode | Iteration | Label | Elapsed ms | Matches | Workspace | Cache hit | Preview |")
$md.Add("| --- | ---: | --- | ---: | ---: | --- | --- | --- |")
foreach ($row in $samples | Sort-Object mode, iteration, label) {
    $preview = if ([string]::IsNullOrWhiteSpace([string]$row.preview)) { "" } else { ([string]$row.preview).Replace("|", "/").Replace("`r", " ").Replace("`n", " ") }
    $workspaceMode = if ([string]::IsNullOrWhiteSpace([string]$row.workspace_mode)) { "" } else { [string]$row.workspace_mode }
    $cacheHit = if ($null -eq $row.workspace_cache_hit) { "" } elseif ([bool]$row.workspace_cache_hit) { "true" } else { "false" }
    $matches = if ($null -eq $row.total_matches) { "" } else { [string]$row.total_matches }
    $md.Add("| $($row.mode) | $($row.iteration) | $($row.label) | $($row.elapsed_ms) | $matches | $workspaceMode | $cacheHit | $preview |")
}

$mdPath = Join-Path $outputRoot "report.md"
$md | Set-Content -Path $mdPath -Encoding utf8

Write-Host "Wrote report:"
Write-Host "  $jsonPath"
Write-Host "  $mdPath"
