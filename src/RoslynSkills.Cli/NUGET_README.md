# DNAKode.RoslynSkills.Cli

`DNAKode.RoslynSkills.Cli` is a .NET global tool for Roslyn-native C# coding operations used by coding agents.

## Install

```bash
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease
```

If your machine has extra/private feeds configured:

```bash
dotnet tool install --global DNAKode.RoslynSkills.Cli --prerelease --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
```

If a fresh preview is still indexing, install by explicit version:

```bash
dotnet tool install --global DNAKode.RoslynSkills.Cli --version <version> --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
```

## Command name

```bash
roscli list-commands --ids-only
roscli quickstart
roscli describe-command session.open
```

Recommended first minute:

1. `roscli list-commands --ids-only`
2. `roscli quickstart`
3. `roscli describe-command session.open`
4. `roscli describe-command edit.create_file`

## Typical usage

```bash
roscli nav.find_symbol src/MyProject/File.cs MySymbol --brief true --max-results 20
roscli diag.get_file_diagnostics src/MyProject/File.cs
roscli edit.create_file src/MyProject/NewType.cs --content "public class NewType { }"
```

`session.open` is for `.cs`/`.csx` files only.

## Repository

- https://github.com/DNAKode/RoslynSkills
- Pit-of-success guide: https://github.com/DNAKode/RoslynSkills/blob/main/docs/PIT_OF_SUCCESS.md
