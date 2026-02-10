# RoslynAgent.Cli

`RoslynAgent.Cli` is a .NET global tool for Roslyn-native C# coding operations used by coding agents.

## Install

```bash
dotnet tool install --global RoslynAgent.Cli --prerelease
```

## Command name

```bash
roslyn-agent list-commands --ids-only
```

## Typical usage

```bash
roslyn-agent nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20
roslyn-agent diag.get_file_diagnostics src/MyProject/File.cs
```

## Repository

- https://github.com/DNAKode/RoslynSkill
