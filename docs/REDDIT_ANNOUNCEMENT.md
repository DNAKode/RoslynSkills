# Reddit Announcement Draft (No Images)

## Title

`RoslynSkills - Roslyn-powered C# tools for coding agents`

## Paste-Ready Post (No Images)

I'm working on **RoslynSkills**, an open C#/.NET project for agent-oriented Roslyn tooling:

https://github.com/DNAKode/RoslynSkills

Initial focus is a CLI-first path: **`roscli`** as the main way to invoke Roslyn-powered operations in agent loops.
Alternate entry points (for example MCP) are also available, but CLI ergonomics and reliability are the first target.

The motivation is a tension I keep seeing in practice: semantic tooling feels like it should give agents a major advantage, but text-first workflows are often surprisingly effective in real coding sessions. This project is an attempt to make that tradeoff measurable instead of rhetorical.

RoslynSkills gives coding agents explicit Roslyn command paths for:

- semantic navigation (`nav.*`, `ctx.*`)
- structured edits (`edit.*`)
- diagnostics and repair loops (`diag.*`, `repair.*`)
- file-scoped in-memory sessions (`session.*`, for `.cs`/`.csx` files)

I am also comparing against LSP-based approaches (including C# LSP) in repeatable runs.
LSP is strong for editor-style interaction. RoslynSkills may be stronger in agent trajectories where explicit command contracts and deterministic edit/diagnostic loops matter.

No strong conclusion yet. Mixed outcomes are useful at this stage.

If you want to engage:

- try it in a real repo and share where it helped or got in the way,
- critique the benchmark design,
- compare RoslynSkills vs your LSP-first setup,
- suggest missing commands or better onboarding prompts for agents.

If you try it, even a short note about where it fails or adds friction is very useful.

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

This loop is intentionally minimal:

- find the exact symbol with semantic context,
- apply a scoped rename at a specific anchor,
- immediately check diagnostics before moving on.

---

### Example roscli Fragment (command + response)

Sample responses below are trimmed for brevity.

```text
roscli nav.find_symbol Target.cs Process --brief true --max-results 50
```

```text
{
  "Ok": true,
  "CommandId": "nav.find_symbol",
  "Preview": "nav.find_symbol ok: matches=4",
  "Data": {
    "total_matches": 4,
    "matches": [
      {
        "text": "Process",
        "is_declaration": true,
        "line": 3,
        "column": 17,
        "symbol_kind": "Method",
        "symbol_display": "Overloads.Process(int)"
        ...
      }
    ]
    ...
  }
  ...
}
```

```text
roscli edit.rename_symbol Target.cs 3 17 Handle --apply true --max-diagnostics 50
```

```text
{
  "Ok": true,
  "CommandId": "edit.rename_symbol",
  "Data": {
    "replacement_count": 2,
    "diagnostics_after_edit": {
      "total": 0
      ...
    }
    ...
  }
  ...
}
```

```text
roscli diag.get_file_diagnostics Target.cs
```

```text
{
  "Ok": true,
  "CommandId": "diag.get_file_diagnostics",
  "Preview": "diag.get_file_diagnostics ok: total=0"
  ...
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
