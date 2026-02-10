# DNAKode.RoslynSkills.Cli

`DNAKode.RoslynSkills.Cli` is a .NET global tool for Roslyn-native C# coding operations used by coding agents.

## Install

```bash
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
```

## Command name

```bash
roscli list-commands --ids-only
roscli describe-command session.open
```

## Typical usage

```bash
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20
roscli diag.get_file_diagnostics src/MyProject/File.cs
roscli edit.create_file src/MyProject/NewType.cs --content "public class NewType { }"
```

`session.open` is for `.cs`/`.csx` files only.

## Repository

- https://github.com/DNAKode/RoslynSkills
