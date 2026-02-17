param(
    [ValidateSet("codex", "claude")][string]$Agent = "claude",
    [string]$RepoPath = ".",
    [string]$GitRef = "HEAD",
    [string]$OutputRoot = "",
    [string]$TaskLabel = "medium-repo-task",
    [string]$BackgroundFile = "",
    [string]$TaskPromptFile = "",
    [ValidateSet("standard", "tight")][string]$TreatmentGuidanceProfile = "tight",
    [string]$AcceptanceCommand = "",
    [int]$TimeoutSeconds = 600,
    [string]$CodexModel = "",
    [AllowEmptyString()][ValidateSet("", "low", "medium", "high", "xhigh")][string]$CodexReasoningEffort = "",
    [string]$ClaudeModel = "",
    [switch]$KeepLaneWorkspaces,
    [bool]$FailOnControlContamination = $true,
    [bool]$FailOnMissingTreatmentRoslynUsage = $false
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

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TaskLabel
    )

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        $safeTask = ($TaskLabel -replace "[^a-zA-Z0-9\\-]+", "-").Trim("-").ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($safeTask)) {
            $safeTask = "task"
        }

        return (Join-Path $RepoRoot "artifacts/tool-thinking-split-runs/$stamp-$Agent-$safeTask")
    }

    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        return $OutputRoot
    }

    return (Join-Path $RepoRoot $OutputRoot)
}

function Resolve-AgentExecutable {
    param([Parameter(Mandatory = $true)][string]$AgentName)

    if ($AgentName -eq "codex") {
        foreach ($candidate in @("codex.cmd", "codex")) {
            $found = Get-Command $candidate -ErrorAction SilentlyContinue
            if ($null -ne $found) {
                return [string]$found.Source
            }
        }
    } elseif ($AgentName -eq "claude") {
        foreach ($candidate in @("claude.cmd", "claude")) {
            $found = Get-Command $candidate -ErrorAction SilentlyContinue
            if ($null -ne $found) {
                return [string]$found.Source
            }
        }
    }

    throw "Could not resolve executable for agent '$AgentName'."
}

function Resolve-RoscliLauncher {
    param([Parameter(Mandatory = $true)][string]$HostRepoRoot)

    foreach ($candidate in @(
            (Join-Path $HostRepoRoot "scripts\\roscli-stable.cmd"),
            (Join-Path $HostRepoRoot "scripts\\roscli.cmd"),
            (Join-Path $HostRepoRoot "scripts\\roscli-stable"),
            (Join-Path $HostRepoRoot "scripts\\roscli")
        )) {
        if (Test-Path $candidate -PathType Leaf) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "Could not locate roscli launcher under '$HostRepoRoot\\scripts'."
}

function Invoke-AgentProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
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
    $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "roslynskills-tool-thinking"
    New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null
    $runId = [Guid]::NewGuid().ToString("n")
    $stdinPath = Join-Path $tempDirectory ("stdin-{0}.txt" -f $runId)
    $stdoutPath = Join-Path $tempDirectory ("stdout-{0}.txt" -f $runId)
    $stderrPath = Join-Path $tempDirectory ("stderr-{0}.txt" -f $runId)

    try {
        foreach ($key in $EnvironmentOverrides.Keys) {
            $keyName = [string]$key
            $previousEnvironmentValues[$keyName] = [System.Environment]::GetEnvironmentVariable($keyName, "Process")
            [System.Environment]::SetEnvironmentVariable($keyName, [string]$EnvironmentOverrides[$keyName], "Process")
            $appliedEnvironmentKeys.Add($keyName) | Out-Null
        }

        Set-Content -Path $stdinPath -Value $PromptText -NoNewline
        $process = Start-Process `
            -FilePath $Executable `
            -ArgumentList $Arguments `
            -NoNewWindow `
            -PassThru `
            -RedirectStandardInput $stdinPath `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill()
            } catch {
            }
            $process.WaitForExit()
            $exitCode = 124
        } else {
            $exitCode = [int]$process.ExitCode
        }

        $outputChunks = New-Object System.Collections.Generic.List[string]
        if (Test-Path $stdoutPath -PathType Leaf) {
            $stdoutText = Get-Content -Path $stdoutPath -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrWhiteSpace($stdoutText)) {
                $outputChunks.Add($stdoutText) | Out-Null
            }
        }
        if (Test-Path $stderrPath -PathType Leaf) {
            $stderrText = Get-Content -Path $stderrPath -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
                $outputChunks.Add($stderrText) | Out-Null
            }
        }

        $combinedOutput = if ($outputChunks.Count -eq 0) { "" } else { [string]::Join([Environment]::NewLine, $outputChunks) }
        if (-not [string]::IsNullOrWhiteSpace($combinedOutput)) {
            Set-Content -Path $TranscriptPath -Value $combinedOutput -NoNewline
        } else {
            Set-Content -Path $TranscriptPath -Value "" -NoNewline
        }
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($hasNativeErrorPreference) {
            $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
        }

        foreach ($keyName in $appliedEnvironmentKeys) {
            [System.Environment]::SetEnvironmentVariable($keyName, $previousEnvironmentValues[$keyName], "Process")
        }

        foreach ($path in @($stdinPath, $stdoutPath, $stderrPath)) {
            if (Test-Path $path -PathType Leaf) {
                Remove-Item -Path $path -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return $exitCode
}

function New-AgentEnvironmentOverrides {
    param(
        [Parameter(Mandatory = $true)][string]$AgentName,
        [Parameter(Mandatory = $true)][string]$WorkspaceRoot
    )

    $agentHomeRoot = Join-Path $WorkspaceRoot ".agent-home"
    $profileRoot = Join-Path $agentHomeRoot "profile"
    $appDataRoot = Join-Path $agentHomeRoot "appdata"
    $localAppDataRoot = Join-Path $agentHomeRoot "localappdata"
    $xdgConfigRoot = Join-Path $agentHomeRoot "xdg-config"
    $xdgCacheRoot = Join-Path $agentHomeRoot "xdg-cache"
    $dotnetCliHomeRoot = Join-Path $agentHomeRoot "dotnet-cli-home"
    $codexHomeRoot = Join-Path $agentHomeRoot "codex-home"
    $claudeConfigRoot = Join-Path $agentHomeRoot "claude-config"

    foreach ($path in @(
            $agentHomeRoot,
            $profileRoot,
            $appDataRoot,
            $localAppDataRoot,
            $xdgConfigRoot,
            $xdgCacheRoot,
            $dotnetCliHomeRoot,
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
        DOTNET_CLI_HOME = $dotnetCliHomeRoot
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
        DOTNET_CLI_TELEMETRY_OPTOUT = "1"
        DOTNET_NOLOGO = "1"
    }

    if ($AgentName -eq "codex") {
        $existingCodexHome = if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) { Join-Path $env:USERPROFILE ".codex" } else { [string]$env:CODEX_HOME }
        if (Test-Path $existingCodexHome -PathType Container) {
            foreach ($fileName in @("auth.json", "cap_sid", "version.json", "models_cache.json", "internal_storage.json")) {
                Copy-FileIfExists `
                    -SourcePath (Join-Path $existingCodexHome $fileName) `
                    -DestinationPath (Join-Path $codexHomeRoot $fileName)
            }
        }
        $overrides["CODEX_HOME"] = $codexHomeRoot
    } elseif ($AgentName -eq "claude") {
        $existingClaudeConfig = if ([string]::IsNullOrWhiteSpace($env:CLAUDE_CONFIG_DIR)) { Join-Path $env:USERPROFILE ".claude" } else { [string]$env:CLAUDE_CONFIG_DIR }
        if (Test-Path $existingClaudeConfig -PathType Container) {
            foreach ($fileName in @(
                    ".credentials.json",
                    "settings.json",
                    "plugins\installed_plugins.json",
                    "plugins\config.json",
                    "plugins\known_marketplaces.json"
                )) {
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

function Initialize-LaneWorkspace {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRepo,
        [Parameter(Mandatory = $true)][string]$GitRef,
        [Parameter(Mandatory = $true)][string]$WorkspacePath
    )

    if (Test-Path $WorkspacePath) {
        Remove-Item -Recurse -Force $WorkspacePath
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $WorkspacePath) | Out-Null
    & git clone --quiet --config core.longpaths=true $SourceRepo $WorkspacePath
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed for '$WorkspacePath'."
    }

    & git -C $WorkspacePath checkout --quiet $GitRef
    if ($LASTEXITCODE -ne 0) {
        throw "git checkout failed for ref '$GitRef' in '$WorkspacePath'."
    }

    $resolvedHead = & git -C $WorkspacePath rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolvedHead)) {
        throw "Failed to resolve HEAD in '$WorkspacePath'."
    }

    return $resolvedHead.Trim()
}

function Invoke-Acceptance {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$AcceptanceCommand
    )

    if ([string]::IsNullOrWhiteSpace($AcceptanceCommand)) {
        return [pscustomobject]@{
            executed = $false
            exit_code = $null
            output = ""
        }
    }

    Push-Location $WorkspacePath
    try {
        $output = & cmd /d /c $AcceptanceCommand 2>&1
        $exitCode = [int]$LASTEXITCODE
        $outputText = if ($output -is [System.Array]) { [string]::Join([Environment]::NewLine, $output) } else { [string]$output }
        return [pscustomobject]@{
            executed = $true
            exit_code = $exitCode
            output = $outputText
        }
    } finally {
        Pop-Location
    }
}

function Invoke-LaneRun {
    param(
        [Parameter(Mandatory = $true)][string]$AgentName,
        [Parameter(Mandatory = $true)][string]$LaneId,
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$AcceptanceCommand,
        [Parameter(Mandatory = $false)][string]$CodexModel,
        [Parameter(Mandatory = $false)][string]$CodexReasoningEffort,
        [Parameter(Mandatory = $false)][string]$ClaudeModel
    )

    $environmentOverrides = New-AgentEnvironmentOverrides -AgentName $AgentName -WorkspaceRoot $WorkspacePath
    $executable = Resolve-AgentExecutable -AgentName $AgentName
    $args = @()
    if ($AgentName -eq "codex") {
        $args = @(
            "exec",
            "--json",
            "--dangerously-bypass-approvals-and-sandbox",
            "--skip-git-repo-check"
        )
        if (-not [string]::IsNullOrWhiteSpace($CodexModel)) {
            $args += @("--model", $CodexModel)
        }
        if (-not [string]::IsNullOrWhiteSpace($CodexReasoningEffort)) {
            $args += @("-c", ("model_reasoning_effort=""{0}""" -f $CodexReasoningEffort))
        }
        $args += @("-")
    } else {
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
        if (-not [string]::IsNullOrWhiteSpace($ClaudeModel)) {
            $args += @("--model", $ClaudeModel)
        }
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $exitCode = 1
    Push-Location $WorkspacePath
    try {
        $exitCode = Invoke-AgentProcess `
            -Executable $executable `
            -Arguments $args `
            -PromptText $PromptText `
            -TranscriptPath $TranscriptPath `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentOverrides $environmentOverrides
    } finally {
        Pop-Location
        $stopwatch.Stop()
    }

    $acceptance = Invoke-Acceptance -WorkspacePath $WorkspacePath -AcceptanceCommand $AcceptanceCommand
    $laneOk = ($exitCode -eq 0 -and (-not $acceptance.executed -or $acceptance.exit_code -eq 0))

    return [ordered]@{
        lane_id = $LaneId
        agent = $AgentName
        exit_code = $exitCode
        duration_seconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        transcript_path = $TranscriptPath
        workspace_path = $WorkspacePath
        acceptance = $acceptance
        lane_ok = $laneOk
    }
}

$scriptRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..\\..")).Path
$targetRepo = Resolve-AbsolutePath -PathValue $RepoPath
$outputDirectory = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot -Agent $Agent -TaskLabel $TaskLabel
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$scaffoldScript = Join-Path $scriptRoot "New-ToolThinkingSplitExperiment.ps1"
$analysisScript = Join-Path $scriptRoot "Analyze-ToolThinkingSplit.ps1"
if (-not (Test-Path $scaffoldScript -PathType Leaf)) {
    throw "Missing scaffold script: $scaffoldScript"
}
if (-not (Test-Path $analysisScript -PathType Leaf)) {
    throw "Missing analysis script: $analysisScript"
}

& powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $scaffoldScript `
    -OutputRoot $outputDirectory `
    -Agent $Agent `
    -TaskLabel $TaskLabel `
    -BackgroundFile $BackgroundFile `
    -TaskPromptFile $TaskPromptFile `
    -TreatmentGuidanceProfile $TreatmentGuidanceProfile | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Failed to scaffold tool-thinking split experiment."
}

$controlPromptPath = Join-Path $outputDirectory "prompt.control.txt"
$treatmentPromptPath = Join-Path $outputDirectory "prompt.treatment.txt"
$controlTranscriptPath = Join-Path $outputDirectory "control.transcript.jsonl"
$treatmentTranscriptPath = Join-Path $outputDirectory "treatment.transcript.jsonl"
$metricsJsonPath = Join-Path $outputDirectory "thinking-metrics.json"
$summaryMarkdownPath = Join-Path $outputDirectory "thinking-summary.md"

$controlPrompt = Get-Content -Raw -Path $controlPromptPath
$treatmentPrompt = Get-Content -Raw -Path $treatmentPromptPath
$roscliLauncherPath = Resolve-RoscliLauncher -HostRepoRoot $repoRoot

$controlPrompt = @"
$controlPrompt

Experiment override:
- Do NOT invoke RoslynSkills launcher: $roscliLauncherPath
- Do NOT invoke `roscli`, `scripts/roscli*`, or `roslyn://` tools in this lane.
"@

$treatmentPrompt = @"
$treatmentPrompt

RoslynSkills launcher for this run:
- $roscliLauncherPath
- Use this path directly when running RoslynSkills commands.
- Start with: "$roscliLauncherPath" list-commands --ids-only
"@

$workspaceRoot = Join-Path $outputDirectory "workspaces"
$controlWorkspace = Join-Path $workspaceRoot "control"
$treatmentWorkspace = Join-Path $workspaceRoot "treatment"

$controlCommit = Initialize-LaneWorkspace -SourceRepo $targetRepo -GitRef $GitRef -WorkspacePath $controlWorkspace
$treatmentCommit = Initialize-LaneWorkspace -SourceRepo $targetRepo -GitRef $GitRef -WorkspacePath $treatmentWorkspace
if (-not [string]::Equals($controlCommit, $treatmentCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Lane workspaces are on different commits: control=$controlCommit treatment=$treatmentCommit"
}

$controlResult = Invoke-LaneRun `
    -AgentName $Agent `
    -LaneId "control" `
    -WorkspacePath $controlWorkspace `
    -PromptText $controlPrompt `
    -TranscriptPath $controlTranscriptPath `
    -TimeoutSeconds $TimeoutSeconds `
    -AcceptanceCommand $AcceptanceCommand `
    -CodexModel $CodexModel `
    -CodexReasoningEffort $CodexReasoningEffort `
    -ClaudeModel $ClaudeModel

$treatmentResult = Invoke-LaneRun `
    -AgentName $Agent `
    -LaneId "treatment" `
    -WorkspacePath $treatmentWorkspace `
    -PromptText $treatmentPrompt `
    -TranscriptPath $treatmentTranscriptPath `
    -TimeoutSeconds $TimeoutSeconds `
    -AcceptanceCommand $AcceptanceCommand `
    -CodexModel $CodexModel `
    -CodexReasoningEffort $CodexReasoningEffort `
    -ClaudeModel $ClaudeModel

$analysisOutput = & powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $analysisScript `
    -ControlTranscript $controlTranscriptPath `
    -TreatmentTranscript $treatmentTranscriptPath `
    -OutputJson $metricsJsonPath `
    -OutputMarkdown $summaryMarkdownPath
if ($LASTEXITCODE -ne 0) {
    throw "Analyze-ToolThinkingSplit.ps1 failed."
}

$metrics = Get-Content -Raw -Path $metricsJsonPath | ConvertFrom-Json
if ($FailOnControlContamination -and [int]$metrics.control.roslyn_command_count -gt 0) {
    throw "Control contamination detected: control.roslyn_command_count=$($metrics.control.roslyn_command_count)"
}
if ($FailOnMissingTreatmentRoslynUsage -and [int]$metrics.treatment.roslyn_command_count -eq 0) {
    throw "Missing treatment Roslyn usage: treatment.roslyn_command_count=0"
}

$summary = [ordered]@{
    schema_version = "1.0"
    generated_utc = [DateTimeOffset]::UtcNow.ToString("o")
    repo_path = $targetRepo
    git_ref = $GitRef
    resolved_commit = $controlCommit
    agent = $Agent
    task_label = $TaskLabel
    treatment_guidance_profile = $TreatmentGuidanceProfile
    roscli_launcher_path = $roscliLauncherPath
    output_directory = $outputDirectory
    control = $controlResult
    treatment = $treatmentResult
    metrics_json_path = $metricsJsonPath
    summary_markdown_path = $summaryMarkdownPath
}

$summaryPath = Join-Path $outputDirectory "run-summary.json"
$summary | ConvertTo-Json -Depth 12 | Set-Content -Path $summaryPath -NoNewline

if (-not $KeepLaneWorkspaces) {
    foreach ($laneWorkspace in @($controlWorkspace, $treatmentWorkspace)) {
        if (Test-Path $laneWorkspace) {
            Remove-Item -Recurse -Force $laneWorkspace -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ("TOOL_THINKING_SPLIT_DONE output={0}" -f $outputDirectory)
