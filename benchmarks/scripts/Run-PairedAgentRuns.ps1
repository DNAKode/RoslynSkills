param(
    [string]$OutputRoot = "",
    [string]$IsolationRoot = "",
    [string]$CodexModel = "",
    [string]$ClaudeModel = "",
    [ValidateSet("standard", "brief-first", "surgical", "skill-minimal", "schema-first")][string]$RoslynGuidanceProfile = "standard",
    [string]$CliPublishConfiguration = "Release",
    [switch]$IncludeMcpTreatment,
    [switch]$IncludeClaudeLspTreatment,
    [bool]$FailOnControlContamination = $true,
    [bool]$FailOnLspRoslynContamination = $true,
    [bool]$FailOnMissingLspTools = $false,
    [bool]$FailOnClaudeAuthUnavailable = $true,
    [switch]$KeepIsolatedWorkspaces,
    [switch]$SkipCodex,
    [switch]$SkipClaude,
    [ValidateSet("single-file", "project")][string]$TaskShape = "single-file"
)

$ErrorActionPreference = "Stop"

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputRoot
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
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$IsolationRoot
    )

    if ([string]::IsNullOrWhiteSpace($IsolationRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        $suffix = [Guid]::NewGuid().ToString("n").Substring(0, 8)
        return (Join-Path ([System.IO.Path]::GetTempPath()) "roslynskills-paired-runs/$stamp-$suffix")
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

    $workspaceRepoTop = & cmd /d /c "git -C `"$WorkspaceDirectory`" rev-parse --show-toplevel 2>nul"
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
            "Program.cs",
            "TargetHarness.csproj",
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

function Write-TaskWorkspaceFiles {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$TargetContent,
        [Parameter(Mandatory = $true)][ValidateSet("single-file", "project")][string]$TaskShape
    )

    $targetPath = Join-Path $RunDirectory "Target.cs"
    $targetOriginalPath = Join-Path $RunDirectory "Target.original.cs"
    Set-Content -Path $targetPath -Value $TargetContent -NoNewline
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

function Test-ClaudeAuthentication {
    param()

    $probePrompt = "Reply with OK only."
    $stdOutText = ""
    $stdErrText = ""
    $exitCode = 1

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "cmd.exe"
    $psi.Arguments = "/d /c claude -p -"
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    try {
        [void]$process.Start()
        $process.StandardInput.WriteLine($probePrompt)
        $process.StandardInput.Close()
        $stdOutText = $process.StandardOutput.ReadToEnd()
        $stdErrText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    } catch {
        $stdErrText = "$stdErrText`n$($_.Exception.Message)"
        $exitCode = 1
    } finally {
        if ($process -and -not $process.HasExited) {
            try {
                $process.Kill()
            } catch {
            }
        }
        if ($process) {
            $process.Dispose()
        }
    }

    $combined = ($stdOutText + "`n" + $stdErrText).Trim()
    $authRevoked = ($combined -match "OAuth token revoked" -or $combined -match "Please run /login")
    $ok = ($exitCode -eq 0 -and -not $authRevoked)

    return [ordered]@{
        ok = $ok
        exit_code = $exitCode
        auth_revoked = $authRevoked
        output_preview = if ([string]::IsNullOrWhiteSpace($combined)) { "" } elseif ($combined.Length -le 200) { $combined } else { $combined.Substring(0, 200) }
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
        throw "dotnet publish failed for RoslynSkills.Cli."
    }

    $cliDllPath = Join-Path $publishDirectory "RoslynSkills.Cli.dll"
    if (-not (Test-Path $cliDllPath)) {
        throw "Published RoslynSkills.Cli.dll not found at '$cliDllPath'."
    }

    return [string](Resolve-Path $cliDllPath).Path
}

function Publish-RoslynMcpServer {
    param(
        [Parameter(Mandatory = $true)][string]$McpProjectPath,
        [Parameter(Mandatory = $true)][string]$BundleDirectory,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $publishDirectory = Join-Path $BundleDirectory "tools/roslyn-mcp"
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

    Write-Host ("Publishing Roslyn MCP server once for run bundle: {0}" -f $publishDirectory)
    & dotnet publish $McpProjectPath -c $Configuration -o $publishDirectory --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynSkills.McpServer."
    }

    $mcpDllPath = Join-Path $publishDirectory "RoslynSkills.McpServer.dll"
    if (-not (Test-Path $mcpDllPath)) {
        throw "Published RoslynSkills.McpServer.dll not found at '$mcpDllPath'."
    }

    return [string](Resolve-Path $mcpDllPath).Path
}

function Set-CodexMcpServerConfig {
    param(
        [Parameter(Mandatory = $true)][string]$CodexHomeDirectory,
        [Parameter(Mandatory = $true)][string]$McpDllPath
    )

    New-Item -ItemType Directory -Force -Path $CodexHomeDirectory | Out-Null
    $configPath = Join-Path $CodexHomeDirectory "config.toml"
    $existing = if (Test-Path $configPath -PathType Leaf) { (Get-Content -Path $configPath -Raw) } else { "" }

    $sectionPattern = "(?ms)^\[mcp_servers\.(roslyn|roslyn_mcp)\]\r?\n.*?(?=^\[|\z)"
    $cleaned = [System.Text.RegularExpressions.Regex]::Replace($existing, $sectionPattern, "")
    $cleaned = $cleaned.TrimEnd()

    $dllForwardPath = $McpDllPath -replace "\\", "/"
    $section = @"
[mcp_servers.roslyn]
command = "dotnet"
args = ["$dllForwardPath"]

[mcp_servers.roslyn_mcp]
command = "dotnet"
args = ["$dllForwardPath"]
"@

    $newContent = if ([string]::IsNullOrWhiteSpace($cleaned)) {
        $section
    } else {
        $cleaned + [Environment]::NewLine + [Environment]::NewLine + $section
    }

    Set-Content -Path $configPath -Value $newContent -NoNewline
    return [string](Resolve-Path $configPath).Path
}

function Write-ClaudeMcpConfig {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$McpDllPath
    )

    $configPath = Join-Path $RunDirectory "claude-mcp.json"
    $dllForwardPath = $McpDllPath -replace "\\", "/"
    $config = [ordered]@{
        mcpServers = [ordered]@{
            roslyn = [ordered]@{
                type = "stdio"
                command = "dotnet"
                args = @($dllForwardPath)
                env = @{}
            }
            roslyn_mcp = [ordered]@{
                type = "stdio"
                command = "dotnet"
                args = @($dllForwardPath)
                env = @{}
            }
        }
    }

    $config | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath
    return [string](Resolve-Path $configPath).Path
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
dotnet "$cliDllForBash" "$@"
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
  param([string[]]`$CommandArgs)
  `$raw = & ".\scripts\roscli.cmd" @CommandArgs 2>&1
  `$rawText = if (`$raw -is [System.Array]) { [string]::Join([Environment]::NewLine, `$raw) } else { [string]`$raw }
  if (`$LASTEXITCODE -ne 0) {
    throw "Roslyn helper command failed: $($CommandArgs -join ' ')`n$rawText"
  }

  try {
    return `$rawText | ConvertFrom-Json
  } catch {
    `$jsonStart = `$rawText.IndexOf("{")
    `$jsonEnd = `$rawText.LastIndexOf("}")
    if (`$jsonStart -ge 0 -and `$jsonEnd -gt `$jsonStart) {
      `$jsonCandidate = `$rawText.Substring(`$jsonStart, (`$jsonEnd - `$jsonStart + 1))
      return `$jsonCandidate | ConvertFrom-Json
    }

    throw "Roslyn helper JSON parse failed: $($CommandArgs -join ' ')`n$rawText"
  }
}

`$rename = Invoke-RoscliJson -CommandArgs @(
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
  new_symbol_matches = `$null
  old_symbol_matches = `$null
  diagnostics_errors = `$null
  checks = @()
}

`$newMatches = Invoke-RoscliJson -CommandArgs @(
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
    detail = "expected=`$ExpectedNewExact actual=`$(`$verification.new_symbol_matches)"
  }
}

if (-not [string]::IsNullOrWhiteSpace(`$OldName)) {
  `$oldMatches = Invoke-RoscliJson -CommandArgs @(
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
      detail = "expected=`$ExpectedOldExact actual=`$(`$verification.old_symbol_matches)"
    }
  }
}

if (`$RequireNoDiagnostics) {
  `$diag = Invoke-RoscliJson -CommandArgs @(
    "diag.get_file_diagnostics",
    `$FilePath
  )
  `$verification.diagnostics_errors = [int]`$diag.Data.errors
  `$verification.checks += [ordered]@{
    name = "no_diagnostics_errors"
    passed = (`$verification.diagnostics_errors -eq 0)
    detail = "errors=`$(`$verification.diagnostics_errors)"
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
        "roslyn_mcp",
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
        "RoslynSkills.Cli"
    )

    foreach ($pattern in $patterns) {
        if ($Text.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Test-IsRoslynToolName {
    param([string]$ToolName)

    if ([string]::IsNullOrWhiteSpace($ToolName)) {
        return $false
    }

    if ($ToolName.IndexOf("roslyn", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    return $false
}

function Test-IsLspCommandText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    $patterns = @(
        "csharp-lsp",
        "csharp_lsp",
        "csharp-ls",
        "csharplspmcp",
        "lspuse.csharp",
        "textDocument/definition",
        "textDocument/references",
        "textDocument/rename"
    )

    foreach ($pattern in $patterns) {
        if ($Text.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Test-IsLspToolName {
    param([string]$ToolName)

    if ([string]::IsNullOrWhiteSpace($ToolName)) {
        return $false
    }

    if ($ToolName.IndexOf("csharp-lsp", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    if ($ToolName.IndexOf("csharp_lsp", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    if ($ToolName.IndexOf("csharp-ls", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    if ($ToolName.IndexOf("lsp", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    return $false
}

function Get-CodexRoslynInvocationText {
    param([object]$Item)

    if ($null -eq $Item) {
        return $null
    }

    $commandText = $null
    $commandProperty = $Item.PSObject.Properties["command"]
    if ($null -ne $commandProperty -and $null -ne $commandProperty.Value) {
        $candidateCommand = [string]$commandProperty.Value
        if ((-not [string]::IsNullOrWhiteSpace($candidateCommand)) -and (Test-IsRoslynCommandText -Text $candidateCommand)) {
            return $candidateCommand
        }
        $commandText = $candidateCommand
    }

    $toolNameCandidates = New-Object System.Collections.Generic.List[string]
    foreach ($propertyName in @("tool_name", "toolName", "name", "server_name", "mcp_tool_name")) {
        $property = $Item.PSObject.Properties[$propertyName]
        if ($null -ne $property -and $null -ne $property.Value) {
            $text = [string]$property.Value
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $toolNameCandidates.Add($text)
            }
        }
    }

    foreach ($toolName in $toolNameCandidates) {
        if (Test-IsRoslynToolName -ToolName $toolName) {
            return ("mcp:{0}" -f $toolName)
        }
    }

    foreach ($propertyName in @("input", "arguments")) {
        $property = $Item.PSObject.Properties[$propertyName]
        if ($null -ne $property -and $null -ne $property.Value) {
            try {
                $serialized = ($property.Value | ConvertTo-Json -Depth 8 -Compress)
                if ((-not [string]::IsNullOrWhiteSpace($serialized)) -and (Test-IsRoslynCommandText -Text $serialized)) {
                    return $serialized
                }
            } catch {
                # Ignore non-serializable payload hints.
            }
        }
    }

    $itemType = [string]$Item.type
    if ($itemType.IndexOf("mcp", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        if ($toolNameCandidates.Count -gt 0) {
            foreach ($toolName in $toolNameCandidates) {
                if (Test-IsRoslynToolName -ToolName $toolName) {
                    return ("mcp:{0}" -f $toolName)
                }
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($commandText) -and (Test-IsRoslynCommandText -Text $commandText)) {
            return $commandText
        }
    }

    return $null
}

function Get-CodexLspInvocationText {
    param([object]$Item)

    if ($null -eq $Item) {
        return $null
    }

    $commandProperty = $Item.PSObject.Properties["command"]
    if ($null -ne $commandProperty -and $null -ne $commandProperty.Value) {
        $candidateCommand = [string]$commandProperty.Value
        if ((-not [string]::IsNullOrWhiteSpace($candidateCommand)) -and (Test-IsLspCommandText -Text $candidateCommand)) {
            return $candidateCommand
        }
    }

    foreach ($propertyName in @("tool_name", "toolName", "name", "server_name", "mcp_tool_name")) {
        $property = $Item.PSObject.Properties[$propertyName]
        if ($null -eq $property -or $null -eq $property.Value) {
            continue
        }

        $toolName = [string]$property.Value
        if (Test-IsLspToolName -ToolName $toolName) {
            return ("lsp:{0}" -f $toolName)
        }
    }

    foreach ($propertyName in @("input", "arguments")) {
        $property = $Item.PSObject.Properties[$propertyName]
        if ($null -eq $property -or $null -eq $property.Value) {
            continue
        }

        try {
            $serialized = ($property.Value | ConvertTo-Json -Depth 8 -Compress)
            if ((-not [string]::IsNullOrWhiteSpace($serialized)) -and (Test-IsLspCommandText -Text $serialized)) {
                return $serialized
            }
        } catch {
            # Ignore non-serializable payload hints.
        }
    }

    return $null
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

        if ($null -eq $event.item) {
            continue
        }

        $invocationText = Get-CodexRoslynInvocationText -Item $event.item
        if ([string]::IsNullOrWhiteSpace($invocationText)) {
            continue
        }

        $itemId = [string]$event.item.id
        if ([string]::IsNullOrWhiteSpace($itemId)) {
            $itemId = [Guid]::NewGuid().ToString("n")
        }

        if ($seenInvocationIds.Add($itemId)) {
            $invocations.Add($invocationText)
        }

        if ($event.type -eq "item.completed") {
            $wasSuccessful = $false
            $statusValue = [string]$event.item.status
            if (-not [string]::IsNullOrWhiteSpace($statusValue) -and $statusValue.Equals("failed", [System.StringComparison]::OrdinalIgnoreCase)) {
                $wasSuccessful = $false
            } elseif ($null -ne $event.item.error) {
                $wasSuccessful = $false
            } elseif ($null -ne $event.item.exit_code) {
                $wasSuccessful = ([int]$event.item.exit_code -eq 0)
            } elseif ($null -ne $event.item.success) {
                $wasSuccessful = [bool]$event.item.success
            } elseif ($null -ne $event.item.is_error) {
                $wasSuccessful = (-not [bool]$event.item.is_error)
            } else {
                $wasSuccessful = $true
            }

            if ($wasSuccessful -and $successfulInvocationIds.Add($itemId)) {
                $successful++
            }
        }
    }

    return @{
        Commands = $invocations.ToArray()
        Successful = $successful
    }
}

function Get-CodexLspUsage {
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

        if ($null -eq $event.item) {
            continue
        }

        $invocationText = Get-CodexLspInvocationText -Item $event.item
        if ([string]::IsNullOrWhiteSpace($invocationText)) {
            continue
        }

        $itemId = [string]$event.item.id
        if ([string]::IsNullOrWhiteSpace($itemId)) {
            $itemId = [Guid]::NewGuid().ToString("n")
        }

        if ($seenInvocationIds.Add($itemId)) {
            $invocations.Add($invocationText)
        }

        if ($event.type -eq "item.completed") {
            $wasSuccessful = $false
            $statusValue = [string]$event.item.status
            if (-not [string]::IsNullOrWhiteSpace($statusValue) -and $statusValue.Equals("failed", [System.StringComparison]::OrdinalIgnoreCase)) {
                $wasSuccessful = $false
            } elseif ($null -ne $event.item.error) {
                $wasSuccessful = $false
            } elseif ($null -ne $event.item.exit_code) {
                $wasSuccessful = ([int]$event.item.exit_code -eq 0)
            } elseif ($null -ne $event.item.success) {
                $wasSuccessful = [bool]$event.item.success
            } elseif ($null -ne $event.item.is_error) {
                $wasSuccessful = (-not [bool]$event.item.is_error)
            } else {
                $wasSuccessful = $true
            }

            if ($wasSuccessful -and $successfulInvocationIds.Add($itemId)) {
                $successful++
            }
        }
    }

    return @{
        Commands = $invocations.ToArray()
        Successful = $successful
    }
}

function Get-ClaudeRoslynUsage {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $roslynToolUsesById = @{}
    $invocations = New-Object System.Collections.Generic.List[string]
    $successful = 0
    $attemptedToolUseIds = New-Object System.Collections.Generic.HashSet[string]

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
                if ($content.type -ne "tool_use") {
                    continue
                }

                $toolUseId = [string]$content.id
                if ([string]::IsNullOrWhiteSpace($toolUseId)) {
                    continue
                }

                $toolName = [string]$content.name
                $invocationText = $null
                if ($toolName -eq "Bash") {
                    $commandText = [string]$content.input.command
                    if (Test-IsRoslynCommandText -Text $commandText) {
                        $invocationText = $commandText
                    }
                } elseif ($toolName -eq "ReadMcpResourceTool") {
                    $resourceUri = [string]$content.input.uri
                    if (-not [string]::IsNullOrWhiteSpace($resourceUri) -and
                        $resourceUri.IndexOf("roslyn://command/", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        $invocationText = ("mcp:{0}" -f $resourceUri)
                    }
                } elseif (Test-IsRoslynToolName -ToolName $toolName) {
                    $invocationText = ("mcp:{0}" -f $toolName)
                }

                if (-not [string]::IsNullOrWhiteSpace($invocationText)) {
                    $roslynToolUsesById[$toolUseId] = $invocationText
                    if ($attemptedToolUseIds.Add($toolUseId)) {
                        $invocations.Add($invocationText)
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
            if ([string]::IsNullOrWhiteSpace($toolUseId) -or -not $roslynToolUsesById.ContainsKey($toolUseId)) {
                continue
            }

            $resultText = ""
            if ($content.content -is [System.Array]) {
                try {
                    $resultText = ($content.content | ConvertTo-Json -Depth 8 -Compress)
                } catch {
                    $resultText = [string]$content.content
                }
            } else {
                $resultText = [string]$content.content
            }
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

function Get-ClaudeLspUsage {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $lspToolUsesById = @{}
    $invocations = New-Object System.Collections.Generic.List[string]
    $successful = 0
    $attemptedToolUseIds = New-Object System.Collections.Generic.HashSet[string]

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
                if ($content.type -ne "tool_use") {
                    continue
                }

                $toolUseId = [string]$content.id
                if ([string]::IsNullOrWhiteSpace($toolUseId)) {
                    continue
                }

                $toolName = [string]$content.name
                $invocationText = $null
                if ($toolName -eq "Bash") {
                    $commandText = [string]$content.input.command
                    if (Test-IsLspCommandText -Text $commandText) {
                        $invocationText = $commandText
                    }
                } elseif ($toolName -eq "ReadMcpResourceTool") {
                    $resourceUri = [string]$content.input.uri
                    if (-not [string]::IsNullOrWhiteSpace($resourceUri) -and
                        (Test-IsLspCommandText -Text $resourceUri)) {
                        $invocationText = ("lsp:{0}" -f $resourceUri)
                    }
                } elseif (Test-IsLspToolName -ToolName $toolName) {
                    $invocationText = ("lsp:{0}" -f $toolName)
                }

                if (-not [string]::IsNullOrWhiteSpace($invocationText)) {
                    $lspToolUsesById[$toolUseId] = $invocationText
                    if ($attemptedToolUseIds.Add($toolUseId)) {
                        $invocations.Add($invocationText)
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
            if ([string]::IsNullOrWhiteSpace($toolUseId) -or -not $lspToolUsesById.ContainsKey($toolUseId)) {
                continue
            }

            $resultText = ""
            if ($content.content -is [System.Array]) {
                try {
                    $resultText = ($content.content | ConvertTo-Json -Depth 8 -Compress)
                } catch {
                    $resultText = [string]$content.content
                }
            } else {
                $resultText = [string]$content.content
            }
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

function Get-LspAvailability {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TranscriptPath
    )

    $indicators = New-Object System.Collections.Generic.HashSet[string]

    foreach ($line in (Get-Content -Path $TranscriptPath -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json
        } catch {
            continue
        }

        if ($Agent -eq "claude" -and $event.type -eq "system" -and [string]$event.subtype -eq "init") {
            if ($null -ne $event.tools -and $event.tools -is [System.Array]) {
                foreach ($tool in $event.tools) {
                    $toolName = [string]$tool
                    if (Test-IsLspToolName -ToolName $toolName -or Test-IsLspCommandText -Text $toolName) {
                        $indicators.Add(("tool:{0}" -f $toolName)) | Out-Null
                    }
                }
            }

            if ($null -ne $event.mcp_servers -and $event.mcp_servers -is [System.Array]) {
                foreach ($server in $event.mcp_servers) {
                    $serverName = [string]$server.name
                    if (Test-IsLspToolName -ToolName $serverName -or Test-IsLspCommandText -Text $serverName) {
                        $indicators.Add(("mcp_server:{0}" -f $serverName)) | Out-Null
                    }
                }
            }

            if ($null -ne $event.plugins -and $event.plugins -is [System.Array]) {
                foreach ($plugin in $event.plugins) {
                    $pluginName = [string]$plugin.name
                    $pluginPath = [string]$plugin.path
                    if (Test-IsLspToolName -ToolName $pluginName -or Test-IsLspCommandText -Text $pluginName) {
                        $indicators.Add(("plugin:{0}" -f $pluginName)) | Out-Null
                    } elseif (Test-IsLspCommandText -Text $pluginPath) {
                        $indicators.Add(("plugin_path:{0}" -f $pluginPath)) | Out-Null
                    }
                }
            }
        }
    }

    $indicatorList = New-Object System.Collections.Generic.List[string]
    foreach ($indicator in $indicators) {
        $indicatorList.Add([string]$indicator) | Out-Null
    }

    return @{
        available = ($indicatorList.Count -gt 0)
        indicators = $indicatorList.ToArray()
    }
}

function Get-LiteralOccurrenceCount {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle
    )

    if ([string]::IsNullOrEmpty($Text) -or [string]::IsNullOrEmpty($Needle)) {
        return 0
    }

    $count = 0
    $start = 0
    while ($start -lt $Text.Length) {
        $index = $Text.IndexOf($Needle, $start, [System.StringComparison]::Ordinal)
        if ($index -lt 0) {
            break
        }

        $count++
        $start = $index + $Needle.Length
    }

    return $count
}

function Get-RoslynWorkspaceContextUsage {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    $content = Get-Content -Path $TranscriptPath -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) {
        $content = ""
    }

    # Escaped MCP payload form (for example: \"workspace_context\":{\"mode\":\"workspace\" ... }).
    $workspaceEscaped = Get-LiteralOccurrenceCount -Text $content -Needle 'workspace_context\\\":{\\\"mode\\\":\\\"workspace'
    $adHocEscaped = Get-LiteralOccurrenceCount -Text $content -Needle 'workspace_context\\\":{\\\"mode\\\":\\\"ad_hoc'

    # Plain JSON form emitted by direct CLI envelopes.
    $workspacePlain = ([regex]::Matches(
            $content,
            '"workspace_context"\s*:\s*\{\s*"mode"\s*:\s*"workspace"',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        )).Count
    $adHocPlain = ([regex]::Matches(
            $content,
            '"workspace_context"\s*:\s*\{\s*"mode"\s*:\s*"ad_hoc"',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        )).Count

    $workspaceCount = $workspaceEscaped + $workspacePlain
    $adHocCount = $adHocEscaped + $adHocPlain
    $total = $workspaceCount + $adHocCount

    $distinctModes = @()
    if ($workspaceCount -gt 0) {
        $distinctModes += "workspace"
    }
    if ($adHocCount -gt 0) {
        $distinctModes += "ad_hoc"
    }

    $lastMode = $null
    if ($total -gt 0) {
        $lastWorkspaceIndex = $content.LastIndexOf('workspace_context\\\":{\\\"mode\\\":\\\"workspace', [System.StringComparison]::Ordinal)
        $lastAdHocIndex = $content.LastIndexOf('workspace_context\\\":{\\\"mode\\\":\\\"ad_hoc', [System.StringComparison]::Ordinal)
        if ($lastWorkspaceIndex -lt 0) {
            $lastWorkspaceIndex = $content.LastIndexOf('"workspace_context"', [System.StringComparison]::Ordinal)
        }
        if ($lastAdHocIndex -lt 0) {
            $lastAdHocIndex = $content.LastIndexOf('"workspace_context"', [System.StringComparison]::Ordinal)
        }

        $lastMode = if ($lastAdHocIndex -gt $lastWorkspaceIndex) { "ad_hoc" } else { "workspace" }
    }

    return @{
        workspace_count = $workspaceCount
        ad_hoc_count = $adHocCount
        total_count = $total
        distinct_modes = $distinctModes
        last_mode = $lastMode
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
        $lspCommandRoundTrips = 0
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
                $itemType = [string]$event.item.type
                $isCommandRoundTrip = ($itemType -eq "command_execution" -or $itemType.IndexOf("mcp", [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
                if ($isCommandRoundTrip) {
                    $commandRoundTrips++
                    $roslynInvocationText = Get-CodexRoslynInvocationText -Item $event.item
                    if (-not [string]::IsNullOrWhiteSpace($roslynInvocationText)) {
                        $roslynCommandRoundTrips++
                    }
                    $lspInvocationText = Get-CodexLspInvocationText -Item $event.item
                    if (-not [string]::IsNullOrWhiteSpace($lspInvocationText)) {
                        $lspCommandRoundTrips++
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
            lsp_command_round_trips = $lspCommandRoundTrips
            non_lsp_command_round_trips = [Math]::Max(0, ($commandRoundTrips - $lspCommandRoundTrips))
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
    $lspCommandRoundTripsClaude = 0
    $commandOutputCharsClaude = 0
    $agentMessageCharsClaude = 0
    $promptTokensClaude = $null
    $completionTokensClaude = $null
    $cacheReadClaude = $null
    $cacheCreationClaude = $null
    foreach ($event in $events) {
        if ($event.type -eq "assistant" -and $null -ne $event.message -and $null -ne $event.message.content) {
            foreach ($content in $event.message.content) {
                if ($content.type -eq "tool_use") {
                    $commandRoundTripsClaude++
                    $toolName = [string]$content.name
                    if ($toolName -eq "Bash") {
                        $commandText = [string]$content.input.command
                        if (Test-IsRoslynCommandText -Text $commandText) {
                            $roslynCommandRoundTripsClaude++
                        }
                        if (Test-IsLspCommandText -Text $commandText) {
                            $lspCommandRoundTripsClaude++
                        }
                    } elseif (Test-IsRoslynToolName -ToolName $toolName) {
                        $roslynCommandRoundTripsClaude++
                    } elseif (Test-IsLspToolName -ToolName $toolName) {
                        $lspCommandRoundTripsClaude++
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
        lsp_command_round_trips = $lspCommandRoundTripsClaude
        non_lsp_command_round_trips = [Math]::Max(0, ($commandRoundTripsClaude - $lspCommandRoundTripsClaude))
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
        [Parameter(Mandatory = $false)][hashtable]$EnvironmentOverrides = @{},
        [Parameter(Mandatory = $false)][int]$TimeoutSeconds = 180
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
    $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "roslynskills-agent-process"
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
            $appliedEnvironmentKeys.Add($keyName)
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

        $timedOut = $false
        $timeoutMessage = $null
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            try {
                $process.Kill()
            } catch {
                # Best-effort termination.
            }

            $process.WaitForExit()
            $timeoutMessage = ("Agent process timed out after {0} second(s) and was terminated." -f $TimeoutSeconds)
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

        if (-not [string]::IsNullOrWhiteSpace($timeoutMessage)) {
            $outputChunks.Add($timeoutMessage) | Out-Null
        }

        $combinedOutput = if ($outputChunks.Count -eq 0) {
            ""
        } else {
            [string]::Join([Environment]::NewLine, $outputChunks)
        }

        if (-not [string]::IsNullOrWhiteSpace($combinedOutput)) {
            $combinedOutput | Tee-Object -FilePath $TranscriptPath | Out-Host
        } else {
            Set-Content -Path $TranscriptPath -Value "" -NoNewline
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

        foreach ($path in @($stdinPath, $stdoutPath, $stderrPath)) {
            if (Test-Path $path -PathType Leaf) {
                Remove-Item -Path $path -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return $exitCode
}

function Get-CsharpLsLauncherPath {
    $candidatePaths = @(
        (Join-Path $env:USERPROFILE ".dotnet\tools\csharp-ls.exe"),
        (Join-Path $env:USERPROFILE ".dotnet\tools\csharp-ls")
    )

    foreach ($candidate in $candidatePaths) {
        if (Test-Path $candidate -PathType Leaf) {
            return [string](Resolve-Path $candidate).Path
        }
    }

    $command = Get-Command "csharp-ls" -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace([string]$command.Source)) {
        return [string](Resolve-Path $command.Source).Path
    }

    return $null
}

function New-AgentEnvironmentOverrides {
    param(
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $false)][string]$Mode = ""
    )

    $agentHomeRoot = Join-Path $RunDirectory ".agent-home"
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

    if ($Agent -eq "codex") {
        $existingCodexHome = if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
            Join-Path $env:USERPROFILE ".codex"
        } else {
            [string]$env:CODEX_HOME
        }

        if (Test-Path $existingCodexHome -PathType Container) {
            foreach ($fileName in @("auth.json", "cap_sid", "version.json", "models_cache.json", "internal_storage.json")) {
                Copy-FileIfExists `
                    -SourcePath (Join-Path $existingCodexHome $fileName) `
                    -DestinationPath (Join-Path $codexHomeRoot $fileName)
            }
        }

        $overrides["CODEX_HOME"] = $codexHomeRoot
    } elseif ($Agent -eq "claude") {
        $existingClaudeConfig = if ([string]::IsNullOrWhiteSpace($env:CLAUDE_CONFIG_DIR)) {
            Join-Path $env:USERPROFILE ".claude"
        } else {
            [string]$env:CLAUDE_CONFIG_DIR
        }

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

    if ($Mode -eq "treatment-lsp") {
        $lspShimDirectory = Join-Path $RunDirectory ".lsp-tools"
        New-Item -ItemType Directory -Force -Path $lspShimDirectory | Out-Null

        $csharpLsPath = Get-CsharpLsLauncherPath
        if (-not [string]::IsNullOrWhiteSpace($csharpLsPath)) {
            $csharpLsShimCmdPath = Join-Path $lspShimDirectory "csharp-ls.cmd"
            $csharpLsShimCmd = @"
@echo off
"$csharpLsPath" %*
"@
            Set-Content -Path $csharpLsShimCmdPath -Value $csharpLsShimCmd -NoNewline
            $overrides["ROSLYNSKILLS_CSHARP_LSP_PATH"] = $csharpLsPath
        }

        $existingPath = [System.Environment]::GetEnvironmentVariable("PATH", "Process")
        if (-not [string]::IsNullOrWhiteSpace($existingPath)) {
            $filteredPathEntries = New-Object System.Collections.Generic.List[string]
            foreach ($entry in ($existingPath -split ';')) {
                $candidate = [string]$entry
                if ([string]::IsNullOrWhiteSpace($candidate)) {
                    continue
                }

                $normalized = $candidate.Trim().Trim('"').TrimEnd('\', '/') -replace "/", "\"
                if ($normalized -match "(?i)[\\/]\\.dotnet[\\/]tools$") {
                    continue
                }

                $filteredPathEntries.Add($candidate.Trim()) | Out-Null
            }

            if ($filteredPathEntries.Count -gt 0) {
                $filteredPath = [string]::Join(";", $filteredPathEntries)
                $overrides["PATH"] = "{0};{1}" -f $lspShimDirectory, $filteredPath
            } else {
                $overrides["PATH"] = $lspShimDirectory
            }
        }
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

    $rawDiagnosticsOutput = $null
    $diagExitCode = 1
    Push-Location $RunDirectory
    try {
        $rawDiagnosticsOutput = & dotnet $CliDllPath diag.get_file_diagnostics Target.cs
        $diagExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
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

function Get-RunModeSortOrder {
    param([string]$Mode)

    switch ($Mode) {
        "control" { return 0 }
        "treatment" { return 1 }
        "treatment-mcp" { return 2 }
        "treatment-lsp" { return 3 }
        default { return 9 }
    }
}

function Write-PairedRunSummaryMarkdown {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Runs,
        [Parameter(Mandatory = $true)][string]$MarkdownPath
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Paired Agent Run Summary")
    $lines.Add("")
    $guidanceProfiles = @(
        $Runs |
        ForEach-Object {
            if ($null -eq $_) {
                return $null
            }

            if ($_ -is [System.Collections.IDictionary]) {
                return $_["roslyn_guidance_profile"]
            }

            if ($_.PSObject.Properties.Name -contains "roslyn_guidance_profile") {
                return $_.roslyn_guidance_profile
            }

            return $null
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Sort-Object -Unique
    )
    if ($guidanceProfiles.Count -gt 0) {
        $lines.Add("- Roslyn guidance profile(s): $([string]::Join(', ', $guidanceProfiles))")
        $lines.Add("")
    }

    if ($Runs.Count -eq 0) {
        $lines.Add("No runs executed.")
    } else {
        $sortedRuns = @(
            $Runs | Sort-Object @{ Expression = "agent"; Ascending = $true }, @{ Expression = {
                    Get-RunModeSortOrder -Mode ([string]$_.mode)
                }; Ascending = $true }
        )

        $lines.Add("| Agent | Mode | Exit | Run Passed | Constraints Passed | Control Contamination | Roslyn Used | Roslyn Calls (ok/attempted) | Workspace Ctx (workspace/ad_hoc) | LSP Used | LSP Calls (ok/attempted) | Duration (s) | Model Total Tokens | Cache-inclusive Tokens | Round Trips | Roslyn Round Trips | LSP Round Trips | Command Output Chars | Agent Message Chars |")
        $lines.Add("| --- | --- | ---: | --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")

        foreach ($run in $sortedRuns) {
            $roslynCalls = ("{0}/{1}" -f (Convert-ToSummaryValue $run.roslyn_successful_calls), (Convert-ToSummaryValue $run.roslyn_attempted_calls))
            $workspaceModes = ("{0}/{1}" -f (Convert-ToSummaryValue $run.roslyn_workspace_mode_workspace_count), (Convert-ToSummaryValue $run.roslyn_workspace_mode_ad_hoc_count))
            $lspCalls = ("{0}/{1}" -f (Convert-ToSummaryValue $run.lsp_successful_calls), (Convert-ToSummaryValue $run.lsp_attempted_calls))
            $line = "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} | {15} | {16} | {17} | {18} |" -f `
                (Convert-ToSummaryValue $run.agent), `
                (Convert-ToSummaryValue $run.mode), `
                (Convert-ToSummaryValue $run.exit_code), `
                (Convert-ToSummaryValue $run.run_passed), `
                (Convert-ToSummaryValue $run.constraint_checks_passed), `
                (Convert-ToSummaryValue $run.control_contamination_detected), `
                (Convert-ToSummaryValue $run.roslyn_used), `
                $roslynCalls, `
                $workspaceModes, `
                (Convert-ToSummaryValue $run.lsp_used), `
                $lspCalls, `
                (Convert-ToSummaryValue $run.duration_seconds), `
                (Convert-ToSummaryValue $run.total_tokens), `
                (Convert-ToSummaryValue $run.cache_inclusive_total_tokens), `
                (Convert-ToSummaryValue $run.command_round_trips), `
                (Convert-ToSummaryValue $run.roslyn_command_round_trips), `
                (Convert-ToSummaryValue $run.lsp_command_round_trips), `
                (Convert-ToSummaryValue $run.token_attribution.command_output_chars), `
                (Convert-ToSummaryValue $run.token_attribution.agent_message_chars)
            $lines.Add($line)
        }

        $lines.Add("")
        $lines.Add("## Agent Breakout (Control vs Treatment)")
        $lines.Add("")
        $lines.Add("| Agent | Control Duration (s) | Treatment Duration (s) | Delta (s) | Ratio | Control Tokens | Treatment Tokens | Token Delta | Token Ratio | Treatment Roslyn Calls (ok/attempted) |")
        $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |")

        foreach ($agentGroup in ($sortedRuns | Group-Object -Property agent | Sort-Object Name)) {
            $control = @($agentGroup.Group | Where-Object { $_.mode -eq "control" } | Select-Object -First 1)
            $treatment = @($agentGroup.Group | Where-Object { $_.mode -eq "treatment" } | Select-Object -First 1)
            if ($control.Count -eq 0 -or $treatment.Count -eq 0) {
                continue
            }
            $agentName = Convert-ToSummaryValue $control[0].agent

            $controlDuration = [double]$control[0].duration_seconds
            $treatmentDuration = [double]$treatment[0].duration_seconds
            $durationDelta = [Math]::Round(($treatmentDuration - $controlDuration), 3)
            $durationRatio = $null
            if ($controlDuration -ne 0.0) {
                $durationRatio = [Math]::Round(($treatmentDuration / $controlDuration), 3)
            }

            $controlTokens = [double]$control[0].total_tokens
            $treatmentTokens = [double]$treatment[0].total_tokens
            $tokenDelta = [Math]::Round(($treatmentTokens - $controlTokens), 0)
            $tokenRatio = $null
            if ($controlTokens -ne 0.0) {
                $tokenRatio = [Math]::Round(($treatmentTokens / $controlTokens), 3)
            }

            $treatmentRoslynCalls = ("{0}/{1}" -f (Convert-ToSummaryValue $treatment[0].roslyn_successful_calls), (Convert-ToSummaryValue $treatment[0].roslyn_attempted_calls))
            $line = "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f `
                $agentName, `
                (Convert-ToSummaryValue $controlDuration), `
                (Convert-ToSummaryValue $treatmentDuration), `
                (Convert-ToSummaryValue $durationDelta), `
                (Convert-ToSummaryValue $durationRatio), `
                (Convert-ToSummaryValue ([Math]::Round($controlTokens, 0))), `
                (Convert-ToSummaryValue ([Math]::Round($treatmentTokens, 0))), `
                (Convert-ToSummaryValue $tokenDelta), `
                (Convert-ToSummaryValue $tokenRatio), `
                $treatmentRoslynCalls
            $lines.Add($line)
        }

        $lines.Add("")
        $lines.Add("## Agent Breakout (Control vs Treatment-MCP)")
        $lines.Add("")
        $lines.Add("| Agent | Control Duration (s) | Treatment-MCP Duration (s) | Delta (s) | Ratio | Control Tokens | Treatment-MCP Tokens | Token Delta | Token Ratio | Treatment-MCP Roslyn Calls (ok/attempted) |")
        $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |")

        foreach ($agentGroup in ($sortedRuns | Group-Object -Property agent | Sort-Object Name)) {
            $control = @($agentGroup.Group | Where-Object { $_.mode -eq "control" } | Select-Object -First 1)
            $treatmentMcp = @($agentGroup.Group | Where-Object { $_.mode -eq "treatment-mcp" } | Select-Object -First 1)
            if ($control.Count -eq 0 -or $treatmentMcp.Count -eq 0) {
                continue
            }
            $agentName = Convert-ToSummaryValue $control[0].agent

            $controlDuration = [double]$control[0].duration_seconds
            $mcpDuration = [double]$treatmentMcp[0].duration_seconds
            $durationDelta = [Math]::Round(($mcpDuration - $controlDuration), 3)
            $durationRatio = $null
            if ($controlDuration -ne 0.0) {
                $durationRatio = [Math]::Round(($mcpDuration / $controlDuration), 3)
            }

            $controlTokens = [double]$control[0].total_tokens
            $mcpTokens = [double]$treatmentMcp[0].total_tokens
            $tokenDelta = [Math]::Round(($mcpTokens - $controlTokens), 0)
            $tokenRatio = $null
            if ($controlTokens -ne 0.0) {
                $tokenRatio = [Math]::Round(($mcpTokens / $controlTokens), 3)
            }

            $mcpRoslynCalls = ("{0}/{1}" -f (Convert-ToSummaryValue $treatmentMcp[0].roslyn_successful_calls), (Convert-ToSummaryValue $treatmentMcp[0].roslyn_attempted_calls))
            $line = "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f `
                $agentName, `
                (Convert-ToSummaryValue $controlDuration), `
                (Convert-ToSummaryValue $mcpDuration), `
                (Convert-ToSummaryValue $durationDelta), `
                (Convert-ToSummaryValue $durationRatio), `
                (Convert-ToSummaryValue ([Math]::Round($controlTokens, 0))), `
                (Convert-ToSummaryValue ([Math]::Round($mcpTokens, 0))), `
                (Convert-ToSummaryValue $tokenDelta), `
                (Convert-ToSummaryValue $tokenRatio), `
                $mcpRoslynCalls
            $lines.Add($line)
        }

        $lines.Add("")
        $lines.Add("## Agent Breakout (Control vs Treatment-LSP)")
        $lines.Add("")
        $lines.Add("| Agent | Control Duration (s) | Treatment-LSP Duration (s) | Delta (s) | Ratio | Control Tokens | Treatment-LSP Tokens | Token Delta | Token Ratio | Treatment-LSP Calls (ok/attempted) |")
        $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |")

        foreach ($agentGroup in ($sortedRuns | Group-Object -Property agent | Sort-Object Name)) {
            $control = @($agentGroup.Group | Where-Object { $_.mode -eq "control" } | Select-Object -First 1)
            $treatmentLsp = @($agentGroup.Group | Where-Object { $_.mode -eq "treatment-lsp" } | Select-Object -First 1)
            if ($control.Count -eq 0 -or $treatmentLsp.Count -eq 0) {
                continue
            }
            $agentName = Convert-ToSummaryValue $control[0].agent

            $controlDuration = [double]$control[0].duration_seconds
            $lspDuration = [double]$treatmentLsp[0].duration_seconds
            $durationDelta = [Math]::Round(($lspDuration - $controlDuration), 3)
            $durationRatio = $null
            if ($controlDuration -ne 0.0) {
                $durationRatio = [Math]::Round(($lspDuration / $controlDuration), 3)
            }

            $controlTokens = [double]$control[0].total_tokens
            $lspTokens = [double]$treatmentLsp[0].total_tokens
            $tokenDelta = [Math]::Round(($lspTokens - $controlTokens), 0)
            $tokenRatio = $null
            if ($controlTokens -ne 0.0) {
                $tokenRatio = [Math]::Round(($lspTokens / $controlTokens), 3)
            }

            $lspCalls = ("{0}/{1}" -f (Convert-ToSummaryValue $treatmentLsp[0].lsp_successful_calls), (Convert-ToSummaryValue $treatmentLsp[0].lsp_attempted_calls))
            $line = "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f `
                $agentName, `
                (Convert-ToSummaryValue $controlDuration), `
                (Convert-ToSummaryValue $lspDuration), `
                (Convert-ToSummaryValue $durationDelta), `
                (Convert-ToSummaryValue $durationRatio), `
                (Convert-ToSummaryValue ([Math]::Round($controlTokens, 0))), `
                (Convert-ToSummaryValue ([Math]::Round($lspTokens, 0))), `
                (Convert-ToSummaryValue $tokenDelta), `
                (Convert-ToSummaryValue $tokenRatio), `
                $lspCalls
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
        [Parameter(Mandatory = $false)][string]$McpDllPath = "",
        [Parameter(Mandatory = $false)][bool]$EnableMcp = $false,
        [Parameter(Mandatory = $false)][bool]$FailOnControlContamination = $true,
        [Parameter(Mandatory = $false)][bool]$FailOnLspRoslynContamination = $true,
        [Parameter(Mandatory = $false)][bool]$FailOnMissingLspTools = $false,
        [Parameter(Mandatory = $false)][bool]$KeepIsolatedWorkspace = $false,
        [Parameter(Mandatory = $false)][string]$Model = "",
        [Parameter(Mandatory = $false)][ValidateSet("single-file", "project")][string]$TaskShape = "single-file"
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
        Write-TaskWorkspaceFiles -RunDirectory $workspaceDirectory -TargetContent $TargetContent -TaskShape $TaskShape
        Set-Content -Path $promptPath -Value $PromptText -NoNewline
        Set-Content -Path $transcriptPath -Value "" -NoNewline

        if ($Mode -eq "treatment") {
            Write-RoslynHelperScripts -RunDirectory $workspaceDirectory -CliDllPath $CliDllPath
        }

        $environmentOverrides = New-AgentEnvironmentOverrides -Agent $Agent -RunDirectory $workspaceDirectory -Mode $Mode
        $codexMcpConfigPath = $null
        $claudeMcpConfigPath = $null
        if ($EnableMcp) {
            if ([string]::IsNullOrWhiteSpace($McpDllPath)) {
                throw "EnableMcp was set but no MCP server DLL path was provided."
            }

            if ($Agent -eq "codex") {
                $codexHome = [string]$environmentOverrides["CODEX_HOME"]
                if ([string]::IsNullOrWhiteSpace($codexHome)) {
                    throw "CODEX_HOME override was not available for MCP setup."
                }

                $codexMcpConfigPath = Set-CodexMcpServerConfig -CodexHomeDirectory $codexHome -McpDllPath $McpDllPath
            } elseif ($Agent -eq "claude") {
                $claudeMcpConfigPath = Write-ClaudeMcpConfig -RunDirectory $workspaceDirectory -McpDllPath $McpDllPath
            }
        }

        $exitCode = 1
        $agentDurationSeconds = $null
        $agentStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
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
                if ($EnableMcp -and -not [string]::IsNullOrWhiteSpace($claudeMcpConfigPath)) {
                    $args += @("--mcp-config", $claudeMcpConfigPath, "--strict-mcp-config")
                }

                $exitCode = Invoke-AgentProcess -Executable $claudeExecutable -Arguments $args -PromptText $PromptText -TranscriptPath $transcriptPath -EnvironmentOverrides $environmentOverrides
            } else {
                throw "Unsupported agent '$Agent'."
            }
        } finally {
            Pop-Location
            $agentStopwatch.Stop()
            $agentDurationSeconds = [Math]::Round($agentStopwatch.Elapsed.TotalSeconds, 3)
        }

        $diffHasChanges = Get-Diff -RunDirectory $workspaceDirectory

        $usage = if ($Agent -eq "codex") {
            Get-CodexRoslynUsage -TranscriptPath $transcriptPath
        } else {
            Get-ClaudeRoslynUsage -TranscriptPath $transcriptPath
        }
        $lspUsage = if ($Agent -eq "codex") {
            Get-CodexLspUsage -TranscriptPath $transcriptPath
        } else {
            Get-ClaudeLspUsage -TranscriptPath $transcriptPath
        }
        $workspaceContextUsage = Get-RoslynWorkspaceContextUsage -TranscriptPath $transcriptPath
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

        $controlContaminationDetected = ($Mode -eq "control" -and ($usage.Commands.Count -gt 0 -or $lspUsage.Commands.Count -gt 0))
        $lspLaneRoslynContaminationDetected = ($Mode -eq "treatment-lsp" -and $usage.Commands.Count -gt 0)
        $lspAvailability = if ($Mode -eq "treatment-lsp") {
            Get-LspAvailability -Agent $Agent -TranscriptPath $transcriptPath
        } else {
            $null
        }
        $lspToolsAvailable = if ($null -eq $lspAvailability) { $null } else { [bool]$lspAvailability.available }
        $lspAvailabilityIndicators = if ($null -eq $lspAvailability) { @() } else { @($lspAvailability.indicators) }
        $lspToolsUnavailableDetected = ($Mode -eq "treatment-lsp" -and -not [bool]$lspToolsAvailable)
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
        if ($lspLaneRoslynContaminationDetected -and $FailOnLspRoslynContamination) {
            $failureReasons.Add("lsp_lane_roslyn_contamination_detected")
        }
        if ($lspToolsUnavailableDetected -and $FailOnMissingLspTools) {
            $failureReasons.Add("lsp_tools_unavailable")
        }
        $runPassed = ($failureReasons.Count -eq 0)

        $metadata = [ordered]@{
            agent = $Agent
            mode = $Mode
            roslyn_guidance_profile = $RoslynGuidanceProfile
            task_shape = $TaskShape
            timestamp_utc = (Get-Date).ToUniversalTime().ToString("o")
            exit_code = $exitCode
            workspace_path = $workspaceDirectory
            workspace_deleted = (-not $KeepIsolatedWorkspace)
            artifact_directory = (Resolve-Path $artifactRunDirectory).Path
            roslyn_used = ($usage.Successful -gt 0)
            roslyn_attempted_calls = $usage.Commands.Count
            roslyn_successful_calls = $usage.Successful
            roslyn_usage_indicators = $usage.Commands
            roslyn_workspace_mode_workspace_count = $workspaceContextUsage.workspace_count
            roslyn_workspace_mode_ad_hoc_count = $workspaceContextUsage.ad_hoc_count
            roslyn_workspace_context_total_count = $workspaceContextUsage.total_count
            roslyn_workspace_mode_distinct = $workspaceContextUsage.distinct_modes
            roslyn_workspace_mode_last = $workspaceContextUsage.last_mode
            lsp_used = ($lspUsage.Successful -gt 0)
            lsp_attempted_calls = $lspUsage.Commands.Count
            lsp_successful_calls = $lspUsage.Successful
            lsp_usage_indicators = $lspUsage.Commands
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
            lsp_command_round_trips = $tokenAttribution.lsp_command_round_trips
            non_lsp_command_round_trips = $tokenAttribution.non_lsp_command_round_trips
            control_contamination_detected = $controlContaminationDetected
            fail_on_control_contamination = $FailOnControlContamination
            lsp_lane_roslyn_contamination_detected = $lspLaneRoslynContaminationDetected
            fail_on_lsp_roslyn_contamination = $FailOnLspRoslynContamination
            lsp_tools_available = $lspToolsAvailable
            lsp_tool_availability_indicators = $lspAvailabilityIndicators
            lsp_tools_unavailable_detected = $lspToolsUnavailableDetected
            fail_on_missing_lsp_tools = $FailOnMissingLspTools
            mcp_enabled = $EnableMcp
            codex_mcp_config_path = $codexMcpConfigPath
            claude_mcp_config_path = $claudeMcpConfigPath
            run_passed = $runPassed
            failure_reasons = $failureReasons.ToArray()
            duration_seconds = $agentDurationSeconds
            constraint_checks_passed = [bool]$constraintChecks.ok
            constraint_checks_path = (Resolve-Path $artifactConstraintChecksPath).Path
            agent_environment = $environmentOverrides
            token_attribution = $tokenAttribution
        }

        $metadataPath = Join-Path $artifactRunDirectory "run-metadata.json"
        $metadata | ConvertTo-Json -Depth 40 | Set-Content -Path $metadataPath

        Write-Host ("RUN {0} exit={1} run_passed={2} control_contamination={3} roslyn_used={4} roslyn_attempted_calls={5} roslyn_successful_calls={6} roslyn_workspace_modes(workspace/ad_hoc)={7}/{8} lsp_used={9} lsp_attempted_calls={10} lsp_successful_calls={11} diff_has_changes={12} round_trips={13} model_total_tokens={14} cache_inclusive_tokens={15}" -f $runId, $exitCode, $metadata.run_passed, $metadata.control_contamination_detected, $metadata.roslyn_used, $metadata.roslyn_attempted_calls, $metadata.roslyn_successful_calls, $metadata.roslyn_workspace_mode_workspace_count, $metadata.roslyn_workspace_mode_ad_hoc_count, $metadata.lsp_used, $metadata.lsp_attempted_calls, $metadata.lsp_successful_calls, $diffHasChanges, $metadata.command_round_trips, $metadata.total_tokens, $metadata.cache_inclusive_total_tokens)

        if ($controlContaminationDetected -and $FailOnControlContamination) {
            throw ("Control contamination detected for run '{0}'. Roslyn indicators: {1}; LSP indicators: {2}" -f $runId, ($usage.Commands -join " | "), ($lspUsage.Commands -join " | "))
        }
        if ($lspLaneRoslynContaminationDetected -and $FailOnLspRoslynContamination) {
            throw ("Roslyn contamination detected in treatment-lsp run '{0}'. Roslyn indicators: {1}" -f $runId, ($usage.Commands -join " | "))
        }
        if ($lspToolsUnavailableDetected -and $FailOnMissingLspTools) {
            throw ("LSP tooling unavailable for run '{0}'. Indicators: {1}" -f $runId, ($lspAvailabilityIndicators -join " | "))
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

$cliProjectPath = (Resolve-Path (Join-Path $repoRoot "src\RoslynSkills.Cli\RoslynSkills.Cli.csproj")).Path
$cliDllPath = Publish-RoslynCli -CliProjectPath $cliProjectPath -BundleDirectory $bundleDirectory -Configuration $CliPublishConfiguration
$mcpDllPath = $null
if ($IncludeMcpTreatment) {
    $mcpProjectPath = (Resolve-Path (Join-Path $repoRoot "src\RoslynSkills.McpServer\RoslynSkills.McpServer.csproj")).Path
    $mcpDllPath = Publish-RoslynMcpServer -McpProjectPath $mcpProjectPath -BundleDirectory $bundleDirectory -Configuration $CliPublishConfiguration
}

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

function Get-CliRoslynGuidanceBlock {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("codex", "claude")][string]$Agent,
        [Parameter(Mandatory = $true)][ValidateSet("standard", "brief-first", "surgical", "skill-minimal", "schema-first")][string]$Profile
    )

    switch ($Profile) {
        "standard" {
            if ($Agent -eq "codex") {
                return @"
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
"@
            }

            return @"
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
"@
        }
        "brief-first" {
            if ($Agent -eq "codex") {
                return @"
Roslyn helper scripts are available in this directory and recommended.
Prioritize compact/surgical usage:
- Skip list-commands unless a Roslyn call fails.
- Prefer brief outputs and direct command targets.
Run this sequence first:
- powershell.exe -ExecutionPolicy Bypass -File .\roslyn-rename-and-verify.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -OldName Process -ExpectedNewExact 2 -ExpectedOldExact 2 -RequireNoDiagnostics
Fallback direct Roslyn calls only if needed:
- scripts\roscli.cmd nav.find_symbol Target.cs Process --brief true --max-results 50
- scripts\roscli.cmd edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 50
- scripts\roscli.cmd diag.get_file_diagnostics Target.cs
"@
            }

            return @"
Roslyn helper scripts are available in this directory and recommended.
Prioritize compact/surgical usage:
- Skip list-commands unless a Roslyn call fails.
- Prefer brief outputs and direct command targets.
For Bash environments, start with:
- bash scripts/roscli edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 50
- bash scripts/roscli diag.get_file_diagnostics Target.cs
Fallback with targeted symbol check only if needed:
- bash scripts/roscli nav.find_symbol Target.cs Process --brief true --max-results 50
For PowerShell environments, you may run:
- powershell.exe -ExecutionPolicy Bypass -File .\roslyn-rename-and-verify.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -OldName Process -ExpectedNewExact 2 -ExpectedOldExact 2 -RequireNoDiagnostics
"@
        }
        "surgical" {
            if ($Agent -eq "codex") {
                return @"
Roslyn helper scripts are available in this directory and recommended.
Use the minimum Roslyn sequence:
1) Run exactly one semantic rename+verify command first:
- powershell.exe -ExecutionPolicy Bypass -File .\roslyn-rename-and-verify.ps1 -FilePath Target.cs -Line 3 -Column 17 -NewName Handle -OldName Process -ExpectedNewExact 2 -ExpectedOldExact 2 -RequireNoDiagnostics
2) Do not call list-commands unless rename+verify fails.
3) If fallback is required, use only:
- scripts\roscli.cmd edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 50
- scripts\roscli.cmd diag.get_file_diagnostics Target.cs
"@
            }

            return @"
Roslyn helper scripts are available in this directory and recommended.
Use the minimum Roslyn sequence for Bash:
1) Run semantic rename directly:
- bash scripts/roscli edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 50
2) Verify diagnostics:
- bash scripts/roscli diag.get_file_diagnostics Target.cs
3) Do not call list-commands unless these fail.
"@
        }
        "skill-minimal" {
            if ($Agent -eq "codex") {
                return @"
Roslyn tooling is available as `scripts\roscli.cmd`.
Treat this as a skill-assisted run:
1) Discover available operations:
- scripts\roscli.cmd list-commands --ids-only
2) Before invoking a command, inspect its contract:
- scripts\roscli.cmd describe-command nav.find_symbol
- scripts\roscli.cmd describe-command edit.rename_symbol
3) Execute a compact Roslyn-first flow to complete the task and verify:
- scripts\roscli.cmd nav.find_symbol Target.cs Process --brief true --max-results 50
- scripts\roscli.cmd edit.rename_symbol Target.cs <line> <column> Handle --apply true --max-diagnostics 50
- scripts\roscli.cmd diag.get_file_diagnostics Target.cs
Guardrail:
- Do not use `session.open` on non-C# files (.sln/.slnx/.csproj).
"@
            }

            return @"
Roslyn tooling is available as `scripts/roscli` (Bash) or `scripts\roscli.cmd` (PowerShell).
Treat this as a skill-assisted run:
1) Discover available operations:
- bash scripts/roscli list-commands --ids-only
2) Before invoking a command, inspect its contract:
- bash scripts/roscli describe-command nav.find_symbol
- bash scripts/roscli describe-command edit.rename_symbol
3) Execute a compact Roslyn-first flow to complete the task and verify:
- bash scripts/roscli nav.find_symbol Target.cs Process --brief true --max-results 50
- bash scripts/roscli edit.rename_symbol Target.cs <line> <column> Handle --apply true --max-diagnostics 50
- bash scripts/roscli diag.get_file_diagnostics Target.cs
Guardrail:
- Do not use `session.open` on non-C# files (.sln/.slnx/.csproj).
"@
        }
        "schema-first" {
            if ($Agent -eq "codex") {
                return @"
Roslyn tooling is available and should be used contract-first.
Required sequence:
1) Read command contracts:
- scripts\roscli.cmd describe-command nav.find_symbol
- scripts\roscli.cmd describe-command edit.rename_symbol
- scripts\roscli.cmd describe-command diag.get_file_diagnostics
2) Create payload files (avoid inline JSON quoting):
- Set-Content nav.find_symbol.json '{"file_path":"Target.cs","symbol_name":"Process","brief":true,"max_results":50}'
- Set-Content edit.rename_symbol.json '{"file_path":"Target.cs","line":3,"column":17,"new_name":"Handle","apply":true,"max_diagnostics":50}'
- Set-Content diag.file.json '{"file_path":"Target.cs"}'
3) Validate payloads:
- scripts\roscli.cmd validate-input nav.find_symbol --input "@nav.find_symbol.json"
- scripts\roscli.cmd validate-input edit.rename_symbol --input "@edit.rename_symbol.json"
4) Execute:
- scripts\roscli.cmd run nav.find_symbol --input "@nav.find_symbol.json"
- scripts\roscli.cmd run edit.rename_symbol --input "@edit.rename_symbol.json"
- scripts\roscli.cmd run diag.get_file_diagnostics --input "@diag.file.json"
If validation fails, fix payload shape before continuing.
"@
            }

            return @"
Roslyn tooling is available and should be used contract-first (Bash lane).
Required sequence:
1) Read command contracts:
- bash scripts/roscli describe-command nav.find_symbol
- bash scripts/roscli describe-command edit.rename_symbol
- bash scripts/roscli describe-command diag.get_file_diagnostics
2) Create payload files (avoid inline JSON quoting):
- printf '{"file_path":"Target.cs","symbol_name":"Process","brief":true,"max_results":50}\n' > nav.find_symbol.json
- printf '{"file_path":"Target.cs","line":3,"column":17,"new_name":"Handle","apply":true,"max_diagnostics":50}\n' > edit.rename_symbol.json
- printf '{"file_path":"Target.cs"}\n' > diag.file.json
3) Validate payloads:
- bash scripts/roscli validate-input nav.find_symbol --input @nav.find_symbol.json
- bash scripts/roscli validate-input edit.rename_symbol --input @edit.rename_symbol.json
4) Execute:
- bash scripts/roscli run nav.find_symbol --input @nav.find_symbol.json
- bash scripts/roscli run edit.rename_symbol --input @edit.rename_symbol.json
- bash scripts/roscli run diag.get_file_diagnostics --input @diag.file.json
If validation fails, fix payload shape before continuing.
"@
        }
    }
}

function Get-McpRoslynGuidanceBlock {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("standard", "brief-first", "surgical", "skill-minimal", "schema-first")][string]$Profile
    )

    switch ($Profile) {
        "standard" {
            return @"
Roslyn MCP resources are available for this run and should be used before manual edits.
Use MCP in this sequence:
1) list_mcp_resource_templates server=roslyn
2) read_mcp_resource server=roslyn uri=roslyn://commands
3) read_mcp_resource server=roslyn uri=roslyn://command/nav.find_symbol?file_path=Target.cs&symbol_name=Process&brief=true&max_results=200
4) read_mcp_resource server=roslyn uri=roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true&max_diagnostics=100
5) read_mcp_resource server=roslyn uri=roslyn://command/diag.get_file_diagnostics?file_path=Target.cs
Use MCP calls sequentially and keep responses compact.
"@
        }
        "brief-first" {
            return @"
Roslyn MCP resources are available for this run and should be used before manual edits.
Prioritize compact MCP usage:
1) Skip roslyn://commands unless direct command URIs fail.
2) Run only targeted calls first:
- read_mcp_resource server=roslyn uri=roslyn://command/nav.find_symbol?file_path=Target.cs&symbol_name=Process&brief=true&max_results=50
- read_mcp_resource server=roslyn uri=roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true&max_diagnostics=50
- read_mcp_resource server=roslyn uri=roslyn://command/diag.get_file_diagnostics?file_path=Target.cs
"@
        }
        "surgical" {
            return @"
Roslyn MCP resources are available for this run and should be used before manual edits.
Use the minimum MCP sequence:
1) read_mcp_resource server=roslyn uri=roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true&max_diagnostics=50
2) read_mcp_resource server=roslyn uri=roslyn://command/diag.get_file_diagnostics?file_path=Target.cs
Only call discovery/catalog resources if these fail.
"@
        }
        "skill-minimal" {
            return @"
Roslyn MCP resources are available and should be used as skill guidance.
Use this sequence:
1) read_mcp_resource server=roslyn uri=roslyn://commands
2) read_mcp_resource server=roslyn uri=roslyn://command/nav.find_symbol?file_path=Target.cs&symbol_name=Process&brief=true&max_results=50
3) read_mcp_resource server=roslyn uri=roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true&max_diagnostics=50
4) read_mcp_resource server=roslyn uri=roslyn://command/diag.get_file_diagnostics?file_path=Target.cs
"@
        }
        "schema-first" {
            return @"
Roslyn MCP resources are available and should be used contract-first.
1) Read command metadata contracts:
- read_mcp_resource server=roslyn uri=roslyn://command-meta/nav.find_symbol
- read_mcp_resource server=roslyn uri=roslyn://command-meta/edit.rename_symbol
- read_mcp_resource server=roslyn uri=roslyn://command-meta/diag.get_file_diagnostics
2) Invoke commands using explicit query arguments:
- read_mcp_resource server=roslyn uri=roslyn://command/nav.find_symbol?file_path=Target.cs&symbol_name=Process&brief=true&max_results=50
- read_mcp_resource server=roslyn uri=roslyn://command/edit.rename_symbol?file_path=Target.cs&line=3&column=17&new_name=Handle&apply=true&max_diagnostics=50
- read_mcp_resource server=roslyn uri=roslyn://command/diag.get_file_diagnostics?file_path=Target.cs
"@
        }
    }
}

function Get-ClaudeLspGuidanceBlock {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("standard", "brief-first", "surgical", "skill-minimal", "schema-first")][string]$Profile
    )

    switch ($Profile) {
        "standard" {
            return @"
Use the Claude `csharp-lsp` plugin/tools for C# semantic operations before direct text edits.
Suggested flow:
1) Use one cheap LSP lookup first (prefer `goToDefinition` from `Process(1)` call site), then optionally `findReferences` if needed.
2) Apply edit with regular file edits (current Claude LSP toolset is navigation-first; no direct rename operation).
3) Re-check diagnostics via LSP diagnostics (or dotnet build if diagnostics tool is unavailable).
Execution rules:
- Run LSP operations strictly sequentially (one LSP tool call per assistant turn).
- Do not issue parallel/concurrent LSP requests in a single message.
Hard rule: do not call `roscli`, `scripts/roscli`, or `roslyn-*` helper scripts in this treatment-lsp lane.
If one LSP request hangs or fails, stop LSP retries and continue with minimal plain text edits.
If LSP tools are missing, state that clearly and continue best effort with plain text edits only.
"@
        }
        "brief-first" {
            return @"
Prefer a minimal LSP-first sequence:
1) Use exactly one LSP lookup (prefer `goToDefinition` from `Process(1)`).
2) Rename and verify quickly.
3) Avoid broad workspace scans.
Execution rules:
- Run LSP operations sequentially only.
- Do not issue more than one LSP call in a single assistant response.
Hard rule: do not call `roscli`, `scripts/roscli`, or `roslyn-*` helper scripts in this treatment-lsp lane.
If one LSP request hangs or fails, stop LSP retries and continue with minimal plain text edits.
If LSP tools are missing, continue with plain text edits only.
"@
        }
        "surgical" {
            return @"
Use the narrowest possible LSP sequence:
1) locate symbol,
2) rename target and matching int call site,
3) verify no collateral edits.
Execution rules:
- One LSP call at a time, then wait for result.
Hard rule: do not call `roscli`, `scripts/roscli`, or `roslyn-*` helper scripts in this treatment-lsp lane.
If LSP tools are missing, continue with plain text edits only.
"@
        }
        "skill-minimal" {
            return @"
Treat csharp-lsp as the primary capability for this run.
Start by discovering available LSP operations, then perform one targeted rename flow.
Execution rules:
- Use sequential LSP calls only (no concurrent tool calls).
Hard rule: do not call `roscli`, `scripts/roscli`, or `roslyn-*` helper scripts in this treatment-lsp lane.
If LSP tools are missing, continue with plain text edits only.
"@
        }
        "schema-first" {
            return @"
Use csharp-lsp tools contract-first:
1) inspect available tool schemas/signatures,
2) run definition/references,
3) apply text edit and verify diagnostics.
Execution rules:
- LSP tool calls must be sequential (wait for each tool_result before next call).
Hard rule: do not call `roscli`, `scripts/roscli`, or `roslyn-*` helper scripts in this treatment-lsp lane.
If LSP tools are missing, continue with plain text edits only.
"@
        }
    }
}

$taskWorkspaceHint = if ($TaskShape -eq "project") {
    "Workspace note: a minimal SDK project (`TargetHarness.csproj`) and `Program.cs` are provided. Prefer project-aware semantic tooling."
} else {
    "Workspace note: this is a focused single-file task (`Target.cs`)."
}

$taskPromptCore = @"
Edit Target.cs in this directory.
$taskWorkspaceHint
Task:
1) Rename method Process(int value) to Handle(int value).
2) Update only the matching invocation Process(1) to Handle(1).
Constraints:
- Do NOT change Process(string value).
- Do NOT change Process("x").
- Do NOT change string literal "Process".
"@

$roslynWorkspaceGuardrail = @"
Workspace-context guardrail for Roslyn runs:
- For `nav.find_symbol` and `diag.get_file_diagnostics`, inspect `workspace_context.mode`.
- For project-backed runs (when `TargetHarness.csproj` exists), expected mode is `workspace`.
- If mode is `ad_hoc`, rerun with explicit workspace binding:
  - CLI: add `--workspace-path TargetHarness.csproj`
  - MCP: add `workspace_path=TargetHarness.csproj` to the command URI query.
"@

$controlPrompt = @"
$taskPromptCore
Baseline condition:
- Do NOT invoke Roslyn helper scripts/commands in this run.
- Do NOT invoke C# LSP tools/plugins in this run.
- Use plain editor/text operations only.
After editing, briefly summarize what changed.
"@

$treatmentPromptCodex = @"
$taskPromptCore
$(Get-CliRoslynGuidanceBlock -Agent "codex" -Profile $RoslynGuidanceProfile)
$roslynWorkspaceGuardrail
After editing, say explicitly whether Roslyn helpers were invoked successfully.
"@

$treatmentPromptClaude = @"
$taskPromptCore
$(Get-CliRoslynGuidanceBlock -Agent "claude" -Profile $RoslynGuidanceProfile)
$roslynWorkspaceGuardrail
After editing, say explicitly whether Roslyn helpers were invoked successfully.
"@

$treatmentPromptCodexMcp = @"
$taskPromptCore
$(Get-McpRoslynGuidanceBlock -Profile $RoslynGuidanceProfile)
$roslynWorkspaceGuardrail
After editing, say explicitly whether Roslyn MCP tools were invoked successfully.
"@

$treatmentPromptClaudeMcp = @"
$taskPromptCore
$(Get-McpRoslynGuidanceBlock -Profile $RoslynGuidanceProfile)
$roslynWorkspaceGuardrail
After editing, say explicitly whether Roslyn MCP tools were invoked successfully.
"@

$treatmentPromptClaudeLsp = @"
$taskPromptCore
$(Get-ClaudeLspGuidanceBlock -Profile $RoslynGuidanceProfile)
After editing, say explicitly whether C# LSP tools were invoked successfully.
"@

$runs = New-Object System.Collections.Generic.List[object]
$runClaude = (-not $SkipClaude)

if ($runClaude) {
    $claudeAuthProbe = Test-ClaudeAuthentication
    if (-not [bool]$claudeAuthProbe.ok) {
        $failureMessage = "Claude auth preflight failed (exit=$($claudeAuthProbe.exit_code), revoked=$($claudeAuthProbe.auth_revoked)). Preview: $($claudeAuthProbe.output_preview)"
        if ($FailOnClaudeAuthUnavailable) {
            throw "$failureMessage. Re-run after 'claude /login' or pass -SkipClaude."
        }

        Write-Warning "$failureMessage. Skipping Claude lanes."
        $runClaude = $false
    }
}

if (-not $SkipCodex) {
    $runs.Add((Invoke-AgentRun -Agent "codex" -Mode "control" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $controlPrompt -TargetContent $targetContent -CliDllPath $cliDllPath -McpDllPath $mcpDllPath -EnableMcp $false -FailOnControlContamination $FailOnControlContamination -FailOnLspRoslynContamination $FailOnLspRoslynContamination -FailOnMissingLspTools $FailOnMissingLspTools -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $CodexModel -TaskShape $TaskShape))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    $runs.Add((Invoke-AgentRun -Agent "codex" -Mode "treatment" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $treatmentPromptCodex -TargetContent $targetContent -CliDllPath $cliDllPath -McpDllPath $mcpDllPath -EnableMcp $false -FailOnControlContamination $FailOnControlContamination -FailOnLspRoslynContamination $FailOnLspRoslynContamination -FailOnMissingLspTools $FailOnMissingLspTools -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $CodexModel -TaskShape $TaskShape))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    if ($IncludeMcpTreatment) {
        $runs.Add((Invoke-AgentRun -Agent "codex" -Mode "treatment-mcp" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $treatmentPromptCodexMcp -TargetContent $targetContent -CliDllPath $cliDllPath -McpDllPath $mcpDllPath -EnableMcp $true -FailOnControlContamination $FailOnControlContamination -FailOnLspRoslynContamination $FailOnLspRoslynContamination -FailOnMissingLspTools $FailOnMissingLspTools -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $CodexModel -TaskShape $TaskShape))
        Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    }
}

if ($runClaude) {
    $runs.Add((Invoke-AgentRun -Agent "claude" -Mode "control" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $controlPrompt -TargetContent $targetContent -CliDllPath $cliDllPath -McpDllPath $mcpDllPath -EnableMcp $false -FailOnControlContamination $FailOnControlContamination -FailOnLspRoslynContamination $FailOnLspRoslynContamination -FailOnMissingLspTools $FailOnMissingLspTools -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $ClaudeModel -TaskShape $TaskShape))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    $runs.Add((Invoke-AgentRun -Agent "claude" -Mode "treatment" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $treatmentPromptClaude -TargetContent $targetContent -CliDllPath $cliDllPath -McpDllPath $mcpDllPath -EnableMcp $false -FailOnControlContamination $FailOnControlContamination -FailOnLspRoslynContamination $FailOnLspRoslynContamination -FailOnMissingLspTools $FailOnMissingLspTools -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $ClaudeModel -TaskShape $TaskShape))
    Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    if ($IncludeMcpTreatment) {
        $runs.Add((Invoke-AgentRun -Agent "claude" -Mode "treatment-mcp" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $treatmentPromptClaudeMcp -TargetContent $targetContent -CliDllPath $cliDllPath -McpDllPath $mcpDllPath -EnableMcp $true -FailOnControlContamination $FailOnControlContamination -FailOnLspRoslynContamination $FailOnLspRoslynContamination -FailOnMissingLspTools $FailOnMissingLspTools -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $ClaudeModel -TaskShape $TaskShape))
        Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    }
    if ($IncludeClaudeLspTreatment) {
        $runs.Add((Invoke-AgentRun -Agent "claude" -Mode "treatment-lsp" -BundleDirectory $bundleDirectory -RepoRoot $repoRoot -IsolationRoot $isolationRootDirectory -PromptText $treatmentPromptClaudeLsp -TargetContent $targetContent -CliDllPath $cliDllPath -McpDllPath $mcpDllPath -EnableMcp $false -FailOnControlContamination $FailOnControlContamination -FailOnLspRoslynContamination $FailOnLspRoslynContamination -FailOnMissingLspTools $FailOnMissingLspTools -KeepIsolatedWorkspace $KeepIsolatedWorkspaces -Model $ClaudeModel -TaskShape $TaskShape))
        Assert-HostContextIntegrity -ExpectedWorkingDirectory $homeWorkingDirectory -ExpectedRepoRoot $expectedRepoRoot -ExpectedHead $expectedRepoHead
    }
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

