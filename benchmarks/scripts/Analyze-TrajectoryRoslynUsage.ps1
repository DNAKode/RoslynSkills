param(
    [Parameter(Mandatory = $true)][string]$TrajectoriesRoot,
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

function Get-TrajectoriesRoot {
    param([Parameter(Mandatory = $true)][string]$InputPath)

    $resolved = (Resolve-Path $InputPath).Path
    $asBundle = Join-Path $resolved "trajectories"
    if (Test-Path $asBundle -PathType Container) {
        return (Resolve-Path $asBundle).Path
    }

    return $resolved
}

function Get-LaneAndTask {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedTrajectoriesRoot,
        [Parameter(Mandatory = $true)][string]$TranscriptPath
    )

    $relative = $TranscriptPath.Substring($ResolvedTrajectoriesRoot.Length).TrimStart('\')
    $segments = $relative -split "\\"
    $lane = if ($segments.Length -gt 0) { $segments[0] } else { "" }
    $task = if ($segments.Length -gt 1) { $segments[1] } else { "" }

    return [pscustomobject]@{
        lane = $lane
        task = $task
    }
}

function Test-IsRoscliCommandText {
    param([Parameter(Mandatory = $true)][string]$CommandText)

    if ([string]::IsNullOrWhiteSpace($CommandText)) {
        return $false
    }

    return ($CommandText -match "roscli(\.cmd)?\b" -or $CommandText -match "RoslynAgent\.Cli")
}

function Get-ExplicitBriefFlag {
    param([Parameter(Mandatory = $true)][string]$CommandText)

    return ($CommandText -match "(^|\s)--brief(\s+|=)(true|false)?(\s|$)")
}

function Add-RoscliResultRecord {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Records,
        [Parameter(Mandatory = $true)][string]$Lane,
        [Parameter(Mandatory = $true)][string]$Task,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$CommandText,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputText
    )

    if (-not (Test-IsRoscliCommandText -CommandText $CommandText)) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($OutputText)) {
        return
    }

    try {
        $parsed = $OutputText | ConvertFrom-Json
    } catch {
        return
    }

    if ($null -eq $parsed -or -not ($parsed.PSObject.Properties.Name -contains "CommandId")) {
        return
    }

    $briefField = $false
    $briefValue = $null
    if ($null -ne $parsed.Data -and ($parsed.Data.PSObject.Properties.Name -contains "query") -and $null -ne $parsed.Data.query -and ($parsed.Data.query.PSObject.Properties.Name -contains "brief")) {
        $briefField = $true
        $briefValue = [bool]$parsed.Data.query.brief
    }

    $sourceChars = $null
    if ($null -ne $parsed.Data -and ($parsed.Data.PSObject.Properties.Name -contains "source") -and $null -ne $parsed.Data.source -and ($parsed.Data.source.PSObject.Properties.Name -contains "character_count")) {
        $sourceChars = [int]$parsed.Data.source.character_count
    }

    $Records.Add([pscustomobject]@{
            lane = $Lane
            task = $Task
            source = $Source
            command_id = (Convert-ToText $parsed.CommandId)
            brief_field = $briefField
            brief_value = $briefValue
            explicit_brief_flag = (Get-ExplicitBriefFlag -CommandText $CommandText)
            output_chars = $OutputText.Length
            source_chars = $sourceChars
            command_text = $CommandText
        }) | Out-Null
}

function Get-TranscriptEvents {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json
            if ($null -ne $event) {
                $events.Add($event)
            }
        } catch {
            continue
        }
    }

    return $events.ToArray()
}

function Get-GroupSummary {
    param([Parameter(Mandatory = $true)][object[]]$Rows)

    $outputChars = @($Rows | Where-Object { $null -ne $_.output_chars } | ForEach-Object { [double]$_.output_chars })
    $sourceChars = @($Rows | Where-Object { $null -ne $_.source_chars } | ForEach-Object { [double]$_.source_chars })

    return [ordered]@{
        count = $Rows.Count
        with_brief_field = @($Rows | Where-Object { $_.brief_field }).Count
        brief_true = @($Rows | Where-Object { $_.brief_field -and $_.brief_value -eq $true }).Count
        brief_false = @($Rows | Where-Object { $_.brief_field -and $_.brief_value -eq $false }).Count
        explicit_brief_flag = @($Rows | Where-Object { $_.explicit_brief_flag }).Count
        avg_output_chars = if ($outputChars.Count -eq 0) { $null } else { [Math]::Round(($outputChars | Measure-Object -Average).Average, 2) }
        avg_source_chars = if ($sourceChars.Count -eq 0) { $null } else { [Math]::Round(($sourceChars | Measure-Object -Average).Average, 2) }
    }
}

$resolvedTrajectoriesRoot = Get-TrajectoriesRoot -InputPath $TrajectoriesRoot
$allRecords = New-Object System.Collections.Generic.List[object]

foreach ($transcript in (Get-ChildItem $resolvedTrajectoriesRoot -Recurse -Filter "transcript.jsonl")) {
    $meta = Get-LaneAndTask -ResolvedTrajectoriesRoot $resolvedTrajectoriesRoot -TranscriptPath $transcript.FullName
    $events = @(Get-TranscriptEvents -TranscriptPath $transcript.FullName)

    $claudeCommandsByToolUseId = @{}

    foreach ($event in $events) {
        if ($event.type -eq "item.completed" -and $null -ne $event.item -and $event.item.type -eq "command_execution") {
            Add-RoscliResultRecord `
                -Records $allRecords `
                -Lane $meta.lane `
                -Task $meta.task `
                -Source "codex" `
                -CommandText (Convert-ToText $event.item.command) `
                -OutputText (Convert-ToText $event.item.aggregated_output)
            continue
        }

        if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($content in $event.message.content) {
                if ($content.type -ne "tool_use" -or $content.name -ne "Bash") {
                    continue
                }

                $toolUseId = Convert-ToText $content.id
                $commandText = Convert-ToText $content.input.command
                if (-not [string]::IsNullOrWhiteSpace($toolUseId) -and -not [string]::IsNullOrWhiteSpace($commandText)) {
                    $claudeCommandsByToolUseId[$toolUseId] = $commandText
                }
            }
            continue
        }

        if ($event.type -eq "user" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($content in $event.message.content) {
                if ($content.type -ne "tool_result") {
                    continue
                }

                $toolUseId = Convert-ToText $content.tool_use_id
                if ([string]::IsNullOrWhiteSpace($toolUseId) -or -not $claudeCommandsByToolUseId.ContainsKey($toolUseId)) {
                    continue
                }

                Add-RoscliResultRecord `
                    -Records $allRecords `
                    -Lane $meta.lane `
                    -Task $meta.task `
                    -Source "claude" `
                    -CommandText (Convert-ToText $claudeCommandsByToolUseId[$toolUseId]) `
                    -OutputText (Convert-ToText $content.content)
            }
        }
    }
}

$records = @($allRecords | Sort-Object lane, task, source, command_id)
$overall = Get-GroupSummary -Rows $records

$byLane = @(
    $records |
    Group-Object lane |
    Sort-Object Name |
    ForEach-Object {
        [ordered]@{
            lane = $_.Name
            summary = Get-GroupSummary -Rows @($_.Group)
        }
    }
)

$byCommand = @(
    $records |
    Group-Object command_id |
    Sort-Object Name |
    ForEach-Object {
        [ordered]@{
            command_id = $_.Name
            summary = Get-GroupSummary -Rows @($_.Group)
        }
    }
)

$examples = @(
    $records |
    Where-Object { $_.brief_field } |
    Select-Object -First 10 lane, task, source, command_id, brief_value, source_chars, output_chars
)

$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    trajectories_root = $resolvedTrajectoriesRoot
    overall = $overall
    by_lane = $byLane
    by_command = $byCommand
    brief_examples = $examples
}

if ([string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $OutputJsonPath = Join-Path (Split-Path -Parent $resolvedTrajectoriesRoot) "trajectory-roslyn-analysis.json"
}
if (-not [System.IO.Path]::IsPathRooted($OutputJsonPath)) {
    $OutputJsonPath = Join-Path (Get-Location) $OutputJsonPath
}

$report | ConvertTo-Json -Depth 80 | Set-Content -Path $OutputJsonPath

if ([string]::IsNullOrWhiteSpace($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path (Split-Path -Parent $resolvedTrajectoriesRoot) "trajectory-roslyn-analysis.md"
}
if (-not [System.IO.Path]::IsPathRooted($OutputMarkdownPath)) {
    $OutputMarkdownPath = Join-Path (Get-Location) $OutputMarkdownPath
}

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Trajectory Roslyn Usage Analysis")
$markdown.Add("")
$markdown.Add("- Generated UTC: $($report.generated_utc)")
$markdown.Add("- Trajectories root: $($report.trajectories_root)")
$markdown.Add("")
$markdown.Add("## Overall")
$markdown.Add("")
$markdown.Add("| Metric | Value |")
$markdown.Add("|---|---:|")
$markdown.Add("| Parsed Roslyn results | $($report.overall.count) |")
$markdown.Add("| Results with brief field | $($report.overall.with_brief_field) |")
$markdown.Add("| brief=true | $($report.overall.brief_true) |")
$markdown.Add("| brief=false | $($report.overall.brief_false) |")
$markdown.Add("| Explicit --brief flags | $($report.overall.explicit_brief_flag) |")
$markdown.Add("| Avg output chars | $($report.overall.avg_output_chars) |")
$markdown.Add("| Avg source chars | $($report.overall.avg_source_chars) |")
$markdown.Add("")
$markdown.Add("## By Lane")
$markdown.Add("")
$markdown.Add("| Lane | Count | brief=true | brief=false | Explicit --brief | Avg output chars |")
$markdown.Add("|---|---:|---:|---:|---:|---:|")
foreach ($lane in $report.by_lane) {
    $markdown.Add("| $($lane.lane) | $($lane.summary.count) | $($lane.summary.brief_true) | $($lane.summary.brief_false) | $($lane.summary.explicit_brief_flag) | $($lane.summary.avg_output_chars) |")
}
$markdown.Add("")
$markdown.Add("## By Command")
$markdown.Add("")
$markdown.Add("| Command | Count | brief=true | brief=false | Avg output chars |")
$markdown.Add("|---|---:|---:|---:|---:|")
foreach ($command in $report.by_command) {
    $markdown.Add("| $($command.command_id) | $($command.summary.count) | $($command.summary.brief_true) | $($command.summary.brief_false) | $($command.summary.avg_output_chars) |")
}

$markdown | Set-Content -Path $OutputMarkdownPath

Write-Host ("TRAJECTORIES_ROOT={0}" -f $resolvedTrajectoriesRoot)
Write-Host ("REPORT_JSON={0}" -f (Resolve-Path $OutputJsonPath).Path)
Write-Host ("REPORT_MD={0}" -f (Resolve-Path $OutputMarkdownPath).Path)
