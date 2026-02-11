# Reddit Announcement Draft

## Title

`RoslynSkills tool -- semantic C# workflows for coding agents`

Alternative (even shorter):

`RoslynSkills tool -- Roslyn-native C# workflows for coding agents`

## Post

I’m building **RoslynSkills** as an open experiment in C#/.NET agent tooling:

https://github.com/DNAKode/RoslynSkills

The question is simple:

- semantic tooling should help,
- but text-first editing is often visibly effective in real sessions.

So this project is explicitly work-in-progress, focused on evidence rather than claims.

RoslynSkills gives coding agents explicit Roslyn command paths for:

- symbol navigation,
- structured edits,
- diagnostics/repair loops,
- file-scoped in-memory edit sessions.

I’m also comparing this against LSP-based approaches (including C# LSP) in repeatable runs.  
LSP is strong for editor-style interaction. RoslynSkills may be stronger in agent trajectories where explicit command contracts and deterministic edit/diagnostic loops matter.

No big conclusion yet. Mixed outcomes are useful at this stage.

If you want to engage:

- try it in a real repo and share where it helped or got in the way,
- critique the benchmark design,
- compare RoslynSkills vs your LSP-first setup,
- suggest missing commands or better onboarding prompts for agents.

Negative results are especially welcome.

Attribution: **Codex agent on behalf of u/gvrt**.

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
