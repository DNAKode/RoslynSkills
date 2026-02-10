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
        [AllowNull()][object]$Object,
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

function Get-RoslynSuccessfulToolCallCount {
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

        $isRoslynTool = ($toolName -match "^(nav|ctx|diag|edit|repair|session)\." -or $toolName -eq "roslyn-agent.run")
        if (-not $isRoslynTool) {
            continue
        }

        $okRaw = Try-GetProperty -Object $call -PropertyName "ok"
        $ok = if ($null -eq $okRaw) { $false } else { [bool]$okRaw }
        if ($ok) {
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

function Get-Sum {
    param([double[]]$Values)

    $items = @($Values)
    if ($items.Count -eq 0) {
        return 0.0
    }

    $measure = $items | Measure-Object -Sum
    if ($null -eq $measure -or -not ($measure.PSObject.Properties.Name -contains "Sum") -or $null -eq $measure.Sum) {
        return 0.0
    }

    return [double]$measure.Sum
}

function Get-PropertySum {
    param(
        [object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    $items = @($Rows | Where-Object { $null -ne $_ -and $null -ne (Try-GetProperty -Object $_ -PropertyName $PropertyName) })
    if ($items.Count -eq 0) {
        return 0.0
    }

    $measure = $items | Measure-Object -Property $PropertyName -Sum
    if ($null -eq $measure -or -not ($measure.PSObject.Properties.Name -contains "Sum") -or $null -eq $measure.Sum) {
        return 0.0
    }

    return [double]$measure.Sum
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
    $roslynSuccessfulToolCalls = Get-RoslynSuccessfulToolCallCount -ToolCalls $toolCalls
    $postRunReflection = Try-GetProperty -Object $run -PropertyName "post_run_reflection"
    $roslynHelpfulnessRaw = Try-GetProperty -Object $postRunReflection -PropertyName "roslyn_helpfulness_score"
    $roslynHelpfulnessScore = if ($null -eq $roslynHelpfulnessRaw -or [string]::IsNullOrWhiteSpace([string]$roslynHelpfulnessRaw)) { $null } else { [double]$roslynHelpfulnessRaw }
    $roslynUsedRaw = Try-GetProperty -Object $run -PropertyName "roslyn_used"
    $roslynAttemptedCalls = Try-GetProperty -Object $run -PropertyName "roslyn_attempted_calls"
    $roslynSuccessfulCalls = Try-GetProperty -Object $run -PropertyName "roslyn_successful_calls"
    if ($null -eq $roslynSuccessfulCalls) {
        $roslynSuccessfulCalls = $roslynSuccessfulToolCalls
    }
    $roslynUsed = if ($null -eq $roslynUsedRaw) { $roslynToolCallCount -gt 0 } else { [bool]$roslynUsedRaw }

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
            roslyn_used = $roslynUsed
            roslyn_attempted_calls = if ($null -eq $roslynAttemptedCalls) { $null } else { [int]$roslynAttemptedCalls }
            roslyn_successful_calls = if ($null -eq $roslynSuccessfulCalls) { $null } else { [int]$roslynSuccessfulCalls }
            roslyn_helpfulness_score = $roslynHelpfulnessScore
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
        $helpfulnessScores = @($groupRows | Where-Object { $null -ne $_.roslyn_helpfulness_score } | ForEach-Object { [double]$_.roslyn_helpfulness_score })
        $roslynSuccessfulCalls = @($groupRows | Where-Object { $null -ne $_.roslyn_successful_calls } | ForEach-Object { [double]$_.roslyn_successful_calls })

        [ordered]@{
            agent = $groupRows[0].agent
            condition_id = $groupRows[0].condition_id
            run_count = $groupRows.Count
            success_count = @($groupRows | Where-Object { $_.succeeded }).Count
            roslyn_used_runs = @($groupRows | Where-Object { $_.roslyn_used }).Count
            elapsed_sum_s = [Math]::Round((Get-Sum -Values $durations), 4)
            elapsed_avg_s = Get-Mean -Values $durations
            elapsed_p50_s = Get-Percentile -Values $durations -Percentile 50
            elapsed_p95_s = Get-Percentile -Values $durations -Percentile 95
            tokens_sum = [Math]::Round((Get-Sum -Values $tokens), 2)
            tokens_avg = Get-Mean -Values $tokens
            round_trips_sum = [int](Get-PropertySum -Rows $groupRows -PropertyName "command_round_trips")
            roslyn_tool_calls_sum = [int](Get-PropertySum -Rows $groupRows -PropertyName "roslyn_tool_call_count")
            roslyn_successful_calls_sum = if ($roslynSuccessfulCalls.Count -eq 0) { 0 } else { [int](Get-Sum -Values $roslynSuccessfulCalls) }
            runs_with_helpfulness_score = $helpfulnessScores.Count
            avg_roslyn_helpfulness_score = Get-Mean -Values $helpfulnessScores
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

    $controlElapsed = [double](Get-PropertySum -Rows $controlRows -PropertyName "duration_seconds")
    $treatmentElapsed = [double](Get-PropertySum -Rows $treatmentRows -PropertyName "duration_seconds")
    $controlTokens = [double](Get-PropertySum -Rows $controlRows -PropertyName "total_tokens")
    $treatmentTokens = [double](Get-PropertySum -Rows $treatmentRows -PropertyName "total_tokens")
    $controlHelpfulness = @($controlRows | Where-Object { $null -ne $_.roslyn_helpfulness_score } | ForEach-Object { [double]$_.roslyn_helpfulness_score })
    $treatmentHelpfulness = @($treatmentRows | Where-Object { $null -ne $_.roslyn_helpfulness_score } | ForEach-Object { [double]$_.roslyn_helpfulness_score })
    $treatmentRoslynSuccess = @($treatmentRows | Where-Object { $null -ne $_.roslyn_successful_calls } | ForEach-Object { [double]$_.roslyn_successful_calls })

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
        treatment_roslyn_used_runs = @($treatmentRows | Where-Object { $_.roslyn_used }).Count
        treatment_roslyn_successful_calls_sum = if ($treatmentRoslynSuccess.Count -eq 0) { 0 } else { [int](Get-Sum -Values $treatmentRoslynSuccess) }
        control_helpfulness_avg = Get-Mean -Values $controlHelpfulness
        treatment_helpfulness_avg = Get-Mean -Values $treatmentHelpfulness
    }
}

$byAgentConditionPairDelta = @()
foreach ($agent in (@($recordArray | Select-Object -ExpandProperty agent -Unique | Sort-Object))) {
    $agentRows = @($recordArray | Where-Object { $_.agent -eq $agent })
    $conditionIds = @($agentRows | Select-Object -ExpandProperty condition_id -Unique | Sort-Object)
    if ($conditionIds.Count -lt 2) {
        continue
    }

    for ($i = 0; $i -lt $conditionIds.Count - 1; $i++) {
        for ($j = $i + 1; $j -lt $conditionIds.Count; $j++) {
            $baselineConditionId = [string]$conditionIds[$i]
            $compareConditionId = [string]$conditionIds[$j]

            $baselineRows = @($agentRows | Where-Object { $_.condition_id -eq $baselineConditionId })
            $compareRows = @($agentRows | Where-Object { $_.condition_id -eq $compareConditionId })
            if ($baselineRows.Count -eq 0 -or $compareRows.Count -eq 0) {
                continue
            }

            $baselineElapsed = [double](Get-PropertySum -Rows $baselineRows -PropertyName "duration_seconds")
            $compareElapsed = [double](Get-PropertySum -Rows $compareRows -PropertyName "duration_seconds")
            $baselineTokens = [double](Get-PropertySum -Rows $baselineRows -PropertyName "total_tokens")
            $compareTokens = [double](Get-PropertySum -Rows $compareRows -PropertyName "total_tokens")
            $baselineRoundTrips = [double](Get-PropertySum -Rows $baselineRows -PropertyName "command_round_trips")
            $compareRoundTrips = [double](Get-PropertySum -Rows $compareRows -PropertyName "command_round_trips")
            $baselineHelpfulness = @($baselineRows | Where-Object { $null -ne $_.roslyn_helpfulness_score } | ForEach-Object { [double]$_.roslyn_helpfulness_score })
            $compareHelpfulness = @($compareRows | Where-Object { $null -ne $_.roslyn_helpfulness_score } | ForEach-Object { [double]$_.roslyn_helpfulness_score })

            $byAgentConditionPairDelta += [ordered]@{
                agent = $agent
                baseline_condition = $baselineConditionId
                compare_condition = $compareConditionId
                baseline_elapsed_s = [Math]::Round($baselineElapsed, 4)
                compare_elapsed_s = [Math]::Round($compareElapsed, 4)
                elapsed_delta_s = [Math]::Round(($compareElapsed - $baselineElapsed), 4)
                elapsed_ratio = [Math]::Round((Safe-Divide -Numerator $compareElapsed -Denominator ([Math]::Max($baselineElapsed, 0.001))), 4)
                baseline_tokens = [Math]::Round($baselineTokens, 2)
                compare_tokens = [Math]::Round($compareTokens, 2)
                token_delta = [Math]::Round(($compareTokens - $baselineTokens), 2)
                token_ratio = [Math]::Round((Safe-Divide -Numerator $compareTokens -Denominator ([Math]::Max($baselineTokens, 1.0))), 4)
                baseline_round_trips = [int]$baselineRoundTrips
                compare_round_trips = [int]$compareRoundTrips
                round_trip_delta = [int]($compareRoundTrips - $baselineRoundTrips)
                baseline_helpfulness_avg = Get-Mean -Values $baselineHelpfulness
                compare_helpfulness_avg = Get-Mean -Values $compareHelpfulness
            }
        }
    }
}

$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    runs_directory = $resolvedRunsDirectory
    run_count = $recordArray.Count
    by_agent_condition = $byAgentCondition
    by_agent_control_treatment_delta = $byAgentControlTreatmentDelta
    by_agent_condition_pair_delta = $byAgentConditionPairDelta
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
$md.Add("| Agent | Condition | Runs | Success | Roslyn Used Runs | Roslyn Successful Calls | Helpfulness Avg | Elapsed Sum (s) | Tokens Sum | Round Trips Sum | Tokens/RoundTrip Avg | Tokens/1kOutputChars Avg |")
$md.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
foreach ($row in $report.by_agent_condition) {
    $md.Add("| $($row.agent) | $($row.condition_id) | $($row.run_count) | $($row.success_count) | $($row.roslyn_used_runs) | $($row.roslyn_successful_calls_sum) | $($row.avg_roslyn_helpfulness_score) | $($row.elapsed_sum_s) | $($row.tokens_sum) | $($row.round_trips_sum) | $($row.tokens_per_round_trip_avg) | $($row.tokens_per_1k_output_chars_avg) |")
}

$md.Add("")
$md.Add("## Control vs Treatment Delta")
$md.Add("")
$md.Add("| Agent | Control Elapsed (s) | Treatment Elapsed (s) | Elapsed Ratio | Control Tokens | Treatment Tokens | Token Ratio | Treatment Roslyn Used Runs | Treatment Roslyn Successful Calls | Treatment Helpfulness Avg |")
$md.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
foreach ($row in $report.by_agent_control_treatment_delta) {
    $md.Add("| $($row.agent) | $($row.control_elapsed_s) | $($row.treatment_elapsed_s) | $($row.elapsed_ratio) | $($row.control_tokens) | $($row.treatment_tokens) | $($row.token_ratio) | $($row.treatment_roslyn_used_runs) | $($row.treatment_roslyn_successful_calls_sum) | $($row.treatment_helpfulness_avg) |")
}

$md.Add("")
$md.Add("## Condition Pair Delta")
$md.Add("")
$md.Add("| Agent | Baseline Condition | Compare Condition | Baseline Elapsed (s) | Compare Elapsed (s) | Elapsed Ratio | Baseline Tokens | Compare Tokens | Token Ratio | Baseline Round Trips | Compare Round Trips | Round Trip Delta |")
$md.Add("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
foreach ($row in $report.by_agent_condition_pair_delta) {
    $md.Add("| $($row.agent) | $($row.baseline_condition) | $($row.compare_condition) | $($row.baseline_elapsed_s) | $($row.compare_elapsed_s) | $($row.elapsed_ratio) | $($row.baseline_tokens) | $($row.compare_tokens) | $($row.token_ratio) | $($row.baseline_round_trips) | $($row.compare_round_trips) | $($row.round_trip_delta) |")
}

$md | Set-Content -Path $OutputMarkdownPath

Write-Host ("RUNS_DIR={0}" -f $resolvedRunsDirectory)
Write-Host ("REPORT_JSON={0}" -f (Resolve-Path $OutputJsonPath).Path)
Write-Host ("REPORT_MD={0}" -f (Resolve-Path $OutputMarkdownPath).Path)
