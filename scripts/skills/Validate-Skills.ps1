param(
    [string]$SkillsRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($SkillsRoot)) {
    $SkillsRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\\..\\skills")).Path
} elseif (-not [System.IO.Path]::IsPathRooted($SkillsRoot)) {
    $SkillsRoot = (Resolve-Path (Join-Path (Get-Location) $SkillsRoot)).Path
} else {
    $SkillsRoot = (Resolve-Path $SkillsRoot).Path
}

if (-not (Test-Path $SkillsRoot -PathType Container)) {
    throw "Skills root not found: $SkillsRoot"
}

function Get-FrontmatterBlock {
    param([Parameter(Mandatory = $true)][string]$Content)

    $lines = $Content -split "`r?`n"
    $first = $lines[0].Trim().TrimStart([char]0xFEFF)
    if ($lines.Count -lt 3 -or $first -ne "---") {
        return $null
    }

    for ($i = 1; $i -lt [Math]::Min($lines.Count, 80); $i++) {
        if ($lines[$i].Trim() -eq "---") {
            return [pscustomobject]@{
                block = ($lines[1..($i - 1)] -join "`n")
                end_index = $i
            }
        }
    }

    return $null
}

function Get-FrontmatterValue {
    param(
        [Parameter(Mandatory = $true)][string]$Frontmatter,
        [Parameter(Mandatory = $true)][string]$Key
    )

    $pattern = "(?m)^" + [Regex]::Escape($Key) + "\s*:\s*(.+)\s*$"
    $m = [Regex]::Match($Frontmatter, $pattern)
    if (-not $m.Success) {
        return $null
    }

    $value = $m.Groups[1].Value.Trim()
    if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
        return $value.Substring(1, $value.Length - 2)
    }

    return $value
}

$errors = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$skillDirs = Get-ChildItem -Path $SkillsRoot -Directory | Sort-Object Name
if ($skillDirs.Count -eq 0) {
    throw "No skill folders found under $SkillsRoot"
}

foreach ($dir in $skillDirs) {
    $skillMd = Join-Path $dir.FullName "SKILL.md"
    if (-not (Test-Path $skillMd -PathType Leaf)) {
        $errors.Add("[$($dir.Name)] Missing SKILL.md (must be exactly 'SKILL.md').") | Out-Null
        continue
    }

    $readme = Get-ChildItem -Path $dir.FullName -File -Filter "README.md" -ErrorAction SilentlyContinue
    if ($null -ne $readme) {
        $warnings.Add("[$($dir.Name)] README.md found inside skill folder; prefer repo-level README and keep skill folder to SKILL.md + references/.") | Out-Null
    }

    $content = Get-Content -Raw -Path $skillMd
    $front = Get-FrontmatterBlock -Content $content
    if ($null -eq $front) {
        $errors.Add("[$($dir.Name)] Missing YAML frontmatter block (must start with '---' and end with '---').") | Out-Null
        continue
    }

    if ($front.block.Contains("<") -or $front.block.Contains(">")) {
        $errors.Add("[$($dir.Name)] Frontmatter contains angle brackets '<' or '>' which are forbidden.") | Out-Null
    }

    $name = Get-FrontmatterValue -Frontmatter $front.block -Key "name"
    $description = Get-FrontmatterValue -Frontmatter $front.block -Key "description"

    if ([string]::IsNullOrWhiteSpace($name)) {
        $errors.Add("[$($dir.Name)] Frontmatter missing required key: name") | Out-Null
    } else {
        if ($name -ne $dir.Name) {
            $errors.Add("[$($dir.Name)] Frontmatter name '$name' must match folder name '$($dir.Name)'.") | Out-Null
        }
        if ($name -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
            $errors.Add("[$($dir.Name)] Frontmatter name '$name' must be kebab-case (lowercase letters/digits and hyphens).") | Out-Null
        }
    }

    if ([string]::IsNullOrWhiteSpace($description)) {
        $errors.Add("[$($dir.Name)] Frontmatter missing required key: description") | Out-Null
    } else {
        if ($description.Length -gt 1024) {
            $errors.Add("[$($dir.Name)] Description length must be <= 1024 characters (was $($description.Length)).") | Out-Null
        }
        if ($description -notmatch '(?i)\bUse when\b') {
            $warnings.Add("[$($dir.Name)] Description should include 'Use when ...' trigger conditions.") | Out-Null
        }
    }

    $words = @($content -split "\s+" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($words.Count -gt 5000) {
        $warnings.Add("[$($dir.Name)] SKILL.md is > 5000 words ($($words.Count)); consider moving details to references/.") | Out-Null
    }
}

foreach ($w in $warnings) {
    Write-Warning $w
}

if ($errors.Count -gt 0) {
    foreach ($e in $errors) {
        Write-Error $e
    }
    throw "Skill validation failed: $($errors.Count) error(s)."
}

Write-Host ("Skill validation passed: {0} folder(s), {1} warning(s)." -f $skillDirs.Count, $warnings.Count)
