param(
    [Parameter(Mandatory = $true)][string]$ControlTranscript,
    [Parameter(Mandatory = $true)][string]$TreatmentTranscript,
    [string]$OutputJson = "",
    [string]$OutputMarkdown = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$PathValue)

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return (Resolve-Path $PathValue).Path
    }

    return (Resolve-Path (Join-Path (Get-Location) $PathValue)).Path
}

function Convert-ToText {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    if ($Value -is [string]) {
        return $Value
    }

    if ($Value -is [System.Array]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($entry in $Value) {
            $parts.Add((Convert-ToText -Value $entry)) | Out-Null
        }

        return [string]::Join([Environment]::NewLine, $parts)
    }

    try {
        return ($Value | ConvertTo-Json -Depth 12 -Compress)
    } catch {
        return [string]$Value
    }
}

function Get-CollectionCount {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    if ($Value -is [System.Array]) {
        return $Value.Length
    }

    if ($Value -is [System.Collections.ICollection]) {
        return $Value.Count
    }

    return 1
}

function Read-JsonEvents {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $events = New-Object System.Collections.Generic.List[object]
    $lineNumber = 0
    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $parsed = $line | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $parsed) {
                $events.Add([pscustomobject]@{
                        event_index = $lineNumber
                        payload = $parsed
                    }) | Out-Null
            }
        } catch {
            # Keep non-JSON lines out of analysis.
        }
    }

    return $events.ToArray()
}

function Get-CommandFamily {
    param([AllowEmptyString()][string]$CommandId)

    if ([string]::IsNullOrWhiteSpace($CommandId)) {
        return "unknown"
    }

    if ($CommandId.StartsWith("cli.", [System.StringComparison]::OrdinalIgnoreCase)) { return "cli" }
    if ($CommandId.StartsWith("nav.", [System.StringComparison]::OrdinalIgnoreCase)) { return "nav" }
    if ($CommandId.StartsWith("ctx.", [System.StringComparison]::OrdinalIgnoreCase)) { return "ctx" }
    if ($CommandId.StartsWith("diag.", [System.StringComparison]::OrdinalIgnoreCase)) { return "diag" }
    if ($CommandId.StartsWith("edit.", [System.StringComparison]::OrdinalIgnoreCase)) { return "edit" }
    if ($CommandId.StartsWith("session.", [System.StringComparison]::OrdinalIgnoreCase)) { return "session" }
    if ($CommandId.StartsWith("repair.", [System.StringComparison]::OrdinalIgnoreCase)) { return "repair" }
    if ($CommandId.StartsWith("analyze.", [System.StringComparison]::OrdinalIgnoreCase)) { return "analyze" }
    if ($CommandId.StartsWith("query.", [System.StringComparison]::OrdinalIgnoreCase)) { return "query" }

    return "other"
}

function Test-IsRoslynCommandText {
    param([AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    return (
        $Text -match "(?i)\broscli(\.cmd)?\b" -or
        $Text -match "(?i)roslyn://" -or
        $Text -match "(?i)RoslynSkills\.Cli"
    )
}

function Try-ParseRoscliEnvelope {
    param([AllowEmptyString()][string]$OutputText)

    if ([string]::IsNullOrWhiteSpace($OutputText)) {
        return $null
    }

    try {
        $direct = $OutputText | ConvertFrom-Json -ErrorAction Stop
        if ($null -ne $direct -and $direct.PSObject.Properties.Name -contains "CommandId") {
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
        $extracted = $candidate | ConvertFrom-Json -ErrorAction Stop
        if ($null -ne $extracted -and $extracted.PSObject.Properties.Name -contains "CommandId") {
            return [pscustomobject]@{
                parsed = $extracted
                parse_mode = "extracted"
            }
        }
    } catch {
    }

    return $null
}

function Test-IsSchemaProbe {
    param(
        [AllowEmptyString()][string]$CommandId,
        [AllowEmptyString()][string]$CommandText
    )

    if ($CommandId -in @("cli.describe_command", "cli.validate_input", "cli.quickstart", "cli.list_commands")) {
        return $true
    }

    if ([string]::IsNullOrWhiteSpace($CommandText)) {
        return $false
    }

    return (
        $CommandText -match "(?i)\bdescribe-command\b" -or
        $CommandText -match "(?i)\bvalidate-input\b" -or
        $CommandText -match "(?i)\blist-commands\b" -or
        $CommandText -match "(?i)\bquickstart\b"
    )
}

function Test-IsEditLikeFamily {
    param([Parameter(Mandatory = $true)][string]$Family)

    return ($Family -in @("edit", "session", "repair"))
}

function Test-IsDiscoveryFamily {
    param([Parameter(Mandatory = $true)][string]$Family)

    return ($Family -in @("cli", "nav", "ctx", "diag", "analyze", "query"))
}

function Test-IsProductiveRoslynRow {
    param([Parameter(Mandatory = $true)][object]$Row)

    if (-not [bool]$Row.is_roslyn_command) {
        return $false
    }

    if ([bool]$Row.is_schema_probe) {
        return $false
    }

    if ([string]::Equals([string]$Row.command_family, "cli", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $true
}

function Test-IsVerificationCommandRow {
    param([Parameter(Mandatory = $true)][object]$Row)

    if ([string]::Equals([string]$Row.command_family, "diag", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $commandText = Convert-ToText -Value $Row.command_text
    if ([string]::IsNullOrWhiteSpace($commandText)) {
        return $false
    }

    return (
        $commandText -match "(?i)\bdotnet\s+(test|build|msbuild)\b" -or
        $commandText -match "(?i)\bvstest\b"
    )
}

function Get-RoslynUsedWellScore {
    param(
        [Parameter(Mandatory = $true)][int]$RoslynCommandCount,
        [Parameter(Mandatory = $true)][int]$ProductiveRoslynCommandCount,
        [AllowNull()][double]$SemanticEditFirstTrySuccessRate,
        [AllowNull()][double]$VerifyAfterEditRate,
        [Parameter(Mandatory = $true)][int]$DiscoveryBeforeFirstEdit,
        [Parameter(Mandatory = $true)][int]$FailedBeforeFirstEdit,
        [Parameter(Mandatory = $true)][int]$SchemaBeforeFirstEdit
    )

    if ($RoslynCommandCount -le 0 -or $ProductiveRoslynCommandCount -le 0) {
        return 0.0
    }

    $productivePresence = if ($ProductiveRoslynCommandCount -gt 0) { 1.0 } else { 0.0 }
    $productiveRatio = if ($RoslynCommandCount -gt 0) {
        [Math]::Min(1.0, [double]$ProductiveRoslynCommandCount / [double]$RoslynCommandCount)
    } else { 0.0 }
    $firstTry = if ($null -ne $SemanticEditFirstTrySuccessRate) {
        [Math]::Max(0.0, [Math]::Min(1.0, [double]$SemanticEditFirstTrySuccessRate))
    } else { 0.0 }
    $verifyRate = if ($null -ne $VerifyAfterEditRate) {
        [Math]::Max(0.0, [Math]::Min(1.0, [double]$VerifyAfterEditRate))
    } else { 0.0 }

    $preEditChurn = [Math]::Max(0, ($DiscoveryBeforeFirstEdit + $FailedBeforeFirstEdit + $SchemaBeforeFirstEdit))
    $churnScore = [Math]::Max(0.0, 1.0 - [Math]::Min(1.0, ([double]$preEditChurn / 6.0)))

    $weighted =
        0.25 * $productivePresence +
        0.20 * $productiveRatio +
        0.25 * $firstTry +
        0.20 * $verifyRate +
        0.10 * $churnScore

    return [Math]::Round((100.0 * $weighted), 2)
}

function Normalize-CommandKey {
    param(
        [AllowEmptyString()][string]$CommandId,
        [AllowEmptyString()][string]$CommandText
    )

    if (-not [string]::IsNullOrWhiteSpace($CommandId)) {
        return ("id:{0}" -f $CommandId.Trim().ToLowerInvariant())
    }

    if ([string]::IsNullOrWhiteSpace($CommandText)) {
        return "unknown"
    }

    $normalized = ($CommandText.Trim().ToLowerInvariant() -replace "\s+", " ")
    if ($normalized.Length -gt 180) {
        $normalized = $normalized.Substring(0, 180)
    }

    return ("text:{0}" -f $normalized)
}

function Get-AssistantMessages {
    param([Parameter(Mandatory = $true)][object[]]$Events)

    $messages = New-Object System.Collections.Generic.List[object]

    foreach ($entry in $Events) {
        $event = $entry.payload
        if ($null -eq $event) {
            continue
        }

        if ($event.type -eq "item.completed" -and $null -ne $event.item -and $event.item.type -eq "agent_message") {
            $text = Convert-ToText -Value $event.item.text
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $messages.Add([pscustomobject]@{
                        event_index = [int]$entry.event_index
                        text = $text
                        source = "codex"
                    }) | Out-Null
            }

            continue
        }

        if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($contentEntry in @($event.message.content)) {
                if ($null -eq $contentEntry -or $contentEntry.type -ne "text") {
                    continue
                }

                $text = Convert-ToText -Value $contentEntry.text
                if (-not [string]::IsNullOrWhiteSpace($text)) {
                    $messages.Add([pscustomobject]@{
                            event_index = [int]$entry.event_index
                            text = $text
                            source = "claude"
                        }) | Out-Null
                }
            }
        }
    }

    return $messages.ToArray()
}

function Get-CodexCommandRecords {
    param([Parameter(Mandatory = $true)][object[]]$Events)

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($entry in $Events) {
        $event = $entry.payload
        if ($null -eq $event -or $event.type -ne "item.completed" -or $null -eq $event.item) {
            continue
        }

        if ($event.item.type -ne "command_execution") {
            continue
        }

        $records.Add([pscustomobject]@{
                event_index = [int]$entry.event_index
                command_text = Convert-ToText -Value $event.item.command
                output_text = Convert-ToText -Value $event.item.aggregated_output
                exit_code = $(if ($null -ne $event.item.exit_code) { [int]$event.item.exit_code } else { $null })
                source = "codex"
                tool_name = "command_execution"
                explicit_error = $false
            }) | Out-Null
    }

    return $records.ToArray()
}

function Get-CodexDirectEditEvents {
    param([Parameter(Mandatory = $true)][object[]]$Events)

    $indexes = New-Object System.Collections.Generic.List[int]
    foreach ($entry in $Events) {
        $event = $entry.payload
        if ($null -eq $event -or $event.type -ne "item.completed" -or $null -eq $event.item) {
            continue
        }

        if ($event.item.type -eq "file_change") {
            $indexes.Add([int]$entry.event_index) | Out-Null
        }
    }

    return $indexes.ToArray()
}

function Get-ClaudeCommandRecords {
    param([Parameter(Mandatory = $true)][object[]]$Events)

    $records = New-Object System.Collections.Generic.List[object]
    $pending = @{}

    foreach ($entry in $Events) {
        $event = $entry.payload
        if ($null -eq $event) {
            continue
        }

        if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($contentEntry in @($event.message.content)) {
                if ($null -eq $contentEntry -or $contentEntry.type -ne "tool_use") {
                    continue
                }

                $toolName = Convert-ToText -Value $contentEntry.name
                if (-not [string]::Equals($toolName, "Bash", [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $toolUseId = Convert-ToText -Value $contentEntry.id
                if ([string]::IsNullOrWhiteSpace($toolUseId)) {
                    continue
                }

                $commandText = ""
                if ($null -ne $contentEntry.input) {
                    $commandText = Convert-ToText -Value $contentEntry.input.command
                }

                $pending[$toolUseId] = [pscustomobject]@{
                    event_index = [int]$entry.event_index
                    command_text = $commandText
                    source = "claude"
                    tool_name = "Bash"
                }
            }

            continue
        }

        if ($event.type -eq "user" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($contentEntry in @($event.message.content)) {
                if ($null -eq $contentEntry -or $contentEntry.type -ne "tool_result") {
                    continue
                }

                $toolUseId = Convert-ToText -Value $contentEntry.tool_use_id
                if ([string]::IsNullOrWhiteSpace($toolUseId) -or -not $pending.ContainsKey($toolUseId)) {
                    continue
                }

                $pendingRecord = $pending[$toolUseId]
                $outputText = Convert-ToText -Value $contentEntry.content
                $explicitError = $false
                if ($contentEntry.PSObject.Properties.Name -contains "is_error" -and $null -ne $contentEntry.is_error) {
                    $explicitError = [bool]$contentEntry.is_error
                }
                if (-not $explicitError -and -not [string]::IsNullOrWhiteSpace($outputText) -and $outputText -match "(?i)^Error:\s*Exit code") {
                    $explicitError = $true
                }

                $records.Add([pscustomobject]@{
                        event_index = [int]$pendingRecord.event_index
                        command_text = [string]$pendingRecord.command_text
                        output_text = $outputText
                        exit_code = $null
                        source = "claude"
                        tool_name = [string]$pendingRecord.tool_name
                        explicit_error = $explicitError
                    }) | Out-Null

                $pending.Remove($toolUseId) | Out-Null
            }
        }
    }

    foreach ($toolUseId in @($pending.Keys)) {
        $pendingRecord = $pending[$toolUseId]
        $records.Add([pscustomobject]@{
                event_index = [int]$pendingRecord.event_index
                command_text = [string]$pendingRecord.command_text
                output_text = ""
                exit_code = $null
                source = "claude"
                tool_name = [string]$pendingRecord.tool_name
                explicit_error = $false
            }) | Out-Null
    }

    return @($records.ToArray() | Sort-Object event_index)
}

function Get-ClaudeDirectEditEvents {
    param([Parameter(Mandatory = $true)][object[]]$Events)

    $indexes = New-Object System.Collections.Generic.List[int]
    foreach ($entry in $Events) {
        $event = $entry.payload
        if ($null -eq $event -or $event.type -ne "assistant" -or $null -eq $event.message -or $null -eq $event.message.content) {
            continue
        }

        foreach ($contentEntry in @($event.message.content)) {
            if ($null -eq $contentEntry -or $contentEntry.type -ne "tool_use") {
                continue
            }

            $toolName = Convert-ToText -Value $contentEntry.name
            if ($toolName -in @("Edit", "Write", "NotebookEdit")) {
                $indexes.Add([int]$entry.event_index) | Out-Null
            }
        }
    }

    return $indexes.ToArray()
}

function Get-TokenMetrics {
    param([Parameter(Mandatory = $true)][object[]]$Events)

    $promptTokens = 0
    $completionTokens = 0
    $cachedInputTokens = 0
    $cacheReadInputTokens = 0
    $cacheCreationInputTokens = 0
    $hasAny = $false

    $codexUsageEvents = @($Events | Where-Object {
            $payload = $_.payload
            $null -ne $payload -and
            $payload.type -eq "turn.completed" -and
            $null -ne $payload.usage
        })

    if ((Get-CollectionCount -Value $codexUsageEvents) -gt 0) {
        foreach ($entry in $codexUsageEvents) {
            $usage = $entry.payload.usage
            if ($null -ne $usage.input_tokens) {
                $promptTokens += [int]$usage.input_tokens
                $hasAny = $true
            }
            if ($null -ne $usage.output_tokens) {
                $completionTokens += [int]$usage.output_tokens
                $hasAny = $true
            }
            if ($null -ne $usage.cached_input_tokens) {
                $cachedInputTokens += [int]$usage.cached_input_tokens
                $hasAny = $true
            }
        }
    } else {
        $messageUsageById = @{}
        foreach ($entry in $Events) {
            $payload = $entry.payload
            if ($null -eq $payload -or $payload.type -ne "assistant" -or $null -eq $payload.message) {
                continue
            }

            $message = $payload.message
            if ($null -eq $message.usage) {
                continue
            }

            $messageId = Convert-ToText -Value $message.id
            if ([string]::IsNullOrWhiteSpace($messageId)) {
                $messageId = ("event:{0}" -f [int]$entry.event_index)
            }

            $messageUsageById[$messageId] = $message.usage
        }

        foreach ($usage in $messageUsageById.Values) {
            if ($null -ne $usage.input_tokens) {
                $promptTokens += [int]$usage.input_tokens
                $hasAny = $true
            }
            if ($null -ne $usage.output_tokens) {
                $completionTokens += [int]$usage.output_tokens
                $hasAny = $true
            }
            if ($null -ne $usage.cache_read_input_tokens) {
                $cacheReadInputTokens += [int]$usage.cache_read_input_tokens
                $hasAny = $true
            }
            if ($null -ne $usage.cache_creation_input_tokens) {
                $cacheCreationInputTokens += [int]$usage.cache_creation_input_tokens
                $hasAny = $true
            }
        }
    }

    if (-not $hasAny) {
        return [pscustomobject]@{
            prompt_tokens = $null
            completion_tokens = $null
            cached_input_tokens = $null
            cache_read_input_tokens = $null
            cache_creation_input_tokens = $null
            total_tokens = $null
            cache_inclusive_total_tokens = $null
        }
    }

    $totalTokens = $promptTokens + $completionTokens
    $cacheInclusive = $totalTokens + $cachedInputTokens + $cacheReadInputTokens + $cacheCreationInputTokens

    return [pscustomobject]@{
        prompt_tokens = $promptTokens
        completion_tokens = $completionTokens
        cached_input_tokens = $(if ($cachedInputTokens -gt 0) { $cachedInputTokens } else { 0 })
        cache_read_input_tokens = $(if ($cacheReadInputTokens -gt 0) { $cacheReadInputTokens } else { 0 })
        cache_creation_input_tokens = $(if ($cacheCreationInputTokens -gt 0) { $cacheCreationInputTokens } else { 0 })
        total_tokens = $totalTokens
        cache_inclusive_total_tokens = $cacheInclusive
    }
}

function Analyze-Transcript {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $events = Read-JsonEvents -TranscriptPath $TranscriptPath
    $commandRecords = New-Object System.Collections.Generic.List[object]

    foreach ($row in (Get-CodexCommandRecords -Events $events)) {
        $commandRecords.Add($row) | Out-Null
    }
    foreach ($row in (Get-ClaudeCommandRecords -Events $events)) {
        $commandRecords.Add($row) | Out-Null
    }

    $commandRecords = @($commandRecords.ToArray() | Sort-Object event_index)
    $directEditEvents = @((Get-CodexDirectEditEvents -Events $events) + (Get-ClaudeDirectEditEvents -Events $events) | Sort-Object)
    $assistantMessages = Get-AssistantMessages -Events $events
    $tokenMetrics = Get-TokenMetrics -Events $events

    $annotated = New-Object System.Collections.Generic.List[object]
    foreach ($row in $commandRecords) {
        $parsedEnvelope = Try-ParseRoscliEnvelope -OutputText $row.output_text
        $commandId = ""
        $envelopeOk = $null
        $parseMode = ""
        if ($null -ne $parsedEnvelope) {
            $parsed = $parsedEnvelope.parsed
            $commandId = Convert-ToText -Value $parsed.CommandId
            if ($parsed.PSObject.Properties.Name -contains "Ok" -and $null -ne $parsed.Ok) {
                $envelopeOk = [bool]$parsed.Ok
            }
            $parseMode = Convert-ToText -Value $parsedEnvelope.parse_mode
        }

        $family = Get-CommandFamily -CommandId $commandId
        $isRoslynCommand = (-not [string]::IsNullOrWhiteSpace($commandId)) -or (Test-IsRoslynCommandText -Text $row.command_text) -or (Test-IsRoslynCommandText -Text $row.output_text)
        $isSchemaProbe = Test-IsSchemaProbe -CommandId $commandId -CommandText $row.command_text
        $isEditLike = (Test-IsEditLikeFamily -Family $family)
        $isDiscovery = (Test-IsDiscoveryFamily -Family $family) -or $isSchemaProbe

        $success = $true
        if ($null -ne $envelopeOk) {
            $success = [bool]$envelopeOk
        } elseif ($null -ne $row.exit_code) {
            $success = ([int]$row.exit_code -eq 0)
        } elseif ([bool]$row.explicit_error) {
            $success = $false
        } elseif (-not [string]::IsNullOrWhiteSpace($row.output_text) -and $row.output_text -match "(?i)^Error:\s*Exit code\s+\d+") {
            $success = $false
        }

        $commandKey = Normalize-CommandKey -CommandId $commandId -CommandText $row.command_text
        $annotated.Add([pscustomobject]@{
                event_index = [int]$row.event_index
                source = [string]$row.source
                tool_name = [string]$row.tool_name
                command_text = [string]$row.command_text
                output_text = [string]$row.output_text
                command_id = $commandId
                command_family = $family
                is_roslyn_command = $isRoslynCommand
                is_schema_probe = $isSchemaProbe
                is_edit_like = $isEditLike
                is_discovery = $isDiscovery
                success = [bool]$success
                parse_mode = $parseMode
                command_key = $commandKey
            }) | Out-Null
    }

    $annotatedRows = @($annotated.ToArray() | Sort-Object event_index)
    $firstEditFromCommands = @($annotatedRows | Where-Object { $_.is_edit_like } | Select-Object -First 1)
    $firstEditEventIndex = $null
    if ((Get-CollectionCount -Value $firstEditFromCommands) -gt 0) {
        $firstEditEventIndex = [int]$firstEditFromCommands[0].event_index
    }
    if ((Get-CollectionCount -Value $directEditEvents) -gt 0) {
        $firstDirectEditEvent = [int]$directEditEvents[0]
        if ($null -eq $firstEditEventIndex -or $firstDirectEditEvent -lt $firstEditEventIndex) {
            $firstEditEventIndex = $firstDirectEditEvent
        }
    }

    $rowsBeforeFirstEdit = @()
    if ($null -ne $firstEditEventIndex) {
        $rowsBeforeFirstEdit = @($annotatedRows | Where-Object { $_.event_index -lt $firstEditEventIndex })
    }

    $firstRoslyn = @($annotatedRows | Where-Object { $_.is_roslyn_command } | Select-Object -First 1)
    $firstRoslynEventIndex = if ((Get-CollectionCount -Value $firstRoslyn) -gt 0) { [int]$firstRoslyn[0].event_index } else { $null }

    $toolStrategyRegex = '(?i)\broscli\b|describe-command|validate-input|list-commands|quickstart|workspace_context|require-workspace|mcp|schema'
    $toolStrategyMessages = @($assistantMessages | Where-Object { $_.text -match $toolStrategyRegex })
    $firstToolStrategyMessage = @($toolStrategyMessages | Select-Object -First 1)
    $firstToolStrategyMessageIndex = if ((Get-CollectionCount -Value $firstToolStrategyMessage) -gt 0) { [int]$firstToolStrategyMessage[0].event_index } else { $null }

    $retryRecoveries = 0
    $groupedByKey = @($annotatedRows | Group-Object command_key)
    foreach ($group in $groupedByKey) {
        $seenFailure = $false
        foreach ($row in @($group.Group | Sort-Object event_index)) {
            if (-not [bool]$row.success) {
                $seenFailure = $true
                continue
            }

            if ($seenFailure) {
                $retryRecoveries++
                $seenFailure = $false
            }
        }
    }

    $topCommands = @(
        $annotatedRows |
        Group-Object command_key |
        Sort-Object Count -Descending |
        Select-Object -First 8 |
        ForEach-Object {
            $first = $_.Group | Select-Object -First 1
            [pscustomobject]@{
                key = [string]$_.Name
                count = [int]$_.Count
                command_id = [string]$first.command_id
                family = [string]$first.command_family
            }
        }
    )

    $totalEvents = Get-CollectionCount -Value $events
    $commandRoundTrips = Get-CollectionCount -Value $annotatedRows
    $roslynCommandCount = @($annotatedRows | Where-Object { $_.is_roslyn_command }).Count
    $roslynSuccessfulCount = @($annotatedRows | Where-Object { $_.is_roslyn_command -and $_.success }).Count
    $roslynFailedCount = [Math]::Max(0, ($roslynCommandCount - $roslynSuccessfulCount))
    $failedCommandCount = @($annotatedRows | Where-Object { -not $_.success }).Count
    $schemaProbeCount = @($annotatedRows | Where-Object { $_.is_schema_probe }).Count
    $discoveryCommandCount = @($annotatedRows | Where-Object { $_.is_discovery }).Count
    $editLikeCommandCount = @($annotatedRows | Where-Object { $_.is_edit_like }).Count

    $roslynBeforeEdit = @($rowsBeforeFirstEdit | Where-Object { $_.is_roslyn_command }).Count
    $schemaBeforeEdit = @($rowsBeforeFirstEdit | Where-Object { $_.is_schema_probe }).Count
    $discoveryBeforeEdit = @($rowsBeforeFirstEdit | Where-Object { $_.is_discovery }).Count
    $failedBeforeEdit = @($rowsBeforeFirstEdit | Where-Object { -not $_.success }).Count
    $eventsBeforeFirstEdit = if ($null -ne $firstEditEventIndex) { [int]$firstEditEventIndex - 1 } else { $null }

    $productiveRoslynRows = @($annotatedRows | Where-Object { Test-IsProductiveRoslynRow -Row $_ })
    $productiveRoslynCommandCount = (Get-CollectionCount -Value $productiveRoslynRows)
    $firstProductiveRoslyn = @($productiveRoslynRows | Select-Object -First 1)
    $firstProductiveRoslynEventIndex = if ((Get-CollectionCount -Value $firstProductiveRoslyn) -gt 0) { [int]$firstProductiveRoslyn[0].event_index } else { $null }
    $eventsBeforeFirstProductiveRoslyn = if ($null -ne $firstProductiveRoslynEventIndex) { [int]$firstProductiveRoslynEventIndex - 1 } else { $null }
    $productiveRoslynBeforeEdit = @($rowsBeforeFirstEdit | Where-Object { Test-IsProductiveRoslynRow -Row $_ }).Count

    $semanticEditRows = @($annotatedRows | Where-Object { $_.is_roslyn_command -and $_.is_edit_like })
    $semanticEditAttempts = (Get-CollectionCount -Value $semanticEditRows)
    $semanticEditGroups = @($semanticEditRows | Group-Object command_key)
    $semanticEditDistinctKeys = (Get-CollectionCount -Value $semanticEditGroups)
    $semanticEditFirstTrySuccesses = 0
    foreach ($group in $semanticEditGroups) {
        $firstInGroup = @($group.Group | Sort-Object event_index | Select-Object -First 1)
        if ((Get-CollectionCount -Value $firstInGroup) -gt 0 -and [bool]$firstInGroup[0].success) {
            $semanticEditFirstTrySuccesses++
        }
    }
    $semanticEditFirstTrySuccessRate = if ($semanticEditDistinctKeys -gt 0) {
        [Math]::Round(([double]$semanticEditFirstTrySuccesses / [double]$semanticEditDistinctKeys), 4)
    } else { $null }

    $editBoundaryIndexes = New-Object System.Collections.Generic.List[int]
    foreach ($row in $semanticEditRows) {
        $editBoundaryIndexes.Add([int]$row.event_index) | Out-Null
    }
    foreach ($idx in $directEditEvents) {
        $editBoundaryIndexes.Add([int]$idx) | Out-Null
    }
    $sortedEditBoundaries = @($editBoundaryIndexes.ToArray() | Sort-Object -Unique)
    $editEventsTotal = (Get-CollectionCount -Value $sortedEditBoundaries)
    $editEventsWithVerification = 0
    for ($i = 0; $i -lt $editEventsTotal; $i++) {
        $currentEditIndex = [int]$sortedEditBoundaries[$i]
        $nextEditIndex = if ($i -lt ($editEventsTotal - 1)) { [int]$sortedEditBoundaries[$i + 1] } else { [int]::MaxValue }
        $windowRows = @($annotatedRows | Where-Object { $_.event_index -gt $currentEditIndex -and $_.event_index -lt $nextEditIndex })
        $hasVerification = @($windowRows | Where-Object { Test-IsVerificationCommandRow -Row $_ } | Select-Object -First 1)
        if ((Get-CollectionCount -Value $hasVerification) -gt 0) {
            $editEventsWithVerification++
        }
    }
    $verifyAfterEditRate = if ($editEventsTotal -gt 0) {
        [Math]::Round(([double]$editEventsWithVerification / [double]$editEventsTotal), 4)
    } else { $null }

    $roslynUsedWellScore = Get-RoslynUsedWellScore `
        -RoslynCommandCount $roslynCommandCount `
        -ProductiveRoslynCommandCount $productiveRoslynCommandCount `
        -SemanticEditFirstTrySuccessRate $semanticEditFirstTrySuccessRate `
        -VerifyAfterEditRate $verifyAfterEditRate `
        -DiscoveryBeforeFirstEdit $discoveryBeforeEdit `
        -FailedBeforeFirstEdit $failedBeforeEdit `
        -SchemaBeforeFirstEdit $schemaBeforeEdit

    $directEditBeforeRoslyn = $null
    if ((Get-CollectionCount -Value $directEditEvents) -gt 0 -and $null -ne $firstRoslynEventIndex) {
        $directEditBeforeRoslyn = @($directEditEvents | Where-Object { $_ -lt $firstRoslynEventIndex }).Count
    } elseif ((Get-CollectionCount -Value $directEditEvents) -gt 0 -and $null -eq $firstRoslynEventIndex) {
        $directEditBeforeRoslyn = Get-CollectionCount -Value $directEditEvents
    }

    return [ordered]@{
        transcript_path = $TranscriptPath
        total_events = $totalEvents
        assistant_messages_total = (Get-CollectionCount -Value $assistantMessages)
        assistant_tool_strategy_messages = (Get-CollectionCount -Value $toolStrategyMessages)
        first_tool_strategy_message_index = $firstToolStrategyMessageIndex
        command_round_trips = $commandRoundTrips
        failed_commands = $failedCommandCount
        retry_recoveries = $retryRecoveries
        distinct_command_keys = (Get-CollectionCount -Value $groupedByKey)
        roslyn_command_count = $roslynCommandCount
        roslyn_successful_count = $roslynSuccessfulCount
        roslyn_failed_count = $roslynFailedCount
        discovery_command_count = $discoveryCommandCount
        edit_like_command_count = $editLikeCommandCount
        schema_probe_count = $schemaProbeCount
        first_roslyn_event_index = $firstRoslynEventIndex
        first_edit_event_index = $firstEditEventIndex
        events_before_first_edit = $eventsBeforeFirstEdit
        roslyn_commands_before_first_edit = $roslynBeforeEdit
        discovery_commands_before_first_edit = $discoveryBeforeEdit
        schema_probes_before_first_edit = $schemaBeforeEdit
        failed_commands_before_first_edit = $failedBeforeEdit
        productive_roslyn_command_count = $productiveRoslynCommandCount
        productive_roslyn_commands_before_first_edit = $productiveRoslynBeforeEdit
        first_productive_roslyn_event_index = $firstProductiveRoslynEventIndex
        events_before_first_productive_roslyn = $eventsBeforeFirstProductiveRoslyn
        semantic_edit_attempts = $semanticEditAttempts
        semantic_edit_distinct_commands = $semanticEditDistinctKeys
        semantic_edit_first_try_successes = $semanticEditFirstTrySuccesses
        semantic_edit_first_try_success_rate = $semanticEditFirstTrySuccessRate
        edit_events_total = $editEventsTotal
        edit_events_with_verification = $editEventsWithVerification
        verify_after_edit_rate = $verifyAfterEditRate
        roslyn_used_well_score = $roslynUsedWellScore
        direct_edit_events = (Get-CollectionCount -Value $directEditEvents)
        direct_edit_events_before_first_roslyn = $directEditBeforeRoslyn
        prompt_tokens = $tokenMetrics.prompt_tokens
        completion_tokens = $tokenMetrics.completion_tokens
        cached_input_tokens = $tokenMetrics.cached_input_tokens
        cache_read_input_tokens = $tokenMetrics.cache_read_input_tokens
        cache_creation_input_tokens = $tokenMetrics.cache_creation_input_tokens
        total_tokens = $tokenMetrics.total_tokens
        cache_inclusive_total_tokens = $tokenMetrics.cache_inclusive_total_tokens
        top_commands = $topCommands
    }
}

function Get-Delta {
    param(
        [AllowNull()][object]$TreatmentValue,
        [AllowNull()][object]$ControlValue
    )

    if ($null -eq $TreatmentValue -or $null -eq $ControlValue) {
        return $null
    }

    return ([double]$TreatmentValue - [double]$ControlValue)
}

$controlPath = Resolve-AbsolutePath -PathValue $ControlTranscript
$treatmentPath = Resolve-AbsolutePath -PathValue $TreatmentTranscript

$control = Analyze-Transcript -TranscriptPath $controlPath
$treatment = Analyze-Transcript -TranscriptPath $treatmentPath

$deltas = [ordered]@{
    total_events = Get-Delta -TreatmentValue $treatment.total_events -ControlValue $control.total_events
    command_round_trips = Get-Delta -TreatmentValue $treatment.command_round_trips -ControlValue $control.command_round_trips
    failed_commands = Get-Delta -TreatmentValue $treatment.failed_commands -ControlValue $control.failed_commands
    retry_recoveries = Get-Delta -TreatmentValue $treatment.retry_recoveries -ControlValue $control.retry_recoveries
    roslyn_command_count = Get-Delta -TreatmentValue $treatment.roslyn_command_count -ControlValue $control.roslyn_command_count
    discovery_command_count = Get-Delta -TreatmentValue $treatment.discovery_command_count -ControlValue $control.discovery_command_count
    schema_probe_count = Get-Delta -TreatmentValue $treatment.schema_probe_count -ControlValue $control.schema_probe_count
    events_before_first_edit = Get-Delta -TreatmentValue $treatment.events_before_first_edit -ControlValue $control.events_before_first_edit
    discovery_commands_before_first_edit = Get-Delta -TreatmentValue $treatment.discovery_commands_before_first_edit -ControlValue $control.discovery_commands_before_first_edit
    failed_commands_before_first_edit = Get-Delta -TreatmentValue $treatment.failed_commands_before_first_edit -ControlValue $control.failed_commands_before_first_edit
    productive_roslyn_command_count = Get-Delta -TreatmentValue $treatment.productive_roslyn_command_count -ControlValue $control.productive_roslyn_command_count
    events_before_first_productive_roslyn = Get-Delta -TreatmentValue $treatment.events_before_first_productive_roslyn -ControlValue $control.events_before_first_productive_roslyn
    semantic_edit_first_try_success_rate = Get-Delta -TreatmentValue $treatment.semantic_edit_first_try_success_rate -ControlValue $control.semantic_edit_first_try_success_rate
    verify_after_edit_rate = Get-Delta -TreatmentValue $treatment.verify_after_edit_rate -ControlValue $control.verify_after_edit_rate
    roslyn_used_well_score = Get-Delta -TreatmentValue $treatment.roslyn_used_well_score -ControlValue $control.roslyn_used_well_score
    total_tokens = Get-Delta -TreatmentValue $treatment.total_tokens -ControlValue $control.total_tokens
    cache_inclusive_total_tokens = Get-Delta -TreatmentValue $treatment.cache_inclusive_total_tokens -ControlValue $control.cache_inclusive_total_tokens
}

$comparison = [ordered]@{
    schema_version = "2.0"
    generated_utc = [DateTimeOffset]::UtcNow.ToString("o")
    control = $control
    treatment = $treatment
    deltas = $deltas
    interpretation = [ordered]@{
        treatment_roslyn_used = ([int]$treatment.roslyn_command_count -gt 0)
        control_roslyn_contamination = ([int]$control.roslyn_command_count -gt 0)
        treatment_upfront_exploration_higher = ($null -ne $deltas.discovery_commands_before_first_edit -and [double]$deltas.discovery_commands_before_first_edit -gt 0)
        treatment_failure_pressure_higher = ($null -ne $deltas.failed_commands_before_first_edit -and [double]$deltas.failed_commands_before_first_edit -gt 0)
        treatment_productive_roslyn_higher = ($null -ne $deltas.productive_roslyn_command_count -and [double]$deltas.productive_roslyn_command_count -gt 0)
        treatment_roslyn_used_well_score_higher = ($null -ne $deltas.roslyn_used_well_score -and [double]$deltas.roslyn_used_well_score -gt 0)
    }
}

if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
    $jsonPath = if ([System.IO.Path]::IsPathRooted($OutputJson)) { $OutputJson } else { Join-Path (Get-Location) $OutputJson }
    $comparison | ConvertTo-Json -Depth 10 | Set-Content -Path $jsonPath -NoNewline
}

if (-not [string]::IsNullOrWhiteSpace($OutputMarkdown)) {
    $mdPath = if ([System.IO.Path]::IsPathRooted($OutputMarkdown)) { $OutputMarkdown } else { Join-Path (Get-Location) $OutputMarkdown }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Tool-Thinking Split Summary")
    $lines.Add("")
    $lines.Add("| Metric | Control | Treatment | Delta (T-C) |")
    $lines.Add("| --- | ---: | ---: | ---: |")
    $lines.Add("| Total events | $($control.total_events) | $($treatment.total_events) | $($deltas.total_events) |")
    $lines.Add("| Command round trips | $($control.command_round_trips) | $($treatment.command_round_trips) | $($deltas.command_round_trips) |")
    $lines.Add("| Roslyn commands | $($control.roslyn_command_count) | $($treatment.roslyn_command_count) | $($deltas.roslyn_command_count) |")
    $lines.Add("| Discovery commands | $($control.discovery_command_count) | $($treatment.discovery_command_count) | $($deltas.discovery_command_count) |")
    $lines.Add("| Schema probes | $($control.schema_probe_count) | $($treatment.schema_probe_count) | $($deltas.schema_probe_count) |")
    $lines.Add("| Failed commands | $($control.failed_commands) | $($treatment.failed_commands) | $($deltas.failed_commands) |")
    $lines.Add("| Retry recoveries | $($control.retry_recoveries) | $($treatment.retry_recoveries) | $($deltas.retry_recoveries) |")
    $lines.Add("| Events before first edit | $($control.events_before_first_edit) | $($treatment.events_before_first_edit) | $($deltas.events_before_first_edit) |")
    $lines.Add("| Discovery before first edit | $($control.discovery_commands_before_first_edit) | $($treatment.discovery_commands_before_first_edit) | $($deltas.discovery_commands_before_first_edit) |")
    $lines.Add("| Failed before first edit | $($control.failed_commands_before_first_edit) | $($treatment.failed_commands_before_first_edit) | $($deltas.failed_commands_before_first_edit) |")
    $lines.Add("| Productive Roslyn commands | $($control.productive_roslyn_command_count) | $($treatment.productive_roslyn_command_count) | $($deltas.productive_roslyn_command_count) |")
    $lines.Add("| Events before first productive Roslyn | $($control.events_before_first_productive_roslyn) | $($treatment.events_before_first_productive_roslyn) | $($deltas.events_before_first_productive_roslyn) |")
    $lines.Add("| Semantic edit first-try success rate | $($control.semantic_edit_first_try_success_rate) | $($treatment.semantic_edit_first_try_success_rate) | $($deltas.semantic_edit_first_try_success_rate) |")
    $lines.Add("| Verify-after-edit rate | $($control.verify_after_edit_rate) | $($treatment.verify_after_edit_rate) | $($deltas.verify_after_edit_rate) |")
    $lines.Add("| Roslyn used-well score | $($control.roslyn_used_well_score) | $($treatment.roslyn_used_well_score) | $($deltas.roslyn_used_well_score) |")
    $lines.Add("| Total tokens | $($control.total_tokens) | $($treatment.total_tokens) | $($deltas.total_tokens) |")
    $lines.Add("| Cache-inclusive tokens | $($control.cache_inclusive_total_tokens) | $($treatment.cache_inclusive_total_tokens) | $($deltas.cache_inclusive_total_tokens) |")
    $lines.Add("")
    $lines.Add("Interpretation hints:")
    $lines.Add("- Positive discovery-before-first-edit delta indicates higher upfront exploration overhead in treatment.")
    $lines.Add("- Positive failed-before-first-edit delta indicates schema/usage friction before productive edits.")
    $lines.Add("- Control should have Roslyn commands = 0; otherwise treat as contamination.")
    Set-Content -Path $mdPath -Value ($lines -join [Environment]::NewLine) -NoNewline
}

$comparison | ConvertTo-Json -Depth 10
