# T0 Spike report — Widget Groups UI feasibility

**Status: GREEN · GO** (A1–A15 pass; manual-only items pending a human).

Throwaway WinExe that proves the **real** `TodoCalendarView` + `NotePanelView`
can be re-hosted in one group window behind **bare top tabs** (2px member-coloured
underline), keep input/focus/state alive, and that real editing, the note title
menu, the todo context menus and the outside-click commit all work. No production
file was changed.

## Files (all new, only `tests/GroupUiSpike/**`)

| File | Role |
|---|---|
| `GroupUiSpike.csproj` | WinExe/net48/x64, ProjectReference to `Kobold.csproj` |
| `SpikeContract.cs` | `SpikeInput` + `ISpikeGroupWindow` (the harness/artifact seam) |
| `Program.cs` | bootstrap, `--check` A1–A15, `--screenshot`, `--ui` |
| `GroupSpikeWindow.cs` | group window (hidden hosts, slots, tabs, title row + chevron, routing) |
| `ControlWindow.cs` | T0.5 always-on-top control window |
| `screenshots/spike-todo.png`, `screenshots/spike-note.png` | artifacts |

## Commands and results

```powershell
dotnet build tests\GroupUiSpike\GroupUiSpike.csproj -c Debug
# Build succeeded. 0 Warning(s) 0 Error(s)

dotnet run --project tests\GroupUiSpike -- --check
# PASS A2..A15, PASS A1, PASS A10 · RESULT: GREEN (15 checks passed) · EXIT=0

dotnet run --project tests\GroupUiSpike -- --screenshot tests\GroupUiSpike\screenshots
# wrote spike-todo.png · wrote spike-note.png · EXIT=0

dotnet run --project tests\GroupUiSpike -- --ui
# process alive after 5 s (manual UI)
```

Real config pin (SHA-256, external + in-harness): unchanged across every run.
The live Kobold instance rotated its own config between review rounds
(`config 41e0ad61…`, `backup 6b175c97…`); three idle samples were identical and a
controlled `--check` run changed neither hash nor mtime. The spike only ever
writes the temp sandbox.

```powershell
git status --porcelain   # identical before/after (baseline covers the T1–T6 work), only tests/GroupUiSpike/ added
```

## RED evidence (fix round 1, before the new checks' implementations)

```text
FAIL A13: the title-row menu affordance 'TitleMenuButton' is not registered on the group window
RESULT: RED (1 failed) · EXIT=1
```

A12 / A14 / A15 passed on their **first** run: the underlying capability already
worked, the gap was missing coverage (no defect). A13 was the only new RED; it
needed a real title-row affordance (chevron + name scope), now implemented.

## Acceptance

| # | Result | Evidence |
|---|---|---|
| A1 | **PASS** | sandbox forced save; real config/backup hashes equal at exit |
| A2 | **PASS** | tab row 32, title row 40, 2 slots, real views in the slots, hosts detached/hidden |
| A3 | **PASS** | transparent tab roots, no pill/Path, exactly 1 visible 2px underline, `#FF6A9BCC` / `#FF788C5D`, flush + tab-width |
| A4 | **PASS** | click sim; 10 switches keep the same view instances and 760×540 |
| A5 | **PASS** | 700×500/820×600: todo `DesiredHeight` 391 (>0), note actual 686×414 / 806×514; tab switch keeps size |
| A6 | **PASS** | `ChromeTitle` in the title row (home + detail, ≠ tab label); flush safe; delete → host `NotifyDataChanged` → `TryUndo` restores |
| A7 | **PASS** | todo add-box text + caret 5 + keyboard focus survive todo→note→todo |
| A8 | **PASS** | Esc closes the note title menu; `PreviewMouseLeftButtonDown` delivered; Ctrl+Z (thread keyboard state + real `PreviewKeyDown`) restored a deleted todo |
| A9 | **PASS** | Show/Hide → `IsVisible`; Topmost toggles; pinned group survives deactivation; lock refuses `TryResize` |
| A10 | **PASS** | `git status --porcelain` byte-identical before/after (every baseline line, in order); any line not in the baseline must be under `tests/GroupUiSpike/`; no git writes |
| A11 | **PASS** | both PNGs rendered (>1 KB, different content); artifact run exits 0 |
| A12 | **PASS** | real `NoteSurfaceView`: `BeginEditWhole` + `FocusEditBox`, appended text, tab switch flushes → text in `NoteData` (65 chars) + `NotifyDataChanged`, detail re-renders it, title row still `ChromeTitle` |
| A13 | **PASS** | title-row chevron (`TitleMenuButton`, visible only on the note tab) click → `IsTitleMenuOpen`; Esc closes and is handled |
| A14 | **PASS** | day-area menu `清理已完成` via real `ContextMenuOpening` (opened: `IsOpen=True`); row menu `添加子项 \| 删除` |
| A15 | **PASS** | inline item editor changed, outside left-click through the group's `PreviewMouseLeftButtonDown` committed `外部点击提交改后文本` to `TodoData`, focus left the editor |

**PENDING HUMAN (not exercised, not claimed):** IME candidate placement,
subjective flicker, real mouse hover/right-click *feel* (menus are verified
structurally + opened programmatically), title-chevron popup anchoring by eye.

## Dependencies T2 must fix

1. **Concrete host types** (`Attach(..., TodoWidget/NoteWidget)`) → hidden hosts;
   needs `IWidgetViewHost`.
2. **Height coupling**: `CurrentContentHeight` reads host state; hidden hosts need
   `PanelWidth/PanelHeight = content size (+42)`. `ApplyResizeLayout()` is internal;
   `Refresh()` rebuilds the day list (expensive on resize).
3. **Title/selection events**: group re-reads `ChromeTitle`/`SelectedDate` and
   hooks host `OnDataChanged`; `NoteWidget.RefreshChromeTitle()` is internal.
4. **Window-level behaviour** (outside-click blur, Esc, Ctrl+Z, note flush) lives
   on the widgets and is re-dispatched by the group; T2 needs a shared controller.
5. **Focus** is dropped when the leaving slot collapses; the spike remembers the
   per-member `IInputElement` and restores it deferred.
6. **Tab icons**: `IconPresenter(Calendar)` / `IconPresenter(File)` (no Note id).
7. **Note editing seams**: `NoteSurfaceView.BeginEditWhole/FocusEditBox/Flush` are
   public, the edit box is `x:Name="EditBox"`, and commits fire
   `DataChanged` → host `NotifyDataChanged`. Good; T2 should keep these.
8. **Bootstrap caveat**: `new App(); InitializeComponent()` cannot be used in a
   dispatcher-pumping harness while the live app runs (`App.OnStartup` signals it
   and shuts this process down on the first pump). The spike uses a plain
   `Application` + `Themes/Controls.xaml`.

## Screenshots

`tests/GroupUiSpike/screenshots/spike-todo.png` (30,830 B, no chevron) ·
`tests/GroupUiSpike/screenshots/spike-note.png` (19,899 B, pin/lock/▾)

## Conclusion

**GO.** Real views render and accept input; tabs, underline, context title,
show/hide, topmost, pin, lock, note editing, note title menu, todo menus and the
outside-click commit all work; the user config is untouched. No blocker for
T1/T2.
