# Kobold 组件类型（插件）与 Todo 组件（Widget Kinds & Todo）设计

> **状态**：设计已确认（2026-09-28，三段评审 + 子待办一层约束修订通过），进入实施计划。
> **前置**：v1.3.0 已发布。本文除数据迁移外不改动现有组件的行为；文件夹组件功能保持不变。
> **决策依据**：代码走查（`Core/FolderData.cs`、`Core/AppConfig.cs`、`Core/WidgetManager.cs`、
> `Core/StartupPanels.cs`、`Controls/FolderWidget.*`、`Controls/IslandWindow.xaml.cs`）；
> PaperTodo 交互参考（仅交互范式，不复制任何代码或素材——PaperTodo 为 PolyForm NC 许可，与 MIT 不兼容）；
> 用户确认的三段设计。

## 1. 目标

组件（widget）不再只有"文件夹"一种内容形态：引入**组件类型（Kind）**概念，同一套面板壳、岛入口、
持久化与菜单体系下，可以挂载不同内容的组件。第一批类型：`folder`（现有）与 `todo`（新增）。
天气等后续类型只加类型模块，不再动框架。

成功标准：

- 新建组件时可在「文件夹 / 待办」之间选择；两种组件在岛、面板壳、菜单、开机恢复里的表现同构。
- 现有文件夹组件的一切行为零回归（浏览、拖拽、收纳、文件夹颜色、行数上限、多屏恢复等）。
- 配置从旧的 `Folders` 形态无损迁移到 `Widgets`；未知类型的条目原样保留、不丢数据。
- Todo 组件符合第 5 节的行为规格；所有可判定逻辑都有控制台自检覆盖。

## 2. 非目标（明确不做）

- **第三方动态插件**：无 DLL 加载器、无接口版本协商、无权限/沙箱、无插件目录。类型全部编译进主程序。
  这是对既有结论的延续（`docs/paper-todo-learnings.md` §7：插件协议栈是抽象税）。
- **天气组件**：留到框架落地之后，作为第二个类型验证扩展点（不在本设计内）。
- Todo 的：拖拽排序、提醒、文件/笔记关联、拖文件进列表、完成行为设置项（自动清除等）、撤销、
  多列表/标签/搜索（明确不做"中心式任务管理器"，保持单组件内的日视图）；层级调整（子项提升为
  父项、跨父项移动、子项拖拽）。**层级封顶在一层**（见 §3.1 / §5.2），这是约束不是延后项。
- 不改动岛、托盘、设置窗口的既有信息架构（只增加"新建"的类型选择）。

## 3. 数据模型与配置迁移

### 3.1 类型

```
WidgetData (abstract)                 共享字段:
├─ Id / Name / Color                  Id, Name, Color, Kind,
├─ FolderData : WidgetData           PosX/PosY, IsExpanded, IsLocked,
│    Items / BrowseStack /           IsPanelPinned, PanelX/PanelY,
│    GridColumns / MaxPanelRows      PanelDpiScale（全部照旧）
├─ TodoData : WidgetData
│    List<TodoItem> Items; DateTime? SelectedDate（记住离开时选中的日期）
└─ UnknownData : WidgetData
     JObject 原始载荷（不实例化窗口，保存时原样写回）
```

`TodoItem`：`Id`（Guid 字符串）、`Text`、`Date`（仅日期有意义，写入前规范化为 `.Date`）、
`Done`、`CompletedDate`（完成当天，未完成/取消勾选为 null）、`List<SubTodoItem> Children`。
`SubTodoItem`：`Id`、`Text`、`Done`——**类型里没有 `Children` 字段，一层约束由类型保证**，
UI 与操作 API 不可能创建第三层。子项没有自己的日期与顺延：一切随父项。所有日期字段只存日期语义。

### 3.2 JSON 规则

- `AppConfig.Widgets : List<WidgetData>`；基类挂 `WidgetDataConverter`：
  - 写：先写 `"Kind"`，再写具体类型的字段；`UnknownData` 原样写回（含原 `Kind`）。
  - 读：按 `Kind` 分派具体类型；**缺 `Kind` 一律视为 `folder`**（老数据零成本）；
    不认识的 `Kind` → `UnknownData` 保留原始 JSON。
- 老属性 `Folders` 只作为迁移入口保留：`NullValueHandling.Ignore`，保存时不再写出。

### 3.3 迁移规则（在 `AppConfig.Load` 内，纯逻辑可测）

| 文件内容 | 行为 |
|---|---|
| 只有 `Folders`（≤1.3.0 格式） | 整表迁入 `Widgets`（顺序、Id、字段不动），清空 `Folders` |
| 只有 `Widgets` | 正常读取 |
| 两者都有 | `Widgets` 优先，忽略 `Folders`（异常形态，测试覆盖） |
| 两类都读不出 | 现有"证据保留 + 备份恢复 + 默认配置"机制不变 |

`RepairLegacyItems` 改为遍历 `Widgets`：文件夹条目修复逻辑照旧；待办条目补空 Id、空文本规范化。

### 3.4 已知代价：降级方向有损

新配置写出后，≤1.3.0 的旧版本读不出 `Folders`（该属性缺失）→ 走"证据保留 + 回默认"：
**不会静默丢数据**（`config.json.failed-*` 证据 + backup），但旧版本会重置为默认配置。
README 增加一句说明。升级方向（本版本读旧配置）无损，由迁移测试保证。

### 3.5 `WidgetKindRegistry`（`Core/`，纯数据）

每个类型一条：

| 字段 | folder | todo |
|---|---|---|
| `Id` | `folder` | `todo` |
| `NameKey`（本地化） | 复用 `UI_DefaultFolderName` | 新增 `UI_DefaultTodoName` |
| 图标（岛 tile） | 现有文件夹渲染器 | 日历字形（Path 绘制，组件色） |
| 菜单能力 | 列数 / 大小 / 行数 | 无 |
| `CreateDefault()` | 现有 `FolderData.Create` 规则 | 空 `TodoData` |

新建组件的公共默认（调色板取色、错位摆放、`IsPanelPinned=true`）由 `WidgetManager` 统一施加。

## 4. 窗口架构与接线

### 4.1 `WidgetChrome`（`Controls/WidgetChrome.xaml`，UserControl）

现有 `FolderWidget.xaml` 中**与内容无关**的壳：

- 圆角边框 + 阴影 + 面板底色/透明度 / `40px` 标题栏（标题、锁定、置顶、拖拽、右键菜单）+ 页脚宿主
- 三个插槽：`HeaderLeading`（folder：返回）、`HeaderTrailing`（folder：帮助/路径）、
  `ContentHost`（folder：文件网格；todo：月历+清单）、`FooterHost`（folder：截断提示）
- 锁/钉点击、标题拖拽、标题右键 → 以事件形式抛给宿主窗口（元素事件不再由 Window 的 code-behind 直连）

### 4.2 `WidgetWindowBase : Window`（abstract）

承载**共享面板行为**（从 `FolderWidget` 迁入，文件夹逻辑本身不动）：

- `ShowPanel/HidePanel`（含动画）、`PanelX/Y` + DPI 恢复（`PanelPlacement`/`MonitorInterop`）、
  面板透明度、`WindowSwitcher` 安静窗口 owner、`IsExpanded` 持久化、语言/主题刷新入口
- 子类钩子：`GetContentSize()`（面板尺寸由内容决定）、`OnPanelShown/OnPanelHidden()`
  （folder 用来启停文件夹监听）
- `WidgetManager._widgets` 类型改为 `List<WidgetWindowBase>`；`OnDeleted/OnDataChanged/ShowAll/HideAll`
  逻辑不变

### 4.3 `WidgetMenuBuilder`（`Helpers/`）

把 `FolderWidget.MenuActions` 的组件菜单抽成公共构建器：公共区（显示/收起、改名、颜色、锁定、置顶、
删除）+ 类型专属区（按 `WidgetKindRegistry` 的能力标记：folder 加列数/大小/行数；todo 无）。
现有文件夹颜色菜单（`MenuRenderCheck` 覆盖）行为保持。

### 4.4 接线

- 岛：`SetWidgets(IEnumerable<WidgetData>)`；tile 图标按 Kind 渲染（folder=现有渲染器，
  todo=日历字形）；点击/右键仍按 Id 派发
- 新建：岛的 `+` 弹类型选择菜单；托盘"新建"改为子菜单（文件夹 / 待办）；两者都走
  `WidgetManager.CreateWidget(kind, ...)`
- 开机恢复（`StartupPanels`）、单实例、Show All / Hide All、配置保存全部沿用
- 未知 Kind：跳过窗口与 tile 创建，仅留在配置里

### 4.5 迁移顺序（风险控制）

1. 先在新壳上跑通 `TodoWidget`（`FolderWidget` 零改动）——验证抽象成立；
2. 再把 `FolderWidget` 迁到 `WidgetChrome` + `WidgetWindowBase`（单独提交）。
   迁移后手动冒烟：开合 / 拖动 / 锁定 / 置顶 / 主题 / 透明度 / DPI 恢复 / Alt+Tab 隐藏 /
   组件菜单 / 浏览与拖拽。若抽象被证伪，回退点就是第 2 步之前（Todo 仍可独立存在）。

## 5. Todo 组件

### 5.1 布局（内容区，宽度固定约 340 DIP）

```
│  ‹   2026年9月   ›   [今天]  │   月份导航（‹ › 切换；不在本月时显示"今天"快捷键）
│  一  二  三  四  五  六  日  │   星期表头（按语言 culture，首日跟 FirstDayOfWeek）
│  21  22  23  24  25  26  27 │
│  28  29  30   1   2   3   4 │   每格：日号 + 任务标记点（≤3，更多显示 +）；
│   5   6   7   8   9  10  11 │   今天=组件色描边；选中=组件色填充；非本月=灰
├─────────────────────────────┤
│  9月28日 · 3 项              │
│  ☐ 买牛奶        ↷ 9/25     │   未完成；顺延角标 = 源自 9/25
│  │  ☐ 全脂                   │   子项：缩进、无独立日期（随父项）
│  │  ☑ ~~低脂~~               │   子项各自勾选，划线
│  ☑ ~~跑全量自检~~            │   完成项划线并置底
│  ＋ 添加待办…                │   行内添加：Enter 提交并保持输入框，可连续录入
└─────────────────────────────┘
```

清单区高度 = 2~6 行，超出滚动；面板高度 = 月历块 + 清单块。Todo 不参与列数/大小/行数设置。

### 5.2 行为规则

| 规则 | 决定 |
|---|---|
| 顺延 | 未完成且 `Date < 今天` → **显示在今天** 并带「↷ 原日期」角标；未来任务显示在其自身日期；**不修改数据**（纯计算） |
| 完成 | 勾选 → `CompletedDate = 今天`，显示在完成当天；当日清单里划线置底、不自动清除 |
| 取消勾选 | `CompletedDate = null`，回到 `max(Date, 今天)` 的位置 |
| 编辑 | 单击文本就地编辑（Enter 提交 / Esc 取消）；空文本提交视为删除该条 |
| 删除 | 悬停出现删除按钮，点击即删（无二次确认）；删除父项连带其全部子项 |
| 子待办 | 父项行悬停「＋」在下方就地加子项（Enter 连续）；子项缩进、各自勾选/编辑/删除；**无独立日期**——顺延、完成、删除都随父项 |
| 完成父项 | 勾选父项**级联勾选全部子项**；取消父项不反向级联，子项保持各自状态、可单独调整 |
| 计数 | 「X 项」与月历标记点只数顶层条目，子项不单独计数 |
| 选中日 | `SelectedDate` 持久化；打开面板时恢复选中的日期与月份 |
| 月历点 | 标记点 = 归到该日的**顶层**条目数（`DisplayDate` 落在该日，完成与未完成都算，≤3，更多显示 +）；点非本月格子先切月再选中 |
| 本地化 | 月/星期名按语言对应 culture（en-US / zh-CN / ja-JP）的 `DateTimeFormat`；首日跟 `FirstDayOfWeek` |

### 5.3 纯逻辑模块（全部进控制台自检）

| 模块 | 职责 | 自检 |
|---|---|---|
| `Core/TodoRollover.cs` | `DisplayDate(item, today)` / `IsCarried` / `CarriedDays` / 勾选与取消的日期规则 | `TodoRolloverCheck` |
| `Core/TodoCalendar.cs` | 按月建网格（周数、覆盖完整、首日、今天/选中/非本月标记）、±1 月导航（跨年） | `TodoCalendarCheck` |
| `Core/TodoListOps.cs` | 顶层添加/勾选/删除/完成置底稳定排序；子项添加/勾选/删除与父项级联；**一层约束由子项类型保证** | 并入上述检查或 `TodoListCheck` |

### 5.4 新增本地化 key（三语齐全，`LangCheck` 兜底）

`UI_WidgetKindFolder` / `UI_WidgetKindTodo` / `UI_DefaultTodoName` / `UI_NewWidgetFormat`（"新建 {0}"）、
`Todo_TodayButton` / `Todo_AddPlaceholder` / `Todo_AddChild`（"添加子项"）/ `Todo_CarriedBadge`（"顺延"）/
`Todo_ItemCountFormat` / `Todo_EmptyDay`。

## 6. 测试与验证

- 纯逻辑：新增 `WidgetKindsCheck`（迁移 / 混合往返 / 缺 Kind / 未知 Kind 保留 / 默认配置）、
  `TodoRolloverCheck`、`TodoCalendarCheck`（含三种 culture 的首日与跨年导航）
- XAML：`XamlLoadCheck` 扩展加载 `WidgetChrome` / `TodoWidget`（三种主题）
- 回归：`ConfigSafetyCheck`、`StartupPanelsCheck`、`MonitorPlacementCheck`、`MenuRenderCheck`、
  `IslandLayoutCheck`、`LangCheck` 等全部保持绿；`Run-Checks.ps1` 矩阵登记新项目
- 手动冒烟（第 4.5 两步迁移各自一份清单，执行记录写进计划文件）
- 已知限制沿用：混合 DPI / 多屏人工验收受本机单屏限制，按既有记录如实声明

## 7. 分阶段交付（每阶段独立提交）

| 阶段 | 内容 | 验证 |
|---|---|---|
| 1. 数据层 | `WidgetData`/`FolderData`/`TodoData`/`UnknownData`、转换器、迁移、`WidgetKindRegistry`、现有引用的类型跟进 | `WidgetKindsCheck` + 全量回归；应用行为不变 |
| 2. 新壳 + Todo | `WidgetChrome`/`WidgetWindowBase`/`WidgetMenuBuilder`、`TodoWidget`（月历+清单）、岛/新建/托盘/恢复接线、三语文案 | 新自检 + 手动冒烟（FolderWidget 未动） |
| 3. FolderWidget 迁壳 | 换用 Chrome + Base，文件夹逻辑不动 | 全量自检 + 手动清单 |
| 4. 收尾 | README（组件类型 + Todo 用法 + 降级说明）、CHANGELOG「Unreleased」、`-Group all` 全绿 | 全量回归 + 手动验收记录 |

## 8. 风险与未验证项

- **降级有损**：见 §3.4；以证据副本兜底，README 明示。
- **FolderWidget 迁壳**（阶段 3）是本次唯一触碰现有 UX 的重构：以"Todo 先证抽象 + 单独提交 +
  全量自检 + 手动清单"控制；回退点为阶段 3 之前。
- **交互参考的许可边界**：PaperTodo 只借交互范式；实现全部自写，不搬代码/素材。
- **本机无法验证**：多屏 / 混合 DPI 的 Todo 面板恢复沿用既有多屏机制，人工验收受单屏限制；
  媒体键、触摸等不在范围。
