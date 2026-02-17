param(
    [string]$SkillName = "roslynskills-tight",
    [string]$SkillSourceDir = "",
    [string]$ClaudeCommand = "claude"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-RepoRoot {
    $here = (Resolve-Path $PSScriptRoot).Path
    return (Resolve-Path (Join-Path $here "..\\..")).Path
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

function Parse-ClaudeInitEvent {
    param([Parameter(Mandatory = $true)][string[]]$Lines)

    foreach ($line in $Lines) {
        $candidate = [string]$line
        if (-not $candidate.StartsWith("{")) {
            continue
        }

        try {
            $obj = $candidate | ConvertFrom-Json
        } catch {
            continue
        }

        if ($obj.type -eq "system" -and $obj.subtype -eq "init") {
            return $obj
        }
    }

    return $null
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($SkillSourceDir)) {
    $SkillSourceDir = Join-Path $repoRoot ("skills\\{0}" -f $SkillName)
}

if (-not (Test-Path $SkillSourceDir -PathType Container)) {
    throw "SkillSourceDir not found: $SkillSourceDir"
}

$probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("roslynskills-claude-skill-load-" + [Guid]::NewGuid().ToString("n").Substring(0, 10))
$configRoot = Join-Path $probeRoot "claude-config"
$skillsRoot = Join-Path $configRoot "skills"
$skillDest = Join-Path $skillsRoot $SkillName

New-Item -ItemType Directory -Force -Path $skillDest | Out-Null
Copy-Item -Recurse -Force -Path (Join-Path $SkillSourceDir "*") -Destination $skillDest

# Copy auth/config so the probe uses a realistic Claude environment; if auth is missing, we still expect init to show loaded skills.
$existingConfig = Get-ClaudeConfigRoot
if (Test-Path $existingConfig -PathType Container) {
    foreach ($fileName in @(
            ".credentials.json",
            "settings.json"
        )) {
        Copy-FileIfExists `
            -SourcePath (Join-Path $existingConfig $fileName) `
            -DestinationPath (Join-Path $configRoot $fileName)
    }
}

$env:CLAUDE_CONFIG_DIR = $configRoot
$env:ANTHROPIC_CONFIG_DIR = $configRoot

Push-Location $probeRoot
try {
    $output = & $ClaudeCommand -p --verbose --output-format stream-json "Say only 'ok'" 2>&1
    $lines = if ($output -is [System.Array]) { $output } else { @([string]$output) }
} finally {
    Pop-Location
}

$init = Parse-ClaudeInitEvent -Lines $lines
if ($null -eq $init) {
    throw "Could not locate Claude init event in output. Probe root: $probeRoot"
}

$loadedSkills = @()
if ($null -ne $init.skills) {
    $loadedSkills = @($init.skills | ForEach-Object { [string]$_ })
}

$loaded = $loadedSkills -contains $SkillName
$slashCommands = @()
if ($null -ne $init.slash_commands) {
    $slashCommands = @($init.slash_commands | ForEach-Object { [string]$_ })
}

[pscustomobject]@{
    ok = $loaded
    skill_name = $SkillName
    skill_loaded = $loaded
    slash_command_present = ($slashCommands -contains $SkillName)
    loaded_skills = $loadedSkills
    probe_root = $probeRoot
    claude_config_dir = $configRoot
    claude_cwd = [string]$init.cwd
    claude_code_version = [string]$init.claude_code_version
    model = [string]$init.model
}

if (-not $loaded) {
    throw "Claude did not report skill '$SkillName' as loaded. See probe_root: $probeRoot"
}

