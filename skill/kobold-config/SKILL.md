---
name: kobold-config
description: Configure Kobold theme, language, island, icon and file-search settings; add, remove or group widgets; debug the search index; and inspect app status, config and diagnostics through the local koboldctl.ps1 control CLI.
---

# Kobold Configuration

Use the local `koboldctl.ps1` control client to inspect and change a running
Kobold installation. It talks to the app over a named pipe restricted to the
current Windows user and session. There is no HTTP endpoint and no remote
access.

## When to use

Use this skill when the user asks to:

- Configure Kobold: theme, language, panel opacity, island position or collapse
  delay, icon style, item scale, or file-search roots, types and exclusions.
- Add, remove, rename, recolor, lock or pin widgets; merge widgets into a tab
  group, reorder tabs, detach a tab, or dissolve a group.
- Fix file search: stuck indexing, missing results, a stale index, rebuilding,
  pausing or resuming a scan, or reading the search log.
- Inspect Kobold: whether it is running, the config summary and revision,
  diagnostics, folder panels, or log paths.

Do not use it for changing source code, installing Kobold, or anything outside
the command surface below.

## Locate koboldctl

`koboldctl.ps1` ships with the app and is also kept in the repository. Resolve
it in this order:

1. The installed copy: `%LocalAppData%\Programs\Kobold\koboldctl.ps1`.
2. The repository copy: `tools\koboldctl.ps1`.
3. The current directory's `tools\koboldctl.ps1`.

From this skill's folder, the bundled locator checks those locations and
prints the first existing path:

```powershell
$ctl = & .\scripts\find-koboldctl.ps1
if (-not $ctl) { throw 'koboldctl.ps1 not found.' }
```

Pass `-Path` with a file or a folder to override the search. If script
execution is blocked, run the locator and the client with
`powershell -NoProfile -ExecutionPolicy Bypass -File`.

## Workflow

1. **Connect.** Run `& $ctl status --json`. If it reports `app_not_running`,
   ask the user whether to start Kobold; only then use `--start`, optionally
   with `-Exe` and an explicit executable path.
2. **Read before writing.** Use `config describe --json` for the schema,
   `config get --summary --json` for settings, widgets, groups and the current
   revision, and `diag snapshot --json` for process, island, search, memory and
   log paths. For search issues also read `search status --json` and
   `diag logs --name search --lines 200 --json`.
3. **Diagnose.** Explain the current state before proposing a change. Prefer
   the smallest change that fixes the problem.
4. **Preview.** Settings changes use `config patch --dry-run`. Structural
   changes use the `widget` and `group` commands, whose responses name the
   changed objects. Read-only `--json` output is the preview for diagnostics.
5. **Ask.** Show the exact command, the fields or widgets it affects, and
   whether it deletes data, scans disks, or needs a restart. Wait for explicit
   user approval.
6. **Apply.** Re-run without `--dry-run`; add `--yes` for every destructive
   command. For config writes, pass `--base-revision` with the revision read in
   step 2, or let the CLI read the revision once. If a `config replace` or
   `config restore` result reports `restartRequired` (exit code 3), ask the
   user, then rerun the same command with `--restart` and `--yes`. On
   `conflict`, go back to step 2, re-read the revision, regenerate the patch,
   and ask again.
7. **Verify.** Run `config get --summary --json`, `diag snapshot --json`, and
   for search `search status --json` plus `search query`. Report the new
   revision and what changed.

## Safety rules

- Never edit the live `%AppData%\Kobold\config.json` directly. Every write goes
  through `koboldctl`.
- For `config patch`, `config replace` and `config restore`, always pass
  `--base-revision` (or let the CLI read it once) and never overwrite a
  conflict.
- These commands require explicit user confirmation and `--yes`:
  `widget delete`, `group dissolve`, `config replace`, `config restore`,
  `search rebuild`, and any command run with `--restart`.
- Default to `config get --summary`. `--full` returns note and task content and
  needs explicit consent.
- Do not run arbitrary shell commands or write arbitrary files. Create only the
  temporary JSON patch or config file that `--file` needs.
- Do not paste full configs or logs into public channels; quote only the lines
  needed.
- Ask before starting or restarting Kobold.

## Command examples

Set `$ctl` with the locator before running these examples.

### Theme and language

```json
{ "Theme": "light", "Language": "zh" }
```

Write that object to `$env:TEMP\kobold-patch.json`, then preview and apply with
the revision read immediately before the write:

```powershell
$revision = (& $ctl config get --summary --json | ConvertFrom-Json).result.revision
& $ctl config patch --file "$env:TEMP\kobold-patch.json" --base-revision $revision --dry-run --json
& $ctl config patch --file "$env:TEMP\kobold-patch.json" --base-revision $revision --json
```

Valid values: `Theme` is `dark`, `light` or `high contrast`; `Language` is
`en`, `zh` or `ja`; `IconStyle` is `classic` or `handdrawn`.

### Add a note widget

```powershell
& $ctl widget add --kind note --name "Meeting notes" --color "#788C5D" --json
```

Run `config describe --json` to list the installed widget kind ids. The
response returns the new widget id; use `widget update --id` to rename,
recolor, lock or pin it.

### Merge widgets into a group

```powershell
$widgets = (& $ctl config get --summary --json | ConvertFrom-Json).result.config.Widgets
$target = ($widgets | Where-Object { $_.Kind -eq 'folder' } | Select-Object -First 1).Id
$member = ($widgets | Where-Object { $_.Kind -eq 'note' } | Select-Object -First 1).Id
& $ctl group merge --target $target --member $member --json
```

Add more `--member` values to merge several widgets at once. Use the
`group detach` command with `--group` and `--widget` to split one tab out, and
`group dissolve --yes` only after explicit confirmation.

### Debug file search

```powershell
& $ctl search status --json
& $ctl diag logs --name search --lines 200 --json
& $ctl search query --text "invoice" --limit 20 --json
& $ctl search rebuild --yes
```

`search rebuild` performs a full rescan and requires user confirmation and
`--yes`. `search pause`, `search resume`, `search cancel` and
`search reconcile` do not prompt.

### Backup and restore

Take a snapshot before a risky change, and keep the returned path for a
rollback:

```powershell
$backup = (& $ctl config backup --label before-change --json | ConvertFrom-Json).result.path
```

Restore that snapshot only after explicit confirmation. Restore accepts only
files under `%AppData%\Kobold\backups`:

```powershell
& $ctl config restore --backup $backup --yes --json
```

If the result reports `restartRequired`, ask the user and rerun with
`--restart`:

```powershell
& $ctl config restore --backup $backup --yes --restart --json
```

## Error handling

- `conflict` (exit 1): another change landed. Read
  `details.currentRevision`, re-run `config get --summary --json`, regenerate
  the patch against the current document, and ask the user again. Never retry
  with a forced or stale revision.
- `restart_required` (exit 3): a `config replace` or `config restore` changed
  widget or group structure and wrote nothing. Explain that Kobold must
  restart, ask the user, then rerun the same command with `--restart` and
  `--yes`. The CLI waits up to 20 seconds for the new control pipe.
- `app_not_running` (exit 2): the pipe is missing. Ask the user whether to
  start Kobold, then retry with `--start`; use `-Exe` with an explicit path
  only when the install directory is non-standard.
- `restart_failed` (exit 1): the config was written but the app did not return.
  Do not retry the write. Ask the user to start Kobold, then verify with
  `status --json` and `config get --summary --json`; the pre-write backup is
  kept under `%AppData%\Kobold\backups`.

Other write failures return `validation_failed` or `forbidden`. Fix the request
from the error details; do not bypass the guard.

## References

- `references/config-schema.md` - top-level fields, widget kinds, group fields,
  FileSearch settings, and patch examples.
- `references/troubleshooting.md` - search problems, config recovery,
  widget/group anomalies, theme/language issues, and app startup.
