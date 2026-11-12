# Kobold config schema

`config describe --json` is the authoritative schema for the running build. It
is generated from the app's own types, so new top-level fields, widget kinds,
group fields and FileSearch fields appear there automatically. Use this page
for orientation and examples; confirm names and allowed values with
`koboldctl.ps1 config describe --json` before writing a patch.

The live document is `%AppData%\Kobold\config.json`. Never edit it directly.
All changes go through `koboldctl.ps1`; settings use `config patch`, widget
structure uses `widget.*`, and group structure uses `group.*`.

## Top-level settings

| Field | Type | Notes |
|---|---|---|
| `SchemaVersion` | number | Written by the app. Read-only for callers; `config replace` must not set a newer value. |
| `Language` | string | `en`, `zh` or `ja`. |
| `Theme` | string | `dark`, `light` or `high contrast`. |
| `StartWithWindows` | boolean | Registers or removes the current-user startup entry. Failures are reported as warnings. |
| `HideDesktopSourceOnStore` | boolean | Hide the desktop source entry after a file is stored. |
| `PanelOpacity` | number | Expanded panel opacity, `0.2` to `1.0`. |
| `IslandX` | number or null | Remembered island centre in DIP. `null` centres it. |
| `IslandCollapseDelay` | number | Seconds after the mouse leaves before the island collapses. |
| `HideDesktopIcons` | boolean | Hide desktop icons while Kobold runs; restored on exit. |
| `HideIconsOriginal` | number or null | Internal desktop-icon bookkeeping. Summary output omits it; do not patch it. |
| `DefaultGridColumns` | number | Columns for newly created folder widgets. |
| `IconStyle` | string | `classic` or `handdrawn`. |
| `ItemScale` | number | Item size scale, `1.0` is base and `1.3` is Windows standard. |
| `PinStorageToQuickAccess` | boolean | Desired Quick Access state for the storage folder; Explorer failures are warnings. |
| `Widgets` | array | Widget objects. Structural: use `widget.*`, not `config patch`. |
| `FrequentFiles` | array or absent | Usage counters for frequently opened files. Structural and optional. |
| `Folders` | array or null | Legacy widget list, migrated into `Widgets` on load. Do not write it. |
| `Groups` | array or absent | Tab groups. Structural: use `group.*`. |
| `FileSearch` | object or absent | File-search settings; absent means unconfigured. |

`config patch` accepts only the non-structural settings:
`Language`, `Theme`, `PanelOpacity`, `IslandX`, `IslandCollapseDelay`,
`DefaultGridColumns`, `IconStyle`, `ItemScale`, `StartWithWindows`,
`HideDesktopSourceOnStore`, `HideDesktopIcons`, `PinStorageToQuickAccess` and
`FileSearch`. Any `Widgets`, `Groups`, `Folders`, `FrequentFiles` or
`SchemaVersion` key is rejected with `forbidden`.

## Widget objects

Every widget shares the fields below.

| Field | Type | Notes |
|---|---|---|
| `Id` | string | Stable identity used by `widget.*` and `group.*`. |
| `Kind` | string | `folder`, `todo`, `note` or `search` in the current build. |
| `Name` | string | Display name. |
| `Color` | string | Hex color such as `#788C5D`. |
| `PosX`, `PosY` | number | Island tile position. |
| `IsExpanded` | boolean | Panel open state. |
| `IsLocked` | boolean | Locked panels cannot be moved or resized. |
| `IsPanelPinned` | boolean | Pinned panel stays open when focus leaves. |
| `PanelX`, `PanelY` | number or null | Remembered panel position. |
| `PanelWidth`, `PanelHeight` | number or null | Remembered panel size. |
| `PanelDpiScale` | number or null | DPI scale the panel geometry was saved on. |
| `GroupId` | string or absent | Group membership index. Use `group.*`. |
| `GroupOrder` | number or absent | 0-based tab order inside the group. Use `group reorder`. |

### Kind-specific fields

| Kind | Fields |
|---|---|
| `folder` | `Path` (folder being shown), `IsReference`, `OriginalPath`, `GridColumns`, `MaxPanelRows`, `BrowseStack`, `Items`. Each item has `Name`, `Path`, `IsReference`, `OriginalPath`. |
| `todo` | `Items`, `SelectedDate`, `HolidayRegion`, `CalendarCellHeight`. Each item has `Id`, `Text`, `Date`, `Done`, `CompletedDate`, `Children`; a child has `Id`, `Text`, `Done`. |
| `note` | `Notes`, `FontScale`. Each note has `Id`, `Text`, `CreatedAt`, `ModifiedAt`. `FontScale` is `0.9`, `1.0` or `1.2`. |
| `search` | No extra fields. Query text is never persisted. |

An unknown kind is preserved as raw JSON and is not turned into a window. Do
not rewrite widgets of an unknown kind.

## Group objects

`Groups` is absent when there are no groups. Each group holds shared panel
state; membership itself lives on the widgets as `GroupId`/`GroupOrder`.

| Field | Type | Notes |
|---|---|---|
| `Id` | string | Group identity. |
| `ActiveWidgetId` | string | Visible tab; falls back to the first member when invalid. |
| `PanelX`, `PanelY` | number or null | Shared panel position. |
| `PanelWidth`, `PanelHeight` | number or null | Shared panel size. |
| `PanelDpiScale` | number or null | DPI scale for the shared geometry. |
| `IsExpanded` | boolean | Group window open state. |
| `IsLocked` | boolean | Group-level lock. |
| `IsPanelPinned` | boolean | Derived from the members: true when any member is pinned. |
| `IslandExpanded` | boolean | Member icons shown inline in the island row. |

A group needs at least two members. Below two, it dissolves and the survivor
becomes a plain widget. `group merge`, `group detach`, `group reorder` and
`group dissolve` maintain these fields.

## FileSearch settings

`FileSearch` is absent until the feature is configured. `config patch` merges
the supplied object into the existing one. If the block is absent, supply a
complete object: missing booleans become `false` and a missing
`ReconcileMinutes` becomes `0` (reconcile disabled).

| Field | Type | Notes |
|---|---|---|
| `Enabled` | boolean | Master switch for the service. |
| `Configured` | boolean | Marks that the user completed file-search setup. |
| `Roots` | array of strings | Drives or directories. Empty means every fixed NTFS drive. |
| `IndexAllFiles` | boolean | When true, `Types` is ignored and every file is indexed. |
| `Types` | array of strings | Lowercase extensions without the dot. Normalized on load. |
| `IndexFolders` | boolean | Index directory names as well as files. |
| `IndexExtensionless` | boolean | Index files with no extension. |
| `ExcludeDirectories` | array of strings | A bare name matches at any depth; a full path matches its subtree. |
| `ReconcileMinutes` | number | Minutes between background reconciles; `0` disables; clamped to `0`..`1440`. |

The index stores metadata only: file names, locations and sizes, never file
contents. Changing roots, types, exclusions or the index flags changes the
settings fingerprint, so the app knows the published index needs a rebuild.

## Patch examples

`config patch` uses RFC 7386 JSON Merge Patch semantics. A `null` value removes
a field (for example, `IslandX` returns to centred). Arrays replace the old
array; nested `FileSearch` objects merge key by key.

Preview every patch with `--dry-run`, then apply it with the revision read
immediately before the write:

```powershell
$revision = (& $ctl config get --summary --json | ConvertFrom-Json).result.revision
& $ctl config patch --file "$env:TEMP\kobold-patch.json" --base-revision $revision --dry-run --json
& $ctl config patch --file "$env:TEMP\kobold-patch.json" --base-revision $revision --json
```

### Theme and language

```json
{
  "Theme": "light",
  "Language": "zh"
}
```

### Island, icons and opacity

```json
{
  "IslandX": null,
  "IslandCollapseDelay": 0.6,
  "IconStyle": "handdrawn",
  "ItemScale": 1.2,
  "PanelOpacity": 0.95
}
```

### File search (complete object for an unconfigured block)

```json
{
  "FileSearch": {
    "Enabled": true,
    "Configured": true,
    "Roots": ["D:\\Documents", "D:\\Projects"],
    "IndexAllFiles": false,
    "Types": ["pdf", "docx", "xlsx", "txt", "md"],
    "IndexFolders": true,
    "IndexExtensionless": true,
    "ExcludeDirectories": ["node_modules", ".git"],
    "ReconcileMinutes": 30
  }
}
```

### Startup and desktop behaviour

```json
{
  "StartWithWindows": true,
  "HideDesktopIcons": true,
  "HideDesktopSourceOnStore": true,
  "PinStorageToQuickAccess": true
}
```

## Validation and replacement

- `config validate` with `--file` and a JSON document path validates a complete
  document without writing. It returns `ok`, `warnings` and a normalized
  summary. An invalid document returns `validation_failed`.
- `config replace` with `--file` and a complete JSON document replaces the
  whole document. A structural difference (widgets, groups, legacy folders or
  frequent files) writes nothing unless `--restart` is passed, and reports
  `restartRequired`; the CLI exits 3. Hot-only changes apply like a patch.
- `config backup` writes a timestamped snapshot under
  `%AppData%\Kobold\backups`. `config restore` accepts only files in that
  directory.
