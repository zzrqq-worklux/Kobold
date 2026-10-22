# Kobold Widget 菜单、配色与新建文件格式设计（launcher · 色盘 · 输入框跟色 · Office 模板）

> **状态**：**已执行（2026-10-22）**（设计 1A / 2A / 3A / 4A / 5A；表格 6A 留到 A）。
> **基线**：`main` @ `2538300`（外部文件拖放已合并、已推送、已部署）。
> **关系**：独立增量 spec；不修改 `2026-10-19-note-navigation-and-polish-design.md` 与
> `2026-10-21-external-file-drop-design.md`。表格 WYSIWYG 编辑是后续独立 spec（A），本 spec 不实现。
> **代码走查（2026-10-21）**：`Controls/NotePanelView.xaml(.cs)`、`Controls/IslandWindow.xaml(.cs)`、
> `Controls/WidgetWindowBase.cs`、`Helpers/WidgetMenuBuilder.cs`、`Helpers/ContextMenuBuilder.cs`、
> `Helpers/DialogFactory.cs`、`Controls/FolderWidget.MenuActions.cs`、`Core/NewFileName.cs`、
> `Services/ShellFileOperations.cs`、`Core/Localization.cs`、`Core/UiTokens.cs`、`Themes/Controls.xaml`、
> `App.xaml`、`tests/XamlLoadCheck`、`tests/MenuRenderCheck`、`tests/ShellOpsCheck`、`tests/LangCheck`。
> **定性**：一次**视觉统一 + 创建体验**改动。左键启动器统一成同一套 anchored launcher 卡片；
> 右键命令菜单继续 ContextMenu；Widget / 文件夹换色统一成居中 3×3 色盘；Widget 内输入框统一跟组件色；
> 新建文件追加 docx/xlsx/pptx 并写最小有效 OOXML。数据格式零新增字段。

## 0. 决策记录（2026-10-21 用户确认）

1. **左键启动器统一**：笔记标题 `▾` 保留两段式（动作行 + 笔记列表），只统一浮层表面与行样式；
   island `＋` 改成同款 launcher 卡片的三行列表（图标 + 名称 + 一句说明）。
2. **右键命令菜单不变**：Widget 菜单、空白菜单、条目菜单继续用全局 `ContextMenu` 模板；
   不把右键菜单改成自绘 popup。
3. **换色统一**：Widget 与文件夹都打开同一个居中 3×3 色盘；文件夹版多一个“恢复默认”；
   文件夹右键菜单从 swatch 子菜单改成单入口“文件夹颜色…”。
4. **输入框跟色**：所有 Widget 内 `DialogFactory.ShowInput` 调用（新建文件夹、重命名 Widget、
   重命名条目、浏览新建文件夹）统一注入组件色；只染确定按钮 + 边框，不改布局。
5. **新建文件格式**：`NewFileName.CommonExtensions` 末尾追加 `.docx` / `.xlsx` / `.pptx`；
   默认仍是 `.txt`；Office 文件写最小有效 OOXML（docx/xlsx 代码生成，pptx 内嵌 1 页空白模板）。
6. **表格 6A 留到 A**：轻量网格方向已确认，但不在本 spec 内实现；A 的 spec 再定稿。

## 1. 目标与成功标准

| # | 目标 | 验收口径 |
|---|---|---|
| 1 | 笔记 `▾` 与 island `＋` 像同一套 | 同一个 `Kobold.Style.LauncherItem` 行样式 + `LauncherCard` 表面；圆角 12、菜单同款阴影 |
| 2 | island `＋` 可读、可点、可关 | 三行：图标 + 名称 + 说明；Esc / 点外部关闭；创建后 popup 关闭、island 不塌陷 |
| 3 | 换色一致 | Widget 与文件夹打开同一色盘；9 色 + 当前圈/勾选 + hover 颜色名；文件夹有“恢复默认” |
| 4 | 文件夹换色入口简洁 | 右键菜单单入口“文件夹颜色…”（带当前色 swatch），不再有 swatch 子菜单 |
| 5 | 输入框跟色 | 所有 Widget 内输入框的确定按钮 = 组件色，边框 25% 混色；布局/文案不变 |
| 6 | Office 文件能打开 | docx 空段落、xlsx 空工作表、pptx 空白幻灯片；双击不报“文件损坏” |
| 7 | 零回归 / 32/32 | 笔记下拉行为、右键菜单渲染、文件夹/待办、配置安全、0 警告；矩阵维持 32 |

## 2. 非目标（本轮不做）

- 不重做右键 `ContextMenu` 的模板；只新增 launcher 样式与卡片。
- 不做 launcher 的方向键 / Home / End / 输入筛选；只保证鼠标、Esc、点外部。
- 不做表格 WYSIWYG（A 的 spec）；不新增列增删。
- 不做自定义 hex 取色器；只用 `UiTokens.FolderPalette` / `FolderIconPalette` 的 9 色。
- 不做新 Widget kind；island 仍只列 folder / todo / note。
- 不做 Office 模板的可编辑占位文字（docx 空段落 / xlsx 空表 / pptx 空白页）。
- 不改 `WidgetData` / `FolderData` / `NoteData` 的 JSON 字段。
- 不新增 check 工程；不改变 32 行矩阵。
- 不把 `Resources/blank.pptx` 的生成带进构建；它是一次性生成的二进制资源。

## 3. 现状与缺口

### 3.1 左键启动器

- 笔记 `▾` 是 `NotePanelView` 里的自绘 `Popup`：动作行 + 笔记列表，`Border` 用
  `Surface` / `Border` / `RadiusControl(8)` / 局部 `NoteMenuButtonStyle`；已过审。
- island `＋` 用 `ContextMenu` + `WidgetMenuBuilder.BuildKindChoices`，视觉是全局右键菜单模板；
  和笔记 `▾` 不是一套。
- 右键 Widget / 空白 / 条目菜单也走全局 `ContextMenu`，这是正确的命令语义，不改。

### 3.2 换色

- Widget 换色：`WidgetWindowBase.BeginColorChange` 自己建一个 340×220 居中窗口，9 个 48px
  swatch，无标题、无颜色名、无关闭按钮；`CreateColorSwatch` 直接改 `_data.Color`。
- 文件夹换色：`CreateFolderColorMenu` 是右键菜单里的 swatch 子菜单（色块 + 名称 +
  “恢复默认”），和 Widget 换色完全两套。
- 两者色相/顺序已经一致（`FolderPalette` 与 `FolderIconPalette` 是同一组 9 色）。

### 3.3 输入框与新建文件

- `DialogFactory.ShowInput` 没有 widget 色参数；新建文件夹、重命名等都用主题色，
  而 `ShowNewFile` 已经通过 `ApplyWidgetColor` 跟色。
- `NewFileName.CommonExtensions` 只有文本类扩展名；创建统一写 0 字节文件。
- `FolderWidget.CreateNewFile` 直接 `File.WriteAllText(path, "")`；
  `CreateBrowseTextFile` 走 `ShellFileOperations.CreateTextFile`。

## 4. 左键启动器（1A + 2A）

### 4.1 共享表面 `Helpers/LauncherCard.cs`

```csharp
public static class LauncherCard
{
    /// <summary>A themed floating card for click-opened launchers.</summary>
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
```

- 只用现有 token；圆角 12（`UiTokens.RadiusWindow`），和菜单/对话框一致。
- 不设置 `Tag`；调用方设置（`NoteTitleMenu` / `IslandAddMenu`），测试可读。

### 4.2 共享行样式 `Kobold.Style.LauncherItem`

在 `Themes/Controls.xaml` 新增 `Button` 样式：

- `Background=Transparent`、`BorderThickness=0`、`Foreground={DynamicResource Kobold.Brush.TextPrimary}`、
  `FontSize=13`、`Padding=8,6`、`Cursor=Hand`、`HorizontalContentAlignment=Left`、
  `HorizontalAlignment=Stretch`、`SnapsToDevicePixels=True`。
- 模板：`Border x:Name="Bd"`，`CornerRadius=8`，`Background={TemplateBinding Background}`，
  `Padding={TemplateBinding Padding}`，`ContentPresenter` 左对齐；触发器：
  `IsMouseOver → SurfaceHover`、`IsPressed → ControlBackground`、`IsEnabled=False → Opacity 0.5`。
- 笔记 `NotePanelView.xaml` 删除本地 `NoteMenuButtonStyle`；`BuildTitleMenuAction` /
  `BuildTitleMenuEntry` 改用 `FindResource("Kobold.Style.LauncherItem") as Style`。
- 行高：动作行 `MinHeight=32`，笔记条目 `MinHeight=34`；勾选列 18；当前条背景
  `ThemeManager.SurfaceHoverBrush`（沿用）。

### 4.3 笔记标题 `▾`

结构不变（方案 1A）：

```
┌──────────────────────────────┐
│ ＋ 新建        ‹ 笔记列表     │  ← 32px 动作行
├──────────────────────────────┤
│ ✓   当前笔记标题             │  ← 34px 行，勾选列 18
│     另一条笔记               │
│     …                        │
└──────────────────────────────┘
```

- 根 `Border` 改成 `LauncherCard.Create(panel, width)`（宽度仍 `max(220, min(360, ActualWidth - 24))`），
  `Tag="NoteTitleMenu"`。
- 不再设置局部 `CornerRadius` / `Background` / `BorderBrush` / `Effect`；由 `LauncherCard` 统一。
- 现有行为不变：动作点击先关下拉再执行；条目点击关下拉再 `OpenNote`；当前条勾选 + 高亮；
  Popup `StaysOpen=false`；Esc 在内容 `PreviewKeyDown` 处理；关闭时若仍在编辑回焦 `EditBox`。
- `BuildTitleMenuContent` 的 tags（`NoteMenuNew` / `NoteMenuHome` / `NoteMenuCheck` / note id）全部保留，
  既有 pin 只改样式断言，不改结构断言。

### 4.4 island `＋`（方案 2A）

```
              ┌────────────────────────────┐
              │ 新建组件                   │  ← 11px muted
              ├────────────────────────────┤
              │ [icon] 文件夹              │
              │        收集文件与快捷方式   │  ← 11px muted
              │ [icon] 待办                │
              │        日历与清单           │
              │ [icon] 笔记                │
              │        Markdown 笔记        │
              └────────────────────────────┘
                 锚在 ＋ 下方，右对齐，280px
```

- `IslandWindow` 新增 `Popup _addWidgetPopup`；`ShowAddWidgetMenu` 从 ContextMenu 改为：
  - 已开 → 关闭（toggle）；
  - 否则 `BuildAddWidgetContent()`，`Popup { PlacementTarget=AddTile, Placement=Bottom,
    StaysOpen=false, AllowsTransparency=true, Child=card,
    HorizontalOffset = -(280 - AddTile.ActualWidth) }`；
  - 打开前 `PauseAutoCollapse()`；`Closed` 清引用 + `ResumeAutoCollapse()`；
  - `content.Focusable=true`，Dispatcher 焦点到第一行；内容 `PreviewKeyDown` Esc → 关闭。
- `public FrameworkElement BuildAddWidgetContent()`（供 pin）：
  - `LauncherCard.Create(panel, 280)`，`Tag="IslandAddMenu"`；
  - 标题 `TextBlock` 11 muted，`Tag="IslandAddHeader"`，文案 `Island_NewWidgetHeader`；
  - 遍历 `WidgetKindRegistry.All`（folder / todo / note），每行一个 `Button`：
    - `Tag=kind.Id`，`Style=FindResource("Kobold.Style.LauncherItem") as Style`，`MinHeight=48`；
    - 内容 `Grid`：24×24 `Canvas` + 名称 13 semibold + 说明 11 muted；
    - 图标 `IconRendererFactory.CreateByKey(kind.IconKey, iconStyle).Render(canvas,
      UiTokens.DefaultFolderColor)`；`iconStyle = WidgetManager.Instance.Config.IconStyle ?? "classic"`；
    - 说明 key：folder → `Island_NewFolderHint`，todo → `Island_NewTodoHint`，
      note → `Island_NewNoteHint`；
    - 点击：关闭 popup → `WidgetManager.Instance.CreateWidget(kind.Id)`。
- 删除 island 里 `BuildKindChoices` 的调用；`WidgetMenuBuilder.BuildKindChoices` 保留给托盘菜单。
- island 的 `ShowActivated=False` 可能影响键盘焦点；Esc 为冒烟项，点外部由 `StaysOpen=false` 保证。

## 5. 换色（3A + 4A）

### 5.1 共享色盘 `Helpers/ColorPaletteDialog.cs`

```csharp
public static class ColorPaletteDialog
{
    // Build returns a closed window; Show sets the owner and calls ShowDialog.
    public static Window Build(
        string title,
        IReadOnlyList<string> palette,
        string current,
        bool allowDefault,
        string defaultText,
        Action<string> onPick,
        Action onDefault);

    public static void Show(
        Window owner,
        string title,
        IReadOnlyList<string> palette,
        string current,
        bool allowDefault,
        string defaultText,
        Action<string> onPick,
        Action onDefault);
}
```

- 窗口：360 宽、`SizeToContent=Height`、居中 owner、`WindowStyle=None`、`AllowsTransparency=true`、
  `Background=Transparent`、`ResizeMode=NoResize`、`ShowInTaskbar=false`。
- 内容：`Border` `DialogBackground` / `DialogBorder` / `RadiusWindow(12)` / 对话框阴影；
  内部 `StackPanel`：
  - 标题行：标题 13 semibold（`Tag="PaletteTitle"`），右侧 `×`（`Tag="PaletteClose"`，
    Cursor=Hand，`MouseLeftButtonUp` → Close）；
  - 3×3 `UniformGrid Columns=3`（或 3 行 3 列 Grid），每个 swatch：
    - `Border` 52×52、Margin 7、`CornerRadius=9`、`Background=SolidColorBrush(color)`、
      `Tag=color`、`Cursor=Hand`；
    - 当前色：`BorderBrush=ThemeManager.TextBrush`、`BorderThickness=3`，中心 `✓`
      （`Tag="PaletteCheck"`，`Foreground=ThemeManager.TextBrush`）；
    - `MouseEnter`：`Opacity=0.85`，底部 caption 显示 `Localization.Get(FolderIconPaletteNames[i])`；
    - `MouseLeave`：`Opacity=1`，caption 回到当前色名；
    - `MouseLeftButtonUp`：`onPick(color)` → `window.Close()`。
  - caption：11 muted，`Tag="PaletteCaption"`；
  - 底部：`allowDefault` 时显示 `Button`（`Tag="PaletteDefault"`，`Kobold.Style.SecondaryButton`，
    文案 `defaultText`，点击 `onDefault()` → Close），否则不显示。
  - `PreviewKeyDown` Esc → Close。
- 不新增 hex；颜色来自传入数组；当前色用 `ThemeManager.TextBrush`，在深浅主题都可读。
- `Show` 设置 `Owner`（null 时居中屏幕）后 `ShowDialog()`；`onPick` / `onDefault` 在关闭前执行。

### 5.2 Widget 换色

`WidgetWindowBase.BeginColorChange` 改为：

```csharp
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
```

- 删除 `CreateColorSwatch` 与本地窗口构造；`RunModal` 继续屏蔽面板失焦塌陷。
- 当前色、hover 名称、关闭、默认按钮都由共享色盘负责。

### 5.3 文件夹换色入口

`FolderWidget.MenuActions`：

- 删除 `CreateFolderColorMenu`；新增：

```csharp
private MenuItem CreateFolderColorEntry(DisplayItem item)
{
    string resource = FolderColor.GetIconResource(item.Path);
    string current = CurrentFolderColor(resource);

    var header = new StackPanel { Orientation = Orientation.Horizontal };
    var swatch = new Rectangle
    {
        Width = 16,
        Height = 16,
        RadiusX = 3,
        RadiusY = 3,
        VerticalAlignment = VerticalAlignment.Center,
        Fill = current == null ? Brushes.Transparent : new SolidColorBrush(Utils.HexToColor(current)),
        Stroke = ThemeManager.BorderBrush,
        StrokeThickness = current == null ? 1 : 0
    };
    header.Children.Add(swatch);
    header.Children.Add(new TextBlock
    {
        Text = Localization.Get("Menu_FolderColor") + "…",
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    });

    var entry = new MenuItem { Header = header, Tag = "FolderColorEntry" };
    entry.Click += (s, a) => OpenFolderColorPalette(item, current);
    return entry;
}

private static string CurrentFolderColor(string resource)
{
    for (int i = 0; i < UiTokens.FolderIconPalette.Length; i++)
    {
        if (IconBelongsTo(resource, UiTokens.FolderIconPalette[i])) return UiTokens.FolderIconPalette[i];
    }
    return null;
}

private void OpenFolderColorPalette(DisplayItem item, string current)
{
    RunModal(() => ColorPaletteDialog.Show(
        this,
        Localization.Get("Menu_FolderColor"),
        UiTokens.FolderIconPalette,
        current,
        allowDefault: current != null,
        defaultText: Localization.Get("Menu_FolderColorDefault"),
        onPick: color => ApplyFolderColor(item, color),
        onDefault: () => RestoreFolderColor(item)));
}
```

- `allowDefault` 只在文件夹已有自定义色时为真（`current != null`），沿用旧菜单
  `IsEnabled = current != null` 的边界；避免对没有自定义色的文件夹执行 `Restore`
  而误移除 System 属性 / 非本程序的 desktop.ini。

- `BuildRootItemMenu` 与 `BuildBrowseItemMenu` 都把 `CreateFolderColorMenu(item)` 换成
  `CreateFolderColorEntry(item)`；`IsColorable(item)` 判断保留。
- `IconBelongsTo`、`ApplyFolderColor`、`RestoreFolderColor`、`RefreshAfterFolderColor` 保留。
- 菜单项仍走全局 `ContextMenu` 模板；只是不再有子菜单。

## 6. 输入框跟色

`Helpers/DialogFactory.cs`：

- `ShowInput` 增加可选参数 `string widgetColor = null`（放在 `validate` 后，源兼容）。
- 抽出 `public static Window BuildInput(Window owner, string title, string prompt,
  string initialValue, Func<string, string> validate, string widgetColor, Action<string> onAccept)`：
  - 构建现有对话框（标题 / prompt / textBox / error / OK / Cancel），OK 校验通过后调用
    `onAccept(value)`；Cancel 关闭；Enter 接受；
  - 设置 `dialog.Content = border` 后调用现有 `ApplyWidgetColor(dialog, widgetColor)`；
  - `ShowInput` 用 `BuildInput` + `ShowDialog`，结果由 `onAccept` 写回；公开 `BuildInput`
    只供 off-screen pin 读资源，不弹窗。
- `WidgetWindowBase.ShowInputModal` 调用 `DialogFactory.ShowInput(..., validate,
  _data == null ? null : _data.Color)`；所有调用方自动跟色。
- 只染 `Kobold.Brush.Accent` / `AccentHover` / `OnAccent` 与 25% 边框；标题/正文/布局不变。
- 高对比度 / 空色：`ApplyWidgetColor` 继续回退主题 accent，不强行染色。

## 7. 新建文件格式与 OOXML

### 7.1 扩展名

`Core/NewFileName.CommonExtensions` 末尾追加 `.docx` / `.xlsx` / `.pptx`；顺序：

```
.txt .md .json .csv .log .html .xml .ini .bat .ps1 .docx .xlsx .pptx
```

默认仍是 `.txt`；`NormalizeExtension` / `Compose` 不变。

### 7.2 `Core/FileTemplates.cs`

```csharp
public static class FileTemplates
{
    public static bool IsOfficeExtension(string extension);
    public static bool TryCreate(string path, out string error);
    public static byte[] BuildOfficePackage(string extension); // tests; null when unsupported
}
```

- `TryCreate`：按 `Path.GetExtension(path)` 分流；Office → `BuildOfficePackage` + `File.WriteAllBytes`；
  其它 → `FileStream(FileMode.CreateNew)` 空文件。失败返回 false + 可读 error，不覆盖已有文件。
- `BuildOfficePackage(".docx")`：`ZipArchive` 写 3 个部件：
  - `[Content_Types].xml`（rels/xml default + word/document.xml override）
  - `_rels/.rels`（officeDocument → word/document.xml）
  - `word/document.xml`（`w:body` 一个空 `w:p`）
- `.xlsx`：4 个部件：
  - `[Content_Types].xml`（workbook + sheet1 overrides）
  - `_rels/.rels` → `xl/workbook.xml`
  - `xl/workbook.xml`（Sheet1 → rId1）
  - `xl/_rels/workbook.xml.rels` → `worksheets/sheet1.xml`
  - `xl/worksheets/sheet1.xml`（空 `sheetData`）
- `.pptx`：读取内嵌资源 `Resources/blank.pptx`（1 页空白幻灯片），原样返回；
  资源缺失 → null（`TryCreate` 失败并报错）。不在构建时调用 Office COM。
- `Resources/blank.pptx` 一次性用 PowerPoint COM 生成（`ppLayoutBlank=12`，
  `ppSaveAsOpenXMLPresentation=24`），作为 `EmbeddedResource` 提交；构建不依赖 Office。
- `Kobold.csproj` 增加框架引用 `System.IO.Compression` / `System.IO.Compression.FileSystem`
  （.NET Framework 自带，非新依赖）。

### 7.3 创建路径

- `FolderWidget.CreateNewFile`（根模式）：`Path.Combine(storage, fileName)` 后
  `FileTemplates.TryCreate`；失败用现有错误提示，不加条目。
- `FolderWidget.MenuActions.CreateBrowseTextFile`（浏览模式）：同样用
  `FileTemplates.TryCreate`，替换 `ShellFileOperations.CreateTextFile`；`RefreshPanel()` 保留。
- `ShellFileOperations.CreateTextFile` 保留（既有测试 / 其它入口），不改语义。
- 重名 / 非法名校验仍由 `ShowNewFileDialog` 的 `NameErrorText` 负责；`TryCreate` 只负责写内容。

## 8. 接口与文件

### 8.1 新增

- `Helpers/LauncherCard.cs`（共享浮层表面）。
- `Helpers/ColorPaletteDialog.cs`（共享 3×3 色盘，`Build`/`Show`）。
- `Core/FileTemplates.cs`（Office / 空文件创建 + 包构建）。
- `Resources/blank.pptx`（内嵌 1 页空白模板，二进制资源）。

### 8.2 修改

- `Themes/Controls.xaml`：新增 `Kobold.Style.LauncherItem`。
- `Controls/NotePanelView.xaml`：删除本地 `NoteMenuButtonStyle`。
- `Controls/NotePanelView.xaml.cs`：`BuildTitleMenuContent` 用 `LauncherCard` + 共享样式；
  tags/行为不变。
- `Controls/IslandWindow.xaml.cs`：`ShowAddWidgetMenu` → `Popup`；新增
  `BuildAddWidgetContent` / `CloseAddWidgetMenu`；删除 `BuildKindChoices` 的 island 调用。
- `Controls/WidgetWindowBase.cs`：`BeginColorChange` 用 `ColorPaletteDialog`；
  `ShowInputModal` 传 `_data.Color`；删除 `CreateColorSwatch`。
- `Helpers/DialogFactory.cs`：`ShowInput` 可选 widgetColor；`BuildInput` 公开。
- `Controls/FolderWidget.MenuActions.cs`：`CreateFolderColorEntry` / `OpenFolderColorPalette` /
  `CurrentFolderColor`；删除 `CreateFolderColorMenu`；两个菜单构建点替换。
- `Core/NewFileName.cs`：追加 3 个扩展名。
- `Kobold.csproj`：`System.IO.Compression` / `System.IO.Compression.FileSystem` 框架引用；
  `Resources/blank.pptx` 作为 `EmbeddedResource`。
- `.gitignore`：增加 `!Resources/blank.pptx` 例外（默认规则忽略 `*.pptx`）。
- `Controls/FolderWidget.MenuActions.cs` 的 `CreateNewFile` / `CreateBrowseTextFile`：走 `FileTemplates`。
- `Core/Localization.cs`：4 个三语 key（见 §8.3）。

### 8.3 新增三语 key

| key | en | zh | ja |
|---|---|---|---|
| `Island_NewWidgetHeader` | `New widget` | `新建组件` | `新規コンポーネント` |
| `Island_NewFolderHint` | `Collect files and shortcuts` | `收集文件与快捷方式` | `ファイルとショートカットを集める` |
| `Island_NewTodoHint` | `Calendar and checklist` | `日历与清单` | `カレンダーとチェックリスト` |
| `Island_NewNoteHint` | `Markdown notes` | `Markdown 笔记` | `Markdown ノート` |

- 色盘标题复用 `Dialog_PickColor`（Widget）/ `Menu_FolderColor`（文件夹）；
  “恢复默认”复用 `Menu_FolderColorDefault`；颜色名复用 `Color_*`。
- 不新增其它 key。

## 9. 测试与验收

### 9.1 自检矩阵（维持 32 行）

**`tests/XamlLoadCheck`（WPF，离屏）新增：**

1. `launcher: the note menu wears the shared card`：`BuildTitleMenuContent()` 返回 `Border`，
   `Tag="NoteTitleMenu"`，`CornerRadius == UiTokens.RadiusWindow`，`Background == ThemeManager.SurfaceBrush`；
   动作 / 条目按钮的 `Style == Application.Current.FindResource("Kobold.Style.LauncherItem")`。
2. `launcher: the island add menu is a three-row card`：`new IslandWindow().BuildAddWidgetContent()`
   返回 `Tag="IslandAddMenu"` 的 `Border`；3 个按钮 `Tag` = folder/todo/note；每个按钮有
   名称 + 说明；标题文案 = `Island_NewWidgetHeader`；行样式 = `Kobold.Style.LauncherItem`。
3. `palette: the shared palette has nine swatches and a current ring`：
   `ColorPaletteDialog.Build("选择颜色", UiTokens.FolderPalette, "#3B82F6", false, null, ...)`
   → `Border` 内容中 9 个 `Tag` 为颜色的 swatch；当前色 `BorderThickness == 3`，
   其它为 1/0；caption = `Color_Blue`。
4. `palette: the folder palette shows the default button`：
   `allowDefault=true` → `Tag="PaletteDefault"` 存在；`allowDefault=false` → 不存在。
5. `folder: the color entry is a single menu item`：反射调用
   `CreateFolderColorEntry(item)` → `MenuItem.Items.Count == 0`，`Tag="FolderColorEntry"`，
   header 含 16px swatch + `Menu_FolderColor` + `…`。
6. `input: the dialog follows the widget color`：
   `DialogFactory.BuildInput(..., "#EF4444", _ => {})` → `window.Resources["Kobold.Brush.Accent"]`
   的 `Color == Utils.HexToColor("#EF4444")`；`null` 色 → 主题 accent；高对比度 → 系统 accent。
7. `note: the dropdown structure still holds`：既有 `NoteMenuNew` / `NoteMenuHome` /
   `NoteMenuCheck` / note id pins 不回归。

**`tests/MenuRenderCheck`（WPF，离屏）新增：**

8. `menu: the folder color entry renders a swatch`：调用 `BuildRootItemMenu` / `BuildBrowseItemMenu`
   反射，找到 `Tag="FolderColorEntry"` 的 `MenuItem`；header 是 `StackPanel`，含 16×16
   `Rectangle` 与文案；不存在 swatch 子菜单。

**`tests/ShellOpsCheck`（纯逻辑，CI）新增：**

9. `new file: the extension list offers Office formats`：`CommonExtensions` 末尾三个
   `.docx` / `.xlsx` / `.pptx`；默认 `.txt` 不变。
10. `new file: docx is a valid package`：`FileTemplates.BuildOfficePackage(".docx")` → 解压含
    `[Content_Types].xml` / `_rels/.rels` / `word/document.xml`；XML 可解析。
11. `new file: xlsx is a valid package`：含 `xl/workbook.xml` / `xl/worksheets/sheet1.xml`。
12. `new file: pptx is a valid package`：含 `ppt/presentation.xml` / `ppt/slides/slide1.xml`。
13. `new file: TryCreate writes the template`：临时目录 `.docx` / `.xlsx` / `.pptx` 均非 0 字节；
    `.txt` 仍 0 字节；已存在文件不被覆盖。

**`tests/LangCheck`：** 4 个新 key 三语齐全；旧 key 不缺失。

**`tests/UiTokensCheck`：** 不新增 hex；色盘颜色仍来自 `FolderPalette` / `FolderIconPalette`。

**既有 pins 零回归**：`MenuRenderCheck` 的 rounded menu / item pill / separator / disabled；
`XamlLoadCheck` 的笔记下拉、WidgetChrome、文件夹/待办、配置安全；`ShellOpsCheck` 的
`CreateTextFile` / 名称校验。

### 9.2 冒烟清单（用户手动）

1. 笔记 `▾`：两段式、动作、当前条勾选 + 高亮、切换/新建/回 home、编辑中打开下拉、Esc/点外部。
2. island `＋`：三行图标/名称/说明；folder/todo/note 各创建一次；Esc/点外部关闭；island 不塌陷。
3. 换色：Widget 右键换色 → 3×3 色盘、hover 名称、当前圈/勾选、取消；重启后保持。
4. 文件夹右键“文件夹颜色…”：9 色 + 恢复默认；当前色 swatch；换色后 Explorer 图标更新。
5. 输入框：新建文件夹 / 重命名 Widget / 重命名条目 / 浏览新建文件夹 → 确定按钮与边框跟组件色；
   浅色 / 高对比度下可读。
6. 新建文件：`.docx` / `.xlsx` / `.pptx` 双击在 Word / Excel / PowerPoint 打开，不报损坏；
   `.txt` 仍空文件；重名 `(1)`；浏览模式与根模式都生效。
7. 回归：右键 Widget / 空白 / 条目菜单、待办、文件夹、锁定/钉住、32/32。
8. 三主题 / 三语 / 高对比度：launcher、色盘、输入框、Office 预览正常。

## 10. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 笔记下拉重样式破坏既有结构 pin | 保留全部 tags / 行为；只换表面与按钮样式；分任务先跑既有 pin |
| island Popup 抢焦点 / Esc 不生效 | `StaysOpen=false` + 内容 `Focusable` + Esc handler；真实键盘冒烟 |
| 色盘模态让文件夹换色变慢 | 3×3 大色块 + 当前圈/名称；这是用户确认的 3A 取舍 |
| 文件夹当前色无法映射到 9 色 | `IconBelongsTo` 匹配；不匹配时 swatch 用空心边框，默认按钮仍可用 |
| 输入框跟色在高对比度下失真 | `ApplyWidgetColor` 高对比度回退主题 accent；不强行染色 |
| docx/xlsx 最小包被 Office 判损坏 | 解压部件 pin + 用户真机 Word/Excel 冒烟；必要时补 styles/theme 部件 |
| pptx 最小包被 PowerPoint 修复 | 用一次性生成的 `Resources/blank.pptx` 内嵌模板，不在构建时跑 Office |
| 新增扩展名影响既有 `NewFileName` pins | 只追加末尾，默认 `.txt` / 归一化 / 去重逻辑不变 |
| 32/32 被新 check 打破 | 不新增工程；pins 放进现有 check |

## 11. 交付物

- `Helpers/LauncherCard.cs`、`Helpers/ColorPaletteDialog.cs`、`Core/FileTemplates.cs`。
- `Resources/blank.pptx`（内嵌模板）。
- `Themes/Controls.xaml`（`Kobold.Style.LauncherItem`）。
- `Controls/NotePanelView.xaml(.cs)`、`Controls/IslandWindow.xaml.cs`、
  `Controls/WidgetWindowBase.cs`、`Helpers/DialogFactory.cs`、
  `Controls/FolderWidget.MenuActions.cs`、`Core/NewFileName.cs`、`Kobold.csproj`。
- `Core/Localization.cs`（4 个三语 key）。
- `tests/XamlLoadCheck/Program.cs`、`tests/MenuRenderCheck/Program.cs`、
  `tests/ShellOpsCheck/Program.cs`（按 §9 增补）。
- `docs/superpowers/plans/2026-10-21-widget-menus-and-file-templates.md`（writing-plans 产出）。
- `README.md`（英/中）、`CHANGELOG.md`（Unreleased）在整支终审时统一补。

## 12. 与既有 spec 的关系

- 本 spec 不修改笔记导航 / 外部拖放的任何条款。
- 右键 `ContextMenu` 仍是命令菜单；本 spec 只统一“左键启动器”。
- 表格 WYSIWYG（A）另立 spec；本 spec 的 `LauncherCard` / 色盘 / 输入框可被 A 复用。
- `WidgetMenuBuilder.BuildKindChoices` 保留给托盘；island 改用 `BuildAddWidgetContent`。
- 数据格式零新增字段；旧配置读写不变。
