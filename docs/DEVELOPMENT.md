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

## Testing an update in the VM

The updater requests the pinned manifest,
`https://api.merrickzq.work/downloads/kobold/latest.json`.
`KOBOLD_UPDATE_MANIFEST_URL` replaces that address when it holds an absolute
URL the app accepts: an `https` URL on any host, or an `http` URL on the
loopback interface. Anything else (a relative path, plain `http` elsewhere,
another scheme) is ignored and the pinned address is used. The variable is read
when a check runs and is inherited only by a process started after it is set,
so set it in the shell and start Kobold from that same shell. The manifest's
own download `url` stays pinned to the `api.merrickzq.work` host (the parser
enforces it), so the override only moves the check; a fully local download
needs that hostname mapped to the test server.

```powershell
# 1. Stage a release next to a scratch install. For an app-driven update the
#    package's Kobold.exe must report the manifest version as its FileVersion
#    (UpdateDownloader checks it); apply-update.ps1 itself never reads it, so
#    the -DryRun below only needs the file to exist. The rest of the package
#    (tools\ included) can be staged too.
$root = Join-Path $env:TEMP 'Kobold-apply-test'
$stage = Join-Path $root 'stage'
$install = Join-Path $root 'install'
New-Item -ItemType Directory -Force $stage, $install | Out-Null
Copy-Item C:\work\bin\Debug\net48\Kobold.exe $stage
Copy-Item C:\work\bin\Debug\net48\Kobold.exe $install   # the copy being replaced

# 2. Exercise the swap without waiting for a process or starting an exe: the
#    backup and the copy land in a temporary folder, the install folder is only
#    read. Run it after every edit to the script.
powershell -NoProfile -ExecutionPolicy Bypass -File C:\work\tools\apply-update.ps1 `
    -ProcessId 0 -StageDir $stage -InstallDir $install -DryRun
# -> "dry run: success", exit code 0

# 3. Fake manifest on loopback. Turn "Install updates automatically on exit"
#    off in Settings first, otherwise the offer starts downloading from the
#    pinned host. Set the variable, THEN start Kobold from this same shell: a
#    running process never sees a later environment change. If an automatic
#    check ran within the last 24 hours, use "Check for updates" in Settings
#    instead — a manual check always runs.
$manifest = '{"version":"2.0.2","url":"https://api.merrickzq.work/downloads/kobold/Kobold-v2.0.2.zip","size":12345678,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}'
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add('http://127.0.0.1:8765/')
$listener.Start()
$env:KOBOLD_UPDATE_MANIFEST_URL = 'http://127.0.0.1:8765/latest.json'
Start-Process 'C:\work\bin\Debug\net48\Kobold.exe'
while ($true) {
    $ctx = $listener.GetContext()
    $body = [Text.Encoding]::UTF8.GetBytes($manifest)
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.OutputStream.Write($body, 0, $body.Length)
    $ctx.Response.Close()
}
# The first automatic check runs 30 seconds after launch: the Updates card
# reads "Version 2.0.2 is available" and the tray menu shows the entry.
# Ctrl+C stops the listener when you are done.
```

`tests\UpdateCheck` has further manifest and downloader examples. The manifest
`url` must be `https://api.merrickzq.work/...`, and a real package must match
its size and SHA-256.

**Rollback.** Run the script without `-DryRun` against a scratch copy of the
install folder and a stage whose `Kobold.exe` cannot start (a text file renamed
to `Kobold.exe` is enough): `-ProcessId 0` skips the wait, the copy is backed
up, the launch fails, the backup is restored, the old exe starts again and the
script exits 3. `%LocalAppData%\Kobold\update.log` records every step; a
successful run removes its own backup and keeps only the newest
`.update-backup-*` folder. The force-kill path can be exercised the same way:
start a copy of another executable renamed `Kobold.exe` that ignores the wait,
pass its PID with `-WaitSeconds 1`, and check the log: the
`icons: asked … to run --restore-desktop-icons` line comes before
`stopping it`.

`Run-Checks.ps1 -Group checks` runs `tests\UpdateCheck` with the rest of the
console matrix (36 checks); the helper script itself is covered by the manual
VM pass above.

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
