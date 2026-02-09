param(
    [string]$ManifestPath = "benchmarks/experiments/lightweight-utility-game-v1/manifest.json",
    [string]$OutputRoot = "",
    [string]$CodexModel = "",
    [string]$ClaudeModel = "",
    [switch]$SkipCodex,
    [switch]$SkipClaude,
    [switch]$SkipControl,
    [switch]$SkipTreatment,
    [string[]]$ConditionIds = @(),
    [string[]]$TaskIds = @(),
    [ValidateSet("standard", "brief-first", "verbose-first")][string]$RoslynGuidanceProfile = "standard",
    [switch]$NoAcceptanceChecks,
    [switch]$UseWorkingTreeOverlay
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

function Get-ObjectPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    if ($null -eq $Object -or [string]::IsNullOrWhiteSpace($PropertyName)) {
        return $null
    }

    if ($Object.PSObject.Properties.Name -contains $PropertyName) {
        return $Object.$PropertyName
    }

    return $null
}

function Convert-ToSlug {
    param([object]$Value)

    $text = Convert-ToText $Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return "unknown"
    }

    $slug = $text.ToLowerInvariant()
    $slug = [regex]::Replace($slug, "[^a-z0-9]+", "-").Trim("-")
    if ([string]::IsNullOrWhiteSpace($slug)) {
        return "unknown"
    }

    return $slug
}

function Get-ConditionMode {
    param([Parameter(Mandatory = $true)][object]$Condition)

    $rawMode = Convert-ToText (Get-ObjectPropertyValue -Object $Condition -PropertyName "mode")
    $roslynEnabled = [bool](Get-ObjectPropertyValue -Object $Condition -PropertyName "roslyn_tools_enabled")
    if ([string]::IsNullOrWhiteSpace($rawMode)) {
        if ($roslynEnabled) {
            return "treatment"
        }

        return "control"
    }

    $normalized = $rawMode.Trim().ToLowerInvariant()
    if ($normalized -in @("control", "treatment")) {
        return $normalized
    }

    if ($roslynEnabled) {
        return "treatment"
    }

    return "control"
}

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputRoot
    )

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        return (Join-Path $RepoRoot "artifacts/real-agent-runs/$stamp-lightweight-utility-game")
    }

    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        return $OutputRoot
    }

    return (Join-Path $RepoRoot $OutputRoot)
}

function Ensure-Command {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return ([System.IO.Path]::GetFullPath((Resolve-Path $Path).Path)).TrimEnd('\')
}

function Assert-HostSessionAnchor {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedCwd,
        [Parameter(Mandatory = $true)][string]$ExpectedGitRoot,
        [Parameter(Mandatory = $true)][string]$Phase
    )

    $currentCwd = Get-NormalizedPath -Path (Get-Location).Path
    $expectedCwd = Get-NormalizedPath -Path $ExpectedCwd
    if (-not [string]::Equals($currentCwd, $expectedCwd, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Host cwd drift detected at '$Phase'. expected='$expectedCwd' current='$currentCwd'."
    }

    $currentGitRootRaw = Convert-ToText (& git -C $currentCwd rev-parse --show-toplevel 2>$null)
    if ([string]::IsNullOrWhiteSpace($currentGitRootRaw)) {
        throw "Host git root missing at '$Phase' for cwd '$currentCwd'."
    }

    $currentGitRoot = Get-NormalizedPath -Path $currentGitRootRaw
    $expectedGitRoot = Get-NormalizedPath -Path $ExpectedGitRoot
    if (-not [string]::Equals($currentGitRoot, $expectedGitRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Host git root drift detected at '$Phase'. expected='$expectedGitRoot' current='$currentGitRoot'."
    }
}

function Sync-WorkingTreeOverlay {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$WorkspacePath
    )

    $source = (Resolve-Path $RepoRoot).Path
    $target = (Resolve-Path $WorkspacePath).Path

    $excludeDirectories = @(
        ".git",
        ".vs",
        "artifacts",
        "bin",
        "obj"
    ) | ForEach-Object { Join-Path $source $_ }

    $roboArgs = @(
        $source,
        $target,
        "/MIR",
        "/NFL",
        "/NDL",
        "/NJH",
        "/NJS",
        "/NP"
    )

    if ($excludeDirectories.Count -gt 0) {
        $roboArgs += "/XD"
        $roboArgs += $excludeDirectories
    }

    $roboArgs += @(
        "/XF",
        "*.user",
        "*.suo"
    )

    & robocopy @roboArgs | Out-Null
    $exitCode = $LASTEXITCODE
    if ($exitCode -ge 8) {
        throw "robocopy overlay failed with exit code $exitCode."
    }
}

function Write-RoscliShim {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspacePath
    )

    $scriptsDir = Join-Path $WorkspacePath "scripts"
    New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null

    $cmdPath = Join-Path $scriptsDir "roscli.cmd"
    $cmdBody = @"
@echo off
dotnet run --project src/RoslynAgent.Cli -- %*
"@
    Set-Content -Path $cmdPath -Value $cmdBody -NoNewline

    $shPath = Join-Path $scriptsDir "roscli"
    $shBody = @"
#!/usr/bin/env bash
set -euo pipefail
dotnet run --project src/RoslynAgent.Cli -- "$@"
"@
    Set-Content -Path $shPath -Value $shBody -NoNewline
}

function Write-RoscliDisabledShim {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspacePath
    )

    $scriptsDir = Join-Path $WorkspacePath "scripts"
    New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null

    $cmdPath = Join-Path $scriptsDir "roscli.cmd"
    $cmdBody = @"
@echo off
echo Roslyn CLI is disabled for control-condition workspaces.
exit /b 88
"@
    Set-Content -Path $cmdPath -Value $cmdBody -NoNewline

    $shPath = Join-Path $scriptsDir "roscli"
    $shBody = @"
#!/usr/bin/env bash
set -euo pipefail
echo "Roslyn CLI is disabled for control-condition workspaces." >&2
exit 88
"@
    Set-Content -Path $shPath -Value $shBody -NoNewline
}

function New-AgentEnvironmentOverrides {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$RunDirectory
    )

    $agentHomeRoot = Join-Path $RunDirectory ".agent-home"
    $profileRoot = Join-Path $agentHomeRoot "profile"
    $appDataRoot = Join-Path $agentHomeRoot "appdata"
    $localAppDataRoot = Join-Path $agentHomeRoot "localappdata"
    $xdgConfigRoot = Join-Path $agentHomeRoot "xdg-config"
    $xdgCacheRoot = Join-Path $agentHomeRoot "xdg-cache"
    $codexHomeRoot = Join-Path $agentHomeRoot "codex-home"
    $claudeConfigRoot = Join-Path $agentHomeRoot "claude-config"

    foreach ($path in @(
            $agentHomeRoot,
            $profileRoot,
            $appDataRoot,
            $localAppDataRoot,
            $xdgConfigRoot,
            $xdgCacheRoot,
            $codexHomeRoot,
            $claudeConfigRoot
        )) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }

    function Copy-FileIfExists {
        param(
            [Parameter(Mandatory = $true)][string]$SourcePath,
            [Parameter(Mandatory = $true)][string]$DestinationPath
        )

        if (Test-Path $SourcePath -PathType Leaf) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
            Copy-Item -Path $SourcePath -Destination $DestinationPath -Force
        }
    }

    $overrides = @{
        HOME = $profileRoot
        USERPROFILE = $profileRoot
        APPDATA = $appDataRoot
        LOCALAPPDATA = $localAppDataRoot
        XDG_CONFIG_HOME = $xdgConfigRoot
        XDG_CACHE_HOME = $xdgCacheRoot
    }

    if ($Agent -eq "codex") {
        $existingCodexHome = Convert-ToText $env:CODEX_HOME
        if ([string]::IsNullOrWhiteSpace($existingCodexHome)) {
            $existingCodexHome = Join-Path $env:USERPROFILE ".codex"
        }

        if (Test-Path $existingCodexHome -PathType Container) {
            foreach ($fileName in @("auth.json", "config.toml", "cap_sid", "internal_storage.json", "version.json", "models_cache.json")) {
                Copy-FileIfExists `
                    -SourcePath (Join-Path $existingCodexHome $fileName) `
                    -DestinationPath (Join-Path $codexHomeRoot $fileName)
            }

            $sourceRules = Join-Path $existingCodexHome "rules"
            $targetRules = Join-Path $codexHomeRoot "rules"
            if (Test-Path $sourceRules -PathType Container) {
                Copy-Item -Path $sourceRules -Destination $targetRules -Recurse -Force
            }
        }

        $overrides["CODEX_HOME"] = $codexHomeRoot
    } elseif ($Agent -eq "claude") {
        $existingClaudeConfig = Convert-ToText $env:CLAUDE_CONFIG_DIR
        if ([string]::IsNullOrWhiteSpace($existingClaudeConfig)) {
            $existingClaudeConfig = Join-Path $env:USERPROFILE ".claude"
        }

        if (Test-Path $existingClaudeConfig -PathType Container) {
            foreach ($fileName in @(".credentials.json", "settings.json", "CLAUDE.md")) {
                Copy-FileIfExists `
                    -SourcePath (Join-Path $existingClaudeConfig $fileName) `
                    -DestinationPath (Join-Path $claudeConfigRoot $fileName)
            }
        }

        $overrides["CLAUDE_CONFIG_DIR"] = $claudeConfigRoot
        $overrides["ANTHROPIC_CONFIG_DIR"] = $claudeConfigRoot
    }

    return $overrides
}

function New-TrajectoryWorkspace {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$BaselineCommit,
        [Parameter(Mandatory = $true)][bool]$EnableRoslynShim,
        [Parameter(Mandatory = $true)][bool]$UseWorkingTreeOverlay
    )

    if (Test-Path $WorkspacePath) {
        Remove-Item -Recurse -Force $WorkspacePath
    }

    & git clone --quiet --no-hardlinks $RepoRoot $WorkspacePath
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed while creating workspace '$WorkspacePath'."
    }

    if (-not [string]::IsNullOrWhiteSpace($BaselineCommit)) {
        & git -C $WorkspacePath checkout --quiet $BaselineCommit
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to checkout baseline commit '$BaselineCommit' in '$WorkspacePath'."
        }
    }

    if ($UseWorkingTreeOverlay) {
        Sync-WorkingTreeOverlay -RepoRoot $RepoRoot -WorkspacePath $WorkspacePath
    }

    & git -C $WorkspacePath config user.name "Benchmark Runner" | Out-Null
    & git -C $WorkspacePath config user.email "benchmark-runner@local.invalid" | Out-Null
    & git -C $WorkspacePath config commit.gpgSign false | Out-Null

    if ($UseWorkingTreeOverlay) {
        $overlayStatus = Convert-ToText (& git -C $WorkspacePath status --porcelain)
        if (-not [string]::IsNullOrWhiteSpace($overlayStatus)) {
            & git -C $WorkspacePath add -A | Out-Null
            & git -C $WorkspacePath commit --no-gpg-sign -m "benchmark/bootstrap-working-tree-overlay" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to commit overlay baseline in '$WorkspacePath'."
            }
        }
    }

    if ($EnableRoslynShim) {
        Write-RoscliShim -WorkspacePath $WorkspacePath
    } else {
        Write-RoscliDisabledShim -WorkspacePath $WorkspacePath
    }
}

function Invoke-AgentProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $false)][hashtable]$EnvironmentOverrides = @{}
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $hasNativeErrorPreference = $null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)
    if ($hasNativeErrorPreference) {
        $previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
    }

    $ErrorActionPreference = "Continue"
    $exitCode = 1
    $appliedEnvironmentKeys = New-Object System.Collections.Generic.List[string]
    $previousEnvironmentValues = @{}
    Push-Location $WorkingDirectory
    try {
        foreach ($key in $EnvironmentOverrides.Keys) {
            $keyName = [string]$key
            $previousEnvironmentValues[$keyName] = [System.Environment]::GetEnvironmentVariable($keyName, "Process")
            [System.Environment]::SetEnvironmentVariable($keyName, [string]$EnvironmentOverrides[$keyName], "Process")
            $appliedEnvironmentKeys.Add($keyName)
        }

        $PromptText | & $Executable @Arguments 2>&1 | Tee-Object -FilePath $TranscriptPath | Out-Host
        if ($null -ne $LASTEXITCODE) {
            $exitCode = [int]$LASTEXITCODE
        }
    } catch {
        $_ | Out-String | Tee-Object -FilePath $TranscriptPath -Append | Out-Host
        if ($null -ne $LASTEXITCODE) {
            $exitCode = [int]$LASTEXITCODE
        }
    } finally {
        Pop-Location
        $ErrorActionPreference = $previousErrorActionPreference
        if ($hasNativeErrorPreference) {
            $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
        }

        foreach ($keyName in $appliedEnvironmentKeys) {
            [System.Environment]::SetEnvironmentVariable($keyName, $previousEnvironmentValues[$keyName], "Process")
        }
    }

    return $exitCode
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$CommandText,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $hasNativeErrorPreference = $null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)
    if ($hasNativeErrorPreference) {
        $previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
    }

    $ErrorActionPreference = "Continue"
    $exitCode = 1
    New-Item -ItemType File -Force -Path $LogPath | Out-Null
    Push-Location $WorkingDirectory
    try {
        & powershell.exe -NoProfile -NonInteractive -Command $CommandText 2>&1 | Tee-Object -FilePath $LogPath -Append | Out-Host
        if ($null -ne $LASTEXITCODE) {
            $exitCode = [int]$LASTEXITCODE
        }
    } catch {
        $_ | Out-String | Tee-Object -FilePath $LogPath -Append | Out-Host
        if ($null -ne $LASTEXITCODE) {
            $exitCode = [int]$LASTEXITCODE
        }
    } finally {
        Pop-Location
        $ErrorActionPreference = $previousErrorActionPreference
        if ($hasNativeErrorPreference) {
            $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
        }
    }

    return $exitCode
}

function Read-JsonLines {
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

function Get-CodexCommandRecords {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $events = Read-JsonLines -TranscriptPath $TranscriptPath
    $records = New-Object System.Collections.Generic.List[object]

    foreach ($event in $events) {
        if ($event.type -ne "item.completed") {
            continue
        }

        if ($null -eq $event.item -or $event.item.type -ne "command_execution") {
            continue
        }

        $commandText = Convert-ToText $event.item.command
        if ([string]::IsNullOrWhiteSpace($commandText)) {
            continue
        }

        $ok = $event.item.exit_code -eq 0
        $records.Add([pscustomobject]@{
                command = $commandText
                ok = $ok
            })
    }

    return $records.ToArray()
}

function Get-ClaudeCommandRecords {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $events = Read-JsonLines -TranscriptPath $TranscriptPath
    $toolUseCommandsById = @{}
    $records = New-Object System.Collections.Generic.List[object]

    foreach ($event in $events) {
        if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($content in $event.message.content) {
                if ($content.type -eq "tool_use" -and $content.name -eq "Bash") {
                    $toolUseId = Convert-ToText $content.id
                    $commandText = Convert-ToText $content.input.command
                    if (-not [string]::IsNullOrWhiteSpace($toolUseId) -and -not [string]::IsNullOrWhiteSpace($commandText)) {
                        $toolUseCommandsById[$toolUseId] = $commandText
                    }
                }
            }
            continue
        }

        if ($event.type -ne "user" -or $null -eq $event.message -or $null -eq $event.message.content) {
            continue
        }

        foreach ($content in $event.message.content) {
            if ($content.type -ne "tool_result") {
                continue
            }

            $toolUseId = Convert-ToText $content.tool_use_id
            if ([string]::IsNullOrWhiteSpace($toolUseId) -or -not $toolUseCommandsById.ContainsKey($toolUseId)) {
                continue
            }

            $commandText = Convert-ToText $toolUseCommandsById[$toolUseId]
            $resultText = Convert-ToText $content.content

            $exitCode = $null
            if ($resultText -match "Exit code\s+(-?\d+)") {
                $exitCode = [int]$Matches[1]
            }

            $ok = if ($null -eq $exitCode) { -not [bool]$content.is_error } else { $exitCode -eq 0 }
            $records.Add([pscustomobject]@{
                    command = $commandText
                    ok = $ok
                })
        }
    }

    return $records.ToArray()
}

function Get-CommandRecords {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TranscriptPath
    )

    if ($Agent -eq "codex") {
        return Get-CodexCommandRecords -TranscriptPath $TranscriptPath
    }

    return Get-ClaudeCommandRecords -TranscriptPath $TranscriptPath
}

function Get-TokenMetrics {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TranscriptPath
    )

    $promptTokens = $null
    $completionTokens = $null
    $totalTokens = $null
    $cachedInputTokens = $null
    $cacheReadInputTokens = $null
    $cacheCreationInputTokens = $null
    $events = Read-JsonLines -TranscriptPath $TranscriptPath

    if ($Agent -eq "codex") {
        $input = 0
        $output = 0
        $cached = 0

        foreach ($event in $events) {
            if ($event.type -ne "turn.completed" -or $null -eq $event.usage) {
                continue
            }

            if ($null -ne $event.usage.input_tokens) {
                $input += [int]$event.usage.input_tokens
            }

            if ($null -ne $event.usage.output_tokens) {
                $output += [int]$event.usage.output_tokens
            }

            if ($null -ne $event.usage.cached_input_tokens) {
                $cached += [int]$event.usage.cached_input_tokens
            }
        }

        $promptTokens = $input
        $completionTokens = $output
        $totalTokens = $input + $output
        $cachedInputTokens = $cached
    } else {
        foreach ($event in $events) {
            if ($event.type -ne "result" -or $null -eq $event.usage) {
                continue
            }

            if ($null -ne $event.usage.input_tokens) {
                $promptTokens = [int]$event.usage.input_tokens
            }

            if ($null -ne $event.usage.output_tokens) {
                $completionTokens = [int]$event.usage.output_tokens
            }

            if ($null -ne $event.usage.cache_read_input_tokens) {
                $cacheReadInputTokens = [int]$event.usage.cache_read_input_tokens
            }

            if ($null -ne $event.usage.cache_creation_input_tokens) {
                $cacheCreationInputTokens = [int]$event.usage.cache_creation_input_tokens
            }
        }

        if ($null -ne $promptTokens -and $null -ne $completionTokens) {
            $totalTokens = [int]$promptTokens + [int]$completionTokens
        }
    }

    return [pscustomobject]@{
        prompt_tokens = $promptTokens
        completion_tokens = $completionTokens
        total_tokens = $totalTokens
        cached_input_tokens = $cachedInputTokens
        cache_read_input_tokens = $cacheReadInputTokens
        cache_creation_input_tokens = $cacheCreationInputTokens
    }
}

function Get-TokenAttribution {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TranscriptPath
    )

    $events = Read-JsonLines -TranscriptPath $TranscriptPath
    $turns = 0
    $commandRoundTrips = 0
    $roslynCommandRoundTrips = 0
    $commandOutputChars = 0
    $agentMessageChars = 0

    foreach ($event in $events) {
        if ($Agent -eq "codex") {
            if ($event.type -eq "turn.completed") {
                $turns++
            }

            if ($event.type -eq "item.completed" -and $null -ne $event.item) {
                if ($event.item.type -eq "command_execution") {
                    $commandRoundTrips++
                    $commandText = Convert-ToText $event.item.command
                    $roslynIds = @(Get-RoslynCommandIds -CommandText $commandText)
                    if (-not [string]::IsNullOrWhiteSpace($commandText) -and $roslynIds.Count -gt 0) {
                        $roslynCommandRoundTrips++
                    }

                    $aggregated = Convert-ToText $event.item.aggregated_output
                    if (-not [string]::IsNullOrWhiteSpace($aggregated)) {
                        $commandOutputChars += $aggregated.Length
                    }
                } elseif ($event.item.type -eq "agent_message") {
                    $messageText = Convert-ToText $event.item.text
                    if (-not [string]::IsNullOrWhiteSpace($messageText)) {
                        $agentMessageChars += $messageText.Length
                    }
                }
            }
        } else {
            if ($event.type -eq "result") {
                $turns++
            }

            if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
                foreach ($content in $event.message.content) {
                    if ($content.type -eq "tool_use" -and $content.name -eq "Bash") {
                        $commandRoundTrips++
                        $commandText = Convert-ToText $content.input.command
                        $roslynIds = @(Get-RoslynCommandIds -CommandText $commandText)
                        if (-not [string]::IsNullOrWhiteSpace($commandText) -and $roslynIds.Count -gt 0) {
                            $roslynCommandRoundTrips++
                        }
                    } elseif ($content.type -eq "text") {
                        $text = Convert-ToText $content.text
                        if (-not [string]::IsNullOrWhiteSpace($text)) {
                            $agentMessageChars += $text.Length
                        }
                    }
                }
            }

            if ($event.type -eq "user" -and $null -ne $event.message -and $null -ne $event.message.content) {
                foreach ($content in $event.message.content) {
                    if ($content.type -ne "tool_result") {
                        continue
                    }

                    $toolOutputText = Convert-ToText $content.content
                    if (-not [string]::IsNullOrWhiteSpace($toolOutputText)) {
                        $commandOutputChars += $toolOutputText.Length
                    }
                }
            }
        }
    }

    return [pscustomobject]@{
        turns = $turns
        command_round_trips = $commandRoundTrips
        roslyn_command_round_trips = $roslynCommandRoundTrips
        non_roslyn_command_round_trips = [Math]::Max(0, ($commandRoundTrips - $roslynCommandRoundTrips))
        command_output_chars = $commandOutputChars
        agent_message_chars = $agentMessageChars
    }
}

function Get-RunSummaryText {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TranscriptPath
    )

    $events = Read-JsonLines -TranscriptPath $TranscriptPath
    if ($Agent -eq "codex") {
        for ($i = $events.Length - 1; $i -ge 0; $i--) {
            $event = $events[$i]
            if ($event.type -eq "item.completed" -and $null -ne $event.item -and $event.item.type -eq "agent_message") {
                $text = Convert-ToText $event.item.text
                if (-not [string]::IsNullOrWhiteSpace($text)) {
                    return $text
                }
            }
        }
    } else {
        for ($i = $events.Length - 1; $i -ge 0; $i--) {
            $event = $events[$i]
            if ($event.type -eq "result") {
                $text = Convert-ToText $event.result
                if (-not [string]::IsNullOrWhiteSpace($text)) {
                    return $text
                }
            }

            if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
                foreach ($content in $event.message.content) {
                    if ($content.type -eq "text") {
                        $text = Convert-ToText $content.text
                        if (-not [string]::IsNullOrWhiteSpace($text)) {
                            return $text
                        }
                    }
                }
            }
        }
    }

    return ""
}

function Get-RoslynCommandIds {
    param([Parameter(Mandatory = $true)][string]$CommandText)

    $ids = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    $regex = [regex]"\b(nav|ctx|diag|edit|repair|session)\.[a-zA-Z_][\w\.]*"
    foreach ($match in $regex.Matches($CommandText)) {
        [void]$ids.Add($match.Value)
    }

    if ($CommandText -match "roslyn-find-symbol\.ps1") {
        [void]$ids.Add("nav.find_symbol")
    }

    if ($CommandText -match "roslyn-rename-symbol\.ps1") {
        [void]$ids.Add("edit.rename_symbol")
    }

    if ($CommandText -match "roslyn-list-commands\.ps1") {
        [void]$ids.Add("roslyn-agent.run")
    }

    if ($CommandText -match "roscli(\.cmd)?\s+run\s+([a-z]+\.[a-zA-Z_][\w\.]*)") {
        [void]$ids.Add($Matches[2])
    }

    if ($CommandText -match "roscli(\.cmd)?\s+([a-z]+\.[a-zA-Z_][\w\.]*)") {
        [void]$ids.Add($Matches[2])
    }

    if ($CommandText -match "roscli(\.cmd)?\s+list-commands") {
        [void]$ids.Add("cli.list_commands")
    }

    if ($CommandText -match "roscli(\.cmd)?\s+describe-command") {
        [void]$ids.Add("cli.describe_command")
    }

    if ($CommandText -match "RoslynAgent\.Cli") {
        [void]$ids.Add("roslyn-agent.run")
    }

    # HashSet<T>.ToArray() depends on LINQ extension methods that are not always
    # available in Windows PowerShell; copy into List<T> then return a true string[].
    $results = New-Object System.Collections.Generic.List[string]
    foreach ($id in $ids) {
        [void]$results.Add($id)
    }

    return $results.ToArray()
}

function Get-RoslynToolsOffered {
    $tools = @(
        "roslyn-agent.run",
        "cli.list_commands",
        "cli.describe_command",
        "nav.find_symbol",
        "nav.find_references",
        "nav.find_implementations",
        "nav.find_overrides",
        "ctx.file_outline",
        "ctx.member_source",
        "ctx.symbol_envelope",
        "ctx.dependency_slice",
        "ctx.call_chain_slice",
        "diag.get_solution_snapshot",
        "diag.get_file_diagnostics",
        "diag.get_after_edit",
        "diag.diff",
        "edit.add_member",
        "edit.rename_symbol",
        "edit.replace_member_body",
        "edit.change_signature",
        "edit.update_usings",
        "edit.apply_code_fix",
        "edit.transaction",
        "repair.propose_from_diagnostics",
        "repair.apply_plan",
        "session.open",
        "session.status",
        "session.set_content",
        "session.apply_text_edits",
        "session.apply_and_commit",
        "session.get_diagnostics",
        "session.diff",
        "session.commit",
        "session.close"
    )

    return @($tools | Sort-Object -Unique)
}

function Test-IsSearchCommand {
    param([Parameter(Mandatory = $true)][string]$CommandText)
    return ($CommandText -match "(^|\s)(rg|grep|findstr)(\s|$)")
}

function Add-ToolCall {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Map,
        [Parameter(Mandatory = $true)][string]$ToolName,
        [Parameter(Mandatory = $true)][bool]$Ok
    )

    if (-not $Map.ContainsKey($ToolName)) {
        $Map[$ToolName] = $Ok
        return
    }

    $Map[$ToolName] = [bool]$Map[$ToolName] -or $Ok
}

function Get-ToolTelemetry {
    param(
        [Parameter(Mandatory = $true)][object[]]$CommandRecords,
        [Parameter(Mandatory = $true)][bool]$DiffHasChanges,
        [Parameter(Mandatory = $true)][bool]$RoslynCondition
    )

    $records = @($CommandRecords)
    $toolMap = @{}
    $roslynIndicators = New-Object System.Collections.Generic.List[string]
    $roslynIdSet = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    $roslynSuccessfulCalls = 0
    $searchUsed = $false

    foreach ($record in $records) {
        $commandText = Convert-ToText $record.command
        $ok = [bool]$record.ok
        if ([string]::IsNullOrWhiteSpace($commandText)) {
            continue
        }

        $roslynIds = @(Get-RoslynCommandIds -CommandText $commandText)
        if ($roslynIds.Count -gt 0) {
            $roslynIndicators.Add($commandText)
            foreach ($id in $roslynIds) {
                [void]$roslynIdSet.Add($id)
                Add-ToolCall -Map $toolMap -ToolName $id -Ok $ok
            }

            if ($ok) {
                $roslynSuccessfulCalls++
            }
        }

        if (Test-IsSearchCommand -CommandText $commandText) {
            $searchUsed = $true
        }
    }

    if ($records.Count -gt 0) {
        Add-ToolCall -Map $toolMap -ToolName "run_shell" -Ok $true
    }

    if ($DiffHasChanges) {
        Add-ToolCall -Map $toolMap -ToolName "text_editing" -Ok $true
    }

    if ($searchUsed) {
        Add-ToolCall -Map $toolMap -ToolName "search" -Ok $true
    }

    $roslynUsed = $roslynIdSet.Count -gt 0
    if ($roslynUsed -and -not $toolMap.ContainsKey("roslyn-agent.run")) {
        Add-ToolCall -Map $toolMap -ToolName "roslyn-agent.run" -Ok ($roslynSuccessfulCalls -gt 0)
    }

    $toolsOffered = @("run_shell", "search", "text_editing")
    if ($RoslynCondition) {
        $toolsOffered += @(Get-RoslynToolsOffered)
    }
    $toolsOffered = @($toolsOffered | Sort-Object -Unique)

    $toolCalls = New-Object System.Collections.Generic.List[object]
    foreach ($name in ($toolMap.Keys | Sort-Object)) {
        $toolCalls.Add([ordered]@{
                tool_name = $name
                ok = [bool]$toolMap[$name]
            })
    }

    return [pscustomobject]@{
        tools_offered = $toolsOffered
        tool_calls = $toolCalls.ToArray()
        roslyn_used = $roslynUsed
        roslyn_successful_calls = $roslynSuccessfulCalls
        roslyn_usage_indicators = ($roslynIndicators | Select-Object -Unique)
    }
}

function Get-TranscriptFragments {
    param(
        [Parameter(Mandatory = $true)][object[]]$CommandRecords,
        [Parameter(Mandatory = $true)][string]$SummaryText
    )

    $fragments = New-Object System.Collections.Generic.List[string]
    foreach ($record in ($CommandRecords | Select-Object -First 4)) {
        $commandText = Convert-ToText $record.command
        if (-not [string]::IsNullOrWhiteSpace($commandText)) {
            $fragments.Add("Ran $commandText")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($SummaryText)) {
        $shortSummary = $SummaryText
        if ($shortSummary.Length -gt 240) {
            $shortSummary = $shortSummary.Substring(0, 240)
        }
        $fragments.Add($shortSummary)
    }

    return $fragments.ToArray()
}

function Invoke-AcceptanceChecks {
    param(
        [Parameter(Mandatory = $true)][string[]]$Commands,
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$TaskDirectory,
        [Parameter(Mandatory = $true)][bool]$SkipChecks
    )

    $results = New-Object System.Collections.Generic.List[object]
    if ($SkipChecks) {
        foreach ($command in $Commands) {
            $results.Add([ordered]@{
                    command = $command
                    exit_code = $null
                    passed = $null
                    skipped = $true
                    log_path = $null
                })
        }

        return [pscustomobject]@{
            all_passed = $true
            checks = $results.ToArray()
            ran_checks = $false
        }
    }

    $allPassed = $true
    for ($i = 0; $i -lt $Commands.Length; $i++) {
        $command = $Commands[$i]
        $logPath = Join-Path $TaskDirectory ("acceptance-{0:D2}.log" -f ($i + 1))
        $exitCode = Invoke-LoggedCommand -CommandText $command -WorkingDirectory $WorkspacePath -LogPath $logPath
        $passed = ($exitCode -eq 0)
        if (-not $passed) {
            $allPassed = $false
        }

        $results.Add([ordered]@{
                command = $command
                exit_code = $exitCode
                passed = $passed
                skipped = $false
                log_path = (Resolve-Path $logPath).Path
            })
    }

    return [pscustomobject]@{
        all_passed = $allPassed
        checks = $results.ToArray()
        ran_checks = $true
    }
}

function Invoke-SetupCommands {
    param(
        [Parameter(Mandatory = $true)][string[]]$Commands,
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$TaskDirectory
    )

    $results = New-Object System.Collections.Generic.List[object]
    $allPassed = $true

    for ($i = 0; $i -lt $Commands.Length; $i++) {
        $command = Convert-ToText $Commands[$i]
        if ([string]::IsNullOrWhiteSpace($command)) {
            continue
        }

        $logPath = Join-Path $TaskDirectory ("setup-{0:D2}.log" -f ($i + 1))
        $exitCode = Invoke-LoggedCommand -CommandText $command -WorkingDirectory $WorkspacePath -LogPath $logPath
        $passed = ($exitCode -eq 0)
        if (-not $passed) {
            $allPassed = $false
        }

        $results.Add([ordered]@{
                command = $command
                exit_code = $exitCode
                passed = $passed
                log_path = (Resolve-Path $logPath).Path
            })

        if (-not $passed) {
            break
        }
    }

    return [pscustomobject]@{
        all_passed = $allPassed
        checks = $results.ToArray()
    }
}

function Get-DiffText {
    param([Parameter(Mandatory = $true)][string]$WorkspacePath)
    $diffText = & git -C $WorkspacePath --no-pager diff --no-color HEAD
    return Convert-ToText $diffText
}

function Commit-WorkspaceState {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $status = Convert-ToText (& git -C $WorkspacePath status --porcelain)
    if ([string]::IsNullOrWhiteSpace($status)) {
        return $false
    }

    & git -C $WorkspacePath add -A | Out-Null
    & git -C $WorkspacePath commit --no-gpg-sign -m $Message | Out-Null
    return $LASTEXITCODE -eq 0
}

function Get-RoslynProfileInstructionBlock {
    param([Parameter(Mandatory = $true)][string]$RoslynGuidanceProfile)

    switch ($RoslynGuidanceProfile) {
        "brief-first" {
            return @"
- Default to compact calls first: use `--brief true` for `nav.find_symbol`, `ctx.member_source`, and `diag.get_solution_snapshot`.
- Escalate to richer payloads (`--brief false` or explicit include flags) only when compact data is insufficient.
"@
        }
        "verbose-first" {
            return @"
- Default to rich payloads first: use full detail mode (omit `--brief` or use `--brief false`) for key Roslyn calls.
- Drop to compact payloads only when token/latency pressure becomes significant.
"@
        }
        default {
            return @"
- Keep Roslyn payloads scoped to the smallest output needed; avoid unnecessary large context dumps.
"@
        }
    }
}

function Build-ConditionPrompt {
    param(
        [Parameter(Mandatory = $true)][object]$Condition,
        [Parameter(Mandatory = $true)][string]$TaskPrompt,
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$RoslynGuidanceProfile
    )

    $conditionId = Convert-ToText (Get-ObjectPropertyValue -Object $Condition -PropertyName "id")
    $conditionName = Convert-ToText (Get-ObjectPropertyValue -Object $Condition -PropertyName "name")
    $conditionNotes = Convert-ToText (Get-ObjectPropertyValue -Object $Condition -PropertyName "notes")
    $conditionInstructionPreamble = Convert-ToText (Get-ObjectPropertyValue -Object $Condition -PropertyName "instruction_preamble")
    $conditionMode = Get-ConditionMode -Condition $Condition

    if ($conditionMode -eq "control") {
        $extraControlGuidance = ""
        if (-not [string]::IsNullOrWhiteSpace($conditionInstructionPreamble)) {
            $extraControlGuidance = @"
$conditionInstructionPreamble

"@
        }

        return @"
Benchmark condition: CONTROL (text-first)
Condition id: $conditionId
Condition name: $conditionName
Working directory: $WorkspacePath

Rules:
- Do NOT invoke Roslyn-specific commands or wrappers.
- Prohibited examples:
  - scripts/roscli*
  - dotnet run --project src/RoslynAgent.Cli ...
  - nav.*, ctx.*, diag.*, edit.*, repair.*, session.* command invocations
- Use regular text navigation/editing and shell tools only.
- Tasks are sequential in one workspace; keep previous task outputs intact.
$extraControlGuidance
Condition notes:
$conditionNotes

Task:
$TaskPrompt

At the end, provide:
1) changed files
2) tests/checks you ran
3) any open issues or risks
"@
    }

    $profileGuidance = Get-RoslynProfileInstructionBlock -RoslynGuidanceProfile $RoslynGuidanceProfile
    $extraTreatmentGuidance = ""
    if (-not [string]::IsNullOrWhiteSpace($conditionInstructionPreamble)) {
        $extraTreatmentGuidance = @"
$conditionInstructionPreamble
"@
    }

    return @"
Benchmark condition: TREATMENT (Roslyn-enabled)
Condition id: $conditionId
Condition name: $conditionName
Working directory: $WorkspacePath

Rules:
- Prefer Roslyn tooling for C# exploration and edits.
- Start by listing commands:
  scripts\roscli.cmd list-commands
- Prefer shorthand commands for simple calls, for example:
  - scripts\roscli.cmd ctx.file_outline src/Some/File.cs
  - scripts\roscli.cmd ctx.member_source src/Some/File.cs 120 12 member
  - scripts\roscli.cmd nav.find_symbol src/Some/File.cs SymbolName
- Use `run <command-id> --input ...` for structured/multi-field payloads.
- Prefer nav.*, ctx.*, diag.*, edit.*, repair.*, session.* where useful before text-only fallbacks.
- Keep stateful `session.*` operations strictly sequential. Do not run `session.commit` and `session.close` in parallel.
- Prefer `session.commit` as the terminal step (it can close the session unless explicitly kept open).
- Guidance profile: $RoslynGuidanceProfile
$profileGuidance
- Condition-specific guidance:
$extraTreatmentGuidance
- Tasks are sequential in one workspace; keep previous task outputs intact.

Condition notes:
$conditionNotes

Task:
$TaskPrompt

At the end, provide:
1) changed files
2) tests/checks you ran
3) whether Roslyn commands were helpful
4) any open issues or risks
"@
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$hostStartCwd = (Resolve-Path ".").Path
$hostStartGitRootRaw = Convert-ToText (& git -C $hostStartCwd rev-parse --show-toplevel 2>$null)
if ([string]::IsNullOrWhiteSpace($hostStartGitRootRaw)) {
    throw "Benchmark harness must be launched from within a git working tree."
}

$hostStartGitRoot = (Resolve-Path $hostStartGitRootRaw).Path
Assert-HostSessionAnchor -ExpectedCwd $hostStartCwd -ExpectedGitRoot $hostStartGitRoot -Phase "script.start"
if (-not [string]::Equals((Get-NormalizedPath -Path $hostStartGitRoot), (Get-NormalizedPath -Path $repoRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Host git root '$hostStartGitRoot' does not match harness repo root '$repoRoot'."
}

$manifestFullPath = (Resolve-Path $ManifestPath).Path
$manifestDirectory = Split-Path -Parent $manifestFullPath
$manifest = Get-Content -Path $manifestFullPath -Raw | ConvertFrom-Json

$selectedTasks = @($manifest.tasks)
if ($TaskIds.Count -gt 0) {
    $taskIdSet = $TaskIds | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $selectedTasks = @($manifest.tasks | Where-Object { $taskIdSet -contains $_.id })
    if ($selectedTasks.Count -eq 0) {
        throw "No tasks matched TaskIds filter."
    }
}

$selectedConditions = New-Object System.Collections.Generic.List[object]
if ($ConditionIds.Count -gt 0) {
    $requestedConditionIds = @(
        $ConditionIds |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    foreach ($requestedId in $requestedConditionIds) {
        $matches = @($manifest.conditions | Where-Object { (Convert-ToText $_.id) -eq $requestedId })
        if ($matches.Count -eq 0) {
            throw "Requested condition id '$requestedId' was not found in manifest."
        }

        $selectedConditions.Add($matches[0])
    }
} else {
    if (-not $SkipControl) {
        $control = @($manifest.conditions | Where-Object { (Convert-ToText $_.id) -eq "control-text-only" } | Select-Object -First 1)
        if ($control.Count -eq 0) {
            $control = @($manifest.conditions | Where-Object { -not [bool]$_.roslyn_tools_enabled } | Select-Object -First 1)
        }
        if ($control.Count -eq 1) {
            $selectedConditions.Add($control[0])
        }
    }
    if (-not $SkipTreatment) {
        $treatment = @($manifest.conditions | Where-Object { (Convert-ToText $_.id) -eq "treatment-roslyn-optional" } | Select-Object -First 1)
        if ($treatment.Count -eq 0) {
            $alreadySelectedIds = @($selectedConditions | ForEach-Object { Convert-ToText $_.id })
            $treatment = @(
                $manifest.conditions |
                Where-Object { [bool]$_.roslyn_tools_enabled -and ($alreadySelectedIds -notcontains (Convert-ToText $_.id)) } |
                Select-Object -First 1
            )
        }
        if ($treatment.Count -eq 1) {
            $selectedConditions.Add($treatment[0])
        }
    }
}

if ($selectedConditions.Count -eq 0) {
    throw "No benchmark conditions selected."
}

$agents = New-Object System.Collections.Generic.List[string]
if (-not $SkipCodex) {
    $agents.Add("codex")
}
if (-not $SkipClaude) {
    $agents.Add("claude")
}

if ($agents.Count -eq 0) {
    throw "No agents selected."
}

Ensure-Command -CommandName "git"
Ensure-Command -CommandName "dotnet"
if ($UseWorkingTreeOverlay) {
    Ensure-Command -CommandName "robocopy"
}
if ($agents -contains "codex") {
    Ensure-Command -CommandName "codex"
}
if ($agents -contains "claude") {
    Ensure-Command -CommandName "claude"
}

$outputDirectory = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot
$trajectoriesDirectory = Join-Path $outputDirectory "trajectories"
$runsDirectory = Join-Path $outputDirectory "runs"
$gateDirectory = Join-Path $outputDirectory "gate"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $trajectoriesDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $runsDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $gateDirectory | Out-Null

$baselineCommit = ""
if ($selectedTasks.Count -gt 0) {
    $baselineCommit = Convert-ToText $selectedTasks[0].commit
}
if ([string]::IsNullOrWhiteSpace($baselineCommit)) {
    $baselineCommit = Convert-ToText (& git -C $repoRoot rev-parse HEAD)
}

$runSummaryRows = New-Object System.Collections.Generic.List[object]

foreach ($agent in $agents) {
    foreach ($condition in $selectedConditions) {
        $conditionId = Convert-ToText $condition.id
        $conditionSlug = Convert-ToSlug $conditionId
        $mode = Get-ConditionMode -Condition $condition
        $trajectoryName = "{0}-{1}-{2}" -f $agent, $mode, $conditionSlug
        $trajectoryDirectory = Join-Path $trajectoriesDirectory $trajectoryName
        $workspacePath = Join-Path $trajectoryDirectory "workspace"
        New-Item -ItemType Directory -Force -Path $trajectoryDirectory | Out-Null

        New-TrajectoryWorkspace `
            -RepoRoot $repoRoot `
            -WorkspacePath $workspacePath `
            -BaselineCommit $baselineCommit `
            -EnableRoslynShim ([bool]$condition.roslyn_tools_enabled) `
            -UseWorkingTreeOverlay ([bool]$UseWorkingTreeOverlay)

        for ($taskIndex = 0; $taskIndex -lt $selectedTasks.Count; $taskIndex++) {
            $task = $selectedTasks[$taskIndex]
            $taskId = Convert-ToText $task.id
            $taskTitle = Convert-ToText $task.title
            $taskPromptRelative = Convert-ToText $task.task_prompt_file
            $taskPromptPath = Join-Path $manifestDirectory $taskPromptRelative
            $taskPromptText = Get-Content -Path $taskPromptPath -Raw
            $fullPrompt = Build-ConditionPrompt `
                -Condition $condition `
                -TaskPrompt $taskPromptText `
                -WorkspacePath $workspacePath `
                -RoslynGuidanceProfile $RoslynGuidanceProfile

            $taskDirectory = Join-Path $trajectoryDirectory ("{0:D2}-{1}" -f ($taskIndex + 1), $taskId)
            New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null

            Assert-HostSessionAnchor -ExpectedCwd $hostStartCwd -ExpectedGitRoot $hostStartGitRoot -Phase ("pre-task/{0}/{1}/{2}" -f $agent, $conditionId, $taskId)

            $promptPath = Join-Path $taskDirectory "prompt.txt"
            $transcriptPath = Join-Path $taskDirectory "transcript.jsonl"
            $diffPath = Join-Path $taskDirectory "diff.patch"
            $taskMetadataPath = Join-Path $taskDirectory "run-metadata.json"
            Set-Content -Path $promptPath -Value $fullPrompt -NoNewline
            Set-Content -Path $transcriptPath -Value "" -NoNewline
            $agentEnvironmentOverrides = New-AgentEnvironmentOverrides -Agent $agent -RunDirectory $taskDirectory

            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

            $setupCommands = @($task.setup_commands)
            $setupResult = Invoke-SetupCommands `
                -Commands $setupCommands `
                -WorkspacePath $workspacePath `
                -TaskDirectory $taskDirectory

            $agentExitCode = 1
            if (-not [bool]$setupResult.all_passed) {
                Add-Content -Path $transcriptPath -Value '{"type":"setup.failed","message":"Setup commands failed. Skipping agent execution for this task."}'
            } elseif ($agent -eq "codex") {
                $codexExecutable = "codex.cmd"
                if (-not (Get-Command $codexExecutable -ErrorAction SilentlyContinue)) {
                    $codexExecutable = "codex"
                }

                $agentArgs = @(
                    "exec",
                    "--json",
                    "--dangerously-bypass-approvals-and-sandbox",
                    "--skip-git-repo-check",
                    "-"
                )
                if (-not [string]::IsNullOrWhiteSpace($CodexModel)) {
                    $agentArgs = @(
                        "exec",
                        "--json",
                        "--dangerously-bypass-approvals-and-sandbox",
                        "--skip-git-repo-check",
                        "--model",
                        $CodexModel,
                        "-"
                    )
                }

                $agentExitCode = Invoke-AgentProcess `
                    -Executable $codexExecutable `
                    -Arguments $agentArgs `
                    -PromptText $fullPrompt `
                    -TranscriptPath $transcriptPath `
                    -WorkingDirectory $workspacePath `
                    -EnvironmentOverrides $agentEnvironmentOverrides
            } else {
                $claudeExecutable = "claude.cmd"
                if (-not (Get-Command $claudeExecutable -ErrorAction SilentlyContinue)) {
                    $claudeExecutable = "claude"
                }

                $agentArgs = @(
                    "--print",
                    "--output-format",
                    "stream-json",
                    "--verbose",
                    "--input-format",
                    "text",
                    "--permission-mode",
                    "bypassPermissions",
                    "--dangerously-skip-permissions"
                )
                if (-not [string]::IsNullOrWhiteSpace($ClaudeModel)) {
                    $agentArgs += @("--model", $ClaudeModel)
                }

                $agentExitCode = Invoke-AgentProcess `
                    -Executable $claudeExecutable `
                    -Arguments $agentArgs `
                    -PromptText $fullPrompt `
                    -TranscriptPath $transcriptPath `
                    -WorkingDirectory $workspacePath `
                    -EnvironmentOverrides $agentEnvironmentOverrides
            }
            Assert-HostSessionAnchor -ExpectedCwd $hostStartCwd -ExpectedGitRoot $hostStartGitRoot -Phase ("post-agent/{0}/{1}/{2}" -f $agent, $conditionId, $taskId)

            $acceptanceChecks = @($task.acceptance_checks)
            $acceptanceResult = Invoke-AcceptanceChecks `
                -Commands $acceptanceChecks `
                -WorkspacePath $workspacePath `
                -TaskDirectory $taskDirectory `
                -SkipChecks ([bool]$NoAcceptanceChecks)
            Assert-HostSessionAnchor -ExpectedCwd $hostStartCwd -ExpectedGitRoot $hostStartGitRoot -Phase ("post-acceptance/{0}/{1}/{2}" -f $agent, $conditionId, $taskId)

            $stopwatch.Stop()
            $durationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)

            $diffText = Get-DiffText -WorkspacePath $workspacePath
            Set-Content -Path $diffPath -Value $diffText
            $diffHasChanges = -not [string]::IsNullOrWhiteSpace($diffText)

            $commandRecords = Get-CommandRecords -Agent $agent -TranscriptPath $transcriptPath
            $tokenMetrics = Get-TokenMetrics -Agent $agent -TranscriptPath $transcriptPath
            $tokenAttribution = Get-TokenAttribution -Agent $agent -TranscriptPath $transcriptPath
            $summaryText = Get-RunSummaryText -Agent $agent -TranscriptPath $transcriptPath
            $toolTelemetry = Get-ToolTelemetry `
                -CommandRecords $commandRecords `
                -DiffHasChanges $diffHasChanges `
                -RoslynCondition ([bool]$condition.roslyn_tools_enabled)
            $fragments = Get-TranscriptFragments -CommandRecords $commandRecords -SummaryText $summaryText

            $testsPassed = [bool]$acceptanceResult.all_passed
            $compilePassed = $testsPassed
            $setupPassed = [bool]$setupResult.all_passed
            $succeeded = ($setupPassed -and $agentExitCode -eq 0 -and $testsPassed)

            $roslynHelpfulnessScore = $null
            if ([bool]$condition.roslyn_tools_enabled -and [bool]$toolTelemetry.roslyn_used) {
                $roslynHelpfulnessScore = if ($succeeded) { 4 } else { 3 }
            }

            $reflectionSummary = $summaryText
            if ([string]::IsNullOrWhiteSpace($reflectionSummary)) {
                $reflectionSummary = if ($succeeded) {
                    "Task completed with captured transcript and acceptance results."
                } else {
                    "Run ended with failures; see transcript and acceptance logs for details."
                }
            }

            $helpfulTools = @()
            if ([bool]$toolTelemetry.roslyn_used) {
                $helpfulTools = @(
                    $toolTelemetry.tool_calls |
                    ForEach-Object {
                        if ($_ -is [hashtable]) { $_["tool_name"] } else { $_.tool_name }
                    } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and ($_ -match "^(nav|ctx|diag|edit|repair|session)\.") } |
                    Select-Object -Unique
                )
            }
            if ($helpfulTools.Count -eq 0 -and $diffHasChanges) {
                $helpfulTools = @("text_editing")
            }

            $runId = "run-{0}-{1}-{2}-{3}-r01" -f $agent, $mode, $conditionSlug, $taskId
            $agentLabel = if ($agent -eq "codex") { "codex-cli" } else { "claude-code" }
            $modelLabel = if ($agent -eq "codex") {
                if (-not [string]::IsNullOrWhiteSpace($CodexModel)) { $CodexModel } else { "codex-default" }
            } else {
                if (-not [string]::IsNullOrWhiteSpace($ClaudeModel)) { $ClaudeModel } else { "claude-default" }
            }

            $runRecord = [ordered]@{
                run_id = $runId
                task_id = $taskId
                condition_id = $conditionId
                replicate = 1
                agent = $agentLabel
                model = $modelLabel
                succeeded = $succeeded
                compile_passed = $compilePassed
                tests_passed = $testsPassed
                duration_seconds = $durationSeconds
                prompt_tokens = $tokenMetrics.prompt_tokens
                completion_tokens = $tokenMetrics.completion_tokens
                total_tokens = $tokenMetrics.total_tokens
                cached_input_tokens = $tokenMetrics.cached_input_tokens
                cache_read_input_tokens = $tokenMetrics.cache_read_input_tokens
                cache_creation_input_tokens = $tokenMetrics.cache_creation_input_tokens
                token_attribution = $tokenAttribution
                tools_offered = @($toolTelemetry.tools_offered)
                tool_calls = @($toolTelemetry.tool_calls)
                context = [ordered]@{
                    task_title = $taskTitle
                    repo = (Convert-ToText $task.repo)
                    repo_url = (Convert-ToText $task.repo_url)
                    commit = $baselineCommit
                    workspace_source = if ($UseWorkingTreeOverlay) { "working-tree-overlay" } else { "baseline-commit" }
                    setup_checks = $setupCommands
                    acceptance_checks = $acceptanceChecks
                    task_prompt_file = $taskPromptRelative
                }
                post_run_reflection = [ordered]@{
                    summary = $reflectionSummary
                    helpful_tools = $helpfulTools
                    unhelpful_tools = @()
                    roslyn_helpfulness_score = $roslynHelpfulnessScore
                }
                transcript_fragments = $fragments
            }

            $runPath = Join-Path $runsDirectory "$runId.json"
            $runRecord | ConvertTo-Json -Depth 80 | Set-Content -Path $runPath

            $taskMetadata = [ordered]@{
                run_id = $runId
                agent = $agent
                mode = $mode
                task_id = $taskId
                task_title = $taskTitle
                condition_id = $conditionId
                condition_name = (Convert-ToText $condition.name)
                prompt_path = (Resolve-Path $promptPath).Path
                transcript_path = (Resolve-Path $transcriptPath).Path
                diff_path = (Resolve-Path $diffPath).Path
                workspace_path = (Resolve-Path $workspacePath).Path
                agent_exit_code = $agentExitCode
                duration_seconds = $durationSeconds
                diff_has_changes = $diffHasChanges
                roslyn_used = [bool]$toolTelemetry.roslyn_used
                roslyn_successful_calls = [int]$toolTelemetry.roslyn_successful_calls
                roslyn_usage_indicators = @($toolTelemetry.roslyn_usage_indicators)
                setup = $setupResult
                acceptance = $acceptanceResult
                prompt_tokens = $tokenMetrics.prompt_tokens
                completion_tokens = $tokenMetrics.completion_tokens
                total_tokens = $tokenMetrics.total_tokens
                cached_input_tokens = $tokenMetrics.cached_input_tokens
                cache_read_input_tokens = $tokenMetrics.cache_read_input_tokens
                cache_creation_input_tokens = $tokenMetrics.cache_creation_input_tokens
                token_attribution = $tokenAttribution
            }
            $taskMetadata | ConvertTo-Json -Depth 80 | Set-Content -Path $taskMetadataPath

            $committed = Commit-WorkspaceState -WorkspacePath $workspacePath -Message ("benchmark/{0}/{1}/{2}/{3}" -f $agent, $mode, $conditionSlug, $taskId)
            $taskMetadata.committed_for_next_task = $committed
            $taskMetadata | ConvertTo-Json -Depth 80 | Set-Content -Path $taskMetadataPath
            Assert-HostSessionAnchor -ExpectedCwd $hostStartCwd -ExpectedGitRoot $hostStartGitRoot -Phase ("post-commit/{0}/{1}/{2}" -f $agent, $conditionId, $taskId)

            $runSummaryRows.Add([ordered]@{
                    run_id = $runId
                    agent = $agent
                    mode = $mode
                    condition_id = $conditionId
                    task_id = $taskId
                    succeeded = $succeeded
                    setup_passed = $setupPassed
                    compile_passed = $compilePassed
                    tests_passed = $testsPassed
                    total_tokens = $tokenMetrics.total_tokens
                    command_round_trips = $tokenAttribution.command_round_trips
                    roslyn_command_round_trips = $tokenAttribution.roslyn_command_round_trips
                    roslyn_used = [bool]$toolTelemetry.roslyn_used
                    roslyn_successful_calls = [int]$toolTelemetry.roslyn_successful_calls
                    run_path = (Resolve-Path $runPath).Path
                    task_metadata_path = (Resolve-Path $taskMetadataPath).Path
                })

            Write-Host ("RUN {0} exit={1} succeeded={2} tests_passed={3} roslyn_used={4} total_tokens={5} round_trips={6}" -f $runId, $agentExitCode, $succeeded, $testsPassed, $toolTelemetry.roslyn_used, $tokenMetrics.total_tokens, $tokenAttribution.command_round_trips)
        }
    }
}

$realManifestTasks = @()
foreach ($task in $selectedTasks) {
    $taskPromptRelative = Convert-ToText $task.task_prompt_file
    $taskPromptAbsolute = [System.IO.Path]::GetFullPath((Join-Path $manifestDirectory $taskPromptRelative))
    $taskCopy = [ordered]@{}
    foreach ($property in $task.PSObject.Properties) {
        $taskCopy[$property.Name] = $property.Value
    }
    $taskCopy.task_prompt_file = $taskPromptAbsolute
    $realManifestTasks += [pscustomobject]$taskCopy
}

$realManifest = @{
    experiment_id = ("{0}-real-tools" -f (Convert-ToText $manifest.experiment_id))
    description = ("Real-agent trajectory runs from {0} with Codex/Claude control+treatment conditions." -f (Convert-ToText $manifest.experiment_id))
    roslyn_tool_prefixes = @($manifest.roslyn_tool_prefixes | ForEach-Object { $_ })
    runs_per_cell = [int]$agents.Count
    conditions = @($selectedConditions | ForEach-Object { $_ })
    tasks = @($realManifestTasks)
}

$realManifestPath = Join-Path $outputDirectory "manifest.real-tools.json"
$realManifest | ConvertTo-Json -Depth 100 | Set-Content -Path $realManifestPath

$summaryPath = Join-Path $outputDirectory "trajectory-run-summary.json"
$summaryPayload = @{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    manifest_path = (Resolve-Path $realManifestPath).Path
    runs_directory = (Resolve-Path $runsDirectory).Path
    rows = $runSummaryRows
}
$summaryPayload | ConvertTo-Json -Depth 100 | Set-Content -Path $summaryPath

$gateLogPath = Join-Path $gateDirectory "agent-eval-gate.log"
$gateCommand = "dotnet run --project src/RoslynAgent.Benchmark -- agent-eval-gate --manifest `"$realManifestPath`" --runs `"$runsDirectory`" --output `"$gateDirectory`""
$gateExitCode = Invoke-LoggedCommand -CommandText $gateCommand -WorkingDirectory $repoRoot -LogPath $gateLogPath
Assert-HostSessionAnchor -ExpectedCwd $hostStartCwd -ExpectedGitRoot $hostStartGitRoot -Phase "post-gate"

$aggregateByAgentMode = $runSummaryRows | Group-Object agent, mode, condition_id | ForEach-Object {
    $rows = $_.Group
    $tokenValues = @($rows | Where-Object { $null -ne $_.total_tokens } | ForEach-Object { [double]$_.total_tokens })
    [ordered]@{
        key = $_.Name
        run_count = $rows.Count
        succeeded_count = @($rows | Where-Object { $_.succeeded }).Count
        tests_passed_count = @($rows | Where-Object { $_.tests_passed }).Count
        roslyn_used_count = @($rows | Where-Object { $_.roslyn_used }).Count
        total_tokens_sum = if ($tokenValues.Count -eq 0) { $null } else { [Math]::Round(($tokenValues | Measure-Object -Sum).Sum, 2) }
        total_tokens_avg = if ($tokenValues.Count -eq 0) { $null } else { [Math]::Round(($tokenValues | Measure-Object -Average).Average, 2) }
    }
}

$aggregatePath = Join-Path $outputDirectory "trajectory-aggregate.json"
[ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    gate_exit_code = $gateExitCode
    gate_log_path = (Resolve-Path $gateLogPath).Path
    aggregate = $aggregateByAgentMode
} | ConvertTo-Json -Depth 50 | Set-Content -Path $aggregatePath

Write-Host ("OUTPUT_DIR={0}" -f ([System.IO.Path]::GetFullPath($outputDirectory)))
Write-Host ("RUNS_DIR={0}" -f ([System.IO.Path]::GetFullPath($runsDirectory)))
Write-Host ("MANIFEST={0}" -f ([System.IO.Path]::GetFullPath($realManifestPath)))
Write-Host ("SUMMARY={0}" -f ([System.IO.Path]::GetFullPath($summaryPath)))
Write-Host ("AGGREGATE={0}" -f ([System.IO.Path]::GetFullPath($aggregatePath)))
Write-Host ("GATE_EXIT={0}" -f $gateExitCode)
Assert-HostSessionAnchor -ExpectedCwd $hostStartCwd -ExpectedGitRoot $hostStartGitRoot -Phase "script.end"
