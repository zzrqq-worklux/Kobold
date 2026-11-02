# Claude Warm UI + Hand-drawn Icons + Island Open State — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) or superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the Claude Warm palette/type/panel language, replace every inline 24 px UI glyph with a classic/hand-drawn `IconPresenter`, and make island tiles reflect real panel open state.

**Architecture:** A generated two-style geometry catalog feeds one lightweight `IconPresenter` that reads a global icon style. Theme tokens and ThemeManager gain the Claude Warm palette, surface2/accent roles, serif family tokens and a near-opaque panel policy. `WidgetWindowBase` publishes panel visibility; `WidgetManager` forwards it to `IslandWindow`, which updates tile visuals in place.

**Tech Stack:** C# 7.3, WPF (.NET Framework 4.8), PowerShell 5.1 generator, console self-checks (`dotnet run --project tests/...`).

**Spec:** `docs/superpowers/specs/2026-11-02-claude-warm-ui-design.md`
**Visual source:** `docs/mockups/2026-10-30-claude-warm.html`

## Global Constraints

- No git commits unless the user explicitly asks.
- Preserve the existing uncommitted search diagnostics/mockup changes.
- 64 px `HandDrawnIconRenderer` family stays; 24 px UI assets are a separate layer.
- No emoji/text glyph/classic path fallback for a 24 px UI icon in hand-drawn mode.
- Panel radius 12, window 10, control 8; light `#F7F2E8`, dark `#191714`, clay `#CC785C`/`#D97757`.
- System serif stack only: `Fraunces, Noto Serif SC, Songti SC, SimSun, Georgia`.
- Every task ends with its checks green; checkpoint only, do not commit.

---

### Task 1: Icon foundation (catalog + presenter + check)

**Files:**
- Create: `Core/Icons/IconId.cs`
- Create: `Core/Icons/IconStyleManager.cs`
- Create: `Core/Icons/IconCatalog.g.cs` (generated)
- Create: `Controls/IconPresenter.cs`
- Create: `assets/icons/24/classic/*.svg`
- Create: `assets/icons/24/handdrawn/*.svg`
- Create: `tools/generate-icons.ps1`
- Create: `tests/IconCatalogCheck/IconCatalogCheck.csproj`, `Program.cs`
- Modify: `tools/Run-Checks.ps1` (register the check)

**Interfaces:**
- `IconId` enum with 35 ids.
- `IconCatalog.Get(IconId, bool) : Geometry`, `GetPathData(IconId, bool) : string`, `All : IReadOnlyCollection<IconId>`.
- `IconPresenter.Icon`, `.IconStyle`, `.Brush`, `.Size`, `.Rotation`, `.StrokeThickness`.

- [ ] **Step 1: Write `tests/IconCatalogCheck`** asserting: 35 ids; both path strings non-empty/different; `Geometry.Parse` succeeds; bounds non-empty and within `-1..25`; a 24×24 `RenderTargetBitmap` of an `IconPresenter` has non-transparent pixels for both styles.
- [ ] **Step 2: Run it and watch it fail** (`dotnet run --project tests/IconCatalogCheck`): missing `IconId`/`IconCatalog`/`IconPresenter`.
- [ ] **Step 3: Write the generator** `tools/generate-icons.ps1`. It owns a `$icons` table (name → classic path, hand-drawn path), writes `assets/icons/24/{classic,handdrawn}/<Name>.svg` and `Core/Icons/IconCatalog.g.cs` with two frozen lazy geometry dictionaries. The 17 mockup icons use the mockup's exact classic/hand-drawn `d` values; the remaining utility icons use the same stroke language and a baked wobble variant.
- [ ] **Step 4: Run the generator** and inspect that both asset directories have 35 files.
- [ ] **Step 5: Implement `IconStyleManager` and `IconPresenter`.** The presenter scales 24→`Size`, uses round caps/joins, reads `IconStyleManager.Current` when `IconStyle` is null, subscribes to style/theme changes on `Loaded`, and unsubscribes on `Unloaded`.
- [ ] **Step 6: Run the check** → `ALL ICON-CATALOG CHECKS PASSED`.
- [ ] **Step 7: Register `IconCatalogCheck` in `tools/Run-Checks.ps1`** with `Ci = $false` (WPF render check).

### Task 2: Claude Warm tokens, fonts and ThemeManager

**Files:**
- Modify: `Core/UiTokens.cs`
- Modify: `Core/ThemeManager.cs`
- Modify: `Core/HighContrastPalette.cs`
- Modify: `Core/Localization.cs` (new colour names)
- Modify: `tests/UiTokensCheck/Program.cs`
- Modify: `tests/MenuRenderCheck/Program.cs` (palette pin)
- Modify: `Themes/Controls.xaml` (token consumers only)

**Interfaces:**
- New `Dark/Light` fields: `Surface2`, `Accent`, `AccentHover`, `AccentSoft`, `OnAccent`, `Danger`; all existing fields kept.
- `ThemeManager.Surface2Brush`, `.AccentBrush`, `.AccentHoverBrush`, `.AccentSoftBrush`, `.OnAccentBrush`, `.DangerBrush`; `AccentBlueBrush => AccentBrush` compatibility alias.
- `UiTokens.FontFamilyUi`, `UiTokens.FontFamilyDisplay`; ThemeManager installs `Kobold.FontFamily.Ui` / `.Display`.
- Radii `4/8/10/12`, type `11/13/14/16`.

- [ ] **Step 1: Pin the target values in `UiTokensCheck`** (exact hex table, new required roles, palette order/names, radii/type scale) and make it fail.
- [ ] **Step 2: Implement `UiTokens`** with the spec table; replace `FolderPalette`/`FolderIconPalette` and their localization-name keys. Add the new colour names to en/zh/ja.
- [ ] **Step 3: Extend `ThemeManager`** with Surface2/accent/danger brushes via `Themed`, high-contrast overrides, and the two FontFamily resources; remove `MenuTint*` constants.
- [ ] **Step 4: Update `MenuRenderCheck` palette expectations** to the nine low-saturation hexes.
- [ ] **Step 5: Run `UiTokensCheck` and `MenuRenderCheck`** → all green.

### Task 3: Menu template + icon-aware builders

**Files:**
- Modify: `App.xaml` (MenuItem template: check column, 16 px icon column, `IconPresenter` chevron, radius 10, tighter pill)
- Modify: `Helpers/ContextMenuBuilder.cs` (icon overloads; delete widget tint)
- Modify: `Helpers/WidgetMenuBuilder.cs` (common + kind menus get ids)
- Modify: `Controls/FolderWidget.MenuActions.cs`
- Modify: `Controls/NotePanelView.xaml.cs`, `Controls/NoteSurfaceView.xaml.cs` (title/context menus)
- Modify: `Controls/SearchPanelView.xaml.cs` (result menu)
- Modify: `Services/TrayIconService.cs`
- Modify: `tests/MenuRenderCheck/Program.cs`

**Interfaces:**
- `MenuBuilder.AddItem(string key, Action onClick, bool isEnabled = true, IconId? icon = null)`
- `MenuBuilder.AddSubmenu(string key, Action<SubmenuBuilder>, IconId? icon = null)`
- `SubmenuBuilder.AddItem/AddCheckItem(..., IconId? icon = null)`
- `MenuIcons.Create(IconId, double size = 15, Brush brush = null)` helper.
- `MenuItem.Icon` is always an `IconPresenter`.

- [ ] **Step 1: Extend `MenuRenderCheck`** to recursively assert every non-null `MenuItem.Icon` is an `IconPresenter`, its `IconId` has both catalog styles, and menu headers contain no `＋`/`✓` glyph prefix. Make it fail.
- [ ] **Step 2: Rewrite the `MenuItem` template** with the check/icon/header/chevron columns; `IconPresenter` for check and submenu arrow; `Overlay*` brushes; clay checked background (no widget tint).
- [ ] **Step 3: Add icon overloads and remove `ApplyWidgetTint`.**
- [ ] **Step 4: Assign ids** to the common widget menu, folder root/browse/background menus, search actions, note title/context menus, and tray menu.
- [ ] **Step 5: Run `MenuRenderCheck` and `XamlLoadCheck`** → all green.

### Task 4: Chrome, island system tiles, folder and todo glyphs

**Files:**
- Modify: `Controls/WidgetChrome.xaml` (+ `.cs` for title serif/radius/theme)
- Modify: `Controls/IslandWindow.xaml` (+ `.cs` style application)
- Modify: `Controls/FolderWidget.xaml` (+ `.cs`, `.Theme.cs`)
- Modify: `Controls/TodoCalendarView.xaml` (+ `.cs`)
- Modify: `tests/XamlLoadCheck/Program.cs`

- [ ] **Step 1: Add failing `XamlLoadCheck` pins**: chrome lock/pin/chevron are `IconPresenter` with correct ids; island desktop/add/settings are presenters; folder back/path/help/empty/badges use presenters; todo prev/next/add/check use presenters; no classic/hand-drawn canvas pairs remain.
- [ ] **Step 2: Replace the chrome glyphs** with presenters and set the title to the display serif + 15 px.
- [ ] **Step 3: Replace the island system tiles** with presenters; `RefreshIconStyle` calls `IconStyleManager.Set` + `RefreshTiles`.
- [ ] **Step 4: Replace folder glyphs** (back/path/help, empty folder, stored archive, missing alert).
- [ ] **Step 5: Replace todo glyphs** (month chevrons, add plus, checkbox check).
- [ ] **Step 6: Run `XamlLoadCheck`** → all green.

### Task 5: Search file types, note glyphs, Settings and Tray

**Files:**
- Modify: `Controls/SearchPanelView.xaml` (+ `.cs` for row `IconId`/brush mapping)
- Modify: `Controls/NotePanelView.xaml.cs`, `Controls/NoteSurfaceView.xaml.cs`, `Controls/NoteMarkdownRenderer.cs`
- Modify: `Windows/SettingsWindow.xaml` (+ `.cs`)
- Modify: `Services/TrayIconService.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

- [ ] **Step 1: Add failing pins**: search rows carry an `IconId` for folder/sheet/doc/image/file extensions and the item template hosts an `IconPresenter`; note title/context menus use `Plus`/`Home`/`Check` presenters; settings headers/close use presenters; tray menu items have presenter icons.
- [ ] **Step 2: Add the search box magnifier** (overlay inside the TextBox padding) and replace result glyphs with typed icons + per-type brushes.
- [ ] **Step 3: Replace note `＋`/`✓` and the markdown task check** with presenters.
- [ ] **Step 4: Rebuild Settings headers** as `IconPresenter + TextBlock`, replace `✕`, remove emoji from combo items, set the title serif.
- [ ] **Step 5: Replace tray menu text-only entries** with presenter icons.
- [ ] **Step 6: Add a production-source scan** rejecting emoji/`＋ ▾ ‹ › ✓ ✕` UI glyphs (exclude docs/tests/mockups).
- [ ] **Step 7: Run `XamlLoadCheck` + `MenuRenderCheck`** → all green.

### Task 6: Island open state

**Files:**
- Modify: `Controls/WidgetWindowBase.cs`
- Modify: `Core/WidgetManager.cs`
- Modify: `Controls/IslandWindow.xaml` (+ `.cs`)
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces:**
- `WidgetWindowBase.PanelVisibilityChanged : event Action<bool>`
- `IslandWindow.SetOpenState(string widgetId, bool isOpen)`
- `IslandWindow.SetOpenStates(IEnumerable<string> openIds)`
- Tile `Tag` is the widget id; child name `TileDot`; the collapsed pill has no state dot.

- [ ] **Step 1: Add failing checks**: show/hide raises the event; `SetOpenState` changes the same tile instance's background + dot; tile count is unchanged; a closed tile is transparent.
- [ ] **Step 2: Implement the event and manager subscription.**
- [ ] **Step 3: Rebuild `CreateTile` as Grid + Viewbox + dot, and implement `SetOpenState`/`SetOpenStates`.**
- [ ] **Step 4: Seed open states in `RefreshIsland`**; the collapsed pill stays a clean bar.
- [ ] **Step 5: Run `XamlLoadCheck`** → all green.

### Task 7: Panel visual cleanup (border tint, serif titles, radius)

**Files:**
- Modify: `Controls/WidgetChrome.xaml.cs`
- Modify: `Controls/FolderWidget.Theme.cs`
- Modify: `Windows/SettingsWindow.xaml` (radius/title)
- Modify: `Themes/Controls.xaml` (radius tokens)
- Modify: `tests/XamlLoadCheck/Program.cs`
- Modify: `tests/UiTokensCheck/Program.cs`

- [ ] **Step 1: Update the failing pins**: shell background is the theme Background (not the widget colour), header is Surface2, the 1 DIP outer border is a 65% widget-colour/hairline mix, the header has a 2px accent rail + 6px title dot, folder selection is widget-colour, island open tiles use a 14% wash, menu radius is 10, settings window radius is 10.
- [ ] **Step 2: Replace `WidgetChrome.UpdatePanelBackground`** with near-opaque theme surfaces and `UpdatePanelBorder` (border/rail/dot tint only; no body/header wash).
- [ ] **Step 3: Restore folder selection to a readable widget-colour border + 12% fill**; island tiles use `WidgetAccent.Tint`.
- [ ] **Step 4: Apply panel/window/control radii and serif title fonts.**
- [ ] **Step 5: Run `XamlLoadCheck` + `MenuRenderCheck` + `UiTokensCheck`** → all green.

### Task 8: Full verification and visual smoke

- [ ] **Step 1: `dotnet build Kobold.csproj -c Release`** → 0 warnings, 0 errors.
- [ ] **Step 2: Run** `FileSearchCheck`, `IconCatalogCheck`, `UiTokensCheck`, `XamlLoadCheck`, `MenuRenderCheck` → all pass.
- [ ] **Step 3: Stop the running installed Kobold**, install the new build via `tools/install.ps1` (or run the Release exe), and take light/dark × classic/hand-drawn screenshots.
- [ ] **Step 4: Compare** with `docs/mockups/2026-10-30-claude-warm.png` and `...-handdrawn.png`; fix visible mismatches.
- [ ] **Step 5: Report** commands, outputs and screenshot paths. Do not commit.

## Self-review

- Spec coverage: palette/type/radius → Task 2/7; icons → Task 1/3/4/5; island state → Task 6; tests → every task; search diagnostics untouched.
- No placeholders: each task names files, interfaces and the check that must go green.
- Type consistency: `IconId`, `IconCatalog.Get/GetPathData`, `IconPresenter`, `PanelVisibilityChanged`, `SetOpenState(s)` are used with the same signatures throughout.
