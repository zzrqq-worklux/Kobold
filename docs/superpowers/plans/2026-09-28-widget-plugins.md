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

  **冒烟中用户逐条拍板的设计调整（已实现、已复审）**：添加行=全宽圆角「＋」；列表按内容收缩、＋紧跟最后一条待办；空列表时点＋就地取代「这一天没有待办」；添加/编辑行行首渲染未勾选方框、无 placeholder；行右键菜单=添加子项/删除（编辑仍左键点文本、Enter 连续添加子项保留）；编辑中点别处自动保存；字号平衡（父 14/子 13、日历格 28）；面板底色跟随组件色；尺寸变化后自动收进显示器工作区。

  **遗留（已修复，2026-10-08）**：编辑/添加行的白底已移除（r11，编辑行与面板同底色、仅光标提示；空列表 = 勾选行 + 全宽圆角＋，无文案）；后续冒烟轮次 r12-r17 还完成了：面板尺寸含壳边框（修 ＋ 下边框裁切）、空态无文案、＋ 与边框/图标跟随组件色（Material 衍生色：底色 ~12%、边框 ~30%）、日历选中日 = 用户手绘圆圈（颜色跟随组件色、圈径加大）、今天 = 实心圆底 + 手绘圈、文件夹选中样式跟随组件色、日切换高度强制重算（修 stale measure）、点击即编辑（I 形光标 + 光标落在点击处，不再全选）。全部经 scoped 复审；r17 构建已部署并实测。**剩余：用户冒烟确认 → 提交 Task 2.4。**

- [ ] **Step 5: 提交（需用户许可）** → `feat(todo): render the calendar, day list and sub-todos`

### Task 2.5: 日历假日标记 + 农历/节气/节日小字（方案确认于 2026-10-08）

**用户确认的范围：** 放假标记优先（中国/日本/美国/英国；组件右键菜单单选地区，默认中国）；中文界面日期格第二行小字（假日 > 节气 > 农历日；农历用 .NET 自带 `ChineseLunisolarCalendar`，无新依赖）；中国的官方调休表**手写内置**（2025/2026 两年 72 行，来源=国务院公告并在文件头注明链接；**不做抓取、不做定时任务、运行时零网络**；2027 公布后手动补录，无数据年份只显示农历/节日基础层）；日本/美国/英国按法定规则计算；日头不加农历；选中日样式（手绘圈）已在 Task 2.4 落地。

**Files:** Create `Core/LunarCalendar.cs`、`Core/PublicHolidays.cs`、`Core/CnHolidayData.cs`（手写数据表，2025/2026）、`tests/LunarCalendarCheck/`；Modify `Core/TodoData.cs`（`HolidayRegion`）、`Controls/TodoCalendarView.xaml.cs`（日期格第二行）、`Controls/TodoWidget.xaml.cs` + `Helpers/WidgetMenuBuilder.cs`（假日地区子菜单）、`Core/Localization.cs`（7 个 key ×3）、`tests/WidgetKindsCheck/Program.cs`、`tests/XamlLoadCheck/Program.cs`、`tools/Run-Checks.ps1`

**执行入口：** `.superpowers/sdd/2026-09-28-widget-plugins/task-2.5-brief-v2.md`（含 72 行数据表全文、全部锚点与规则清单）。

- [ ] **Step 1–4: TDD**（LunarCalendarCheck：农历/节气/节日、CN 表锚点、JP/US/UK 规则、region 解析）；实现；全量回归（矩阵 28 项 + installer = 29 行）。
- [ ] **Step 5: 手动冒烟**（用户）：2026-10 格子（10/1 国庆、10/10 班、10/8 寒露、农历日）；切地区（日本 9/22 国民休日、美国 11/26 感恩节、英国 12/28 节礼日补休）；「关闭」只留农历层；en/ja 界面无小字；组件菜单勾选状态；重启后地区保留。
- [ ] **Step 6: 提交（需用户许可）** → `feat(todo): mark lunar dates, solar terms and holidays in the calendar`

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

### Task 3.3（用户新增，2026-10-09 确认）: 岛图标样式精简 + 手绘风 + 实时刷新（folder + todo）

**决策（用户拍板）**：只留 `classic` + 手绘（`handdrawn`）两种，**folder 与 todo 两种组件的岛 tile 都适用**；modern/minimal/rounded/flat/gradient 五个渲染器与下拉项**直接删除**，不写旧值迁移/兜底代码（`IconRendererFactory` 的 default 分支天然回落 classic 即可）；手绘做 **B 方案**：把抖动离线烘焙进固定的 Path 几何数据（WPF 不支持 SVG `feTurbulence`/`feDisplacementMap`，渲染期零随机、零滤镜）；修复"改设置不刷新"：样式变更/保存/取消都必须立刻重建岛 tile。颜色适配：folder=组件色+浅/深派生；todo=纸张/墨线跟主题（浅/深/高对比），标题栏/日期块/圈注/勾选跟组件色（用户 2026-10-09 补充的两份 Todo SVG 见 `task-3.3-brief.md`；映射推荐方案待用户最终确认）。

**Files:** Modify `Controls/IconRenderers/IconStyles.cs`（删 5 个渲染器）、`Controls/IconRenderers/IconRendererBase.cs`（工厂 2 分支 + default + `CreateTodo`）、Create `Controls/IconRenderers/HandDrawnIconRenderer.cs`、Create `Controls/IconRenderers/TodoIconStyles.cs`（Classic/HandDrawn Todo）、`Controls/IslandWindow.xaml.cs`（todo 分支走 CreateTodo、删 RenderTodoGlyph、`RefreshIconStyle()`）、`Windows/SettingsWindow.xaml(.cs)`（下拉 2 项、emoji/key 数组、三处调用新刷新）、`Core/Localization.cs`（三语：加 `Settings_HandDrawn`、删 5 个退休 key）、`Core/AppConfig.cs`（注释）、`Core/WidgetManager.cs`（新 `ApplyIconStyle()` → `RefreshIconStyle()`）、`tests/XamlLoadCheck/Program.cs`（工厂分派/颜色/主题适配/刷新链路 pins；不新增工程，矩阵保持 29）。

**根因（已定位）**：`SettingsWindow.IconStyleCombo_SelectionChanged` → `WidgetManager.RefreshAllWidgets()`（WidgetManager.cs:384-390）只调 `widget.RefreshTheme()`；岛 tile 只在 `RefreshIsland()`/`SetWidgets()` 时经 `IslandWindow.RebuildTiles()`（读 `Config.IconStyle`，IslandWindow.xaml.cs:487）重建，所以样式重启才生效。

- [ ] **Step 1: 失败自检**（工厂分派 classic/handdrawn、退休值→classic、手绘渲染色来自组件色、深色主题描边适配、改样式后岛 tile 重建被触发）。
- [ ] **Step 2: 实现**（渲染器 + 设置链路；手绘路径数据离线生成后固化，临时生成脚本不入库）。
- [ ] **Step 3: 自检转绿 + 全量回归** → `Run-Checks.ps1 -Group all` 29/29、`dotnet build` 0 警告。
- [ ] **Step 4: 手动冒烟**（用户）：浅/深/高对比主题下 classic 与手绘的观感与可读性；下拉切换即时生效；取消回滚；保存后重启保留。
- [ ] **Step 5: 提交（需用户许可）** → `feat(island): slim the widget icon styles to classic and hand-drawn`

### Task 3.4（用户新增，2026-10-09 确认）: 组件取色板统一为 9 色

**决策**：组件取色板（`UiTokens.FolderPalette`）由 12 色替换为与文件夹变色（`FolderIconPalette`）一致的 9 色：红橙黄绿(青柠)绿青蓝紫灰，同值同序；老组件已用颜色**不迁移**（取色板不再提供即可）；新组件默认取色循环改为 蓝→红→绿→紫→橙（保持现有观感）。

**Files:** Modify `Core/UiTokens.cs`、`Core/WidgetManager.cs`（`CreateWidget` 默认取色循环）、`tests/MenuRenderCheck/Program.cs`（板一致性 pin）。

- [ ] **Step 1: 失败自检**（`FolderPalette` 长度 9 且与 `FolderIconPalette` 逐元素相等）。
- [ ] **Step 2: 实现**（数组替换 + 默认循环）。
- [ ] **Step 3: 自检绿 + 全量回归 29/29 + build 0 警告**。
- [ ] **Step 4: 冒烟**（用户）：组件菜单→颜色只有 9 色；新建组件默认色循环；老颜色组件显示正常。
- [ ] **Step 5: 提交** → `refactor(widgets): align the widget colour palette with the folder palette`

### Task 3.5（用户新增，2026-10-09 确认）: Kobold 存储固定到快速访问

**决策**：设置窗口新增「Kobold 存储」行：显示 `%AppData%\Kobold\Storage` 路径 + 「打开文件夹」+ 「固定到快速访问 / 取消固定」开关（显式、可逆、无需管理员）；实现用 Shell COM 规范动词 `pintohome` / `unpinfromhome`；不做 Shell 命名空间扩展、不映射盘符（文件选择器左侧默认显示"快速访问"，固定即出现在那里）。COM 失败/组策略禁用时给出本地化提示。自动化测试只覆盖纯逻辑与只读状态查询，**绝不调用固定/取消动作**（人工冒烟执行）。

**Files:** Create `Helpers/QuickAccess.cs`；Modify `Windows/SettingsWindow.xaml(.cs)`、`Core/Localization.cs`（三语 keys）、`tests/*`（纯逻辑/只读 pins，选最合适的既有工程）。

- [ ] **Step 1: 失败自检**（路径匹配/状态解析纯函数；`TryIsPinned` 只读且不抛）。
- [ ] **Step 2: 实现**（COM 封装 + 设置行 + 失败提示）。
- [ ] **Step 3: 自检绿 + 全量回归 29/29 + build 0 警告**。
- [ ] **Step 4: 冒烟**（用户）：设置里固定 → 资源管理器与网页上传的文件选择器左侧「快速访问」出现 Kobold 存储；取消固定后消失；重启后状态保留。
- [ ] **Step 5: 提交** → `feat(settings): pin the Kobold storage to Quick Access`

**修复轮 1（用户冒烟 2026-10-09）**：① 存储行按钮太小、文字撑满 → 放大按钮（MinHeight 34-36、Padding 16,0、MinWidth，zh/en/ja 三语不裁切）；② 固定默认开启 → `AppConfig.PinStorageToQuickAccess`（默认 true）+ 启动时尽力固定（只强制 pin、不自动 unpin）+ 设置开关写配置、取消恢复原状态。提交 `fix(settings): ...`。

### Task 3.6（用户新增，2026-10-09 确认）: 系统 tile（隐藏桌面/设置/添加）手绘版

**决策**：岛的系统入口 tile（隐藏/取消隐藏桌面、设置、添加）也随 `IconStyle` 提供手绘版（classic = 现状）；用户提供 4 个手绘 SVG（显示器、显示器+斜线、齿轮、加号）；颜色跟随主题（`Kobold.Brush.IslandForeground`，浅/深/高对比自动适配）；抖动烘焙（B 方案，无滤镜）；`RefreshIconStyle()` 一并切换；桌面 tile 的斜线在两种样式下都要随隐藏状态显隐。

**Files:** Modify `Controls/IslandWindow.xaml(.cs)`（每 tile 双画布或等价切换 + 手绘几何常量）、`tests/XamlLoadCheck/Program.cs`（样式切换与斜线两态 pins）。

- [ ] **Step 1: 失败自检**（classic→手绘的可见性切换；隐藏桌面时两种样式的斜线都显隐正确）。
- [ ] **Step 2: 实现**（4 个手绘几何烘焙到 24 空间；颜色走主题资源）。
- [ ] **Step 3: 全量回归 29/29 + build 0 警告**。
- [ ] **Step 4: 冒烟**（用户）：两种样式下桌面/设置/添加 tile 观感；隐藏桌面切换斜线；主题切换颜色跟随。
- [ ] **Step 5: 提交** → `feat(island): add hand-drawn system tile icons`

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

---

## 会话交接（2026-10-08，本次会话结束时写入）

**仓库/工作区**
- 主仓库 `C:\Users\31177347\Desktop\Coding\Kobold`
- 隔离工作区：`.worktrees/widget-plugins`（分支 `widget-plugins`），HEAD = `3b25c69`
- SDD ledger：`.worktrees/widget-plugins/.superpowers/sdd/2026-09-28-widget-plugins/progress.md`
  （含全部 Ruling、minor 清单、每轮快照编号、2026-10-08 交接段；新会话先读它）
- 审查快照工具：同目录 `snap.sh` / `pkg.sh`；本会话的审查包按 r11-r17 命名，
  生成时排除计划文件（`:(exclude)docs/superpowers/plans/2026-09-28-widget-plugins.md`）

**已完成（已提交）**
- Task 1.1 `4cd97f7`、Task 1.2 `ee863cc`、Task 2.1 `fe392f4`、Task 2.2 `52b6cac`、Task 2.3 `3b25c69`

**进行中（已暂存、未提交）**
- Task 2.4 全部实现 + 冒烟轮次 r0-r17 的修复（暂存于 index，11 个文件；最近复审快照 `8ef4f82f`，
  r17 构建已部署并实测）；**未提交**——等用户冒烟确认后由实现子代理提交。

**遗留事项（按顺序执行）**
1. 用户冒烟 r11-r17（高度收放、点击即编辑、＋/选中样式、功能清单）→ OK 后提交 Task 2.4：
   `feat(todo): render the calendar, day list and sub-todos`（只提交 index 中的 11 个暂存文件，勿含计划文件）；
2. Task 2.5 农历：用户已确认"选中样式=手绘圈"，**其余 3 个决策（显示范围/内容范围/日头是否加农历）
   仍未回答**——先确认再实施；
3. 阶段 3：Task 3.1 / 3.2 → **提交前先把 diff 给用户过目** → 冒烟；
4. 阶段 4：Task 4.1（README/CHANGELOG）、Task 4.2（全量 Run-Checks + 执行记录；2.5 未落地时
   矩阵=28 项 + installer=29 行）；
5. 终审（整分支，含 ledger 的 minor/裁决清单）→ finishing-a-development-branch；
6. 清理：用户配置里的探针组件 `sdd-layout-probe`。

**流程约定（延续）**
- subagent-driven-development：每任务 = 实现子代理（TDD）→ 暂存快照 → 审查 → 修复循环 → 复审 → 用户冒烟 → 提交；
- 提交权限：自检绿 + 审查通过后由实现子代理提交；**阶段 3 的重构提交必须先给用户过目**；
- 冒烟用 `tools\deploy.ps1`（装到 `%LocalAppData%\Programs\Kobold` 并重启）——预期行为；
- 手动冒烟由用户操作；混合 DPI/多屏受单屏限制，如实声明；
- 硬约束：net48/C# 7.3、不新增依赖；三语 key 同步（LangCheck 绿）；新自检登记进 `tools/Run-Checks.ps1`；
  （例外记录：r15 经用户明确要求改了 FolderWidget 的选中样式，属用户设计调整，非计划外重构）
- 用户的设计调整优先于计划草图；争议以用户为准；**新需求先与用户确认方案再实施**。

---

## 会话交接（2026-10-09，本次会话结束时写入）

**仓库/工作区**
- 主仓库 `C:\Users\31177347\Desktop\Coding\Kobold`；隔离工作区 `.worktrees/widget-plugins`（分支 `widget-plugins`），HEAD = `7f68856`
- SDD ledger：`.worktrees/widget-plugins/.superpowers/sdd/2026-09-28-widget-plugins/progress.md`（含全部 Ruling、minor 清单、每轮快照编号；新会话先读）
- 启动 prompt 副本：同目录 `handoff-prompt-2026-10-09.md`；审查快照工具 `snap.sh` / `pkg.sh`（通用版 `pkg18.sh`），审查包排除本计划文件

**已完成（已提交）**
- Task 1.1 `4cd97f7`、1.2 `ee863cc`、2.1 `fe392f4`、2.2 `52b6cac`、2.3 `3b25c69`
- Task 2.4 `7e82193`（含 r0-r18 修复；r11-r18 冒烟通过）
- Task 2.5 假日/农历标记 `f0272eb`（12 文件；CN 调休表手写 72 行、零抓取；JP/US/UK 规则计算；冒烟通过）
- 点击编辑 r19 修复 `7f68856`（编辑 A 时点 B：全选闪烁 + 编辑器被关闭；根因=一次性光标点 + 重建销毁编辑器的过期延迟提交；冒烟通过）

**进行中**
- 无未提交代码；**计划文件本身的修订未暂存**（会话交接 + Task 2.5 新章节），留给 Task 4.2 的执行记录提交，勿并入其他提交。

**遗留事项（按顺序）**
1. 阶段 3：Task 3.1（FolderWidget 换 WidgetChrome）→ **提交前先把 diff 给用户过目**（硬性要求）→ 提交 → 冒烟；Task 3.2 同样"diff 先过目"。
2. **方向 A 的 spec/plan**：用户 2026-10-09 批准方向 A（原生自由拖动缩放 + 通用自适应引擎）。要点：取代文件夹 列数/图标大小/最大行数 手动设置（旧字段做迁移输入）；Todo 第二行小字按尺寸显隐（日历密度问题随之解决）；复用现有布局代码（PanelRows/ItemScale/格高字号常量→纯函数适配器）。**阶段 3 完成后先起草 A 的 spec/plan 给用户审，不要直接实施；不做便宜的密度止血（用户明确不要）。A 不属于本计划。**
3. 阶段 4：Task 4.1（README/CHANGELOG，英中同步）；Task 4.2（全量 `Run-Checks.ps1 -Group all` 29 行 + 执行记录，含把计划文件未暂存修订一并提交）。
4. 终审（整分支审查，含 ledger 的 minor/裁决清单）→ `finishing-a-development-branch`。
5. 清理：删除用户配置里的探针组件 `sdd-layout-probe`（`%APPDATA%\Kobold\config.json`）。

**流程约定（延续）**
- subagent-driven-development：每任务 = 实现子代理（TDD）→ 快照 → 审查/复审 → 用户冒烟 → 提交；
- 提交权限：自检绿 + 审查通过后由实现子代理提交；**阶段 3 的重构提交必须先给用户过目**；
- 冒烟用 `tools\deploy.ps1`（装到 `%LocalAppData%\Programs\Kobold` 并重启）——预期行为；
- 硬约束：net48 / C# 7.3、不新增依赖；三语 key 同步（LangCheck 绿）；新自检登记进 `tools/Run-Checks.ps1`；混合 DPI/多屏受单屏限制，如实声明；
- **不要驱动用户真实鼠标做复现**（r19 教训：脚本点击会卡住子代理并干扰用户；用离线 pin + 插桩日志复现）；
- 用户的设计调整优先于计划草图；**新需求先与用户确认方案再实施**。

**2026-10-09 会话中新增（已确认）**：Task 3.3 = 岛文件夹图标样式只留 classic + 手绘（删其余 5 个、无兜底）、手绘抖动离线烘焙（B 方案）、修复"改设置不重建岛"的刷新 bug；排在 Task 3.2 之后、Task 4.1 之前；细节见计划 Task 3.3 节与 `task-3.3-brief.md`。

