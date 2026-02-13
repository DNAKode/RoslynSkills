param(
    [string]$ManifestPath = "benchmarks/experiments/oss-csharp-pilot-v1/manifest.json",
    [string]$OutputRoot = "",
    [string]$CodexModel = "gpt-5.3-codex",
    [ValidateSet("low", "medium", "high", "xhigh")][string]$CodexReasoningEffort = "low",
    [ValidateSet("control-text-only", "treatment-roslyn-optional")][string[]]$ConditionIds = @("control-text-only", "treatment-roslyn-optional"),
    [string[]]$TaskIds = @('avalonia-cornerradius-tryparse'),
    [int]$CodexTimeoutSeconds = 240,
    [int]$RunsPerCell = 1,
    [ValidateSet("brief-first", "standard", "verbose-first")][string]$RoslynGuidanceProfile = "brief-first",
    [switch]$KeepWorkspaces
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Convert-ToText {
    param([object]$Value)

    if ($null -eq $Value) { return "" }
    if ($Value -is [System.Array]) { return [string]::Join([Environment]::NewLine, $Value) }
    return [string]$Value
}

function Convert-ToSlug {
    param([object]$Value)

    $text = (Convert-ToText $Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return "unknown" }
    $slug = $text.ToLowerInvariant()
    $slug = [regex]::Replace($slug, "[^a-z0-9]+", "-").Trim("-")
    if ([string]::IsNullOrWhiteSpace($slug)) { return "unknown" }
    return $slug
}

function Ensure-Command {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\.." )).Path
}

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputRoot
    )

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        return (Join-Path $RepoRoot "artifacts/real-agent-runs/$stamp-oss-csharp-pilot")
    }

    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        return $OutputRoot
    }

    return (Join-Path $RepoRoot $OutputRoot)
}

function Publish-Roscli {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputDirectory
    )

    $cliProjectPath = (Resolve-Path (Join-Path $RepoRoot "src\RoslynSkills.Cli\RoslynSkills.Cli.csproj")).Path
    $publishDirectory = Join-Path $OutputDirectory "tools/roscli"
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

    & dotnet publish $cliProjectPath -c Release -o $publishDirectory --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynSkills.Cli."
    }

    $dllPath = Join-Path $publishDirectory "RoslynSkills.Cli.dll"
    if (-not (Test-Path $dllPath -PathType Leaf)) {
        throw "Published RoslynSkills.Cli.dll not found at '$dllPath'."
    }

    return [string](Resolve-Path $dllPath).Path
}

function Write-RoscliShim {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$PublishedCliDll
    )

    $scriptsDir = Join-Path $WorkspacePath "scripts"
    New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null

    $cmd = @"
@echo off
setlocal
set "CLI_DLL=$PublishedCliDll"
dotnet "%CLI_DLL%" %*
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
"@

    $cmdPath = Join-Path $scriptsDir "roscli.cmd"
    $cmd | Out-File -FilePath $cmdPath -Encoding ascii -NoNewline
}

function Copy-CodexAuthToRunHome {
    param([Parameter(Mandatory = $true)][string]$RunCodexHome)

    New-Item -ItemType Directory -Force -Path $RunCodexHome | Out-Null
    $sourceCodexHome = Join-Path $env:USERPROFILE ".codex"

    foreach ($fileName in @("auth.json", "cap_sid", "version.json", "models_cache.json", "internal_storage.json")) {
        $src = Join-Path $sourceCodexHome $fileName
        $dst = Join-Path $RunCodexHome $fileName
        if (Test-Path $src -PathType Leaf) {
            Copy-Item -Path $src -Destination $dst -Force
        }
    }
}

function Invoke-CommandLine {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$CommandLine,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    Push-Location $WorkingDirectory
    try {
        Add-Content -Path $LogPath -Value ("$ Command: {0}" -f $CommandLine)

        $savedPreference = $ErrorActionPreference
        try {
            # Some dotnet tooling writes warnings to stderr; don't treat that as a terminating error.
            $ErrorActionPreference = "Continue"
            & cmd.exe /d /c $CommandLine 2>&1 | ForEach-Object { Add-Content -Path $LogPath -Value $_ }
        } finally {
            $ErrorActionPreference = $savedPreference
        }

        $exitCode = $LASTEXITCODE
        Add-Content -Path $LogPath -Value ("$ ExitCode: {0}" -f $exitCode)
        return $exitCode
    } finally {
        Pop-Location
    }
}

function Get-CodexTokenMetrics {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $promptTokens = $null
    $completionTokens = $null

    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $event = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($event.type -eq "turn.completed" -and $null -ne $event.usage) {
            if ($event.usage.PSObject.Properties.Match("input_tokens").Count -gt 0) { $promptTokens = [double]$event.usage.input_tokens }
            if ($event.usage.PSObject.Properties.Match("output_tokens").Count -gt 0) { $completionTokens = [double]$event.usage.output_tokens }
        }
    }

    $total = $null
    if ($null -ne $promptTokens -and $null -ne $completionTokens) { $total = $promptTokens + $completionTokens }
    return @{ prompt_tokens = $promptTokens; completion_tokens = $completionTokens; total_tokens = $total }
}

function Invoke-Codex {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspacePath,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
        [Parameter(Mandatory = $true)][string]$Model,
        [Parameter(Mandatory = $true)][string]$ReasoningEffort,
        [Parameter(Mandatory = $true)][hashtable]$EnvironmentOverrides
    )

    $codexExecutable = "codex.cmd"
    if (-not (Get-Command $codexExecutable -ErrorAction SilentlyContinue)) { $codexExecutable = "codex" }

    $args = @(
        "exec",
        "--json",
        "--dangerously-bypass-approvals-and-sandbox",
        "--skip-git-repo-check",
        "--model", $Model,
        "-c", ('model_reasoning_effort="{0}"' -f $ReasoningEffort),
        "-"
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $codexExecutable
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $WorkspacePath
    $psi.Arguments = [string]::Join(" ", ($args | ForEach-Object { if ($_ -match '\\s') { '"' + ($_ -replace '"','\\"') + '"' } else { $_ } }))

    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) { $psi.Environment[$entry.Key] = [string]$entry.Value }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    $process.StandardInput.WriteLine($PromptText)
    $process.StandardInput.Close()

    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()

    $timeoutMs = [int]([math]::Max(1, $CodexTimeoutSeconds) * 1000)
    $exited = $process.WaitForExit($timeoutMs)
    if (-not $exited) {
        try { & cmd.exe /d /c ("taskkill /PID {0} /T /F" -f $process.Id) | Out-Null } catch { }
        [void]$process.WaitForExit(5000)
    }

    $sw.Stop()
    $timedOut = -not $exited

    $stdOut = ""
    $stdErr = ""
    try { if ($outTask.Wait(5000)) { $stdOut = [string]$outTask.Result } } catch { }
    try { if ($errTask.Wait(5000)) { $stdErr = [string]$errTask.Result } } catch { }

    $combined = $stdOut
    if (-not [string]::IsNullOrWhiteSpace($stdErr)) { $combined = ($combined + "`n" + $stdErr).Trim() }
    if ($timedOut) {
        if (-not [string]::IsNullOrWhiteSpace($combined)) { $combined += "`n" }
        $combined += "TIMED_OUT"
    }

    Set-Content -Path $TranscriptPath -Value $combined

    $exit = if ($timedOut) { 124 } else { [int]$process.ExitCode }
    return @{ exit_code = $exit; duration_seconds = [math]::Round($sw.Elapsed.TotalSeconds, 3); timed_out = $timedOut }
}

function Build-Prompt {
    param(
        [Parameter(Mandatory = $true)][string]$ConditionId,
        [Parameter(Mandatory = $true)][string]$TaskPrompt
    )

    if ($ConditionId -eq "control-text-only") {
        return @"
$TaskPrompt

Tooling constraint:
- Do NOT use RoslynSkills tools (do not run scripts\\roscli.cmd).
"@
    }

    if ($RoslynGuidanceProfile -eq "verbose-first") {
        return @"
$TaskPrompt

RoslynSkills tools are available via scripts\\roscli.cmd.
Pit-of-success:
1) scripts\\roscli.cmd list-commands --ids-only
2) scripts\\roscli.cmd nav.find_symbol <file> <name> --brief true --max-results 20 --require-workspace true
3) scripts\\roscli.cmd diag.get_file_diagnostics <file> --require-workspace true
Always check workspace_context.mode.
"@
    }

    if ($RoslynGuidanceProfile -eq "standard") {
        return @"
$TaskPrompt

RoslynSkills tools are available via scripts\\roscli.cmd.
Prefer 1-2 Roslyn calls for targeting and verification (require_workspace=true).
"@
    }

    return @"
$TaskPrompt

RoslynSkills tools are available via scripts\\roscli.cmd.
Brief-first workflow:
1) scripts\\roscli.cmd nav.find_symbol <file> <name> --brief true --max-results 20 --require-workspace true
2) Apply minimal safe edit.
3) scripts\\roscli.cmd diag.get_file_diagnostics <file> --require-workspace true
"@
}

function New-WorkspaceId {
    param(
        [Parameter(Mandatory = $true)][string]$TaskSlug,
        [Parameter(Mandatory = $true)][string]$ConditionSlug,
        [Parameter(Mandatory = $true)][string]$ProfileSlug,
        [Parameter(Mandatory = $true)][int]$Replicate
    )

    $raw = "{0}|{1}|{2}|{3}" -f $TaskSlug, $ConditionSlug, $ProfileSlug, $Replicate
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($raw)
        $hash = ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''
    } finally {
        $sha.Dispose()
    }

    $t = if ($TaskSlug.Length -gt 12) { $TaskSlug.Substring(0, 12) } else { $TaskSlug }
    $c = if ($ConditionSlug.Length -gt 12) { $ConditionSlug.Substring(0, 12) } else { $ConditionSlug }
    $p = if ($ProfileSlug.Length -gt 12) { $ProfileSlug.Substring(0, 12) } else { $ProfileSlug }

    return ("oss-{0}-{1}-{2}-r{3:00}-{4}" -f $c, $t, $p, $Replicate, $hash.Substring(0, 8))
}

$repoRoot = Resolve-RepoRoot
Ensure-Command git
Ensure-Command dotnet
Ensure-Command codex

$manifestFullPath = Join-Path $repoRoot $ManifestPath
$manifest = Get-Content -Path $manifestFullPath -Raw | ConvertFrom-Json
$manifestDir = Split-Path -Path $manifestFullPath -Parent

$outputDir = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$outputDir = (Resolve-Path $outputDir).Path

$runsDir = Join-Path $repoRoot "benchmarks/experiments/oss-csharp-pilot-v1/runs"
New-Item -ItemType Directory -Force -Path $runsDir | Out-Null

$runBundleId = Split-Path -Path $outputDir -Leaf
$runBundleDir = Join-Path $runsDir $runBundleId
New-Item -ItemType Directory -Force -Path $runBundleDir | Out-Null

# Keep cache/workspaces on a short path to avoid Windows max-path issues in deep OSS repos.
$repoCacheRoot = Join-Path $repoRoot "_tmp\oss-csharp-pilot\repo-cache"
$workspaceRoot = Join-Path $repoRoot "_tmp\oss-csharp-pilot\workspaces"
New-Item -ItemType Directory -Force -Path $repoCacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path $workspaceRoot | Out-Null

$publishedCliDll = Publish-Roscli -RepoRoot $repoRoot -OutputDirectory $outputDir

$taskLookup = @{}
foreach ($t in @($manifest.tasks)) { $taskLookup[(Convert-ToText $t.id)] = $t }

foreach ($taskId in $TaskIds) {
    if (-not $taskLookup.ContainsKey($taskId)) { throw "Unknown task id '$taskId'" }
    $task = $taskLookup[$taskId]

    $taskPromptPath = Join-Path $manifestDir (Convert-ToText $task.task_prompt_file)
    $taskPromptText = Get-Content -Path $taskPromptPath -Raw

    $taskSlug = Convert-ToSlug $taskId
    $repoCacheDir = Join-Path $repoCacheRoot $taskSlug

    if (-not (Test-Path (Join-Path $repoCacheDir ".git"))) {
        & git clone (Convert-ToText $task.repo_url) $repoCacheDir | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $($task.repo_url)" }
    }

    Push-Location $repoCacheDir
    try {
        & git fetch --all --tags --prune | Out-Host
        & git fetch origin (Convert-ToText $task.commit) | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "git fetch failed for commit $($task.commit)" }
    } finally { Pop-Location }

    foreach ($conditionId in $ConditionIds) {
        $condSlug = Convert-ToSlug $conditionId
        $profileSlug = Convert-ToSlug $RoslynGuidanceProfile

        for ($rep = 1; $rep -le [Math]::Max(1, $RunsPerCell); $rep++) {
            $runId = "run-codex-{0}-{1}-{2}-r{3:00}" -f $condSlug, $taskSlug, $profileSlug, $rep
            $runDir = Join-Path $outputDir (Join-Path $taskId (Join-Path $conditionId $runId))
            New-Item -ItemType Directory -Force -Path $runDir | Out-Null

            $workspaceId = New-WorkspaceId -TaskSlug $taskSlug -ConditionSlug $condSlug -ProfileSlug $profileSlug -Replicate $rep
            $workspaceDir = Join-Path $workspaceRoot $workspaceId

            $setupLog = Join-Path $runDir "setup.log"
            $acceptLog = Join-Path $runDir "acceptance.log"
            $transcriptPath = Join-Path $runDir "transcript.jsonl"
            $promptPath = Join-Path $runDir "prompt.txt"
            $diffPath = Join-Path $runDir "diff.patch"

            $codexHome = Join-Path $runDir "codex-home"
            Copy-CodexAuthToRunHome -RunCodexHome $codexHome
            $envOverrides = @{ CODEX_HOME = $codexHome }

            # Ensure no stale worktree entry or directory exists.
            if (Test-Path (Join-Path $workspaceDir ".git")) {
                try { & git -C $repoCacheDir worktree remove -f $workspaceDir | Out-Null } catch { }
            }
            if (Test-Path $workspaceDir) { & cmd.exe /d /c ('rmdir /s /q "{0}"' -f $workspaceDir) | Out-Null }

            & git -C $repoCacheDir worktree add --detach -f $workspaceDir (Convert-ToText $task.commit) | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for commit $($task.commit)" }

            Write-RoscliShim -WorkspacePath $workspaceDir -PublishedCliDll $publishedCliDll

            $fullPrompt = Build-Prompt -ConditionId $conditionId -TaskPrompt $taskPromptText
            Set-Content -Path $promptPath -Value $fullPrompt -NoNewline
            Set-Content -Path $transcriptPath -Value "" -NoNewline
            Set-Content -Path $setupLog -Value "" -NoNewline
            Set-Content -Path $acceptLog -Value "" -NoNewline

            $setupOk = $true
            foreach ($cmdLine in @($task.setup_commands)) {
                if ([string]::IsNullOrWhiteSpace($cmdLine)) { continue }
                if ((Invoke-CommandLine -WorkingDirectory $workspaceDir -CommandLine (Convert-ToText $cmdLine) -LogPath $setupLog) -ne 0) { $setupOk = $false; break }
            }

            $agentExit = 1
            $durationSeconds = $null
            $timedOut = $false
            if ($setupOk) {
                $result = Invoke-Codex -WorkspacePath $workspaceDir -PromptText $fullPrompt -TranscriptPath $transcriptPath -Model $CodexModel -ReasoningEffort $CodexReasoningEffort -EnvironmentOverrides $envOverrides
                $agentExit = [int]$result.exit_code
                $durationSeconds = [double]$result.duration_seconds
                $timedOut = [bool]$result.timed_out
            } else {
                Add-Content -Path $transcriptPath -Value '{"type":"setup.failed","message":"Setup commands failed."}'
            }

            $acceptOk = $false
            if ($setupOk) {
                $acceptOk = $true
                foreach ($cmdLine in @($task.acceptance_checks)) {
                    if ([string]::IsNullOrWhiteSpace($cmdLine)) { continue }
                    if ((Invoke-CommandLine -WorkingDirectory $workspaceDir -CommandLine (Convert-ToText $cmdLine) -LogPath $acceptLog) -ne 0) { $acceptOk = $false; break }
                }
            } else {
                Add-Content -Path $acceptLog -Value 'SKIPPED: acceptance checks not run because setup failed.'
            }

            Push-Location $workspaceDir
            try { Set-Content -Path $diffPath -Value (& git diff) } finally { Pop-Location }

            if (-not $KeepWorkspaces) {
                & git -C $repoCacheDir worktree remove -f $workspaceDir | Out-Host
                if (Test-Path $workspaceDir) { & cmd.exe /d /c ('rmdir /s /q "{0}"' -f $workspaceDir) | Out-Null }
            }

            $tokens = Get-CodexTokenMetrics -TranscriptPath $transcriptPath
            $succeeded = ($setupOk -and $agentExit -eq 0 -and $acceptOk)

            $runRecord = [ordered]@{
                run_id = $runId
                task_id = $taskId
                condition_id = $conditionId
                replicate = $rep
                agent = "codex-cli"
                model = $CodexModel
                model_reasoning_effort = $CodexReasoningEffort
                succeeded = $succeeded
                setup_passed = $setupOk
                compile_passed = $acceptOk
                tests_passed = $acceptOk
                exit_code = $agentExit
                timed_out = $timedOut
                duration_seconds = $durationSeconds
                prompt_tokens = $tokens.prompt_tokens
                completion_tokens = $tokens.completion_tokens
                total_tokens = $tokens.total_tokens
                tool_calls = @()
                context = [ordered]@{
                    task_title = (Convert-ToText $task.title)
                    repo = (Convert-ToText $task.repo)
                    repo_url = (Convert-ToText $task.repo_url)
                    commit = (Convert-ToText $task.commit)
                    setup_checks = @($task.setup_commands)
                    acceptance_checks = @($task.acceptance_checks)
                    task_prompt_file = (Convert-ToText $task.task_prompt_file)
                    roslyn_guidance_profile = $RoslynGuidanceProfile
                    run_directory = $runDir
                    workspace_directory = $workspaceDir
                }
            }

            $runPath = Join-Path $runBundleDir ("{0}.json" -f $runId)
            $runRecord | ConvertTo-Json -Depth 12 | Set-Content -Path $runPath
        }
    }
}




