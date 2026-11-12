# P2 Island Group Folder + Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the confirmed P2 island group folder (one folded 44×44 group tile, inline right-expanded 4-column member section, per-member pin), make group tabs show the member's original name, and show a pre-rendered preview of the dragged component over a merge target.

**Architecture:** `WidgetGroup` remains the state owner; membership stays on `WidgetData.GroupId/GroupOrder`. A richer island projection (`WidgetGroupTileMember` / `WidgetGroupTileInfo`) is built by `GroupHostPolicy` and the manager, so `IslandWindow` never reads group config. The island renders one tile per group at its first member's slot; a click expands the existing capsule right (no popup/second window). Pin becomes per-member (`WidgetData.IsPanelPinned`), with `WidgetGroup.IsPanelPinned` kept as a derived compatibility field. Merge feedback uses the existing `WidgetGhostRenderer` bitmap painted into a new `WidgetChrome` preview layer.

**Tech Stack:** WPF / .NET Framework 4.8 / C# 7.3; existing `tests/*Check` console projects + `tools/Run-Checks.ps1`.

**Spec:** `.superpowers/sdd/2026-11-03-widget-groups/P2-design-options.md` (confirmed 2026-11-04; this plan implements it). Extra user decisions 2026-11-06: tabs show the member name; merge target shows a **pre-rendered preview** (not a mask); tab labels and P2 ship together.

## Global Constraints

- Do not commit git. Work on `main` in the existing uncommitted T3a/T3b tree at `aa1efbc`.
- Do not edit `Core/FolderColor.cs`, `Helpers/FolderIconSource.cs`, or `tests/FolderColorCheck/Program.cs`.
- Do not reset/stash/delete `desktop.ini`, `docs/mockups/2026-11-03-widget-groups-ui1*`, `tests/GroupUiSpike/`, or the T3a/T3b changes.
- No separate member `Window`/`Popup`; the member section is the right extension of the existing island capsule.
- Keep the tab underline rule: 2px, active member colour, no pill/background.
- Folded group tile: never a Folder glyph, never a count badge; 2×2 max icons, 5+ = first 3 + `+N`; no member-colour ring on the folded tile.
- Member section: 4 columns, 312 DIP, 66×76 cells, 12px padding, 1px internal seam; whole capsule clamps, no left flip.
- Pin: per member; any pinned member keeps the group window open on blur; header pin = active member only.
- Preview: pre-rendered source bitmap in the target chrome; if render fails, fall back to the existing accent border hint.
- Existing checks stay green; each task ends with its own failing check first.

---

### Task 1: Group tabs show the member's original name

**Files:**
- Modify: `Controls/WidgetGroupTabStrip.cs`
- Modify: `Helpers/WidgetGroupMenuBuilder.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces:**
- Produces: `internal static string WidgetGroupTabStrip.MemberLabel(WidgetData member)` — trimmed `Name` when non-empty, otherwise `KindLabel(member)`.
- Produces: `internal IReadOnlyList<string> WidgetGroupTabStrip.LabelTextsForChecks` — the current label strings in tab order.
- Keeps: `KindLabel` as the fallback-only helper.

- [ ] **Step 1: Write the failing pin**

Add to `tests/XamlLoadCheck/Program.cs` near the group pins:

```csharp
private static void GroupTabsShowMemberNames()
{
    var folder = new FolderData { Id = "tab-name-folder", Name = "季度资料", Color = "#6A9BCC" };
    var note = new NoteData { Id = "tab-name-note", Name = "会议记录", Color = "#788C5D" };
    WidgetGroup group;
    var window = CreateT3bFolderGroup(folder, note, out group);
    try
    {
        if (window.TabStrip.TabButtons.Count != 2)
            throw new InvalidOperationException("the group does not have two tabs");
        var labels = window.TabStrip.LabelTextsForChecks;
        if (labels.Count != 2 || labels[0] != "季度资料" || labels[1] != "会议记录")
            throw new InvalidOperationException(
                "tabs show kind labels instead of member names: " + string.Join(" / ", labels));
    }
    finally
    {
        if (window.IsVisible) window.Close();
    }
}
```

Register it with:
```csharp
failures += TryCheck("group: tabs show the member names", GroupTabsShowMemberNames);
```

- [ ] **Step 2: Run it and verify red**

Run: `dotnet run --project tests\XamlLoadCheck`
Expected: FAIL `tabs show kind labels instead of member names`.

- [ ] **Step 3: Implement the label helper**

In `WidgetGroupTabStrip.cs`:

```csharp
internal static string MemberLabel(WidgetData member)
{
    if (member != null && !string.IsNullOrWhiteSpace(member.Name))
        return member.Name.Trim();
    return KindLabel(member);
}
```

Use it in `BuildTab` and `RefreshLocalization`; set the label to
`TextTrimming = TextTrimming.CharacterEllipsis`, `MaxWidth = 160`, and the tab
tooltip to `member?.Name` (or the label when the name is empty). Implement
`LabelTextsForChecks => _labels.Select(l => l.Text).ToList()` in the strip. In
`WidgetGroupMenuBuilder.Build`, use `WidgetGroupTabStrip.MemberLabel(member)`
for the switch entry header.

- [ ] **Step 4: Run green**

Run: `dotnet run --project tests\XamlLoadCheck`
Expected: the new pin and all existing group/folder pins PASS.

---

### Task 2: Per-member pin model, migration and group-window pin

**Files:**
- Modify: `Controls/WidgetWindowBase.cs` (`TogglePin` virtual)
- Modify: `Controls/WidgetGroupWindow.xaml.cs` (`GroupPanelState`, `TogglePin`, `UpdateUI`)
- Modify: `Controls/WidgetChrome.xaml` / `.xaml.cs` (`HeaderMeta`)
- Modify: `Core/AppConfig.cs` (`RepairGroups` migration)
- Modify: `Core/WidgetManager.cs` (merge/detach/dissolve pin projection)
- Modify: `Core/GroupHostPolicy.cs` (pure any-pinned helper)
- Modify: `Core/Localization.cs` (group pinned meta format)
- Modify: `tests/WidgetGroupsCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`
- Modify: `tests/StartupPanelsCheck/Program.cs` if its group focus is affected

**Interfaces:**
- Produces: `GroupHostPolicy.AnyMemberPinned(IEnumerable<WidgetData> members)` and `PinnedCount(...)`.
- Produces: `WidgetGroupWindow.TogglePin()` override (active member only).
- Produces: `WidgetChrome.HeaderMeta` (string; collapsed/empty by default). The group window sets `"N 个成员 · P 已固定"` (localized key `UI_GroupPinnedMeta`).
- Keeps: `WidgetGroup.IsPanelPinned` serialized as a derived compatibility field.

- [ ] **Step 1: Write the failing pure checks**

Add to `tests/WidgetGroupsCheck/Program.cs`:

```csharp
private static void PerMemberPinPins()
{
    var a = new TodoData { Id = "pin-a" };
    var b = new NoteData { Id = "pin-b" };
    Check(!GroupHostPolicy.AnyMemberPinned(new[] { a, b }),
        "pin: an all-unpinned group is unpinned");
    a.IsPanelPinned = true;
    Check(GroupHostPolicy.AnyMemberPinned(new[] { a, b }),
        "pin: any pinned member keeps the group pinned");
    Check(GroupHostPolicy.PinnedCount(new[] { a, b }) == 1,
        "pin: the pinned count counts members, not groups");
}
```

Register it; run `dotnet run --project tests\WidgetGroupsCheck`; expected:
`AnyMemberPinned` does not exist (compile red).

- [ ] **Step 2: Add the pure helpers and migration**

`GroupHostPolicy`:

```csharp
public static bool AnyMemberPinned(IEnumerable<WidgetData> members)
{
    if (members == null) return false;
    foreach (var member in members)
        if (member != null && member.IsPanelPinned) return true;
    return false;
}

public static int PinnedCount(IEnumerable<WidgetData> members)
{
    int count = 0;
    if (members == null) return 0;
    foreach (var member in members)
        if (member != null && member.IsPanelPinned) count++;
    return count;
}
```

`AppConfig.RepairGroups`, after `ActiveWidgetId` normalization:

```csharp
var pinned = GroupHostPolicy.PinnedCount(members);
if (group.IsPanelPinned && pinned == 0)
{
    // Legacy group-level pin: keep the group open by pinning its active tab.
    foreach (var member in members)
    {
        if (string.Equals(member.Id, group.ActiveWidgetId, StringComparison.Ordinal))
        {
            member.IsPanelPinned = true;
            break;
        }
    }
}
group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(members);
```

- [ ] **Step 3: Make the group pin toggle the active member**

`WidgetWindowBase.TogglePin` becomes `public virtual void TogglePin()`.
`WidgetGroupWindow` override:

```csharp
public override void TogglePin()
{
    WidgetData active = ActiveView == null ? null : ActiveView.Data;
    if (active == null) return;

    active.IsPanelPinned = !active.IsPanelPinned;
    _group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(_members);
    UpdateUI();
    NotifyDataChanged();
    OnPanelStateChanged();
}
```

`GroupPanelState` gets the member list and its `IsPanelPinned` getter returns
`GroupHostPolicy.AnyMemberPinned(_members)`; keep the setter writing
`_group.IsPanelPinned` for compatibility. Because the base constructor builds
the state before the group copies its member list, expose the base state
through a protected `PanelState` property and assign the live `_members` list
to the group state after the copy:
```csharp
// in WidgetWindowBase (internal: IPanelState is internal)
internal IPanelState PanelState => _state;
// in GroupPanelState
public IList<WidgetData> Members { get; set; }
// in WidgetGroupWindow after _members = new List<WidgetData>(members);
var state = PanelState as GroupPanelState;
if (state != null) state.Members = _members;
```
`GroupWindow.UpdateUI` sets
`GroupChrome.IsPinned = ActiveView != null && ActiveView.Data.IsPanelPinned`
after `base.UpdateUI()`.

Add `TextBlock x:Name="HeaderMeta"` to `WidgetChrome.xaml` in the title row
(after `TitleText`, muted 10.5px, collapsed when empty) and a `HeaderMeta`
string property in the code-behind. `WidgetGroupWindow.UpdateUI` sets it to
`Localization.Format("UI_GroupPinnedMeta", _members.Count, GroupHostPolicy.PinnedCount(_members))`;
standalone widgets leave it empty. Add the key to en/zh/ja, e.g.
`"{0} members · {1} pinned"`.

`WidgetManager`:
- group creation/merge: `group.IsPanelPinned = GroupHostPolicy.AnyMemberPinned(members)`.
- `ApplyDetachedStandaloneState` / `ApplyDissolvedStandaloneState`: stop copying
  `group.IsPanelPinned` into `data.IsPanelPinned`; each member keeps its own
  pin. Keep `data.IsExpanded/IsLocked` as today.

- [ ] **Step 4: Write the UI pins**

In XamlLoadCheck:
- group with two members, both unpinned: header pin is gray, group stays
  closable, `GroupPanelState` reports false.
- pin active member: only that member's `IsPanelPinned` true, group field true,
  header pin gold; unpin it: group false.
- pin the inactive member: active header pin stays gray but the group field is
  true (any-pinned keeps the window open).
- legacy config: group `IsPanelPinned=true`, no member pinned → load pins the
  active member and keeps the group field true.
- detach an unpinned member from a pinned group → standalone member pin false;
  detach the pinned member → true.

- [ ] **Step 5: Run**

```powershell
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\WidgetGroupsCheck
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\StartupPanelsCheck
```

Expected: green; legacy groups keep their open behavior.

---

### Task 3: Island group projection + folded group tile

**Files:**
- Modify: `Core/GroupHostPolicy.cs`
- Modify: `Core/WidgetManager.cs`
- Modify: `Core/IslandLayout.cs`
- Modify: `Controls/IslandWindow.xaml` / `.xaml.cs`
- Modify: `tests/WidgetGroupsCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`
- Modify: `tests/IslandLayoutCheck/Program.cs`

**Interfaces:**
- Produces in `GroupHostPolicy.cs`:

```csharp
public sealed class WidgetGroupTileMember
{
    public string Id { get; set; }
    public string Kind { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
    public bool IsPinned { get; set; }
}

public sealed class WidgetGroupTileInfo
{
    public string GroupId { get; set; }
    public string ActiveWidgetId { get; set; }
    public int MemberCount { get; set; }
    public string MemberNames { get; set; }
    public bool IsOpen { get; set; }
    public int PinnedCount { get; set; }
    public List<WidgetGroupTileMember> Members { get; set; }
}
```

- Produces:
  `List<WidgetGroupTileInfo> GroupHostPolicy.BuildIslandGroupTiles(groups, widgets, Func<string,bool> isHostOpen)`.
- Produces:
  `List<string> GroupHostPolicy.OpenProjectedTileIds(groups, widgets, Func<string,bool> isHostOpen)` — group id for an open group, widget id for an ungrouped open widget.
- `IslandWindow.SetWidgets(IEnumerable<WidgetData> widgets, IReadOnlyList<WidgetGroupTileInfo> groups = null)`.
- `IslandWindow.WidgetMenuRequested` can still carry a group id; `WidgetActivated` carries a member id.

- [ ] **Step 1: Write the failing projection checks**

In `WidgetGroupsCheck`:

```csharp
private static void IslandGroupProjectionPins()
{
    var folder = new FolderData { Id = "island-f", Name = "资料", Kind = "folder" };
    var todo = new TodoData { Id = "island-t", Name = "待办", Kind = "todo" };
    var note = new NoteData { Id = "island-n", Name = "笔记", Kind = "note" };
    var group = new WidgetGroup { Id = "island-g", ActiveWidgetId = todo.Id };
    folder.GroupId = group.Id; folder.GroupOrder = 0;
    todo.GroupId = group.Id; todo.GroupOrder = 1;
    note.GroupId = group.Id; note.GroupOrder = 2;

    var tiles = GroupHostPolicy.BuildIslandGroupTiles(
        new[] { group }, new WidgetData[] { folder, todo, note }, id => id == group.Id);
    Check(tiles.Count == 1 && tiles[0].GroupId == group.Id,
        "island projection: one tile per group");
    Check(tiles[0].Members.Count == 3 && tiles[0].Members[0].Id == folder.Id
          && tiles[0].Members[1].Id == todo.Id && tiles[0].Members[2].Id == note.Id,
        "island projection: members keep GroupOrder");
    Check(tiles[0].ActiveWidgetId == todo.Id && tiles[0].IsOpen,
        "island projection: active id and open state");
    var open = GroupHostPolicy.OpenProjectedTileIds(
        new[] { group }, new WidgetData[] { folder, todo, note }, id => id == group.Id);
    Check(open.Count == 1 && open[0] == group.Id,
        "island projection: the open ring is keyed by the group tile");
}
```

In `IslandLayoutCheck`, add pure layout pins for the new constants:

```csharp
Check(IslandLayout.GroupSectionWidth == 312, "island layout: 312 DIP member section");
Check(IslandLayout.GroupRows(6) == 2, "island layout: 6 members = 2 rows");
Check(IslandLayout.GroupRows(9) == 3, "island layout: 9 members = 3 rows");
Check(IslandLayout.GroupSectionHeight(6) == 214,
    "island layout: 6 members = 214 DIP high");
Check(IslandLayout.ClampExpandedCenter(1500, 600, 312, 0, 1920, 16) == 1292,
    "island layout: the expanded capsule clamps 16 DIP inside the work area");
```

Run both; expected red (missing types/methods).

- [ ] **Step 2: Implement the projection and registry**

`BuildIslandGroupTiles`:
- iterate groups in config order;
- members via `MembersOf`; skip <2;
- build `WidgetGroupTileMember` per member (kind, name, color, pin);
- `MemberNames = join(" / ", MemberLabel or name)`;
- `PinnedCount = PinnedCount(members)`;
- `IsOpen = isHostOpen(group.Id)`;
- one `WidgetGroupTileInfo` per group, not per member.

`OpenProjectedTileIds`: iterate widgets in config order; ungrouped open →
widget id; first group member when the group is open → group id; dedupe.

`WidgetManager.RefreshIslandNow`:
```csharp
_island?.SetWidgets(_config.Widgets, BuildIslandGroupTiles());
_island?.SetOpenStates(GroupHostPolicy.OpenProjectedTileIds(
    _config.Groups, _config.Widgets, IsHostOpen));
```

`IslandLayout`:
```csharp
public const double GroupSectionWidth = 312;
public const double GroupSectionHeaderHeight = 30;
public const double GroupCellWidth = 66;
public const double GroupCellHeight = 76;
public const double GroupCellGap = 8;
public const double GroupSectionPadding = 12;
public static int GroupRows(int count) => count <= 0 ? 0 : (count + 3) / 4;
public static double GroupSectionHeight(int count) =>
    count <= 0 ? 0 : GroupSectionHeaderHeight + 2 * GroupSectionPadding
        + GroupRows(count) * GroupCellHeight + (GroupRows(count) - 1) * GroupCellGap;
public static double ExpandedCapsuleWidth(double baseWidth, bool memberSectionOpen) =>
    baseWidth + (memberSectionOpen ? 1 + GroupSectionWidth : 0);
public static double ClampExpandedCenter(double center, double baseWidth,
    double memberWidth, double workLeft, double workWidth, double margin)
{
    double minCenter = workLeft + margin + baseWidth / 2;
    double maxCenter = workLeft + workWidth - margin - baseWidth / 2 - memberWidth;
    if (center > maxCenter) center = maxCenter;
    if (center < minCenter) center = minCenter;
    if (maxCenter < minCenter) center = minCenter; // oversized: anchor left
    return center;
}
```

- [ ] **Step 3: Fold the island tiles**

In `IslandWindow`:
- store `_groupTiles` and build a projected tile list: iterate `_widgets`
  with a window kind; if the widget's group id has a tile and that group has
  not been emitted, emit one group tile at this slot; otherwise emit a normal
  widget tile. Skip the other members.
- `RebuildTiles` uses the projected count for `TileScroller.Width`.
- `CreateGroupTile`:
  - 44×44 Border, `Tag = "group:" + groupId`, tooltip `MemberNames`;
  - inner 34×34 Border, radius 8, translucent wash + 1px border;
  - 2×2 `UniformGrid`/Grid with 3px padding and 1px gap; render member icons
    at 11px via `IconRendererFactory.CreateByKey(WidgetKindRegistry.Find(member.Kind)?.IconKey, iconStyle).Render(...)`
    with the member colour;
  - if `MemberCount <= 4`, draw all; if 5+, draw the first 3 and a `+N`
    TextBlock in the fourth cell;
  - no dot, no count badge, no Folder glyph;
  - click toggles the member section (Task 4 will provide
    `ToggleGroupSection`; for this task raise an internal
    `GroupTileClicked(groupId)` event or no-op that Task 4 wires);
  - right-click raises `WidgetMenuRequested(groupId)`.
- open state: `_openStates` contains group ids for group tiles; a group tile
  gets a neutral 1.5px outline + lifted background, never a member colour.
  `ApplyTileOpenState` branches on `Tag` starting with `group:`.
- remove the old per-member `GroupBadge` path and its tests; update the
  `IslandGroupBadgeMarksMembers` / P0-B badge pins to the new group tile.

- [ ] **Step 4: Run**

```powershell
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\WidgetGroupsCheck
dotnet run --project tests\IslandLayoutCheck
dotnet run --project tests\XamlLoadCheck
```

Expected: one group tile per group, 2×2/+N, open outline, no count badge.

---

### Task 4: Inline expanded member section (the right extension of the capsule)

**Files:**
- Modify: `Controls/IslandWindow.xaml` / `.xaml.cs`
- Modify: `Core/IslandLayout.cs` (already has constants; add any missing math)
- Modify: `Core/Localization.cs` (new labels)
- Modify: `tests/IslandLayoutCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces:**
- `IslandWindow` internal seam for checks:
  - `bool IsGroupSectionOpenForChecks { get; }`
  - `string ExpandedGroupIdForChecks { get; }`
  - `void ToggleGroupSectionForChecks(string groupId)`
- `WidgetPinToggleRequested` event (string member id) for the manager.

- [ ] **Step 1: Write the failing UI pins**

In XamlLoadCheck:
- Build an island with `SetWidgets` for a 6-member group; find the group tile
  (`Tag` starts with `group:`), click it through the internal seam; assert:
  - `IsGroupSectionOpenForChecks` true and the expanded group id matches;
  - `MemberSection.Visibility == Visible`;
  - 6 member cells exist, 4 per row (2 rows);
  - the member section is part of the capsule (same `PillShape`, no extra
    `Window`/`Popup` in `Application.Current.Windows`);
  - clicking the group tile again collapses it;
  - clicking a member raises `WidgetActivated` with that member id and
    collapses the section.
- Pin the visual rules: group tile has a 2×2 inner grid; 5+ shows `+N`; no
  count badge and no Folder glyph; active member cell has a ring, inactive
  members do not; pinned cell shows the gold upright pin; unpinned cell has no
  persistent pin.
- `IslandLayoutCheck`: section width/height/rows/clamp (Task 3's math).

Run: red because there is no section.

- [ ] **Step 2: Restructure the island XAML for a left-anchored capsule**

Change `IslandBody`, `PillShape`, and the row to `HorizontalAlignment="Left"`;
put `IslandRow` and a new `MemberSection` in one horizontal `Grid`
(`CapsuleRow`) inside the capsule; set the body's left margin to
`ShadowPadding`. Keep one `PillShape` background/border/shadow. Add:

```xml
<Border x:Name="MemberSection" Width="312" Visibility="Collapsed"
        VerticalAlignment="Stretch" Background="Transparent">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="30"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <Border x:Name="MemberSeam" Width="1" HorizontalAlignment="Left"
                Margin="0,14,0,14" Background="{DynamicResource Kobold.Brush.IslandDivider}"/>
        <Grid x:Name="MemberHeader" Margin="12,0,12,0">...</Grid>
        <ScrollViewer x:Name="MemberScroller" Grid.Row="1" VerticalScrollBarVisibility="Auto"
                      HorizontalScrollBarVisibility="Disabled">
            <!-- Generated at 288 DIP wide: four 66px columns separated by
                 three 8px gap columns; rows are 76px plus 8px gaps. -->
            <Grid x:Name="MemberPanel" Width="288" Margin="12,0,0,12"/>
        </ScrollViewer>
    </Grid>
</Border>
```

`MemberHeader`: a 14×14 2×2 group mark, `成员`, `N 个 · P 已固定` meta, and an
`Esc` chip. Add localized keys `Island_GroupMembers`,
`Island_GroupMemberCountFormat`, `Island_GroupPinnedCountFormat`,
`Island_Esc` to en/zh/ja.

- [ ] **Step 3: Build the member cells**

`CreateMemberCell(WidgetGroupTileMember member, bool active)`:
- 66×76 cell placed in `MemberPanel`; icon plate 34×34 radius 9; 16px member icon;
- name 10.5px, one line, ellipsis, tooltip full name;
- active ring: a 38×38 `Border` with 1.5px member-colour border and 2px gap,
  visible only when `member.Id == group.ActiveWidgetId && group.IsOpen`;
- pin: a 20×20 hit target at top-right; pinned = gold upright 9px pin;
  unpinned = no persistent glyph, hover shows 45° gray; left click toggles pin
  (`WidgetPinToggleRequested`), right click toggles pin as well;
- click raises `WidgetActivated(member.Id)` and collapses the section.
- 4 columns: use the 288-wide generated Grid with 66-wide cells and 8px gaps.

- [ ] **Step 4: Animate the capsule extension**

- `ToggleGroupSection(groupId)`:
  - if the same group is open, collapse; if another, replace;
  - fill `MemberPanel` from `WidgetGroupTileInfo.Members`, `MemberHeader` meta;
  - set `MemberSection.Visibility = Visible`; animate `IslandBody`,
    `PillShape` and `CapsuleRow` width from base to
    `base + 1 + 312`; height from 72 to
    `max(72, GroupSectionHeight(count))`; fade the section in;
  - on collapse animate back, then `Visibility=Collapsed`.
- `UpdateWindowGeometry` reserves the max expanded capsule width and the max
  member-section height (3 rows), so the window is fixed-size; the content is
  left-anchored. `CenterX` returns the **base** capsule center (not the window
  center). `UpdateWindowGeometry` sets `Left` so the base capsule center is
  the saved `IslandX`; use `IslandLayout.ClampExpandedCenter` to keep the
  whole expanded capsule 16 DIP inside the work area. The pill drag's
  mouse-up clamp must use the same base-center/expanded-width math, not
  `Left + Width / 2`.
- Click outside / Esc:
  - start a `DispatcherTimer` (50ms) only while the section is open;
  - tick: if `GetAsyncKeyState(VK_ESCAPE)` is down, collapse; else if the
    left mouse button is down and the cursor is outside the expanded capsule,
    collapse. It must not block the outside click.
  - the `Esc` chip also collapses on click.
- Only one section open; group tile click toggles; clicking a member collapses.

- [ ] **Step 5: Wire the manager**

- `IslandWindow.WidgetPinToggleRequested` → manager:
  `data.IsPanelPinned = !data.IsPanelPinned; group.IsPanelPinned = AnyMemberPinned(members); SaveConfig(); host?.UpdateUI(); RefreshIsland();`
- `WidgetActivated(memberId)` already opens/switch through the manager; the
  island collapses the section after raising it.
- `SetWidgets` must update an open section in place when membership changes;
  never rebuild the island row for a same-group membership update.

- [ ] **Step 6: Run**

```powershell
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\IslandLayoutCheck
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\MenuRenderCheck
```

Expected: section opens/collapses, 4 columns, 6 = 2 rows, 9–12 = 3 rows,
clamp keeps 16 DIP, member pin/ring behavior matches the design.

---

### Task 5: Merge target pre-rendered preview

**Files:**
- Modify: `Controls/WidgetChrome.xaml` / `.xaml.cs`
- Modify: `Controls/WidgetWindowBase.cs`
- Modify: `Core/Localization.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`
- Modify: `tests/MenuRenderCheck/Program.cs` if the chrome visual pins live there

**Interfaces:**
- `WidgetChrome.ShowMergePreview(ImageSource preview, string label)`
- `WidgetChrome.HideMergePreview()`
- `WidgetWindowBase.SetMergeHint(bool on, ImageSource preview = null)`
- `WidgetWindowBase.MergePreviewFor(WidgetData source)` uses
  `WidgetGhostRenderer.Render(source)` and caches one bitmap per drag.

- [ ] **Step 1: Write the failing pin**

In XamlLoadCheck:

```csharp
private static void MergePreviewShowsTheDraggedComponent()
{
    var target = new TodoWidget(new TodoData { Id = "preview-target" });
    try
    {
        var preview = new WriteableBitmap(40, 30, 96, 96, PixelFormats.Pbgra32, null);
        target.SetMergeHint(true, preview);
        var chrome = ChromeOf(target);
        if (chrome.FindName("MergePreviewLayer") is Grid layer
            && layer.Visibility != Visibility.Visible)
            throw new InvalidOperationException("the merge preview layer is hidden");
        if (!ReferenceEquals(((Image)chrome.FindName("MergePreviewImage")).Source, preview))
            throw new InvalidOperationException("the merge preview image is not the rendered source");
        target.SetMergeHint(false);
        if (((Grid)chrome.FindName("MergePreviewLayer")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("the merge preview layer stayed visible");
    }
    finally
    {
        target.Close();
    }
}
```

Run: red (no layer / no overload).

- [ ] **Step 2: Add the chrome layer**

In `WidgetChrome.xaml`, add a `MergePreviewLayer` Grid (last child of the shell
grid, `IsHitTestVisible=False`, `Visibility=Collapsed`, `Grid.RowSpan=4`):
- translucent accent wash (`Kobold.Brush.AccentSoft`, opacity ~0.22);
- 2px accent border inside the shell;
- `Image x:Name="MergePreviewImage"` with `Stretch="Uniform"`,
  `Margin="16,80,16,16"`, `Opacity="0.85"`;
- a small pill with `TextBlock x:Name="MergePreviewLabel"` at the top center.
Add `ShowMergePreview`/`HideMergePreview` in the code-behind.

- [ ] **Step 3: Cache and pass the bitmap**

- `WidgetWindowBase.OnHeaderDragStarted` clears `_mergePreviewImage` and
  `_mergePreviewSource`.
- `SetMergeHintTarget(target)` passes `MergePreviewFor(Data)` to
  `next.SetMergeHint(true, preview)`.
- `SetMergeHint(bool on, ImageSource preview = null)` calls
  `Chrome.ShowMergePreview(preview, Localization.Get("UI_MergeDropHint"))` or
  `Chrome.HideMergePreview()`; keep the existing border hint. Change the
  early-return guard to `_mergeHint == on && ReferenceEquals(_mergePreviewImage, preview)`
  so a later preview image still paints after the border hint.
- If `WidgetGhostRenderer.Render` returns null, show only the accent border
  (no image), not an empty layer.
- Add `UI_MergeDropHint` to en/zh/ja: "松开以合并" / "Release to merge" /
  "ドロップして結合".

- [ ] **Step 4: Run**

```powershell
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\MenuRenderCheck
```

Expected: preview visible on a merge target, cleared on leave/release, and the
existing merge hint pins still pass.

---

### Task 6: Integration, docs and full regression

**Files:**
- Modify: `README.md`, `CHANGELOG.md`
- Modify: `tests/XamlLoadCheck/Program.cs` (integration pins only)
- Modify: `tests/WidgetGroupsCheck/Program.cs` if a pure pin is missing
- Modify: `tools/Run-Checks.ps1` only if a new check project is added (none expected)

- [ ] **Step 1: Add integration pins**

In XamlLoadCheck:
- a Folder+Todo+Note group renders one folded group tile; opening the island
  section shows all three; clicking Todo opens the group and switches to Todo;
- pin a member from the island section; close the group window; the group
  survives blur because one member is pinned; unpin it and the group closes;
- merge/detach repaints the group tile thumbnails without a row rebuild;
- legacy group-level pin config migrates to the active member.

In WidgetGroupsCheck: projection, pin helpers and legacy migration are pure.

- [ ] **Step 2: Docs**

Update `README.md` / `CHANGELOG.md`:
- island group tile + inline member section + per-member pin;
- tabs show member names;
- merge target preview;
- P2 is no longer deferred; island data projection changed.

- [ ] **Step 3: Full matrix**

```powershell
dotnet build Kobold.csproj -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group ci
# isolated APPDATA for the UI/config-pin group
$env:APPDATA = "<fresh temp dir>"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group checks
dotnet run --project tests\GroupUiSpike -- --check
```

Expected: build 0/0; ci 27/27; checks 34/34 with config-pin True;
GroupUiSpike 15/15 GREEN.

- [ ] **Step 4: Manual smoke (human)**

- Drag a Folder into a group; confirm the target preview appears before release.
- Open a 2/3/6-member group on the island; check 2×2/+N, no count badge, no
  Folder glyph, neutral open outline.
- Click the group tile; confirm the island capsule extends right, left edge
  fixed, 4 columns, member names, active ring, pins, `Esc`/outside click.
- Pin a member, blur the group window; confirm it stays open; unpin all and
  confirm it collapses.
- Switch tabs; confirm tabs show component names, not kind labels.
- Merge/detach/dissolve and restart; confirm the island projection and pins
  survive.

---

## Check Matrix

- `WidgetGroupsCheck`: projection, per-member pin, legacy pin migration,
  open projected ids.
- `IslandLayoutCheck`: 312 DIP section, rows/height, expanded clamp.
- `XamlLoadCheck`: tab names, group tile, member section, pin ring/glyph,
  merge preview, group integration.
- `MenuRenderCheck`: chrome preview layer structure.
- `StartupPanelsCheck`: legacy group pin migration does not double-restore.
- `Folder*Check`: unchanged, green.
- `GroupUiSpike`: unchanged 15/15.

## Self-Review Notes

- Spec coverage: P2 folded tile, inline section, per-member pin, click/outside
  rules, clamp, tabs, preview are each mapped to a task.
- The plan explicitly excludes a separate member panel/popup and any island
  data read from `WidgetGroup` inside `IslandWindow`.
- The only new structural data is the island projection; it is pure and
  tested before the UI consumes it.
