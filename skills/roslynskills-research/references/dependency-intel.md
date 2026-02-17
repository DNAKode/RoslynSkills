# Dependency / Package Intelligence (Optional)

When the task is primarily about external package APIs (overloads, version diffs, vulnerability metadata), combine RoslynSkills with `dotnet-inspect` if available:

```powershell
dnx dotnet-inspect -y -- api JsonSerializer --package System.Text.Json
```

Selection hints:

- external package API shape / overloads / version diffs: `dotnet-inspect`
- in-repo symbol targeting / edits / diagnostics: `roscli`
- dependency-driven local changes: use both (inspect first, edit second)

