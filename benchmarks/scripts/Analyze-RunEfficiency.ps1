param(
    [string]$BundleRoot = "",
    [string]$RunsDirectory = "",
    [string]$OutputJsonPath = "",
    [string]$OutputMarkdownPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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

function Resolve-RunsDirectory {
    param(
        [string]$BundleRoot,
        [string]$RunsDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($RunsDirectory)) {
        return (Resolve-Path $RunsDirectory).Path
    }

    if ([string]::IsNullOrWhiteSpace($BundleRoot)) {
        throw "Provide either -BundleRoot or -RunsDirectory."
    }

    $resolvedBundleRoot = (Resolve-Path $BundleRoot).Path
    $candidate = Join-Path $resolvedBundleRoot "runs"
    if (-not (Test-Path $candidate -PathType Container)) {
        throw "Bundle root '$resolvedBundleRoot' does not contain a 'runs' directory."
    }

    return (Resolve-Path $candidate).Path
}

function Try-GetProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    if ($null -eq $Object) {
        return $null
    }

    if ($Object.PSObject.Properties.Name -contains $PropertyName) {
        return $Object.$PropertyName
    }

    return $null
}

function Get-RoslynToolCallCount {
    param([object[]]$ToolCalls)

    if ($null -eq $ToolCalls) {
        return 0
    }

    $count = 0
    foreach ($call in $ToolCalls) {
        $toolName = Convert-ToText (Try-GetProperty -Object $call -PropertyName "tool_name")
        if ([string]::IsNullOrWhiteSpace($toolName)) {
            continue
        }

        if ($toolName -match "^(nav|ctx|diag|edit|repair|session)\." -or $toolName -eq "roslyn-agent.run") {
            $count++
        }
    }

    return $count
}

function Get-Mean {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    return [Math]::Round(($Values | Measure-Object -Average).Average, 4)
}

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $rank = [int][Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) {
        $rank = 0
    }
    if ($rank -ge $sorted.Count) {
        $rank = $sorted.Count - 1
    }

    return [Math]::Round([double]$sorted[$rank], 4)
}

function Safe-Divide {
    param(
        [double]$Numerator,
        [double]$Denominator
    )

    if ($Denominator -eq 0.0) {
        return $null
    }

    return ($Numerator / $Denominator)
}

$resolvedRunsDirectory = Resolve-RunsDirectory -BundleRoot $BundleRoot -RunsDirectory $RunsDirectory
$runFiles = @(Get-ChildItem -Path $resolvedRunsDirectory -Filter "*.json" -File)
if ($runFiles.Count -eq 0) {
    throw "No run JSON files found in '$resolvedRunsDirectory'."
}

$records = New-Object System.Collections.Generic.List[object]
foreach ($file in $runFiles) {
    $run = Get-Content -Path $file.FullName -Raw | ConvertFrom-Json

    $durationSeconds = [double](Try-GetProperty -Object $run -PropertyName "duration_seconds")
    $totalTokens = [double](Try-GetProperty -Object $run -PropertyName "total_tokens")
    $conditionId = Convert-ToText (Try-GetProperty -Object $run -PropertyName "condition_id")
    $agent = Convert-ToText (Try-GetProperty -Object $run -PropertyName "agent")
    $taskId = Convert-ToText (Try-GetProperty -Object $run -PropertyName "task_id")
    $succeeded = [bool](Try-GetProperty -Object $run -PropertyName "succeeded")
    $tokenAttribution = Try-GetProperty -Object $run -PropertyName "token_attribution"
    $roundTrips = Try-GetProperty -Object $tokenAttribution -PropertyName "command_round_trips"
    $commandOutputChars = Try-GetProperty -Object $tokenAttribution -PropertyName "command_output_chars"
    $toolCalls = @(Try-GetProperty -Object $run -PropertyName "tool_calls")
    $roslynToolCallCount = Get-RoslynToolCallCount -ToolCalls $toolCalls

    $tokensPerSecond = if ($durationSeconds -gt 0) { [Math]::Round((Safe-Divide -Numerator $totalTokens -Denominator $durationSeconds), 4) } else { $null }
    $tokensPerRoundTrip = if ($null -ne $roundTrips -and [double]$roundTrips -gt 0) { [Math]::Round((Safe-Divide -Numerator $totalTokens -Denominator ([double]$roundTrips)), 4) } else { $null }
    $tokensPer1kOutputChars = if ($null -ne $commandOutputChars -and [double]$commandOutputChars -gt 0) { [Math]::Round((Safe-Divide -Numerator $totalTokens -Denominator ([double]$commandOutputChars / 1000.0)), 4) } else { $null }

    $records.Add([pscustomobject]@{
            run_id = (Convert-ToText (Try-GetProperty -Object $run -PropertyName "run_id"))
            agent = $agent
            condition_id = $conditionId
            task_id = $taskId
            succeeded = $succeeded
            duration_seconds = $durationSeconds
            total_tokens = $totalTokens
            command_round_trips = if ($null -eq $roundTrips) { $null } else { [int]$roundTrips }
            command_output_chars = if ($null -eq $commandOutputChars) { $null } else { [double]$commandOutputChars }
            roslyn_tool_call_count = $roslynToolCallCount
            tokens_per_second = $tokensPerSecond
            tokens_per_round_trip = $tokensPerRoundTrip
            tokens_per_1k_output_chars = $tokensPer1kOutputChars
        }) | Out-Null
}

$recordArray = @($records | Sort-Object agent, condition_id, task_id, run_id)

$byAgentCondition = @(
    $recordArray |
    Group-Object agent, condition_id |
    Sort-Object Name |
    ForEach-Object {
        $groupRows = @($_.Group)
        $durations = @($groupRows | ForEach-Object { [double]$_.duration_seconds })
        $tokens = @($groupRows | ForEach-Object { [double]$_.total_tokens })
        $tokensPerRoundTrip = @($groupRows | Where-Object { $null -ne $_.tokens_per_round_trip } | ForEach-Object { [double]$_.tokens_per_round_trip })
        $tokensPer1kOutput = @($groupRows | Where-Object { $null -ne $_.tokens_per_1k_output_chars } | ForEach-Object { [double]$_.tokens_per_1k_output_chars })

        [ordered]@{
            agent = $groupRows[0].agent
            condition_id = $groupRows[0].condition_id
            run_count = $groupRows.Count
            success_count = @($groupRows | Where-Object { $_.succeeded }).Count
            elapsed_sum_s = [Math]::Round(($durations | Measure-Object -Sum).Sum, 4)
            elapsed_avg_s = Get-Mean -Values $durations
            elapsed_p50_s = Get-Percentile -Values $durations -Percentile 50
            elapsed_p95_s = Get-Percentile -Values $durations -Percentile 95
            tokens_sum = [Math]::Round(($tokens | Measure-Object -Sum).Sum, 2)
            tokens_avg = Get-Mean -Values $tokens
            round_trips_sum = [int](($groupRows | Where-Object { $null -ne $_.command_round_trips } | Measure-Object -Property command_round_trips -Sum).Sum)
            roslyn_tool_calls_sum = [int](($groupRows | Measure-Object -Property roslyn_tool_call_count -Sum).Sum)
            tokens_per_round_trip_avg = Get-Mean -Values $tokensPerRoundTrip
            tokens_per_1k_output_chars_avg = Get-Mean -Values $tokensPer1kOutput
        }
    }
)

$byAgentControlTreatmentDelta = @()
foreach ($agent in (@($recordArray | Select-Object -ExpandProperty agent -Unique | Sort-Object))) {
    $controlRows = @($recordArray | Where-Object { $_.agent -eq $agent -and $_.condition_id -eq "control-text-only" })
    $treatmentRows = @($recordArray | Where-Object { $_.agent -eq $agent -and $_.condition_id -eq "treatment-roslyn-optional" })
    if ($controlRows.Count -eq 0 -or $treatmentRows.Count -eq 0) {
        continue
    }

    $controlElapsed = [double](($controlRows | Measure-Object -Property duration_seconds -Sum).Sum)
    $treatmentElapsed = [double](($treatmentRows | Measure-Object -Property duration_seconds -Sum).Sum)
    $controlTokens = [double](($controlRows | Measure-Object -Property total_tokens -Sum).Sum)
    $treatmentTokens = [double](($treatmentRows | Measure-Object -Property total_tokens -Sum).Sum)

    $byAgentControlTreatmentDelta += [ordered]@{
        agent = $agent
        control_elapsed_s = [Math]::Round($controlElapsed, 4)
        treatment_elapsed_s = [Math]::Round($treatmentElapsed, 4)
        elapsed_delta_s = [Math]::Round(($treatmentElapsed - $controlElapsed), 4)
        elapsed_ratio = [Math]::Round((Safe-Divide -Numerator $treatmentElapsed -Denominator ([Math]::Max($controlElapsed, 0.001))), 4)
        control_tokens = [Math]::Round($controlTokens, 2)
        treatment_tokens = [Math]::Round($treatmentTokens, 2)
        token_delta = [Math]::Round(($treatmentTokens - $controlTokens), 2)
        token_ratio = [Math]::Round((Safe-Divide -Numerator $treatmentTokens -Denominator ([Math]::Max($controlTokens, 1.0))), 4)
    }
}

$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    runs_directory = $resolvedRunsDirectory
    run_count = $recordArray.Count
    by_agent_condition = $byAgentCondition
    by_agent_control_treatment_delta = $byAgentControlTreatmentDelta
    run_records = $recordArray
}

if ([string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $OutputJsonPath = Join-Path (Split-Path -Parent $resolvedRunsDirectory) "run-efficiency-analysis.json"
}
if (-not [System.IO.Path]::IsPathRooted($OutputJsonPath)) {
    $OutputJsonPath = Join-Path (Get-Location) $OutputJsonPath
}
$report | ConvertTo-Json -Depth 100 | Set-Content -Path $OutputJsonPath

if ([string]::IsNullOrWhiteSpace($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path (Split-Path -Parent $resolvedRunsDirectory) "run-efficiency-analysis.md"
}
if (-not [System.IO.Path]::IsPathRooted($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path (Get-Location) $OutputMarkdownPath
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Run Efficiency Analysis")
$md.Add("")
$md.Add("- Generated UTC: $($report.generated_utc)")
$md.Add("- Runs directory: $($report.runs_directory)")
$md.Add("- Runs parsed: $($report.run_count)")
$md.Add("")
$md.Add("## By Agent + Condition")
$md.Add("")
$md.Add("| Agent | Condition | Runs | Success | Elapsed Sum (s) | Tokens Sum | Round Trips Sum | Tokens/RoundTrip Avg | Tokens/1kOutputChars Avg |")
$md.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|")
foreach ($row in $report.by_agent_condition) {
    $md.Add("| $($row.agent) | $($row.condition_id) | $($row.run_count) | $($row.success_count) | $($row.elapsed_sum_s) | $($row.tokens_sum) | $($row.round_trips_sum) | $($row.tokens_per_round_trip_avg) | $($row.tokens_per_1k_output_chars_avg) |")
}

$md.Add("")
$md.Add("## Control vs Treatment Delta")
$md.Add("")
$md.Add("| Agent | Control Elapsed (s) | Treatment Elapsed (s) | Elapsed Ratio | Control Tokens | Treatment Tokens | Token Ratio |")
$md.Add("|---|---:|---:|---:|---:|---:|---:|")
foreach ($row in $report.by_agent_control_treatment_delta) {
    $md.Add("| $($row.agent) | $($row.control_elapsed_s) | $($row.treatment_elapsed_s) | $($row.elapsed_ratio) | $($row.control_tokens) | $($row.treatment_tokens) | $($row.token_ratio) |")
}

$md | Set-Content -Path $OutputMarkdownPath

Write-Host ("RUNS_DIR={0}" -f $resolvedRunsDirectory)
Write-Host ("REPORT_JSON={0}" -f (Resolve-Path $OutputJsonPath).Path)
Write-Host ("REPORT_MD={0}" -f (Resolve-Path $OutputMarkdownPath).Path)
