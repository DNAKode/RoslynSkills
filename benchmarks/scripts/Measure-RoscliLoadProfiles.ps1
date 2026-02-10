param(
    [string]$OutputJsonPath = "",
    [string]$OutputMarkdownPath = "",
    [int[]]$LoadCounts = @(1, 5, 20),
    [switch]$IncludeRefreshProfile,
    [switch]$IncludeStaleCheckOffProfiles,
    [switch]$IncludeStaleCheckOnProfiles
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-RoscliCall {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][hashtable]$EnvironmentOverrides
    )

    $existingEnv = @{}
    foreach ($key in $EnvironmentOverrides.Keys) {
        [void]$existingEnv.Set_Item($key, [Environment]::GetEnvironmentVariable($key, "Process"))
        [void][Environment]::SetEnvironmentVariable($key, [string]$EnvironmentOverrides[$key], "Process")
    }

    try {
        $output = (& "$PSScriptRoot\..\..\scripts\roscli.cmd" @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $outputText = if ($output -is [System.Array]) { [string]::Join([Environment]::NewLine, $output) } else { [string]$output }
        $result = [pscustomobject]@{
            exit_code = $exitCode
            output = $outputText
        }
        return ,$result
    } finally {
        foreach ($key in $EnvironmentOverrides.Keys) {
            [void][Environment]::SetEnvironmentVariable($key, $existingEnv[$key], "Process")
        }
    }
}

function Invoke-Profile {
    param(
        [Parameter(Mandatory = $true)][string]$ProfileId,
        [Parameter(Mandatory = $true)][hashtable]$EnvironmentOverrides,
        [Parameter(Mandatory = $false)][bool]$Prewarm = $false,
        [Parameter(Mandatory = $true)][int[]]$LoadCounts,
        [Parameter(Mandatory = $true)][hashtable[]]$CommandProfiles
    )

    $rows = New-Object System.Collections.Generic.List[object]

    if ($Prewarm) {
        try {
            [void](Invoke-RoscliCall -Arguments @("system.ping") -EnvironmentOverrides $EnvironmentOverrides)
        } catch {
            throw "Prewarm failed for profile '$ProfileId'."
        }
    }

    foreach ($commandProfile in $CommandProfiles) {
        $commandId = [string]$commandProfile.id
        $arguments = @($commandProfile.args)

        foreach ($load in $LoadCounts) {
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $failures = 0
            $lastOutput = ""

            for ($i = 0; $i -lt $load; $i++) {
                $result = Invoke-RoscliCall -Arguments $arguments -EnvironmentOverrides $EnvironmentOverrides
                if ([int]$result.exit_code -ne 0) {
                    $failures++
                }
                $lastOutput = [string]$result.output
            }

            $stopwatch.Stop()
            $elapsedMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 2)
            $avgMs = if ($load -gt 0) { [Math]::Round(($elapsedMs / $load), 2) } else { $null }

            $rows.Add([ordered]@{
                    profile = $ProfileId
                    command = $commandId
                    load_count = $load
                    total_elapsed_ms = $elapsedMs
                    avg_elapsed_ms = $avgMs
                    failures = $failures
                    last_output_preview = if ([string]::IsNullOrWhiteSpace($lastOutput)) { "" } elseif ($lastOutput.Length -gt 160) { $lastOutput.Substring(0, 160) } else { $lastOutput }
                }) | Out-Null
        }
    }

    return @($rows.ToArray())
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$commandProfiles = @(
    @{ id = "system.ping"; args = @("system.ping") }
    @{ id = "cli.list_commands.compact"; args = @("list-commands", "--compact") }
    @{ id = "nav.find_symbol.cliapp"; args = @("nav.find_symbol", "src/RoslynAgent.Cli/CliApplication.cs", "TryGetCommandAndInputAsync", "--brief", "true", "--max-results", "20") }
)

$profiles = @(
    @{ id = "dotnet_run"; env = @{ ROSCLI_USE_PUBLISHED = "0"; ROSCLI_REFRESH_PUBLISHED = "0" }; prewarm = $false }
    @{ id = "published_cached"; env = @{ ROSCLI_USE_PUBLISHED = "1"; ROSCLI_REFRESH_PUBLISHED = "0" }; prewarm = $false }
    @{ id = "published_prewarmed"; env = @{ ROSCLI_USE_PUBLISHED = "1"; ROSCLI_REFRESH_PUBLISHED = "0" }; prewarm = $true }
)

if ($IncludeRefreshProfile) {
    $profiles += @{ id = "published_refresh_each_run"; env = @{ ROSCLI_USE_PUBLISHED = "1"; ROSCLI_REFRESH_PUBLISHED = "1" }; prewarm = $false }
}

if ($IncludeStaleCheckOffProfiles) {
    $profiles += @(
        @{ id = "published_cached_stale_off"; env = @{ ROSCLI_USE_PUBLISHED = "1"; ROSCLI_REFRESH_PUBLISHED = "0"; ROSCLI_STALE_CHECK = "0" }; prewarm = $false }
        @{ id = "published_prewarmed_stale_off"; env = @{ ROSCLI_USE_PUBLISHED = "1"; ROSCLI_REFRESH_PUBLISHED = "0"; ROSCLI_STALE_CHECK = "0" }; prewarm = $true }
    )
}

if ($IncludeStaleCheckOnProfiles) {
    $profiles += @(
        @{ id = "published_cached_stale_on"; env = @{ ROSCLI_USE_PUBLISHED = "1"; ROSCLI_REFRESH_PUBLISHED = "0"; ROSCLI_STALE_CHECK = "1" }; prewarm = $false }
        @{ id = "published_prewarmed_stale_on"; env = @{ ROSCLI_USE_PUBLISHED = "1"; ROSCLI_REFRESH_PUBLISHED = "0"; ROSCLI_STALE_CHECK = "1" }; prewarm = $true }
    )
}

$allRows = New-Object System.Collections.Generic.List[object]
foreach ($profile in $profiles) {
    Write-Host ("PROFILE={0}" -f $profile.id)
    $rows = Invoke-Profile `
        -ProfileId ([string]$profile.id) `
        -EnvironmentOverrides ([hashtable]$profile.env) `
        -Prewarm ([bool]$profile.prewarm) `
        -LoadCounts $LoadCounts `
        -CommandProfiles $commandProfiles
    foreach ($row in $rows) {
        $allRows.Add($row) | Out-Null
    }
}

$rowsArray = @($allRows | Sort-Object profile, command, load_count)
$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    repo_root = $repoRoot
    load_counts = $LoadCounts
    command_profiles = $commandProfiles
    rows = $rowsArray
}

if ([string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $OutputJsonPath = Join-Path $repoRoot "artifacts/roscli-load-profiles/roscli-load-profiles.json"
}
if (-not [System.IO.Path]::IsPathRooted($OutputJsonPath)) {
    $OutputJsonPath = Join-Path $repoRoot $OutputJsonPath
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputJsonPath) | Out-Null
$report | ConvertTo-Json -Depth 20 | Set-Content -Path $OutputJsonPath

if ([string]::IsNullOrWhiteSpace($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path (Split-Path -Parent $OutputJsonPath) "roscli-load-profiles.md"
}
if (-not [System.IO.Path]::IsPathRooted($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path $repoRoot $OutputMarkdownPath
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Roscli Load Profile Benchmark")
$md.Add("")
$md.Add("- Generated UTC: $($report.generated_utc)")
$md.Add("- Repo root: $($report.repo_root)")
$md.Add("- Load counts: $([string]::Join(', ', $report.load_counts))")
$md.Add("")
$md.Add("| Profile | Command | Load | Total Elapsed (ms) | Avg/Call (ms) | Failures |")
$md.Add("|---|---|---:|---:|---:|---:|")
foreach ($row in $rowsArray) {
    $md.Add("| $($row.profile) | $($row.command) | $($row.load_count) | $($row.total_elapsed_ms) | $($row.avg_elapsed_ms) | $($row.failures) |")
}
$md | Set-Content -Path $OutputMarkdownPath

Write-Host ("REPORT_JSON={0}" -f $OutputJsonPath)
Write-Host ("REPORT_MD={0}" -f $OutputMarkdownPath)
