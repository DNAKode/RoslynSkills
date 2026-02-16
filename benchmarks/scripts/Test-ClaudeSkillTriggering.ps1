param(
    [string]$OutputRoot = "",
    [string]$ClaudeModel = "sonnet",
    [int]$Replicates = 1,
    [ValidateSet(
        "rename-overload-v1",
        "rename-overload-collision-classes-v1",
        "rename-overload-collision-nested-v1",
        "rename-overload-collision-generic-v1",
        "change-signature-named-args-v1",
        "update-usings-cleanup-v1",
        "add-member-threshold-v1",
        "replace-member-body-guard-v1",
        "create-file-audit-log-v1",
        "rename-multifile-collision-v1"
    )][string[]]$TaskId = @(
        "rename-overload-v1",
        "rename-overload-collision-classes-v1",
        "rename-overload-collision-nested-v1",
        "rename-overload-collision-generic-v1",
        "change-signature-named-args-v1",
        "update-usings-cleanup-v1",
        "add-member-threshold-v1",
        "replace-member-body-guard-v1",
        "create-file-audit-log-v1",
        "rename-multifile-collision-v1"
    ),
    [string]$SkillName = "roslynskills-tight",
    [switch]$IncludeExplicitInvocation,
    [switch]$IncludeTaskCallBudgetGuidance,
    [switch]$FailIfSkillNotLoaded,
    [switch]$FailIfNoRoslynUsed,
    [switch]$FailIfTaskCallBudgetExceeded,
    [switch]$IncludeDotnetBuildGate,
    [int]$ClaudeTimeoutSeconds = 180,
    [switch]$IgnoreAuthenticationError
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-RepoRoot {
    $here = (Resolve-Path $PSScriptRoot).Path
    return (Resolve-Path (Join-Path $here "..\\..")).Path
}

function Resolve-OutputDirectory {
    param([Parameter(Mandatory = $true)][string]$RepoRoot, [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputRoot)

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        return (Join-Path $RepoRoot "artifacts/skill-tests/$stamp-claude-skill-trigger")
    }

    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        return $OutputRoot
    }

    return (Join-Path $RepoRoot $OutputRoot)
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

function Get-ClaudeConfigRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:CLAUDE_CONFIG_DIR)) {
        return [string]$env:CLAUDE_CONFIG_DIR
    }

    return (Join-Path $env:USERPROFILE ".claude")
}

function Get-TaskDefinition {
    param([Parameter(Mandatory = $true)][string]$TaskId)

    # Prompts are intentionally "user-like" and do NOT mention roscli/MCP.
    switch ($TaskId) {
        "rename-overload-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, rename only the int overload of Process to Handle and update only the matching int call site. Do not change the string overload or any string literals. Keep changes minimal."
                target = @"
// Task: rename-overload-v1
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
            }
        }
        "rename-overload-collision-classes-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, rename only Overloads.Process(int) to Handle and update only the matching int call site. There is another class with Process(int); do not modify that class. Do not change string overloads or string literals."
                target = @"
// Task: rename-overload-collision-classes-v1
// Note: there is also a separate class below with its own Process(int).
// Rename only Overloads.Process(int), not AnotherOverloads.Process(int).
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

public class AnotherOverloads
{
    public void Process(int value)
    {
    }

    public void Execute()
    {
        Process(1);
        System.Console.WriteLine("Process");
    }
}
"@
            }
        }
        "rename-overload-collision-nested-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, rename only Overloads.Process(int) to Handle and update only the int call site inside Overloads.Execute(). There is an inner type with its own Process(int); do not modify that inner type. Do not change string overloads or string literals."
                target = @"
// Task: rename-overload-collision-nested-v1
// Note: there is an inner type below with its own Process(int).
// Rename only Overloads.Process(int), not Overloads.Inner.Process(int).
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

    public class Inner
    {
        public void Process(int value)
        {
        }

        public void Process(string value)
        {
        }

        public void ExecuteInner()
        {
            Process(1);
            Process("x");
            System.Console.WriteLine("Process");
        }
    }
}
"@
            }
        }
        "rename-overload-collision-generic-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, rename only Overloads.Process(int) to Handle and update only the matching int call site. There is also Process<T>(T); do not rename the generic method or its call sites. Do not change string overloads or string literals."
                target = @"
// Task: rename-overload-collision-generic-v1
// Note: there is also a generic method Process<T>(T value).
// Rename only Overloads.Process(int).
public class Overloads
{
    public void Process(int value)
    {
    }

    public void Process(string value)
    {
    }

    public void Process<T>(T value)
    {
    }

    public void Execute()
    {
        Process(1);
        Process("x");
        Process<object>(new object());
        System.Console.WriteLine("Process");
    }
}
"@
            }
        }
        "change-signature-named-args-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, change the method signature of Combine from (int left, int right) to (int primary, int right) and update only the named-argument call sites accordingly. Keep behavior the same and keep edits minimal."
                target = @"
// Task: change-signature-named-args-v1
public class Calculator
{
    public int Combine(int left, int right)
    {
        return left + right;
    }

    public int Compute()
    {
        return Combine(left: 1, right: 2) + Combine(3, 4);
    }
}
"@
            }
        }
        "update-usings-cleanup-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, remove only unused using directives while preserving behavior. Keep required usings and keep changes minimal."
                target = @"
// Task: update-usings-cleanup-v1
using System;
using System.Linq;
using System.Text;

public class MessageBuilder
{
    public string Build(string name)
    {
        var builder = new StringBuilder();
        builder.Append("Hello ");
        builder.Append(name.Trim());
        return builder.ToString();
    }

    public int CountCharacters(string[] values)
    {
        return values.Length;
    }
}
"@
            }
        }
        "add-member-threshold-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, add a public method bool HasLongName(int threshold) to Profile that returns Name.Length > threshold. Do not change existing constructor/property behavior."
                target = @"
// Task: add-member-threshold-v1
public class Profile
{
    public string Name { get; }

    public Profile(string name)
    {
        Name = name;
    }
}
"@
            }
        }
        "replace-member-body-guard-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "In Target.cs, update only the body of ParseCount so it returns 0 for null/whitespace input and otherwise returns input.Trim().Length. Keep the method signature unchanged."
                target = @"
// Task: replace-member-body-guard-v1
public class Parser
{
    public int ParseCount(string? input)
    {
        return input!.Length;
    }
}
"@
            }
        }
        "create-file-audit-log-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "Create a new file AuditLog.cs that defines public class AuditLog with public List<string> Entries { get; } = new(); and method public void Add(string message) that appends to Entries. Do not modify Target.cs."
                target = @"
// Task: create-file-audit-log-v1
public class Worker
{
    public string Run(string input)
    {
        return input.Trim();
    }
}
"@
            }
        }
        "rename-multifile-collision-v1" {
            return [ordered]@{
                task_id = $TaskId
                prompt = "Rename only Service.Transform(int) to Scale and update matching call sites across files. Do not rename Service.Transform(string) or AnotherService.Transform(int). Keep edits minimal."
                target = @"
// Task: rename-multifile-collision-v1
public class Service
{
    public int Transform(int value)
    {
        return value * 2;
    }

    public string Transform(string value)
    {
        return value.ToUpperInvariant();
    }
}
"@
                additional_files = [ordered]@{
                    "Consumer.cs" = @"
public class Consumer
{
    public int Use(Service service)
    {
        return service.Transform(2);
    }
}
"@
                    "AnotherService.cs" = @"
public class AnotherService
{
    public int Transform(int value)
    {
        return value + 1;
    }
}
"@
                }
            }
        }
        default {
            throw "Unsupported TaskId '$TaskId'."
        }
    }
}

function Get-TaskCallBudget {
    param([Parameter(Mandatory = $true)][string]$TaskId)

    switch ($TaskId) {
        "rename-overload-v1" { return 3 }
        "rename-overload-collision-classes-v1" { return 3 }
        "rename-overload-collision-nested-v1" { return 3 }
        "rename-overload-collision-generic-v1" { return 3 }
        "change-signature-named-args-v1" { return 3 }
        "update-usings-cleanup-v1" { return 3 }
        "add-member-threshold-v1" { return 2 }
        "replace-member-body-guard-v1" { return 2 }
        "create-file-audit-log-v1" { return 2 }
        "rename-multifile-collision-v1" { return 4 }
        default { throw "Unsupported TaskId '$TaskId'." }
    }
}

function Get-TaskCallBudgetGuidance {
    param(
        [Parameter(Mandatory = $true)][string]$TaskId,
        [Parameter(Mandatory = $true)][int]$Budget
    )

    $strategy = switch ($TaskId) {
        "rename-multifile-collision-v1" { "Use at most one lookup call, then one rename call, then at most one verification call." }
        "change-signature-named-args-v1" { "Prefer one signature/body edit call and one verification call; avoid exploratory loops." }
        "update-usings-cleanup-v1" { "Apply one focused edit and verify once; avoid repeated diagnostics loops." }
        "add-member-threshold-v1" { "Apply the member addition directly with no exploratory calls unless blocked." }
        "replace-member-body-guard-v1" { "Apply one member-body edit directly; verify once only if needed." }
        "create-file-audit-log-v1" { "Create the file in one step and stop; verify once only if needed." }
        default { "Use a direct find->edit flow and add one verification call only if needed." }
    }

    return @"
Execution constraints:
- Tool call budget for this task: at most $Budget total tool calls.
- Keep tool usage minimal; avoid schema/catalog loops unless blocked by an error.
- $strategy
"@
}

function Build-RunPrompt {
    param(
        [Parameter(Mandatory = $true)][string]$TaskPrompt,
        [Parameter(Mandatory = $true)][string]$TaskId,
        [Parameter(Mandatory = $true)][string]$SkillName,
        [Parameter(Mandatory = $true)][bool]$InvokeSkill,
        [Parameter(Mandatory = $true)][bool]$IncludeCallBudgetGuidance
    )

    $prompt = $TaskPrompt
    if ($IncludeCallBudgetGuidance) {
        $budget = Get-TaskCallBudget -TaskId $TaskId
        $guidance = Get-TaskCallBudgetGuidance -TaskId $TaskId -Budget $budget
        $prompt = "$prompt`n`n$guidance"
    }

    if ($InvokeSkill) {
        return "/$SkillName`n$prompt"
    }

    return $prompt
}

function Publish-CliOnce {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$PublishDirectory
    )

    $cliProject = Join-Path $RepoRoot "src\\RoslynSkills.Cli\\RoslynSkills.Cli.csproj"
    New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

    & dotnet publish $cliProject -c Release -o $PublishDirectory --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynSkills.Cli."
    }

    $dll = Join-Path $PublishDirectory "RoslynSkills.Cli.dll"
    if (-not (Test-Path $dll -PathType Leaf)) {
        throw "Published RoslynSkills.Cli.dll not found at '$dll'."
    }

    return $dll
}

function Write-RoscliWrappers {
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
}

function Write-WorkspaceFiles {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspaceDir,
        [Parameter(Mandatory = $true)][string]$TargetContent,
        [System.Collections.IDictionary]$AdditionalFiles = $null
    )

    New-Item -ItemType Directory -Force -Path $WorkspaceDir | Out-Null

    $targetPath = Join-Path $WorkspaceDir "Target.cs"
    $originalPath = Join-Path $WorkspaceDir "Target.original.cs"
    $programPath = Join-Path $WorkspaceDir "Program.cs"
    $projectPath = Join-Path $WorkspaceDir "TargetHarness.csproj"

    # Shift line numbers so coordinate hardcoding is unreliable.
    $padded = "// Padding: $(Get-Date -Format o)`n" + $TargetContent
    Set-Content -Path $targetPath -Value $padded -NoNewline
    Copy-Item -Force -Path $targetPath -Destination $originalPath

    Set-Content -Path $programPath -Value @"
using System;
public static class Program
{
    public static void Main() => Console.WriteLine("ok");
}
"@ -NoNewline

    Set-Content -Path $projectPath -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Keep the snapshot file for diffing, but exclude it from compilation. -->
    <Compile Remove="*.original.cs" />
  </ItemGroup>
</Project>
"@ -NoNewline

    if ($null -ne $AdditionalFiles) {
        foreach ($kv in $AdditionalFiles.GetEnumerator()) {
            $path = Join-Path $WorkspaceDir ([string]$kv.Key)
            $dir = Split-Path -Parent $path
            if (-not [string]::IsNullOrWhiteSpace($dir)) {
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
            }
            Set-Content -Path $path -Value ([string]$kv.Value) -NoNewline
        }
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
}

function Get-WorkspaceSourceHashes {
    param([Parameter(Mandatory = $true)][string]$WorkspaceDir)

    $map = [ordered]@{}
    if (-not (Test-Path $WorkspaceDir -PathType Container)) { return $map }

    $files = Get-ChildItem -Path $WorkspaceDir -File | Where-Object {
        ($_.Extension -in @(".cs", ".csproj")) -and ($_.Name -notlike "*.original.cs")
    }

    foreach ($f in ($files | Sort-Object Name)) {
        $map[$f.Name] = (Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash
    }

    return $map
}

function Get-WorkspaceChanged {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Before,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$After
    )

    if ($Before.Count -ne $After.Count) { return $true }
    foreach ($k in $Before.Keys) {
        if (-not $After.Contains($k)) { return $true }
        if ([string]$Before[$k] -ne [string]$After[$k]) { return $true }
    }

    return $false
}

function Try-Write-GitNoIndexDiff {
    param(
        [Parameter(Mandatory = $true)][string]$OriginalPath,
        [Parameter(Mandatory = $true)][string]$UpdatedPath,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) { return $false }

    # git diff exits 1 when differences exist; capture output regardless.
    # Merge stderr into stdout so warnings don't become terminating errors under $ErrorActionPreference=Stop.
    $out = & git -c core.safecrlf=false diff --no-index -- $OriginalPath $UpdatedPath 2>&1
    $filtered = @($out | Where-Object { $_ -notmatch '^(git\\.exe\\s*:)?\\s*warning:' })
    $filtered | Set-Content -Path $OutputPath
    return $true
}

function Invoke-DotnetBuild {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspaceDir,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    Push-Location $WorkspaceDir
    try {
        $out = & dotnet build --nologo --no-incremental 2>&1
        if ($out -is [System.Array]) {
            $out | Set-Content -Path $OutputPath
        } else {
            [string]$out | Set-Content -Path $OutputPath
        }
        return ($LASTEXITCODE -eq 0)
    } finally {
        Pop-Location
    }
}

function Get-RoslynIndicatorCountFromTranscriptText {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    if (-not (Test-Path $TranscriptPath -PathType Leaf)) { return 0 }

    # Count only actual tool invocations, not mentions in the skill text block.
    $lines = Get-Content -Path $TranscriptPath
    $count = 0
    foreach ($line in $lines) {
        $text = [string]$line
        if ($text -notlike '*"type":"tool_use"*') { continue }
        if ($text -notlike '*"name":"Bash"*' -and $text -notlike '*"name":"Shell"*') { continue }

        if (
            $text -ilike '*scripts/roscli*' -or
            $text -ilike '*roscli.cmd*' -or
            $text -ilike '*RoslynSkills.Cli.dll*'
        ) {
            $count++
        }
    }

    return $count
}

function Get-RegexCount {
    param(
        [AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    $options = [System.Text.RegularExpressions.RegexOptions]::Singleline
    return [System.Text.RegularExpressions.Regex]::Matches($Text, $Pattern, $options).Count
}

function Invoke-ConstraintChecks {
    param(
        [Parameter(Mandatory = $true)][string]$WorkspaceDir,
        [Parameter(Mandatory = $true)][ValidateSet(
            "rename-overload-v1",
            "rename-overload-collision-classes-v1",
            "rename-overload-collision-nested-v1",
            "rename-overload-collision-generic-v1",
            "change-signature-named-args-v1",
            "update-usings-cleanup-v1",
            "add-member-threshold-v1",
            "replace-member-body-guard-v1",
            "create-file-audit-log-v1",
            "rename-multifile-collision-v1"
        )][string]$TaskId
    )

    $targetPath = Join-Path $WorkspaceDir "Target.cs"
    $content = Get-Content -Raw -Path $targetPath
    $checks = New-Object System.Collections.Generic.List[object]
    $fileCache = [ordered]@{ "Target.cs" = $content }

    function Get-FileContent {
        param([string]$RelativePath)
        if ($fileCache.Contains($RelativePath)) { return [string]$fileCache[$RelativePath] }

        $fullPath = Join-Path $WorkspaceDir $RelativePath
        if (-not (Test-Path $fullPath -PathType Leaf)) {
            $fileCache[$RelativePath] = $null
            return $null
        }

        $text = Get-Content -Raw -Path $fullPath
        $fileCache[$RelativePath] = $text
        return $text
    }

    function Add-CountCheck {
        param([string]$Name, [string]$Pattern, [int]$Expected)
        $count = Get-RegexCount -Text $content -Pattern $Pattern
        $checks.Add([pscustomobject]@{ name = $Name; passed = ($count -eq $Expected); detail = "count=$count expected=$Expected" }) | Out-Null
    }

    function Add-MatchCheck {
        param([string]$Name, [string]$Pattern, [bool]$ExpectedMatch)
        $options = [System.Text.RegularExpressions.RegexOptions]::Singleline
        $matched = [System.Text.RegularExpressions.Regex]::IsMatch($content, $Pattern, $options)
        $checks.Add([pscustomobject]@{ name = $Name; passed = ($matched -eq $ExpectedMatch); detail = "matched=$matched expected=$ExpectedMatch" }) | Out-Null
    }

    function Add-CountCheckInFile {
        param([string]$File, [string]$Name, [string]$Pattern, [int]$Expected)
        $text = Get-FileContent -RelativePath $File
        if ($null -eq $text) {
            $checks.Add([pscustomobject]@{ name = $Name; passed = $false; detail = "file_missing=$File" }) | Out-Null
            return
        }

        $count = Get-RegexCount -Text $text -Pattern $Pattern
        $checks.Add([pscustomobject]@{ name = $Name; passed = ($count -eq $Expected); detail = "file=$File count=$count expected=$Expected" }) | Out-Null
    }

    function Add-MatchCheckInFile {
        param([string]$File, [string]$Name, [string]$Pattern, [bool]$ExpectedMatch)
        $text = Get-FileContent -RelativePath $File
        if ($null -eq $text) {
            $checks.Add([pscustomobject]@{ name = $Name; passed = $false; detail = "file_missing=$File" }) | Out-Null
            return
        }

        $options = [System.Text.RegularExpressions.RegexOptions]::Singleline
        $matched = [System.Text.RegularExpressions.Regex]::IsMatch($text, $Pattern, $options)
        $checks.Add([pscustomobject]@{ name = $Name; passed = ($matched -eq $ExpectedMatch); detail = "file=$File matched=$matched expected=$ExpectedMatch" }) | Out-Null
    }

    function Add-FileExistsCheck {
        param([string]$Name, [string]$RelativePath, [bool]$ExpectedExists)
        $exists = Test-Path (Join-Path $WorkspaceDir $RelativePath) -PathType Leaf
        $checks.Add([pscustomobject]@{ name = $Name; passed = ($exists -eq $ExpectedExists); detail = "file=$RelativePath exists=$exists expected=$ExpectedExists" }) | Out-Null
    }

    switch ($TaskId) {
        "rename-overload-v1" {
            Add-CountCheck -Name "handle_int_signature_once" -Pattern 'public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "process_int_signature_removed" -Pattern 'public\s+void\s+Process\s*\(\s*int\s+value\s*\)' -Expected 0
            Add-CountCheck -Name "process_string_signature_preserved" -Pattern 'public\s+void\s+Process\s*\(\s*string\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "handle_invocation_updated_once" -Pattern '\bHandle\s*\(\s*1\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_int_invocation_removed" -Pattern '\bProcess\s*\(\s*1\s*\)\s*;' -Expected 0
            Add-CountCheck -Name "process_string_invocation_preserved" -Pattern '\bProcess\s*\(\s*"x"\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_string_literal_preserved" -Pattern 'System\.Console\.WriteLine\(\s*"Process"\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "forbidden_handle_string_invocation_absent" -Pattern '\bHandle\s*\(\s*"x"\s*\)\s*;' -Expected 0
        }
        "rename-overload-collision-classes-v1" {
            Add-MatchCheck -Name "overloads_has_handle_int" -Pattern 'public\s+class\s+Overloads[\s\S]*?public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -ExpectedMatch $true
            Add-MatchCheck -Name "overloads_no_process_int_before_other_class" -Pattern 'public\s+class\s+Overloads[\s\S]*?public\s+void\s+Process\s*\(\s*int\s+value\s*\)[\s\S]*?public\s+class\s+AnotherOverloads' -ExpectedMatch $false
            Add-MatchCheck -Name "other_overloads_still_process_int" -Pattern 'public\s+class\s+AnotherOverloads[\s\S]*?public\s+void\s+Process\s*\(\s*int\s+value\s*\)' -ExpectedMatch $true
            Add-MatchCheck -Name "other_overloads_no_handle_int" -Pattern 'public\s+class\s+AnotherOverloads[\s\S]*?public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -ExpectedMatch $false

            Add-CountCheck -Name "handle_int_signature_once" -Pattern 'public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "process_int_signature_once" -Pattern 'public\s+void\s+Process\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "handle_invocation_updated_once" -Pattern '\bHandle\s*\(\s*1\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_int_invocation_once" -Pattern '\bProcess\s*\(\s*1\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_string_invocation_preserved_once" -Pattern '\bProcess\s*\(\s*"x"\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_string_literal_preserved_twice" -Pattern 'System\.Console\.WriteLine\(\s*"Process"\s*\)\s*;' -Expected 2
        }
        "rename-overload-collision-nested-v1" {
            Add-MatchCheck -Name "outer_has_handle_int" -Pattern 'public\s+class\s+Overloads[\s\S]*?public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -ExpectedMatch $true
            Add-MatchCheck -Name "outer_no_process_int_before_inner" -Pattern 'public\s+class\s+Overloads[\s\S]*?public\s+void\s+Process\s*\(\s*int\s+value\s*\)[\s\S]*?public\s+class\s+Inner' -ExpectedMatch $false
            Add-MatchCheck -Name "inner_still_process_int" -Pattern 'public\s+class\s+Inner[\s\S]*?public\s+void\s+Process\s*\(\s*int\s+value\s*\)' -ExpectedMatch $true
            Add-MatchCheck -Name "inner_no_handle_int" -Pattern 'public\s+class\s+Inner[\s\S]*?public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -ExpectedMatch $false

            Add-CountCheck -Name "handle_int_signature_once" -Pattern 'public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "process_int_signature_once" -Pattern 'public\s+void\s+Process\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "handle_invocation_updated_once" -Pattern '\bHandle\s*\(\s*1\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_int_invocation_once" -Pattern '\bProcess\s*\(\s*1\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_string_invocation_preserved_twice" -Pattern '\bProcess\s*\(\s*"x"\s*\)\s*;' -Expected 2
            Add-CountCheck -Name "process_string_literal_preserved_twice" -Pattern 'System\.Console\.WriteLine\(\s*"Process"\s*\)\s*;' -Expected 2
            Add-CountCheck -Name "forbidden_handle_string_invocation_absent" -Pattern '\bHandle\s*\(\s*"x"\s*\)\s*;' -Expected 0
        }
        "rename-overload-collision-generic-v1" {
            Add-CountCheck -Name "handle_int_signature_once" -Pattern 'public\s+void\s+Handle\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "process_int_signature_removed" -Pattern 'public\s+void\s+Process\s*\(\s*int\s+value\s*\)' -Expected 0
            Add-CountCheck -Name "process_string_signature_preserved" -Pattern 'public\s+void\s+Process\s*\(\s*string\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "process_generic_signature_preserved" -Pattern 'public\s+void\s+Process\s*<' -Expected 1
            Add-CountCheck -Name "forbidden_handle_generic_signature_absent" -Pattern 'public\s+void\s+Handle\s*<' -Expected 0

            Add-CountCheck -Name "handle_invocation_updated_once" -Pattern '\bHandle\s*\(\s*1\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_object_invocation_preserved" -Pattern '\bProcess\s*<\s*object\s*>\s*\(' -Expected 1
            Add-CountCheck -Name "forbidden_handle_object_invocation_absent" -Pattern '\bHandle\s*<\s*object\s*>\s*\(' -Expected 0
            Add-CountCheck -Name "process_string_invocation_preserved" -Pattern '\bProcess\s*\(\s*"x"\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "process_string_literal_preserved" -Pattern 'System\.Console\.WriteLine\(\s*"Process"\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "forbidden_handle_string_invocation_absent" -Pattern '\bHandle\s*\(\s*"x"\s*\)\s*;' -Expected 0
        }
        "change-signature-named-args-v1" {
            Add-CountCheck -Name "combine_signature_primary_right_once" -Pattern 'public\s+int\s+Combine\s*\(\s*int\s+primary\s*,\s*int\s+right\s*\)' -Expected 1
            Add-CountCheck -Name "combine_signature_left_removed" -Pattern 'public\s+int\s+Combine\s*\(\s*int\s+left\s*,\s*int\s+right\s*\)' -Expected 0
            Add-CountCheck -Name "named_arg_updated_once" -Pattern '\bCombine\s*\(\s*primary\s*:\s*1\s*,\s*right\s*:\s*2\s*\)' -Expected 1
            Add-CountCheck -Name "old_named_arg_removed" -Pattern '\bCombine\s*\(\s*left\s*:\s*1\s*,\s*right\s*:\s*2\s*\)' -Expected 0
            Add-CountCheck -Name "positional_call_preserved_once" -Pattern '\bCombine\s*\(\s*3\s*,\s*4\s*\)' -Expected 1
        }
        "update-usings-cleanup-v1" {
            Add-CountCheck -Name "system_using_present" -Pattern '\busing\s+System\s*;' -Expected 1
            Add-CountCheck -Name "system_text_using_present" -Pattern '\busing\s+System\.Text\s*;' -Expected 1
            Add-CountCheck -Name "system_linq_using_removed" -Pattern '\busing\s+System\.Linq\s*;' -Expected 0
            Add-CountCheck -Name "stringbuilder_usage_preserved" -Pattern '\bStringBuilder\b' -Expected 1
        }
        "add-member-threshold-v1" {
            Add-CountCheck -Name "has_long_name_signature_once" -Pattern 'public\s+bool\s+HasLongName\s*\(\s*int\s+threshold\s*\)' -Expected 1
            Add-CountCheck -Name "has_long_name_body_once" -Pattern 'return\s+Name\.Length\s*>\s*threshold\s*;' -Expected 1
            Add-CountCheck -Name "profile_ctor_preserved" -Pattern 'public\s+Profile\s*\(\s*string\s+name\s*\)' -Expected 1
        }
        "replace-member-body-guard-v1" {
            Add-CountCheck -Name "parsecount_signature_preserved" -Pattern 'public\s+int\s+ParseCount\s*\(\s*string\?\s+input\s*\)' -Expected 1
            Add-CountCheck -Name "null_whitespace_check_present" -Pattern 'string\.IsNullOrWhiteSpace\s*\(\s*input\s*\)' -Expected 1
            Add-CountCheck -Name "trimmed_length_expression_present" -Pattern 'input\.Trim\(\)\.Length' -Expected 1
            Add-CountCheck -Name "null_forgiving_removed" -Pattern 'input!\.Length' -Expected 0
        }
        "create-file-audit-log-v1" {
            Add-FileExistsCheck -Name "auditlog_file_created" -RelativePath "AuditLog.cs" -ExpectedExists $true
            Add-CountCheckInFile -File "AuditLog.cs" -Name "auditlog_class_once" -Pattern 'public\s+class\s+AuditLog' -Expected 1
            Add-CountCheckInFile -File "AuditLog.cs" -Name "entries_property_once" -Pattern 'public\s+List<string>\s+Entries\s*\{\s*get\s*;\s*\}\s*=\s*new(\s*List<string>\s*)?\(\)\s*;' -Expected 1
            Add-CountCheckInFile -File "AuditLog.cs" -Name "add_method_once" -Pattern 'public\s+void\s+Add\s*\(\s*string\s+message\s*\)' -Expected 1
            Add-CountCheckInFile -File "AuditLog.cs" -Name "entries_add_call_once" -Pattern 'Entries\.Add\s*\(\s*message\s*\)\s*;' -Expected 1
            Add-CountCheck -Name "target_worker_class_still_present" -Pattern 'public\s+class\s+Worker' -Expected 1
        }
        "rename-multifile-collision-v1" {
            Add-CountCheck -Name "service_scale_int_signature_once" -Pattern 'public\s+int\s+Scale\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheck -Name "service_transform_int_removed" -Pattern 'public\s+int\s+Transform\s*\(\s*int\s+value\s*\)' -Expected 0
            Add-CountCheck -Name "service_transform_string_preserved" -Pattern 'public\s+string\s+Transform\s*\(\s*string\s+value\s*\)' -Expected 1
            Add-CountCheckInFile -File "Consumer.cs" -Name "consumer_updated_to_scale" -Pattern '\bservice\.Scale\s*\(\s*2\s*\)\s*;' -Expected 1
            Add-CountCheckInFile -File "Consumer.cs" -Name "consumer_old_transform_removed" -Pattern '\bservice\.Transform\s*\(\s*2\s*\)\s*;' -Expected 0
            Add-CountCheckInFile -File "AnotherService.cs" -Name "another_service_transform_preserved" -Pattern 'public\s+int\s+Transform\s*\(\s*int\s+value\s*\)' -Expected 1
            Add-CountCheckInFile -File "AnotherService.cs" -Name "another_service_scale_absent" -Pattern 'public\s+int\s+Scale\s*\(\s*int\s+value\s*\)' -Expected 0
        }
    }

    $passed = (@($checks | Where-Object passed -eq $false).Count -eq 0)
    return [ordered]@{
        passed = $passed
        checks = $checks
    }
}

function New-ClaudeIsolatedConfig {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][bool]$InstallSkill,
        [Parameter(Mandatory = $true)][string]$SkillName,
        [Parameter(Mandatory = $true)][string]$SkillSourceDir
    )

    $agentHome = Join-Path $RunDirectory ".agent-home"
    $claudeConfigRoot = Join-Path $agentHome "claude-config"
    New-Item -ItemType Directory -Force -Path $claudeConfigRoot | Out-Null

    $existing = Get-ClaudeConfigRoot
    if (Test-Path $existing -PathType Container) {
        foreach ($fileName in @(
                ".credentials.json",
                "settings.json",
                "plugins\\installed_plugins.json",
                "plugins\\config.json",
                "plugins\\known_marketplaces.json"
            )) {
            Copy-FileIfExists `
                -SourcePath (Join-Path $existing $fileName) `
                -DestinationPath (Join-Path $claudeConfigRoot $fileName)
        }
    }

    if ($InstallSkill) {
        $dest = Join-Path $claudeConfigRoot ("skills\\{0}" -f $SkillName)
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -Recurse -Force -Path (Join-Path $SkillSourceDir "*") -Destination $dest
    }

    return $claudeConfigRoot
}

function Invoke-ClaudeRun {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$PromptText,
        [Parameter(Mandatory = $true)][string]$ClaudeConfigDir,
        [Parameter(Mandatory = $true)][string]$Model,
        [Parameter(Mandatory = $true)][string]$TranscriptPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $stdoutPath = "$TranscriptPath.stdout.tmp"
    $stderrPath = "$TranscriptPath.stderr.tmp"
    $stdinPath = "$TranscriptPath.stdin.tmp"

    $oldClaudeConfig = $env:CLAUDE_CONFIG_DIR
    $oldAnthropicConfig = $env:ANTHROPIC_CONFIG_DIR
    $env:CLAUDE_CONFIG_DIR = $ClaudeConfigDir
    $env:ANTHROPIC_CONFIG_DIR = $ClaudeConfigDir

    $proc = $null
    $timedOut = $false
    $exitCode = $null

    try {
        Set-Content -Path $stdinPath -Value $PromptText -NoNewline

        $argList = @(
            "-p",
            "--verbose",
            "--output-format", "stream-json",
            "--permission-mode", "bypassPermissions",
            "--model", $Model
        )

        $proc = Start-Process -FilePath "claude" -ArgumentList $argList -WorkingDirectory $RunDirectory -RedirectStandardInput $stdinPath -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -NoNewWindow -PassThru
        $finished = $proc.WaitForExit($TimeoutSeconds * 1000)
        if (-not $finished) {
            $timedOut = $true
            try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
        }

        if (-not $timedOut) {
            $exitCode = $proc.ExitCode
        }

        $stdoutText = ""
        $stderrText = ""
        if (Test-Path $stdoutPath -PathType Leaf) { $stdoutText = Get-Content -Raw -Path $stdoutPath }
        if (Test-Path $stderrPath -PathType Leaf) { $stderrText = Get-Content -Raw -Path $stderrPath }

        $parts = New-Object System.Collections.Generic.List[string]
        if (-not [string]::IsNullOrWhiteSpace($stdoutText)) { $parts.Add($stdoutText) | Out-Null }
        if (-not [string]::IsNullOrWhiteSpace($stderrText)) { $parts.Add($stderrText) | Out-Null }
        if ($timedOut) { $parts.Add('{"type":"result","subtype":"timeout","is_error":true,"result":"Timed out"}') | Out-Null }

        $combined = [string]::Join("`n", $parts)
        Set-Content -Path $TranscriptPath -Value $combined -NoNewline
    } finally {
        if (Test-Path $stdinPath -PathType Leaf) { Remove-Item -Force $stdinPath }
        if (Test-Path $stdoutPath -PathType Leaf) { Remove-Item -Force $stdoutPath }
        if (Test-Path $stderrPath -PathType Leaf) { Remove-Item -Force $stderrPath }
        $env:CLAUDE_CONFIG_DIR = $oldClaudeConfig
        $env:ANTHROPIC_CONFIG_DIR = $oldAnthropicConfig
    }

    return [ordered]@{
        timed_out = $timedOut
        exit_code = $exitCode
    }
}

function Parse-Transcript {
    param([Parameter(Mandatory = $true)][string]$TranscriptPath)

    function Get-OptionalPropertyValue {
        param($Object, [string]$Name)
        if ($null -eq $Object) { return $null }
        $prop = $Object.PSObject.Properties[$Name]
        if ($null -eq $prop) { return $null }
        return $prop.Value
    }

    $lines = Get-Content -Path $TranscriptPath
    $events = New-Object System.Collections.Generic.List[object]
    foreach ($line in $lines) {
        $text = [string]$line
        if (-not $text.StartsWith("{")) { continue }
        try { $events.Add(($text | ConvertFrom-Json)) | Out-Null } catch { }
    }

    $init = $events | Where-Object {
        [string](Get-OptionalPropertyValue -Object $_ -Name "type") -eq "system" -and
        [string](Get-OptionalPropertyValue -Object $_ -Name "subtype") -eq "init"
    } | Select-Object -First 1
    $skills = @()
    $initSkills = Get-OptionalPropertyValue -Object $init -Name "skills"
    if ($null -ne $initSkills) { $skills = @($initSkills | ForEach-Object { [string]$_ }) }

    $toolUses = New-Object System.Collections.Generic.List[object]
    foreach ($e in $events) {
        $etype = [string](Get-OptionalPropertyValue -Object $e -Name "type")
        if ($etype -ne "assistant") { continue }

        $msg = Get-OptionalPropertyValue -Object $e -Name "message"
        $content = Get-OptionalPropertyValue -Object $msg -Name "content"
        if ($null -eq $content) { continue }

        foreach ($c in $content) {
            $ctype = [string](Get-OptionalPropertyValue -Object $c -Name "type")
            if ($ctype -eq "tool_use") { $toolUses.Add($c) | Out-Null }
        }
    }

    # Transcript text scan is the most robust (tool JSON shapes and stderr behavior vary by Claude versions).
    $roslynIndicators = Get-RoslynIndicatorCountFromTranscriptText -TranscriptPath $TranscriptPath
    $authError = $false
    $authErrorMessage = $null
    foreach ($e in $events) {
        $err = [string](Get-OptionalPropertyValue -Object $e -Name "error")
        if ($null -ne $err -and $err -match "(?i)authentication_failed") {
            $authError = $true
        }

        $etype = [string](Get-OptionalPropertyValue -Object $e -Name "type")
        $msg = Get-OptionalPropertyValue -Object $e -Name "message"
        $content = Get-OptionalPropertyValue -Object $msg -Name "content"
        if ($etype -eq "assistant" -and $null -ne $content) {
            foreach ($c in $content) {
                if ([string](Get-OptionalPropertyValue -Object $c -Name "type") -eq "text") {
                    $txt = [string](Get-OptionalPropertyValue -Object $c -Name "text")
                    if ($txt -match "(?i)does not have access to Claude|login again|contact your administrator") {
                        $authError = $true
                        if ([string]::IsNullOrWhiteSpace($authErrorMessage)) { $authErrorMessage = $txt }
                    }
                }
            }
        }
    }

    $result = $events | Where-Object { [string](Get-OptionalPropertyValue -Object $_ -Name "type") -eq "result" } | Select-Object -Last 1
    $usage = Get-OptionalPropertyValue -Object $result -Name "usage"
    if ($authError -and [string]::IsNullOrWhiteSpace($authErrorMessage) -and $null -ne $result) {
        $resultText = [string](Get-OptionalPropertyValue -Object $result -Name "result")
        if (-not [string]::IsNullOrWhiteSpace($resultText)) { $authErrorMessage = $resultText }
    }

    return [ordered]@{
        skills = $skills
        tool_uses = $toolUses
        tool_use_count = $toolUses.Count
        roslyn_indicator_count = $roslynIndicators
        roslyn_used = ($roslynIndicators -gt 0)
        auth_error = $authError
        auth_error_message = $authErrorMessage
        usage = $usage
        events = $events
    }
}

$repoRoot = Resolve-RepoRoot
$outputRootFull = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot
New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null

$skillSourceDir = Join-Path $repoRoot ("skills\\{0}" -f $SkillName)
if (-not (Test-Path $skillSourceDir -PathType Container)) {
    throw "Skill source folder not found: $skillSourceDir"
}

$toolsDir = Join-Path $outputRootFull "tools\\roslyn-cli"
$cliDllPath = Publish-CliOnce -RepoRoot $repoRoot -PublishDirectory $toolsDir

$runs = New-Object System.Collections.Generic.List[object]

for ($rep = 1; $rep -le $Replicates; $rep++) {
    foreach ($id in $TaskId) {
        $task = Get-TaskDefinition -TaskId $id

        $conditions = New-Object System.Collections.Generic.List[object]
        $conditions.Add(@{ id = "no-skill"; install = $false; invoke = $false; budgeted = $false }) | Out-Null
        $conditions.Add(@{ id = "with-skill"; install = $true; invoke = $false; budgeted = $false }) | Out-Null
        if ($IncludeExplicitInvocation) {
            $conditions.Add(@{ id = "with-skill-invoked"; install = $true; invoke = $true; budgeted = $false }) | Out-Null
        }
        if ($IncludeTaskCallBudgetGuidance) {
            $conditions.Add(@{ id = "with-skill-invoked-budgeted"; install = $true; invoke = $true; budgeted = $true }) | Out-Null
        }

        foreach ($condition in $conditions) {
            $runName = "{0}-{1}-r{2:00}" -f $id, $condition.id, $rep
            $runDir = Join-Path $outputRootFull $runName
            $wsDir = Join-Path $runDir "workspace"
            New-Item -ItemType Directory -Force -Path $runDir | Out-Null

            $additionalFiles = $null
            if ($task -is [System.Collections.IDictionary] -and $task.Contains("additional_files")) {
                $additionalFiles = $task["additional_files"]
            } elseif ($task.PSObject.Properties.Name -contains "additional_files") {
                $additionalFiles = $task.additional_files
            }
            Write-WorkspaceFiles -WorkspaceDir $wsDir -TargetContent ([string]$task.target) -AdditionalFiles $additionalFiles
            Write-RoscliWrappers -RunDirectory $wsDir -CliDllPath $cliDllPath

            $cfg = New-ClaudeIsolatedConfig -RunDirectory $runDir -InstallSkill ([bool]$condition.install) -SkillName $SkillName -SkillSourceDir $skillSourceDir

            $transcriptPath = Join-Path $runDir "transcript.jsonl"
            $applyCallBudget = ([bool]$condition.install -and [bool]$condition.budgeted)
            $taskCallBudget = if ($applyCallBudget) { Get-TaskCallBudget -TaskId ([string]$id) } else { $null }
            $promptText = Build-RunPrompt `
                -TaskPrompt ([string]$task.prompt) `
                -TaskId ([string]$id) `
                -SkillName $SkillName `
                -InvokeSkill ([bool]$condition.invoke) `
                -IncludeCallBudgetGuidance $applyCallBudget
            $preWorkspaceHashes = Get-WorkspaceSourceHashes -WorkspaceDir $wsDir
            $runExec = Invoke-ClaudeRun -RunDirectory $wsDir -PromptText $promptText -ClaudeConfigDir $cfg -Model $ClaudeModel -TranscriptPath $transcriptPath -TimeoutSeconds $ClaudeTimeoutSeconds

            $parsed = Parse-Transcript -TranscriptPath $transcriptPath
            $skillLoaded = ($parsed.skills -contains $SkillName)
            $taskCallBudgetExceeded = if ($null -eq $taskCallBudget) { $null } else { ([int]$parsed.tool_use_count -gt [int]$taskCallBudget) }
            if (-not $IgnoreAuthenticationError -and [bool]$parsed.auth_error) {
                throw "Run '$runName' hit Claude authentication/access failure: $($parsed.auth_error_message)"
            }

            $constraints = Invoke-ConstraintChecks -WorkspaceDir $wsDir -TaskId $id
            $constraintsPath = Join-Path $runDir "constraint-checks.json"
            $constraints | ConvertTo-Json -Depth 10 | Set-Content -Path $constraintsPath

            $targetPath = Join-Path $wsDir "Target.cs"
            $originalPath = Join-Path $wsDir "Target.original.cs"
            $targetHash = Get-FileSha256 -Path $targetPath
            $originalHash = Get-FileSha256 -Path $originalPath
            $fileChanged = ($null -ne $targetHash -and $null -ne $originalHash -and ($targetHash -ne $originalHash))
            $postWorkspaceHashes = Get-WorkspaceSourceHashes -WorkspaceDir $wsDir
            $workspaceChanged = Get-WorkspaceChanged -Before $preWorkspaceHashes -After $postWorkspaceHashes

            $diffPath = Join-Path $runDir "Target.diff.txt"
            if ((Test-Path $originalPath -PathType Leaf) -and (Test-Path $targetPath -PathType Leaf)) {
                [void](Try-Write-GitNoIndexDiff -OriginalPath $originalPath -UpdatedPath $targetPath -OutputPath $diffPath)
            }

            $buildSucceeded = $null
            $buildOutputPath = $null
            if ($IncludeDotnetBuildGate) {
                $buildOutputPath = Join-Path $runDir "dotnet-build.txt"
                $buildSucceeded = Invoke-DotnetBuild -WorkspaceDir $wsDir -OutputPath $buildOutputPath
            }

            if ($condition.install -and $FailIfSkillNotLoaded -and -not $skillLoaded) {
                throw "Run '$runName' expected skill '$SkillName' to be loaded, but init.skills did not include it."
            }
            if ($condition.install -and $FailIfNoRoslynUsed -and -not [bool]$parsed.roslyn_used) {
                throw "Run '$runName' expected Roslyn usage (scripts/roscli), but none was detected."
            }
            if ($condition.install -and [bool]$condition.budgeted -and $FailIfTaskCallBudgetExceeded -and [bool]$taskCallBudgetExceeded) {
                throw "Run '$runName' exceeded task call budget: tool_use_count=$($parsed.tool_use_count) budget=$taskCallBudget"
            }

            $runs.Add([pscustomobject]@{
                    run_name = $runName
                    task_id = [string]$id
                    condition_id = [string]$condition.id
                    replicate = $rep
                    skill_installed = [bool]$condition.install
                    skill_invoked = [bool]$condition.invoke
                    skill_loaded = $skillLoaded
                    roslyn_used = [bool]$parsed.roslyn_used
                    roslyn_indicator_count = [int]$parsed.roslyn_indicator_count
                    tool_use_count = [int]$parsed.tool_use_count
                    task_call_budget = $taskCallBudget
                    task_call_budget_applied = [bool]$applyCallBudget
                    task_call_budget_exceeded = $taskCallBudgetExceeded
                    constraint_checks_passed = [bool]$constraints.passed
                    constraint_checks_path = $constraintsPath
                    file_changed = $fileChanged
                    workspace_changed = $workspaceChanged
                    target_sha256 = $targetHash
                    original_sha256 = $originalHash
                    diff_path = $diffPath
                    build_succeeded = $buildSucceeded
                    build_output_path = $buildOutputPath
                    run_timed_out = [bool]$runExec.timed_out
                    run_exit_code = $runExec.exit_code
                    auth_error = [bool]$parsed.auth_error
                    auth_error_message = $parsed.auth_error_message
                    transcript_path = $transcriptPath
                    claude_config_dir = $cfg
                    usage = $parsed.usage
                }) | Out-Null
        }
    }
}

# Post-process Roslyn usage from transcripts to avoid any transient read/parse issues during run execution.
# Rebuild run records rather than mutating, to avoid PSObject/list mutation edge cases under StrictMode.
$runsFinal = New-Object System.Collections.Generic.List[object]
foreach ($r in $runs) {
    $count = Get-RoslynIndicatorCountFromTranscriptText -TranscriptPath ([string]$r.transcript_path)
    $h = [ordered]@{}
    foreach ($p in $r.PSObject.Properties) { $h[$p.Name] = $p.Value }
    $h["roslyn_indicator_count"] = [int]$count
    $h["roslyn_used"] = ($count -gt 0)
    $runsFinal.Add(([pscustomobject]$h)) | Out-Null
}
$runs = $runsFinal

$summary = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    skill_name = $SkillName
    model = $ClaudeModel
    replicates = $Replicates
    include_explicit_invocation = [bool]$IncludeExplicitInvocation
    include_task_call_budget_guidance = [bool]$IncludeTaskCallBudgetGuidance
    task_ids = $TaskId
    runs = $runs
}

$summaryPath = Join-Path $outputRootFull "skill-trigger-summary.json"
$summary | ConvertTo-Json -Depth 20 | Set-Content -Path $summaryPath

$byCondition = $runs | Group-Object condition_id | Sort-Object Name | ForEach-Object {
    $g = @($_.Group)
    $gCount = $g.Count
    $skillLoadedCount = @($g | Where-Object skill_loaded).Count
    $roslynUsedCount = @($g | Where-Object roslyn_used).Count
    $passedCount = @($g | Where-Object constraint_checks_passed).Count
    $changedCount = @($g | Where-Object file_changed).Count
    $workspaceChangedCount = @($g | Where-Object workspace_changed).Count
    $buildCount = @($g | Where-Object { $_.build_succeeded -eq $true }).Count
    $timeoutCount = @($g | Where-Object run_timed_out).Count
    $authErrorCount = @($g | Where-Object auth_error).Count
    $budgetedRuns = @($g | Where-Object task_call_budget_applied)
    $budgetExceededCount = @($g | Where-Object task_call_budget_exceeded).Count
    [pscustomobject]@{
        condition_id = $_.Name
        runs = $gCount
        skill_loaded_rate = if (@($g | Where-Object skill_installed).Count -gt 0) { [math]::Round(($skillLoadedCount / [double]$gCount), 3) } else { $null }
        roslyn_used_rate = [math]::Round(($roslynUsedCount / [double]$gCount), 3)
        pass_rate = [math]::Round(($passedCount / [double]$gCount), 3)
        file_changed_rate = [math]::Round(($changedCount / [double]$gCount), 3)
        workspace_changed_rate = [math]::Round(($workspaceChangedCount / [double]$gCount), 3)
        dotnet_build_pass_rate = if (@($g | Where-Object { $_.build_succeeded -ne $null }).Count -gt 0) { [math]::Round(($buildCount / [double]$gCount), 3) } else { $null }
        timeout_rate = [math]::Round(($timeoutCount / [double]$gCount), 3)
        auth_error_rate = [math]::Round(($authErrorCount / [double]$gCount), 3)
        task_call_budget_applied_rate = [math]::Round((@($budgetedRuns).Count / [double]$gCount), 3)
        task_call_budget_exceeded_rate = if (@($budgetedRuns).Count -gt 0) { [math]::Round(($budgetExceededCount / [double]@($budgetedRuns).Count), 3) } else { $null }
        avg_tool_uses = [math]::Round((($g | Measure-Object tool_use_count -Average).Average), 3)
        avg_roslyn_indicators = [math]::Round((($g | Measure-Object roslyn_indicator_count -Average).Average), 3)
    }
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Claude skill trigger sweep") | Out-Null
$md.Add("") | Out-Null
$md.Add(("Skill: {0}" -f $SkillName)) | Out-Null
$md.Add(("Model: {0}" -f $ClaudeModel)) | Out-Null
$md.Add(("Replicates: {0}" -f $Replicates)) | Out-Null
$md.Add(("IncludeExplicitInvocation: {0}" -f [bool]$IncludeExplicitInvocation)) | Out-Null
$md.Add(("IncludeTaskCallBudgetGuidance: {0}" -f [bool]$IncludeTaskCallBudgetGuidance)) | Out-Null
$md.Add("Tasks: " + ($TaskId -join ", ")) | Out-Null
$md.Add("") | Out-Null
$md.Add("| condition | runs | skill_loaded_rate | roslyn_used_rate | pass_rate | file_changed_rate | workspace_changed_rate | dotnet_build_pass_rate | timeout_rate | auth_error_rate | task_call_budget_applied_rate | task_call_budget_exceeded_rate | avg_tool_uses | avg_roslyn_indicators |") | Out-Null
$md.Add("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |") | Out-Null
foreach ($row in $byCondition) {
    $md.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} |' -f $row.condition_id, $row.runs, $row.skill_loaded_rate, $row.roslyn_used_rate, $row.pass_rate, $row.file_changed_rate, $row.workspace_changed_rate, $row.dotnet_build_pass_rate, $row.timeout_rate, $row.auth_error_rate, $row.task_call_budget_applied_rate, $row.task_call_budget_exceeded_rate, $row.avg_tool_uses, $row.avg_roslyn_indicators)) | Out-Null
}
$mdPath = Join-Path $outputRootFull "skill-trigger-summary.md"
$mdText = [string]::Join("`n", $md)
Set-Content -Path $mdPath -Value $mdText -NoNewline

Write-Host ("SUMMARY={0}" -f ([System.IO.Path]::GetFullPath($summaryPath)))
Write-Host ("SUMMARY_MD={0}" -f ([System.IO.Path]::GetFullPath($mdPath)))
