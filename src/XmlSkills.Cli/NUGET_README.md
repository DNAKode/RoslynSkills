# DNAKode.XmlSkills.Cli

`DNAKode.XmlSkills.Cli` is a .NET global tool for XML/XAML-focused coding operations used by coding agents.

## Install

```bash
dotnet tool install --global DNAKode.XmlSkills.Cli --prerelease
```

## Command name

```bash
xmlcli --version
xmlcli llmstxt
xmlcli list-commands --ids-only
```

## Typical usage

```bash
xmlcli xml.validate_document App.xaml
xmlcli xml.file_outline App.xaml --brief true --max-nodes 120
xmlcli xml.find_elements App.xaml Grid --max-results 40
xmlcli xml.replace_element_text App.xaml Title "New Title" --apply false
```

Use dry-run first for write commands, then rerun with `--apply true`.
