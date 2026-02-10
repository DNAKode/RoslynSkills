param(
    [int]$Iterations = 5,
    [string]$OutputDirectory = "",
    [switch]$SkipPublish,
    [int]$TransportResponseTimeoutMs = 30000
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Iterations -lt 1) {
    throw "Iterations must be >= 1."
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
        $map[$property.Name] = $property.Value
    }

    return $map
}

function Invoke-Sample {
    param(
        [Parameter(Mandatory = $true)][string]$ModeName,
        [Parameter(Mandatory = $true)][string]$Executable,
        [AllowEmptyCollection()][string[]]$ModePrefixArgs = @(),
        [Parameter(Mandatory = $true)][string]$CommandId,
        [Parameter(Mandatory = $true)][string[]]$CommandArgs,
        [Parameter(Mandatory = $true)][int]$Iteration,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $allArgs = @($ModePrefixArgs + $CommandArgs)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $output = $null
    $exitCode = 1
    Push-Location $WorkingDirectory
    try {
        $output = & $Executable @allArgs 2>&1
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    } catch {
        $output = $_
        $exitCode = if ($null -eq $LASTEXITCODE) { 1 } else { [int]$LASTEXITCODE }
    } finally {
        Pop-Location
        $sw.Stop()
    }

    $outputText = Convert-ToText $output
    return [pscustomobject]@{
        mode = $ModeName
        command_id = $CommandId
        iteration = $Iteration
        elapsed_ms = [Math]::Round($sw.Elapsed.TotalMilliseconds, 3)
        exit_code = $exitCode
        succeeded = ($exitCode -eq 0)
        request_chars = 0
        output_chars = $outputText.Length
    }
}

function Start-TransportServer {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $escapedArgs = @(
        $Arguments |
        ForEach-Object {
            $arg = [string]$_
            if ($arg -match "[\s`"]") {
                '"' + $arg.Replace('"', '\"') + '"'
            } else {
                $arg
            }
        }
    )
    $startInfo.Arguments = [string]::Join(" ", $escapedArgs)

    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $false
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

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
        # Continue to forced termination path below if graceful shutdown fails.
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

function Invoke-TransportSample {
    param(
        [Parameter(Mandatory = $true)][string]$ModeName,
        [Parameter(Mandatory = $true)][string]$CommandId,
        [Parameter(Mandatory = $true)][object]$RequestPayload,
        [Parameter(Mandatory = $true)][int]$Iteration,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$ServerProcess,
        [Parameter(Mandatory = $true)][int]$ResponseTimeoutMs
    )

    $requestMap = Convert-ToRequestMap -Payload $RequestPayload
    $requestMap.id = "$ModeName-$CommandId-$Iteration"
    $requestJson = $requestMap | ConvertTo-Json -Depth 100 -Compress

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $responseText = ""
    $exitCode = 1
    try {
        if ($ServerProcess.HasExited) {
            throw "Transport server is not running (exit code $($ServerProcess.ExitCode))."
        }

        $ServerProcess.StandardInput.WriteLine($requestJson)
        $ServerProcess.StandardInput.Flush()

        $responseLine = Read-StreamLineWithTimeout -Reader $ServerProcess.StandardOutput -TimeoutMs $ResponseTimeoutMs
        if ($null -eq $responseLine) {
            throw "Transport server output stream closed."
        }

        $responseText = [string]$responseLine
        $response = $responseText | ConvertFrom-Json
        $exitCode = if ($response.ok -eq $true) { 0 } else { 1 }
    } catch {
        $responseText = Convert-ToText $_
        $exitCode = if ($ServerProcess.HasExited) { [int]$ServerProcess.ExitCode } else { 1 }
    } finally {
        $sw.Stop()
    }

    return [pscustomobject]@{
        mode = $ModeName
        command_id = $CommandId
        iteration = $Iteration
        elapsed_ms = [Math]::Round($sw.Elapsed.TotalMilliseconds, 3)
        exit_code = $exitCode
        succeeded = ($exitCode -eq 0)
        request_chars = $requestJson.Length
        output_chars = $responseText.Length
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    $OutputDirectory = Join-Path $repoRoot "artifacts/roscli-invocation-benchmark/$stamp"
}
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$publishDirectory = Join-Path $OutputDirectory "publish"
$publishDllPath = Join-Path $publishDirectory "RoslynSkills.Cli.dll"
$transportPublishDirectory = Join-Path $OutputDirectory "publish-transport"
$transportDllPath = Join-Path $transportPublishDirectory "RoslynSkills.TransportServer.dll"

if (-not $SkipPublish) {
    & dotnet publish src/RoslynSkills.Cli -c Release -o $publishDirectory | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynSkills.Cli."
    }

    & dotnet publish src/RoslynSkills.TransportServer -c Release -o $transportPublishDirectory | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for RoslynSkills.TransportServer."
    }
}

$modes = New-Object System.Collections.Generic.List[object]
$modes.Add([pscustomobject]@{
        mode = "roscli_script"
        invocation_type = "process_per_call"
        executable = (Join-Path $repoRoot "scripts/roscli.cmd")
        prefix_args = @()
    }) | Out-Null
$modes.Add([pscustomobject]@{
        mode = "dotnet_run_project"
        invocation_type = "process_per_call"
        executable = "dotnet"
        prefix_args = @("run", "--project", "src/RoslynSkills.Cli", "--")
    }) | Out-Null

if (Test-Path $publishDllPath -PathType Leaf) {
    $modes.Add([pscustomobject]@{
            mode = "dotnet_published_dll"
            invocation_type = "process_per_call"
            executable = "dotnet"
            prefix_args = @($publishDllPath)
        }) | Out-Null
}

if (Test-Path $transportDllPath -PathType Leaf) {
    $modes.Add([pscustomobject]@{
            mode = "transport_published_server"
            invocation_type = "persistent_transport"
            executable = "dotnet"
            prefix_args = @($transportDllPath)
        }) | Out-Null
}

$commands = @(
    [pscustomobject]@{
        id = "system.ping"
        args = @("system.ping")
        request_payload = @{
            method = "tool/call"
            command_id = "system.ping"
            input = @{}
        }
    },
    [pscustomobject]@{
        id = "cli.list_commands.compact"
        args = @("list-commands", "--compact")
        request_payload = @{
            method = "tool/list"
        }
    },
    [pscustomobject]@{
        id = "diag.get_file_diagnostics.program"
        args = @("diag.get_file_diagnostics", "src/RoslynSkills.Cli/Program.cs")
        request_payload = @{
            method = "tool/call"
            command_id = "diag.get_file_diagnostics"
            input = @{
                file_path = "src/RoslynSkills.Cli/Program.cs"
            }
        }
    }
)

$samples = New-Object System.Collections.Generic.List[object]
foreach ($mode in $modes) {
    $transportProcess = $null
    try {
        if ($mode.invocation_type -eq "persistent_transport") {
            $transportProcess = Start-TransportServer `
                -Executable $mode.executable `
                -Arguments @($mode.prefix_args) `
                -WorkingDirectory $repoRoot
        }

        foreach ($command in $commands) {
            for ($i = 1; $i -le $Iterations; $i++) {
                if ($mode.invocation_type -eq "persistent_transport") {
                    $sample = Invoke-TransportSample `
                        -ModeName $mode.mode `
                        -CommandId $command.id `
                        -RequestPayload $command.request_payload `
                        -Iteration $i `
                        -ServerProcess $transportProcess `
                        -ResponseTimeoutMs $TransportResponseTimeoutMs
                } else {
                    $sample = Invoke-Sample `
                        -ModeName $mode.mode `
                        -Executable $mode.executable `
                        -ModePrefixArgs @($mode.prefix_args) `
                        -CommandId $command.id `
                        -CommandArgs @($command.args) `
                        -Iteration $i `
                        -WorkingDirectory $repoRoot
                }

                $samples.Add($sample) | Out-Null
                Write-Host ("SAMPLE mode={0} command={1} iter={2} elapsed_ms={3} exit={4}" -f $sample.mode, $sample.command_id, $sample.iteration, $sample.elapsed_ms, $sample.exit_code)
            }
        }
    } finally {
        if ($null -ne $transportProcess) {
            Stop-TransportServer -Process $transportProcess -TimeoutMs $TransportResponseTimeoutMs
        }
    }
}

$sampleArray = @($samples.ToArray())
$aggregate = @(
    $sampleArray |
    Group-Object mode, command_id |
    Sort-Object Name |
    ForEach-Object {
        $groupRows = @($_.Group)
        $elapsed = @($groupRows | ForEach-Object { [double]$_.elapsed_ms })
        $requestChars = @($groupRows | ForEach-Object { [double]$_.request_chars })
        $outputChars = @($groupRows | ForEach-Object { [double]$_.output_chars })

        [ordered]@{
            mode = $groupRows[0].mode
            command_id = $groupRows[0].command_id
            sample_count = $groupRows.Count
            success_count = @($groupRows | Where-Object { $_.succeeded }).Count
            elapsed_ms_avg = [Math]::Round(($elapsed | Measure-Object -Average).Average, 3)
            elapsed_ms_p50 = Get-Percentile -Values $elapsed -Percentile 50
            elapsed_ms_p95 = Get-Percentile -Values $elapsed -Percentile 95
            request_chars_avg = [Math]::Round(($requestChars | Measure-Object -Average).Average, 2)
            output_chars_avg = [Math]::Round(($outputChars | Measure-Object -Average).Average, 2)
        }
    }
)

$repoCommit = Convert-ToText (& git -C $repoRoot rev-parse HEAD)
$resolvedPublishDirectory = $null
if (Test-Path -LiteralPath $publishDirectory -PathType Container) {
    $resolvedPublishDirectory = [string](Resolve-Path -LiteralPath $publishDirectory | Select-Object -First 1 -ExpandProperty Path)
}

$resolvedTransportPublishDirectory = $null
if (Test-Path -LiteralPath $transportPublishDirectory -PathType Container) {
    $resolvedTransportPublishDirectory = [string](Resolve-Path -LiteralPath $transportPublishDirectory | Select-Object -First 1 -ExpandProperty Path)
}

$modeArray = @($modes.ToArray())
$commandArray = @($commands)
$report = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("o")
    repo_root = $repoRoot
    repo_commit = $repoCommit
    iterations = $Iterations
    publish_directory = $resolvedPublishDirectory
    transport_publish_directory = $resolvedTransportPublishDirectory
    transport_response_timeout_ms = $TransportResponseTimeoutMs
    modes = $modeArray
    commands = $commandArray
    aggregate = $aggregate
    samples = $sampleArray
}

$jsonPath = Join-Path $OutputDirectory "roscli-invocation-benchmark.json"
$mdPath = Join-Path $OutputDirectory "roscli-invocation-benchmark.md"

$report | ConvertTo-Json -Depth 100 | Set-Content -Path $jsonPath

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Roscli Invocation Benchmark")
$md.Add("")
$md.Add("- Generated UTC: $($report.generated_utc)")
$md.Add("- Repo commit: $($report.repo_commit)")
$md.Add("- Iterations per mode+command: $($report.iterations)")
$md.Add("")
$md.Add("## Aggregate")
$md.Add("")
$md.Add("| Mode | Command | Samples | Success | Avg ms | P50 ms | P95 ms | Avg request chars | Avg output chars |")
$md.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|")
foreach ($row in $report.aggregate) {
    $md.Add("| $($row.mode) | $($row.command_id) | $($row.sample_count) | $($row.success_count) | $($row.elapsed_ms_avg) | $($row.elapsed_ms_p50) | $($row.elapsed_ms_p95) | $($row.request_chars_avg) | $($row.output_chars_avg) |")
}
$md | Set-Content -Path $mdPath

Write-Host ("OUTPUT_DIR={0}" -f ([System.IO.Path]::GetFullPath($OutputDirectory)))
Write-Host ("REPORT_JSON={0}" -f (Resolve-Path $jsonPath).Path)
Write-Host ("REPORT_MD={0}" -f (Resolve-Path $mdPath).Path)

