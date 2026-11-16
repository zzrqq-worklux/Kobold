# Kobold — development notes

Everything a contributor needs that is deliberately kept out of the user
manual: build, tests, packaging, data locations, the local agent control
plane, and upgrade/compatibility notes.

> User manual: [README](../README.md) · Full feature reference: [FEATURES](FEATURES.md)

## Local agent control / kobold-config skill

`koboldctl.ps1` ships in the Kobold install directory (by default
`%LocalAppData%\Programs\Kobold`) and is also available as
`tools\koboldctl.ps1` in the repository. It talks to a running Kobold over a
named pipe restricted to the current Windows user and session - there is no
HTTP endpoint and no remote access. The command surface covers `status`,
`config get/describe/validate/patch/replace/backup/restore`,
`widget add/update/delete`, `group merge/detach/reorder/dissolve`,
`search status/query/rebuild/cancel/pause/resume/reconcile`, and
`diag snapshot/logs/folders`. Config writes are revision-guarded, and
destructive commands require `--yes`.

`skill\kobold-config` is a portable skill folder. Copy the whole
`kobold-config` directory into your agent's skills directory, then use
`scripts/find-koboldctl.ps1` to locate the CLI. It works with the installed
copy or with the repository checkout.

## Build & test

Requires the .NET SDK (8.x) on Windows.

```powershell
dotnet build Kobold.csproj -c Release
# -> bin\Release\net48\Kobold.exe
```

Run the console self-checks through one entry point - it runs every selected
check, prints a summary and exits non-zero when anything fails:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -List   # show the matrix
```

`checks` = every console self-check, `ci` = the subset that runs on GitHub
runners (the window/desktop-heavy checks stay local), `installer` = the
installer self-check (logic only, temp folders, no registry writes) and
`all` = checks + installer.

The matrix on a normal interactive Windows desktop:

| Command | Checks |
|---|---|
| `Run-Checks.ps1 -Group ci` | **28** (the GitHub Actions subset) |
| `Run-Checks.ps1 -Group checks` | **36** (full local console matrix) |
| `dotnet run --project tests\GroupUiSpike -- --check` | **15** interactive UI checks |
| `Run-Checks.ps1 -Group installer` | installer logic only |

`GroupUiSpike` needs a real desktop session, so it is run separately from the
console matrix. `FileSearchCheck` covers the SQLite index, scanner, watcher and
service; `XamlLoadCheck` loads every XAML surface.

The Release output is flat: `Kobold.exe` plus 11 runtime files, including the
SQLite native runtime (`e_sqlite3.dll`, `Microsoft.Data.Sqlite.dll`,
`SQLitePCLRaw.*`) and the `System.Buffers` / `System.Memory` /
`System.Numerics.Vectors` / `System.Runtime.CompilerServices.Unsafe` support
assemblies. `tools\package.ps1` and the installer copy that same list.

A GitHub Actions workflow runs the `ci` group on every push whose HEAD commit
mentions `[ci]` (or on manual dispatch), keeping runner usage deliberate.

End-to-end install/uninstall is verified by a one-off manual drill before a
release (it writes a real uninstall entry and a shortcut).

Package a release zip; the version defaults to the one in `Kobold.csproj`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1
```

Release notes come from `CHANGELOG.md`, and a missing version section is a hard
error - a release cannot go out without changelog entries:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-notes.ps1 -Version 1.2.0
# -> prints the notes, or write them with -OutFile notes.md
```

## Data

- Config: `%AppData%\Kobold\config.json` (auto-backup as `config.json.backup`).
  A config file that could not be read is first kept aside as
  `config.json.failed-<timestamp>` before defaults are used.
- Widgets are stored in the new `Widgets` format, which older releases cannot
  read: falling back to v1.3.0 or v1.3.1 resets you to default settings but
  keeps the original file aside as `config.json.failed-<timestamp>`. Releases
  older than v1.3.0 do not have that safety net. An unknown kind is kept, not
  dropped: v1.4.0 reads a config with a note in it as an unknown kind and
  hands it back unchanged, so the note is still there on the way back.
- Each widget's **panel size is saved with the widget**, so panels come back
  the size you left them. On upgrade the old per-widget columns / max-rows
  settings are converted **once** into an initial panel size (they stay in the
  config file but are no longer used); **item size** keeps working.
- Stored files: `%AppData%\Kobold\Storage`
- Folder icons: `%AppData%\Kobold\FolderIcons`
- Explicit config backups: `%AppData%\Kobold\backups` (the control plane keeps
  the newest 20)
- Search index: `%LocalAppData%\Kobold\search-index.db` (metadata only —
  file names and locations, never contents). Safe to delete; it rebuilds.
- Logs: `%LocalAppData%\Kobold\logs\app.log` and `search.log` (1 MB rotation).

## Config safety

Never edit the live `%AppData%\Kobold\config.json` while Kobold is running: the
app owns the document, validates it, and can overwrite a hand edit on its next
save. Use the settings window, or the revision-guarded `koboldctl.ps1` control
plane (`config patch` for non-structural fields, `widget.*` / `group.*` for
widget structure). A structurally incompatible `config replace` or
`config restore` reports `restartRequired` and writes nothing until it is rerun
with `--restart`.

Source layout: `Core/` (models, config, localization, design tokens,
theme manager), `Controls/` (island + folder widget windows),
`Windows/` (settings), `Helpers/` (dialogs, menus, animation, cursors),
`Themes/` (XAML control styles), `Services/` (tray, shell integration,
terminal, memory trim), `tests/` (console checks).
