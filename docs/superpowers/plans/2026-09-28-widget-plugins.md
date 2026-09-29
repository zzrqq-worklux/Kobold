# 组件类型（插件）与 Todo 组件（Widget Kinds & Todo）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在既有组件体系上引入"组件类型"（folder / todo），并交付第一个新类型 Todo（月历 + 当日清单 + 一层子待办），全程文件夹组件零回归。

**Architecture:** `Core` 里引入 `WidgetData` 基类 + `Kind` 判别转换器（老 `Folders` 配置无损迁移）；UI 侧把面板"壳"抽成 `WidgetChrome`，共享行为放进 `WidgetWindowBase`，`WidgetManager` 通过 `IWidgetWindow` 契约管理两种窗口；Todo 的全部可判定逻辑（顺延/月历/列表操作）写成 `Core` 纯函数，由控制台自检覆盖。

**Tech Stack:** WPF / .NET Framework 4.8 / C# 7.3；Newtonsoft.Json；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵）。

**Spec:** `docs/superpowers/specs/2026-09-28-widget-plugins-design.md`

## Global Constraints

- C# 7.3 / net48，不引入新 NuGet 依赖。
- TDD：每个任务先写失败自检（**编译失败也算红**）→ 最小实现 → 自检与全量回归全绿。提交需用户许可。
- 新自检项目必须登记进 `tools/Run-Checks.ps1` 矩阵（CI 子集标记照旧）。
- **阶段 3 之前不改 `FolderWidget` 的行为**；阶段 3 的迁移单独提交、单独冒烟。
- PaperTodo 只参考交互范式，不复制代码/素材（PolyForm NC 与 MIT 不兼容）。
- 一层子待办约束由 `SubTodoItem` 类型保证（类型里没有子项列表字段）。
- 新增本地化 key 三语同步；`LangCheck` 必须保持绿。
- 计划不包含发版；版本、打包、Release 由用户另行决定。
- 说明：spec §4.2 写的是 `_widgets: List<WidgetWindowBase>`；阶段 2 因 `FolderWidget` 尚未迁入基类，
  先用 `IWidgetWindow` 接口做管理器契约（folder 显式实现），阶段 3 后 `FolderWidget` 继承基类，
  接口保留为管理器契约（实施细化，不改变 spec 意图）。

---

## 阶段 1：数据层（应用行为不变）

### Task 1.1: WidgetData 基类 + 类型判别转换器 + 配置迁移

**Files:**
- Create: `Core/WidgetData.cs`（基类 + `UnknownData`）、`Core/TodoData.cs`（`TodoData`/`TodoItem`/`SubTodoItem`）、`Core/WidgetDataConverter.cs`
- Modify: `Core/FolderData.cs`（`class FolderData : WidgetData`，字段照旧）、`Core/AppConfig.cs`（`Widgets` + 迁移 + 修复）
- Modify（编译跟进）: `Core/StartupPanels.cs`、`Core/WidgetManager.cs`、`Controls/IslandWindow.xaml.cs`、`tests/StartupPanelsCheck/Program.cs`
- Create: `tests/WidgetKindsCheck/`（新自检工程）、登记 `tools/Run-Checks.ps1`

- [ ] **Step 1: 写失败自检**

新建 `tests/WidgetKindsCheck/WidgetKindsCheck.csproj`（照抄 `ConfigSafetyCheck.csproj` 模板，`AssemblyName=Kobold.WidgetKindsCheck`），`Program.cs` 用既有 Check 结构覆盖：

```csharp
// 1) 老配置只有 Folders → 加载后全部迁到 Widgets，Id/顺序/字段不变
var legacy = @"{ ""Folders"": [ { ""Id"": ""f1"", ""Name"": ""A"", ""Color"": ""#EF4444"", ""GridColumns"": 3,
  ""Items"": [ { ""Name"": ""x"", ""Path"": ""C:\\x"" } ] } ] }";
File.WriteAllText(main, legacy);
var cfg = AppConfig.Load(main, backup);
Check(cfg.Widgets.Count == 1 && cfg.Widgets[0] is FolderData, "legacy Folders migrate into Widgets");
Check(((FolderData)cfg.Widgets[0]).Id == "f1", "migration keeps the Id");
cfg.Save();
var saved = File.ReadAllText(main);
Check(saved.Contains("\"Widgets\"") && !saved.Contains("\"Folders\""), "saving writes Widgets and no Folders");

// 2) 缺 Kind → folder
// 3) 认不得的 Kind（如 "weather"）→ UnknownData：加载后仍在列表里，保存原样写回
// 4) 两者都有 → Widgets 优先
// 5) Todo 往返：父项 Date/Done/CompletedDate + Children（子项只有 Id/Text/Done）
// 6) 全新配置 → 一个 folder 组件（现有默认行为）
```

- [ ] **Step 2: 运行并确认失败** → `dotnet run --project tests/WidgetKindsCheck` 编译失败（类型不存在）。

- [ ] **Step 3: 实现**

```csharp
// Core/WidgetData.cs
public abstract class WidgetData {
    public string Id { get; set; } public string Name { get; set; } public string Color { get; set; }
    public int PosX { get; set; } public int PosY { get; set; } public bool IsExpanded { get; set; }
    public bool IsLocked { get; set; } public bool IsPanelPinned { get; set; }
    public double? PanelX { get; set; } public double? PanelY { get; set; } public double? PanelDpiScale { get; set; }
    public string Kind { get; set; }
}
public class UnknownData : WidgetData { public JObject Raw { get; set; } }
```

`FolderData` 继承后删除重复字段；`TodoData`/`TodoItem`/`SubTodoItem` 按 spec §3.1（子项类型无 `Children`）。

`WidgetDataConverter`：写时先 `writer.WritePropertyName("Kind")`（`UnknownData` 直接写 `Raw`）；读时 `JObject.Parse` 后看 `Kind`（缺失/空 → `folder`，`folder`/`todo` 分派，其余 → `UnknownData` 存 `Raw`）。

`AppConfig`：`public List<WidgetData> Widgets { get; set; }`；保留
`[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<FolderData> Folders` 只作读入口；
`Load` 末尾 `MigrateLegacyWidgets()`：`Widgets` 空且 `Folders` 非空 → 整表 `Cast<WidgetData>` 迁入、`Folders=null`；
`RepairLegacyItems` 遍历 `Widgets`（folder 规则照旧；todo 补空 Id、`Date`/`CompletedDate` 取 `.Date`）。

编译跟进：`StartupPanels.ToRestore(IEnumerable<WidgetData>)`；`WidgetManager` 遍历 `_config.Widgets`
（阶段 1 只对 `FolderData` 建窗口，Todo 暂不实例化）；`IslandWindow.SetWidgets(IEnumerable<WidgetData>)`；
`StartupPanelsCheck` 的类型跟进。`FolderWidget` 构造仍收 `FolderData`（`is FolderData f` 过滤）。

- [ ] **Step 4: 自检转绿** → `dotnet run --project tests/WidgetKindsCheck` 全 PASS。

- [ ] **Step 5: 全量回归** → `dotnet build Kobold.csproj -c Release`（0 警告）；
  `Run-Checks.ps1 -Group all` 25 项 + 新项全绿；在 `Run-Checks.ps1` 矩阵加
  `@{ Name = 'WidgetKindsCheck'; Ci = $true }`。

- [ ] **Step 6: 提交（需用户许可）** → `feat(widgets): add the widget data model and Folders->Widgets migration`

### Task 1.2: WidgetKindRegistry + Todo 修复规则

**Files:** Create `Core/WidgetKindRegistry.cs`；Modify `Core/AppConfig.cs`（todo 修复细化）、`tests/WidgetKindsCheck/Program.cs`

- [ ] **Step 1: 追加失败自检**：
  `WidgetKindRegistry.Find("todo").NameKey == "UI_DefaultTodoName"`；`Find("weather") == null`；
  `CreateDefault("todo")` 返回空 `TodoData`、`CreateDefault("folder")` 返回带默认名/色的 FolderData；
  修复：todo 条目缺 Id → 补非空；`Date` 带时间 → 加载后 `.Date`；子项缺 Id → 补。

- [ ] **Step 2: 运行确认失败**（编译失败）。

- [ ] **Step 3: 实现**：

```csharp
public sealed class WidgetKind {
    public string Id; public string NameKey; public string IconKey; // "folder" | "todo"
    public bool SupportsGridColumns; public bool SupportsItemScale; public bool SupportsMaxRows;
    public Func<WidgetData> CreateDefault;
}
public static class WidgetKindRegistry { public static IReadOnlyList<WidgetKind> All { get; }
    public static WidgetKind Find(string id); }
```

- [ ] **Step 4: 自检转绿 + 全量回归**（同 Task 1.1 Step 5）。

- [ ] **Step 5: 提交（需用户许可）** → `feat(widgets): add the widget kind registry`

---

## 阶段 2：新壳 + Todo（FolderWidget 不动）

### Task 2.1: WidgetChrome 壳控件

**Files:** Create `Controls/WidgetChrome.xaml(.cs)`；Modify `tests/XamlLoadCheck/Program.cs`

- [ ] **Step 1: XamlLoadCheck 追加失败用例**：在 App 资源装载后 `new WidgetChrome()`、设置 Title/Lock/Pin、`ApplyTheme()` 不抛异常；三种主题各一次。

- [ ] **Step 2: 运行确认失败**（编译失败）。

- [ ] **Step 3: 实现**（结构照搬 `FolderWidget.xaml` 的壳，去掉文件夹内容）：

```xml
<!-- WidgetChrome.xaml: UserControl -->
<Grid>
  <Border x:Name="Shell" CornerRadius="16" BorderThickness="1" BorderBrush="{DynamicResource Kobold.Brush.PanelBorder}">
    <Grid>
      <Grid.RowDefinitions><RowDefinition Height="40"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
      <Border x:Name="Header" Grid.Row="0" CornerRadius="16,16,0,0" Padding="12,0" Cursor="Hand">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/>
          </Grid.ColumnDefinitions>
          <ContentPresenter x:Name="HeaderLeading" Grid.Column="0"/>
          <TextBlock x:Name="TitleText" Grid.Column="1" FontSize="13" FontWeight="SemiBold"
                     VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
          <ContentPresenter x:Name="HeaderTrailing" Grid.Column="2"/>
          <!-- 锁定 / 置顶按钮：Path 与 FolderWidget 相同 -->
        </Grid>
      </Border>
      <ContentPresenter x:Name="ContentHost" Grid.Row="1"/>
      <ContentPresenter x:Name="FooterHost" Grid.Row="2"/>
    </Grid>
  </Border>
</Grid>
```

代码侧 API：`Title`、`IsLocked`/`IsPinned`（含图标状态）、`ApplyTheme()`、`ApplyOpacity(double)`；
事件：`LockClicked`、`PinClicked`、`HeaderDragStarted/Moved/Ended`（把鼠标事件转发成
`DragStarted(Point screenCursor)` 等，由宿主窗口做移动）、`HeaderRightClicked`；
插槽访问器：`HeaderLeading`/`HeaderTrailing`/`ContentHost`/`FooterHost`。

- [ ] **Step 4: 自检转绿 + 全量回归**。

- [ ] **Step 5: 提交（需用户许可）** → `feat(widgets): add the shared panel chrome control`

### Task 2.2: WidgetWindowBase + IWidgetWindow + TodoWidget 骨架 + 接线

**Files:**
- Create: `Controls/IWidgetWindow.cs`、`Controls/WidgetWindowBase.cs`、`Controls/TodoWidget.xaml(.cs)`（先只有壳+空内容）
- Create: `Helpers/WidgetMenuBuilder.cs`
- Modify: `Core/WidgetManager.cs`（kind 工厂、`List<IWidgetWindow>`、新建入口）、`Controls/IslandWindow.xaml.cs`（`+` 弹类型菜单、tile 图标按 Kind）、`Services/TrayIconService.cs`（新建子菜单）、`Controls/FolderWidget.xaml.cs`（显式实现 `IWidgetWindow`，行为不变）
- Modify: `Core/Localization.cs`（三语 key）、`tests/XamlLoadCheck/Program.cs`

**Interfaces:**

```csharp
public interface IWidgetWindow {
    string WidgetId { get; } WidgetData Data { get; } bool IsPanelOpen { get; } bool IsModalOpen { get; }
    (double width, double height) GetPanelSize();
    void ShowPanel(double defaultLeft, double defaultTop, bool activate = true);
    void HidePanel(); void ShowWidgetMenu(Action onClosed); void UpdateUI(); void RefreshTheme();
    event Action<IWidgetWindow> OnDeleted; event Action OnDataChanged;
}
```

- [ ] **Step 1: XamlLoadCheck 追加失败用例**：`new TodoWidget(new TodoData())` 可加载（三主题）。

- [ ] **Step 2: 运行确认失败**（编译失败）。

- [ ] **Step 3: 实现**
  - `WidgetWindowBase`：抽象 Window（`WindowStyle=None`、`AllowsTransparency`、透明背景、`ResizeMode=NoResize`、
    `ShowInTaskbar=False`）；组合 `WidgetChrome`；实现 `IWidgetWindow` 的**共享行为**（逐个从
    `FolderWidget.Interactions.cs:ShowPanel/HidePanel/PersistPanelState/ResolvePanelPosition` +
    `FolderWidget.Theme.cs` 透明度 + `FolderWidget.xaml.cs` 的 `WindowSwitcher` 接线复制过来，
    只改元素引用为 `Chrome.Shell`）；虚方法 `GetContentSize()` 由子类给内容尺寸；
    `OnPanelShown/OnPanelHidden` 空虚钩子；面板位置/DPI 逻辑与 folder 现行为一致。
  - `WidgetMenuBuilder.Build(IWidgetWindow host, WidgetKind kind, Action onClosed)`：公共区
    （显示/收起、改名、颜色、锁定、置顶、删除）+ 按 `kind.Supports*` 追加专属区（folder: 列数/大小/行数；
    todo: 无）。Todo 用它；folder 继续用旧菜单（阶段 3 再切）。
  - `TodoWidget`：`WidgetChrome` 宿主 + 空 `TodoCalendarView` 占位；kind 菜单 = `WidgetMenuBuilder`。
  - `FolderWidget` 显式实现 `IWidgetWindow`（`WidgetData IWidgetWindow.Data => _data;` 等）。
  - `WidgetManager`：`_widgets: List<IWidgetWindow>`；`CreateWidgetInternal(WidgetData)` 按
    `Kind`/registry 建窗口；`_config.Widgets` 循环跳过非 folder/todo；`CreateWidget(kind)` 公共默认
    （调色板取色、错位、`IsPanelPinned=true`）后 `RefreshIsland + OpenPanel + SaveConfig`。
    新建入口：岛 `AddTile` 弹 `ContextMenu`（文件夹 / 待办，本地化），托盘"新建"子菜单同两项。
  - 岛 tile：`CreateTile` 里 `Kind == "todo"` → 日历字形 Path（组件色），否则现有渲染器。
  - 本地化（三语）：`UI_WidgetKindFolder`、`UI_WidgetKindTodo`、`UI_DefaultTodoName`、`UI_NewWidgetFormat`。

- [ ] **Step 4: 自检转绿 + 全量回归 + 手动冒烟**（FolderWidget 零改动）：
  岛 `+` 出类型菜单 → 新建待办 → 面板出现在岛下方；开合/拖动/锁定/置顶/改名/颜色/删除；
  主题切换；Alt+Tab 不出现；面板位置重启恢复；文件夹组件全部照旧（开合/菜单/拖拽抽查）。

- [ ] **Step 5: 提交（需用户许可）** → `feat(widgets): add the todo widget shell and kind wiring`

### Task 2.3: Todo 纯逻辑模块 + 自检

**Files:** Create `Core/TodoRollover.cs`、`Core/TodoCalendar.cs`、`Core/TodoListOps.cs`；
Create `tests/TodoRolloverCheck/`、`tests/TodoCalendarCheck/`；Modify `tools/Run-Checks.ps1`

- [ ] **Step 1: 写失败自检**（两个新工程，`Ci=$true`），关键用例：

```csharp
// TodoRolloverCheck（today = 2026-09-28）
DisplayDate(未完成, Date=9/25, today) == 9/28;            // 顺延显示在今天
IsCarried(未完成, Date=9/25, today) == true; CarriedDays == 3;
DisplayDate(未完成, Date=10/02, today) == 10/02;          // 未来项留在自己日期
DisplayDate(已完成 CompletedDate=9/26, Date=9/25) == 9/26;// 完成项显示在完成日
Toggle(父, true) → CompletedDate=9/28 且 所有子项.Done==true;   // 级联
Toggle(父, false) → 父未完成、子项保持各自状态;              // 不反向级联
ToggleChild 只动子项;
// TodoCalendarCheck（三种 culture：en-US/zh-CN/ja-JP 的 FirstDayOfWeek）
BuildMonth(2026, 9) 覆盖整月、每格唯一、周数 4~6;
首格 = 9/1 所在周的 FirstDayOfWeek; 跨年 ±1 月（2026-12 / 2027-01）;
cell.IsToday/IsSelected/InMonth 标记正确; TopLevelCount 只数顶层;
// TodoListOpsCheck（并入 TodoCalendarCheck 或单独）：AddItem 顺序、完成置底稳定排序、
ForDay 只返回顶层且只含 DisplayDate 命中该日的条目、RemoveItem 连带子项。
```

- [ ] **Step 2: 运行确认失败**（编译失败）。

- [ ] **Step 3: 实现**（签名按 spec §5.3；`DisplayDate/Toggle/ForDay` 见上；`BuildMonth` 返回
  `List<TodoDayCell>{ Date, InMonth, IsToday, IsSelected, TopLevelCount }`）。

- [ ] **Step 4: 自检转绿 + 全量回归**。

- [ ] **Step 5: 提交（需用户许可）** → `feat(todo): add the pure rollover, calendar and list logic`

### Task 2.4: Todo 内容视图（月历 + 清单 + 子待办）

**Files:** Create `Controls/TodoCalendarView.xaml(.cs)`；Modify `Controls/TodoWidget.*`、
`tests/XamlLoadCheck/Program.cs`、`Core/Localization.cs`

- [ ] **Step 1: XamlLoadCheck 追加失败用例**：`new TodoWidget(new TodoData())` + 视图渲染空数据不抛异常。

- [ ] **Step 2: 运行确认失败**。

- [ ] **Step 3: 实现**
  - 顶部：`‹ 2026年9月 ›`（culture 格式化）+ 非本月时显示的「今天」；
  - 星期表头 + 6×7 `UniformGrid`：日号、标记点（≤3 + 「+」）、今天描边、选中填充、非本月灰；
    点击非本月格 → 先切月再选中；
  - 清单：`ItemsControl`（父模板：复选框/文本/顺延角标「↷ M/d」/悬停「＋」「×」；子模板：缩进、
    复选框/文本/悬停「×」）；完成项置底划线；底部「＋ 添加待办…」行内录入（Enter 连续）；
    单击文本就地编辑（Enter/Esc）、空文本提交=删除；
  - 子项：父行「＋」就地加子项（Enter 连续）；勾选父项走 `TodoListOps.Toggle`（级联）；
  - `SelectedDate` 持久化（`TodoData.SelectedDate`，`SaveConfig` 走防抖）；
  - 面板尺寸：宽 340；高 = 月历块 + `clamp(清单行, 2, 6)` 行；`OnDataChanged` 后重算一次
    （条目增删/换日时更新面板高度，超出滚动）；
  - 本地化（三语）：`Todo_TodayButton`、`Todo_AddPlaceholder`、`Todo_AddChild`、`Todo_CarriedBadge`、
    `Todo_ItemCountFormat`、`Todo_EmptyDay`。

- [ ] **Step 4: 自检转绿 + 全量回归 + 手动冒烟**：
  选中昨天加待办 → 回今天出现「↷ 9/27」角标；勾选 → 显示在完成日；取消 → 回到今天；
  加子项/勾选子项；勾选父项级联；删除父项连带子项；点未来日期加任务留未来；
  换月/跨年导航；「今天」按钮；重启后选中日与任务保留。

- [ ] **Step 5: 提交（需用户许可）** → `feat(todo): render the calendar, day list and sub-todos`

---

## 阶段 3：FolderWidget 迁到共享壳

### Task 3.1: FolderWidget 的 XAML 换成 WidgetChrome

**Files:** Modify `Controls/FolderWidget.xaml(.cs)`、`Controls/FolderWidget.Theme.cs`（元素引用改走 Chrome）

- [ ] **Step 1: 全量自检基线**（迁移前先确认绿，作为对照）。
- [ ] **Step 2: 换 XAML**：删除窗口 XAML 里的壳（Border/Header/锁/钉），改为 `WidgetChrome` 宿主；
  返回/帮助/路径按钮塞进 `HeaderLeading`/`HeaderTrailing` 插槽；文件网格留在 `ContentHost`、
  截断提示留在 `FooterHost`；code-behind 里 `ExpandedPanel`/`PanelHeader`/`LockButton` 等引用改走
  `Chrome.*`；拖拽/右键/锁定/置顶接到 Chrome 事件。
- [ ] **Step 3: 自检全绿** + `dotnet build` 0 警告。
- [ ] **Step 4: 手动冒烟清单**：开合（含动画）、Header 拖动与 DPI 恢复、锁定/置顶按钮状态、
  主题切换、透明度设置、Alt+Tab 隐藏、组件菜单（改名/颜色/列数/大小/行数/删除）、
  浏览文件夹（进入/返回/路径菜单）、拖入/拖出、文件夹颜色、关机保存。
- [ ] **Step 5: 提交（需用户许可）** → `refactor(widget): move FolderWidget onto the shared chrome`

### Task 3.2: 共享行为收敛到基类 + 菜单切换

**Files:** Modify `Controls/FolderWidget.xaml.cs`、`Controls/FolderWidget.Interactions.cs`、
`Controls/FolderWidget.MenuActions.cs`、`Controls/WidgetWindowBase.cs`

- [ ] **Step 1: 基线自检**。
- [ ] **Step 2: 收编**：`FolderWidget : WidgetWindowBase`；删除与基类重复的
  `ShowPanel/HidePanel/PersistPanelState/ResolvePanelPosition/透明度` 实现，改调基类；
  文件夹独有的 `UpdateBrowseWatch` 走 `OnPanelShown/OnPanelHidden` 钩子；
  组件菜单改用 `WidgetMenuBuilder`（颜色子菜单的构建逻辑搬进 builder，`MenuRenderCheck` 必须继续绿）。
- [ ] **Step 3: 全量自检 + 构建**。
- [ ] **Step 4: 手动冒烟**（同 Task 3.1 Step 4 清单再跑一遍，重点：锁定/置顶、面板重启恢复、菜单项齐全）。
- [ ] **Step 5: 提交（需用户许可）** → `refactor(widget): share the panel behaviours through the base window`

---

## 阶段 4：收尾

### Task 4.1: README + CHANGELOG

**Files:** Modify `README.md`（英/中）、`CHANGELOG.md`

- [ ] **Step 1: README**（英中同步）：
  - 功能表加「组件类型」条目（文件夹 / 待办；新建时选择）与「待办组件」条目（月历、当日清单、
    顺延角标、一层子待办）；
  - 用法表加：新建待办、选中日期、添加子待办、勾选级联；
  - 「数据」节加降级说明：新配置格式（`Widgets`）不被 ≤1.3.0 读取，回退旧版会重置设置并把原文件
    保留为 `config.json.failed-*`。
- [ ] **Step 2: CHANGELOG「Unreleased」**：新增条目（组件类型框架、Todo 组件、一层子待办）；
  兼容性一句话（升级无损、降级有损）。
- [ ] **Step 3: 提交（需用户许可）** → `docs: document the widget kinds and the Todo widget`

### Task 4.2: 全量回归 + 计划执行记录

- [ ] **Step 1:** `powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all` 全绿
  （预期 28 项：现 25 + WidgetKindsCheck + TodoRolloverCheck + TodoCalendarCheck）。
- [ ] **Step 2:** 手动验收记录写进本计划「执行记录」：Todo 冒烟结果、阶段 3 迁移冒烟结果、
  未验证项（混合 DPI/多屏受单屏限制，如实声明）。
- [ ] **Step 3:** 提交执行记录（需用户许可）→ `docs(plans): record the widget kinds execution`

---

## Self-Review

- **Spec 覆盖**：§3 数据模型/迁移/注册表=Task 1.1/1.2；§4 壳/基类/菜单/接线=Task 2.1/2.2、阶段 3；
  §5 Todo=Task 2.3/2.4；§6 测试=各任务 + 4.2；§7 阶段=本计划四阶段；§8 风险=阶段 3 的顺序与冒烟、
  README 降级说明（4.1）。
- **占位符扫描**：无 TBD；所有签名、用例、命令为最终值；界面像素值（340 宽、2~6 行）由实现微调，
  不影响契约。
- **类型一致性**：`IWidgetWindow`（阶段 2 起）与 `WidgetWindowBase`（阶段 3 收敛）命名一致；
  `WidgetData.Kind` 取值 `folder`/`todo` 与 registry、转换器、岛图标分支一致。
- **风险**：阶段 3 是唯一触碰现有 UX 的重构——Todo 先证抽象、单独提交、全量自检 + 手动清单，
  回退点是阶段 3 之前。
