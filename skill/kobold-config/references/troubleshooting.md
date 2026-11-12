# Troubleshooting Kobold

Start with read-only checks. Change nothing until the state is understood and
the user agrees. Every command below is `koboldctl.ps1`; never edit the live
`%AppData%\Kobold\config.json`.

## File search is stuck or slow

Read the live state and the search log:

```powershell
& $ctl search status --json
& $ctl diag logs --name search --lines 200 --json
```

`state` is one of `Unconfigured`, `NoIndex`, `Ready`, `Indexing`,
`Reconciling`, `Paused` or `Error`. Check `message`, `filesIndexed`,
`directoriesScanned`, `skippedDirectories`, `incrementalAvailable`,
`scanCancelled` and `needsRebuild`.

- `Unconfigured`: `FileSearch` is absent or setup never completed. Configure
  roots and types with `config patch`, then rebuild.
- `NoIndex`: settings exist but no published index. Run
  `search rebuild --yes` after confirmation.
- `Indexing` with a slowly rising `filesIndexed`: a first full scan is running.
  Large or network roots and a broad `Types` list make it slower. Let it finish,
  or narrow the roots/exclusions and rebuild.
- `Paused`: run `search resume`.
- `Error`: read `message` and the log tail. Common causes are an unreadable
  root, a removed drive, or a locked index database. Fix the root first.
- `needsRebuild: true`: the settings fingerprint no longer matches the
  published index. Rebuild is required for results to reflect the new settings.
- `incrementalAvailable: false`: at least one root has no live watcher, so
  changes are only picked up by reconcile. On a local fixed drive this usually
  means the root is unavailable or the watcher limit was hit.

Useful recovery commands:

```powershell
& $ctl search cancel
& $ctl search pause
& $ctl search resume
& $ctl search reconcile
& $ctl search rebuild --yes
```

`search cancel` stops the current scan; `search reconcile` queues a background
diff without discarding the index. `search rebuild` discards and rescans the
index; it is destructive and needs explicit confirmation and `--yes`.

## File search returns no results

Check the service and a direct query:

```powershell
& $ctl search status --json
& $ctl search query --text "invoice" --limit 20 --json
& $ctl config get --summary --json
```

- `totalMatches: 0` with `state: Ready`: the query may be too narrow, or the
  file type is not indexed. Check `FileSearch.Types`, `IndexAllFiles`,
  `IndexFolders` and `IndexExtensionless`.
- `index.hasIndex: false` or `needsRebuild: true`: rebuild after confirmation.
- Empty or wrong `Roots`: empty roots mean every fixed NTFS drive. If the
  expected file lives on a removable/network drive, add that path explicitly.
- `ExcludeDirectories` may be hiding the folder. A bare name matches at any
  depth; a full path matches its subtree.
- `filesIndexed` is metadata count, not a content index. Kobold never indexes
  file contents, so searches match names, not text inside documents.

If a query works but the UI shows nothing, the widget may be bound to a stale
service state. Read `diag snapshot --json` for `search.state` and compare it
with the query result; ask the user before restarting the app.

## Config validation or corruption

Read the config summary and validate a candidate document before writing:

```powershell
& $ctl config get --summary --json
& $ctl config validate --file "$env:AppData\Kobold\config.json" --json
```

The app never silently overwrites an unreadable file. It first keeps an
evidence copy as `config.json.failed-<timestamp>` next to the live config, then
tries `config.json.backup`, then falls back to defaults. A `.failed-*` file is
evidence, not a live config.

Recovery order:

1. `config get --summary --json` - confirm the app is running and see the live
   revision and path.
2. Look in `%AppData%\Kobold\backups` for a snapshot written by
   `config backup` or by a previous replace/restore.
3. `config restore` with `--backup`, the snapshot path, `--yes` and `--json` -
   only files inside the backups directory are accepted. If the result is
   `restartRequired`, ask the user and rerun with `--restart`.
4. If the only usable copy is `config.json.failed-*` or
   `config.json.backup`, validate it with `config validate --file`, explain the
   risk, and use `config replace --file` with the same confirmation and restart
   rules. A replace always writes a fresh backup first.

Do not hand-edit a failed copy into the live path. Do not delete `.failed-*`
files while diagnosing; they are the only record of the damaged document.

If a write fails with `validation_failed`, read `details.errors` or the
`warnings` array and correct the document. If it fails with `forbidden`, the
patch touched a structural field; use `widget.*`, `group.*` or
`config replace` instead.

## Revision conflicts

`conflict` means the live config changed after the revision was read. The
response carries `details.currentRevision`. Re-read the document, regenerate
the patch against the new content, and ask the user again. Never retry with a
stale or invented revision, and never force a write over the conflict.

The CLI can read the current revision itself when `--base-revision` is omitted,
but that is still a single read: the revision must be used immediately.

## Widget or group anomalies

Inspect the structure before changing it:

```powershell
& $ctl config get --summary --json
& $ctl diag snapshot --json
& $ctl diag folders --json
```

- A widget missing from the island: check `Widgets` in the summary for its
  `Id`, `Kind`, `IsExpanded`, `GroupId` and `PanelX/PanelY`. `diag folders`
  lists only folder views that the app actually loaded.
- A group with fewer than two members is not valid. On load the app clears
  stale `GroupId`/`GroupOrder` fields and drops the group; the surviving
  widget becomes a plain panel. This is repair, not data loss.
- To split one tab out, use `group detach` with `--group` and `--widget`.
- To change tab order, use `group reorder` with `--group`, `--from` and `--to`
  and 0-based positions.
- To dissolve a group while keeping its widgets, use `group dissolve` with
  `--group` and `--yes` after confirmation.
- To remove a widget, use `widget delete` with `--id` and `--yes` after
  confirmation. Deleting a grouped member updates the group automatically;
  below two members the group dissolves.
- A widget that is renamed, recolored, locked or pinned through
  `widget update` keeps its id, group membership and panel geometry. Group
  `IsPanelPinned` is derived from the members, so pinning one member pins the
  group window.

If the summary and `diag snapshot` disagree on counts, the app may be mid-save.
Wait for the debounced save, re-read the summary, and only then act.

## Theme, language or another setting did not take effect

`config patch` applies hot fields immediately and returns `changes`,
`warnings` and `restartRequired: false`. Verify what was accepted:

```powershell
$revision = (& $ctl config get --summary --json | ConvertFrom-Json).result.revision
& $ctl config patch --file "$env:TEMP\kobold-patch.json" --base-revision $revision --dry-run --json
& $ctl config get --summary --json
```

- `changes: []` means the patch was a no-op; the value already matched.
- Check the spelling and allowed values: `Theme` is `dark`, `light` or
  `high contrast`; `Language` is `en`, `zh` or `ja`; `IconStyle` is `classic`
  or `handdrawn`.
- `warnings` can report a failed startup-entry or Quick Access write even
  though the config itself was saved. Report the warning instead of retrying
  the whole patch.
- If the config shows the new value but the UI still looks stale, ask the user
  to restart Kobold. `--restart` belongs to `config replace` and
  `config restore` only; do not invent a restart command for a patch.

A structural change (widget/group membership, or a full `config replace`) is
different: the app writes nothing until `--restart` is passed, and the CLI
exits 3 with `restartRequired`. Ask the user, then rerun with `--restart`.

## App not running or unreachable

`app_not_running` and exit code 2 mean the current-user control pipe was not
found. Ask the user whether to start Kobold:

```powershell
& $ctl status --json
& $ctl --start status --json
```

`--start` looks for `%LocalAppData%\Programs\Kobold\Kobold.exe`, then the
repository's `bin\Debug\net48\Kobold.exe`, and waits up to 10 seconds for the
pipe. Use `-Exe <path>` when the app is installed elsewhere. Do not start or
restart the app without the user's consent.

If the pipe exists but the app answers with `control_unavailable` or
`unsupported_protocol`, the running build and the CLI do not match. Ask the
user to restart or update Kobold; do not keep retrying the same request.

`restart_failed` means the config was written but the app did not return within
20 seconds. Do not repeat the write. Ask the user to start Kobold manually,
then verify:

```powershell
& $ctl status --json
& $ctl config get --summary --json
```

The pre-write backup remains under `%AppData%\Kobold\backups`; restore it only
with the user's explicit approval.
