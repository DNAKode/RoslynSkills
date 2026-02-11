# Reddit Announcement Draft (Short + WIP Tone)

## Title

`RoslynSkills tool -- semantic C#/.NET ops for coding agents (WIP)`

Alternative (even shorter):

`RoslynSkills tool -- semantic C#/.NET workflows for coding agents`

## Post

I have been building **RoslynSkills** as an open experiment in C#/.NET agent tooling:

https://github.com/DNAKode/RoslynSkills

The core question is still unresolved for me:

- intuition says semantic tooling should help agents,
- but text-first editing is often visibly effective in practice.

So this is a work in progress aimed at testing, not declaring victory.

RoslynSkills gives coding agents explicit semantic commands for:

- symbol navigation,
- structured edits,
- diagnostics/repair loops,
- file-scoped in-memory edit sessions.

I am also comparing against LSP-based approaches (including C# LSP) in repeatable runs.  
My current hypothesis is that LSP is excellent for editor-like interaction, while explicit Roslyn command contracts can be stronger for deterministic multi-step agent trajectories.

No big claims yet. Mixed results are expected and useful.

If you want to help:

- try it in a real repo and share where it helped or got in the way,
- critique the benchmark design,
- compare RoslynSkills vs your LSP-first setup,
- suggest missing commands or better onboarding prompts for agents.

I care just as much about negative results as positive ones.

## Verified Roscli Fragments (from run artifacts)

Source transcript:

- `artifacts/skill-intro-ablation/20260210-v1/paired-schema-first/codex-treatment/transcript.jsonl:68`
- `artifacts/skill-intro-ablation/20260210-v1/paired-schema-first/codex-treatment/transcript.jsonl:71`
- `artifacts/skill-intro-ablation/20260210-v1/paired-schema-first/codex-treatment/transcript.jsonl:74`

Fragment 1:

```json
{
  "Ok": true,
  "CommandId": "nav.find_symbol",
  "Preview": "nav.find_symbol ok: matches=4",
  "Summary": "nav.find_symbol ok: matches=4"
}
```

Fragment 2:

```json
{
  "Ok": true,
  "CommandId": "edit.rename_symbol",
  "Data": {
    "old_name": "Process",
    "new_name": "Handle",
    "replacement_count": 2,
    "diagnostics_after_edit": {
      "total": 0
    }
  }
}
```

Fragment 3:

```json
{
  "Ok": true,
  "CommandId": "diag.get_file_diagnostics",
  "Preview": "diag.get_file_diagnostics ok: total=0"
}
```

## Suggested Image Asset

Use this generated overview panel:

- `docs/images/reddit-overview.svg`

