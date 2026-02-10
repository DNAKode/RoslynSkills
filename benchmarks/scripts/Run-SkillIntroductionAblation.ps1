param(
    [string]$OutputRoot = "",
    [string[]]$Profiles = @("standard", "skill-minimal", "schema-first", "brief-first", "surgical"),
    [string]$CodexModel = "",
    [string]$ClaudeModel = "",
    [switch]$SkipCodex,
    [switch]$SkipClaude,
    [switch]$IncludeMcpTreatment,
    [string]$CliPublishConfiguration = "Release",
    [bool]$FailOnControlContamination = $true,
    [switch]$ReuseExistingBundles
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        return (Join-Path $RepoRoot "artifacts/skill-intro-ablation/$stamp")
    }

    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        return $OutputRoot
    }

    return (Join-Path $RepoRoot $OutputRoot)
}

function Get-RunByMode {
    param(
        [Parameter(Mandatory = $true)][object[]]$Runs,
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$Mode
    )

    $matches = @($Runs | Where-Object { $_.agent -eq $Agent -and $_.mode -eq $Mode })
    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Safe-Delta {
    param([AllowNull()][double]$Treatment, [AllowNull()][double]$Control)
    if ($null -eq $Treatment -or $null -eq $Control) {
        return $null
    }

    return [Math]::Round(($Treatment - $Control), 3)
}

function Safe-Ratio {
    param([AllowNull()][double]$Treatment, [AllowNull()][double]$Control)
    if ($null -eq $Treatment -or $null -eq $Control -or $Control -eq 0) {
        return $null
    }

    return [Math]::Round(($Treatment / $Control), 4)
}

function To-NumberOrNull {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $null
    }

    return [double]$Value
}

function To-BoolOrFalse {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) {
        return $false
    }

    return [bool]$Value
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outputDirectory = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$outputDirectory = (Resolve-Path $outputDirectory).Path

$pairedScriptPath = (Resolve-Path (Join-Path $repoRoot "benchmarks/scripts/Run-PairedAgentRuns.ps1")).Path
$supportedProfiles = @("standard", "brief-first", "surgical", "skill-minimal", "schema-first")

$profileList = New-Object System.Collections.Generic.List[string]
foreach ($profile in $Profiles) {
    $normalized = [string]$profile
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        continue
    }

    if ($supportedProfiles -notcontains $normalized) {
        throw "Unsupported profile '$normalized'. Supported: $([string]::Join(', ', $supportedProfiles))."
    }

    if (-not $profileList.Contains($normalized)) {
        $profileList.Add($normalized) | Out-Null
    }
}

if ($profileList.Count -eq 0) {
    throw "No valid profiles were provided."
}

$records = New-Object System.Collections.Generic.List[object]
$bundleRows = New-Object System.Collections.Generic.List[object]

foreach ($profile in $profileList) {
    $bundleDirectory = Join-Path $outputDirectory ("paired-{0}" -f $profile)
    New-Item -ItemType Directory -Force -Path $bundleDirectory | Out-Null

    $summaryPath = Join-Path $bundleDirectory "paired-run-summary.json"
    if ($ReuseExistingBundles -and (Test-Path $summaryPath -PathType Leaf)) {
        Write-Host ("REUSE_PROFILE={0}" -f $profile)
    }
    else {
        Write-Host ("RUN_PROFILE={0}" -f $profile)

        $runArgs = @(
            "-ExecutionPolicy", "Bypass",
            "-File", $pairedScriptPath,
            "-OutputRoot", $bundleDirectory,
            "-RoslynGuidanceProfile", $profile,
            "-CliPublishConfiguration", $CliPublishConfiguration
        )

        if ($IncludeMcpTreatment) {
            $runArgs += "-IncludeMcpTreatment"
        }
        if ($SkipCodex) {
            $runArgs += "-SkipCodex"
        }
        if ($SkipClaude) {
            $runArgs += "-SkipClaude"
        }
        if (-not [string]::IsNullOrWhiteSpace($CodexModel)) {
            $runArgs += @("-CodexModel", $CodexModel)
        }
        if (-not [string]::IsNullOrWhiteSpace($ClaudeModel)) {
            $runArgs += @("-ClaudeModel", $ClaudeModel)
        }
        & powershell.exe @runArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Run-PairedAgentRuns failed for profile '$profile'."
        }
    }

    if (-not (Test-Path $summaryPath -PathType Leaf)) {
        throw "Expected paired summary not found for profile '$profile': $summaryPath"
    }

    $runs = @((Get-Content -Path $summaryPath -Raw | ConvertFrom-Json))

    $agents = @($runs.agent | Select-Object -Unique)
    foreach ($agent in $agents) {
        $control = Get-RunByMode -Runs $runs -Agent $agent -Mode "control"
        $treatment = Get-RunByMode -Runs $runs -Agent $agent -Mode "treatment"
        $treatmentMcp = Get-RunByMode -Runs $runs -Agent $agent -Mode "treatment-mcp"

        $controlDuration = if ($null -eq $control) { $null } else { To-NumberOrNull $control.duration_seconds }
        $treatmentDuration = if ($null -eq $treatment) { $null } else { To-NumberOrNull $treatment.duration_seconds }
        $treatmentMcpDuration = if ($null -eq $treatmentMcp) { $null } else { To-NumberOrNull $treatmentMcp.duration_seconds }
        $controlTokens = if ($null -eq $control) { $null } else { To-NumberOrNull $control.total_tokens }
        $treatmentTokens = if ($null -eq $treatment) { $null } else { To-NumberOrNull $treatment.total_tokens }
        $treatmentMcpTokens = if ($null -eq $treatmentMcp) { $null } else { To-NumberOrNull $treatmentMcp.total_tokens }
        $controlRoundTrips = if ($null -eq $control) { $null } else { To-NumberOrNull $control.command_round_trips }
        $treatmentRoundTrips = if ($null -eq $treatment) { $null } else { To-NumberOrNull $treatment.command_round_trips }

        $record = [ordered]@{
            profile = $profile
            agent = $agent
            bundle_directory = (Resolve-Path $bundleDirectory).Path
            control_run_passed = if ($null -eq $control) { $null } else { To-BoolOrFalse $control.run_passed }
            treatment_run_passed = if ($null -eq $treatment) { $null } else { To-BoolOrFalse $treatment.run_passed }
            treatment_mcp_run_passed = if ($null -eq $treatmentMcp) { $null } else { To-BoolOrFalse $treatmentMcp.run_passed }
            control_roslyn_successful_calls = if ($null -eq $control) { $null } else { [int]$control.roslyn_successful_calls }
            treatment_roslyn_successful_calls = if ($null -eq $treatment) { $null } else { [int]$treatment.roslyn_successful_calls }
            treatment_mcp_roslyn_successful_calls = if ($null -eq $treatmentMcp) { $null } else { [int]$treatmentMcp.roslyn_successful_calls }
            control_duration_seconds = $controlDuration
            treatment_duration_seconds = $treatmentDuration
            treatment_mcp_duration_seconds = $treatmentMcpDuration
            treatment_vs_control_duration_delta = Safe-Delta -Treatment $treatmentDuration -Control $controlDuration
            treatment_vs_control_duration_ratio = Safe-Ratio -Treatment $treatmentDuration -Control $controlDuration
            control_total_tokens = $controlTokens
            treatment_total_tokens = $treatmentTokens
            treatment_mcp_total_tokens = $treatmentMcpTokens
            treatment_vs_control_token_delta = Safe-Delta -Treatment $treatmentTokens -Control $controlTokens
            treatment_vs_control_token_ratio = Safe-Ratio -Treatment $treatmentTokens -Control $controlTokens
            control_round_trips = $controlRoundTrips
            treatment_round_trips = $treatmentRoundTrips
            treatment_vs_control_round_trip_delta = Safe-Delta -Treatment $treatmentRoundTrips -Control $controlRoundTrips
        }

        $records.Add([pscustomobject]$record) | Out-Null
    }

    $bundleRows.Add([pscustomobject]@{
            profile = $profile
            summary_path = (Resolve-Path $summaryPath).Path
            run_count = $runs.Count
        }) | Out-Null
}

$recordsArray = @($records | Sort-Object agent, profile)

$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    output_root = $outputDirectory
    profiles = @($profileList)
    include_mcp_treatment = [bool]$IncludeMcpTreatment
    skip_codex = [bool]$SkipCodex
    skip_claude = [bool]$SkipClaude
    bundle_runs = @($bundleRows | Sort-Object profile)
    per_agent_profile = $recordsArray
}

$reportJsonPath = Join-Path $outputDirectory "skill-intro-ablation-report.json"
$report | ConvertTo-Json -Depth 40 | Set-Content -Path $reportJsonPath

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Skill Introduction Profile Ablation")
$lines.Add("")
$lines.Add("- Generated (UTC): $($report.generated_utc)")
$lines.Add("- Output root: $($report.output_root)")
$lines.Add("- Profiles: $([string]::Join(', ', $report.profiles))")
$lines.Add("- Include MCP treatment: $($report.include_mcp_treatment)")
$lines.Add("- Skip codex: $($report.skip_codex)")
$lines.Add("- Skip claude: $($report.skip_claude)")
$lines.Add("")
$lines.Add("## Bundle Runs")
$lines.Add("")
$lines.Add("| Profile | Runs | Summary |")
$lines.Add("| --- | ---: | --- |")
foreach ($bundle in ($report.bundle_runs | Sort-Object profile)) {
    $lines.Add("| $($bundle.profile) | $($bundle.run_count) | ``$($bundle.summary_path)`` |")
}
$lines.Add("")
$lines.Add("## Per-Agent Outcome")
$lines.Add("")
$lines.Add("| Agent | Profile | control pass | treatment pass | treatment roslyn calls | duration delta (s) | token delta | round-trip delta |")
$lines.Add("| --- | --- | --- | --- | ---: | ---: | ---: | ---: |")
foreach ($row in $recordsArray) {
    $lines.Add("| $($row.agent) | $($row.profile) | $($row.control_run_passed) | $($row.treatment_run_passed) | $($row.treatment_roslyn_successful_calls) | $($row.treatment_vs_control_duration_delta) | $($row.treatment_vs_control_token_delta) | $($row.treatment_vs_control_round_trip_delta) |")
}
$lines.Add("")
$lines.Add("## Interpretation Prompt")
$lines.Add("")
$lines.Add("Use this table to compare onboarding posture effects. Negative deltas indicate treatment improvement versus control for the same profile and agent.")

$reportMarkdownPath = Join-Path $outputDirectory "skill-intro-ablation-report.md"
Set-Content -Path $reportMarkdownPath -Value ($lines -join [Environment]::NewLine)

Write-Host ("REPORT_JSON={0}" -f (Resolve-Path $reportJsonPath).Path)
Write-Host ("REPORT_MARKDOWN={0}" -f (Resolve-Path $reportMarkdownPath).Path)
