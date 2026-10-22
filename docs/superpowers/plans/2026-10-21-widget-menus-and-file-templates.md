# Widget 菜单、配色与新建文件格式实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **状态：已执行（2026-10-22）；最终终审使用官方 `deepseek/deepseek-flash`。**
> **Spec**：`docs/superpowers/specs/2026-10-21-widget-menus-and-file-templates-design.md`（本计划只引 §号，不复述决策）。
> **基线**：`main` @ `2538300`；worktree：`C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-widget-menus`；分支 `widget-menus`。
> **执行方式**：subagent-driven-development。每任务：实现子代理 TDD → 按 pathspec `git -C <worktree> add`（不提交）→ 控制者 `git -C <worktree> stash create` 快照 + 审查包 → 任务审查 → 修复 / 复审 → 复审通过后由实现子代理按计划信息提交。
> **子代理模型**：官方 `deepseek/deepseek-flash`。实现与审查同一模型。
> **子代理命令**：一律绝对路径 / `git -C <worktree>`；**禁止 `Set-Location`**。
> **会话位置**：控制者会话驻留 `C:\Users\31177347\Desktop\Coding`；所有工作树操作使用绝对路径。

**Goal：** 把笔记 `▾` 与 island `＋` 统一成同一套 anchored launcher 卡片；Widget / 文件夹换色统一成居中 3×3 色盘；Widget 内输入框跟组件色；新建文件追加 docx/xlsx/pptx 并写最小有效 OOXML。

**Architecture：** 共享 `LauncherCard` 表面 + `Kobold.Style.LauncherItem` 行样式；island `＋` 从 ContextMenu 改为 Popup；`ColorPaletteDialog` 抽自 Widget 换色并供文件夹复用；`DialogFactory.BuildInput` 暴露可测的跟色对话框；`Core/FileTemplates` 统一空文件 / Office 包创建。右键命令菜单仍用全局 ContextMenu。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；`System.IO.Compression`（框架自带）；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵 32 行不变）。

---

## Global Constraints

- C# 7.3 / net48；零新 NuGet 依赖；构建 0 警告。
- TDD：每任务先写失败 pin（编译失败或运行时失败均可）→ 最小实现 → 相关自检全绿；红/绿输出写进执行记录。
- 不新增 check 工程；矩阵维持 30 check + config-pin + installer = 32。
- 自检内**禁止读写系统剪贴板**、**禁止驱动真实鼠标**；真实键盘/弹窗边界交冒烟。
- 时间戳夹具一律显式传入；断言不得依赖 `DateTime.Now`。
- 三语 key 同步（zh/en/ja 同一提交）；`LangCheck` 保持绿。
- hex 字面量只许在 `Core/UiTokens.cs`；色盘颜色只来自 `FolderPalette` / `FolderIconPalette`。
- 每个提交独立可编译、可回退；先任务审查后提交。
- 合并 / 推送 / 部署必须先问用户。
- 子代理命令一律绝对路径 / `git -C <worktree>`；禁止 `Set-Location`。
- `Resources/blank.pptx` 是一次性生成的二进制资源；构建不调用 Office COM。

## 任务依赖图

```
T1 共享 Launcher 表面 + 行样式 + 笔记 ▾（独立）
T2 island ＋ Popup（依赖 T1）
T3 共享色盘 + Widget 换色（独立，复用 T1 的样式可）
T4 文件夹换色入口 + 输入框跟色（依赖 T3）
T5 Office 模板 + 新建文件路径（独立）
T6 文档 + 全量自检 + 执行记录
```

---

## Task 1: 共享 Launcher 表面 + 行样式 + 笔记 ▾

**Files：**
- Create: `Helpers/LauncherCard.cs`
- Modify: `Themes/Controls.xaml`
- Modify: `Controls/NotePanelView.xaml`
- Modify: `Controls/NotePanelView.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces（T2/T3 消费）：
  ```csharp
  public static class LauncherCard
  {
      public static Border Create(UIElement content, double width = double.NaN);
  }
  ```
- XAML 资源 key：`Kobold.Style.LauncherItem`（TargetType Button）。

### Step 1: 红 pin

In `XamlLoadCheck` `Main`, near the note pins (after `NoteTitleMenuGroupsActionsAndNotes` registration), add:
```csharp
failures += TryCheck("launcher: the note menu wears the shared card", NoteTitleMenuUsesTheLauncherStyle);
```
Add the method near the note helpers:
```csharp
private static void NoteTitleMenuUsesTheLauncherStyle()
{
    ThemeManager.Initialize("dark");
    var data = new NoteData { Name = "Notebook" };
    data.Notes.Add(NewNote("a", "Alpha", new DateTime(2026, 3, 5, 9, 0, 0)));

    var view = new NoteWidget(data).PanelView;
    view.Refresh();

    var menu = view.BuildTitleMenuContent() as Border;
    if (menu == null || !"NoteTitleMenu".Equals(menu.Tag as string))
        throw new InvalidOperationException("the title menu is not a tagged Border");
    if (Math.Abs(menu.CornerRadius.TopLeft - UiTokens.RadiusWindow) > 0.01)
        throw new InvalidOperationException("the title menu radius is " + menu.CornerRadius.TopLeft);
    if (!ReferenceEquals(menu.Background, ThemeManager.SurfaceBrush))
        throw new InvalidOperationException("the title menu does not wear the surface brush");

    var style = Application.Current.FindResource("Kobold.Style.LauncherItem") as Style;
    if (style == null) throw new InvalidOperationException("no Kobold.Style.LauncherItem resource");

    var newButton = TitleMenuPart(menu, "NoteMenuNew") as Button;
    if (newButton == null || !ReferenceEquals(newButton.Style, style))
        throw new InvalidOperationException("the new action does not wear the launcher style");
    var entry = NoteMenuEntries(menu)[0];
    if (!ReferenceEquals(entry.Style, style))
        throw new InvalidOperationException("a note entry does not wear the launcher style");
}
```

Run:
```
dotnet run --project "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-widget-menus\tests\XamlLoadCheck\XamlLoadCheck.csproj" -c Debug --nologo
```
Expected: fail (`Kobold.Style.LauncherItem` missing / radius 8). Record.

### Step 2: `Helpers/LauncherCard.cs`

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Kobold.Core;

namespace Kobold.Helpers
{
    /// <summary>The shared floating surface behind click-opened launchers.</summary>
    public static class LauncherCard
    {
        public static Border Create(UIElement content, double width = double.NaN)
        {
            var card = new Border
            {
                Background = ThemeManager.SurfaceBrush,
                BorderBrush = ThemeManager.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTokens.RadiusWindow),
                Padding = new Thickness(4),
                Child = content,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 5,
                    Opacity = 0.4,
                    Color = Colors.Black,
                    RenderingBias = RenderingBias.Quality
                }
            };
            if (!double.IsNaN(width) && width > 0) card.Width = width;
            return card;
        }
    }
}
```

### Step 3: `Kobold.Style.LauncherItem` in `Themes/Controls.xaml`

Add after the existing button styles (before `</ResourceDictionary>`):
```xml
<Style x:Key="Kobold.Style.LauncherItem" TargetType="{x:Type Button}">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="Foreground" Value="{DynamicResource Kobold.Brush.TextPrimary}"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Padding" Value="8,6"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="HorizontalContentAlignment" Value="Left"/>
    <Setter Property="HorizontalAlignment" Value="Stretch"/>
    <Setter Property="SnapsToDevicePixels" Value="True"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type Button}">
                <Border x:Name="Bd"
                        CornerRadius="8"
                        Background="{TemplateBinding Background}"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource Kobold.Brush.SurfaceHover}"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource Kobold.Brush.ControlBackground}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter TargetName="Bd" Property="Opacity" Value="0.5"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

### Step 4: `NotePanelView` wiring

- `Controls/NotePanelView.xaml`: delete the whole `<Style x:Key="NoteMenuButtonStyle" ...>` resource block
  (the `UserControl.Resources` becomes empty; remove the element).
- `Controls/NotePanelView.xaml.cs`:
  - In `BuildTitleMenuContent`, replace the root `var root = new Border { ... }` with:
    ```csharp
    var root = LauncherCard.Create(panel,
        Math.Max(MENU_MIN_WIDTH, Math.Min(MENU_MAX_WIDTH, ActualWidth - 24)));
    root.Tag = "NoteTitleMenu";
    ```
    Remove the old `Width`, `Padding`, `Background`, `BorderBrush`, `BorderThickness`,
    `CornerRadius`, `Child` assignments.
  - In `BuildTitleMenuAction` / `BuildTitleMenuEntry`:
    ```csharp
    Style = FindResource("Kobold.Style.LauncherItem") as Style
    ```
    (replace `NoteMenuButtonStyle`); add `MinHeight = 32` for actions, `MinHeight = 34` for entries.
  - Keep the check column width 18 and the current row background.
  - Keep the Esc `PreviewKeyDown` handler and all tags.

### Step 5: 绿 + 提交

Run XamlLoadCheck; expect the new pin + all existing note pins PASS. Build 0 warnings.

```
git -C "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-widget-menus" add Helpers/LauncherCard.cs Themes/Controls.xaml Controls/NotePanelView.xaml Controls/NotePanelView.xaml.cs tests/XamlLoadCheck/Program.cs
git -C "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-widget-menus" commit -m "feat(ui): share the launcher card with the note title menu"
```

---

## Task 2: island `＋` 三行 launcher

**Files：**
- Modify: `Controls/IslandWindow.xaml.cs`
- Modify: `Core/Localization.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Consumes: Task 1 的 `LauncherCard` / `Kobold.Style.LauncherItem`.
- Produces: `public FrameworkElement BuildAddWidgetContent()`（供 pin）。

### Step 1: 红 pin

In `XamlLoadCheck` `Main`, after the island pins, add:
```csharp
failures += TryCheck("launcher: the island add menu is a three-row card", IslandAddMenuUsesTheLauncherCard);
```
Method:
```csharp
private static void IslandAddMenuUsesTheLauncherCard()
{
    ThemeManager.Initialize("dark");
    var island = new IslandWindow();
    var card = island.BuildAddWidgetContent() as Border;
    if (card == null || !"IslandAddMenu".Equals(card.Tag as string))
        throw new InvalidOperationException("the island add menu is not a tagged Border");
    if (Math.Abs(card.CornerRadius.TopLeft - UiTokens.RadiusWindow) > 0.01)
        throw new InvalidOperationException("the island add menu radius is " + card.CornerRadius.TopLeft);

    var header = FindByTag(card, "IslandAddHeader") as TextBlock;
    if (header == null || header.Text != Localization.Get("Island_NewWidgetHeader"))
        throw new InvalidOperationException("the island add menu has no localized header");

    var style = Application.Current.FindResource("Kobold.Style.LauncherItem") as Style;
    foreach (var id in new[] { "folder", "todo", "note" })
    {
        var row = FindByTag(card, id) as Button;
        if (row == null) throw new InvalidOperationException("no " + id + " row");
        if (!ReferenceEquals(row.Style, style))
            throw new InvalidOperationException("the " + id + " row does not wear the launcher style");
        if (row.Content == null) throw new InvalidOperationException("the " + id + " row has no content");
    }
}
```
Add a helper:
```csharp
private static FrameworkElement FindByTag(DependencyObject root, string tag)
{
    if (root == null) return null;
    var element = root as FrameworkElement;
    if (element != null && tag.Equals(element.Tag as string)) return element;
    int count = VisualTreeHelper.GetChildrenCount(root);
    for (int i = 0; i < count; i++)
    {
        var found = FindByTag(VisualTreeHelper.GetChild(root, i), tag);
        if (found != null) return found;
    }
    return null;
}
```
Run XamlLoadCheck → compile fail (`BuildAddWidgetContent` missing). Record.

### Step 2: 三语 key

In `Core/Localization.cs`, add to each dictionary (near the island keys):
```csharp
["Island_NewWidgetHeader"] = "New widget",          // zh: 新建组件; ja: 新規コンポーネント
["Island_NewFolderHint"] = "Collect files and shortcuts", // zh: 收集文件与快捷方式; ja: ファイルとショートカットを集める
["Island_NewTodoHint"] = "Calendar and checklist",  // zh: 日历与清单; ja: カレンダーとチェックリスト
["Island_NewNoteHint"] = "Markdown notes",          // zh: Markdown 笔记; ja: Markdown ノート
```
Write the actual three-language values (do not use English for zh/ja).

### Step 3: island code

In `Controls/IslandWindow.xaml.cs`:

1. Add `using System.Windows.Controls.Primitives;` (Popup/PlacementMode).
2. Add field:
```csharp
private Popup _addWidgetPopup;
```
3. Replace `ShowAddWidgetMenu` with:
```csharp
private void ShowAddWidgetMenu()
{
    if (_addWidgetPopup != null)
    {
        CloseAddWidgetMenu();
        return;
    }

    var content = BuildAddWidgetContent();
    var popup = new Popup
    {
        PlacementTarget = AddTile,
        Placement = PlacementMode.Bottom,
        StaysOpen = false,
        AllowsTransparency = true,
        Child = content,
        HorizontalOffset = -(280 - AddTile.ActualWidth)
    };
    popup.Closed += (s, e) =>
    {
        if (ReferenceEquals(_addWidgetPopup, popup)) _addWidgetPopup = null;
        ResumeAutoCollapse();
    };

    _addWidgetPopup = popup;
    PauseAutoCollapse();
    popup.IsOpen = true;

    content.Focusable = true;
    content.Focus();
}

private void CloseAddWidgetMenu()
{
    var popup = _addWidgetPopup;
    if (popup == null) return;
    _addWidgetPopup = null;
    popup.IsOpen = false;
}
```
4. Add:
```csharp
public FrameworkElement BuildAddWidgetContent()
{
    var panel = new StackPanel { Margin = new Thickness(4) };
    panel.Children.Add(new TextBlock
    {
        Tag = "IslandAddHeader",
        Text = Localization.Get("Island_NewWidgetHeader"),
        FontSize = 11,
        Foreground = ThemeManager.SecondaryTextBrush,
        Margin = new Thickness(8, 5, 8, 5)
    });

    string iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic";
    foreach (var kind in WidgetKindRegistry.All)
    {
        string kindId = kind.Id;
        var iconCanvas = new Canvas { Width = 24, Height = 24 };
        IconRendererFactory.CreateByKey(kind.IconKey, iconStyle)
            .Render(iconCanvas, UiTokens.DefaultFolderColor);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = Localization.Get(kind.NameKey),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.TextBrush
        });
        text.Children.Add(new TextBlock
        {
            Text = Localization.Get(HintKey(kindId)),
            FontSize = 11,
            Foreground = ThemeManager.SecondaryTextBrush
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconCanvas, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(iconCanvas);
        grid.Children.Add(text);

        var row = new Button
        {
            Tag = kindId,
            Content = grid,
            MinHeight = 48,
            Style = FindResource("Kobold.Style.LauncherItem") as Style
        };
        row.Click += (s, e) =>
        {
            CloseAddWidgetMenu();
            WidgetManager.Instance.CreateWidget(kindId);
        };
        panel.Children.Add(row);
    }

    var card = LauncherCard.Create(panel, 280);
    card.Tag = "IslandAddMenu";
    card.PreviewKeyDown += (s, e) =>
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseAddWidgetMenu();
    };
    return card;
}

private static string HintKey(string kindId)
{
    switch (kindId)
    {
        case "folder": return "Island_NewFolderHint";
        case "todo": return "Island_NewTodoHint";
        case "note": return "Island_NewNoteHint";
        default: return string.Empty;
    }
}
```
- Remove the old `ShowAddWidgetMenu` ContextMenu body (the `MenuBuilder.Prepare` / `BuildKindChoices` block). `BuildKindChoices` remains for the tray.
- `CloseAddWidgetMenu` on `Closed`? The `Closed` handler nulls the field and resumes; `CloseAddWidgetMenu` nulls before `IsOpen=false`, so the `Closed` handler sees `_addWidgetPopup` not the popup but still calls `ResumeAutoCollapse`. Good.
- `CloseAddWidgetMenu` idempotent.

### Step 4: 绿 + 提交

Run XamlLoadCheck + LangCheck; expect all PASS. Build 0 warnings.

```
git -C "..." add Controls/IslandWindow.xaml.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs
git -C "..." commit -m "feat(island): open a launcher card from the add tile"
```

---

## Task 3: 共享色盘 + Widget 换色

**Files：**
- Create: `Helpers/ColorPaletteDialog.cs`
- Modify: `Controls/WidgetWindowBase.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces（T4 消费）：
  ```csharp
  public static class ColorPaletteDialog
  {
      public static Window Build(string title, IReadOnlyList<string> palette, string current,
          bool allowDefault, string defaultText, Action<string> onPick, Action onDefault);
      public static void Show(Window owner, string title, IReadOnlyList<string> palette, string current,
          bool allowDefault, string defaultText, Action<string> onPick, Action onDefault);
  }
  ```

### Step 1: 红 pin

In `XamlLoadCheck` `Main`, add:
```csharp
failures += TryCheck("palette: the shared palette has nine swatches and a current ring", SharedPaletteHasNineSwatches);
failures += TryCheck("palette: the folder palette shows the default button", FolderPaletteShowsDefault);
```
Methods:
```csharp
private static void SharedPaletteHasNineSwatches()
{
    ThemeManager.Initialize("dark");
    var window = ColorPaletteDialog.Build("选择颜色", UiTokens.FolderPalette, "#3B82F6",
        false, null, c => { }, () => { });
    var root = window.Content as Border;
    if (root == null) throw new InvalidOperationException("the palette has no Border content");

    int swatches = 0;
    foreach (var color in UiTokens.FolderPalette)
    {
        var swatch = FindByTag(root, color) as Border;
        if (swatch == null) throw new InvalidOperationException("no swatch for " + color);
        swatches++;
    }
    if (swatches != 9) throw new InvalidOperationException("the palette has " + swatches + " swatches");

    var current = FindByTag(root, "#3B82F6") as Border;
    if (current == null || current.BorderThickness.Left != 3)
        throw new InvalidOperationException("the current swatch has no ring");

    var caption = FindByTag(root, "PaletteCaption") as TextBlock;
    if (caption == null || caption.Text != Localization.Get("Color_Blue"))
        throw new InvalidOperationException("the caption does not name the current color");

    if (FindByTag(root, "PaletteDefault") != null)
        throw new InvalidOperationException("a widget palette shows the default button");
}

private static void FolderPaletteShowsDefault()
{
    ThemeManager.Initialize("dark");
    var window = ColorPaletteDialog.Build("文件夹颜色", UiTokens.FolderIconPalette, null,
        true, Localization.Get("Menu_FolderColorDefault"), c => { }, () => { });
    var root = window.Content as Border;
    if (FindByTag(root, "PaletteDefault") == null)
        throw new InvalidOperationException("the folder palette has no default button");
}
```
`FindByTag` is the helper from Task 2. Run XamlLoadCheck → compile fail (`ColorPaletteDialog` missing). Record.

### Step 2: `Helpers/ColorPaletteDialog.cs`

Implement:
- `Build` creates a `Window` (no `ShowDialog`), sets `WindowStartupLocation=CenterScreen`, `WindowStyle=None`,
  `AllowsTransparency=true`, `Background=Brushes.Transparent`, `ResizeMode=NoResize`, `ShowInTaskbar=false`,
  `Width=360`, `SizeToContent=SizeToContent.Height`.
- `Border` content (DialogBackground / DialogBorder / RadiusWindow / shadow).
- `StackPanel`:
  - header `Grid` with title TextBlock (Tag `PaletteTitle`, 13 semibold) and close TextBlock `×`
    (Tag `PaletteClose`, 14 muted, Cursor Hand, `MouseLeftButtonUp` → Close);
  - `UniformGrid Columns=3` (or 3×3 Grid) with 9 swatches:
    - `Border` 52×52, Margin 7, CornerRadius 9, Background color, Tag color, Cursor Hand;
    - current: BorderBrush = `ThemeManager.TextBrush`, BorderThickness = 3, child TextBlock `✓`
      (Tag `PaletteCheck`, Foreground = `ThemeManager.TextBrush`, centered);
    - MouseEnter: Opacity 0.85; caption.Text = name; MouseLeave: Opacity 1; caption.Text = currentName;
    - MouseLeftButtonUp: `onPick(color); window.Close();`
  - caption TextBlock Tag `PaletteCaption`, 11 muted, text = current color name or empty;
  - footer: if `allowDefault`, `Button` Tag `PaletteDefault`, Style `Kobold.Style.SecondaryButton`,
    Content `defaultText`, Click → `onDefault(); window.Close();`; else null.
- `PreviewKeyDown` Esc → Close.
- `Show` sets `window.Owner = owner`, `WindowStartupLocation = owner != null ? CenterOwner : CenterScreen`,
  `ShowDialog()`.
- Color name: `UiTokens.FolderIconPaletteNames[i]`; guard index < palette.Count.
- Use `Utils.HexToColor` for the brush; freeze.
- No new hex; `ThemeManager.TextBrush` for ring/check.

`WidgetWindowBase.BeginColorChange`:
```csharp
public void BeginColorChange()
{
    RunModal(() => ColorPaletteDialog.Show(
        this,
        Localization.Get("Dialog_PickColor"),
        UiTokens.FolderPalette,
        _data.Color,
        allowDefault: false,
        defaultText: null,
        onPick: color =>
        {
            _data.Color = color;
            RefreshTheme();
            OnDataChanged?.Invoke();
        },
        onDefault: null));
}
```
Delete `CreateColorSwatch` and any now-unused usings (careful: `DropShadowEffect` still used elsewhere in the file; check).

### Step 3: 绿 + 提交

Run XamlLoadCheck (palette pins + existing WidgetChrome pins), build 0 warnings.

```
git -C "..." add Helpers/ColorPaletteDialog.cs Controls/WidgetWindowBase.cs tests/XamlLoadCheck/Program.cs
git -C "..." commit -m "feat(ui): share one color palette for widgets and folders"
```

---

## Task 4: 文件夹换色入口 + 输入框跟色

**Files：**
- Modify: `Controls/FolderWidget.MenuActions.cs`
- Modify: `Helpers/DialogFactory.cs`
- Modify: `Controls/WidgetWindowBase.cs`
- Modify: `tests/MenuRenderCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Consumes: Task 3 的 `ColorPaletteDialog`.
- Produces: `FolderWidget.CreateFolderColorEntry`（单入口）；`DialogFactory.BuildInput`.

### Step 1: 红 pins

**`MenuRenderCheck`** currently reflects `CreateFolderColorMenu`; rewrite the main block:
```csharp
var widget = new FolderWidget(new FolderData());
var factory = typeof(FolderWidget).GetMethod("CreateFolderColorEntry",
    BindingFlags.Instance | BindingFlags.NonPublic);
if (factory == null) { Console.WriteLine("TOP FAIL: CreateFolderColorEntry not found"); return 1; }

var item = new DisplayItem { Path = tempFolder, IsDirectory = true };
var entry = factory.Invoke(widget, new object[] { item }) as MenuItem;
if (entry == null) { Console.WriteLine("TOP FAIL: the folder color entry was not built"); return 1; }

FolderColorEntryIsSingle(entry);
FolderColorEntryPaintsItsSwatch(entry);
```
Replace `SwatchesArePainted` / `ResetEntryIsLabelled` with:
```csharp
private static void FolderColorEntryIsSingle(MenuItem entry)
{
    if (entry.Items.Count != 0)
        Errors.Add("the folder color entry has a submenu");
    if (!"FolderColorEntry".Equals(entry.Tag as string))
        Errors.Add("the folder color entry is not tagged");
    var header = entry.Header as StackPanel;
    if (header == null || header.Children.Count < 2)
        Errors.Add("the folder color entry header is not a swatch + label");
}

private static void FolderColorEntryPaintsItsSwatch(MenuItem entry)
{
    var header = entry.Header as StackPanel;
    var swatch = header == null ? null : header.Children[0] as System.Windows.Shapes.Rectangle;
    if (swatch == null || swatch.Width < 16 || swatch.Height < 16)
        Errors.Add("the folder color entry has no 16px swatch");
    var label = header == null ? null : header.Children[1] as TextBlock;
    if (label == null || label.Text.IndexOf(Localization.Get("Menu_FolderColor"), StringComparison.Ordinal) < 0)
        Errors.Add("the folder color entry has no localized label");
}
```
Add `using Localization = Kobold.Core.Localization;` if missing. `MenuRenderCheck` already uses `Kobold.Core`; add alias.

**`XamlLoadCheck`** add:
```csharp
failures += TryCheck("input: the dialog follows the widget color", InputDialogFollowsWidgetColor);
```
Method:
```csharp
private static void InputDialogFollowsWidgetColor()
{
    ThemeManager.Initialize("dark");
    var window = DialogFactory.BuildInput(null, "t", "p", "x", null, "#EF4444", v => { });
    var accent = window.Resources[ThemeManager.BrushKeyPrefix + "Accent"] as SolidColorBrush;
    if (accent == null || accent.Color != Utils.HexToColor("#EF4444"))
        throw new InvalidOperationException("the input dialog did not take the widget accent");

    var plain = DialogFactory.BuildInput(null, "t", "p", "x", null, null, v => { });
    if (plain.Resources.Contains(ThemeManager.BrushKeyPrefix + "Accent"))
        throw new InvalidOperationException("a null widget color wrote an accent override");
}
```
Run both checks → compile fail (`BuildInput` / `CreateFolderColorEntry` missing). Record.

### Step 2: `DialogFactory.BuildInput`

Refactor `ShowInput`:
```csharp
public static string ShowInput(Window owner, string title, string prompt, string initialValue,
    Func<string, string> validate = null, string widgetColor = null)
{
    string result = null;
    Window dialog = null;
    dialog = BuildInput(owner, title, prompt, initialValue, validate, widgetColor,
        value => { result = value; dialog.Close(); });
    dialog.ShowDialog();
    return result;
}

public static Window BuildInput(Window owner, string title, string prompt, string initialValue,
    Func<string, string> validate, string widgetColor, Action<string> onAccept)
{
    var dialog = new Window { ... existing ... };
    // existing textBox / error / accept / buttons / content
    Action accept = () =>
    {
        string value = textBox.Text;
        string error = validate == null ? null : validate(value);
        if (error != null) { ...; return; }
        onAccept?.Invoke(value);
    };
    // ... existing OK/Cancel/Enter wiring ...
    dialog.Content = new Border { ... existing ... Child = content };
    ApplyWidgetColor(dialog, widgetColor);
    dialog.Loaded += (s, e) => { textBox.Focus(); textBox.SelectAll(); };
    return dialog;
}
```
- The existing `ShowInput` body becomes `BuildInput`; move the `dialog.ShowDialog(); return result;` out.
- `ApplyWidgetColor` is already in `DialogFactory`; call it after `Content` is set.
- `WidgetWindowBase.ShowInputModal`:
  ```csharp
  result = DialogFactory.ShowInput(this, title, prompt, initialValue, validate,
      _data == null ? null : _data.Color);
  ```

### Step 3: `FolderWidget.MenuActions`

- Add `CreateFolderColorEntry`, `CurrentFolderColor`, `OpenFolderColorPalette` exactly as spec §5.3.
- Replace both `menu.Items.Add(CreateFolderColorMenu(item));` with `CreateFolderColorEntry(item)`.
- Delete `CreateFolderColorMenu`; keep `IconBelongsTo`.
- `OpenFolderColorPalette` uses `RunModal` + `ColorPaletteDialog.Show`.
- Ensure `using System.Windows.Shapes;` for `Rectangle` (or use fully qualified).

### Step 4: 绿 + 提交

Run `MenuRenderCheck`, `XamlLoadCheck`, `LangCheck`, build. Expect all PASS.

```
git -C "..." add Controls/FolderWidget.MenuActions.cs Helpers/DialogFactory.cs Controls/WidgetWindowBase.cs tests/MenuRenderCheck/Program.cs tests/XamlLoadCheck/Program.cs
git -C "..." commit -m "feat(folder): open the shared palette and tint input dialogs"
```

---

## Task 5: Office 模板 + 新建文件路径

**Files：**
- Create: `Core/FileTemplates.cs`
- Create: `Resources/blank.pptx`（一次性生成，二进制）
- Modify: `Core/NewFileName.cs`
- Modify: `Controls/FolderWidget.MenuActions.cs`
- Modify: `Kobold.csproj`
- Modify: `.gitignore`（允许 `Resources/blank.pptx`；`.gitignore` 默认忽略 `*.pptx`）
- Modify: `tests/ShellOpsCheck/Program.cs`
- Modify: `tests/ShellOpsCheck/ShellOpsCheck.csproj`

**Interfaces：**
- Produces:
  ```csharp
  public static class FileTemplates
  {
      public static bool IsOfficeExtension(string extension);
      public static bool TryCreate(string path, out string error);
      public static byte[] BuildOfficePackage(string extension);
  }
  ```

### Step 0: `Resources/blank.pptx`

The controller generates it once with PowerPoint COM (not in the build):
```powershell
$ppt = New-Object -ComObject PowerPoint.Application
$pres = $ppt.Presentations.Add()
while ($pres.Slides.Count -gt 0) { $pres.Slides.Item(1).Delete() }
$pres.Slides.Add(1, 12) # ppLayoutBlank
$pres.SaveAs("C:\...\Resources\blank.pptx", 24) # ppSaveAsOpenXMLPresentation
$pres.Close()
$ppt.Quit()
```
Verify size > 0 and the zip contains `ppt/presentation.xml`.

### Step 1: 红 tests（`ShellOpsCheck`）

Add to `Main`:
```csharp
OfficeFileFormats();
```
Method:
```csharp
private static void OfficeFileFormats()
{
    var exts = NewFileName.CommonExtensions;
    Check(exts[exts.Length - 3] == ".docx" && exts[exts.Length - 2] == ".xlsx" && exts[exts.Length - 1] == ".pptx",
        "new file: the extension list ends with docx/xlsx/pptx");

    Check(HasPart(FileTemplates.BuildOfficePackage(".docx"), "[Content_Types].xml") &&
          HasPart(FileTemplates.BuildOfficePackage(".docx"), "_rels/.rels") &&
          HasPart(FileTemplates.BuildOfficePackage(".docx"), "word/document.xml"),
        "new file: docx carries its minimal parts");
    Check(HasPart(FileTemplates.BuildOfficePackage(".xlsx"), "xl/workbook.xml") &&
          HasPart(FileTemplates.BuildOfficePackage(".xlsx"), "xl/worksheets/sheet1.xml"),
        "new file: xlsx carries its minimal parts");
    Check(HasPart(FileTemplates.BuildOfficePackage(".pptx"), "ppt/presentation.xml") &&
          HasPart(FileTemplates.BuildOfficePackage(".pptx"), "ppt/slides/slide1.xml"),
        "new file: pptx carries its minimal parts");

    string dir = Path.Combine(Path.GetTempPath(), "kobold-office-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        string error;
        Check(FileTemplates.TryCreate(Path.Combine(dir, "a.docx"), out error) &&
              new FileInfo(Path.Combine(dir, "a.docx")).Length > 0,
            "new file: TryCreate writes a docx");
        Check(FileTemplates.TryCreate(Path.Combine(dir, "b.txt"), out error) &&
              new FileInfo(Path.Combine(dir, "b.txt")).Length == 0,
            "new file: a text file stays empty");
        Check(!FileTemplates.TryCreate(Path.Combine(dir, "a.docx"), out error),
            "new file: TryCreate never overwrites");
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

private static bool HasPart(byte[] package, string name)
{
    if (package == null) return false;
    using (var stream = new MemoryStream(package))
    using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
    {
        return zip.GetEntry(name) != null;
    }
}
```
`ShellOpsCheck.csproj` add:
```xml
<Reference Include="System.IO.Compression" />
<Reference Include="System.IO.Compression.FileSystem" />
```
Run → compile fail (`FileTemplates` missing). Record.

### Step 2: `Core/FileTemplates.cs`

Implement:
- `IsOfficeExtension`: `.docx` / `.xlsx` / `.pptx` case-insensitive.
- `TryCreate`: `Path.GetExtension(path)` → office `BuildOfficePackage` + `File.WriteAllBytes`;
  else `FileStream(FileMode.CreateNew)`; catch → error.
- `BuildOfficePackage`:
  - `.docx`: zip `[Content_Types].xml`, `_rels/.rels`, `word/document.xml` (empty `w:p`).
  - `.xlsx`: `[Content_Types].xml`, `_rels/.rels`, `xl/workbook.xml`,
    `xl/_rels/workbook.xml.rels`, `xl/worksheets/sheet1.xml`.
  - `.pptx`: read `Kobold.Resources.blank.pptx` from `typeof(FileTemplates).Assembly`;
    return bytes or null.
  - `AddEntry(zip, name, xml)` with `UTF8Encoding(false)`.

Use these exact minimal XML constants:

```csharp
private const string DocxContentTypes =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
    "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
    "</Types>";

private const string DocxRels =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
    "</Relationships>";

private const string DocxDocument =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
    "<w:body><w:p/></w:body></w:document>";

private const string XlsxContentTypes =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
    "</Types>";

private const string XlsxRels =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
    "</Relationships>";

private const string XlsxWorkbook =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
    "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
    "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

private const string XlsxWorkbookRels =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
    "</Relationships>";

private const string XlsxSheet =
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
    "<sheetData/></worksheet>";
```

`BuildOfficePackage` switches on the lower-case extension and writes the matching entries:
`.docx` → `[Content_Types].xml`/DocxContentTypes, `_rels/.rels`/DocxRels,
`word/document.xml`/DocxDocument; `.xlsx` → the five Xlsx parts above.
- `Kobold.csproj`: add
  ```xml
  <Reference Include="System.IO.Compression" />
  <Reference Include="System.IO.Compression.FileSystem" />
  ...
  <EmbeddedResource Include="Resources\blank.pptx" />
  ```

### Step 3: 扩展名 + 创建路径

- `Core/NewFileName.cs`: `CommonExtensions` append `.docx`, `.xlsx`, `.pptx`.
- `Controls/FolderWidget.MenuActions.cs`:
  - `CreateNewFile`:
    ```csharp
    string filePath = System.IO.Path.Combine(storage, fileName);
    string error;
    if (!FileTemplates.TryCreate(filePath, out error))
    {
        ShowMessage($"Error creating file: {error}");
        return;
    }
    ```
    remove `File.WriteAllText`.
  - `CreateBrowseTextFile`: replace `ShellFileOperations.CreateTextFile` with
    `FileTemplates.TryCreate(Path.Combine(directory, name), out error)`; keep `Dialog_CreateFailed`.
- Keep `ShellFileOperations.CreateTextFile` untouched.

### Step 4: 绿 + 提交

Run `ShellOpsCheck`, build. Expect all PASS.

```
git -C "..." add Core/FileTemplates.cs Core/NewFileName.cs Controls/FolderWidget.MenuActions.cs Kobold.csproj .gitignore tests/ShellOpsCheck/Program.cs tests/ShellOpsCheck/ShellOpsCheck.csproj Resources/blank.pptx
git -C "..." commit -m "feat(file): create minimal Office documents from the new-file dialog"
```

---

## Task 6: 文档 + 全量自检 + 执行记录

**Files：**
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/plans/2026-10-21-widget-menus-and-file-templates.md`
- Modify: `docs/superpowers/specs/2026-10-21-widget-menus-and-file-templates-design.md`（状态 → 已执行）

### Steps

1. README EN/ZH:
   - Note `▾` and island `＋` share the launcher card; island shows icon/name/description.
   - Widget/folder color uses one 3×3 palette; folder entry "Folder color…".
   - Input dialogs follow the widget color.
   - New file extensions include `.docx` / `.xlsx` / `.pptx`.
2. CHANGELOG `## Unreleased`:
   - `### 新增`: Office file formats + templates.
   - `### 变化`: launcher/palette/input tint consistency.
3. Spec status → `已执行（2026-10-21）`.
4. Append execution record with actual commits, red/green evidence, 32/32.
5. Run full:
   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-widget-menus\tools\Run-Checks.ps1" -Group all
   ```
   Expect 32/32; no config-pin failure.
6. Commit:
   ```
   git -C "..." add README.md CHANGELOG.md docs/superpowers/plans/2026-10-21-widget-menus-and-file-templates.md docs/superpowers/specs/2026-10-21-widget-menus-and-file-templates-design.md
   git -C "..." commit -m "docs: document the widget menus and file templates"
   ```
7. Report; ask before merge/push/deploy.

---

## 执行记录（2026-10-22）

- 基线：`main` @ `2538300`；worktree `C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-widget-menus`；分支 `widget-menus`。
- Task 1 `707eabf` `feat(ui): share the launcher card with the note title menu`。
  - `LauncherCard` + `Kobold.Style.LauncherItem`；笔记 `▾` 根 Border 换成共享卡片；局部 `NoteMenuButtonStyle` 删除。
  - 验证：build 0 警告；`XamlLoadCheck` **287 PASS**。
  - 审查通过；遗留 minor（勾选列 16 vs 设计 18、阴影/边框未 pin、popup 阴影裁切风险、RED 证据二手）。
- Task 2 `7114344` `feat(island): open a launcher card from the add tile` + `e000eab` `fix(island): scale the launcher icons and label the kinds`。
  - island `＋` 从 ContextMenu 改为 Popup；三行图标/名称/说明；Esc/点外部/自动收起；三语 key。
  - 审查发现图标 64→24 未缩放，修复为 Viewbox + 64 canvas；行标题改用 `UI_WidgetKind*`；pin 加 3 行/24px/名称+说明断言。
  - 验证：build 0 警告；`XamlLoadCheck` **288 PASS**；`LangCheck` **182 keys**。
- Task 3 `bdfdf3c` `feat(ui): share one color palette for widgets and folders`。
  - `ColorPaletteDialog`（3×3、当前圈/勾选、hover 名称、默认按钮、Esc/×）；Widget 换色改用它；`CreateColorSwatch` 删除。
  - 验证：build 0 警告；`XamlLoadCheck` **290 PASS**。
- Task 4 `5579daf` `feat(folder): open the shared palette and tint input dialogs` + `a48c556` `fix(folder): only offer the default color when one is set`。
  - 文件夹右键单入口「文件夹颜色…」；`DialogFactory.BuildInput`/`ShowInput` 跟色；`MenuRenderCheck` 重写。
  - 审查发现无自定义色时“恢复默认”仍可点，修复为 `allowDefault: FolderColor.HasCustomIcon(resource)`（任意自定义图标；`current` 只决定圈/名称）。
  - 验证：build 0 警告；`MenuRenderCheck` 全 PASS；`XamlLoadCheck` **291 PASS**。
- Task 5 `288ca29` `feat(file): create minimal Office documents from the new-file dialog`。
  - `FileTemplates`（docx/xlsx 最小包、pptx 内嵌模板）、扩展名追加、根/浏览创建路径；`.gitignore` 例外 + EmbeddedResource。
  - 验证：build 0 警告；`ShellOpsCheck` **77 PASS**（含 7 条 Office pin）。
- Task 6 文档：README 英/中、CHANGELOG Unreleased、spec 状态；提交 `8a12bdb`（后续 docs 修正提交见 `git log`）。
  全量 `Run-Checks.ps1 -Group all` **32/32**（日志 `%TEMP%\kobold-widget-menus-matrix.log`：
  `All 32 check(s) passed`；XamlLoadCheck 76s、MenuRenderCheck 4.8s）。
- 终审修复波 `27cfa36` `fix(ui): close the final review findings`：popup 10px 阴影带、文件夹菜单接线 pin / 默认门、Office XML 解析 pin、`MENU_CHECK_WIDTH=18`、island 64 canvas pin、palette token/ring、`BuildInput` null 关闭、`FileTemplates.CreateNew`。
  验证：`MenuRenderCheck` 全 PASS、`ShellOpsCheck` **83 PASS**、`XamlLoadCheck` **291 PASS**、全量 **32/32**。
  scoped 复审子代理因环境 `EPERM` 启动失败；控制者按终审 findings 逐条核对 diff。
- **冒烟：待用户**。修复版尚未部署；launcher / 色盘 / 输入框跟色 / Office 双击打开需要真机确认。
- 已知限制：docx/xlsx 为最小 OOXML，真实 Office 打开仅冒烟；pptx 为一次性生成的 1 页空白模板；island 非激活窗口上的 Esc 是 best-effort，点外部是保证关闭路径。

## 自检（writing-plans）

**Spec coverage：**
- §4 launcher → T1/T2。
- §5 色盘 / 文件夹入口 → T3/T4。
- §6 输入框 → T4。
- §7 Office → T5。
- §8 接口 / keys → T1-T5。
- §9 测试 → T1-T5 + T6 全量。
- §10 风险 → 任务内边界与冒烟。

**Placeholder scan：** 无 TBD/TODO；所有代码步骤有实际代码或明确的 XML 部件。

**Type consistency：**
- `LauncherCard.Create(UIElement, double)` 与 T1/T2 调用一致。
- `Kobold.Style.LauncherItem` 在 T1 定义，T2 使用。
- `ColorPaletteDialog.Build/Show` 与 T3/T4 调用一致。
- `DialogFactory.BuildInput(Window, string, string, string, Func<string,string>, string, Action<string>)` 与 T4 pin / ShowInput 一致。
- `FileTemplates.TryCreate(string, out string)` / `BuildOfficePackage(string)` 与 T5 一致。
- `CreateFolderColorEntry` 返回 `MenuItem`，T4 两个菜单构建点与 pin 一致。
