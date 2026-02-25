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

    return (
        $CommandText -match "roscli(\.cmd)?\b" -or
        $CommandText -match "RoslynSkills\.Cli" -or
        $CommandText -match "roslyn-list-commands\.ps1" -or
        $CommandText -match "roslyn-find-symbol\.ps1" -or
        $CommandText -match "roslyn-rename-symbol\.ps1" -or
        $CommandText -match "roslyn-rename-and-verify\.ps1"
    )
}

function Test-IsLlmstxtCommandText {
    param([Parameter(Mandatory = $true)][string]$CommandText)

    if ([string]::IsNullOrWhiteSpace($CommandText)) {
        return $false
    }

    return (
        $CommandText -match "roscli(\.cmd)?\b.*\bllmstxt\b" -or
        $CommandText -match "RoslynSkills\.Cli.*\bllmstxt\b"
    )
}

function Get-ExplicitBriefFlag {
    param([Parameter(Mandatory = $true)][string]$CommandText)

    return ($CommandText -match "(^|\s)--brief(\s+|=)(true|false)?(\s|$)")
}

function Get-CommandFamily {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$CommandId)

    if ([string]::IsNullOrWhiteSpace($CommandId)) {
        return "unknown"
    }

    if ($CommandId.StartsWith("roslyn.", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "helper"
    }

    $separator = $CommandId.IndexOf(".")
    if ($separator -le 0) {
        return "other"
    }

    return $CommandId.Substring(0, $separator).ToLowerInvariant()
}

function Test-IsDiscoveryFamily {
    param([Parameter(Mandatory = $true)][string]$Family)

    return ($Family -in @("cli", "nav", "ctx", "diag"))
}

function Test-IsEditLikeFamily {
    param([Parameter(Mandatory = $true)][string]$Family)

    return ($Family -in @("edit", "session", "helper"))
}

function Test-IsCatalogCommand {
    param([Parameter(Mandatory = $true)][string]$CommandId)

    return ($CommandId -eq "cli.list_commands")
}

function Test-RunHasNonEmptyDiff {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $runDirectory = Split-Path -Parent $TranscriptPath
    $diffPath = Join-Path $runDirectory "diff.patch"
    if (-not (Test-Path -LiteralPath $diffPath -PathType Leaf)) {
        return $false
    }

    try {
        $rawDiff = Get-Content -LiteralPath $diffPath -Raw
        return (-not [string]::IsNullOrWhiteSpace($rawDiff))
    } catch {
        return $false
    }
}

function Try-ParseRoscliEnvelope {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputText)

    if ([string]::IsNullOrWhiteSpace($OutputText)) {
        return $null
    }

    try {
        $direct = $OutputText | ConvertFrom-Json
        if ($null -ne $direct) {
            return [pscustomobject]@{
                parsed = $direct
                parse_mode = "direct"
            }
        }
    } catch {
    }

    $jsonStart = $OutputText.IndexOf("{")
    $jsonEnd = $OutputText.LastIndexOf("}")
    if ($jsonStart -lt 0 -or $jsonEnd -le $jsonStart) {
        return $null
    }

    $candidate = $OutputText.Substring($jsonStart, ($jsonEnd - $jsonStart + 1))
    try {
        $extracted = $candidate | ConvertFrom-Json
        if ($null -ne $extracted) {
            return [pscustomobject]@{
                parsed = $extracted
                parse_mode = "extracted"
            }
        }
    } catch {
    }

    return $null
}

function Get-CommandInputPayload {
    param(
        [Parameter(Mandatory = $true)][string]$CommandText,
        [AllowEmptyString()][string]$WorkspaceDirectory
    )

    if ([string]::IsNullOrWhiteSpace($CommandText)) {
        return $null
    }

    $match = [regex]::Match($CommandText, "--input\s+@(?:""([^""]+)""|'([^']+)'|([^\s]+))", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return $null
    }

    $rawPath = ""
    for ($groupIndex = 1; $groupIndex -le 3; $groupIndex++) {
        if ($match.Groups[$groupIndex].Success) {
            $rawPath = Convert-ToText $match.Groups[$groupIndex].Value
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($rawPath)) {
        return $null
    }

    $candidatePath = $rawPath.Trim()
    if ($candidatePath.StartsWith("{") -or $candidatePath.StartsWith("[")) {
        return $null
    }

    $isPathRooted = $false
    try {
        $isPathRooted = [System.IO.Path]::IsPathRooted($candidatePath)
    } catch {
        return $null
    }

    if (-not $isPathRooted) {
        if ([string]::IsNullOrWhiteSpace($WorkspaceDirectory)) {
            return $null
        }

        $candidatePath = Join-Path $WorkspaceDirectory $candidatePath
    }

    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
        return $null
    }

    try {
        return (Get-Content -LiteralPath $candidatePath -Raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Get-InputBriefValue {
    param([AllowNull()][object]$InputPayload)

    if ($null -eq $InputPayload) {
        return [pscustomobject]@{
            has_brief = $false
            brief = $null
        }
    }

    if ($InputPayload.PSObject.Properties.Name -contains "brief") {
        return [pscustomobject]@{
            has_brief = $true
            brief = [bool]$InputPayload.brief
        }
    }

    if (($InputPayload.PSObject.Properties.Name -contains "query") -and $null -ne $InputPayload.query -and ($InputPayload.query.PSObject.Properties.Name -contains "brief")) {
        return [pscustomobject]@{
            has_brief = $true
            brief = [bool]$InputPayload.query.brief
        }
    }

    return [pscustomobject]@{
        has_brief = $false
        brief = $null
    }
}

function Add-RoscliResultRecord {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Records,
        [Parameter(Mandatory = $true)][string]$Lane,
        [Parameter(Mandatory = $true)][string]$Task,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
        [AllowEmptyString()][string]$WorkspaceDirectory,
        [Parameter(Mandatory = $true)][int]$EventIndex,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$CommandText,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputText
    )

    if (-not (Test-IsRoscliCommandText -CommandText $CommandText)) {
        return
    }

    $isLlmstxt = Test-IsLlmstxtCommandText -CommandText $CommandText

    if ($isLlmstxt) {
        $Records.Add([pscustomobject]@{
                lane = $Lane
                task = $Task
                source = $Source
                transcript_path = $TranscriptPath
                event_index = $EventIndex
                command_id = "cli.llmstxt"
                command_family = "cli"
                is_discovery_call = $true
                is_edit_like_call = $false
                is_catalog_call = $false
                is_llmstxt_call = $true
                brief_field = $false
                brief_value = $null
                input_brief_field = $false
                input_brief_value = $null
                effective_brief_field = $false
                effective_brief_value = $null
                explicit_brief_flag = $false
                output_chars = if ([string]::IsNullOrWhiteSpace($OutputText)) { 0 } else { $OutputText.Length }
                source_chars = $null
                command_text = $CommandText
                parse_mode = "llmstxt_text"
            }) | Out-Null
        return
    }

    if ([string]::IsNullOrWhiteSpace($OutputText)) {
        return
    }

    $parsedEnvelope = Try-ParseRoscliEnvelope -OutputText $OutputText
    if ($null -eq $parsedEnvelope) {
        return
    }

    $parsed = $parsedEnvelope.parsed
    if ($null -eq $parsed) {
        return
    }

    $commandId = ""
    if ($parsed.PSObject.Properties.Name -contains "CommandId" -and $null -ne $parsed.CommandId) {
        $commandId = Convert-ToText $parsed.CommandId
    } elseif ($parsed.PSObject.Properties.Name -contains "command" -and $null -ne $parsed.command) {
        $helperCommand = Convert-ToText $parsed.command
        if ($helperCommand.StartsWith("roslyn.", [System.StringComparison]::OrdinalIgnoreCase)) {
            $commandId = $helperCommand
        }
    }

    if ([string]::IsNullOrWhiteSpace($commandId)) {
        return
    }

    $commandFamily = Get-CommandFamily -CommandId $commandId
    $parsedData = $null
    if ($parsed.PSObject.Properties.Name -contains "Data") {
        $parsedData = $parsed.Data
    }

    $briefField = $false
    $briefValue = $null
    if ($null -ne $parsedData -and ($parsedData.PSObject.Properties.Name -contains "query") -and $null -ne $parsedData.query -and ($parsedData.query.PSObject.Properties.Name -contains "brief")) {
        $briefField = $true
        $briefValue = [bool]$parsedData.query.brief
    }

    $sourceChars = $null
    if ($null -ne $parsedData -and ($parsedData.PSObject.Properties.Name -contains "source") -and $null -ne $parsedData.source -and ($parsedData.source.PSObject.Properties.Name -contains "character_count")) {
        $sourceChars = [int]$parsedData.source.character_count
    }

    $inputPayload = Get-CommandInputPayload -CommandText $CommandText -WorkspaceDirectory $WorkspaceDirectory
    $inputBriefInfo = Get-InputBriefValue -InputPayload $inputPayload
    $inputBriefField = [bool]$inputBriefInfo.has_brief
    $inputBriefValue = $inputBriefInfo.brief

    $effectiveBriefField = ($briefField -or $inputBriefField)
    $effectiveBriefValue = if ($briefField) { $briefValue } elseif ($inputBriefField) { $inputBriefValue } else { $null }

    $Records.Add([pscustomobject]@{
            lane = $Lane
            task = $Task
            source = $Source
            transcript_path = $TranscriptPath
            event_index = $EventIndex
            command_id = $commandId
            command_family = $commandFamily
            is_discovery_call = (Test-IsDiscoveryFamily -Family $commandFamily)
            is_edit_like_call = (Test-IsEditLikeFamily -Family $commandFamily)
            is_catalog_call = (Test-IsCatalogCommand -CommandId $commandId)
            is_llmstxt_call = ($commandId -eq "cli.llmstxt")
            brief_field = $briefField
            brief_value = $briefValue
            input_brief_field = $inputBriefField
            input_brief_value = $inputBriefValue
            effective_brief_field = $effectiveBriefField
            effective_brief_value = $effectiveBriefValue
            explicit_brief_flag = (Get-ExplicitBriefFlag -CommandText $CommandText)
            output_chars = $OutputText.Length
            source_chars = $sourceChars
            command_text = $CommandText
            parse_mode = [string]$parsedEnvelope.parse_mode
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

function Get-NullableAverage {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    return [Math]::Round(($Values | Measure-Object -Average).Average, 3)
}

function Get-TranscriptTimelineRows {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Rows)

    $rowsArray = @($Rows)
    $rowCount = ($rowsArray | Measure-Object).Count
    if ($rowCount -eq 0) {
        return @()
    }

    $timeline = New-Object System.Collections.Generic.List[object]
    $byTranscript = @($rowsArray | Group-Object transcript_path)
    foreach ($group in $byTranscript) {
        $ordered = @($group.Group | Sort-Object event_index)
        $orderedCount = ($ordered | Measure-Object).Count
        if ($orderedCount -eq 0) {
            continue
        }

        $first = $ordered[0]
        $firstEdit = @($ordered | Where-Object { $_.is_edit_like_call } | Select-Object -First 1)
        $firstEditIndex = if ($firstEdit.Count -eq 0) { $null } else { [int]$firstEdit[0].event_index }
        $firstLlmstxt = @($ordered | Where-Object { $_.is_llmstxt_call } | Select-Object -First 1)
        $firstLlmstxtIndex = if ($firstLlmstxt.Count -eq 0) { $null } else { [int]$firstLlmstxt[0].event_index }
        $runHasDiff = Test-RunHasNonEmptyDiff -TranscriptPath ([string]$group.Name)

        $rowsBeforeEdit = if ($firstEdit.Count -eq 0) {
            @($ordered)
        } else {
            @($ordered | Where-Object { [int]$_.event_index -lt $firstEditIndex })
        }

        $llmstxtTotal = @($ordered | Where-Object { $_.is_llmstxt_call }).Count
        $llmstxtBeforeFirstEdit = @($rowsBeforeEdit | Where-Object { $_.is_llmstxt_call }).Count
        $mutationChannel = if ($null -ne $firstEditIndex) {
            "roslyn_semantic_edit"
        } elseif ($runHasDiff) {
            "text_patch_or_non_roslyn_edit"
        } else {
            "no_mutation_observed"
        }

        $timeline.Add([pscustomobject]@{
                transcript_path = [string]$group.Name
                lane = [string]$first.lane
                task = [string]$first.task
                source = [string]$first.source
                first_roslyn_event_index = [int]$first.event_index
                first_llmstxt_event_index = $firstLlmstxtIndex
                first_edit_event_index = $firstEditIndex
                has_edit_call = ($null -ne $firstEditIndex)
                roslyn_calls_total = (($ordered | Measure-Object).Count)
                roslyn_calls_before_first_edit = (($rowsBeforeEdit | Measure-Object).Count)
                discovery_calls_before_first_edit = @($rowsBeforeEdit | Where-Object { $_.is_discovery_call }).Count
                list_commands_before_first_edit = @($rowsBeforeEdit | Where-Object { $_.is_catalog_call }).Count
                llmstxt_calls_total = $llmstxtTotal
                llmstxt_calls_before_first_edit = $llmstxtBeforeFirstEdit
                has_llmstxt_call = ($llmstxtTotal -gt 0)
                has_llmstxt_before_first_edit = ($llmstxtBeforeFirstEdit -gt 0)
                run_has_diff = $runHasDiff
                mutation_channel = $mutationChannel
            }) | Out-Null
    }

    return @($timeline.ToArray())
}

function Get-TimelineSummary {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Rows)

    $rowsArray = @($Rows)
    $timelineRows = @(Get-TranscriptTimelineRows -Rows $rowsArray)
    $timelineCount = ($timelineRows | Measure-Object).Count
    if ($timelineCount -eq 0) {
        return [ordered]@{
            transcript_count = 0
            transcripts_with_edit_calls = 0
            transcripts_with_llmstxt_calls = 0
            transcripts_with_llmstxt_before_first_edit = 0
            avg_roslyn_calls_per_transcript = $null
            avg_roslyn_calls_before_first_edit = $null
            avg_discovery_calls_before_first_edit = $null
            avg_list_commands_before_first_edit = $null
            avg_llmstxt_calls_per_transcript = $null
            avg_llmstxt_calls_before_first_edit = $null
            mutation_channel_roslyn_semantic_edit = 0
            mutation_channel_text_patch_or_non_roslyn_edit = 0
            mutation_channel_no_mutation_observed = 0
        }
    }

    $withEdit = @($timelineRows | Where-Object { $_.has_edit_call })
    $withEditCount = ($withEdit | Measure-Object).Count
    $rowsForBeforeEdit = if ($withEditCount -gt 0) { $withEdit } else { $timelineRows }

    return [ordered]@{
        transcript_count = $timelineCount
        transcripts_with_edit_calls = $withEditCount
        transcripts_with_llmstxt_calls = @($timelineRows | Where-Object { $_.has_llmstxt_call }).Count
        transcripts_with_llmstxt_before_first_edit = @($timelineRows | Where-Object { $_.has_llmstxt_before_first_edit }).Count
        avg_roslyn_calls_per_transcript = Get-NullableAverage -Values @($timelineRows | ForEach-Object { [double]$_.roslyn_calls_total })
        avg_roslyn_calls_before_first_edit = Get-NullableAverage -Values @($rowsForBeforeEdit | ForEach-Object { [double]$_.roslyn_calls_before_first_edit })
        avg_discovery_calls_before_first_edit = Get-NullableAverage -Values @($rowsForBeforeEdit | ForEach-Object { [double]$_.discovery_calls_before_first_edit })
        avg_list_commands_before_first_edit = Get-NullableAverage -Values @($rowsForBeforeEdit | ForEach-Object { [double]$_.list_commands_before_first_edit })
        avg_llmstxt_calls_per_transcript = Get-NullableAverage -Values @($timelineRows | ForEach-Object { [double]$_.llmstxt_calls_total })
        avg_llmstxt_calls_before_first_edit = Get-NullableAverage -Values @($rowsForBeforeEdit | ForEach-Object { [double]$_.llmstxt_calls_before_first_edit })
        mutation_channel_roslyn_semantic_edit = @($timelineRows | Where-Object { $_.mutation_channel -eq "roslyn_semantic_edit" }).Count
        mutation_channel_text_patch_or_non_roslyn_edit = @($timelineRows | Where-Object { $_.mutation_channel -eq "text_patch_or_non_roslyn_edit" }).Count
        mutation_channel_no_mutation_observed = @($timelineRows | Where-Object { $_.mutation_channel -eq "no_mutation_observed" }).Count
    }
}

function Get-GroupSummary {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Rows)

    $rowsArray = @($Rows)
    $outputChars = @($rowsArray | Where-Object { $null -ne $_.output_chars } | ForEach-Object { [double]$_.output_chars })
    $sourceChars = @($rowsArray | Where-Object { $null -ne $_.source_chars } | ForEach-Object { [double]$_.source_chars })
    $discoveryCalls = @($rowsArray | Where-Object { $_.is_discovery_call }).Count
    $editLikeCalls = @($rowsArray | Where-Object { $_.is_edit_like_call }).Count
    $helperCalls = @($rowsArray | Where-Object { $_.command_family -eq "helper" }).Count
    $listCommandsCalls = @($rowsArray | Where-Object { $_.is_catalog_call }).Count
    $parsedDirect = @($rowsArray | Where-Object { $_.parse_mode -eq "direct" }).Count
    $parsedExtracted = @($rowsArray | Where-Object { $_.parse_mode -eq "extracted" }).Count
    $timeline = Get-TimelineSummary -Rows $rowsArray

    return [ordered]@{
        count = (($rowsArray | Measure-Object).Count)
        with_brief_field = @($rowsArray | Where-Object { $_.brief_field }).Count
        brief_true = @($rowsArray | Where-Object { $_.brief_field -and $_.brief_value -eq $true }).Count
        brief_false = @($rowsArray | Where-Object { $_.brief_field -and $_.brief_value -eq $false }).Count
        input_brief_field = @($rowsArray | Where-Object { $_.input_brief_field }).Count
        input_brief_true = @($rowsArray | Where-Object { $_.input_brief_field -and $_.input_brief_value -eq $true }).Count
        input_brief_false = @($rowsArray | Where-Object { $_.input_brief_field -and $_.input_brief_value -eq $false }).Count
        effective_brief_field = @($rowsArray | Where-Object { $_.effective_brief_field }).Count
        effective_brief_true = @($rowsArray | Where-Object { $_.effective_brief_field -and $_.effective_brief_value -eq $true }).Count
        effective_brief_false = @($rowsArray | Where-Object { $_.effective_brief_field -and $_.effective_brief_value -eq $false }).Count
        explicit_brief_flag = @($rowsArray | Where-Object { $_.explicit_brief_flag }).Count
        parsed_direct = $parsedDirect
        parsed_extracted = $parsedExtracted
        avg_output_chars = if ($outputChars.Count -eq 0) { $null } else { [Math]::Round(($outputChars | Measure-Object -Average).Average, 2) }
        avg_source_chars = if ($sourceChars.Count -eq 0) { $null } else { [Math]::Round(($sourceChars | Measure-Object -Average).Average, 2) }
        discovery_calls = $discoveryCalls
        edit_like_calls = $editLikeCalls
        helper_calls = $helperCalls
        list_commands_calls = $listCommandsCalls
        discovery_to_edit_ratio = if ($editLikeCalls -eq 0) { $null } else { [Math]::Round(([double]$discoveryCalls / [double]$editLikeCalls), 3) }
        transcript_count = $timeline.transcript_count
        transcripts_with_edit_calls = $timeline.transcripts_with_edit_calls
        transcripts_with_llmstxt_calls = $timeline.transcripts_with_llmstxt_calls
        transcripts_with_llmstxt_before_first_edit = $timeline.transcripts_with_llmstxt_before_first_edit
        avg_roslyn_calls_per_transcript = $timeline.avg_roslyn_calls_per_transcript
        avg_roslyn_calls_before_first_edit = $timeline.avg_roslyn_calls_before_first_edit
        avg_discovery_calls_before_first_edit = $timeline.avg_discovery_calls_before_first_edit
        avg_list_commands_before_first_edit = $timeline.avg_list_commands_before_first_edit
        avg_llmstxt_calls_per_transcript = $timeline.avg_llmstxt_calls_per_transcript
        avg_llmstxt_calls_before_first_edit = $timeline.avg_llmstxt_calls_before_first_edit
        mutation_channel_roslyn_semantic_edit = $timeline.mutation_channel_roslyn_semantic_edit
        mutation_channel_text_patch_or_non_roslyn_edit = $timeline.mutation_channel_text_patch_or_non_roslyn_edit
        mutation_channel_no_mutation_observed = $timeline.mutation_channel_no_mutation_observed
    }
}

$resolvedTrajectoriesRoot = Get-TrajectoriesRoot -InputPath $TrajectoriesRoot
$allRecords = New-Object System.Collections.Generic.List[object]

foreach ($transcript in (Get-ChildItem $resolvedTrajectoriesRoot -Recurse -Filter "transcript.jsonl")) {
    $meta = Get-LaneAndTask -ResolvedTrajectoriesRoot $resolvedTrajectoriesRoot -TranscriptPath $transcript.FullName
    $workspaceDirectory = Join-Path (Join-Path $resolvedTrajectoriesRoot $meta.lane) "workspace"
    $events = @(Get-TranscriptEvents -TranscriptPath $transcript.FullName)

    $claudeCommandsByToolUseId = @{}

    for ($eventIndex = 0; $eventIndex -lt $events.Count; $eventIndex++) {
        $event = $events[$eventIndex]
        if ($event.type -eq "item.completed" -and $null -ne $event.item -and $event.item.type -eq "command_execution") {
            Add-RoscliResultRecord `
                -Records $allRecords `
                -Lane $meta.lane `
                -Task $meta.task `
                -Source "codex" `
                -TranscriptPath $transcript.FullName `
                -WorkspaceDirectory $workspaceDirectory `
                -EventIndex $eventIndex `
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
                    -TranscriptPath $transcript.FullName `
                    -WorkspaceDirectory $workspaceDirectory `
                    -EventIndex $eventIndex `
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

$bySource = @(
    $records |
    Group-Object source |
    Sort-Object Name |
    ForEach-Object {
        [ordered]@{
            source = $_.Name
            summary = Get-GroupSummary -Rows @($_.Group)
        }
    }
)

$timelineRows = @(Get-TranscriptTimelineRows -Rows $records)
$timelineExamples = @(
    $timelineRows |
    Sort-Object -Property @{ Expression = "discovery_calls_before_first_edit"; Descending = $true }, @{ Expression = "roslyn_calls_total"; Descending = $true } |
    Select-Object -First 10 lane, task, source, roslyn_calls_total, roslyn_calls_before_first_edit, discovery_calls_before_first_edit, list_commands_before_first_edit, llmstxt_calls_before_first_edit, first_edit_event_index, mutation_channel
)

$examples = @(
    $records |
    Where-Object { $_.effective_brief_field } |
    Select-Object -First 10 lane, task, source, command_id, effective_brief_value, source_chars, output_chars
)

$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    trajectories_root = $resolvedTrajectoriesRoot
    overall = $overall
    by_lane = $byLane
    by_source = $bySource
    by_command = $byCommand
    timeline_examples = $timelineExamples
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
$markdown.Add("| Results with response brief field | $($report.overall.with_brief_field) |")
$markdown.Add("| Response brief=true | $($report.overall.brief_true) |")
$markdown.Add("| Response brief=false | $($report.overall.brief_false) |")
$markdown.Add("| Results with input brief field | $($report.overall.input_brief_field) |")
$markdown.Add("| Input brief=true | $($report.overall.input_brief_true) |")
$markdown.Add("| Input brief=false | $($report.overall.input_brief_false) |")
$markdown.Add("| Results with effective brief value | $($report.overall.effective_brief_field) |")
$markdown.Add("| Effective brief=true | $($report.overall.effective_brief_true) |")
$markdown.Add("| Effective brief=false | $($report.overall.effective_brief_false) |")
$markdown.Add("| Explicit --brief flags | $($report.overall.explicit_brief_flag) |")
$markdown.Add("| Parsed via direct JSON | $($report.overall.parsed_direct) |")
$markdown.Add("| Parsed via extracted JSON envelope | $($report.overall.parsed_extracted) |")
$markdown.Add("| Discovery calls | $($report.overall.discovery_calls) |")
$markdown.Add("| Edit-like calls | $($report.overall.edit_like_calls) |")
$markdown.Add("| Helper calls | $($report.overall.helper_calls) |")
$markdown.Add("| list-commands calls | $($report.overall.list_commands_calls) |")
$markdown.Add("| Transcripts with llmstxt calls | $($report.overall.transcripts_with_llmstxt_calls) |")
$markdown.Add("| Transcripts with llmstxt before first edit | $($report.overall.transcripts_with_llmstxt_before_first_edit) |")
$markdown.Add("| Discovery/Edit ratio | $($report.overall.discovery_to_edit_ratio) |")
$markdown.Add("| Transcript count | $($report.overall.transcript_count) |")
$markdown.Add("| Transcripts with edit calls | $($report.overall.transcripts_with_edit_calls) |")
$markdown.Add("| Avg Roslyn calls/transcript | $($report.overall.avg_roslyn_calls_per_transcript) |")
$markdown.Add("| Avg Roslyn calls before first edit | $($report.overall.avg_roslyn_calls_before_first_edit) |")
$markdown.Add("| Avg discovery calls before first edit | $($report.overall.avg_discovery_calls_before_first_edit) |")
$markdown.Add("| Avg list-commands before first edit | $($report.overall.avg_list_commands_before_first_edit) |")
$markdown.Add("| Avg llmstxt calls/transcript | $($report.overall.avg_llmstxt_calls_per_transcript) |")
$markdown.Add("| Avg llmstxt calls before first edit | $($report.overall.avg_llmstxt_calls_before_first_edit) |")
$markdown.Add("| Mutation channel: roslyn_semantic_edit | $($report.overall.mutation_channel_roslyn_semantic_edit) |")
$markdown.Add("| Mutation channel: text_patch_or_non_roslyn_edit | $($report.overall.mutation_channel_text_patch_or_non_roslyn_edit) |")
$markdown.Add("| Mutation channel: no_mutation_observed | $($report.overall.mutation_channel_no_mutation_observed) |")
$markdown.Add("| Avg output chars | $($report.overall.avg_output_chars) |")
$markdown.Add("| Avg source chars | $($report.overall.avg_source_chars) |")
$markdown.Add("")
$markdown.Add("## By Lane")
$markdown.Add("")
$markdown.Add("| Lane | Count | Discovery Calls | Edit-like Calls | list-commands | llmstxt tx | Discovery/Edit | Avg discovery before first edit | Avg llmstxt before first edit | Avg output chars |")
$markdown.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
foreach ($lane in $report.by_lane) {
    $markdown.Add("| $($lane.lane) | $($lane.summary.count) | $($lane.summary.discovery_calls) | $($lane.summary.edit_like_calls) | $($lane.summary.list_commands_calls) | $($lane.summary.transcripts_with_llmstxt_calls) | $($lane.summary.discovery_to_edit_ratio) | $($lane.summary.avg_discovery_calls_before_first_edit) | $($lane.summary.avg_llmstxt_calls_before_first_edit) | $($lane.summary.avg_output_chars) |")
}
$markdown.Add("")
$markdown.Add("## By Source")
$markdown.Add("")
$markdown.Add("| Source | Count | Effective brief=true | Discovery Calls | Edit-like Calls | list-commands | llmstxt tx | Discovery/Edit |")
$markdown.Add("|---|---:|---:|---:|---:|---:|---:|---:|")
foreach ($source in $report.by_source) {
    $markdown.Add("| $($source.source) | $($source.summary.count) | $($source.summary.effective_brief_true) | $($source.summary.discovery_calls) | $($source.summary.edit_like_calls) | $($source.summary.list_commands_calls) | $($source.summary.transcripts_with_llmstxt_calls) | $($source.summary.discovery_to_edit_ratio) |")
}
$markdown.Add("")
$markdown.Add("## By Command")
$markdown.Add("")
$markdown.Add("| Command | Family | Count | Effective brief=true | Effective brief=false | Avg output chars |")
$markdown.Add("|---|---|---:|---:|---:|---:|")
foreach ($command in $report.by_command) {
    $family = Get-CommandFamily -CommandId ([string]$command.command_id)
    $markdown.Add("| $($command.command_id) | $family | $($command.summary.count) | $($command.summary.effective_brief_true) | $($command.summary.effective_brief_false) | $($command.summary.avg_output_chars) |")
}
$markdown.Add("")
$markdown.Add("## High Exploration Examples")
$markdown.Add("")
$markdown.Add("| Lane | Task | Source | Roslyn Calls | Calls Before First Edit | Discovery Before First Edit | list-commands Before First Edit | llmstxt Before First Edit | First Edit Event Index | Mutation Channel |")
$markdown.Add("|---|---|---|---:|---:|---:|---:|---:|---:|---|")
foreach ($row in $report.timeline_examples) {
    $markdown.Add("| $($row.lane) | $($row.task) | $($row.source) | $($row.roslyn_calls_total) | $($row.roslyn_calls_before_first_edit) | $($row.discovery_calls_before_first_edit) | $($row.list_commands_before_first_edit) | $($row.llmstxt_calls_before_first_edit) | $($row.first_edit_event_index) | $($row.mutation_channel) |")
}

$markdown | Set-Content -Path $OutputMarkdownPath

Write-Host ("TRAJECTORIES_ROOT={0}" -f $resolvedTrajectoriesRoot)
Write-Host ("REPORT_JSON={0}" -f (Resolve-Path $OutputJsonPath).Path)
Write-Host ("REPORT_MD={0}" -f (Resolve-Path $OutputMarkdownPath).Path)

