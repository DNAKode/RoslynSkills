# DNAKode.RoslynSkills.Cli

`DNAKode.RoslynSkills.Cli` is a .NET global tool for Roslyn-native C# coding operations used by coding agents.

## Install

```bash
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
```

## Command name

```bash
roscli list-commands --ids-only
```

## Typical usage

```bash
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20
roscli diag.get_file_diagnostics src/MyProject/File.cs
```

## Repository

- https://github.com/DNAKode/RoslynSkills
