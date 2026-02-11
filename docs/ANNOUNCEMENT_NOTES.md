# Announcement Notes (Style + Structure)

Date: 2026-02-11

These notes are based on the referenced `dotnet-inspect` community announcement and are intended for future RoslynSkills announcements.

## Style Observations That Worked

1. Start with a clear analogy and concrete problem.
- Example pattern: "`X` for .NET resources, similar to `docker inspect` / `kubectl describe`."

2. Keep the intro short and action-oriented.
- state what the tool does in one sentence.
- state why it helps agentic workflows in one sentence.

3. Give one "try this now" command.
- keep it copy-pasteable.
- no setup-heavy preamble in the post body.

4. Show realistic prompt/output snippets.
- include 2-3 concrete agent questions.
- include screenshots or short transcript excerpts proving utility.

5. End with a direct invitation.
- ask for bug reports and feature requests.
- reduce friction for feedback.

## Suggested RoslynSkills Post Skeleton

1. Title:
- one-line value statement + "LLM/agent friendly" framing.

2. Opening:
- what RoslynSkills is.
- what pain it removes (wrong symbol edits, ambiguous overloads, delayed compile feedback).

3. Quick start:
- `dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease`
- one `roscli` command example.

4. "Show, don't tell" snippets:
- snippet 1: `nav.find_symbol` to disambiguate symbols.
- snippet 2: `edit.rename_symbol` (or `edit.create_file`) + immediate diagnostics.
- snippet 3: optional session flow (`session.open` + `session.apply_and_commit`) for non-destructive edits.

5. Positioning:
- RoslynSkills complements package/assembly inspection tools.
- RoslynSkills focuses on workspace-native semantic edits/diagnostics.

6. CTA:
- invite users to try on one C# task and share transcript excerpts + failures.

## Snippet Hygiene Rules

- use exact commands that work in default shells.
- avoid shell-fragile JSON quoting in the post.
- prefer direct shorthand commands over complex payloads in public examples.
- include expected outcome in one line after each snippet.
