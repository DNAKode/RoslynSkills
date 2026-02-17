param(
    [Parameter(Mandatory = $true)][string]$RunsDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RoslynToolsOffered {
    return @(
        "nav.find_symbol",
        "ctx.member_source",
        "ctx.symbol_envelope",
        "diag.get_file_diagnostics",
        "diag.get_workspace_snapshot",
        "diag.get_solution_snapshot",
        "edit.rename_symbol",
        "edit.replace_member_body",
        "edit.transaction",
        "edit.create_file",
        "session.open",
        "session.apply_text_edits",
        "session.apply_and_commit",
        "session.commit",
        "session.close"
    )
}

function Get-RoslynCommandPattern {
    # Regex (PowerShell single-quoted string): match "scripts\roscli.cmd <commandId>" or "scripts/roscli.cmd <commandId>"
    return '(?is)scripts[\\/]+roscli\.cmd\s+([a-z]+\.[a-z0-9_]+)'
}

$runsRoot = (Resolve-Path $RunsDirectory).Path
$runFiles = Get-ChildItem -Path $runsRoot -Filter *.json -Recurse -File

$updated = 0
foreach ($file in $runFiles) {
    $raw = Get-Content -Path $file.FullName -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { continue }

    try {
        $j = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        continue
    }

    # Only touch agent-eval run records (identified by run_id/task_id/condition_id).
    if ($j.PSObject.Properties.Match("run_id").Count -eq 0) { continue }
    if ($j.PSObject.Properties.Match("task_id").Count -eq 0) { continue }
    if ($j.PSObject.Properties.Match("condition_id").Count -eq 0) { continue }

    $changed = $false

    $conditionId = [string]$j.condition_id
    $roslynEnabled = -not [string]::IsNullOrWhiteSpace($conditionId) -and ($conditionId -ne "control-text-only")

    if ($j.PSObject.Properties.Match("tools_offered").Count -eq 0 -or $null -eq $j.tools_offered) {
        $toolsOffered = @("shell.exec")
        if ($roslynEnabled) { $toolsOffered += Get-RoslynToolsOffered }
        $j | Add-Member -NotePropertyName tools_offered -NotePropertyValue $toolsOffered -Force
        $changed = $true
    }

    $toolCallsMissing = ($j.PSObject.Properties.Match("tool_calls").Count -eq 0 -or $null -eq $j.tool_calls)
    $toolCallsEmpty = (-not $toolCallsMissing -and @($j.tool_calls).Count -eq 0)

    $containsFallbackRoscli = $false
    if (-not $toolCallsMissing -and -not $toolCallsEmpty) {
        foreach ($call in @($j.tool_calls)) {
            if ($null -eq $call) { continue }
            $name = $null
            if ($call.PSObject.Properties.Match("tool_name").Count -gt 0) { $name = [string]$call.tool_name }
            elseif ($call.PSObject.Properties.Match("toolName").Count -gt 0) { $name = [string]$call.toolName }
            if (-not [string]::IsNullOrWhiteSpace($name) -and $name -eq "roscli") { $containsFallbackRoscli = $true; break }
        }
    }

    if ($toolCallsMissing -or $toolCallsEmpty -or $containsFallbackRoscli) {
        $toolCalls = @()
        $pattern = Get-RoslynCommandPattern
        if ($j.PSObject.Properties.Match("roslyn_usage_indicators").Count -gt 0 -and $null -ne $j.roslyn_usage_indicators) {
            foreach ($invocation in @($j.roslyn_usage_indicators)) {
                $toolName = "roscli"
                $text = [string]$invocation
                if (-not [string]::IsNullOrWhiteSpace($text) -and ($text -match $pattern)) {
                    $candidate = [string]$Matches[1]
                    if (-not [string]::IsNullOrWhiteSpace($candidate)) { $toolName = $candidate.Trim() }
                }
                $toolCalls += [ordered]@{ tool_name = $toolName; ok = $true }
            }
        }

        $j | Add-Member -NotePropertyName tool_calls -NotePropertyValue $toolCalls -Force
        $changed = $true
    }

    if ($changed) {
        $updated++
        $j | ConvertTo-Json -Depth 30 | Set-Content -Path $file.FullName -NoNewline
    }
}

Write-Host ("Updated {0} run record(s) under {1}" -f $updated, $runsRoot)
