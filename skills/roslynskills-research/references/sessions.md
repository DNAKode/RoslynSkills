# Sessions (Non-Destructive Edits)

Use sessions when you want:

- in-memory edits before writing to disk,
- post-edit diagnostics before commit,
- conflict guards (`expected_generation`, `require_disk_unchanged`).

## Basic Loop

```powershell
roscli session.open src/MyFile.cs demo
roscli session.status demo
roscli session.apply_text_edits --input-stdin
roscli session.get_diagnostics demo
roscli session.diff demo
roscli session.commit demo --keep-session false --require-disk-unchanged true
```

## One-Shot

For small scoped edits, prefer `session.apply_and_commit` to reduce round-trips:

```powershell
roscli session.open src/MyFile.cs demo
roscli session.apply_and_commit --input-stdin
```

## Guardrails

- `session.open` supports `.cs`/`.csx` only. Do not open `.sln`, `.slnx`, `.csproj`.
- Keep `session.*` mutations sequential (no parallel calls).

