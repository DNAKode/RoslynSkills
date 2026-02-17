param(
    [string]$ManifestPath = "benchmarks/experiments/oss-csharp-pilot-v1/manifest.json",
    [string]$OutputRoot = "",
    [string]$CodexModel = "gpt-5.3-codex",
    [ValidateSet("low", "medium", "high", "xhigh")][string]$CodexReasoningEffort = "low",
    [ValidateSet("control-text-only", "treatment-roslyn-optional", "treatment-roslyn-required")][string[]]$ConditionIds = @("control-text-only", "treatment-roslyn-optional"),
    [string[]]$TaskIds = @('avalonia-cornerradius-tryparse'),
    [int]$CodexTimeoutSeconds = 240,
    [int]$RunsPerCell = 1,
    [ValidateSet("brief-first", "brief-first-v4", "brief-first-v5", "standard", "verbose-first")][string]$RoslynGuidanceProfile = "brief-first",
    [switch]$KeepWorkspaces
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-IsRoslynCommandText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    # Keep this aligned with the paired-run harness: detect both the shim and direct command ids.
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
        "diag.get_workspace_snapshot",
        "edit.replace_member_body",
        "edit.transaction",
        "RoslynSkills.Cli"
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
    $toolCalls = New-Object System.Collections.Generic.List[hashtable]
    # Regex (PowerShell single-quoted string): match "scripts\roscli.cmd <commandId>" or "scripts/roscli.cmd <commandId>"
    $roslynCommandPattern = '(?is)scripts[\\/]+roscli\.cmd\s+([a-z]+\.[a-z0-9_]+)'

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

        if ($null -eq $event.item) {
            continue
        }

        $candidateCommand = $null
        if ($event.item.PSObject.Properties.Match("command").Count -gt 0 -and $null -ne $event.item.command) {
            $candidateCommand = [string]$event.item.command
        }

        $invocationText = $null
        if (-not [string]::IsNullOrWhiteSpace($candidateCommand) -and (Test-IsRoslynCommandText -Text $candidateCommand)) {
            $invocationText = $candidateCommand
        }

        $isRoslynInvocation = -not [string]::IsNullOrWhiteSpace($invocationText)

        $itemId = $null
        if ($event.item.PSObject.Properties.Match("id").Count -gt 0 -and $null -ne $event.item.id) {
            $itemId = [string]$event.item.id
        }
        if ([string]::IsNullOrWhiteSpace($itemId)) {
            $itemId = [Guid]::NewGuid().ToString("n")
        }

        if ($isRoslynInvocation) {
            if ($seenInvocationIds.Add($itemId)) {
                $invocations.Add($invocationText)
            }
        }

        if ($event.type -eq "item.completed") {
            $wasSuccessful = $false
            if ($event.item.PSObject.Properties.Match("status").Count -gt 0 -and $null -ne $event.item.status) {
                $statusValue = [string]$event.item.status
                if (-not [string]::IsNullOrWhiteSpace($statusValue) -and $statusValue.Equals("failed", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $wasSuccessful = $false
                } else {
                    $wasSuccessful = $true
                }
            } else {
                $wasSuccessful = $true
            }

            if ($event.item.PSObject.Properties.Match("exit_code").Count -gt 0 -and $null -ne $event.item.exit_code) {
                $wasSuccessful = ([int]$event.item.exit_code -eq 0)
            }

            if ($event.item.PSObject.Properties.Match("error").Count -gt 0 -and $null -ne $event.item.error) {
                $wasSuccessful = $false
            }

            # Track tool calls for analysis/scoring. We only record completed items because they include success/failure.
            if (-not [string]::IsNullOrWhiteSpace($candidateCommand)) {
                $toolName = "shell.exec"
                if (Test-IsRoslynCommandText -Text $candidateCommand) {
                    $toolName = "roscli"
                    if ($candidateCommand -match $roslynCommandPattern) {
                        $candidate = [string]$Matches[1]
                        if (-not [string]::IsNullOrWhiteSpace($candidate)) { $toolName = $candidate.Trim() }
                    }
                }
                $toolCalls.Add(@{ tool_name = $toolName; ok = [bool]$wasSuccessful }) | Out-Null
            }

            if ($isRoslynInvocation -and $wasSuccessful -and $successfulInvocationIds.Add($itemId)) {
                $successful++
            }
        }
    }

    return @{
        Commands = $invocations.ToArray()
        Successful = $successful
        ToolCalls = $toolCalls.ToArray()
    }
}

function Get-RoslynWorkspaceContextUsage {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $content = Get-Content -Path $TranscriptPath -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) {
        $content = ""
    }

    # Handle both plain JSON and escaped JSON payloads (tool output embedded in JSON strings).
    $patterns = @(
        '(?is)"workspace_context"\\s*:\\s*\\{.*?"mode"\\s*:\\s*"(workspace|ad_hoc)"',
        '(?is)\\\\\"workspace_context\\\\\"\\s*:\\s*\\{.*?\\\\\"mode\\\\\"\\s*:\\s*\\\\\"(workspace|ad_hoc)\\\\\"'
    )

    $modes = New-Object System.Collections.Generic.List[string]
    foreach ($pattern in $patterns) {
        foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($content, $pattern)) {
            if ($null -eq $match -or $match.Groups.Count -lt 2) {
                continue
            }

            $mode = [string]$match.Groups[1].Value
            if (-not [string]::IsNullOrWhiteSpace($mode)) {
                $modes.Add($mode.Trim().ToLowerInvariant()) | Out-Null
            }
        }
    }

    if ($modes.Count -eq 0) {
        # Fallback: some transcripts truncate/reshape tool payloads, but usually preserve the Preview/Summary text.
        if ($content -match "workspace=workspace") { $modes.Add("workspace") | Out-Null }
        if ($content -match "workspace=ad_hoc") { $modes.Add("ad_hoc") | Out-Null }
    }

    $workspaceCount = @($modes | Where-Object { $_ -eq "workspace" }).Count
    $adHocCount = @($modes | Where-Object { $_ -eq "ad_hoc" }).Count

    $lastMode = $null
    for ($index = $modes.Count - 1; $index -ge 0; $index--) {
        $candidate = $modes[$index]
        if ($candidate -eq "workspace" -or $candidate -eq "ad_hoc") {
            $lastMode = $candidate
            break
        }
    }

    return @{
        workspace_count = $workspaceCount
        ad_hoc_count = $adHocCount
        total_count = ($workspaceCount + $adHocCount)
        distinct_modes = @($modes | Where-Object { $_ -eq "workspace" -or $_ -eq "ad_hoc" } | Select-Object -Unique)
        last_mode = $lastMode
    }
}

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

    if ($ConditionId -eq "treatment-roslyn-required") {
        if ($RoslynGuidanceProfile -eq "brief-first-v4") {
            return @"
$TaskPrompt

Tooling constraint (treatment-required):
- You MUST use RoslynSkills at least once (run scripts\\roscli.cmd) and the tool call must succeed.
- You MUST make at least one call that reports workspace_context.mode=workspace (use --require-workspace true).
- If you cannot satisfy those constraints, stop and explain why without attempting a text-only solution.

Roslyn usage rules (tight):
- Do NOT run list-commands.
- Do NOT do broad workspace scans.
- Before the first edit, make at most 2 Roslyn calls (1 targeting + 1 diagnostics check).
- Prefer `--brief true` where supported.

Tight workflow:
1) One targeted lookup for the exact symbol/member you intend to edit:
   scripts\\roscli.cmd nav.find_symbol <file> <name> --brief true --max-results 20 --require-workspace true
2) If you need local context, prefer Roslyn extraction:
   scripts\\roscli.cmd ctx.member_source <file> <line> <column> body
3) Apply the minimal safe edit(s).
4) Verify only what you touched:
   scripts\\roscli.cmd diag.get_file_diagnostics <file-you-edited> --require-workspace true
"@
        }

        if ($RoslynGuidanceProfile -eq "brief-first-v5") {
            return @"
$TaskPrompt

Tooling constraint (treatment-required):
- You MUST use RoslynSkills at least once (run scripts\\roscli.cmd) and the tool call must succeed.
- You MUST make at least one call that reports workspace_context.mode=workspace (use --require-workspace true).
- If you cannot satisfy those constraints, stop and explain why without attempting a text-only solution.

Roslyn usage rules (tight):
- Do NOT run list-commands.
- Do NOT do broad workspace scans.
- Before the first edit, make at most 2 Roslyn calls (targeting only; keep them `--brief true`).

Tight workflow:
1) Target the exact symbol/member to edit:
   scripts\\roscli.cmd nav.find_symbol <file> <name> --brief true --max-results 20 --require-workspace true
2) Apply the minimal safe edit(s).
3) Verify only the files you edited:
   scripts\\roscli.cmd diag.get_file_diagnostics <file-you-edited> --require-workspace true

Output rule:
- Do not paste build/test logs.
- Finish with 2 lines:
  - status: ok/failed
  - roslyn: used=<true/false> workspace_mode=<workspace|ad_hoc|unknown> successful_calls=<n>
"@
        }

        return @"
$TaskPrompt

Tooling constraint (treatment-required):
- You MUST use RoslynSkills at least once (run scripts\\roscli.cmd) and the tool call must succeed.
- You MUST make at least one call that reports workspace_context.mode=workspace (use --require-workspace true).
- If you cannot satisfy those constraints, stop and explain why without attempting a text-only solution.

Minimal pit-of-success:
1) scripts\\roscli.cmd nav.find_symbol <file> <name> --brief true --max-results 20 --require-workspace true
2) Apply minimal safe edit.
3) scripts\\roscli.cmd diag.get_file_diagnostics <file> --require-workspace true
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

            $roslynUsage = Get-CodexRoslynUsage -TranscriptPath $transcriptPath
            $workspaceContextUsage = Get-RoslynWorkspaceContextUsage -TranscriptPath $transcriptPath
            $cs0518Detected = (Select-String -Path $transcriptPath -Pattern "CS0518" -SimpleMatch -Quiet)

            $treatmentRequired = ($conditionId -eq "treatment-roslyn-required")
            $missingRequiredRoslynUsageDetected = ($treatmentRequired -and [int]$roslynUsage.Successful -le 0)
            $missingRequiredWorkspaceModeDetected = ($treatmentRequired -and [int]$workspaceContextUsage.workspace_count -le 0)
            $workspaceHealthGateFailed = ($treatmentRequired -and [bool]$cs0518Detected)

            $acceptOk = $false
            if ($setupOk) {
                if ($missingRequiredRoslynUsageDetected -or $missingRequiredWorkspaceModeDetected -or $workspaceHealthGateFailed) {
                    $acceptOk = $false
                    Add-Content -Path $acceptLog -Value "SKIPPED: acceptance checks not run because treatment-required validity gates failed."
                    if ($missingRequiredRoslynUsageDetected) { Add-Content -Path $acceptLog -Value "Gate: missing required successful Roslyn usage." }
                    if ($missingRequiredWorkspaceModeDetected) { Add-Content -Path $acceptLog -Value "Gate: missing required workspace_context.mode=workspace evidence." }
                    if ($workspaceHealthGateFailed) { Add-Content -Path $acceptLog -Value "Gate: CS0518 detected (workspace reference assemblies unhealthy)." }
                } else {
                    $acceptOk = $true
                    foreach ($cmdLine in @($task.acceptance_checks)) {
                        if ([string]::IsNullOrWhiteSpace($cmdLine)) { continue }
                        if ((Invoke-CommandLine -WorkingDirectory $workspaceDir -CommandLine (Convert-ToText $cmdLine) -LogPath $acceptLog) -ne 0) { $acceptOk = $false; break }
                    }
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

            $failureReasons = @()
            if ($missingRequiredRoslynUsageDetected) { $failureReasons += "missing_required_roslyn_usage" }
            if ($missingRequiredWorkspaceModeDetected) { $failureReasons += "missing_required_workspace_mode" }
            if ($workspaceHealthGateFailed) { $failureReasons += "workspace_health_gate_cs0518" }

            $roslynToolsEnabled = ($conditionId -ne "control-text-only")
            $toolsOffered = @("shell.exec")
            if ($roslynToolsEnabled) {
                $toolsOffered += @(
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
                tools_offered = @($toolsOffered)
                tool_calls = @($roslynUsage.ToolCalls)
                roslyn_used = ([int]$roslynUsage.Successful -gt 0)
                roslyn_attempted_calls = [int]$roslynUsage.Commands.Count
                roslyn_successful_calls = [int]$roslynUsage.Successful
                roslyn_usage_indicators = @($roslynUsage.Commands)
                roslyn_workspace_mode_workspace_count = [int]$workspaceContextUsage.workspace_count
                roslyn_workspace_mode_ad_hoc_count = [int]$workspaceContextUsage.ad_hoc_count
                roslyn_workspace_mode_last = $workspaceContextUsage.last_mode
                cs0518_detected = [bool]$cs0518Detected
                validity_gates = [ordered]@{
                    treatment_required = [bool]$treatmentRequired
                    missing_required_roslyn_usage = [bool]$missingRequiredRoslynUsageDetected
                    missing_required_workspace_mode = [bool]$missingRequiredWorkspaceModeDetected
                    workspace_health_gate_cs0518 = [bool]$workspaceHealthGateFailed
                    failure_reasons = @($failureReasons)
                }
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

            if ($treatmentRequired -and $failureReasons.Count -gt 0) {
                throw ("Treatment-required gates failed for run '{0}': {1}" -f $runId, ([string]::Join(", ", $failureReasons)))
            }
        }
    }
}




