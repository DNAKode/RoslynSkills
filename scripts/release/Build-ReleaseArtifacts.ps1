param(
    [string]$Version = "",
    [string]$OutputRoot = "",
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Normalize-Version {
    param([Parameter(Mandatory = $true)][string]$RawVersion)

    $trimmed = $RawVersion.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "Version cannot be empty."
    }

    if ($trimmed.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $trimmed.Substring(1)
    }

    return $trimmed
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Push-Location $WorkingDirectory
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet command failed: dotnet $($Arguments -join ' ')"
        }
    } finally {
        Pop-Location
    }
}

function Set-ExecutableIfSupported {
    param([Parameter(Mandatory = $true)][string]$Path)

    $isWindowsHost = $false
    if ($env:OS -eq "Windows_NT") {
        $isWindowsHost = $true
    }

    if ($isWindowsHost) {
        return
    }

    & chmod +x $Path
    if ($LASTEXITCODE -ne 0) {
        throw "chmod failed for '$Path'."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$versionInput = $Version
if ([string]::IsNullOrWhiteSpace($versionInput)) {
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
        $versionInput = $env:GITHUB_REF_NAME
    } else {
        throw "Provide -Version (for example 0.1.0 or v0.1.0)."
    }
}

$normalizedVersion = Normalize-Version -RawVersion $versionInput
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot ("artifacts/release/{0}" -f $normalizedVersion)
} elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path $OutputRoot).Path

$bundleRoot = Join-Path $OutputRoot "roslynskills-bundle"
$cliPublishDir = Join-Path $bundleRoot "cli"
$mcpPublishDir = Join-Path $bundleRoot "mcp"
$transportPublishDir = Join-Path $bundleRoot "transport"
$binDir = Join-Path $bundleRoot "bin"
$skillDir = Join-Path $bundleRoot "skills\roslynskills-research"
$tightSkillDir = Join-Path $bundleRoot "skills\roslynskills-tight"

foreach ($path in @($bundleRoot, $cliPublishDir, $mcpPublishDir, $transportPublishDir, $binDir, $skillDir, $tightSkillDir)) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

$solutionPath = Join-Path $repoRoot "RoslynSkills.slnx"
$cliProjectPath = Join-Path $repoRoot "src\RoslynSkills.Cli\RoslynSkills.Cli.csproj"
$mcpProjectPath = Join-Path $repoRoot "src\RoslynSkills.McpServer\RoslynSkills.McpServer.csproj"
$transportProjectPath = Join-Path $repoRoot "src\RoslynSkills.TransportServer\RoslynSkills.TransportServer.csproj"
$toolPackageId = "DNAKode.RoslynSkills.Cli"

Write-Host ("VERSION={0}" -f $normalizedVersion)
Write-Host ("OUTPUT_ROOT={0}" -f $OutputRoot)

Invoke-Dotnet -WorkingDirectory $repoRoot -Arguments @("restore", $solutionPath, "--nologo")

if (-not $SkipTests) {
    Invoke-Dotnet -WorkingDirectory $repoRoot -Arguments @("test", $solutionPath, "-c", $Configuration, "--nologo")
}

& (Join-Path $repoRoot "scripts\\skills\\Validate-Skills.ps1")

Invoke-Dotnet -WorkingDirectory $repoRoot -Arguments @("publish", $cliProjectPath, "-c", $Configuration, "-o", $cliPublishDir, "--nologo")
Invoke-Dotnet -WorkingDirectory $repoRoot -Arguments @("publish", $mcpProjectPath, "-c", $Configuration, "-o", $mcpPublishDir, "--nologo")
Invoke-Dotnet -WorkingDirectory $repoRoot -Arguments @("publish", $transportProjectPath, "-c", $Configuration, "-o", $transportPublishDir, "--nologo")

Invoke-Dotnet -WorkingDirectory $repoRoot -Arguments @(
    "pack",
    $cliProjectPath,
    "-c", $Configuration,
    "-o", $OutputRoot,
    "--nologo",
    "-p:Version=$normalizedVersion",
    "-p:PackageVersion=$normalizedVersion"
)

$roscliCmd = @"
@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "CLI_DLL=%SCRIPT_DIR%..\cli\RoslynSkills.Cli.dll"
dotnet "%CLI_DLL%" %*
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
"@

$roscliSh = @'
#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet "$script_dir/../cli/RoslynSkills.Cli.dll" "$@"
'@

$mcpCmd = @"
@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "MCP_DLL=%SCRIPT_DIR%..\mcp\RoslynSkills.McpServer.dll"
dotnet "%MCP_DLL%"
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
"@

$mcpSh = @'
#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet "$script_dir/../mcp/RoslynSkills.McpServer.dll"
'@

$transportCmd = @"
@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "SERVER_DLL=%SCRIPT_DIR%..\transport\RoslynSkills.TransportServer.dll"
dotnet "%SERVER_DLL%"
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
"@

$transportSh = @'
#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet "$script_dir/../transport/RoslynSkills.TransportServer.dll"
'@

$roscliCmdPath = Join-Path $binDir "roscli.cmd"
$roscliShPath = Join-Path $binDir "roscli"
$mcpCmdPath = Join-Path $binDir "roslyn-mcp.cmd"
$mcpShPath = Join-Path $binDir "roslyn-mcp"
$transportCmdPath = Join-Path $binDir "roslyn-transport.cmd"
$transportShPath = Join-Path $binDir "roslyn-transport"

Set-Content -Path $roscliCmdPath -Value $roscliCmd -NoNewline
Set-Content -Path $roscliShPath -Value $roscliSh -NoNewline
Set-Content -Path $mcpCmdPath -Value $mcpCmd -NoNewline
Set-Content -Path $mcpShPath -Value $mcpSh -NoNewline
Set-Content -Path $transportCmdPath -Value $transportCmd -NoNewline
Set-Content -Path $transportShPath -Value $transportSh -NoNewline

Set-ExecutableIfSupported -Path $roscliShPath
Set-ExecutableIfSupported -Path $mcpShPath
Set-ExecutableIfSupported -Path $transportShPath

$skillSourceDir = Join-Path $repoRoot "skills\\roslynskills-research"
if (Test-Path $skillSourceDir -PathType Container) {
    Copy-Item -Path (Join-Path $skillSourceDir "*") -Destination $skillDir -Recurse -Force
}

$tightSkillSourceDir = Join-Path $repoRoot "skills\\roslynskills-tight"
if (Test-Path $tightSkillSourceDir -PathType Container) {
    Copy-Item -Path (Join-Path $tightSkillSourceDir "*") -Destination $tightSkillDir -Recurse -Force
}

$pitOfSuccessSource = Join-Path $repoRoot "docs\PIT_OF_SUCCESS.md"
if (Test-Path $pitOfSuccessSource -PathType Leaf) {
    Copy-Item -Path $pitOfSuccessSource -Destination (Join-Path $bundleRoot "PIT_OF_SUCCESS.md") -Force
}

$bundleReadme = @"
# RoslynSkills Release Bundle

Version: $normalizedVersion

Contents:
- cli/: published RoslynSkills CLI tool package
- mcp/: published Roslyn MCP server
- transport/: published persistent transport server
- bin/: launchers
  - roscli(.cmd)
  - roslyn-mcp(.cmd)
  - roslyn-transport(.cmd)
- PIT_OF_SUCCESS.md
- skills/roslynskills-research/SKILL.md
- skills/roslynskills-research/references/
- skills/roslynskills-tight/SKILL.md

First command:
- bin/roscli quickstart
"@
Set-Content -Path (Join-Path $bundleRoot "README.txt") -Value $bundleReadme

$manifest = [ordered]@{
    version = $normalizedVersion
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    configuration = $Configuration
    bundle_root = $bundleRoot
    files = @(
        "roslynskills-bundle-$normalizedVersion.zip",
        "roslynskills-research-skill-$normalizedVersion.zip",
        "roslynskills-tight-skill-$normalizedVersion.zip",
        "$toolPackageId.$normalizedVersion.nupkg"
    )
}
$manifestPath = Join-Path $OutputRoot "release-manifest.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath

$bundleZipPath = Join-Path $OutputRoot ("roslynskills-bundle-{0}.zip" -f $normalizedVersion)
if (Test-Path $bundleZipPath -PathType Leaf) {
    Remove-Item $bundleZipPath -Force
}
Compress-Archive -Path (Join-Path $bundleRoot "*") -DestinationPath $bundleZipPath -Force

$skillZipPath = Join-Path $OutputRoot ("roslynskills-research-skill-{0}.zip" -f $normalizedVersion)
if (Test-Path $skillZipPath -PathType Leaf) {
    Remove-Item $skillZipPath -Force
}
if (Test-Path $skillSourceDir -PathType Container) {
    Compress-Archive -Path (Join-Path $skillSourceDir "*") -DestinationPath $skillZipPath -Force
}

$tightSkillZipPath = Join-Path $OutputRoot ("roslynskills-tight-skill-{0}.zip" -f $normalizedVersion)
if (Test-Path $tightSkillZipPath -PathType Leaf) {
    Remove-Item $tightSkillZipPath -Force
}
if (Test-Path $tightSkillSourceDir -PathType Container) {
    Compress-Archive -Path (Join-Path $tightSkillSourceDir "*") -DestinationPath $tightSkillZipPath -Force
}

$checksumEntries = New-Object System.Collections.Generic.List[string]
foreach ($file in (Get-ChildItem -Path $OutputRoot -File | Sort-Object Name)) {
    if ($file.Name -eq "checksums.sha256") {
        continue
    }

    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = "{0} *{1}" -f @($hash, $file.Name)
    $checksumEntries.Add($checksumLine) | Out-Null
}
$checksumsPath = Join-Path $OutputRoot "checksums.sha256"
Set-Content -Path $checksumsPath -Value $checksumEntries

Write-Host ("BUNDLE_ZIP={0}" -f $bundleZipPath)
Write-Host ("SKILL_ZIP={0}" -f $skillZipPath)
Write-Host ("TIGHT_SKILL_ZIP={0}" -f $tightSkillZipPath)
Write-Host ("MANIFEST={0}" -f $manifestPath)
Write-Host ("CHECKSUMS={0}" -f $checksumsPath)

