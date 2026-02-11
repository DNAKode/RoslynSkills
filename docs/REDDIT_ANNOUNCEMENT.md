# Reddit Announcement Draft (No Images)

## Title

`RoslynSkills tool -- Roslyn-native C# workflows for coding agents`

## Paste-Ready Post (No Images)

I'm building **RoslynSkills** as an open experiment in C#/.NET agent tooling:

https://github.com/DNAKode/RoslynSkills

Initial focus is a CLI-first path: **`roscli`** as the main way to invoke Roslyn-powered operations in agent loops.
Alternate entry points (for example MCP) are also available, but CLI ergonomics and reliability are the first target.

The question is simple:

- semantic tooling should help,
- but text-first editing is often visibly effective in real sessions.

So this project is explicitly work-in-progress, focused on evidence rather than claims.

RoslynSkills gives coding agents explicit Roslyn command paths for:

- semantic navigation (`nav.*`, `ctx.*`)
- structured edits (`edit.*`)
- diagnostics and repair loops (`diag.*`, `repair.*`)
- file-scoped in-memory sessions (`session.*`, for `.cs`/`.csx` files)

I am also comparing against LSP-based approaches (including C# LSP) in repeatable runs.
LSP is strong for editor-style interaction. RoslynSkills may be stronger in agent trajectories where explicit command contracts and deterministic edit/diagnostic loops matter.

No big conclusion yet. Mixed outcomes are useful at this stage.

If you want to engage:

- try it in a real repo and share where it helped or got in the way,
- critique the benchmark design,
- compare RoslynSkills vs your LSP-first setup,
- suggest missing commands or better onboarding prompts for agents.

Negative results are especially welcome.

---

### Quick Roscli Shape

```text
roscli --version
roscli list-commands --ids-only
roscli quickstart
roscli describe-command session.open
```

Typical loop:

```text
roscli nav.find_symbol src/MyFile.cs Process --brief true --max-results 20
roscli edit.rename_symbol src/MyFile.cs 42 17 Handle --apply true
roscli diag.get_file_diagnostics src/MyFile.cs
```

---

### Real Roscli Fragment (command + response)

```text
roscli run nav.find_symbol --input '{"file_path":"Target.cs","symbol_name":"Process","brief":true,"max_results":50}'
```

```json
{
  "Ok": true,
  "CommandId": "nav.find_symbol",
  "Preview": "nav.find_symbol ok: matches=4"
}
```

```text
roscli run edit.rename_symbol --input '{"file_path":"Target.cs","line":3,"column":17,"new_name":"Handle","apply":true,"max_diagnostics":50}'
```

```json
{
  "Ok": true,
  "CommandId": "edit.rename_symbol",
  "Data": {
    "replacement_count": 2,
    "diagnostics_after_edit": {
      "total": 0
    }
  }
}
```

```text
roscli run diag.get_file_diagnostics --input '{"file_path":"Target.cs"}'
```

```json
{
  "Ok": true,
  "CommandId": "diag.get_file_diagnostics",
  "Preview": "diag.get_file_diagnostics ok: total=0"
}
```

Attribution: **Codex agent on behalf of u/gvrt**.

## Reddit Formatting Notes (What Works)

Based on Reddit Help docs:

- Use desktop **Rich Text** editor for straightforward formatting controls.
- Markdown is also supported for posts/comments.
- Code blocks are supported; fenced blocks (triple backticks) are fine in modern Reddit.
- For widest compatibility (including old clients), 4-space-indented code blocks are the safe fallback.

References:

- https://support.reddithelp.com/hc/en-us/articles/205191185-How-do-I-format-my-comment-or-post-
- https://support.reddithelp.com/hc/en-us/articles/360043033952-Formatting-Guide
