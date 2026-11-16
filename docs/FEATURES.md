# Kobold — feature reference

This is the detailed feature log moved out of the README. It is intentionally exhaustive: implementation notes, edge cases and the manual full-disk smoke checklist live here so the README can stay a user manual.

## Features

- **Dynamic island launcher** — a slim pill at the top centre expands on
  hover into desktop / widget / add / settings entries. Click a widget
  tile to toggle its panel; drag the pill to move the island. Only the
  widget tiles scroll (a slim bar underneath hints at it) when there are
  more than fit; right-click a tile for that widget's options.
- **Folder panels** — glassy panels with per-widget colours, lock, pin and
  rename; drag any edge or the bottom-right corner to resize one freely, and
  the size is remembered per widget across restarts (a locked panel cannot be
  resized). The grid follows the panel: a wider panel spreads the icons out
  and adds a column as soon as one more fits, while a taller one only adds rows
  with the row spacing unchanged — the extra height stays at the bottom.
  **Item size** is a six-step setting in the widget menu and no longer follows
  the panel; the old per-widget **columns** / **max rows** options are gone,
  and panels no longer resize themselves with their content. Panels that were
  open when the app closed come back on the next launch, on the monitor and
  scale they were saved on, without stealing focus.
- **Folder selection in the title bar** — the panel title follows the
  selection: one entry shows its full name (the tooltip holds the whole
  path), several show **N selected**, and an empty selection falls back to
  the widget's name (or the folder being browsed). The arrow keys walk the
  grid: left/right step to the neighbouring cell across rows, up/down hold
  the column, a short last row falls to its last entry, and an edge stops —
  with nothing selected, the first arrow picks the first entry.
- **Widget kinds** — a new widget is a **folder**, a **todo** or a **note**,
  picked when you create it; all three kinds behave the same on the island, in
  panels, in the context menus and when panels come back after a restart.
- **One launcher card** — the note title `▾` and the island `+` open the same
  floating card: one surface and row style; the island rows show each kind's
  icon, name and a one-line hint. Right-click menus stay classic context menus.
- **Note widget** — a note widget is a list of cards (its home), newest
  first: each card shows the note's **title** (its first heading or line),
  when it was last edited, a two-line excerpt derived from the text and a
  `✓ 2/5` task-progress badge when the note holds task boxes. Click a card and
  the note opens as a detail page that fills the widget — no page jump — and
  the list hides. The `▾` next to the title opens a dropdown: **＋ New**,
  **‹ Notes** back to the list, and every note for a one-click switch (the
  current one is ticked and highlighted); the title text itself stays the drag
  area. The detail is a rendered Markdown subset — headings, lists, task
  boxes, quotes, code blocks, tables, dividers, emphasis and links
  (http/https links open in the browser) — and its task boxes stay live:
  clicking one writes it back into the source. Click any block and **that
  block alone** becomes a source box in place, with the caret exactly on the
  character you clicked, while the rest stays rendered. Clicking the blank
  space (between blocks, the card padding or the panel margin) saves and
  leaves the edit but stays on the note, and so does Esc; clicking the blank
  space below the last block appends a new block and starts editing it; Esc or
  Backspace on a rendered note returns to the list. The box keeps the
  quick-insert right-click menu (headings; bold / italic / strike / inline
  code / link; bullet, numbered and task lists, quote, code block, table,
  divider), whose insertions the box's own Ctrl+Z undoes. **Font size** —
  Small / Medium / Large — is a per-widget entry in the widget's right-click
  menu that scales the note and its inline box, not the cards. There is no
  save or cancel button: typing is saved as you go, Esc never drops text, the
  dropdown's **＋ New** (or the blank area's right-click menu on home) starts a
  new note with the editor ready (an empty new note goes away when you leave),
  a note emptied on the way out is discarded, and a delete from the card's row
  menu steps back with the list's Ctrl+Z.
- **Todo widget** — a month calendar (‹ › change the month, **Today** when
  you are on another month) over the day's list: completed entries are struck
  through and sink to the bottom. An open entry whose date is in the past
  shows up on today with a `↷ original date` badge (display only — the stored
  date never changes). Sub-todos nest one level: indented, without dates of
  their own; ticking a parent ticks all of its children, while unticking the
  parent leaves the children as they are. The panel size is entirely up to
  dragging: the calendar cell height and the day's list stretch with the
  height. In the Chinese UI a day cell's **second line** (festival / solar
  term / lunar date) shows only when the cell is tall enough. The divider
  between the calendar and the list can itself be dragged to give either half
  more room; the **+** row sticks to the bottom while the list scrolls and
  follows the last entry when there is room, and clicking it jumps the list to
  the bottom. Today wears the hand-drawn circle with a matching hand-drawn
  check tucked into its bottom-right corner.
- **Todo editing, undo & cleanup** — click a row to edit it in place: the
  insertion caret takes the widget's colour (system ink under high contrast),
  and Backspace at the very start of the text is a ladder — an open entry is
  ticked (the normal completion path: today is stamped as the completion date
  and a parent's children follow), a completed or emptied one is deleted,
  parents and sub-todos alike. Enter walks the list: a parent's editor opens
  its first sub-todo (or its sub-todo adder when it has none), a sub-todo's
  editor opens the next sibling (or its parent's adder after the last), and
  an adder keeps adding while you type; an empty Enter moves to the next
  parent's editor and ends at the bottom add box. Clicking away never jumps.
  Deletes and ticks — the row menu, the ladder, **Clear completed**, parent /
  child / ladder ticks — step back with Ctrl+Z up to 20 steps (no redo; a
  focused editor keeps its own text undo). **Clear completed**, on the list's
  or the calendar's blank space, removes the selected day's completed entries
  — a completed parent goes with its whole subtree, completed sub-todos under
  an open parent go one by one — and is greyed out when the day has none;
  Ctrl+Z brings the batch back.
- **Widget groups** — merge a folder, todo, note or search widget into one
  group window by dragging one panel's header onto another. The members sit
  behind bare top tabs: no pill or divider, just a 2px underline in the active
  widget's colour, and each tab shows the member's own name (the kind label is
  only a fallback). The title shows the active tab's context and carries no
  member or pin count. Lock and the panel geometry belong to the group; pinning
  is per member — any pinned member keeps the group window open when it loses
  focus, and the header pin toggles only the active member. Right-clicking the
  title row opens the active member's own menu (rename, colour, kind-specific
  options, lock, pin, delete), while right-clicking a tab still opens the group
  menu (switch tab / detach / hide group); the island group tile keeps that
  same group menu. On the island a group folds into one 44×44 tile: a 2×2 grid
  of member icons (the first three plus `+N` for five or more), a neutral
  outline while the group is open, and no count badge or folder glyph. Clicking
  the tile expands it inline: 44×44 member icon tiles are inserted immediately
  to the right of the group tile, pushing the rest of the row right — not a
  popup, a second window or a separate panel; clicking it again folds them
  away. Each icon names its member in a tooltip only, the active member wears a
  soft wash and a small dot, and a small gold pin marks pinned members. Clicking
  a member icon opens the group and switches to its tab. The expansion is
  stored per group (`WidgetGroup.IslandExpanded`): it survives island
  collapse/expand and restart, and several groups may be expanded at once.
  While you drag one component over another, the merge target shows a
  pre-rendered preview of the dragged component before you release. Drag a tab
  out (or right-click **Detach this widget**) to split it off again, and a
  group that drops to one member dissolves by itself. Deleting a folder member
  from a group ejects its stored files first, and a folder tab keeps browsing,
  its native file menu, drag/drop and the live folder watcher while it is the
  active tab.
- **Quiet windows** — the island and the panels stay out of Alt+Tab and Task
  View while still behaving like normal, activatable windows.
- **Steps aside for fullscreen** — while a fullscreen app or video is in
  front, the island steps out of the way and comes back when the fullscreen
  session ends.
- **Browse folders in place** — double-click a folder entry to list its
  contents inside the panel: drill in, go back (button or Backspace), open
  files with the shell. While the panel is open the listing follows the
  folder live, and the panel remembers the folder you left it in, across
  hides and restarts.
- **File actions in panels** — while browsing a folder, create, rename and
  delete (to the Recycle Bin) in place, open a terminal at the current
  folder, or fall back to the full Windows context menu (Git Bash, 7-Zip,
  properties…) via **Show more options**.
- **File clipboard while browsing** — press Ctrl+C to copy the selected
  entries, Ctrl+X to cut them, Ctrl+V to paste them into the folder on
  screen. It is Explorer's own file clipboard, so it works both ways: copy
  out of a panel and paste into an Explorer window or the desktop, or copy
  in Explorer and paste into a panel (a cut pastes as a move). A copy-paste
  never overwrites — when the name is taken, both copies are kept; a
  cut-paste collision keeps the shell's own replace prompt. Locked panels
  refuse every clipboard action.
- **File clipboard on a panel** — with items selected at a panel's root,
  Ctrl+C publishes them to the system clipboard so Explorer or another panel
  can paste them; Ctrl+V adds the clipboard's files to the panel as
  **references** — the same rule as dropping files in: the original stays
  where it is. The root panel does not respond to Ctrl+X.
- **New File dialog** — the panel's empty-area **New File** entry and the
  browse menu's entry share one dialog: a name box and an editable extension
  selector with the thirteen common suffixes (.txt, .md, .json, .csv, .log,
  .html, .xml, .ini, .bat, .ps1, .docx, .xlsx, .pptx; a custom
  single-segment extension can be typed) plus a live **Will create** preview
  of the final name. A name that already ends with the suffix is not doubled;
  the dialog follows the widget's colour. Text files are created empty under
  exactly the previewed name; `.docx` / `.xlsx` / `.pptx` are written from a
  minimal OOXML template (a blank paragraph / worksheet / slide) with the
  parts Office expects. Opening them in Word / Excel / PowerPoint is a smoke
  check, not an automated pin. The browse menu's old **New Text
  Document** entry is now this **New File**.
- **Folder colors** — right-click a folder in a panel and pick one of nine
  colors from the same 3×3 palette the widget colour uses: the icon changes
  everywhere (Explorer included), and **Default color** puts the system icon
  back. The default entry is only offered when the folder already has a
  custom colour. The name/prompt dialogs (new folder, rename) follow the
  widget's colour too.
- **Drag & drop everywhere** — drop files from Explorer onto a panel,
  reorder entries with a drop indicator, move them between widgets, or drag
  them out. Windows performs the move, so dropping an entry into an Explorer
  window or onto the desktop really relocates the file — collision prompts
  and progress are the shell's own.
- **Move files between panels** — browse a folder in two panels and drag
  entries from one into the other: the files move into the folder on screen.
- **Drop into a folder** — while browsing, drop entries onto any folder
  entry to move them inside, or hold Ctrl to copy them; dropping them on the
  **Back** button moves them up to the parent folder. Locked panels refuse
  the drop.
- **Drop into a browsed folder** — an external file dropped on the panel
  blank copies into the current folder; dropped on a folder row it copies
  into that subfolder. Explorer-style `(1)` names keep both copies. File
  rows and locked panels still refuse.
- **Enterprise WeChat** — its FileDrop (a WXWork cache path) follows the
  same rules: the widget root keeps a reference, a browsed folder gets a
  copy.
- **Readable badges** — a coloured cabinet badge means the file lives in
  Kobold storage; a grey `!` means the source was deleted or moved.
- **Safe by design** — ejecting or unstoring moves files back to their
  original location; nothing is trapped inside a widget.
- **Desktop icons toggle** — single-click the island's desktop entry to
  hide/show desktop icons, double-click to open the Desktop folder.
- **Theming** — dark/light applies to every surface (panels, island,
  settings, menus, tooltips); context menus pick up their widget's colour;
  with Windows high contrast on, every surface follows the system colours.
- **Slim scrollbars, rounded menus** — every scrollbar is a thin pill: an
  8 px hit area, a 4 px resting thumb that grows to 6 px and darkens while
  hovered or dragged, and no track arrows. Context menus share one rounded,
  airy surface — 12 px corners, an inset pill hover, a thin chevron for
  submenus, and disabled entries at half opacity — still tinted with their
  widget's colour.
- **Three languages** — English, 简体中文, 日本語.
- **Tray + startup** — tray menu for add / show all / hide all / settings
  / exit, single-instance guard and optional auto-start with Windows.
- **Idle memory trim** — after two minutes without interaction the app hands
  its working set back to Windows: the Task Manager figure drops to a few MB
  while idle, and the pages fault back in on demand.
- **DPI aware** — PerMonitorV2, verified at 200% scaling.

## File search

Add the **search** widget from the island `+` menu (or the tray menu) to find
files and folders by name across your fixed NTFS drives.

- **Index** — `%LocalAppData%\Kobold\search-index.db` (SQLite). The native
  `e_sqlite3.dll` and its managed support assemblies ship flat beside
  `Kobold.exe`; the installer copies all 12 runtime files. Only **metadata** is
  stored — path, name, extension, size, modified time and root. File contents
  are never read or indexed.
- **Diagnostics** — every service state and query is appended to
  `%LocalAppData%\Kobold\logs\search.log` (rotates at 1 MB, one `.1` kept). The
  file records the query text, match count, elapsed time and any error, so a
  transient "no results" leaves evidence.
- **Defaults** — folders and extensionless files are included, plus a built-in
  list of common document, image, video, audio, archive and code types.
  **All files** turns the type filter off; the type list and extra excluded
  folders are editable in **Settings → File search**.
- **Skipped by default** — ten cache trees stay out of both the scan and the
  watcher: Chrome and Edge `User Data`, Firefox `Profiles` (Roaming, plus the
  Local copy Profile Groups leave), the Store apps' volatile directories
  (`%LocalAppData%\Packages\<app>\LocalCache`, `TempState`, `AC` — `LocalState`
  is still searched), `INetCache`, `WebCache`, JetBrains caches, and the
  `%AppData%\Code`, `~\.vscode` and `~\.cache` folders. Rows an older build
  indexed under them are removed by the next reconcile, so a reconcile interval
  of 0 keeps them until the next rebuild.
- **No admin rights** — the index is per-user under `%LocalAppData%`. System
  and build folders (`WinSxS`, `node_modules`, `.git`), reparse points and
  over-long paths are skipped, and the skipped-folder count is shown with the
  status.
- **First run** — an onboarding card lists the fixed drives with **Start
  indexing** and **Later**. Nothing is scanned until you start; the chosen
  roots are remembered.
- **Lifecycle** — a full scan runs at below-normal priority and publishes
  atomically, so the old results stay searchable until it finishes; cancelling
  keeps the old index. A watcher applies creates, renames and deletes
  incrementally, and a **30-minute reconcile** catches missed changes.
  **Rebuild index** and **Pause indexing** live in the widget menu and
  Settings.
- **Results** — up to 300 rows in a virtualized list with the true match count
  (“showing first N of M”); name matches rank before path-only matches. Open,
  open location and copy path sit on each row.
- **Three languages** — en / zh / ja copy for the widget, onboarding, status
  and settings.

### Manual full-disk smoke checklist

The automated checks use scratch trees only. A full-disk pass on a real machine
is still **outstanding** — do this before calling the feature release-ready:

1. Start indexing on a machine with a full system drive; wait for Ready and
   check that the file count is plausible.
2. Search a Chinese name, a very long path and an extensionless file.
3. Create, rename and delete a file while the widget is open; each change
   appears without a rebuild.
4. Restart Kobold; the index is kept and searching works without rescanning.
5. Use **Rebuild index**; results stay searchable during the scan and the
   rebuilt index finishes Ready.
6. Delete or corrupt `search-index.db` and restart; the app quarantines or
   recreates it and can rebuild.
7. Install and uninstall without a UAC prompt; choose **All** and confirm the
   index files are gone while `%LocalAppData%\Kobold` itself is left in place.
