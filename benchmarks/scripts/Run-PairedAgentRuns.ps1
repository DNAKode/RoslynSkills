param(
    [string]$OutputRoot = "",
    [string]$IsolationRoot = "",
    [string]$CodexModel = "",
    [string]$ClaudeModel = "",
    [string]$CliPublishConfiguration = "Release",
    [bool]$FailOnControlContamination = $true,
    [switch]$KeepIsolatedWorkspaces,
    [switch]$SkipCodex,
    [switch]$SkipClaude
)

$ErrorActionPreference = "Stop"

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        return (Join-Path $RepoRoot "artifacts/real-agent-runs/$stamp-paired")
    }

    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        return $OutputRoot
    }

    return (Join-Path $RepoRoot $OutputRoot)
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Test-PathIsUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $normalizedPath = (Get-FullPath -Path $Path).TrimEnd('\', '/')
    $normalizedRoot = (Get-FullPath -Path $Root).TrimEnd('\', '/')

    if ($normalizedPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $rootWithSep = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    $rootWithAltSep = $normalizedRoot + [System.IO.Path]::AltDirectorySeparatorChar

    return (
        $normalizedPath.StartsWith($rootWithSep, [System.StringComparison]::OrdinalIgnoreCase) -or
        $normalizedPath.StartsWith($rootWithAltSep, [System.StringComparison]::OrdinalIgnoreCase)
    )
}

function Resolve-IsolationRootDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$IsolationRoot
    )

    if ([string]::IsNullOrWhiteSpace($IsolationRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        $suffix = [Guid]::NewGuid().ToString("n").Substring(0, 8)
        return (Join-Path ([System.IO.Path]::GetTempPath()) "roslyn-agent-paired-runs/$stamp-$suffix")
    }

    if ([System.IO.Path]::IsPathRooted($IsolationRoot)) {
        return $IsolationRoot
    }

    return (Join-Path $RepoRoot $IsolationRoot)
}

function New-IsolatedRunWorkspace {
    param(
        [Parameter(Mandatory = $true)][string]$IsolationRoot,
        [Parameter(Mandatory = $true)][string]$RunId
    )

    $suffix = [Guid]::NewGuid().ToString("n").Substring(0, 8)
    $workspaceDirectory = Join-Path $IsolationRoot ("workspace-{0}-{1}" -f $RunId, $suffix)
    New-Item -ItemType Directory -Force -Path $workspaceDirectory | Out-Null
    return [string](Resolve-Path $workspaceDirectory).Path
}

function Resolve-RepoTopLevel {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedRoot = & git -C $Path rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolvedRoot)) {
        throw "Failed to resolve git top-level for '$Path'."
    }

    return (Get-FullPath -Path $resolvedRoot.Trim())
}

function Assert-IsolatedRunWorkspace {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspaceDirectory,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    if (Test-PathIsUnderRoot -Path $WorkspaceDirectory -Root $RepoRoot) {
        throw "Run workspace '$WorkspaceDirectory' is inside repo root '$RepoRoot'. Isolation requires a workspace outside the repository tree."
    }

    $workspaceRepoTop = & git -C $WorkspaceDirectory rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($workspaceRepoTop)) {
        throw "Run workspace '$WorkspaceDirectory' is inside git repo '$($workspaceRepoTop.Trim())'."
    }
}

function Copy-RunArtifactFiles {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspaceDirectory,
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null

    foreach ($fileName in @(
            "Target.cs",
            "Target.original.cs",
            "prompt.txt",
            "transcript.jsonl",
            "diff.patch",
            "constraint-checks.json"
        )) {
        $sourcePath = Join-Path $WorkspaceDirectory $fileName
        if (Test-Path $sourcePath) {
            Copy-Item -Path $sourcePath -Destination (Join-Path $ArtifactDirectory $fileName) -Force
        }
    }

    $scriptsSource = Join-Path $WorkspaceDirectory "scripts"
    if (Test-Path $scriptsSource) {
        Copy-Item -Path $scriptsSource -Destination (Join-Path $ArtifactDirectory "scripts") -Recurse -Force
    }

    foreach ($helperFile in @(
            "roslyn-list-commands.ps1",
            "roslyn-find-symbol.ps1",
            "roslyn-rename-symbol.ps1",
            "roslyn-rename-and-verify.ps1"
        )) {
        $helperSource = Join-Path $WorkspaceDirectory $helperFile
        if (Test-Path $helperSource) {
            Copy-Item -Path $helperSource -Destination (Join-Path $ArtifactDirectory $helperFile) -Force
        }
    }
}

function Assert-HostContextIntegrity {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedWorkingDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedRepoRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedHead
    )

    $expectedWorkingDirectoryFull = Get-FullPath -Path $ExpectedWorkingDirectory
    $expectedRepoRootFull = Get-FullPath -Path $ExpectedRepoRoot
    $currentLocationFull = Get-FullPath -Path (Get-Location).Path

    if (-not $currentLocationFull.Equals($expectedWorkingDirectoryFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host ("Host location drift detected: '{0}' -> restoring '{1}'." -f $currentLocationFull, $expectedWorkingDirectoryFull)
        Set-Location $expectedWorkingDirectoryFull
    }

    $actualRepoRoot = Resolve-RepoTopLevel -Path $expectedWorkingDirectoryFull
    if (-not $actualRepoRoot.Equals($expectedRepoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host ("Host git root drift detected: '{0}' -> restoring '{1}'." -f $actualRepoRoot, $expectedRepoRootFull)
        Set-Location $expectedRepoRootFull
        $actualRepoRoot = Resolve-RepoTopLevel -Path $expectedRepoRootFull
        if (-not $actualRepoRoot.Equals($expectedRepoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Failed to restore host git repo root to '$expectedRepoRootFull'. Current: '$actualRepoRoot'."
        }
    }

    $actualHead = (& git -C $expectedRepoRootFull rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($actualHead)) {
        throw "Failed to resolve host repo HEAD for '$expectedRepoRootFull'."
    }

    $actualHead = $actualHead.Trim()
    if (-not $actualHead.Equals($ExpectedHead, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Host repo HEAD changed during paired runs. Expected '$ExpectedHead', actual '$actualHead'."
    }
}

function Publish-RoslynCli {
    param(
        [Parameter(Mandatory = $true)][string]$CliProjectPath,
        [Parameter(Mandatory = $true)][string]$BundleDirectory,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $publishDirectory = Join-Path $BundleDirectory "tools/roslyn-cli"
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

    Write-Host ("Publishing Roslyn CLI once for run bundle: {0}" -f $publishDirectory)
    & dotnet publish $CliProjectPath -c $Configuration -o $publishDirectory --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynAgent.Cli."
    }

    $cliDllPath = Join-Path $publishDirectory "RoslynAgent.Cli.dll"
    if (-not (Test-Path $cliDllPath)) {
        throw "Published RoslynAgent.Cli.dll not found at '$cliDllPath'."
    }

    return [string](Resolve-Path $cliDllPath).Path
}

function Write-RoslynHelperScripts {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$CliDllPath
    )

    $scriptsDir = Join-Path $RunDirectory "scripts"
    New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null

    $cliDllForCmd = $CliDllPath
    $cliDllForBash = $CliDllPath -replace "\\", "/"

    $localRoscliCmdPath = Join-Path $scriptsDir "roscli.cmd"
    $localRoscliBashPath = Join-Path $scriptsDir "roscli"

    $roscliCmd = @"
@echo off
dotnet "$cliDllForCmd" %*
"@
    Set-Content -Path $localRoscliCmdPath -Value $roscliCmd -NoNewline

    $roscliBash = @"
#!/usr/bin/env bash
set -euo pipefail
dotnet "$cliDllForBash" "\$@"
"@
    Set-Content -Path $localRoscliBashPath -Value $roscliBash -NoNewline

$listScript = @"
param()
& ".\scripts\roscli.cmd" list-commands --ids-only
"@
    $findScript = @"
param(
  [Parameter(Mandatory=`$true)][string]`$FilePath,
  [Parameter(Mandatory=`$true)][string]`$SymbolName
)
& ".\scripts\roscli.cmd" nav.find_symbol `$FilePath `$SymbolName --brief true --max-results 200
"@
    $renameScript = @"
param(
  [Parameter(Mandatory=`$true)][string]`$FilePath,
  [Parameter(Mandatory=`$true)][int]`$Line,
  [Parameter(Mandatory=`$true)][int]`$Column,
  [Parameter(Mandatory=`$true)][string]`$NewName,
  [switch]`$Apply
)
`$applyValue = if (`$Apply) { "true" } else { "false" }
& ".\scripts\roscli.cmd" edit.rename_symbol `$FilePath `$Line `$Column `$NewName --apply `$applyValue --max-diagnostics 100
"@
    $renameVerifyScript = @"
param(
  [Parameter(Mandatory=`$true)][string]`$FilePath,
  [Parameter(Mandatory=`$true)][int]`$Line,
  [Parameter(Mandatory=`$true)][int]`$Column,
  [Parameter(Mandatory=`$true)][string]`$NewName,
  [string]`$OldName = "",
  [int]`$ExpectedNewExact = -1,
  [int]`$ExpectedOldExact = -1,
  [switch]`$RequireNoDiagnostics = `$true
)

function Invoke-RoscliJson {
  param([string[]]`$Args)
  `$raw = & ".\scripts\roscli.cmd" @Args
  if (`$LASTEXITCODE -ne 0) {
    throw "Roslyn helper command failed: $($Args -join ' ')"
  }
  return `$raw | ConvertFrom-Json
}

`$rename = Invoke-RoscliJson -Args @(
  "edit.rename_symbol",
  `$FilePath,
  `$Line.ToString(),
  `$Column.ToString(),
  `$NewName,
  "--apply", "true",
  "--max-diagnostics", "100"
)

if (-not `$rename.Ok) {
  `$rename | ConvertTo-Json -Depth 12
  exit 1
}

`$verification = [ordered]@{
  new_symbol_matches = $null
  old_symbol_matches = $null
  diagnostics_errors = $null
  checks = @()
}

`$newMatches = Invoke-RoscliJson -Args @(
  "nav.find_symbol",
  `$FilePath,
  `$NewName,
  "--brief", "true",
  "--max-results", "200"
)
`$verification.new_symbol_matches = [int]`$newMatches.Data.total_matches

if (`$ExpectedNewExact -ge 0) {
  `$verification.checks += [ordered]@{
    name = "expected_new_exact"
    passed = (`$verification.new_symbol_matches -eq `$ExpectedNewExact)
    detail = "expected=$ExpectedNewExact actual=$($verification.new_symbol_matches)"
  }
}

if (-not [string]::IsNullOrWhiteSpace(`$OldName)) {
  `$oldMatches = Invoke-RoscliJson -Args @(
    "nav.find_symbol",
    `$FilePath,
    `$OldName,
    "--brief", "true",
    "--max-results", "200"
  )
  `$verification.old_symbol_matches = [int]`$oldMatches.Data.total_matches

  if (`$ExpectedOldExact -ge 0) {
    `$verification.checks += [ordered]@{
      name = "expected_old_exact"
      passed = (`$verification.old_symbol_matches -eq `$ExpectedOldExact)
      detail = "expected=$ExpectedOldExact actual=$($verification.old_symbol_matches)"
    }
  }
}

if (`$RequireNoDiagnostics) {
  `$diag = Invoke-RoscliJson -Args @(
    "diag.get_file_diagnostics",
    `$FilePath
  )
  `$verification.diagnostics_errors = [int]`$diag.Data.errors
  `$verification.checks += [ordered]@{
    name = "no_diagnostics_errors"
    passed = (`$verification.diagnostics_errors -eq 0)
    detail = "errors=$($verification.diagnostics_errors)"
  }
}

`$allChecksPassed = -not (`$verification.checks | Where-Object { -not [bool]`$_.passed })
`$result = [ordered]@{
  ok = [bool]`$allChecksPassed
  command = "roslyn.rename_and_verify"
  rename = `$rename.Data
  verify = `$verification
}

`$result | ConvertTo-Json -Depth 12
if (-not `$allChecksPassed) {
  exit 1
}
"@

    Set-Content -Path (Join-Path $RunDirectory "roslyn-list-commands.ps1") -Value $listScript -NoNewline
    Set-Content -Path (Join-Path $RunDirectory "roslyn-find-symbol.ps1") -Value $findScript -NoNewline
    Set-Content -Path (Join-Path $RunDirectory "roslyn-rename-symbol.ps1") -Value $renameScript -NoNewline
    Set-Content -Path (Join-Path $RunDirectory "roslyn-rename-and-verify.ps1") -Value $renameVerifyScript -NoNewline
}

function Test-IsRoslynCommandText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    $patterns = @(
        "roslyn-list-commands.ps1",
        "roslyn-find-symbol.ps1",
        "roslyn-rename-symbol.ps1",
        "roslyn-rename-and-verify.ps1",
        "roscli.cmd",
        "scripts/roscli",
        "./scripts/roscli",
        "nav.find_symbol",
        "edit.rename_symbol",
        "diag.get_file_diagnostics",
        "edit.replace_member_body",
        "RoslynAgent.Cli"
    )

    foreach ($pattern in $patterns) {
        if ($Text.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Get-CodexRoslynUsage {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $invocations = New-Object System.Collections.Generic.List[string]
    $seenInvocationIds = New-Object System.Collections.Generic.HashSet[string]
    $successfulInvocationIds = New-Object System.Collections.Generic.HashSet[string]
    $successful = 0

    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json
        } catch {
            continue
        }

        if ($event.type -ne "item.started" -and $event.type -ne "item.completed") {
            continue
        }

        if ($null -eq $event.item -or $event.item.type -ne "command_execution") {
            continue
        }

        $commandText = [string]$event.item.command
        if (-not (Test-IsRoslynCommandText -Text $commandText)) {
            continue
        }

        $itemId = [string]$event.item.id
        if ([string]::IsNullOrWhiteSpace($itemId)) {
            $itemId = [Guid]::NewGuid().ToString("n")
        }

        if ($seenInvocationIds.Add($itemId)) {
            $invocations.Add($commandText)
        }

        if ($event.type -eq "item.completed" -and $event.item.exit_code -eq 0 -and $successfulInvocationIds.Add($itemId)) {
            $successful++
        }
    }

    return @{
        Commands = $invocations.ToArray()
        Successful = $successful
    }
}

function Get-ClaudeRoslynUsage {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $roslynToolUseCommandsById = @{}
    $invocations = New-Object System.Collections.Generic.List[string]
    $successful = 0

    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json
        } catch {
            continue
        }

        if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($content in $event.message.content) {
                if ($content.type -eq "tool_use" -and $content.name -eq "Bash") {
                    $commandText = [string]$content.input.command
                    if (Test-IsRoslynCommandText -Text $commandText) {
                        $toolUseId = [string]$content.id
                        if (-not [string]::IsNullOrWhiteSpace($toolUseId)) {
                            $roslynToolUseCommandsById[$toolUseId] = $commandText
                        }
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

            $toolUseId = [string]$content.tool_use_id
            if ([string]::IsNullOrWhiteSpace($toolUseId) -or -not $roslynToolUseCommandsById.ContainsKey($toolUseId)) {
                continue
            }

            $commandText = [string]$roslynToolUseCommandsById[$toolUseId]
            $invocations.Add($commandText)

            $resultText = [string]$content.content
            $exitCode = $null
            if ($resultText -match "Exit code\s+(-?\d+)") {
                $exitCode = [int]$Matches[1]
            }

            if ($null -eq $exitCode) {
                if (-not [bool]$content.is_error) {
                    $successful++
                }
            } elseif ($exitCode -eq 0) {
                $successful++
            }
        }
    }

    return @{
        Commands = $invocations.ToArray()
        Successful = $successful
    }
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

    if ($Agent -eq "codex") {
        $input = 0
        $output = 0
        $cached = 0

        foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            try {
                $event = $line | ConvertFrom-Json
            } catch {
                continue
            }

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
    } elseif ($Agent -eq "claude") {
        foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            try {
                $event = $line | ConvertFrom-Json
            } catch {
                continue
            }

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

    return @{
        PromptTokens = $promptTokens
        CompletionTokens = $completionTokens
        TotalTokens = $totalTokens
        CachedInputTokens = $cachedInputTokens
        CacheReadInputTokens = $cacheReadInputTokens
        CacheCreationInputTokens = $cacheCreationInputTokens
    }
}

function Get-TokenAttribution {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TranscriptPath
    )

    $events = @()
    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $events += @($line | ConvertFrom-Json)
        } catch {
            continue
        }
    }

    if ($Agent -eq "codex") {
        $turns = 0
        $promptTokens = 0
        $completionTokens = 0
        $cachedInputTokens = 0
        $commandRoundTrips = 0
        $roslynCommandRoundTrips = 0
        $commandOutputChars = 0
        $agentMessageChars = 0

        foreach ($event in $events) {
            if ($event.type -eq "turn.completed" -and $null -ne $event.usage) {
                $turns++
                if ($null -ne $event.usage.input_tokens) {
                    $promptTokens += [int]$event.usage.input_tokens
                }
                if ($null -ne $event.usage.output_tokens) {
                    $completionTokens += [int]$event.usage.output_tokens
                }
                if ($null -ne $event.usage.cached_input_tokens) {
                    $cachedInputTokens += [int]$event.usage.cached_input_tokens
                }
            }

            if ($event.type -eq "item.completed" -and $null -ne $event.item) {
                if ($event.item.type -eq "command_execution") {
                    $commandRoundTrips++
                    $commandText = [string]$event.item.command
                    if (Test-IsRoslynCommandText -Text $commandText) {
                        $roslynCommandRoundTrips++
                    }

                    $outputText = [string]$event.item.aggregated_output
                    if (-not [string]::IsNullOrWhiteSpace($outputText)) {
                        $commandOutputChars += $outputText.Length
                    }
                } elseif ($event.item.type -eq "agent_message") {
                    $messageText = [string]$event.item.text
                    if (-not [string]::IsNullOrWhiteSpace($messageText)) {
                        $agentMessageChars += $messageText.Length
                    }
                }
            }
        }

        return @{
            turns = $turns
            command_round_trips = $commandRoundTrips
            roslyn_command_round_trips = $roslynCommandRoundTrips
            non_roslyn_command_round_trips = [Math]::Max(0, ($commandRoundTrips - $roslynCommandRoundTrips))
            command_output_chars = $commandOutputChars
            agent_message_chars = $agentMessageChars
            prompt_tokens = $promptTokens
            completion_tokens = $completionTokens
            cached_input_tokens = $cachedInputTokens
            cache_read_input_tokens = $null
            cache_creation_input_tokens = $null
            cache_inclusive_total_tokens = ($promptTokens + $completionTokens + $cachedInputTokens)
        }
    }

    $turnsClaude = 0
    $commandRoundTripsClaude = 0
    $roslynCommandRoundTripsClaude = 0
    $commandOutputCharsClaude = 0
    $agentMessageCharsClaude = 0
    $promptTokensClaude = $null
    $completionTokensClaude = $null
    $cacheReadClaude = $null
    $cacheCreationClaude = $null
    $roslynToolUseIds = New-Object System.Collections.Generic.HashSet[string]

    foreach ($event in $events) {
        if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($content in $event.message.content) {
                if ($content.type -eq "tool_use" -and $content.name -eq "Bash") {
                    $commandRoundTripsClaude++
                    $commandText = [string]$content.input.command
                    if (Test-IsRoslynCommandText -Text $commandText) {
                        $roslynCommandRoundTripsClaude++
                        $toolUseId = [string]$content.id
                        if (-not [string]::IsNullOrWhiteSpace($toolUseId)) {
                            [void]$roslynToolUseIds.Add($toolUseId)
                        }
                    }
                } elseif ($content.type -eq "text") {
                    $text = [string]$content.text
                    if (-not [string]::IsNullOrWhiteSpace($text)) {
                        $agentMessageCharsClaude += $text.Length
                    }
                }
            }
            continue
        }

        if ($event.type -eq "user" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($content in $event.message.content) {
                if ($content.type -ne "tool_result") {
                    continue
                }

                $textContent = [string]$content.content
                if (-not [string]::IsNullOrWhiteSpace($textContent)) {
                    $commandOutputCharsClaude += $textContent.Length
                }
            }
            continue
        }

        if ($event.type -eq "result" -and $null -ne $event.usage) {
            $turnsClaude++
            if ($null -ne $event.usage.input_tokens) {
                $promptTokensClaude = [int]$event.usage.input_tokens
            }
            if ($null -ne $event.usage.output_tokens) {
                $completionTokensClaude = [int]$event.usage.output_tokens
            }
            if ($null -ne $event.usage.cache_read_input_tokens) {
                $cacheReadClaude = [int]$event.usage.cache_read_input_tokens
            }
            if ($null -ne $event.usage.cache_creation_input_tokens) {
                $cacheCreationClaude = [int]$event.usage.cache_creation_input_tokens
            }
        }
    }

    $cacheInclusiveTotal = $null
    if ($null -ne $promptTokensClaude -or $null -ne $completionTokensClaude -or $null -ne $cacheReadClaude -or $null -ne $cacheCreationClaude) {
        $cacheInclusiveTotal = 0
        if ($null -ne $promptTokensClaude) { $cacheInclusiveTotal += [int]$promptTokensClaude }
        if ($null -ne $completionTokensClaude) { $cacheInclusiveTotal += [int]$completionTokensClaude }
        if ($null -ne $cacheReadClaude) { $cacheInclusiveTotal += [int]$cacheReadClaude }
        if ($null -ne $cacheCreationClaude) { $cacheInclusiveTotal += [int]$cacheCreationClaude }
    }

    return @{
        turns = $turnsClaude
        command_round_trips = $commandRoundTripsClaude
        roslyn_command_round_trips = $roslynCommandRoundTripsClaude
        non_roslyn_command_round_trips = [Math]::Max(0, ($commandRoundTripsClaude - $roslynCommandRoundTripsClaude))
        command_output_chars = $commandOutputCharsClaude
        agent_message_chars = $agentMessageCharsClaude
        prompt_tokens = $promptTokensClaude
        completion_tokens = $completionTokensClaude
        cached_input_tokens = $null
        cache_read_input_tokens = $cacheReadClaude
        cache_creation_input_tokens = $cacheCreationClaude
        cache_inclusive_total_tokens = $cacheInclusiveTotal
    }
}

function Invoke-AgentProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
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

    $overrides = @{
        HOME = $profileRoot
        USERPROFILE = $profileRoot
        APPDATA = $appDataRoot
        LOCALAPPDATA = $localAppDataRoot
        XDG_CONFIG_HOME = $xdgConfigRoot
        XDG_CACHE_HOME = $xdgCacheRoot
    }

    if ($Agent -eq "codex") {
        $overrides["CODEX_HOME"] = $codexHomeRoot
    } elseif ($Agent -eq "claude") {
        $overrides["CLAUDE_CONFIG_DIR"] = $claudeConfigRoot
        $overrides["ANTHROPIC_CONFIG_DIR"] = $claudeConfigRoot
    }

    return $overrides
}

function Get-RegexCount {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    return [System.Text.RegularExpressions.Regex]::Matches(
        $Text,
        $Pattern,
        [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
}

function Invoke-RenameConstraintChecks {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$CliDllPath
    )

    $targetPath = Join-Path $RunDirectory "Target.cs"
    $content = Get-Content -Path $targetPath -Raw

    $handleIntSignatureCount = Get-RegexCount -Text $content -Pattern "public\s+void\s+Handle\s*\(\s*int\s+value\s*\)"
    $processIntSignatureCount = Get-RegexCount -Text $content -Pattern "public\s+void\s+Process\s*\(\s*int\s+value\s*\)"
    $processStringSignatureCount = Get-RegexCount -Text $content -Pattern "public\s+void\s+Process\s*\(\s*string\s+value\s*\)"
    $handleInvocationCount = Get-RegexCount -Text $content -Pattern "\bHandle\s*\(\s*1\s*\)\s*;"
    $processIntInvocationCount = Get-RegexCount -Text $content -Pattern "\bProcess\s*\(\s*1\s*\)\s*;"
    $processStringInvocationCount = Get-RegexCount -Text $content -Pattern "\bProcess\s*\(\s*\""x\""\s*\)\s*;"
    $writeLineLiteralCount = Get-RegexCount -Text $content -Pattern "System\.Console\.WriteLine\(\s*\""Process\""\s*\)\s*;"
    $forbiddenHandleStringInvocationCount = Get-RegexCount -Text $content -Pattern "\bHandle\s*\(\s*\""x\""\s*\)\s*;"

    $checks = New-Object System.Collections.Generic.List[object]
    $checks.Add([ordered]@{
        name = "handle_int_signature_once"
        passed = ($handleIntSignatureCount -eq 1)
        detail = "count=$handleIntSignatureCount"
    })
    $checks.Add([ordered]@{
        name = "process_int_signature_removed"
        passed = ($processIntSignatureCount -eq 0)
        detail = "count=$processIntSignatureCount"
    })
    $checks.Add([ordered]@{
        name = "process_string_signature_preserved"
        passed = ($processStringSignatureCount -eq 1)
        detail = "count=$processStringSignatureCount"
    })
    $checks.Add([ordered]@{
        name = "handle_invocation_updated_once"
        passed = ($handleInvocationCount -eq 1)
        detail = "count=$handleInvocationCount"
    })
    $checks.Add([ordered]@{
        name = "process_int_invocation_removed"
        passed = ($processIntInvocationCount -eq 0)
        detail = "count=$processIntInvocationCount"
    })
    $checks.Add([ordered]@{
        name = "process_string_invocation_preserved"
        passed = ($processStringInvocationCount -eq 1)
        detail = "count=$processStringInvocationCount"
    })
    $checks.Add([ordered]@{
        name = "process_string_literal_preserved"
        passed = ($writeLineLiteralCount -eq 1)
        detail = "count=$writeLineLiteralCount"
    })
    $checks.Add([ordered]@{
        name = "forbidden_handle_string_invocation_absent"
        passed = ($forbiddenHandleStringInvocationCount -eq 0)
        detail = "count=$forbiddenHandleStringInvocationCount"
    })

    $diagnostics = [ordered]@{
        command_ok = $false
        parse_ok = $false
        errors = $null
        warnings = $null
        command_output = $null
    }

    $rawDiagnosticsOutput = & dotnet $CliDllPath diag.get_file_diagnostics Target.cs
    $diagExitCode = $LASTEXITCODE
    $diagOutputText = if ($rawDiagnosticsOutput -is [System.Array]) {
        [string]::Join([Environment]::NewLine, $rawDiagnosticsOutput)
    } else {
        [string]$rawDiagnosticsOutput
    }

    $diagnostics.command_output = $diagOutputText
    if ($diagExitCode -eq 0) {
        $diagnostics.command_ok = $true
        try {
            $diagEnvelope = $diagOutputText | ConvertFrom-Json
            if ($diagEnvelope.Ok) {
                $diagnostics.parse_ok = $true
                $diagnostics.errors = [int]$diagEnvelope.Data.errors
                $diagnostics.warnings = [int]$diagEnvelope.Data.warnings
            }
        } catch {
            $diagnostics.parse_ok = $false
        }
    }

    $diagnosticsPassed = ($diagnostics.command_ok -and $diagnostics.parse_ok -and [int]$diagnostics.errors -eq 0)
    $checks.Add([ordered]@{
        name = "no_diagnostics_errors"
        passed = $diagnosticsPassed
        detail = "errors=$($diagnostics.errors); command_ok=$($diagnostics.command_ok); parse_ok=$($diagnostics.parse_ok)"
    })

    $allChecksPassed = (@($checks | Where-Object { -not [bool]$_.passed }).Count -eq 0)
    return [ordered]@{
        ok = [bool]$allChecksPassed
        checks = $checks
        diagnostics = $diagnostics
    }
}

function Convert-ToSummaryValue {
    param([object]$Value)

    if ($null -eq $Value) {
        return "n/a"
    }

    return [string]$Value
}

function Write-PairedRunSummaryMarkdown {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Runs,
        [Parameter(Mandatory = $true)][string]$MarkdownPath
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Paired Agent Run Summary")
    $lines.Add("")

    if ($Runs.Count -eq 0) {
        $lines.Add("No runs executed.")
    } else {
        $lines.Add("| Agent | Mode | Exit | Run Passed | Constraints Passed | Control Contamination | Roslyn Used | Roslyn Calls (ok/attempted) | Model Total Tokens | Cache-inclusive Tokens | Round Trips | Roslyn Round Trips | Command Output Chars | Agent Message Chars |")
        $lines.Add("| --- | --- | ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |")

        foreach ($run in ($Runs | Sort-Object @{ Expression = "agent"; Ascending = $true }, @{ Expression = {
                    if ($_.mode -eq "control") { 0 } elseif ($_.mode -eq "treatment") { 1 } else { 2 }
                }; Ascending = $true })) {
            $roslynCalls = ("{0}/{1}" -f (Convert-ToSummaryValue $run.roslyn_successful_calls), (Convert-ToSummaryValue $run.roslyn_attempted_calls))
            $line = "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} |" -f `
                (Convert-ToSummaryValue $run.agent), `
                (Convert-ToSummaryValue $run.mode), `
                (Convert-ToSummaryValue $run.exit_code), `
                (Convert-ToSummaryValue $run.run_passed), `
                (Convert-ToSummaryValue $run.constraint_checks_passed), `
                (Convert-ToSummaryValue $run.control_contamination_detected), `
                (Convert-ToSummaryValue $run.roslyn_used), `
                $roslynCalls, `
                (Convert-ToSummaryValue $run.total_tokens), `
                (Convert-ToSummaryValue $run.cache_inclusive_total_tokens), `
                (Convert-ToSummaryValue $run.command_round_trips), `
                (Convert-ToSummaryValue $run.roslyn_command_round_trips), `
                (Convert-ToSummaryValue $run.token_attribution.command_output_chars), `
                (Convert-ToSummaryValue $run.token_attribution.agent_message_chars)
            $lines.Add($line)
        }
    }

    Set-Content -Path $MarkdownPath -Value ($lines -join [Environment]::NewLine)
}

function Get-Diff {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory
    )

    Push-Location $RunDirectory
    try {
        $diffText = & git --no-pager diff --no-index -- Target.original.cs Target.cs
        $diffExitCode = $LASTEXITCODE

        if ($null -eq $diffText) {
            $diffText = ""
        }

        if ($diffText -is [System.Array]) {
            $diffText = [string]::Join([Environment]::NewLine, $diffText)
        }

        Set-Content -Path (Join-Path $RunDirectory "diff.patch") -Value $diffText
        return ($diffExitCode -eq 1)
    } finally {
        Pop-Location
    }
}

function Invoke-AgentRun {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$BundleDirectory,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$IsolationRoot,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$TargetContent,
        [Parameter(Mandatory = $true)][string]$CliDllPath,
        [Parameter(Mandatory = $false)][bool]$FailOnControlContamination = $true,
        [Parameter(Mandatory = $false)][bool]$KeepIsolatedWorkspace = $false,
        [Parameter(Mandatory = $false)][string]$Model = ""
    )

    $runId = "$Agent-$Mode"
    $artifactRunDirectory = Join-Path $BundleDirectory $runId
    New-Item -ItemType Directory -Force -Path $artifactRunDirectory | Out-Null

    $workspaceDirectory = New-IsolatedRunWorkspace -IsolationRoot $IsolationRoot -RunId $runId
    Assert-IsolatedRunWorkspace -WorkspaceDirectory $workspaceDirectory -RepoRoot $RepoRoot

    $targetPath = Join-Path $workspaceDirectory "Target.cs"
    $targetOriginalPath = Join-Path $workspaceDirectory "Target.original.cs"
    $promptPath = Join-Path $workspaceDirectory "prompt.txt"
    $transcriptPath = Join-Path $workspaceDirectory "transcript.jsonl"

    try {
        Set-Content -Path $targetPath -Value $TargetContent -NoNewline
        Copy-Item -Path $targetPath -Destination $targetOriginalPath -Force
        Set-Content -Path $promptPath -Value $PromptText -NoNewline
        Set-Content -Path $transcriptPath -Value "" -NoNewline

        if ($Mode -eq "treatment") {
            Write-RoslynHelperScripts -RunDirectory $workspaceDirectory -CliDllPath $CliDllPath
        }

        $environmentOverrides = New-AgentEnvironmentOverrides -Agent $Agent -RunDirectory $workspaceDirectory

        $exitCode = 1
        Push-Location $workspaceDirectory
        try {
            if ($Agent -eq "codex") {
                $codexExecutable = "codex.cmd"
                if (-not (Get-Command $codexExecutable -ErrorAction SilentlyContinue)) {
                    $codexExecutable = "codex"
                }

                $args = @(
                    "exec",
                    "--json",
                    "--dangerously-bypass-approvals-and-sandbox",
                    "--skip-git-repo-check",
                    "-"
                )
                if (-not [string]::IsNullOrWhiteSpace($Model)) {
                    $args = @("exec", "--json", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check", "--model", $Model, "-")
                }

                $exitCode = Invoke-AgentProcess -Executable $codexExecutable -Arguments $args -PromptText $PromptText -TranscriptPath $transcriptPath -EnvironmentOverrides $environmentOverrides
            } elseif ($Agent -eq "claude") {
                $claudeExecutable = "claude.cmd"
                if (-not (Get-Command $claudeExecutable -ErrorAction SilentlyContinue)) {
                    $claudeExecutable = "claude"
                }

                $args = @(
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
                if (-not [string]::IsNullOrWhiteSpace($Model)) {
                    $args += @("--model", $Model)
                }

                $exitCode = Invoke-AgentProcess -Executable $claudeExecutable -Arguments $args -PromptText $PromptText -TranscriptPath $transcriptPath -EnvironmentOverrides $environmentOverrides
            } else {
                throw "Unsupported agent '$Agent'."
            }
        } finally {
            Pop-Location
        }

        $diffHasChanges = Get-Diff -RunDirectory $workspaceDirectory

        $usage = if ($Agent -eq "codex") {
            Get-CodexRoslynUsage -TranscriptPath $transcriptPath
        } else {
            Get-ClaudeRoslynUsage -TranscriptPath $transcriptPath
        }
        $tokens = Get-TokenMetrics -Agent $Agent -TranscriptPath $transcriptPath
        $tokenAttribution = Get-TokenAttribution -Agent $Agent -TranscriptPath $transcriptPath
        $constraintChecks = Invoke-RenameConstraintChecks -RunDirectory $workspaceDirectory -CliDllPath $CliDllPath
        $constraintChecksPath = Join-Path $workspaceDirectory "constraint-checks.json"
        $constraintChecks | ConvertTo-Json -Depth 40 | Set-Content -Path $constraintChecksPath

        Copy-RunArtifactFiles -WorkspaceDirectory $workspaceDirectory -ArtifactDirectory $artifactRunDirectory

        $artifactTargetPath = Join-Path $artifactRunDirectory "Target.cs"
        $artifactTargetOriginalPath = Join-Path $artifactRunDirectory "Target.original.cs"
        $artifactPromptPath = Join-Path $artifactRunDirectory "prompt.txt"
        $artifactTranscriptPath = Join-Path $artifactRunDirectory "transcript.jsonl"
        $artifactDiffPath = Join-Path $artifactRunDirectory "diff.patch"
        $artifactConstraintChecksPath = Join-Path $artifactRunDirectory "constraint-checks.json"

        $controlContaminationDetected = ($Mode -eq "control" -and $usage.Commands.Count -gt 0)
        $failureReasons = New-Object System.Collections.Generic.List[string]
        if ($exitCode -ne 0) {
            $failureReasons.Add("agent_exit_code_non_zero")
        }
        if (-not [bool]$constraintChecks.ok) {
            $failureReasons.Add("constraint_checks_failed")
        }
        if ($controlContaminationDetected -and $FailOnControlContamination) {
            $failureReasons.Add("control_contamination_detected")
        }
        $runPassed = ($failureReasons.Count -eq 0)

        $metadata = [ordered]@{
            agent = $Agent
            mode = $Mode
            timestamp_utc = (Get-Date).ToUniversalTime().ToString("o")
            exit_code = $exitCode
            workspace_path = $workspaceDirectory
            workspace_deleted = (-not $KeepIsolatedWorkspace)
            artifact_directory = (Resolve-Path $artifactRunDirectory).Path
            roslyn_used = ($usage.Successful -gt 0)
            roslyn_attempted_calls = $usage.Commands.Count
            roslyn_successful_calls = $usage.Successful
            roslyn_usage_indicators = $usage.Commands
            diff_has_changes = $diffHasChanges
            transcript_path = (Resolve-Path $artifactTranscriptPath).Path
            prompt_path = (Resolve-Path $artifactPromptPath).Path
            original_file = (Resolve-Path $artifactTargetOriginalPath).Path
            edited_file = (Resolve-Path $artifactTargetPath).Path
            diff_path = (Resolve-Path $artifactDiffPath).Path
            prompt_tokens = $tokens.PromptTokens
            completion_tokens = $tokens.CompletionTokens
            total_tokens = $tokens.TotalTokens
            cached_input_tokens = $tokens.CachedInputTokens
            cache_read_input_tokens = $tokens.CacheReadInputTokens
            cache_creation_input_tokens = $tokens.CacheCreationInputTokens
            cache_inclusive_total_tokens = $tokenAttribution.cache_inclusive_total_tokens
            command_round_trips = $tokenAttribution.command_round_trips
            roslyn_command_round_trips = $tokenAttribution.roslyn_command_round_trips
            non_roslyn_command_round_trips = $tokenAttribution.non_roslyn_command_round_trips
            control_contamination_detected = $controlContaminationDetected
            fail_on_control_contamination = $FailOnControlContamination
            run_passed = $runPassed
            failure_reasons = $failureReasons.ToArray()
            constraint_checks_passed = [bool]$constraintChecks.ok
            constraint_checks_path = (Resolve-Path $artifactConstraintChecksPath).Path
            agent_environment = $environmentOverrides
            token_attribution = $tokenAttribution
        }

        $metadataPath = Join-Path $artifactRunDirectory "run-metadata.json"
        $metadata | ConvertTo-Json -Depth 40 | Set-Content -Path $metadataPath

        Write-Host ("RUN {0} exit={1} run_passed={2} control_contamination={3} roslyn_used={4} roslyn_attempted_calls={5} roslyn_successful_calls={6} diff_has_changes={7} round_trips={8} model_total_tokens={9} cache_inclusive_tokens={10}" -f $runId, $exitCode, $metadata.run_passed, $metadata.control_contamination_detected, $metadata.roslyn_used, $metadata.roslyn_attempted_calls, $metadata.roslyn_successful_calls, $diffHasChanges, $metadata.command_round_trips, $metadata.total_tokens, $metadata.cache_inclusive_total_tokens)

        if ($controlContaminationDetected -and $FailOnControlContamination) {
            throw ("Control contamination detected for run '{0}'. Roslyn indicators: {1}" -f $runId, ($usage.Commands -join " | "))
        }

        return $metadata
    } finally {
        if (-not $KeepIsolatedWorkspace -and (Test-Path $workspaceDirectory)) {
            Remove-Item -Recurse -Force $workspaceDirectory -ErrorAction SilentlyContinue
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$homeWorkingDirectory = (Get-Location).Path
$expectedRepoRoot = Resolve-RepoTopLevel -Path $repoRoot
$expectedRepoHead = (& git -C $expectedRepoRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($expectedRepoHead)) {
    throw "Failed to resolve initial HEAD for repo '$expectedRepoRoot'."
}
$expectedRepoHead = $expectedRepoHead.Trim()

$isolationRootDirectory = Resolve-IsolationRootDirectory -RepoRoot $repoRoot -IsolationRoot $IsolationRoot
New-Item -ItemType Directory -Force -Path $isolationRootDirectory | Out-Null
$isolationRootDirectory = [string](Resolve-Path $isolationRootDirectory).Path
if (Test-PathIsUnderRoot -Path $isolationRootDirectory -Root $repoRoot) {
    throw "Isolation root '$isolationRootDirectory' is inside repo root '$repoRoot'. Choose an isolation root outside the repository."
}
Write-Host ("ISOLATION_ROOT={0}" -f $isolationRootDirectory)

$bundleDirectory = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot
New-Item -ItemType Directory -Force -Path $bundleDirectory | Out-Null

$cliProjectPath = (Resolve-Path (Join-Path $repoRoot "src\RoslynAgent.Cli\RoslynAgent.Cli.csproj")).Path
$cliDllPath = Publish-RoslynCli -CliProjectPath $cliProjectPath -BundleDirectory $bundleDirectory -Configuration $CliPublishConfiguration

$targetContent = @"
public class Overloads
{
    public void Process(int value)
    {
    }

    public void Process(string value)
    {
    }

    public void Execute()
    {
        Process(1);
        Process("x");
        System.Console.WriteLine("Process");
    }
}
"@

$controlPrompt = @"
Edit Target.cs in this directory.
Task:
1) Rename method Process(int value) to Handle(int value).
2) Update only the matching invocation Process(1) to Handle(1).
Constraints:
- Do NOT change Process(string value).
- Do NOT change Process("x").
- Do NOT change string literal "Process".
Baseline condition:
- Do NOT invoke Roslyn helper scripts/commands in this run.
- Use plain editor/text operations only.
After editing, briefly summarize what changed.
"@

$treatmentPromptCodex = @"
Edit Target.cs in this directory.
Task:
1) Rename method Process(int value) to Handle(int value).
2) Update only the matching invocation Process(1) to Handle(1).
Constraints:
- Do NOT change Process(string value).
- Do NOT change Process("x").
- Do NOT change string literal "Process".
Roslyn helper scripts are available in this directory and recommended.
Run Roslyn commands sequentially (not in parallel) to avoid transient dotnet build locks.
- scripts\roscli.cmd list-commands --ids-only
- powershell.exe -ExecutionPolicy Bypass -File .\roslyn-rename-and-verify.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -OldName Process -ExpectedNewExact 2 -ExpectedOldExact 2 -RequireNoDiagnostics
Or equivalent direct Roslyn calls:
- scripts\roscli.cmd nav.find_symbol Target.cs Process --brief true --max-results 200
- scripts\roscli.cmd edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 100
- scripts\roscli.cmd diag.get_file_diagnostics Target.cs
Compatibility helpers are also available:
- powershell.exe -ExecutionPolicy Bypass -File ./roslyn-list-commands.ps1
- powershell.exe -ExecutionPolicy Bypass -File ./roslyn-find-symbol.ps1 -FilePath Target.cs -SymbolName Process
- powershell.exe -ExecutionPolicy Bypass -File ./roslyn-rename-symbol.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -Apply
- powershell.exe -ExecutionPolicy Bypass -File .\roslyn-rename-and-verify.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -OldName Process -ExpectedNewExact 2 -ExpectedOldExact 2 -RequireNoDiagnostics
Prefer using Roslyn helpers before editing. If they fail, continue with best effort.
After editing, say explicitly whether Roslyn helpers were invoked successfully.
"@

$treatmentPromptClaude = @"
Edit Target.cs in this directory.
Task:
1) Rename method Process(int value) to Handle(int value).
2) Update only the matching invocation Process(1) to Handle(1).
Constraints:
- Do NOT change Process(string value).
- Do NOT change Process("x").
- Do NOT change string literal "Process".
Roslyn helper scripts are available in this directory and recommended.
Run Roslyn commands sequentially (not in parallel) to avoid transient dotnet build locks.
For Bash environments, use:
- bash scripts/roscli list-commands --ids-only
- bash scripts/roscli nav.find_symbol Target.cs Process --brief true --max-results 200
- bash scripts/roscli edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 100
- bash scripts/roscli diag.get_file_diagnostics Target.cs
For PowerShell environments, compatibility helpers are also available:
- powershell.exe -ExecutionPolicy Bypass -File ./roslyn-list-commands.ps1
- powershell.exe -ExecutionPolicy Bypass -File ./roslyn-find-symbol.ps1 -FilePath Target.cs -SymbolName Process
- powershell.exe -ExecutionPolicy Bypass -File ./roslyn-rename-symbol.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -Apply
- powershell.exe -ExecutionPolicy Bypass -File .\roslyn-rename-and-verify.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -OldName Process -ExpectedNewExact 2 -ExpectedOldExact 2 -RequireNoDiagnostics
Prefer using Roslyn helpers before editing. If they fail, continue with best effort.
After editing, say explicitly whether Roslyn helpers were invoked successfully.
"@

$runs = New-Object System.Collections.Generic.List[object]

if (-not $SkipCodex) {
    $runs.Add((Invoke-AgentRun -Agent "codex" -Mode "control" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $controlPrompt -TargetContent $targetContent -CliDllPath $cliDllPath -FailOnControlContamination $FailOnControlContamination -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $CodexModel))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    $runs.Add((Invoke-AgentRun -Agent "codex" -Mode "treatment" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $treatmentPromptCodex -TargetContent $targetContent -CliDllPath $cliDllPath -FailOnControlContamination $FailOnControlContamination -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $CodexModel))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
}

if (-not $SkipClaude) {
    $runs.Add((Invoke-AgentRun -Agent "claude" -Mode "control" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $controlPrompt -TargetContent $targetContent -CliDllPath $cliDllPath -FailOnControlContamination $FailOnControlContamination -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $ClaudeModel))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    $runs.Add((Invoke-AgentRun -Agent "claude" -Mode "treatment" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $treatmentPromptClaude -TargetContent $targetContent -CliDllPath $cliDllPath -FailOnControlContamination $FailOnControlContamination -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $ClaudeModel))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
}

Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead

$summaryPath = Join-Path $bundleDirectory "paired-run-summary.json"
if ($runs.Count -eq 0) {
    "[]" | Set-Content -Path $summaryPath -NoNewline
} else {
    $runs | ConvertTo-Json -Depth 40 | Set-Content -Path $summaryPath
}

$summaryFullPath = [System.IO.Path]::GetFullPath($summaryPath)
Write-Host ("SUMMARY={0}" -f $summaryFullPath)

$summaryMarkdownPath = Join-Path $bundleDirectory "paired-run-summary.md"
Write-PairedRunSummaryMarkdown -Runs $runs.ToArray() -MarkdownPath $summaryMarkdownPath
$summaryMarkdownFullPath = [System.IO.Path]::GetFullPath($summaryMarkdownPath)
Write-Host ("SUMMARY_MARKDOWN={0}" -f $summaryMarkdownFullPath)
