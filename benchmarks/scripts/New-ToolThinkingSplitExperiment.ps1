param(
    [string]$OutputRoot = "",
    [ValidateSet("claude", "codex", "gemini")][string]$Agent = "claude",
    [string]$TaskLabel = "medium-repo-task",
    [string]$BackgroundFile = "",
    [string]$TaskPromptFile = "",
    [ValidateSet("standard", "tight")][string]$TreatmentGuidanceProfile = "tight"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\\..")).Path
}

function Resolve-OutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Agent,
        [Parameter(Mandatory = $true)][string]$TaskLabel
    )

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
        $safeTask = ($TaskLabel -replace "[^a-zA-Z0-9\\-]+", "-").Trim("-").ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($safeTask)) {
            $safeTask = "task"
        }

        return (Join-Path $RepoRoot "artifacts/tool-thinking-split/$stamp-$Agent-$safeTask")
    }

    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        return $OutputRoot
    }

    return (Join-Path $RepoRoot $OutputRoot)
}

function Resolve-OptionalPath {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$PathValue
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return ""
    }

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return (Resolve-Path $PathValue).Path
    }

    return (Resolve-Path (Join-Path (Get-Location) $PathValue)).Path
}

function Read-OptionalContent {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return ""
    }

    return (Get-Content -Raw -Path $PathValue)
}

$repoRoot = Get-RepoRoot
$outputDirectory = Resolve-OutputDirectory -RepoRoot $repoRoot -OutputRoot $OutputRoot -Agent $Agent -TaskLabel $TaskLabel
$backgroundPath = Resolve-OptionalPath -PathValue $BackgroundFile
$taskPromptPath = Resolve-OptionalPath -PathValue $TaskPromptFile
$backgroundText = Read-OptionalContent -PathValue $backgroundPath
$taskPromptText = Read-OptionalContent -PathValue $taskPromptPath

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$controlPromptPath = Join-Path $outputDirectory "prompt.control.txt"
$treatmentPromptPath = Join-Path $outputDirectory "prompt.treatment.txt"
$manifestPath = Join-Path $outputDirectory "manifest.json"
$checklistPath = Join-Path $outputDirectory "checklist.md"

$backgroundBlock = if ([string]::IsNullOrWhiteSpace($backgroundText)) {
    "<background intentionally omitted>"
} else {
    $backgroundText.TrimEnd()
}

$taskBlock = if ([string]::IsNullOrWhiteSpace($taskPromptText)) {
    "<task prompt intentionally omitted>"
} else {
    $taskPromptText.TrimEnd()
}

$controlPrompt = @"
You are running the CONTROL lane for a split transcript experiment.

Rules:
- Do NOT use RoslynSkills (`roscli`, `roslyn://`, or RoslynSkills MCP tools).
- Work only with generic editor/shell capabilities.
- Explain your reasoning in normal task-focused style.

Repository background (high-level only):
$backgroundBlock

Task:
$taskBlock
"@

if ($TreatmentGuidanceProfile -eq "tight") {
    $treatmentRules = @"
Rules:
- Use RoslynSkills where semantically relevant.
- Keep pre-edit Roslyn calls tight (target: <=2 before first edit).
- Skip `describe-command` unless a Roslyn command fails or argument shape is genuinely unclear.
- Prefer one direct semantic lookup (`nav.find_symbol` or `ctx.search_text`) before editing.
- Keep payloads brief (`--brief true` where available).
"@
} else {
    $treatmentRules = @"
Rules:
- Use RoslynSkills where semantically relevant.
- Prefer pit-of-success flow:
  1) list-commands --ids-only
  2) describe-command for uncertain args
  3) run semantic nav/ctx first, then edits, then diagnostics
- Keep command payloads tight (`--brief true` where available).
"@
}

$treatmentPrompt = @"
You are running the TREATMENT lane for a split transcript experiment.

$treatmentRules

Repository background (high-level only):
$backgroundBlock

Task:
$taskBlock
"@

Set-Content -Path $controlPromptPath -Value $controlPrompt -NoNewline
Set-Content -Path $treatmentPromptPath -Value $treatmentPrompt -NoNewline

$manifest = [ordered]@{
    schema_version = "1.0"
    generated_utc = [DateTimeOffset]::UtcNow.ToString("o")
    experiment_type = "tool-thinking-split"
    agent = $Agent
    task_label = $TaskLabel
    treatment_guidance_profile = $TreatmentGuidanceProfile
    lanes = @(
        [ordered]@{
            lane_id = "control"
            prompt_path = $controlPromptPath
            transcript_path = (Join-Path $outputDirectory "control.transcript.jsonl")
            tool_policy = "text_only"
        },
        [ordered]@{
            lane_id = "treatment"
            prompt_path = $treatmentPromptPath
            transcript_path = (Join-Path $outputDirectory "treatment.transcript.jsonl")
            tool_policy = "roslyn_enabled"
        }
    )
    hypothesis = [ordered]@{
        context_overhead_possible = $true
        expected_treatment_advantage = "higher semantic targeting quality after initial overhead"
    }
    analysis_outputs = [ordered]@{
        pair_metrics_json = (Join-Path $outputDirectory "thinking-metrics.json")
        summary_markdown = (Join-Path $outputDirectory "thinking-summary.md")
    }
}

$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -NoNewline

$checklist = @"
# Tool-Thinking Split Experiment Checklist

1. Start from the same repo commit for both lanes.
2. Provide only high-level background (no deep file excerpts) before the task.
3. Run lane `control` with no RoslynSkills usage.
4. Run lane `treatment` with Roslyn-first guidance.
5. Save raw transcripts to:
   - control: `control.transcript.jsonl`
   - treatment: `treatment.transcript.jsonl`
6. Analyze with:
   `pwsh benchmarks/scripts/Analyze-ToolThinkingSplit.ps1 -ControlTranscript <...> -TreatmentTranscript <...> -OutputJson thinking-metrics.json -OutputMarkdown thinking-summary.md`
7. Record findings in `RESEARCH_FINDINGS.md` and any tooling gaps in `ROSLYN_FALLBACK_REFLECTION_LOG.md`.
"@

Set-Content -Path $checklistPath -Value $checklist -NoNewline

Write-Output "Created tool-thinking split experiment scaffold at: $outputDirectory"
