param(
    [string]$OutputRoot = "",
    [string[]]$Models = @("gpt-5.3-codex", "gpt-5.3-codex-spark"),
    [ValidateSet("single-file", "project")][string]$TaskShape = "project",
    [string[]]$ReasoningEfforts = @("low", "medium", "high"),
    [int]$CodexTimeoutSeconds = 300,
    [ValidateSet("control", "roslyn-mcp", "lsp-mcp", "roslyn-plus-lsp-mcp")][string[]]$Scenarios = @("control", "roslyn-mcp", "lsp-mcp", "roslyn-plus-lsp-mcp"),
    [string]$CliPublishConfiguration = "Release",
    [string]$LspMcpCommand = "",
    [string[]]$LspMcpArgs = @(),
    [switch]$KeepWorkspaces
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputRoot
    )

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        return (Join-Path $RepoRoot "artifacts/real-agent-runs/$stamp-codex-mcp-interop")
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

function Resolve-CodexExecutable {
    $codexCmd = Get-Command "codex.cmd" -ErrorAction SilentlyContinue
    if ($null -ne $codexCmd) {
        return $codexCmd.Source
    }

    $codex = Get-Command "codex" -ErrorAction SilentlyContinue
    if ($null -ne $codex) {
        return $codex.Source
    }

    throw "Could not resolve codex executable."
}

function Get-DefaultLspMcpCommand {
    param([AllowEmptyString()][string]$Configured)

    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        return $Configured
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ROSLYNSKILLS_LSP_MCP_COMMAND)) {
        return [string]$env:ROSLYNSKILLS_LSP_MCP_COMMAND
    }

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

    $localCmd = Join-Path $repoRoot "scripts\csharp-lsp-mcp.cmd"
    if (Test-Path $localCmd -PathType Leaf) {
        return [string](Resolve-Path $localCmd).Path
    }

    $localSh = Join-Path $repoRoot "scripts\csharp-lsp-mcp"
    if (Test-Path $localSh -PathType Leaf) {
        return [string](Resolve-Path $localSh).Path
    }

    foreach ($candidate in @("cclsp", "mcp-lsp", "csharp-lsp-mcp", "csharp_lsp_mcp", "lspuse-csharp", "csharp-ls-mcp")) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    return ""
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

function Copy-FileIfExists {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    if ([string]::IsNullOrWhiteSpace($SourcePath) -or [string]::IsNullOrWhiteSpace($DestinationPath)) {
        return
    }

    if (-not (Test-Path $SourcePath -PathType Leaf)) {
        return
    }

    $destinationDirectory = Split-Path -Path $DestinationPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    }

    Copy-Item -Path $SourcePath -Destination $DestinationPath -Force
}

function Copy-CodexAuthToRunHome {
    param([Parameter(Mandatory = $true)][string]$RunCodexHome)

    New-Item -ItemType Directory -Force -Path $RunCodexHome | Out-Null
    $sourceCodexHome = Join-Path $env:USERPROFILE ".codex"

    foreach ($fileName in @("auth.json", "cap_sid", "version.json", "models_cache.json", "internal_storage.json")) {
        Copy-FileIfExists `
            -SourcePath (Join-Path $sourceCodexHome $fileName) `
            -DestinationPath (Join-Path $RunCodexHome $fileName)
    }
}

function Publish-RoslynMcpServer {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $mcpProjectPath = (Resolve-Path (Join-Path $RepoRoot "src\RoslynSkills.McpServer\RoslynSkills.McpServer.csproj")).Path
    $publishDirectory = Join-Path $OutputDirectory "tools/roslyn-mcp"
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

    & dotnet publish $mcpProjectPath -c $Configuration -o $publishDirectory --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynSkills.McpServer."
    }

    $mcpDllPath = Join-Path $publishDirectory "RoslynSkills.McpServer.dll"
    if (-not (Test-Path $mcpDllPath -PathType Leaf)) {
        throw "Published RoslynSkills.McpServer.dll not found at '$mcpDllPath'."
    }

    return [string](Resolve-Path $mcpDllPath).Path
}

function Write-CodexMcpConfig {
    param(
        [Parameter(Mandatory = $true)][string]$CodexHomeDirectory,
        [Parameter(Mandatory = $true)][hashtable]$Servers
    )

    New-Item -ItemType Directory -Force -Path $CodexHomeDirectory | Out-Null
    $configPath = Join-Path $CodexHomeDirectory "config.toml"

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($serverName in ($Servers.Keys | Sort-Object)) {
        $server = $Servers[$serverName]
        $command = [string]$server.command
        $args = @($server.args)

        $safeServerName = $serverName -replace "[^A-Za-z0-9_-]", "_"
        $safeCommand = $command.Replace("\", "/").Replace('"', '\"')

        $lines.Add(("[mcp_servers.{0}]" -f $safeServerName))
        $lines.Add(("command = ""{0}""" -f $safeCommand))

        $argFragments = New-Object System.Collections.Generic.List[string]
        foreach ($arg in $args) {
            $safeArg = ([string]$arg).Replace("\", "/").Replace('"', '\"')
            $argFragments.Add(("""{0}""" -f $safeArg)) | Out-Null
        }

        $lines.Add(("args = [{0}]" -f ([string]::Join(", ", $argFragments))))
        $lines.Add("")
    }

    Set-Content -Path $configPath -Value ([string]::Join([Environment]::NewLine, $lines)) -NoNewline
    return [string](Resolve-Path $configPath).Path
}

function Test-CommandResolvable {
    param([AllowEmptyString()][string]$Command)

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return $false
    }

    if ([System.IO.Path]::IsPathRooted($Command)) {
        return (Test-Path $Command -PathType Leaf)
    }

    return ($null -ne (Get-Command $Command -ErrorAction SilentlyContinue))
}

function Test-UsesCclspAdapter {
    param(
        [AllowEmptyString()][string]$Command,
        [string[]]$Args
    )

    $blob = ((@($Command) + @($Args)) -join " ").ToLowerInvariant()
    return ($blob -match "(^|[\s/\\@._-])cclsp($|[\s/\\@._-])")
}

function Write-CclspConfig {
    param([Parameter(Mandatory = $true)][string]$RunDirectory)

    $config = @{
        servers = @(
            @{
                extensions = @("cs", "csx")
                command = @("csharp-ls")
                rootDir = "."
            }
        )
    }

    $configPath = Join-Path $RunDirectory "cclsp.json"
    $config | ConvertTo-Json -Depth 12 | Set-Content -Path $configPath -NoNewline
    return [string](Resolve-Path $configPath).Path
}

function Write-TaskWorkspaceFiles {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][ValidateSet("single-file", "project")][string]$TaskShape
    )

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

    $targetPath = Join-Path $RunDirectory "Target.cs"
    $targetOriginalPath = Join-Path $RunDirectory "Target.original.cs"
    Set-Content -Path $targetPath -Value $targetContent -NoNewline
    Copy-Item -Path $targetPath -Destination $targetOriginalPath -Force

    if ($TaskShape -eq "project") {
        $programContent = @"
public static class Program
{
    public static void Main()
    {
        var overloads = new Overloads();
        overloads.Execute();
    }
}
"@

        $projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove="Target.original.cs" />
  </ItemGroup>
</Project>
"@

        Set-Content -Path (Join-Path $RunDirectory "Program.cs") -Value $programContent -NoNewline
        Set-Content -Path (Join-Path $RunDirectory "TargetHarness.csproj") -Value $projectContent -NoNewline
    }
}

function Get-ScenarioPrompt {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("control", "roslyn-mcp", "lsp-mcp", "roslyn-plus-lsp-mcp")][string]$Scenario,
        [Parameter(Mandatory = $true)][ValidateSet("single-file", "project")][string]$TaskShape
    )

    $workspaceHint = if ($TaskShape -eq "project") {
        "Workspace includes TargetHarness.csproj; prefer project-aware semantics."
    } else {
        "Workspace is single-file only; ad_hoc context may be expected."
    }

    $taskPrompt = @"
Update Target.cs:
- Rename only Process(int value) -> Handle(int value).
- Update only Process(1) -> Handle(1).
- Keep Process(string value), Process("x"), and string literal "Process" unchanged.
$workspaceHint
"@

    switch ($Scenario) {
        "control" {
            return @"
$taskPrompt
Use plain text editing only. Do not use MCP tools.
After editing, summarize exactly what changed.
"@
        }
        "roslyn-mcp" {
            return @"
$taskPrompt
Use MCP server 'roslyn' as the primary tool path.
Suggested MCP sequence:
1) list_mcp_resource_templates server=roslyn
2) read_mcp_resource server=roslyn uri=roslyn://commands
3) read_mcp_resource server=roslyn uri=roslyn://command/nav.find_symbol?file_path=Target.cs&symbol_name=Process&brief=true&max_results=50
4) read_mcp_resource server=roslyn uri=roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true&max_diagnostics=50
5) read_mcp_resource server=roslyn uri=roslyn://command/diag.get_file_diagnostics?file_path=Target.cs
After editing, state whether Roslyn MCP calls succeeded.
"@
        }
        "lsp-mcp" {
            return @"
$taskPrompt
Use MCP server 'csharp_lsp' as the primary tool path.
Prefer LSP-style symbol lookup/definition/reference checks and diagnostics before/after edit.
If csharp_lsp MCP calls fail, report the failure reason and continue best-effort.
After editing, state whether csharp_lsp MCP calls succeeded.
"@
        }
        default {
            return @"
$taskPrompt
Use MCP servers 'roslyn' and 'csharp_lsp' together.
Prefer one targeted semantic lookup then apply the minimal safe edit and verify diagnostics.
After editing, state which server(s) were used successfully.
"@
        }
    }
}

function Invoke-CodexRun {
    param(
        [Parameter(Mandatory = $true)][string]$CodexExecutable,
        [Parameter(Mandatory = $true)][string]$WorkspaceDirectory,
        [Parameter(Mandatory = $true)][string]$Model,
        [Parameter(Mandatory = $true)][string]$ReasoningEffort,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
        [Parameter(Mandatory = $true)][hashtable]$EnvironmentOverrides,
        [int]$TimeoutSeconds = 300
    )

    $args = @(
        "exec",
        "--json",
        "--dangerously-bypass-approvals-and-sandbox",
        "--skip-git-repo-check",
        "--model", $Model,
        "-c", ("model_reasoning_effort=""{0}""" -f $ReasoningEffort),
        "-C", $WorkspaceDirectory
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $CodexExecutable
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.Arguments = [string]::Join(" ", ($args | ForEach-Object {
                if ([string]::IsNullOrWhiteSpace($_)) { '""' } elseif ($_ -match '\s') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
            }))

    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
        $psi.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    $process.StandardInput.WriteLine($PromptText)
    $process.StandardInput.Close()

    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()

    $timeoutMs = [int]([math]::Max(1, $TimeoutSeconds) * 1000)
    $exited = $process.WaitForExit($timeoutMs)
    if (-not $exited) {
        try {
            & cmd.exe /d /c ("taskkill /PID {0} /T /F" -f $process.Id) | Out-Null
        } catch {
        }
        [void]$process.WaitForExit(5000)
    }

    $stopwatch.Stop()

    $timedOut = -not $exited

    $stdOut = ""
    $stdErr = ""

    try {
        if ($outTask.Wait(5000)) {
            $stdOut = [string]$outTask.Result
        }
    } catch {
    }

    try {
        if ($errTask.Wait(5000)) {
            $stdErr = [string]$errTask.Result
        }
    } catch {
    }

    $combined = $stdOut
    if (-not [string]::IsNullOrWhiteSpace($stdErr)) {
        if (-not [string]::IsNullOrWhiteSpace($combined)) {
            $combined += [Environment]::NewLine
        }
        $combined += $stdErr
    }

    if ($timedOut) {
        if (-not [string]::IsNullOrWhiteSpace($combined)) {
            $combined += [Environment]::NewLine
        }
        $combined += "TIMED_OUT"
    }

    Set-Content -Path $TranscriptPath -Value $combined

    return @{
        exit_code = if ($timedOut) { 124 } else { [int]$process.ExitCode }
        stdout = $stdOut
        stderr = $stdErr
        combined = $combined
        duration_seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        timed_out = $timedOut
        timeout_seconds = [int]$TimeoutSeconds
    }
}

function Get-CodexTokenMetrics {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $promptTokens = $null
    $completionTokens = $null
    $totalTokens = $null

    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json -ErrorAction Stop
        } catch {
            continue
        }

        if ($event.type -eq "turn.completed" -and $null -ne $event.usage) {
            if ($event.usage.PSObject.Properties.Match("input_tokens").Count -gt 0) {
                $promptTokens = [double]$event.usage.input_tokens
            }
            if ($event.usage.PSObject.Properties.Match("output_tokens").Count -gt 0) {
                $completionTokens = [double]$event.usage.output_tokens
            }
            if ($null -ne $promptTokens -and $null -ne $completionTokens) {
                $totalTokens = $promptTokens + $completionTokens
            }
        }
    }

    return @{
        prompt_tokens = $promptTokens
        completion_tokens = $completionTokens
        total_tokens = $totalTokens
    }
}

function Get-CodexUsageCounters {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $roslynCalls = 0
    $lspCalls = 0
    $roundTrips = 0

    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json -ErrorAction Stop
        } catch {
            continue
        }

        if ($event.type -ne "item.completed") {
            continue
        }

        if ($null -eq $event.item -or $event.item.PSObject.Properties.Match("type").Count -eq 0) {
            continue
        }

        $itemType = [string]$event.item.type
        if ($itemType -eq "command_execution" -or $itemType -eq "mcp_tool_call") {
            $roundTrips++
        }

        $serverName = if ($event.item.PSObject.Properties.Match("server").Count -gt 0) { [string]$event.item.server } else { "" }
        $toolName = if ($event.item.PSObject.Properties.Match("tool").Count -gt 0) { [string]$event.item.tool } else { "" }
        $commandText = if ($event.item.PSObject.Properties.Match("command").Count -gt 0) { [string]$event.item.command } else { "" }
        $blob = ($serverName + " " + $toolName + " " + $commandText).ToLowerInvariant()

        if ($itemType -eq "mcp_tool_call") {
            if (-not [string]::IsNullOrWhiteSpace($serverName) -and $serverName.ToLowerInvariant().Contains("roslyn")) {
                $roslynCalls++
            }

            if (-not [string]::IsNullOrWhiteSpace($serverName)) {
                $serverLower = $serverName.ToLowerInvariant()
                if ($serverLower.Contains("lsp") -or $serverLower.Contains("csharp")) {
                    $lspCalls++
                }
            }
        }
    }

    return @{
        round_trips = $roundTrips
        roslyn_calls = $roslynCalls
        lsp_calls = $lspCalls
    }
}

function Get-FailureSnippet {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    foreach ($line in ($Text -split "(`r`n|`n|`r)")) {
        if ($line -match '"type":"error"' -or
            $line -match 'turn.failed' -or
            $line -match 'model is not supported' -or
            $line -match 'http 400') {
            return $line.Trim()
        }
    }

    return (($Text -split "(`r`n|`n|`r)") | Select-Object -Last 1).Trim()
}

function Test-RenameConstraints {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][ValidateSet("single-file", "project")][string]$TaskShape
    )

    $targetPath = Join-Path $RunDirectory "Target.cs"
    if (-not (Test-Path $targetPath -PathType Leaf)) {
        return @{ passed = $false; reason = "Target.cs missing" }
    }

    $content = Get-Content -Path $targetPath -Raw
    $checks = @(
        @{ ok = $content.Contains("public void Handle(int value)"); reason = "int overload not renamed" },
        @{ ok = $content.Contains("Handle(1);"); reason = "int callsite not renamed" },
        @{ ok = $content.Contains("public void Process(string value)"); reason = "string overload changed unexpectedly" },
        @{ ok = $content.Contains("Process(""x"");"); reason = "string call changed unexpectedly" },
        @{ ok = $content.Contains("WriteLine(""Process"")"); reason = "string literal changed unexpectedly" }
    )

    foreach ($check in $checks) {
        if (-not [bool]$check.ok) {
            return @{ passed = $false; reason = [string]$check.reason }
        }
    }

    if ($TaskShape -eq "project") {
        & dotnet build (Join-Path $RunDirectory "TargetHarness.csproj") --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) {
            return @{ passed = $false; reason = "dotnet build failed" }
        }
    }

    return @{ passed = $true; reason = "" }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outputDirectory = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$outputDirectory = (Resolve-Path $outputDirectory).Path

$codexExecutable = Resolve-CodexExecutable
$isolationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("roslynskills-codex-mcp-interop-" + (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $isolationRoot | Out-Null

$runRecords = New-Object System.Collections.Generic.List[object]

$needRoslynMcp = ($Scenarios -contains "roslyn-mcp" -or $Scenarios -contains "roslyn-plus-lsp-mcp")
$roslynMcpDllPath = $null
if ($needRoslynMcp) {
    $roslynMcpDllPath = Publish-RoslynMcpServer -RepoRoot $repoRoot -OutputDirectory $outputDirectory -Configuration $CliPublishConfiguration
}

$resolvedLspMcpCommand = Get-DefaultLspMcpCommand -Configured $LspMcpCommand
$resolvedLspMcpArgs = @($LspMcpArgs)
if ($resolvedLspMcpArgs.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:ROSLYNSKILLS_LSP_MCP_ARGS)) {
    $resolvedLspMcpArgs = @($env:ROSLYNSKILLS_LSP_MCP_ARGS -split '\s+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

foreach ($model in $Models) {
    foreach ($effort in $ReasoningEfforts) {
        foreach ($scenario in $Scenarios) {
            $runId = ("{0}-{1}-{2}" -f $scenario, $model, $effort) -replace "[^A-Za-z0-9._-]", "_"
            $runDirectory = Join-Path $outputDirectory $runId
            New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

            $record = [ordered]@{
                scenario = $scenario
                model = $model
                reasoning_effort = $effort
                task_shape = $TaskShape
                run_directory = $runDirectory
                timestamp_utc = (Get-Date).ToUniversalTime().ToString("o")
                skipped = $false
                skip_reason = ""
                exit_code = $null
                run_passed = $false
                failure_reason = ""
                duration_seconds = $null
                round_trips = 0
                roslyn_calls = 0
                lsp_calls = 0
                prompt_tokens = $null
                completion_tokens = $null
                total_tokens = $null
                constraint_reason = ""
                codex_home = ""
                mcp_config_path = ""
                lsp_mcp_command = if ([string]::IsNullOrWhiteSpace($resolvedLspMcpCommand)) { $null } else { $resolvedLspMcpCommand }
                lsp_runtime_config_path = $null
            }

            $requiresLspMcp = ($scenario -eq "lsp-mcp" -or $scenario -eq "roslyn-plus-lsp-mcp")
            if ($requiresLspMcp -and [string]::IsNullOrWhiteSpace($resolvedLspMcpCommand)) {
                $record.skipped = $true
                $record.skip_reason = "No LSP MCP command configured/found. Set -LspMcpCommand or ROSLYNSKILLS_LSP_MCP_COMMAND."
                $record.failure_reason = $record.skip_reason
                $runRecords.Add([pscustomobject]$record) | Out-Null
                continue
            }
            if ($requiresLspMcp -and -not (Test-CommandResolvable -Command $resolvedLspMcpCommand)) {
                $record.skipped = $true
                $record.skip_reason = ("Configured LSP MCP command is not resolvable: {0}" -f $resolvedLspMcpCommand)
                $record.failure_reason = $record.skip_reason
                $runRecords.Add([pscustomobject]$record) | Out-Null
                continue
            }

            $workspaceDirectory = New-IsolatedRunWorkspace -IsolationRoot $isolationRoot -RunId $runId
            Write-TaskWorkspaceFiles -RunDirectory $workspaceDirectory -TaskShape $TaskShape
            if ($requiresLspMcp -and (Test-UsesCclspAdapter -Command $resolvedLspMcpCommand -Args $resolvedLspMcpArgs)) {
                $record.lsp_runtime_config_path = Write-CclspConfig -RunDirectory $workspaceDirectory
            }
            $promptText = Get-ScenarioPrompt -Scenario $scenario -TaskShape $TaskShape
            Set-Content -Path (Join-Path $runDirectory "prompt.txt") -Value $promptText -NoNewline

            $runCodexHome = Join-Path $workspaceDirectory ".agent-home\codex-home"
            Copy-CodexAuthToRunHome -RunCodexHome $runCodexHome
            $record.codex_home = $runCodexHome

            $servers = @{}
            if ($scenario -eq "roslyn-mcp" -or $scenario -eq "roslyn-plus-lsp-mcp") {
                $servers["roslyn"] = @{
                    command = "dotnet"
                    args = @($roslynMcpDllPath)
                }
            }
            if ($scenario -eq "lsp-mcp" -or $scenario -eq "roslyn-plus-lsp-mcp") {
                $servers["csharp_lsp"] = @{
                    command = $resolvedLspMcpCommand
                    args = $resolvedLspMcpArgs
                }
            }

            if ($servers.Count -gt 0) {
                $mcpConfigPath = Write-CodexMcpConfig -CodexHomeDirectory $runCodexHome -Servers $servers
                $record.mcp_config_path = $mcpConfigPath
            }

            $transcriptPath = Join-Path $runDirectory "transcript.jsonl"
            $envOverrides = @{
                CODEX_HOME = $runCodexHome
            }

            $runResult = Invoke-CodexRun `
                -CodexExecutable $codexExecutable `
                -WorkspaceDirectory $workspaceDirectory `
                -Model $model `
                -ReasoningEffort $effort `
                -PromptText $promptText `
                -TranscriptPath $transcriptPath `
                -EnvironmentOverrides $envOverrides `
                -TimeoutSeconds $CodexTimeoutSeconds

            $record.exit_code = [int]$runResult.exit_code
            $record.duration_seconds = [double]$runResult.duration_seconds
            $record.timed_out = [bool]$runResult.timed_out
            $record.timeout_seconds = [int]$runResult.timeout_seconds

            $usage = Get-CodexUsageCounters -TranscriptPath $transcriptPath
            $record.round_trips = [int]$usage.round_trips
            $record.roslyn_calls = [int]$usage.roslyn_calls
            $record.lsp_calls = [int]$usage.lsp_calls

            $tokens = Get-CodexTokenMetrics -TranscriptPath $transcriptPath
            $record.prompt_tokens = $tokens.prompt_tokens
            $record.completion_tokens = $tokens.completion_tokens
            $record.total_tokens = $tokens.total_tokens

            if ($runResult.exit_code -eq 0) {
                $constraintCheck = Test-RenameConstraints -RunDirectory $workspaceDirectory -TaskShape $TaskShape
                $record.run_passed = [bool]$constraintCheck.passed
                $record.constraint_reason = [string]$constraintCheck.reason
                if (-not [bool]$constraintCheck.passed) {
                    $record.failure_reason = [string]$constraintCheck.reason
                }
            } else {
                $record.run_passed = $false
                $record.failure_reason = Get-FailureSnippet -Text ([string]$runResult.combined)
            }

            Copy-FileIfExists -SourcePath (Join-Path $workspaceDirectory "Target.cs") -DestinationPath (Join-Path $runDirectory "Target.cs")
            Copy-FileIfExists -SourcePath (Join-Path $workspaceDirectory "Target.original.cs") -DestinationPath (Join-Path $runDirectory "Target.original.cs")
            Copy-FileIfExists -SourcePath (Join-Path $workspaceDirectory "TargetHarness.csproj") -DestinationPath (Join-Path $runDirectory "TargetHarness.csproj")
            Copy-FileIfExists -SourcePath (Join-Path $workspaceDirectory "Program.cs") -DestinationPath (Join-Path $runDirectory "Program.cs")

            $runRecordPath = Join-Path $runDirectory "run-record.json"
            ([pscustomobject]$record) | ConvertTo-Json -Depth 20 | Set-Content -Path $runRecordPath

            if (-not $KeepWorkspaces) {
                Remove-Item -LiteralPath $workspaceDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }

            Write-Host ("RUN scenario={0} model={1} effort={2} duration_s={3} exit={4} pass={5} roslyn_calls={6} lsp_calls={7} tokens={8}" -f `
                    $scenario, $model, $effort, $record.duration_seconds, $record.exit_code, $record.run_passed, $record.roslyn_calls, $record.lsp_calls, $record.total_tokens)

            $runRecords.Add([pscustomobject]$record) | Out-Null
        }
    }
}

$recordsArray = @($runRecords | Sort-Object scenario, model, reasoning_effort)
$summary = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    output_root = $outputDirectory
    task_shape = $TaskShape
    models = @($Models)
    reasoning_efforts = @($ReasoningEfforts)
    scenarios = @($Scenarios)
    roslyn_mcp_dll = $roslynMcpDllPath
    lsp_mcp_command = if ([string]::IsNullOrWhiteSpace($resolvedLspMcpCommand)) { $null } else { $resolvedLspMcpCommand }
    lsp_mcp_args = @($resolvedLspMcpArgs)
    runs = $recordsArray
}

$summaryJsonPath = Join-Path $outputDirectory "codex-mcp-interop-summary.json"
$summary | ConvertTo-Json -Depth 30 | Set-Content -Path $summaryJsonPath

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Codex MCP Interop Summary")
$lines.Add("")
$lines.Add("- Generated (UTC): $($summary.generated_utc)")
$lines.Add("- Task shape: $($summary.task_shape)")
$lines.Add("- Models: $([string]::Join(', ', $summary.models))")
$lines.Add("- Reasoning efforts: $([string]::Join(', ', $summary.reasoning_efforts))")
$lines.Add("- Scenarios: $([string]::Join(', ', $summary.scenarios))")
$lines.Add("- Roslyn MCP DLL: $($summary.roslyn_mcp_dll)")
$lines.Add("- LSP MCP command: $($summary.lsp_mcp_command)")
$lines.Add("")
$lines.Add("| Scenario | Model | Effort | Duration (s) | Skipped | Exit | Passed | Roslyn Calls | LSP Calls | Tokens | Failure/Skip Reason |")
$lines.Add("| --- | --- | --- | ---: | --- | ---: | --- | ---: | ---: | ---: | --- |")

foreach ($run in $recordsArray) {
    $reason = if (-not [string]::IsNullOrWhiteSpace([string]$run.skip_reason)) { [string]$run.skip_reason } elseif (-not [string]::IsNullOrWhiteSpace([string]$run.failure_reason)) { [string]$run.failure_reason } else { "" }
    $reason = $reason.Replace("|", "/")
    $lines.Add(("{0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} |" -f `
            $run.scenario, `
            $run.model, `
            $run.reasoning_effort, `
            $run.duration_seconds, `
            $run.skipped, `
            $run.exit_code, `
            $run.run_passed, `
            $run.roslyn_calls, `
            $run.lsp_calls, `
            $run.total_tokens, `
            $reason))
}

$summaryMarkdownPath = Join-Path $outputDirectory "codex-mcp-interop-summary.md"
Set-Content -Path $summaryMarkdownPath -Value ($lines -join [Environment]::NewLine)

Write-Host ("SUMMARY_JSON={0}" -f $summaryJsonPath)
Write-Host ("SUMMARY_MARKDOWN={0}" -f $summaryMarkdownPath)










