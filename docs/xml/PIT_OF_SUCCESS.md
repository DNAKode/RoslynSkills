# XmlSkills Pit Of Success

This is the recommended startup path for `xmlcli` agent workflows.

## First Minute

1. `xmlcli llmstxt`
2. `xmlcli list-commands --ids-only`
3. `xmlcli xml.validate_document <file-path>`
4. `xmlcli xml.file_outline <file-path> --brief true --max-nodes 120`
5. `xmlcli xml.find_elements <file-path> <element-name> --max-results 40`
6. `xmlcli xml.parse_compare <file-path>`

## Write Workflow

1. Run read-only validation and discovery commands first.
2. Run replace command in dry-run mode:
`xmlcli xml.replace_element_text <file> <element> <new-text> --apply false`
3. Review `changes_preview` and `replaced_count`.
4. Re-run with `--apply true`.
5. Re-run `xmlcli xml.validate_document <file-path>`.

## Guardrails

- Keep responses bounded with `--max-results` and `--max-nodes`.
- Prefer element-name targeted edits over broad text replacement.
- `xml.replace_element_text` edits leaf elements only.
- For XAML, combine xml structure edits with Roslyn/compile validation when code-behind semantics are involved.
- Experimental backend toggle:
`$env:XMLCLI_ENABLE_LANGUAGE_XML = "1"` then use `--backend language_xml` on read commands.

## Research Harness

Run backend comparison over fixture packs:

```powershell
powershell -ExecutionPolicy Bypass -File benchmarks/scripts/Run-XmlParserBackendComparison.ps1 -EnableLanguageXml
```

Default fixture pack:

- `benchmarks/fixtures/xml-backends/`

## Current Scope

- XML and XAML syntax-level operations (well-formedness, structure, targeted element text replacement).
- Not a replacement for Roslyn semantic analysis over generated/compiled C#.
