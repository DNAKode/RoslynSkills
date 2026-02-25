param(
    [string]$InputRoot = "",
    [string]$OutputRoot = "",
    [int]$MaxFiles = 200,
    [switch]$EnableLanguageXml,
    [string[]]$IncludePatterns = @("*.xml", "*.xaml"),
    [string]$XmlCliPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-RepoRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)
    return (Resolve-Path (Join-Path $ScriptPath "..\..")).Path
}

function Ensure-OutputRoot {
    param(
        [string]$RepoRoot,
        [string]$OutputRootInput
    )

    if ([string]::IsNullOrWhiteSpace($OutputRootInput)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        $OutputRootInput = Join-Path $RepoRoot "artifacts\xml-backend-compare\$stamp"
    } elseif (-not [System.IO.Path]::IsPathRooted($OutputRootInput)) {
        $OutputRootInput = Join-Path $RepoRoot $OutputRootInput
    }

    New-Item -ItemType Directory -Path $OutputRootInput -Force | Out-Null
    return (Resolve-Path $OutputRootInput).Path
}

function Resolve-XmlCliPath {
    param(
        [string]$RepoRoot,
        [string]$XmlCliPathInput
    )

    if ([string]::IsNullOrWhiteSpace($XmlCliPathInput)) {
        $defaultCmd = Join-Path $RepoRoot "scripts\xmlcli.cmd"
        if (Test-Path $defaultCmd -PathType Leaf) {
            return $defaultCmd
        }

        $defaultSh = Join-Path $RepoRoot "scripts\xmlcli"
        if (Test-Path $defaultSh -PathType Leaf) {
            return $defaultSh
        }

        throw "Could not resolve xmlcli launcher under scripts/."
    }

    if ([System.IO.Path]::IsPathRooted($XmlCliPathInput)) {
        return $XmlCliPathInput
    }

    return Join-Path $RepoRoot $XmlCliPathInput
}

function Get-CandidateFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Patterns
    )

    $found = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    foreach ($pattern in $Patterns) {
        foreach ($file in Get-ChildItem -Path $Root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue) {
            $found.Add($file) | Out-Null
        }
    }

    return $found
}

function Get-RelativePathCompat {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    try {
        if ([System.IO.Path].GetMethod("GetRelativePath", [Type[]]@([string], [string]))) {
            return [System.IO.Path]::GetRelativePath($BasePath, $TargetPath)
        }
    } catch {
        # Fall back below.
    }

    $baseUri = New-Object System.Uri((Resolve-Path $BasePath).Path.TrimEnd('\') + '\')
    $targetUri = New-Object System.Uri((Resolve-Path $TargetPath).Path)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString().Replace('/', '\'))
}

$repoRoot = Resolve-RepoRoot -ScriptPath $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InputRoot)) {
    $InputRoot = Join-Path $repoRoot "benchmarks\fixtures\xml-backends"
} elseif (-not [System.IO.Path]::IsPathRooted($InputRoot)) {
    $InputRoot = Join-Path $repoRoot $InputRoot
}

if (-not (Test-Path $InputRoot -PathType Container)) {
    throw "Input root '$InputRoot' does not exist."
}

$outputRoot = Ensure-OutputRoot -RepoRoot $repoRoot -OutputRootInput $OutputRoot
$xmlCli = Resolve-XmlCliPath -RepoRoot $repoRoot -XmlCliPathInput $XmlCliPath

$candidateFiles = Get-CandidateFiles -Root $InputRoot -Patterns $IncludePatterns |
    Sort-Object FullName |
    Select-Object -First $MaxFiles

if ($candidateFiles.Count -eq 0) {
    throw "No XML/XAML files found under '$InputRoot' using patterns: $($IncludePatterns -join ', ')."
}

$previousFlag = $env:XMLCLI_ENABLE_LANGUAGE_XML
if ($EnableLanguageXml) {
    $env:XMLCLI_ENABLE_LANGUAGE_XML = "1"
}

$records = New-Object System.Collections.Generic.List[object]
try {
    foreach ($file in $candidateFiles) {
        $raw = & $xmlCli xml.parse_compare $file.FullName 2>&1
        $exitCode = $LASTEXITCODE
        $joined = ($raw | Out-String).Trim()

        $strictSuccess = $false
        $tolerantSuccess = $null
        $divergence = $null
        $strictMs = $null
        $tolerantMs = $null
        $strictElements = $null
        $tolerantElements = $null
        $recordError = $null

        if ($exitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($joined)) {
            try {
                $obj = $joined | ConvertFrom-Json
                $strictSuccess = [bool]$obj.Data.comparison.strict_parse_succeeded
                $tolerantSuccess = if ($null -eq $obj.Data.comparison.tolerant_parse_succeeded) { $null } else { [bool]$obj.Data.comparison.tolerant_parse_succeeded }
                $divergence = if ($null -eq $obj.Data.comparison.divergence_detected) { $null } else { [bool]$obj.Data.comparison.divergence_detected }
                $strictMs = if ($null -eq $obj.Data.strict_backend.duration_ms) { $null } else { [int]$obj.Data.strict_backend.duration_ms }
                $tolerantMs = if ($null -eq $obj.Data.tolerant_backend.duration_ms) { $null } else { [int]$obj.Data.tolerant_backend.duration_ms }
                $strictElements = if ($null -eq $obj.Data.comparison.strict_elements) { $null } else { [int]$obj.Data.comparison.strict_elements }
                $tolerantElements = if ($null -eq $obj.Data.comparison.tolerant_elements) { $null } else { [int]$obj.Data.comparison.tolerant_elements }
            } catch {
                $recordError = "json_parse_failed: $($_.Exception.Message)"
            }
        } else {
            $recordError = if ([string]::IsNullOrWhiteSpace($joined)) { "xmlcli_failed_exit_$exitCode" } else { $joined }
        }

        $records.Add([pscustomobject]@{
            file = $file.FullName
            extension = $file.Extension
            exit_code = $exitCode
            strict_parse_succeeded = $strictSuccess
            tolerant_parse_succeeded = $tolerantSuccess
            divergence_detected = $divergence
            strict_duration_ms = $strictMs
            tolerant_duration_ms = $tolerantMs
            strict_elements = $strictElements
            tolerant_elements = $tolerantElements
            error = $recordError
        }) | Out-Null
    }
} finally {
    if ($null -eq $previousFlag) {
        Remove-Item Env:XMLCLI_ENABLE_LANGUAGE_XML -ErrorAction SilentlyContinue
    } else {
        $env:XMLCLI_ENABLE_LANGUAGE_XML = $previousFlag
    }
}

$recordsPath = Join-Path $outputRoot "xml-parse-compare-records.json"
$records | ConvertTo-Json -Depth 20 | Set-Content -Path $recordsPath

$successRecords = $records | Where-Object { $_.exit_code -eq 0 -and $_.error -eq $null }
$strictSuccessCount = @($successRecords | Where-Object { $_.strict_parse_succeeded }).Count
$tolerantSuccessCount = @($successRecords | Where-Object { $_.tolerant_parse_succeeded -eq $true }).Count
$divergenceCount = @($successRecords | Where-Object { $_.divergence_detected -eq $true }).Count

$summaryPath = Join-Path $outputRoot "xml-parse-compare-summary.md"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# XML Parser Backend Comparison")
$lines.Add("")
$lines.Add("- generated_utc: $((Get-Date).ToUniversalTime().ToString('o'))")
$lines.Add("- input_root: $InputRoot")
$lines.Add("- xmlcli: $xmlCli")
$lines.Add("- records: $recordsPath")
$lines.Add("- files_scanned: $($records.Count)")
$lines.Add("- strict_parse_successes: $strictSuccessCount")
$lines.Add("- tolerant_parse_successes: $tolerantSuccessCount")
$lines.Add("- divergence_count: $divergenceCount")
$lines.Add("- language_xml_enabled_during_run: $($EnableLanguageXml.IsPresent)")
$lines.Add("")
$lines.Add("| File | Ext | Strict OK | Tolerant OK | Diverged | Strict ms | Tolerant ms | Strict elems | Tolerant elems | Error |")
$lines.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |")

foreach ($r in $records) {
    $relative = Get-RelativePathCompat -BasePath $repoRoot -TargetPath $r.file
    $strict = if ($r.strict_parse_succeeded) { "true" } else { "false" }
    $tol = if ($null -eq $r.tolerant_parse_succeeded) { "n/a" } elseif ($r.tolerant_parse_succeeded) { "true" } else { "false" }
    $div = if ($null -eq $r.divergence_detected) { "n/a" } elseif ($r.divergence_detected) { "true" } else { "false" }
    $err = if ([string]::IsNullOrWhiteSpace($r.error)) { "" } else { ($r.error -replace "`r?`n", " " -replace "\|", "\|") }
    $lines.Add("| $relative | $($r.extension) | $strict | $tol | $div | $($r.strict_duration_ms) | $($r.tolerant_duration_ms) | $($r.strict_elements) | $($r.tolerant_elements) | $err |")
}

$lines | Set-Content -Path $summaryPath

Write-Host "OUTPUT_ROOT=$outputRoot"
Write-Host "RECORDS_JSON=$recordsPath"
Write-Host "SUMMARY_MD=$summaryPath"
