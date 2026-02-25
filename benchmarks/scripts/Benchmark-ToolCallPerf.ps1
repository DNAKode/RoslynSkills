param(
    [int]$Iterations = 5,
    [int]$WarmupIterations = 1,
    [string]$OutputDirectory = "",
    [string]$RoscliPath = "",
    [string]$XmlcliPath = "",
    [string[]]$IncludeCommands = @(),
    [string[]]$ExcludeCommands = @(),
    [switch]$IncludeStaleCheckOnProfiles,
    [switch]$IncludeDotnetRunNoBuildProfiles,
    [switch]$IncludeJitSensitivityProfiles,
    [switch]$IncludeBootstrapCi,
    [int]$BootstrapResamples = 2000,
    [int]$BootstrapSeed = 12345,
    [switch]$IncludeRoscliTransportProfile,
    [string]$RoscliTransportDllPath = "",
    [int]$TransportResponseTimeoutMs = 30000,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Iterations -lt 1) {
    throw "Iterations must be >= 1."
}

if ($WarmupIterations -lt 0) {
    throw "WarmupIterations must be >= 0."
}

if ($BootstrapResamples -lt 100) {
    throw "BootstrapResamples must be >= 100."
}

function Resolve-RepoRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)

    return (Resolve-Path (Join-Path $ScriptPath "..\..")).Path
}

function Resolve-LauncherPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ToolName,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$PathOverride
    )

    if (-not [string]::IsNullOrWhiteSpace($PathOverride)) {
        if ([System.IO.Path]::IsPathRooted($PathOverride)) {
            return $PathOverride
        }

        return Join-Path $RepoRoot $PathOverride
    }

    if ($IsWindows) {
        return Join-Path $RepoRoot ("scripts/{0}.cmd" -f $ToolName)
    }

    return Join-Path $RepoRoot ("scripts/{0}" -f $ToolName)
}

function Ensure-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputDirectoryInput
    )

    $resolved = $OutputDirectoryInput
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        $resolved = Join-Path $RepoRoot ("artifacts/tool-call-perf/{0}" -f $stamp)
    } elseif (-not [System.IO.Path]::IsPathRooted($resolved)) {
        $resolved = Join-Path $RepoRoot $resolved
    }

    New-Item -ItemType Directory -Force -Path $resolved | Out-Null
    return (Resolve-Path $resolved).Path
}

function Convert-ToNullableDouble {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    try {
        return [double]$Value
    } catch {
        return $null
    }
}

function Get-NestedValue {
    param(
        [object]$Object,
        [string[]]$Path
    )

    $cursor = $Object
    foreach ($segment in $Path) {
        if ($null -eq $cursor) {
            return $null
        }

        if ($cursor -is [System.Collections.IDictionary]) {
            if ($cursor.Contains($segment)) {
                $cursor = $cursor[$segment]
                continue
            }

            $matchedKey = $null
            foreach ($key in $cursor.Keys) {
                if ([string]::Equals([string]$key, $segment, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $matchedKey = $key
                    break
                }
            }

            if ($null -eq $matchedKey) {
                return $null
            }

            $cursor = $cursor[$matchedKey]
            continue
        }

        $property = $cursor.PSObject.Properties[$segment]
        if ($null -eq $property) {
            return $null
        }

        $cursor = $property.Value
    }

    return $cursor
}

function Try-ParseEnvelope {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $trimmed = $Text.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return $null
    }

    try {
        return ($trimmed | ConvertFrom-Json -ErrorAction Stop)
    } catch {
        # Continue to fallback parsing.
    }

    $firstBrace = $trimmed.IndexOf('{')
    $lastBrace = $trimmed.LastIndexOf('}')
    if ($firstBrace -ge 0 -and $lastBrace -gt $firstBrace) {
        $slice = $trimmed.Substring($firstBrace, $lastBrace - $firstBrace + 1)
        try {
            return ($slice | ConvertFrom-Json -ErrorAction Stop)
        } catch {
            # Continue to line probe.
        }
    }

    $lines = $trimmed -split "`r?`n"
    for ($i = $lines.Length - 1; $i -ge 0; $i--) {
        $candidate = $lines[$i].Trim()
        if (-not $candidate.StartsWith("{", [StringComparison]::Ordinal) -or -not $candidate.EndsWith("}", [StringComparison]::Ordinal)) {
            continue
        }

        try {
            return ($candidate | ConvertFrom-Json -ErrorAction Stop)
        } catch {
            # Keep scanning.
        }
    }

    return $null
}

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $rank = [int][Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) {
        $rank = 0
    }
    if ($rank -ge $sorted.Count) {
        $rank = $sorted.Count - 1
    }

    return [Math]::Round([double]$sorted[$rank], 3)
}

function Get-NullableAverage {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    return [Math]::Round(($Values | Measure-Object -Average).Average, 3)
}

function Get-SampleStandardDeviation {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -lt 2) {
        return $null
    }

    $mean = ($Values | Measure-Object -Average).Average
    $sumSquaredDiff = 0.0
    foreach ($value in $Values) {
        $delta = ([double]$value - [double]$mean)
        $sumSquaredDiff += ($delta * $delta)
    }

    $variance = $sumSquaredDiff / ($Values.Count - 1)
    return [Math]::Round([Math]::Sqrt($variance), 3)
}

function Get-ConfidenceInterval95 {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -lt 2) {
        return [pscustomobject][ordered]@{
            sample_count = if ($null -eq $Values) { 0 } else { $Values.Count }
            stddev = $null
            stderr = $null
            margin = $null
            low = $null
            high = $null
        }
    }

    $mean = ($Values | Measure-Object -Average).Average
    $stddev = Get-SampleStandardDeviation -Values $Values
    $stderr = if ($null -eq $stddev) { $null } else { ([double]$stddev / [Math]::Sqrt([double]$Values.Count)) }
    $margin = if ($null -eq $stderr) { $null } else { (1.96 * $stderr) }

    return [pscustomobject][ordered]@{
        sample_count = $Values.Count
        stddev = if ($null -eq $stddev) { $null } else { [Math]::Round([double]$stddev, 3) }
        stderr = if ($null -eq $stderr) { $null } else { [Math]::Round([double]$stderr, 3) }
        margin = if ($null -eq $margin) { $null } else { [Math]::Round([double]$margin, 3) }
        low = if ($null -eq $margin) { $null } else { [Math]::Round(([double]$mean - [double]$margin), 3) }
        high = if ($null -eq $margin) { $null } else { [Math]::Round(([double]$mean + [double]$margin), 3) }
    }
}

function Get-BootstrapConfidenceInterval95 {
    param(
        [double[]]$Values,
        [int]$Resamples,
        [System.Random]$Random
    )

    if ($null -eq $Values -or $Values.Count -lt 2) {
        return [pscustomobject][ordered]@{
            sample_count = if ($null -eq $Values) { 0 } else { $Values.Count }
            resamples = $Resamples
            low = $null
            high = $null
        }
    }

    $n = $Values.Count
    $means = New-Object double[] $Resamples
    for ($i = 0; $i -lt $Resamples; $i++) {
        $sum = 0.0
        for ($j = 0; $j -lt $n; $j++) {
            $index = $Random.Next($n)
            $sum += [double]$Values[$index]
        }

        $means[$i] = ($sum / $n)
    }

    $sorted = @($means | Sort-Object)
    $lowIndex = [int][Math]::Floor(0.025 * ($Resamples - 1))
    $highIndex = [int][Math]::Ceiling(0.975 * ($Resamples - 1))
    if ($lowIndex -lt 0) {
        $lowIndex = 0
    }
    if ($highIndex -ge $sorted.Count) {
        $highIndex = $sorted.Count - 1
    }

    return [pscustomobject][ordered]@{
        sample_count = $n
        resamples = $Resamples
        low = [Math]::Round([double]$sorted[$lowIndex], 3)
        high = [Math]::Round([double]$sorted[$highIndex], 3)
    }
}

function Get-BootstrapDeltaRatioConfidenceInterval95 {
    param(
        [double[]]$ProfileValues,
        [double[]]$BaselineValues,
        [int]$Resamples,
        [System.Random]$Random
    )

    if ($null -eq $ProfileValues -or $null -eq $BaselineValues -or
        $ProfileValues.Count -lt 2 -or $BaselineValues.Count -lt 2) {
        return [pscustomobject][ordered]@{
            profile_sample_count = if ($null -eq $ProfileValues) { 0 } else { $ProfileValues.Count }
            baseline_sample_count = if ($null -eq $BaselineValues) { 0 } else { $BaselineValues.Count }
            resamples = $Resamples
            delta_low = $null
            delta_high = $null
            ratio_low = $null
            ratio_high = $null
        }
    }

    $profileN = $ProfileValues.Count
    $baselineN = $BaselineValues.Count
    $deltaSamples = New-Object double[] $Resamples
    $ratioSamples = New-Object double[] $Resamples

    for ($i = 0; $i -lt $Resamples; $i++) {
        $profileSum = 0.0
        for ($j = 0; $j -lt $profileN; $j++) {
            $profileIndex = $Random.Next($profileN)
            $profileSum += [double]$ProfileValues[$profileIndex]
        }

        $baselineSum = 0.0
        for ($k = 0; $k -lt $baselineN; $k++) {
            $baselineIndex = $Random.Next($baselineN)
            $baselineSum += [double]$BaselineValues[$baselineIndex]
        }

        $profileMean = $profileSum / $profileN
        $baselineMean = $baselineSum / $baselineN
        $deltaSamples[$i] = ($profileMean - $baselineMean)
        $ratioSamples[$i] = if ($baselineMean -eq 0.0) { [double]::NaN } else { ($profileMean / $baselineMean) }
    }

    $sortedDelta = @($deltaSamples | Sort-Object)
    $validRatios = @($ratioSamples | Where-Object { -not [double]::IsNaN($_) } | Sort-Object)

    $lowIndex = [int][Math]::Floor(0.025 * ($Resamples - 1))
    $highIndex = [int][Math]::Ceiling(0.975 * ($Resamples - 1))
    if ($lowIndex -lt 0) {
        $lowIndex = 0
    }
    if ($highIndex -ge $sortedDelta.Count) {
        $highIndex = $sortedDelta.Count - 1
    }

    $ratioLow = $null
    $ratioHigh = $null
    if ($validRatios.Count -gt 0) {
        $ratioHighIndex = $highIndex
        if ($ratioHighIndex -ge $validRatios.Count) {
            $ratioHighIndex = $validRatios.Count - 1
        }

        $ratioLowIndex = $lowIndex
        if ($ratioLowIndex -ge $validRatios.Count) {
            $ratioLowIndex = $validRatios.Count - 1
        }

        $ratioLow = [Math]::Round([double]$validRatios[$ratioLowIndex], 3)
        $ratioHigh = [Math]::Round([double]$validRatios[$ratioHighIndex], 3)
    }

    return [pscustomobject][ordered]@{
        profile_sample_count = $profileN
        baseline_sample_count = $baselineN
        resamples = $Resamples
        delta_low = [Math]::Round([double]$sortedDelta[$lowIndex], 3)
        delta_high = [Math]::Round([double]$sortedDelta[$highIndex], 3)
        ratio_low = $ratioLow
        ratio_high = $ratioHigh
    }
}

function Get-MetricValuesForProfileSamples {
    param(
        [Parameter(Mandatory = $true)][object[]]$Samples,
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][string]$CommandId,
        [Parameter(Mandatory = $true)][string]$ProfileId,
        [Parameter(Mandatory = $true)][string]$MetricProperty
    )

    return @(
        $Samples |
        Where-Object {
            [string]::Equals([string]$_.tool, $Tool, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string]$_.command_id, $CommandId, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string]$_.profile_id, $ProfileId, [System.StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            $property = $_.PSObject.Properties[$MetricProperty]
            if ($null -eq $property) {
                return $null
            }

            return Convert-ToNullableDouble $property.Value
        } |
        Where-Object { $null -ne $_ }
    )
}

function Format-CiRange {
    param(
        [object]$Low,
        [object]$High
    )

    $lowDouble = Convert-ToNullableDouble $Low
    $highDouble = Convert-ToNullableDouble $High
    if ($null -eq $lowDouble -or $null -eq $highDouble) {
        return ""
    }

    return ("[{0}, {1}]" -f $lowDouble, $highDouble)
}

function Convert-ToText {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    if ($Value -is [System.Array]) {
        return [string]::Join([Environment]::NewLine, $Value)
    }

    return [string]$Value
}

function Invoke-ExternalProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][hashtable]$EnvironmentOverrides
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $effectiveExecutable = $Executable
    $effectiveArguments = @($Arguments)
    if ($IsWindows -and (
        $Executable.EndsWith(".cmd", [StringComparison]::OrdinalIgnoreCase) -or
        $Executable.EndsWith(".bat", [StringComparison]::OrdinalIgnoreCase))) {
        $effectiveExecutable = "cmd.exe"
        $effectiveArguments = @("/d", "/c", $Executable) + @($Arguments)
    }

    $startInfo.FileName = $effectiveExecutable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    foreach ($arg in $effectiveArguments) {
        $startInfo.ArgumentList.Add([string]$arg)
    }

    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
        $key = [string]$entry.Key
        $value = [string]$entry.Value
        if ([string]::IsNullOrWhiteSpace($value)) {
            [void]$startInfo.Environment.Remove($key)
        } else {
            $startInfo.Environment[$key] = $value
        }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $null = $process.Start()

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $timedOut = $false
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        try {
            $process.Kill($true)
        } catch {
            $process.Kill()
        }
        $process.WaitForExit()
    }
    $stopwatch.Stop()

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = if ($timedOut) { 124 } else { [int]$process.ExitCode }

    return [pscustomobject]@{
        exit_code = $exitCode
        timed_out = $timedOut
        duration_ms = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        stdout = $stdout
        stderr = $stderr
    }
}

function Read-StreamLineWithTimeout {
    param(
        [Parameter(Mandatory = $true)][System.IO.StreamReader]$Reader,
        [Parameter(Mandatory = $true)][int]$TimeoutMs
    )

    $readTask = $Reader.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMs)) {
        throw "Timed out waiting for transport server response after $TimeoutMs ms."
    }

    return $readTask.Result
}

function Start-TransportServer {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][hashtable]$EnvironmentOverrides
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    foreach ($arg in $Arguments) {
        $startInfo.ArgumentList.Add([string]$arg)
    }

    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
        $key = [string]$entry.Key
        $value = [string]$entry.Value
        if ([string]::IsNullOrWhiteSpace($value)) {
            [void]$startInfo.Environment.Remove($key)
        } else {
            $startInfo.Environment[$key] = $value
        }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $null = $process.Start()
    Start-Sleep -Milliseconds 50

    if ($process.HasExited) {
        throw "Transport server exited immediately with code $($process.ExitCode)."
    }

    return $process
}

function Stop-TransportServer {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][int]$TimeoutMs
    )

    if ($Process.HasExited) {
        return
    }

    try {
        $shutdownJson = @{ id = "shutdown"; method = "shutdown" } | ConvertTo-Json -Depth 8 -Compress
        $Process.StandardInput.WriteLine($shutdownJson)
        $Process.StandardInput.Flush()
        $null = Read-StreamLineWithTimeout -Reader $Process.StandardOutput -TimeoutMs $TimeoutMs
    } catch {
        # If graceful shutdown fails, continue to forced termination.
    }

    if (-not $Process.WaitForExit($TimeoutMs)) {
        try {
            $Process.Kill($true)
        } catch {
            $Process.Kill()
        }
        $Process.WaitForExit()
    }
}

function Convert-ToRequestMap {
    param([Parameter(Mandatory = $true)][object]$Payload)

    $map = [ordered]@{}
    if ($Payload -is [System.Collections.IDictionary]) {
        foreach ($key in $Payload.Keys) {
            $map[[string]$key] = $Payload[$key]
        }
        return $map
    }

    foreach ($property in $Payload.PSObject.Properties) {
        $map[[string]$property.Name] = $property.Value
    }

    return $map
}

function Resolve-TransportServerDllPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$PathOverride
    )

    if (-not [string]::IsNullOrWhiteSpace($PathOverride)) {
        $candidate = if ([System.IO.Path]::IsPathRooted($PathOverride)) {
            $PathOverride
        } else {
            Join-Path $RepoRoot $PathOverride
        }

        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Roscli transport server DLL override not found: $candidate"
        }

        return (Resolve-Path -LiteralPath $candidate).Path
    }

    $releaseRoot = Join-Path $RepoRoot "src/RoslynSkills.TransportServer/bin/Release"
    if (Test-Path -LiteralPath $releaseRoot -PathType Container) {
        $existing = @(
            Get-ChildItem -LiteralPath $releaseRoot -Filter "RoslynSkills.TransportServer.dll" -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending
        )

        if ($existing.Count -gt 0) {
            return [string]$existing[0].FullName
        }
    }

    $publishDirectory = Join-Path $OutputRoot "publish-transport"
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
    Write-Host ("PUBLISH transport-server -> {0}" -f $publishDirectory)

    & dotnet publish src/RoslynSkills.TransportServer -c Release -o $publishDirectory | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynSkills.TransportServer."
    }

    $publishedDllPath = Join-Path $publishDirectory "RoslynSkills.TransportServer.dll"
    if (-not (Test-Path -LiteralPath $publishedDllPath -PathType Leaf)) {
        throw "Published RoslynSkills.TransportServer.dll not found at '$publishedDllPath'."
    }

    return (Resolve-Path -LiteralPath $publishedDllPath).Path
}

function Matches-CommandFilterPattern {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][string]$CommandId,
        [AllowNull()][AllowEmptyCollection()][string[]]$Patterns = @()
    )

    $qualified = "{0}:{1}" -f $Tool, $CommandId
    foreach ($rawPattern in $Patterns) {
        if ([string]::IsNullOrWhiteSpace($rawPattern)) {
            continue
        }

        $pattern = $rawPattern.Trim()
        $hasWildcard = $pattern.Contains("*") -or $pattern.Contains("?")
        if ($hasWildcard) {
            if ($CommandId -like $pattern -or $qualified -like $pattern) {
                return $true
            }

            continue
        }

        if ([string]::Equals($CommandId, $pattern, [System.StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($qualified, $pattern, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Select-CommandsForTool {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][pscustomobject[]]$Commands,
        [AllowEmptyCollection()][string[]]$IncludePatterns = @(),
        [AllowEmptyCollection()][string[]]$ExcludePatterns = @()
    )

    $selected = @($Commands)
    if (@($IncludePatterns).Count -gt 0) {
        $selected = @(
            $selected |
            Where-Object {
                Matches-CommandFilterPattern `
                    -Tool $Tool `
                    -CommandId ([string]$_.id) `
                    -Patterns $IncludePatterns
            }
        )
    }

    if (@($ExcludePatterns).Count -gt 0) {
        $selected = @(
            $selected |
            Where-Object {
                -not (Matches-CommandFilterPattern `
                        -Tool $Tool `
                        -CommandId ([string]$_.id) `
                        -Patterns $ExcludePatterns)
            }
        )
    }

    return @($selected)
}

function Normalize-CommandPatterns {
    param([AllowEmptyCollection()][string[]]$Patterns = @())

    $normalized = New-Object System.Collections.Generic.List[string]
    foreach ($raw in $Patterns) {
        if ([string]::IsNullOrWhiteSpace($raw)) {
            continue
        }

        foreach ($token in ($raw -split "[,;`r`n]+")) {
            $candidate = $token.Trim().Trim("'`"")
            if ([string]::IsNullOrWhiteSpace($candidate)) {
                continue
            }

            $normalized.Add($candidate) | Out-Null
        }
    }

    return @($normalized.ToArray())
}

function Get-DeltaValue {
    param(
        [object]$Value,
        [object]$Baseline
    )

    $valueDouble = Convert-ToNullableDouble $Value
    $baselineDouble = Convert-ToNullableDouble $Baseline
    if ($null -eq $valueDouble -or $null -eq $baselineDouble) {
        return $null
    }

    return [Math]::Round(($valueDouble - $baselineDouble), 3)
}

function Get-RatioValue {
    param(
        [object]$Value,
        [object]$Baseline
    )

    $valueDouble = Convert-ToNullableDouble $Value
    $baselineDouble = Convert-ToNullableDouble $Baseline
    if ($null -eq $valueDouble -or $null -eq $baselineDouble -or $baselineDouble -eq 0.0) {
        return $null
    }

    return [Math]::Round(($valueDouble / $baselineDouble), 3)
}

function Get-WorkspaceContextFromEnvelope {
    param([object]$Envelope)

    $direct = Get-NestedValue -Object $Envelope -Path @("Data", "workspace_context")
    if ($null -ne $direct) {
        return $direct
    }

    return (Get-NestedValue -Object $Envelope -Path @("Data", "query", "workspace_context"))
}

function Invoke-ToolSample {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][string]$LauncherPath,
        [Parameter(Mandatory = $true)][hashtable]$Profile,
        [Parameter(Mandatory = $true)][pscustomobject]$Command,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][int]$Iteration,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $invocation = Invoke-ExternalProcess `
        -Executable $LauncherPath `
        -Arguments @($Command.args) `
        -WorkingDirectory $RepoRoot `
        -TimeoutSeconds $TimeoutSeconds `
        -EnvironmentOverrides $Profile.env

    $stdout = [string]$invocation.stdout
    $stderr = [string]$invocation.stderr
    $envelope = Try-ParseEnvelope -Text $stdout
    $workspaceContext = Get-WorkspaceContextFromEnvelope -Envelope $envelope

    $telemetryValidateMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "timing", "validate_ms"))
    $telemetryExecuteMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "timing", "execute_ms"))
    $telemetryTotalMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "timing", "total_ms"))
    $startupOverheadMs = if ($null -eq $telemetryTotalMs) { $null } else { [Math]::Round(([double]$invocation.duration_ms - $telemetryTotalMs), 3) }
    $binaryLaunchMode = Get-NestedValue -Object $envelope -Path @("Telemetry", "cache_context", "binary_launch_mode")
    $commandParseMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "command_telemetry", "timing", "parse_ms"))
    $parseCacheMode = Get-NestedValue -Object $envelope -Path @("Telemetry", "command_telemetry", "cache_context", "parse_cache_mode")
    $parseCacheHit = Get-NestedValue -Object $envelope -Path @("Telemetry", "command_telemetry", "cache_context", "parse_cache_hit")
    $workspaceMode = Get-NestedValue -Object $workspaceContext -Path @("mode")
    $workspaceLoadMs = Convert-ToNullableDouble (Get-NestedValue -Object $workspaceContext -Path @("workspace_load_duration_ms"))
    $msbuildRegistrationMs = Convert-ToNullableDouble (Get-NestedValue -Object $workspaceContext -Path @("msbuild_registration_duration_ms"))

    return [pscustomobject][ordered]@{
        tool = $Tool
        profile_id = [string]$Profile.id
        invocation_mode = "process_per_call"
        command_id = [string]$Command.id
        phase = $Phase
        iteration = $Iteration
        exit_code = [int]$invocation.exit_code
        timed_out = [bool]$invocation.timed_out
        succeeded = ([int]$invocation.exit_code -eq 0)
        wall_ms = [double]$invocation.duration_ms
        stdout_chars = $stdout.Length
        stderr_chars = $stderr.Length
        envelope_parsed = ($null -ne $envelope)
        envelope_ok = if ($null -eq $envelope) { $null } else { [bool]$envelope.Ok }
        envelope_command_id = if ($null -eq $envelope) { $null } else { [string]$envelope.CommandId }
        telemetry_validate_ms = $telemetryValidateMs
        telemetry_execute_ms = $telemetryExecuteMs
        telemetry_total_ms = $telemetryTotalMs
        startup_overhead_ms = $startupOverheadMs
        binary_launch_mode = if ($null -eq $binaryLaunchMode) { $null } else { [string]$binaryLaunchMode }
        command_parse_ms = $commandParseMs
        parse_cache_mode = if ($null -eq $parseCacheMode) { $null } else { [string]$parseCacheMode }
        parse_cache_hit = if ($null -eq $parseCacheHit) { $null } else { [bool]$parseCacheHit }
        workspace_mode = if ($null -eq $workspaceMode) { $null } else { [string]$workspaceMode }
        workspace_load_ms = $workspaceLoadMs
        msbuild_registration_ms = $msbuildRegistrationMs
        request_chars = 0
        stdout_preview = if ($stdout.Length -le 180) { $stdout } else { $stdout.Substring(0, 180) }
        stderr_preview = if ($stderr.Length -le 180) { $stderr } else { $stderr.Substring(0, 180) }
    }
}

function Invoke-TransportToolSample {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][hashtable]$Profile,
        [Parameter(Mandatory = $true)][pscustomobject]$Command,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][int]$Iteration,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$TransportProcess,
        [Parameter(Mandatory = $true)][int]$ResponseTimeoutMs
    )

    if ($null -eq $Command.PSObject.Properties["transport_request"]) {
        throw "Command '$($Command.id)' is missing transport_request payload."
    }

    $requestMap = Convert-ToRequestMap -Payload $Command.transport_request
    $requestMap.id = "{0}-{1}-{2}" -f $Profile.id, $Command.id, $Iteration
    $requestJson = $requestMap | ConvertTo-Json -Depth 100 -Compress

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $responseText = ""
    $stderrText = ""
    $exitCode = 1
    $envelope = $null
    try {
        if ($TransportProcess.HasExited) {
            throw "Transport server is not running (exit code $($TransportProcess.ExitCode))."
        }

        $TransportProcess.StandardInput.WriteLine($requestJson)
        $TransportProcess.StandardInput.Flush()

        $responseLine = Read-StreamLineWithTimeout -Reader $TransportProcess.StandardOutput -TimeoutMs $ResponseTimeoutMs
        if ($null -eq $responseLine) {
            throw "Transport server output stream closed."
        }

        $responseText = [string]$responseLine
        $response = $responseText | ConvertFrom-Json -ErrorAction Stop
        $envelope = Get-NestedValue -Object $response -Path @("envelope")
        $responseOk = Get-NestedValue -Object $response -Path @("ok")
        $exitCode = if ($responseOk -eq $true) { 0 } else { 1 }
    } catch {
        $timedOut = $_.Exception.Message.IndexOf("Timed out waiting for transport server response", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        $stderrText = Convert-ToText $_
        $exitCode = if ($timedOut) { 124 } elseif ($TransportProcess.HasExited) { [int]$TransportProcess.ExitCode } else { 1 }
    } finally {
        $sw.Stop()
    }

    $workspaceContext = Get-WorkspaceContextFromEnvelope -Envelope $envelope
    $telemetryValidateMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "timing", "validate_ms"))
    $telemetryExecuteMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "timing", "execute_ms"))
    $telemetryTotalMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "timing", "total_ms"))
    $startupOverheadMs = if ($null -eq $telemetryTotalMs) { $null } else { [Math]::Round(($sw.Elapsed.TotalMilliseconds - $telemetryTotalMs), 3) }
    $binaryLaunchMode = Get-NestedValue -Object $envelope -Path @("Telemetry", "cache_context", "binary_launch_mode")
    $commandParseMs = Convert-ToNullableDouble (Get-NestedValue -Object $envelope -Path @("Telemetry", "command_telemetry", "timing", "parse_ms"))
    $parseCacheMode = Get-NestedValue -Object $envelope -Path @("Telemetry", "command_telemetry", "cache_context", "parse_cache_mode")
    $parseCacheHit = Get-NestedValue -Object $envelope -Path @("Telemetry", "command_telemetry", "cache_context", "parse_cache_hit")
    $workspaceMode = Get-NestedValue -Object $workspaceContext -Path @("mode")
    $workspaceLoadMs = Convert-ToNullableDouble (Get-NestedValue -Object $workspaceContext -Path @("workspace_load_duration_ms"))
    $msbuildRegistrationMs = Convert-ToNullableDouble (Get-NestedValue -Object $workspaceContext -Path @("msbuild_registration_duration_ms"))

    return [pscustomobject][ordered]@{
        tool = $Tool
        profile_id = [string]$Profile.id
        invocation_mode = "persistent_transport"
        command_id = [string]$Command.id
        phase = $Phase
        iteration = $Iteration
        exit_code = $exitCode
        timed_out = $timedOut
        succeeded = ($exitCode -eq 0)
        wall_ms = [Math]::Round($sw.Elapsed.TotalMilliseconds, 3)
        stdout_chars = $responseText.Length
        stderr_chars = $stderrText.Length
        envelope_parsed = ($null -ne $envelope)
        envelope_ok = if ($null -eq $envelope) { $null } else { [bool]$envelope.Ok }
        envelope_command_id = if ($null -eq $envelope) { $null } else { [string]$envelope.CommandId }
        telemetry_validate_ms = $telemetryValidateMs
        telemetry_execute_ms = $telemetryExecuteMs
        telemetry_total_ms = $telemetryTotalMs
        startup_overhead_ms = $startupOverheadMs
        binary_launch_mode = if ($null -eq $binaryLaunchMode) { $null } else { [string]$binaryLaunchMode }
        command_parse_ms = $commandParseMs
        parse_cache_mode = if ($null -eq $parseCacheMode) { $null } else { [string]$parseCacheMode }
        parse_cache_hit = if ($null -eq $parseCacheHit) { $null } else { [bool]$parseCacheHit }
        workspace_mode = if ($null -eq $workspaceMode) { $null } else { [string]$workspaceMode }
        workspace_load_ms = $workspaceLoadMs
        msbuild_registration_ms = $msbuildRegistrationMs
        request_chars = $requestJson.Length
        stdout_preview = if ($responseText.Length -le 180) { $responseText } else { $responseText.Substring(0, 180) }
        stderr_preview = if ($stderrText.Length -le 180) { $stderrText } else { $stderrText.Substring(0, 180) }
    }
}

function New-ToolProfiles {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][bool]$IncludeStaleOn,
        [Parameter(Mandatory = $true)][bool]$IncludeNoBuild,
        [Parameter(Mandatory = $true)][bool]$IncludeJitSensitivity,
        [Parameter(Mandatory = $true)][bool]$IncludeTransportProfile,
        [AllowEmptyString()][string]$TransportDllPath
    )

    $prefix = if ($Tool -eq "roscli") { "ROSCLI" } else { "XMLCLI" }
    $profiles = New-Object System.Collections.Generic.List[object]

    $profiles.Add([ordered]@{
            id = "dotnet_run"
            invocation_type = "process_per_call"
            prewarm = $false
            env = @{
                "${prefix}_USE_PUBLISHED" = "0"
                "${prefix}_REFRESH_PUBLISHED" = "0"
            }
        }) | Out-Null

    if ($IncludeNoBuild) {
        $profiles.Add([ordered]@{
                id = "dotnet_run_no_build_release"
                invocation_type = "process_per_call"
                prewarm = $true
                env = @{
                    "${prefix}_USE_PUBLISHED" = "0"
                    "${prefix}_REFRESH_PUBLISHED" = "0"
                    "${prefix}_DOTNET_RUN_NO_BUILD" = "1"
                    "${prefix}_DOTNET_RUN_CONFIGURATION" = "Release"
                }
            }) | Out-Null
    }

    $profiles.Add([ordered]@{
            id = "published_cached_stale_off"
            invocation_type = "process_per_call"
            prewarm = $false
            prime_refresh_once = $true
            env = @{
                "${prefix}_USE_PUBLISHED" = "1"
                "${prefix}_REFRESH_PUBLISHED" = "0"
                "${prefix}_STALE_CHECK" = "0"
            }
        }) | Out-Null

    $profiles.Add([ordered]@{
            id = "published_prewarmed_stale_off"
            invocation_type = "process_per_call"
            prewarm = $true
            prime_refresh_once = $true
            env = @{
                "${prefix}_USE_PUBLISHED" = "1"
                "${prefix}_REFRESH_PUBLISHED" = "0"
                "${prefix}_STALE_CHECK" = "0"
            }
        }) | Out-Null

    if ($IncludeJitSensitivity) {
        $jitEnv = @{
            "DOTNET_ReadyToRun" = "0"
            "DOTNET_TieredCompilation" = "0"
            "DOTNET_TC_QuickJit" = "0"
            "DOTNET_TC_QuickJitForLoops" = "0"
        }

        $profiles.Add([ordered]@{
                id = "published_cached_stale_off_jit_forced"
                invocation_type = "process_per_call"
                prewarm = $false
                prime_refresh_once = $true
                env = @{
                    "${prefix}_USE_PUBLISHED" = "1"
                    "${prefix}_REFRESH_PUBLISHED" = "0"
                    "${prefix}_STALE_CHECK" = "0"
                    "DOTNET_ReadyToRun" = $jitEnv["DOTNET_ReadyToRun"]
                    "DOTNET_TieredCompilation" = $jitEnv["DOTNET_TieredCompilation"]
                    "DOTNET_TC_QuickJit" = $jitEnv["DOTNET_TC_QuickJit"]
                    "DOTNET_TC_QuickJitForLoops" = $jitEnv["DOTNET_TC_QuickJitForLoops"]
                }
            }) | Out-Null

        $profiles.Add([ordered]@{
                id = "published_prewarmed_stale_off_jit_forced"
                invocation_type = "process_per_call"
                prewarm = $true
                prime_refresh_once = $true
                env = @{
                    "${prefix}_USE_PUBLISHED" = "1"
                    "${prefix}_REFRESH_PUBLISHED" = "0"
                    "${prefix}_STALE_CHECK" = "0"
                    "DOTNET_ReadyToRun" = $jitEnv["DOTNET_ReadyToRun"]
                    "DOTNET_TieredCompilation" = $jitEnv["DOTNET_TieredCompilation"]
                    "DOTNET_TC_QuickJit" = $jitEnv["DOTNET_TC_QuickJit"]
                    "DOTNET_TC_QuickJitForLoops" = $jitEnv["DOTNET_TC_QuickJitForLoops"]
                }
            }) | Out-Null
    }

    if ($IncludeStaleOn) {
        $profiles.Add([ordered]@{
                id = "published_cached_stale_on"
                invocation_type = "process_per_call"
                prewarm = $false
                prime_refresh_once = $true
                env = @{
                    "${prefix}_USE_PUBLISHED" = "1"
                    "${prefix}_REFRESH_PUBLISHED" = "0"
                    "${prefix}_STALE_CHECK" = "1"
                }
            }) | Out-Null
        $profiles.Add([ordered]@{
                id = "published_prewarmed_stale_on"
                invocation_type = "process_per_call"
                prewarm = $true
                prime_refresh_once = $true
                env = @{
                    "${prefix}_USE_PUBLISHED" = "1"
                    "${prefix}_REFRESH_PUBLISHED" = "0"
                    "${prefix}_STALE_CHECK" = "1"
                }
            }) | Out-Null
    }

    if ($Tool -eq "roscli" -and $IncludeTransportProfile) {
        if ([string]::IsNullOrWhiteSpace($TransportDllPath)) {
            throw "TransportDllPath must be set when IncludeTransportProfile is enabled for roscli."
        }

        $profiles.Add([ordered]@{
                id = "transport_persistent_server"
                invocation_type = "persistent_transport"
                prewarm = $false
                executable = "dotnet"
                prefix_args = @($TransportDllPath)
                env = @{}
            }) | Out-Null

        if ($IncludeJitSensitivity) {
            $profiles.Add([ordered]@{
                    id = "transport_persistent_server_jit_forced"
                    invocation_type = "persistent_transport"
                    prewarm = $false
                    executable = "dotnet"
                    prefix_args = @($TransportDllPath)
                    env = @{
                        "DOTNET_ReadyToRun" = "0"
                        "DOTNET_TieredCompilation" = "0"
                        "DOTNET_TC_QuickJit" = "0"
                        "DOTNET_TC_QuickJitForLoops" = "0"
                    }
                }) | Out-Null
        }
    }

    return @($profiles.ToArray())
}

function Get-SourcePath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        $full = Join-Path $RepoRoot $candidate
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            return $full
        }
    }

    throw "Could not resolve any required path from candidates: $($Candidates -join ', ')"
}

$repoRoot = Resolve-RepoRoot -ScriptPath $PSScriptRoot
$outputRoot = Ensure-OutputDirectory -RepoRoot $repoRoot -OutputDirectoryInput $OutputDirectory
$roscliLauncher = Resolve-LauncherPath -RepoRoot $repoRoot -ToolName "roscli" -PathOverride $RoscliPath
$xmlcliLauncher = Resolve-LauncherPath -RepoRoot $repoRoot -ToolName "xmlcli" -PathOverride $XmlcliPath

if (-not (Test-Path -LiteralPath $roscliLauncher -PathType Leaf)) {
    throw "Roscli launcher not found: $roscliLauncher"
}

if (-not (Test-Path -LiteralPath $xmlcliLauncher -PathType Leaf)) {
    throw "Xmlcli launcher not found: $xmlcliLauncher"
}

$roslynCliSourcePath = Get-SourcePath -RepoRoot $repoRoot -Candidates @(
    "src/RoslynSkills.Cli/CliApplication.cs",
    "src/RoslynAgent.Cli/CliApplication.cs"
)
$workspacePath = "RoslynSkills.slnx"

$xmlValidPath = Get-SourcePath -RepoRoot $repoRoot -Candidates @(
    "benchmarks/fixtures/xml-backends/valid-basic.xml"
)

$xamlValidPath = Get-SourcePath -RepoRoot $repoRoot -Candidates @(
    "benchmarks/fixtures/xml-backends/valid-layout.xaml"
)

$commandsByTool = @{
    roscli = @(
        [pscustomobject]@{
            id = "system.ping"
            args = @("system.ping")
            transport_request = @{
                method = "tool/call"
                command_id = "system.ping"
                input = @{}
            }
        }
        [pscustomobject]@{
            id = "cli.list_commands.ids"
            args = @("list-commands", "--ids-only")
            transport_request = @{
                method = "tool/list"
            }
        }
        [pscustomobject]@{
            id = "nav.find_symbol.cliapp"
            args = @(
                "nav.find_symbol", $roslynCliSourcePath, "HandleRunAsync",
                "--brief", "true",
                "--max-results", "10",
                "--workspace-path", $workspacePath,
                "--require-workspace", "true"
            )
            transport_request = @{
                method = "tool/call"
                command_id = "nav.find_symbol"
                input = @{
                    file_path = $roslynCliSourcePath
                    symbol_name = "HandleRunAsync"
                    brief = $true
                    max_results = 10
                    workspace_path = $workspacePath
                    require_workspace = $true
                }
            }
        }
        [pscustomobject]@{
            id = "diag.get_file_diagnostics.cliapp"
            args = @(
                "diag.get_file_diagnostics", $roslynCliSourcePath,
                "--workspace-path", $workspacePath,
                "--require-workspace", "true"
            )
            transport_request = @{
                method = "tool/call"
                command_id = "diag.get_file_diagnostics"
                input = @{
                    file_path = $roslynCliSourcePath
                    workspace_path = $workspacePath
                    require_workspace = $true
                }
            }
        }
    )
    xmlcli = @(
        [pscustomobject]@{ id = "system.ping"; args = @("system.ping") }
        [pscustomobject]@{ id = "xml.validate_document.valid_basic"; args = @("xml.validate_document", $xmlValidPath) }
        [pscustomobject]@{ id = "xml.file_outline.layout_brief"; args = @("xml.file_outline", $xamlValidPath, "--brief", "true", "--max-nodes", "120") }
        [pscustomobject]@{ id = "xml.parse_compare.layout"; args = @("xml.parse_compare", $xamlValidPath) }
    )
}

$transportDllPath = ""
if ($IncludeRoscliTransportProfile.IsPresent) {
    $transportDllPath = Resolve-TransportServerDllPath `
        -RepoRoot $repoRoot `
        -OutputRoot $outputRoot `
        -PathOverride $RoscliTransportDllPath
    Write-Host ("TRANSPORT_DLL={0}" -f $transportDllPath)
}

$includePatterns = Normalize-CommandPatterns -Patterns $IncludeCommands
$excludePatterns = Normalize-CommandPatterns -Patterns $ExcludeCommands
$selectedCommandsByTool = @{}
foreach ($tool in @("roscli", "xmlcli")) {
    $sourceCommands = @($commandsByTool[$tool])
    $selectedCommands = @(
        Select-CommandsForTool `
            -Tool $tool `
            -Commands $sourceCommands `
            -IncludePatterns $includePatterns `
            -ExcludePatterns $excludePatterns
    )
    $selectedCommandsByTool[$tool] = @($selectedCommands)
    Write-Host ("COMMANDS tool={0} total={1} selected={2}" -f $tool, @($sourceCommands).Count, @($selectedCommands).Count)
}

$selectedCommandTotal = @($selectedCommandsByTool.Values | ForEach-Object { @($_).Count } | Measure-Object -Sum).Sum
if ($selectedCommandTotal -lt 1) {
    throw "No commands selected after IncludeCommands/ExcludeCommands filtering."
}

$profilesByTool = @{
    roscli = New-ToolProfiles `
        -Tool "roscli" `
        -IncludeStaleOn $IncludeStaleCheckOnProfiles.IsPresent `
        -IncludeNoBuild $IncludeDotnetRunNoBuildProfiles.IsPresent `
        -IncludeJitSensitivity $IncludeJitSensitivityProfiles.IsPresent `
        -IncludeTransportProfile $IncludeRoscliTransportProfile.IsPresent `
        -TransportDllPath $transportDllPath
    xmlcli = New-ToolProfiles `
        -Tool "xmlcli" `
        -IncludeStaleOn $IncludeStaleCheckOnProfiles.IsPresent `
        -IncludeNoBuild $IncludeDotnetRunNoBuildProfiles.IsPresent `
        -IncludeJitSensitivity $IncludeJitSensitivityProfiles.IsPresent `
        -IncludeTransportProfile $false `
        -TransportDllPath ""
}

$samples = New-Object System.Collections.Generic.List[object]
foreach ($tool in @("roscli", "xmlcli")) {
    $launcher = if ($tool -eq "roscli") { $roscliLauncher } else { $xmlcliLauncher }
    $toolProfiles = @($profilesByTool[$tool])
    $toolCommands = @($selectedCommandsByTool[$tool])
    if ($toolCommands.Count -eq 0) {
        Write-Host ("SKIP tool={0} reason=no-selected-commands" -f $tool)
        continue
    }

    foreach ($profile in $toolProfiles) {
        $invocationTypeValue = Get-NestedValue -Object $profile -Path @("invocation_type")
        $invocationType = if ([string]::IsNullOrWhiteSpace([string]$invocationTypeValue)) { "process_per_call" } else { [string]$invocationTypeValue }
        Write-Host ("PROFILE tool={0} profile={1} invocation={2}" -f $tool, $profile.id, $invocationType)

        if ($invocationType -eq "persistent_transport") {
            $transportProcess = $null
            try {
                $transportProcess = Start-TransportServer `
                    -Executable ([string]$profile.executable) `
                    -Arguments @($profile.prefix_args) `
                    -WorkingDirectory $repoRoot `
                    -EnvironmentOverrides $profile.env

                foreach ($command in $toolCommands) {
                    for ($warmup = 1; $warmup -le $WarmupIterations; $warmup++) {
                        $warmSample = Invoke-TransportToolSample `
                            -Tool $tool `
                            -Profile $profile `
                            -Command $command `
                            -Phase "warmup" `
                            -Iteration $warmup `
                            -TransportProcess $transportProcess `
                            -ResponseTimeoutMs $TransportResponseTimeoutMs
                        $samples.Add($warmSample) | Out-Null
                    }

                    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
                        $sample = Invoke-TransportToolSample `
                            -Tool $tool `
                            -Profile $profile `
                            -Command $command `
                            -Phase "measure" `
                            -Iteration $iteration `
                            -TransportProcess $transportProcess `
                            -ResponseTimeoutMs $TransportResponseTimeoutMs
                        $samples.Add($sample) | Out-Null
                        Write-Host ("SAMPLE tool={0} profile={1} command={2} phase={3} iter={4} wall_ms={5} exit={6}" -f $sample.tool, $sample.profile_id, $sample.command_id, $sample.phase, $sample.iteration, $sample.wall_ms, $sample.exit_code)
                    }
                }
            } finally {
                if ($null -ne $transportProcess) {
                    Stop-TransportServer -Process $transportProcess -TimeoutMs $TransportResponseTimeoutMs
                }
            }

            continue
        }

        $primeRefreshOnce = [bool](Get-NestedValue -Object $profile -Path @("prime_refresh_once"))

        if ($primeRefreshOnce) {
            $refreshKey = if ($tool -eq "roscli") { "ROSCLI_REFRESH_PUBLISHED" } else { "XMLCLI_REFRESH_PUBLISHED" }
            $primeEnv = @{}
            foreach ($entry in $profile.env.GetEnumerator()) {
                $primeEnv[[string]$entry.Key] = [string]$entry.Value
            }
            $primeEnv[$refreshKey] = "1"
            $primeProfile = [ordered]@{
                id = [string]$profile.id
                env = $primeEnv
            }
            $primeCommand = $toolCommands[0]
            $primeSample = Invoke-ToolSample `
                -Tool $tool `
                -LauncherPath $launcher `
                -Profile $primeProfile `
                -Command $primeCommand `
                -Phase "prime_refresh" `
                -Iteration 0 `
                -RepoRoot $repoRoot `
                -TimeoutSeconds $TimeoutSeconds
            $samples.Add($primeSample) | Out-Null
        }

        if ([bool]$profile.prewarm) {
            $prewarmCommand = $toolCommands[0]
            $prewarmSample = Invoke-ToolSample `
                -Tool $tool `
                -LauncherPath $launcher `
                -Profile $profile `
                -Command $prewarmCommand `
                -Phase "prewarm" `
                -Iteration 0 `
                -RepoRoot $repoRoot `
                -TimeoutSeconds $TimeoutSeconds
            $samples.Add($prewarmSample) | Out-Null
        }

        foreach ($command in $toolCommands) {
            for ($warmup = 1; $warmup -le $WarmupIterations; $warmup++) {
                $warmSample = Invoke-ToolSample `
                    -Tool $tool `
                    -LauncherPath $launcher `
                    -Profile $profile `
                    -Command $command `
                    -Phase "warmup" `
                    -Iteration $warmup `
                    -RepoRoot $repoRoot `
                    -TimeoutSeconds $TimeoutSeconds
                $samples.Add($warmSample) | Out-Null
            }

            for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
                $sample = Invoke-ToolSample `
                    -Tool $tool `
                    -LauncherPath $launcher `
                    -Profile $profile `
                    -Command $command `
                    -Phase "measure" `
                    -Iteration $iteration `
                    -RepoRoot $repoRoot `
                    -TimeoutSeconds $TimeoutSeconds
                $samples.Add($sample) | Out-Null
                Write-Host ("SAMPLE tool={0} profile={1} command={2} phase={3} iter={4} wall_ms={5} exit={6}" -f $sample.tool, $sample.profile_id, $sample.command_id, $sample.phase, $sample.iteration, $sample.wall_ms, $sample.exit_code)
            }
        }
    }
}

$sampleArray = @($samples.ToArray())
$measureSamples = @($sampleArray | Where-Object { $_.phase -eq "measure" })
$bootstrapRandom = if ($IncludeBootstrapCi.IsPresent) { [System.Random]::new($BootstrapSeed) } else { $null }

$aggregate = @(
    $measureSamples |
    Group-Object tool, profile_id, command_id |
    Sort-Object Name |
    ForEach-Object {
        $rows = @($_.Group)
        $wallValues = @($rows | ForEach-Object { [double]$_.wall_ms })
        $firstMeasureValues = @($rows | Where-Object { [int]$_.iteration -eq 1 } | ForEach-Object { [double]$_.wall_ms })
        $steadyMeasureValues = @($rows | Where-Object { [int]$_.iteration -gt 1 } | ForEach-Object { [double]$_.wall_ms })
        $wallCi95 = Get-ConfidenceInterval95 -Values $wallValues
        $firstCi95 = Get-ConfidenceInterval95 -Values $firstMeasureValues
        $steadyCi95 = Get-ConfidenceInterval95 -Values $steadyMeasureValues
        $wallBootstrapCi95 = if ($IncludeBootstrapCi.IsPresent) {
            Get-BootstrapConfidenceInterval95 -Values $wallValues -Resamples $BootstrapResamples -Random $bootstrapRandom
        } else {
            $null
        }
        $steadyBootstrapCi95 = if ($IncludeBootstrapCi.IsPresent) {
            Get-BootstrapConfidenceInterval95 -Values $steadyMeasureValues -Resamples $BootstrapResamples -Random $bootstrapRandom
        } else {
            $null
        }
        $wallAvg = Get-NullableAverage -Values $wallValues
        $firstAvg = Get-NullableAverage -Values $firstMeasureValues
        $steadyAvg = Get-NullableAverage -Values $steadyMeasureValues
        $telemetryTotals = @($rows | ForEach-Object { Convert-ToNullableDouble $_.telemetry_total_ms } | Where-Object { $null -ne $_ })
        $startupOverheads = @($rows | ForEach-Object { Convert-ToNullableDouble $_.startup_overhead_ms } | Where-Object { $null -ne $_ })
        $workspaceLoads = @($rows | ForEach-Object { Convert-ToNullableDouble $_.workspace_load_ms } | Where-Object { $null -ne $_ })
        $msbuildLoads = @($rows | ForEach-Object { Convert-ToNullableDouble $_.msbuild_registration_ms } | Where-Object { $null -ne $_ })
        $parseLoads = @($rows | ForEach-Object { Convert-ToNullableDouble $_.command_parse_ms } | Where-Object { $null -ne $_ })
        $requestSizes = @($rows | ForEach-Object { Convert-ToNullableDouble $_.request_chars } | Where-Object { $null -ne $_ })
        $parseHits = @($rows | Where-Object { $_.parse_cache_hit -eq $true }).Count
        $parseHitRate = if ($rows.Count -eq 0) { $null } else { [Math]::Round(($parseHits / $rows.Count), 3) }

        [pscustomobject][ordered]@{
            tool = [string]$rows[0].tool
            profile_id = [string]$rows[0].profile_id
            command_id = [string]$rows[0].command_id
            sample_count = $rows.Count
            success_count = @($rows | Where-Object { $_.succeeded }).Count
            wall_ms_avg = $wallAvg
            wall_ms_stddev = $wallCi95.stddev
            wall_ms_stderr = $wallCi95.stderr
            wall_ms_ci95_low = $wallCi95.low
            wall_ms_ci95_high = $wallCi95.high
            wall_ms_bootstrap_ci95_low = if ($null -eq $wallBootstrapCi95) { $null } else { $wallBootstrapCi95.low }
            wall_ms_bootstrap_ci95_high = if ($null -eq $wallBootstrapCi95) { $null } else { $wallBootstrapCi95.high }
            first_measure_wall_ms = $firstAvg
            first_measure_wall_ms_ci95_low = $firstCi95.low
            first_measure_wall_ms_ci95_high = $firstCi95.high
            steady_wall_ms_avg = $steadyAvg
            steady_wall_ms_stddev = $steadyCi95.stddev
            steady_wall_ms_stderr = $steadyCi95.stderr
            steady_wall_ms_ci95_low = $steadyCi95.low
            steady_wall_ms_ci95_high = $steadyCi95.high
            steady_wall_ms_bootstrap_ci95_low = if ($null -eq $steadyBootstrapCi95) { $null } else { $steadyBootstrapCi95.low }
            steady_wall_ms_bootstrap_ci95_high = if ($null -eq $steadyBootstrapCi95) { $null } else { $steadyBootstrapCi95.high }
            steady_sample_count = @($steadyMeasureValues).Count
            steady_vs_first_wall_ms_delta = Get-DeltaValue -Value $steadyAvg -Baseline $firstAvg
            steady_vs_first_wall_ms_ratio = Get-RatioValue -Value $steadyAvg -Baseline $firstAvg
            wall_ms_p50 = Get-Percentile -Values $wallValues -Percentile 50
            wall_ms_p95 = Get-Percentile -Values $wallValues -Percentile 95
            telemetry_total_ms_avg = Get-NullableAverage -Values $telemetryTotals
            startup_overhead_ms_avg = Get-NullableAverage -Values $startupOverheads
            workspace_load_ms_avg = Get-NullableAverage -Values $workspaceLoads
            msbuild_registration_ms_avg = Get-NullableAverage -Values $msbuildLoads
            command_parse_ms_avg = Get-NullableAverage -Values $parseLoads
            request_chars_avg = Get-NullableAverage -Values $requestSizes
            parse_cache_hit_rate = $parseHitRate
            invocation_modes = @($rows | ForEach-Object { $_.invocation_mode } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) -join ","
            binary_launch_mode = @($rows | ForEach-Object { $_.binary_launch_mode } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) -join ","
            workspace_modes = @($rows | ForEach-Object { $_.workspace_mode } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) -join ","
            parse_cache_modes = @($rows | ForEach-Object { $_.parse_cache_mode } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) -join ","
        }
    }
)

$profileSummary = @(
    $aggregate |
    Group-Object tool, profile_id |
    Sort-Object Name |
    ForEach-Object {
        $rows = @($_.Group)
        [pscustomobject][ordered]@{
            tool = [string]$rows[0].tool
            profile_id = [string]$rows[0].profile_id
            command_count = $rows.Count
            total_samples = @($rows | ForEach-Object { [int]$_.sample_count } | Measure-Object -Sum).Sum
            total_successes = @($rows | ForEach-Object { [int]$_.success_count } | Measure-Object -Sum).Sum
            wall_ms_avg_across_commands = Get-NullableAverage -Values @($rows | ForEach-Object { Convert-ToNullableDouble $_.wall_ms_avg } | Where-Object { $null -ne $_ })
            startup_overhead_ms_avg_across_commands = Get-NullableAverage -Values @($rows | ForEach-Object { Convert-ToNullableDouble $_.startup_overhead_ms_avg } | Where-Object { $null -ne $_ })
            telemetry_total_ms_avg_across_commands = Get-NullableAverage -Values @($rows | ForEach-Object { Convert-ToNullableDouble $_.telemetry_total_ms_avg } | Where-Object { $null -ne $_ })
        }
    }
)

$bestProfiles = @(
    $aggregate |
    Group-Object tool, command_id |
    Sort-Object Name |
    ForEach-Object {
        $rows = @($_.Group | Where-Object { $_.success_count -eq $_.sample_count })
        if ($rows.Count -eq 0) {
            return
        }

        $best = $rows | Sort-Object wall_ms_avg | Select-Object -First 1
        [pscustomobject][ordered]@{
            tool = [string]$best.tool
            command_id = [string]$best.command_id
            best_profile_id = [string]$best.profile_id
            best_wall_ms_avg = Convert-ToNullableDouble $best.wall_ms_avg
            best_startup_overhead_ms_avg = Convert-ToNullableDouble $best.startup_overhead_ms_avg
        }
    }
)

$coldWarmSummary = @(
    $aggregate |
    Sort-Object tool, command_id, profile_id |
    ForEach-Object {
        $first = Convert-ToNullableDouble $_.first_measure_wall_ms
        $steady = Convert-ToNullableDouble $_.steady_wall_ms_avg

        [pscustomobject][ordered]@{
            tool = [string]$_.tool
            command_id = [string]$_.command_id
            profile_id = [string]$_.profile_id
            invocation_modes = [string]$_.invocation_modes
            first_measure_wall_ms = $first
            steady_wall_ms_avg = $steady
            steady_sample_count = [int]$_.steady_sample_count
            first_minus_steady_wall_ms = Get-DeltaValue -Value $first -Baseline $steady
            first_over_steady_wall_ratio = Get-RatioValue -Value $first -Baseline $steady
            steady_wall_ms_ci95_low = Convert-ToNullableDouble $_.steady_wall_ms_ci95_low
            steady_wall_ms_ci95_high = Convert-ToNullableDouble $_.steady_wall_ms_ci95_high
            steady_wall_ms_bootstrap_ci95_low = Convert-ToNullableDouble $_.steady_wall_ms_bootstrap_ci95_low
            steady_wall_ms_bootstrap_ci95_high = Convert-ToNullableDouble $_.steady_wall_ms_bootstrap_ci95_high
        }
    }
)

$baselineDeltas = @(
    $aggregate |
    Group-Object tool, command_id |
    Sort-Object Name |
    ForEach-Object {
        $rows = @($_.Group)
        $baseline = @(
            $rows |
            Where-Object { $_.profile_id -eq "dotnet_run" -and $_.success_count -eq $_.sample_count } |
            Select-Object -First 1
        )
        if ($baseline.Count -eq 0) {
            return
        }

        $baselineWallSamples = Get-MetricValuesForProfileSamples `
            -Samples $measureSamples `
            -Tool ([string]$rows[0].tool) `
            -CommandId ([string]$rows[0].command_id) `
            -ProfileId "dotnet_run" `
            -MetricProperty "wall_ms"
        $baselineStartupSamples = Get-MetricValuesForProfileSamples `
            -Samples $measureSamples `
            -Tool ([string]$rows[0].tool) `
            -CommandId ([string]$rows[0].command_id) `
            -ProfileId "dotnet_run" `
            -MetricProperty "startup_overhead_ms"
        $baselineTelemetrySamples = Get-MetricValuesForProfileSamples `
            -Samples $measureSamples `
            -Tool ([string]$rows[0].tool) `
            -CommandId ([string]$rows[0].command_id) `
            -ProfileId "dotnet_run" `
            -MetricProperty "telemetry_total_ms"

        $baselineWall = Convert-ToNullableDouble $baseline[0].wall_ms_avg
        $baselineStartup = Convert-ToNullableDouble $baseline[0].startup_overhead_ms_avg
        $baselineTelemetry = Convert-ToNullableDouble $baseline[0].telemetry_total_ms_avg

        foreach ($row in $rows) {
            if ([string]::Equals([string]$row.profile_id, "dotnet_run", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $wall = Convert-ToNullableDouble $row.wall_ms_avg
            $startup = Convert-ToNullableDouble $row.startup_overhead_ms_avg
            $telemetry = Convert-ToNullableDouble $row.telemetry_total_ms_avg
            $profileWallSamples = Get-MetricValuesForProfileSamples `
                -Samples $measureSamples `
                -Tool ([string]$row.tool) `
                -CommandId ([string]$row.command_id) `
                -ProfileId ([string]$row.profile_id) `
                -MetricProperty "wall_ms"
            $profileStartupSamples = Get-MetricValuesForProfileSamples `
                -Samples $measureSamples `
                -Tool ([string]$row.tool) `
                -CommandId ([string]$row.command_id) `
                -ProfileId ([string]$row.profile_id) `
                -MetricProperty "startup_overhead_ms"
            $profileTelemetrySamples = Get-MetricValuesForProfileSamples `
                -Samples $measureSamples `
                -Tool ([string]$row.tool) `
                -CommandId ([string]$row.command_id) `
                -ProfileId ([string]$row.profile_id) `
                -MetricProperty "telemetry_total_ms"

            $wallBootstrap = if ($IncludeBootstrapCi.IsPresent) {
                Get-BootstrapDeltaRatioConfidenceInterval95 `
                    -ProfileValues $profileWallSamples `
                    -BaselineValues $baselineWallSamples `
                    -Resamples $BootstrapResamples `
                    -Random $bootstrapRandom
            } else {
                $null
            }
            $startupBootstrap = if ($IncludeBootstrapCi.IsPresent) {
                Get-BootstrapDeltaRatioConfidenceInterval95 `
                    -ProfileValues $profileStartupSamples `
                    -BaselineValues $baselineStartupSamples `
                    -Resamples $BootstrapResamples `
                    -Random $bootstrapRandom
            } else {
                $null
            }
            $telemetryBootstrap = if ($IncludeBootstrapCi.IsPresent) {
                Get-BootstrapDeltaRatioConfidenceInterval95 `
                    -ProfileValues $profileTelemetrySamples `
                    -BaselineValues $baselineTelemetrySamples `
                    -Resamples $BootstrapResamples `
                    -Random $bootstrapRandom
            } else {
                $null
            }

            [pscustomobject][ordered]@{
                tool = [string]$row.tool
                command_id = [string]$row.command_id
                profile_id = [string]$row.profile_id
                baseline_profile_id = "dotnet_run"
                baseline_wall_ms_avg = $baselineWall
                wall_ms_avg = $wall
                wall_ms_delta = Get-DeltaValue -Value $wall -Baseline $baselineWall
                wall_ms_ratio = Get-RatioValue -Value $wall -Baseline $baselineWall
                wall_ms_delta_bootstrap_ci95_low = if ($null -eq $wallBootstrap) { $null } else { $wallBootstrap.delta_low }
                wall_ms_delta_bootstrap_ci95_high = if ($null -eq $wallBootstrap) { $null } else { $wallBootstrap.delta_high }
                wall_ms_ratio_bootstrap_ci95_low = if ($null -eq $wallBootstrap) { $null } else { $wallBootstrap.ratio_low }
                wall_ms_ratio_bootstrap_ci95_high = if ($null -eq $wallBootstrap) { $null } else { $wallBootstrap.ratio_high }
                baseline_startup_overhead_ms_avg = $baselineStartup
                startup_overhead_ms_avg = $startup
                startup_overhead_ms_delta = Get-DeltaValue -Value $startup -Baseline $baselineStartup
                startup_overhead_ms_ratio = Get-RatioValue -Value $startup -Baseline $baselineStartup
                startup_overhead_ms_delta_bootstrap_ci95_low = if ($null -eq $startupBootstrap) { $null } else { $startupBootstrap.delta_low }
                startup_overhead_ms_delta_bootstrap_ci95_high = if ($null -eq $startupBootstrap) { $null } else { $startupBootstrap.delta_high }
                startup_overhead_ms_ratio_bootstrap_ci95_low = if ($null -eq $startupBootstrap) { $null } else { $startupBootstrap.ratio_low }
                startup_overhead_ms_ratio_bootstrap_ci95_high = if ($null -eq $startupBootstrap) { $null } else { $startupBootstrap.ratio_high }
                baseline_telemetry_total_ms_avg = $baselineTelemetry
                telemetry_total_ms_avg = $telemetry
                telemetry_total_ms_delta = Get-DeltaValue -Value $telemetry -Baseline $baselineTelemetry
                telemetry_total_ms_ratio = Get-RatioValue -Value $telemetry -Baseline $baselineTelemetry
                telemetry_total_ms_delta_bootstrap_ci95_low = if ($null -eq $telemetryBootstrap) { $null } else { $telemetryBootstrap.delta_low }
                telemetry_total_ms_delta_bootstrap_ci95_high = if ($null -eq $telemetryBootstrap) { $null } else { $telemetryBootstrap.delta_high }
                telemetry_total_ms_ratio_bootstrap_ci95_low = if ($null -eq $telemetryBootstrap) { $null } else { $telemetryBootstrap.ratio_low }
                telemetry_total_ms_ratio_bootstrap_ci95_high = if ($null -eq $telemetryBootstrap) { $null } else { $telemetryBootstrap.ratio_high }
            }
        }
    }
)

$repoCommit = (& git -C $repoRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0) {
    $repoCommit = ""
}

$report = [ordered]@{
    schema_version = "1.0"
    confidence_method = if ($IncludeBootstrapCi.IsPresent) { "normal_approx_95+bootstrap_percentile_95" } else { "normal_approx_95" }
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    repo_root = $repoRoot
    repo_commit = [string]$repoCommit
    iterations = $Iterations
    warmup_iterations = $WarmupIterations
    timeout_seconds = $TimeoutSeconds
    include_commands = $includePatterns
    exclude_commands = $excludePatterns
    include_stale_check_on_profiles = $IncludeStaleCheckOnProfiles.IsPresent
    include_dotnet_run_no_build_profiles = $IncludeDotnetRunNoBuildProfiles.IsPresent
    include_jit_sensitivity_profiles = $IncludeJitSensitivityProfiles.IsPresent
    include_bootstrap_ci = $IncludeBootstrapCi.IsPresent
    bootstrap_resamples = $BootstrapResamples
    bootstrap_seed = $BootstrapSeed
    include_roscli_transport_profile = $IncludeRoscliTransportProfile.IsPresent
    roscli_transport_dll_path = $transportDllPath
    transport_response_timeout_ms = $TransportResponseTimeoutMs
    launchers = [ordered]@{
        roscli = $roscliLauncher
        xmlcli = $xmlcliLauncher
    }
    commands = $selectedCommandsByTool
    command_catalog = $commandsByTool
    profiles_by_tool = $profilesByTool
    samples = $sampleArray
    aggregate = $aggregate
    cold_warm_summary = $coldWarmSummary
    profile_summary = $profileSummary
    best_profiles = $bestProfiles
    baseline_deltas = $baselineDeltas
}

$jsonPath = Join-Path $outputRoot "tool-call-perf.json"
$markdownPath = Join-Path $outputRoot "tool-call-perf.md"
$report | ConvertTo-Json -Depth 100 | Set-Content -Path $jsonPath

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Tool Call Performance Benchmark")
$md.Add("")
$md.Add("- schema_version: $($report.schema_version)")
$md.Add("- confidence_method: $($report.confidence_method)")
$md.Add("- generated_utc: $($report.generated_utc)")
$md.Add("- repo_commit: $($report.repo_commit)")
$md.Add("- iterations: $($report.iterations)")
$md.Add("- warmup_iterations: $($report.warmup_iterations)")
$md.Add("- include_commands: $([string]::Join(', ', @($report.include_commands)))")
$md.Add("- exclude_commands: $([string]::Join(', ', @($report.exclude_commands)))")
$md.Add("- include_stale_check_on_profiles: $($report.include_stale_check_on_profiles)")
$md.Add("- include_dotnet_run_no_build_profiles: $($report.include_dotnet_run_no_build_profiles)")
$md.Add("- include_jit_sensitivity_profiles: $($report.include_jit_sensitivity_profiles)")
$md.Add("- include_bootstrap_ci: $($report.include_bootstrap_ci)")
$md.Add("- bootstrap_resamples: $($report.bootstrap_resamples)")
$md.Add("- include_roscli_transport_profile: $($report.include_roscli_transport_profile)")
$md.Add("- roscli_transport_dll_path: $($report.roscli_transport_dll_path)")
$md.Add("")
$md.Add("## Aggregate")
$md.Add("")
$md.Add("| Tool | Profile | Command | Samples | Success | Invocation | Wall avg (ms) | Wall CI95 (ms) | Wall Bootstrap CI95 (ms) | First measure (ms) | Steady avg (ms) | Steady/First ratio | Wall p50 (ms) | Wall p95 (ms) | Telemetry total avg (ms) | Startup overhead avg (ms) | Avg request chars | Binary mode | Workspace mode(s) | Parse cache mode(s) |")
$md.Add("|---|---|---|---:|---:|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|")
foreach ($row in $aggregate) {
    $wallCiRange = Format-CiRange -Low $row.wall_ms_ci95_low -High $row.wall_ms_ci95_high
    $wallBootstrapCiRange = Format-CiRange -Low $row.wall_ms_bootstrap_ci95_low -High $row.wall_ms_bootstrap_ci95_high
    $md.Add("| $($row.tool) | $($row.profile_id) | $($row.command_id) | $($row.sample_count) | $($row.success_count) | $($row.invocation_modes) | $($row.wall_ms_avg) | $wallCiRange | $wallBootstrapCiRange | $($row.first_measure_wall_ms) | $($row.steady_wall_ms_avg) | $($row.steady_vs_first_wall_ms_ratio) | $($row.wall_ms_p50) | $($row.wall_ms_p95) | $($row.telemetry_total_ms_avg) | $($row.startup_overhead_ms_avg) | $($row.request_chars_avg) | $($row.binary_launch_mode) | $($row.workspace_modes) | $($row.parse_cache_modes) |")
}

$md.Add("")
$md.Add("## Cold vs Steady Summary")
$md.Add("")
$md.Add("| Tool | Profile | Command | Invocation | First (ms) | Steady avg (ms) | First-Steady (ms) | First/Steady ratio | Steady CI95 (ms) | Steady Bootstrap CI95 (ms) | Steady samples |")
$md.Add("|---|---|---|---|---:|---:|---:|---:|---|---|---:|")
foreach ($row in $coldWarmSummary) {
    $steadyCiRange = Format-CiRange -Low $row.steady_wall_ms_ci95_low -High $row.steady_wall_ms_ci95_high
    $steadyBootstrapCiRange = Format-CiRange -Low $row.steady_wall_ms_bootstrap_ci95_low -High $row.steady_wall_ms_bootstrap_ci95_high
    $md.Add("| $($row.tool) | $($row.profile_id) | $($row.command_id) | $($row.invocation_modes) | $($row.first_measure_wall_ms) | $($row.steady_wall_ms_avg) | $($row.first_minus_steady_wall_ms) | $($row.first_over_steady_wall_ratio) | $steadyCiRange | $steadyBootstrapCiRange | $($row.steady_sample_count) |")
}

$md.Add("")
$md.Add("## Best Profile Per Command")
$md.Add("")
$md.Add("| Tool | Command | Best profile | Best wall avg (ms) | Startup overhead avg (ms) |")
$md.Add("|---|---|---|---:|---:|")
foreach ($row in $bestProfiles) {
    $md.Add("| $($row.tool) | $($row.command_id) | $($row.best_profile_id) | $($row.best_wall_ms_avg) | $($row.best_startup_overhead_ms_avg) |")
}

$md.Add("")
$md.Add("## Baseline Deltas (vs dotnet_run)")
$md.Add("")
$md.Add("| Tool | Command | Profile | Wall avg (ms) | Delta (ms) | Ratio | Wall Ratio Bootstrap CI95 | Startup delta (ms) | Startup ratio | Startup Ratio Bootstrap CI95 | Telemetry delta (ms) | Telemetry ratio | Telemetry Ratio Bootstrap CI95 |")
$md.Add("|---|---|---|---:|---:|---:|---|---:|---:|---|---:|---:|---|")
foreach ($row in $baselineDeltas) {
    $wallRatioBootstrapCiRange = Format-CiRange -Low $row.wall_ms_ratio_bootstrap_ci95_low -High $row.wall_ms_ratio_bootstrap_ci95_high
    $startupRatioBootstrapCiRange = Format-CiRange -Low $row.startup_overhead_ms_ratio_bootstrap_ci95_low -High $row.startup_overhead_ms_ratio_bootstrap_ci95_high
    $telemetryRatioBootstrapCiRange = Format-CiRange -Low $row.telemetry_total_ms_ratio_bootstrap_ci95_low -High $row.telemetry_total_ms_ratio_bootstrap_ci95_high
    $md.Add("| $($row.tool) | $($row.command_id) | $($row.profile_id) | $($row.wall_ms_avg) | $($row.wall_ms_delta) | $($row.wall_ms_ratio) | $wallRatioBootstrapCiRange | $($row.startup_overhead_ms_delta) | $($row.startup_overhead_ms_ratio) | $startupRatioBootstrapCiRange | $($row.telemetry_total_ms_delta) | $($row.telemetry_total_ms_ratio) | $telemetryRatioBootstrapCiRange |")
}

$md | Set-Content -Path $markdownPath

Write-Host ("OUTPUT_DIR={0}" -f $outputRoot)
Write-Host ("REPORT_JSON={0}" -f $jsonPath)
Write-Host ("REPORT_MD={0}" -f $markdownPath)
